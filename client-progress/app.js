/*
 * Progress page renderer.
 * Reads the global `progressData` (progress-data.js), validates it and builds the page.
 * Text is inserted with textContent only (elements are built, no HTML strings).
 */
(function () {
  'use strict';

  const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];
  const WEEKDAYS = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];
  const STATUS_LABELS = { planned: 'Planned', 'in-progress': 'In progress', done: 'Done' };
  const MILESTONE_LABELS = { complete: 'Completed', current: 'Current', upcoming: 'Upcoming' };
  const UNAVAILABLE_TEXT = 'Progress information is not available right now.';

  /* ---------- Helpers ---------- */

  function warn(message, value) {
    if (arguments.length > 1) console.warn('progressData: ' + message, value);
    else console.warn('progressData: ' + message);
  }

  function isObject(value) {
    return value !== null && typeof value === 'object' && !Array.isArray(value);
  }

  function hasText(value) {
    return typeof value === 'string' && value.trim() !== '';
  }

  function el(tag, className, text) {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined && text !== null) node.textContent = String(text);
    return node;
  }

  function setText(id, text) {
    const node = document.getElementById(id);
    if (node && hasText(text)) node.textContent = text.trim();
  }

  // Strict "YYYY-MM-DD" as a local date. Impossible dates such as 2026-02-30 return null.
  function parseIsoDate(value) {
    if (typeof value !== 'string') return null;
    const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(value);
    if (!match) return null;
    const year = Number(match[1]);
    const month = Number(match[2]) - 1;
    const day = Number(match[3]);
    const date = new Date(year, month, day);
    const sameDay = date.getFullYear() === year && date.getMonth() === month && date.getDate() === day;
    return sameDay ? date : null;
  }

  function toIso(date) {
    const pad = (n) => String(n).padStart(2, '0');
    return date.getFullYear() + '-' + pad(date.getMonth() + 1) + '-' + pad(date.getDate());
  }

  function formatDate(date, withWeekday) {
    const text = date.getDate() + ' ' + MONTHS[date.getMonth()] + ' ' + date.getFullYear();
    return withWeekday ? WEEKDAYS[date.getDay()] + ' ' + text : text;
  }

  // "15–18 Sep" within one month, "26 Sep – 2 Oct" across months.
  function formatRange(start, end) {
    if (start.getMonth() === end.getMonth() && start.getFullYear() === end.getFullYear()) {
      return start.getDate() + '–' + end.getDate() + ' ' + MONTHS[end.getMonth()];
    }
    return start.getDate() + ' ' + MONTHS[start.getMonth()] + ' – ' + end.getDate() + ' ' + MONTHS[end.getMonth()];
  }

  // Today's local date. `?today=YYYY-MM-DD` previews the timeline on another date.
  function getToday() {
    const override = new URLSearchParams(window.location.search).get('today');
    const date = parseIsoDate(override);
    if (override !== null && !date) warn('ignored invalid ?today= value; using the current date.', override);
    return toIso(date || new Date());
  }

  function asList(value, label) {
    if (value === undefined) return [];
    if (!Array.isArray(value)) {
      warn(label + ' should be a list.', value);
      return [];
    }
    return value;
  }

  // Reading `progressData` throws if progress-data.js failed part-way (the constant is then
  // declared but never initialised), so the read is guarded.
  function readData() {
    try {
      return typeof progressData === 'undefined' ? undefined : progressData;
    } catch (error) {
      return undefined;
    }
  }

  /* ---------- Data validation ---------- */

  function cleanFeature(raw, where) {
    if (!isObject(raw)) {
      warn('skipped ' + where + ' (not an object).', raw);
      return null;
    }
    if (!hasText(raw.name)) {
      warn('skipped ' + where + ' (missing name).', raw);
      return null;
    }
    const name = raw.name.trim();

    const key = typeof raw.status === 'string' ? raw.status.trim().toLowerCase().replace(/[\s_]+/g, '-') : '';
    let status = key;
    if (!Object.prototype.hasOwnProperty.call(STATUS_LABELS, key)) {
      warn('"' + name + '" has an unknown status; shown as Planned.', raw.status);
      status = 'planned';
    }

    let progress = 0;
    if (raw.progress === undefined) {
      if (status === 'in-progress') warn('"' + name + '" is In progress but has no progress number; using 0.');
    } else if (typeof raw.progress !== 'number' || !Number.isFinite(raw.progress)) {
      warn('"' + name + '" progress is not a number; using 0.', raw.progress);
    } else {
      if (raw.progress < 0 || raw.progress > 100) {
        warn('"' + name + '" progress is outside 0–100; clamped.', raw.progress);
      }
      progress = Math.min(100, Math.max(0, raw.progress));
    }

    // Done = 100, In progress = its progress value, Planned = 0.
    let percent = 0;
    if (status === 'done') percent = 100;
    else if (status === 'in-progress') percent = Math.round(progress);
    return { name: name, status: status, percent: percent };
  }

  function cleanGroups(rawGroups) {
    const groups = [];
    asList(rawGroups, 'groups').forEach((raw, gi) => {
      const where = 'group #' + (gi + 1);
      if (!isObject(raw)) {
        warn('skipped ' + where + ' (not an object).', raw);
        return;
      }
      if (!hasText(raw.name)) {
        warn('skipped ' + where + ' (missing name).', raw);
        return;
      }
      const features = asList(raw.features, 'features in ' + where)
        .map((feature, fi) => cleanFeature(feature, 'feature #' + (fi + 1) + ' in ' + where))
        .filter(Boolean);
      if (features.length === 0) {
        warn('skipped ' + where + ' (no valid features).', raw);
        return;
      }
      groups.push({
        name: raw.name.trim(),
        features: features,
        // A group opts out of the headline figure with `countsTowardOverall: false`.
        // Launch activities (client preview, device testing, go-live, training) are real
        // work, but they are not development: averaging four 0% items into the headline
        // understates how much of the build is finished. Such a group keeps its own block
        // and its own count, and is labelled as not counted. Default is to count.
        countsTowardOverall: raw.countsTowardOverall !== false
      });
    });
    return groups;
  }

  function cleanMilestones(rawMilestones) {
    const milestones = [];
    asList(rawMilestones, 'milestones').forEach((raw, i) => {
      const where = 'milestone #' + (i + 1);
      if (!isObject(raw)) {
        warn('skipped ' + where + ' (not an object).', raw);
        return;
      }
      if (!hasText(raw.title)) {
        warn('skipped ' + where + ' (missing title).', raw);
        return;
      }
      let start = parseIsoDate(raw.start);
      let end = parseIsoDate(raw.end);
      if (!start || !end || end < start) {
        warn('"' + raw.title.trim() + '" has invalid dates (use YYYY-MM-DD); shown as Upcoming.', { start: raw.start, end: raw.end });
        start = null;
        end = null;
      }
      milestones.push({ week: hasText(raw.week) ? raw.week.trim() : '', title: raw.title.trim(), start: start, end: end });
    });
    return milestones;
  }

  // Valid dates newest first, then entries with an invalid date in their original order.
  function cleanUpdates(rawUpdates) {
    const dated = [];
    const undated = [];
    asList(rawUpdates, 'updates').forEach((raw, i) => {
      const where = 'update #' + (i + 1);
      if (!isObject(raw)) {
        warn('skipped ' + where + ' (not an object).', raw);
        return;
      }
      if (!hasText(raw.text)) {
        warn('skipped ' + where + ' (missing text).', raw);
        return;
      }
      const entry = { date: parseIsoDate(raw.date), text: raw.text.trim() };
      if (entry.date) {
        dated.push(entry);
      } else {
        warn(where + ' has an invalid date (use YYYY-MM-DD); listed after dated entries.', raw.date);
        undated.push(entry);
      }
    });
    dated.sort((a, b) => b.date - a.date);
    return dated.concat(undated);
  }

  /* ---------- Progress ---------- */

  function computeProgress(features) {
    const stats = { total: features.length, done: 0, inProgress: 0, planned: 0, percent: 0 };
    let sum = 0;
    features.forEach((feature) => {
      if (feature.status === 'done') stats.done += 1;
      else if (feature.status === 'in-progress') stats.inProgress += 1;
      else stats.planned += 1;
      sum += feature.percent;
    });
    if (stats.total > 0) {
      stats.percent = Math.round(sum / stats.total);
      // Never show 100% until every feature is Done.
      if (stats.percent === 100 && stats.done < stats.total) stats.percent = 99;
    }
    return stats;
  }

  /* ---------- Rendering ---------- */

  function createProgressBar(percent, labelledBy, label, modifier) {
    const bar = el('div', 'bar' + (modifier ? ' bar--' + modifier : ''));
    bar.setAttribute('role', 'progressbar');
    bar.setAttribute('aria-valuemin', '0');
    bar.setAttribute('aria-valuemax', '100');
    bar.setAttribute('aria-valuenow', String(percent));
    bar.setAttribute('aria-valuetext', percent + '%');
    if (labelledBy) bar.setAttribute('aria-labelledby', labelledBy);
    if (label) bar.setAttribute('aria-label', label);
    const fill = el('div', 'bar__fill');
    fill.style.width = percent + '%';
    bar.appendChild(fill);
    return bar;
  }

  // Header label rows start hidden in the HTML and are only shown with a valid value.
  function showMetaItem(id, text, datetime) {
    const node = document.getElementById(id);
    node.textContent = text;
    if (datetime) node.setAttribute('datetime', datetime);
    const item = node.closest('.meta__item');
    if (item) item.hidden = false;
  }

  function hideMetaItems() {
    document.querySelectorAll('.meta__item').forEach((item) => {
      item.hidden = true;
    });
  }

  function showUnavailable() {
    hideMetaItems();
    document.getElementById('main').replaceChildren(el('p', 'notice container', UNAVAILABLE_TEXT));
  }

  function renderHeader(project, lastUpdated) {
    if (hasText(project.title)) document.title = project.title.trim();
    setText('project-title', project.title);
    setText('project-client', project.client);

    const agency = hasText(project.agency) ? project.agency.trim() : '';
    if (agency) {
      setText('prepared-by', 'Prepared by ' + agency);
      setText('footer-text', 'Prepared by ' + agency + (hasText(project.client) ? ' for ' + project.client.trim() : ''));
    }

    const start = parseIsoDate(project.startDate);
    const end = parseIsoDate(project.endDate);
    if (start && end) {
      showMetaItem('project-timeline', formatDate(start) + ' – ' + formatDate(end));
    } else {
      warn('project startDate/endDate must be YYYY-MM-DD.', { startDate: project.startDate, endDate: project.endDate });
    }

    const updated = parseIsoDate(lastUpdated);
    if (updated) {
      showMetaItem('last-updated', formatDate(updated), toIso(updated));
    } else {
      warn('lastUpdated must be YYYY-MM-DD.', lastUpdated);
    }
  }

  function renderOverall(container, stats) {
    const figure = el('p', 'overall__figure');
    figure.append(el('span', 'overall__pct', stats.percent + '%'), el('span', 'overall__caption', 'complete'));

    const counts = el('ul', 'counts');
    counts.setAttribute('role', 'list');
    [
      ['done', stats.done, 'Done'],
      ['in-progress', stats.inProgress, 'In progress'],
      ['planned', stats.planned, 'Planned']
    ].forEach(([key, count, label]) => {
      const item = el('li', 'counts__item counts__item--' + key);
      item.append(el('span', 'counts__num', count), el('span', 'counts__label', label));
      counts.appendChild(item);
    });

    container.replaceChildren(
      figure,
      createProgressBar(stats.percent, null, 'Overall progress', 'large'),
      el('p', 'overall__note', 'Average across all ' + stats.total + ' features.'),
      counts
    );
  }

  function renderTimeline(list, milestones, today) {
    list.replaceChildren();
    milestones.forEach((milestone) => {
      let state = 'upcoming';
      if (milestone.start && milestone.end) {
        if (today > toIso(milestone.end)) state = 'complete';
        else if (today >= toIso(milestone.start)) state = 'current';
      }

      const item = el('li', 'milestone milestone--' + state);
      if (state === 'current') item.setAttribute('aria-current', 'step');
      if (milestone.week) item.appendChild(el('p', 'milestone__week', milestone.week));
      item.appendChild(el('p', 'milestone__title', milestone.title));
      if (milestone.start && milestone.end) {
        const dates = el('time', 'milestone__dates', formatRange(milestone.start, milestone.end));
        dates.setAttribute('datetime', toIso(milestone.start));
        item.appendChild(dates);
      }
      item.appendChild(el('p', 'milestone__state', MILESTONE_LABELS[state]));
      list.appendChild(item);
    });
  }

  function renderFeature(feature, id) {
    const item = el('li', 'feature feature--' + feature.status);

    const top = el('div', 'feature__top');
    const name = el('span', 'feature__name', feature.name);
    name.id = id;
    top.append(name, el('span', 'pill pill--' + feature.status, STATUS_LABELS[feature.status]));

    const progress = el('div', 'feature__progress');
    const value = el('span', 'feature__pct', feature.percent + '%');
    value.setAttribute('aria-hidden', 'true'); // the progressbar already announces the value
    progress.append(createProgressBar(feature.percent, id, null, feature.status), value);

    item.append(top, progress);
    return item;
  }

  function renderGroups(container, groups) {
    container.replaceChildren();
    groups.forEach((group, groupIndex) => {
      const stats = computeProgress(group.features);
      const listId = 'group-' + groupIndex + '-features';

      const wrapper = el('div', 'group');
      const head = el('div', 'group__head');
      const heading = el('div', 'group__heading');
      const metaText = stats.percent + '% complete · ' + stats.done + ' of ' + stats.total + ' done'
        + (group.countsTowardOverall ? '' : ' · not counted in the overall figure');
      heading.append(
        el('h3', 'group__title', group.name),
        el('p', 'group__meta', metaText)
      );

      // Show/hide button, visible on small screens only (hidden by CSS from 768px).
      const toggle = el('button', 'group__toggle');
      toggle.type = 'button';
      toggle.setAttribute('aria-expanded', 'true');
      toggle.setAttribute('aria-controls', listId);
      const toggleText = el('span', null, 'Hide');
      toggle.append(toggleText, el('span', 'visually-hidden', ' ' + group.name + ' features'));
      toggle.addEventListener('click', () => {
        const expanded = toggle.getAttribute('aria-expanded') === 'true';
        toggle.setAttribute('aria-expanded', String(!expanded));
        toggleText.textContent = expanded ? 'Show' : 'Hide';
        wrapper.classList.toggle('is-collapsed', expanded);
      });

      head.append(heading, toggle);

      const list = el('ul', 'group__list');
      list.id = listId;
      list.setAttribute('role', 'list');
      group.features.forEach((feature, featureIndex) => {
        list.appendChild(renderFeature(feature, 'feature-' + groupIndex + '-' + featureIndex));
      });

      wrapper.append(head, list);
      container.appendChild(wrapper);
    });
  }

  function renderLog(list, updates) {
    list.replaceChildren();
    updates.forEach((entry) => {
      const item = el('li', 'log__item');
      if (entry.date) {
        const date = el('time', 'log__date', formatDate(entry.date, true));
        date.setAttribute('datetime', toIso(entry.date));
        item.appendChild(date);
      }
      item.appendChild(el('p', 'log__text', entry.text));
      list.appendChild(item);
    });
  }

  /* ---------- Start ---------- */

  function init() {
    const data = readData();
    if (!isObject(data)) {
      warn('progressData is missing or could not be read.');
      showUnavailable();
      return;
    }

    try {
      const groups = cleanGroups(data.groups);
      const counted = groups.filter((group) => group.countsTowardOverall);
      // If every group opted out, a 0% headline would be a lie rather than a simplification,
      // so fall back to counting everything.
      const allFeatures = (counted.length > 0 ? counted : groups).flatMap((group) => group.features);

      renderHeader(isObject(data.project) ? data.project : {}, data.lastUpdated);
      renderOverall(document.getElementById('overall'), computeProgress(allFeatures));
      renderTimeline(document.getElementById('timeline'), cleanMilestones(data.milestones), getToday());
      renderGroups(document.getElementById('groups'), groups);
      renderLog(document.getElementById('log'), cleanUpdates(data.updates));
    } catch (error) {
      console.error('progressData: the page could not be built.', error);
      showUnavailable();
    }
  }

  init();
})();
