/**
 * The `/api/admin/content/{home,announcements,size-guides,mega-menu}` half of the admin stub (#192: ADM-17,
 * GLB-03, PDP-04; #215: GLB-05).
 *
 * It lives beside `admin-catalog-stub.mjs`, which calls {@link handleContentRoute}, for the reason the
 * product stub gives: that file is long enough already, and these rules are worth reading on their own.
 *
 * **It is deliberately no more permissive than the real API.** Field names are the DTOs' field for field,
 * and it enforces what a screen can get wrong:
 *
 * - **the whole-form rule**: a body missing any listed key is a 400 naming it (`Send <field>.`);
 * - a value for a field the block does not use is a 400 (`This block does not use <field>.`);
 * - lengths, ranges, the link rules (a site path starting `/` and not `//`; `http(s)://` only where
 *   allowed), wall-clock dates with no offset, end after start;
 * - a missing or stale row version is a 409 `stale_row_version`, and a write moves it — for an item,
 *   the SECTION's row version, because the section is the aggregate;
 * - 409 `items_not_supported`, `section_full`, `too_many_announcements`, `size_guide_in_use`,
 *   `too_many_mega_menu_cards`;
 * - a mega-menu card switched on without a picture is a 400 on `imagePublicId` (#215);
 * - `/__test__/content-permission?deny=view|write|none` makes the routes answer 403, as a role
 *   without `Content.*` would.
 */

import { isSitePath, linkProblem, textProblem } from './content-field-rules.mjs';

/** Eight-byte row versions, base64, as the screen round-trips them. */
function rowVersion(seed) {
  return Buffer.from([0, 0, 0, 0, 0, 1, seed >> 8, seed & 0xff]).toString('base64');
}

let tick = 100;

function nextRowVersion() {
  tick += 1;
  return rowVersion(tick);
}

const DEPARTMENTS = [
  { id: 1, name: 'Women', slug: 'women', isActive: true },
  { id: 2, name: 'Men', slug: 'men', isActive: true },
  { id: 3, name: 'Kids', slug: 'kids', isActive: false },
];

const BENEFIT_ICONS = ['delivery', 'shipping', 'returns', 'secure', 'support'];

/** Products an item may name by slug. */
const PRODUCT_SLUGS = ['linen-shirt', 'denim-jacket', 'wrap-dress'];

/** Media the seed points at, so an untouched item round-trips with its image. */
const SEED_MEDIA = new Map([
  ['11111111-0000-4000-8000-000000000001', { url: '/__test__/logo.webp', altText: 'A model in a linen dress' }],
  ['11111111-0000-4000-8000-000000000002', { url: '/__test__/logo.webp', altText: 'Women in the new season' }],
]);

const RULES = {
  HeroVideo: {
    sectionFields: ['title', 'subtitle', 'overlay'],
    itemFields: ['title', 'subtitle', 'buttonLabel', 'linkUrl', 'image', 'mobileImage', 'video'],
    requiredItemFields: [],
    maxItems: 1,
    linkMayLeaveSite: false,
    needsArtwork: true,
  },
  ShopByDepartment: {
    sectionFields: ['title', 'subtitle'],
    itemFields: ['title', 'buttonLabel', 'linkUrl', 'image', 'mobileImage', 'department'],
    requiredItemFields: ['department'],
    maxItems: 6,
    linkMayLeaveSite: false,
  },
  NewArrivals: {
    sectionFields: ['title', 'subtitle', 'count', 'listingUrl', 'linkLabel'],
    itemFields: [],
    requiredItemFields: [],
    maxItems: 0,
    linkMayLeaveSite: false,
  },
  TrendingItems: {
    sectionFields: ['title', 'subtitle', 'mode', 'count', 'listingUrl', 'linkLabel'],
    itemFields: ['title', 'subtitle', 'linkUrl', 'image', 'product'],
    requiredItemFields: ['title'],
    maxItems: 12,
    linkMayLeaveSite: false,
  },
  CampaignBanner: {
    sectionFields: ['title', 'subtitle', 'overlay'],
    itemFields: ['title', 'subtitle', 'body', 'buttonLabel', 'linkUrl', 'image', 'mobileImage'],
    requiredItemFields: [],
    maxItems: 1,
    linkMayLeaveSite: false,
    needsArtwork: true,
  },
  ActivewearFeature: {
    sectionFields: ['title', 'subtitle', 'overlay'],
    itemFields: ['title', 'subtitle', 'body', 'buttonLabel', 'linkUrl', 'image', 'mobileImage'],
    requiredItemFields: [],
    maxItems: 1,
    linkMayLeaveSite: false,
    needsArtwork: true,
  },
  SaleSection: {
    sectionFields: ['title', 'subtitle', 'count', 'listingUrl', 'linkLabel'],
    itemFields: [],
    requiredItemFields: [],
    maxItems: 0,
    linkMayLeaveSite: false,
  },
  SocialGallery: {
    sectionFields: ['title', 'subtitle'],
    itemFields: ['title', 'linkUrl', 'image'],
    requiredItemFields: ['image'],
    maxItems: 12,
    linkMayLeaveSite: true,
  },
  Benefits: {
    sectionFields: ['title', 'subtitle'],
    itemFields: ['title', 'body', 'iconKey'],
    requiredItemFields: ['iconKey'],
    maxItems: 6,
    linkMayLeaveSite: false,
  },
  Newsletter: {
    sectionFields: ['title', 'subtitle'],
    itemFields: [],
    requiredItemFields: [],
    maxItems: 0,
    linkMayLeaveSite: false,
  },
};

const ORDER = Object.keys(RULES);

const SECTION_KEYS = [
  'title', 'subtitle', 'isEnabled', 'sortOrder', 'startsAt', 'endsAt',
  'mode', 'count', 'listingUrl', 'linkLabel', 'overlay', 'rowVersion',
];

const ITEM_KEYS = [
  'title', 'subtitle', 'body', 'buttonLabel', 'linkUrl', 'imagePublicId', 'mobileImagePublicId',
  'videoPublicId', 'departmentId', 'productSlug', 'iconKey', 'sortOrder', 'isEnabled', 'startsAt',
  'endsAt', 'rowVersion',
];

/** Item rule field → the request key that carries it. */
const ITEM_FIELD_KEY = {
  title: 'title',
  subtitle: 'subtitle',
  body: 'body',
  buttonLabel: 'buttonLabel',
  linkUrl: 'linkUrl',
  image: 'imagePublicId',
  mobileImage: 'mobileImagePublicId',
  video: 'videoPublicId',
  department: 'departmentId',
  product: 'productSlug',
  iconKey: 'iconKey',
};

const ANNOUNCEMENT_KEYS = ['message', 'linkUrl', 'sortOrder', 'isActive', 'startsAt', 'endsAt'];
const MEGA_MENU_CARD_KEYS = [
  'departmentId', 'title', 'subtitle', 'buttonLabel', 'linkUrl', 'imagePublicId', 'sortOrder', 'isActive', 'startsAt',
  'endsAt',
];

/** `MegaMenuPromoCardLimits` and `ContentFieldLimits.ButtonLabelMaxLength` (#215). */
const MEGA_MENU = { title: 100, subtitle: 200, buttonLabel: 40, maxCardsPerDepartment: 6 };
const SIZE_GUIDE_KEYS = ['name', 'introHtml', 'tableJson', 'measuringTipsHtml', 'imagePublicId'];

const LIMITS = { maxColumns: 20, maxRows: 100, maxCells: 1000, maxCellLength: 60, maxHtmlLength: 20000 };

function seedSection(id, type, overrides = {}) {
  return {
    id,
    type,
    title: null,
    subtitle: null,
    isEnabled: true,
    sortOrder: id - 1,
    startsAt: null,
    endsAt: null,
    mode: type === 'TrendingItems' ? 'Auto' : null,
    count: RULES[type].sectionFields.includes('count') ? 8 : null,
    listingUrl: RULES[type].sectionFields.includes('listingUrl') ? '/new' : null,
    linkLabel: null,
    overlay: null,
    items: [],
    rowVersion: nextRowVersion(),
    ...overrides,
  };
}

function seedItem(id, overrides = {}) {
  return {
    id,
    title: null,
    subtitle: null,
    body: null,
    buttonLabel: null,
    linkUrl: null,
    imagePublicId: null,
    mobileImagePublicId: null,
    videoPublicId: null,
    departmentId: null,
    productSlug: null,
    iconKey: null,
    sortOrder: 0,
    isEnabled: true,
    startsAt: null,
    endsAt: null,
    ...overrides,
  };
}

export function freshContentState() {
  const sections = ORDER.map((type, index) => seedSection(index + 1, type));
  const byType = Object.fromEntries(sections.map((section) => [section.type, section]));

  byType.HeroVideo.title = 'The linen edit';
  byType.HeroVideo.overlay = 'dark';
  byType.HeroVideo.items = [
    seedItem(1, {
      title: 'Made for the heat',
      buttonLabel: 'Shop linen',
      linkUrl: '/women/linen',
      imagePublicId: '11111111-0000-4000-8000-000000000001',
    }),
  ];
  byType.ShopByDepartment.title = 'Shop by department';
  byType.ShopByDepartment.items = [
    seedItem(2, { departmentId: 1, imagePublicId: '11111111-0000-4000-8000-000000000002' }),
  ];
  byType.Benefits.title = 'Why shop with us';
  byType.Benefits.items = [seedItem(3, { title: 'Island-wide delivery', iconKey: 'delivery' })];
  byType.Newsletter.isEnabled = false;

  return {
    sections,
    nextItemId: 100,
    announcements: [
      {
        id: 1,
        message: 'Free delivery on orders over Rs. 10,000',
        linkUrl: '/delivery',
        sortOrder: 0,
        isActive: true,
        startsAt: null,
        endsAt: null,
        version: nextRowVersion(),
      },
    ],
    nextAnnouncementId: 2,
    sizeGuides: [
      {
        id: 1,
        name: "Women's tops",
        introHtml: '<p>Our tops run true to size.</p>',
        tableJson: JSON.stringify({ columns: ['Size', 'Chest (cm)'], rows: [['S', '86-91'], ['M', '92-97']] }),
        measuringTipsHtml: null,
        imagePublicId: null,
        categoryCount: 2,
        productCount: 5,
        updatedAtUtc: '2026-10-01T04:00:00Z',
        rowVersion: nextRowVersion(),
      },
      {
        id: 2,
        name: 'Unused guide',
        introHtml: null,
        tableJson: JSON.stringify({ columns: ['Size'], rows: [['One size']] }),
        measuringTipsHtml: null,
        imagePublicId: null,
        categoryCount: 0,
        productCount: 0,
        updatedAtUtc: null,
        rowVersion: nextRowVersion(),
      },
    ],
    nextSizeGuideId: 3,
    // #215: what the seeder offers a fresh database: one switched-off card per department in today's words.
    megaMenuCards: DEPARTMENTS.map((department, index) => ({
      id: index + 1,
      departmentId: department.id,
      title: `New in ${department.name}`,
      subtitle: null,
      buttonLabel: 'Shop now',
      linkUrl: '/new-arrivals',
      imagePublicId: null,
      sortOrder: 0,
      isActive: false,
      startsAt: null,
      endsAt: null,
      rowVersion: nextRowVersion(),
    })),
    nextMegaMenuCardId: DEPARTMENTS.length + 1,
    deny: 'none',
  };
}

// ---- helpers --------------------------------------------------------------------------------------

/** The shop's wall-clock time now, `yyyy-MM-ddTHH:mm` (UTC+05:30, no daylight saving). */
function colomboNow() {
  return new Date(Date.now() + 330 * 60 * 1000).toISOString().slice(0, 16);
}

function isLive(enabled, startsAt, endsAt) {
  const now = colomboNow();
  return enabled && (startsAt === null || startsAt.slice(0, 16) <= now) && (endsAt === null || endsAt.slice(0, 16) > now);
}

const WALL_CLOCK = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2})?$/;

function mediaRef(state, publicId) {
  if (publicId === null) {
    return null;
  }

  const uploaded = state.uploads?.get(publicId);
  if (uploaded) {
    return { publicId, url: uploaded.url, altText: uploaded.altText ?? null, posterUrl: uploaded.posterUrl ?? null };
  }

  const seeded = SEED_MEDIA.get(publicId);
  return seeded ? { publicId, url: seeded.url, altText: seeded.altText, posterUrl: null } : null;
}

function mediaExists(state, publicId) {
  return state.uploads?.has(publicId) || SEED_MEDIA.has(publicId);
}

function sectionDto(state, section) {
  const items = [...section.items].sort((a, b) => a.sortOrder - b.sortOrder || a.id - b.id);

  return {
    id: section.id,
    type: section.type,
    title: section.title,
    subtitle: section.subtitle,
    isEnabled: section.isEnabled,
    sortOrder: section.sortOrder,
    startsAt: section.startsAt,
    endsAt: section.endsAt,
    mode: section.mode,
    count: section.count,
    listingUrl: section.listingUrl,
    linkLabel: section.linkLabel,
    overlay: section.overlay,
    isLiveNow: isLive(section.isEnabled, section.startsAt, section.endsAt),
    rules: { needsArtwork: false, ...RULES[section.type] },
    items: items.map((item) => {
      const image = mediaRef(state, item.imagePublicId);
      const mobile = mediaRef(state, item.mobileImagePublicId);
      const video = mediaRef(state, item.videoPublicId);

      return {
        id: item.id,
        title: item.title,
        subtitle: item.subtitle,
        body: item.body,
        buttonLabel: item.buttonLabel,
        linkUrl: item.linkUrl,
        image: image && { publicId: image.publicId, url: image.url, altText: image.altText },
        mobileImage: mobile && { publicId: mobile.publicId, url: mobile.url, altText: mobile.altText },
        video: video && { publicId: video.publicId, url: video.url, posterUrl: video.posterUrl },
        departmentId: item.departmentId,
        productSlug: item.productSlug,
        iconKey: item.iconKey,
        sortOrder: item.sortOrder,
        isEnabled: item.isEnabled,
        startsAt: item.startsAt,
        endsAt: item.endsAt,
        isLiveNow: isLive(item.isEnabled, item.startsAt, item.endsAt),
      };
    }),
    rowVersion: section.rowVersion,
  };
}

/** `GET /api/admin/content/mega-menu`: every department, its cards, and which one the menu draws now. */
function megaMenuDto(state) {
  const cards = [...state.content.megaMenuCards].sort((a, b) => a.sortOrder - b.sortOrder || a.id - b.id);

  return {
    departments: DEPARTMENTS.map((department) => {
      const own = cards.filter((card) => card.departmentId === department.id);
      const shown = own.find(
        (card) => isLive(card.isActive, card.startsAt, card.endsAt) && mediaRef(state, card.imagePublicId) !== null,
      );

      return {
        id: department.id,
        name: department.name,
        slug: department.slug,
        isActive: department.isActive,
        description: department.id === 1 ? 'Everyday essentials and occasion pieces.' : null,
        heroImage: null,
        cards: own.map((card) => {
          const image = mediaRef(state, card.imagePublicId);
          return {
            id: card.id,
            departmentId: card.departmentId,
            title: card.title,
            subtitle: card.subtitle,
            buttonLabel: card.buttonLabel,
            linkUrl: card.linkUrl,
            image: image && { publicId: image.publicId, url: image.url, altText: image.altText },
            sortOrder: card.sortOrder,
            isActive: card.isActive,
            startsAt: card.startsAt,
            endsAt: card.endsAt,
            isLiveNow: isLive(card.isActive, card.startsAt, card.endsAt),
            isShownNow: card === shown,
            rowVersion: card.rowVersion,
          };
        }),
      };
    }),
    maxCardsPerDepartment: MEGA_MENU.maxCardsPerDepartment,
  };
}

function announcementDto(row) {
  return { ...row, isLiveNow: isLive(row.isActive, row.startsAt, row.endsAt) };
}

function sizeGuideListDto(guide) {
  return {
    id: guide.id,
    name: guide.name,
    categoryCount: guide.categoryCount,
    productCount: guide.productCount,
    hasImage: guide.imagePublicId !== null,
    updatedAtUtc: guide.updatedAtUtc,
    rowVersion: guide.rowVersion,
  };
}

function sizeGuideDto(state, guide) {
  let table = null;
  try {
    const parsed = JSON.parse(guide.tableJson);
    table = parsed && Array.isArray(parsed.columns) && parsed.columns.length > 0 ? parsed : null;
  } catch {
    table = null;
  }

  const image = mediaRef(state, guide.imagePublicId);

  return {
    id: guide.id,
    name: guide.name,
    introHtml: guide.introHtml,
    table,
    measuringTipsHtml: guide.measuringTipsHtml,
    image: image && { publicId: image.publicId, url: image.url, altText: image.altText },
    categoryCount: guide.categoryCount,
    productCount: guide.productCount,
    rowVersion: guide.rowVersion,
    limits: LIMITS,
  };
}

/** Collects field errors the way a validation problem holds them. */
class Errors {
  constructor() {
    this.errors = {};
  }

  add(field, message) {
    (this.errors[field] ??= []).push(message);
  }

  get any() {
    return Object.keys(this.errors).length > 0;
  }
}

function validationProblem(json, response, errors) {
  return json(response, 400, {
    type: 'https://tools.ietf.org/html/rfc9110#section-15.5.1',
    title: 'One or more validation errors occurred.',
    status: 400,
    errors,
  });
}

/** The whole-form rule: every key must be present, null or not. */
function missingKeys(body, keys, errors) {
  for (const key of keys) {
    if (!Object.prototype.hasOwnProperty.call(body, key)) {
      errors.add(key, `Send ${key}.`);
    }
  }
}

// #192 tester D3: the text and link rules are the API's own, from content-field-rules.mjs, which is held to
// ContentFieldRules.cs by one shared table of cases (content-rule-cases.json).
function checkText(errors, field, value, max, required = false) {
  const problemText = textProblem(value, max, required);
  if (problemText !== null) {
    errors.add(field, problemText);
  }
}

function sitePath(value) {
  return isSitePath(value);
}

function checkLink(errors, field, value, mayLeaveSite) {
  const problemText = linkProblem(value, mayLeaveSite);
  if (problemText !== null) {
    errors.add(field, problemText);
  }
}

function checkInt(errors, field, value, min, max, required = true) {
  if (value === null) {
    if (required) {
      errors.add(field, 'Required.');
    }
    return;
  }
  if (!Number.isInteger(value) || value < min || value > max) {
    errors.add(field, `Must be a whole number from ${min} to ${max}.`);
  }
}

function checkSchedule(errors, body) {
  for (const field of ['startsAt', 'endsAt']) {
    const value = body[field];
    if (value !== null && (typeof value !== 'string' || !WALL_CLOCK.test(value))) {
      errors.add(field, "Use the shop's own time, like 2026-11-01T09:00, with no time zone.");
    }
  }
  if (
    typeof body.startsAt === 'string' &&
    typeof body.endsAt === 'string' &&
    WALL_CLOCK.test(body.startsAt) &&
    WALL_CLOCK.test(body.endsAt) &&
    body.endsAt.slice(0, 16) <= body.startsAt.slice(0, 16)
  ) {
    errors.add('endsAt', 'The end must be after the start.');
  }
}

function stale(problem, response) {
  return problem(
    response,
    409,
    'This change cannot be applied',
    'Somebody else changed this while you were editing it. Reload the page to see their change.',
    'stale_row_version',
  );
}

function ifMatch(request) {
  const header = request.headers['if-match'];
  return typeof header === 'string' && header.trim().length > 0
    ? header.trim().replace(/^W\//i, '').replace(/^"|"$/g, '')
    : null;
}

/** The allow-list sanitiser, as far as a test needs it: scripts and event handlers go, and are reported. */
function sanitise(html) {
  if (html === null) {
    return { html: null, removed: [] };
  }
  const removed = [];
  let cleaned = html.replace(/<script\b[^>]*>[\s\S]*?<\/script>/gi, () => {
    if (!removed.includes('<script>')) removed.push('<script>');
    return '';
  });
  cleaned = cleaned.replace(/\s(on[a-z]+)\s*=\s*("[^"]*"|'[^']*'|[^\s>]+)/gi, (_, name) => {
    const attribute = name.toLowerCase();
    if (!removed.includes(attribute)) removed.push(attribute);
    return '';
  });
  return { html: cleaned.trim().length === 0 ? null : cleaned, removed };
}

// ---- the routes -----------------------------------------------------------------------------------

/**
 * Answers a content route, or returns false when the path is not one of these.
 *
 * @param ctx `{ path, method, url, request, response, state, json, problem, noContent, readBody }`
 */
export async function handleContentRoute(ctx) {
  const { path, method, url, request, response, state, json, problem, noContent, readBody } = ctx;

  if (path === '/__test__/content-permission') {
    state.content ??= freshContentState();
    state.content.deny = url.searchParams.get('deny') ?? 'none';
    json(response, 200, { deny: state.content.deny });
    return true;
  }

  // Moves a row version as another editor's save would, so a stale write can be provoked.
  if (path === '/__test__/content-bump') {
    state.content ??= freshContentState();
    const kind = url.searchParams.get('kind');
    const id = Number(url.searchParams.get('id'));
    const list =
      kind === 'section'
        ? state.content.sections
        : kind === 'announcement'
          ? state.content.announcements
          : kind === 'card'
            ? state.content.megaMenuCards
            : state.content.sizeGuides;
    const row = list.find((candidate) => candidate.id === id);
    if (row) {
      if (kind === 'announcement') row.version = nextRowVersion();
      else row.rowVersion = nextRowVersion();
    }
    json(response, 200, { ok: Boolean(row) });
    return true;
  }

  if (!/^\/api\/admin\/content\/(home|announcements|size-guides|mega-menu)(\/|$)/.test(path)) {
    return false;
  }

  state.content ??= freshContentState();
  const content = state.content;

  if (content.deny === 'view' || (content.deny === 'write' && method !== 'GET')) {
    problem(response, 403, 'Forbidden', 'Your role does not have the permission this needs.', 'forbidden');
    return true;
  }

  const readJson = async () => {
    const body = await readBody(request);
    if (body === undefined || body === null || typeof body !== 'object' || Array.isArray(body)) {
      problem(response, 400, 'Malformed JSON', 'The request body is not a JSON object.', 'malformed_json');
      return null;
    }
    return body;
  };

  // ---- homepage ----------------------------------------------------------------------------------
  if (path === '/api/admin/content/home' && method === 'GET') {
    const sections = [...content.sections].sort((a, b) => a.sortOrder - b.sortOrder || a.id - b.id);
    json(response, 200, {
      sections: sections.map((section) => sectionDto(state, section)),
      departments: DEPARTMENTS,
      benefitIcons: BENEFIT_ICONS,
    });
    return true;
  }

  const sectionMatch = path.match(/^\/api\/admin\/content\/home\/sections\/(\d+)$/);
  if (sectionMatch && method === 'PUT') {
    const section = content.sections.find((candidate) => candidate.id === Number(sectionMatch[1]));
    if (!section) {
      problem(response, 404, 'Not found', 'That block is not there.', 'not_found');
      return true;
    }
    const body = await readJson();
    if (body === null) return true;

    const errors = new Errors();
    missingKeys(body, SECTION_KEYS, errors);
    if (errors.any) {
      validationProblem(json, response, errors.errors);
      return true;
    }

    const rules = RULES[section.type];
    for (const field of ['title', 'subtitle', 'mode', 'count', 'listingUrl', 'linkLabel', 'overlay']) {
      if (!rules.sectionFields.includes(field) && body[field] !== null) {
        errors.add(field, `This block does not use ${field}.`);
      }
    }

    checkText(errors, 'title', body.title, 150);
    checkText(errors, 'subtitle', body.subtitle, 300);
    if (typeof body.isEnabled !== 'boolean') errors.add('isEnabled', 'Say whether the block is shown.');
    checkInt(errors, 'sortOrder', body.sortOrder, 0, 999);
    checkSchedule(errors, body);

    if (rules.sectionFields.includes('mode') && body.mode !== 'Auto' && body.mode !== 'Manual') {
      errors.add('mode', 'Choose automatic or hand-picked.');
    }
    if (rules.sectionFields.includes('count')) checkInt(errors, 'count', body.count, 1, 24);
    if (rules.sectionFields.includes('listingUrl')) {
      const auto = !rules.sectionFields.includes('mode') || body.mode === 'Auto';
      if (body.listingUrl === null) {
        if (auto) errors.add('listingUrl', 'Required: the listing these products come from.');
      } else if (!sitePath(body.listingUrl) || body.listingUrl.length > 500) {
        errors.add('listingUrl', 'Use a page on this shop, starting with / (for example /women/dresses).');
      }
    }
    checkText(errors, 'linkLabel', body.linkLabel, 40);
    if (body.overlay !== null && !['dark', 'light', 'none'].includes(body.overlay)) {
      errors.add('overlay', 'Choose dark, light or none.');
    }

    if (errors.any) {
      validationProblem(json, response, errors.errors);
      return true;
    }
    if (body.rowVersion !== section.rowVersion) {
      stale(problem, response);
      return true;
    }

    for (const key of SECTION_KEYS.filter((key) => key !== 'rowVersion')) {
      section[key] = body[key];
    }
    section.rowVersion = nextRowVersion();
    json(response, 200, { id: section.id, rowVersion: section.rowVersion });
    return true;
  }

  const createItemMatch = path.match(/^\/api\/admin\/content\/home\/sections\/(\d+)\/items$/);
  const itemMatch = path.match(/^\/api\/admin\/content\/home\/items\/(\d+)$/);

  if ((createItemMatch && method === 'POST') || (itemMatch && (method === 'PUT' || method === 'DELETE'))) {
    let section;
    let item = null;

    if (createItemMatch) {
      section = content.sections.find((candidate) => candidate.id === Number(createItemMatch[1]));
    } else {
      const id = Number(itemMatch[1]);
      section = content.sections.find((candidate) => candidate.items.some((row) => row.id === id));
      item = section?.items.find((row) => row.id === id) ?? null;
    }

    if (!section || (itemMatch && !item)) {
      problem(response, 404, 'Not found', 'That item is not there.', 'not_found');
      return true;
    }

    if (method === 'DELETE') {
      if (ifMatch(request) !== section.rowVersion) {
        stale(problem, response);
        return true;
      }
      section.items = section.items.filter((row) => row !== item);
      section.rowVersion = nextRowVersion();
      noContent(response);
      return true;
    }

    const rules = RULES[section.type];
    if (rules.maxItems === 0) {
      problem(response, 409, 'This change cannot be applied', 'This block has no items.', 'items_not_supported');
      return true;
    }

    const body = await readJson();
    if (body === null) return true;

    const errors = new Errors();
    missingKeys(body, ITEM_KEYS, errors);
    if (errors.any) {
      validationProblem(json, response, errors.errors);
      return true;
    }

    for (const [field, key] of Object.entries(ITEM_FIELD_KEY)) {
      if (!rules.itemFields.includes(field) && body[key] !== null) {
        errors.add(key, `This block does not use ${field}.`);
      }
    }
    for (const field of rules.requiredItemFields) {
      const key = ITEM_FIELD_KEY[field];
      if (body[key] === null || (typeof body[key] === 'string' && body[key].trim() === '')) {
        errors.add(key, 'Required for this block.');
      }
    }

    checkText(errors, 'title', body.title, 150);
    checkText(errors, 'subtitle', body.subtitle, 300);
    checkText(errors, 'body', body.body, 1000);
    checkText(errors, 'buttonLabel', body.buttonLabel, 40);
    checkLink(errors, 'linkUrl', body.linkUrl, rules.linkMayLeaveSite);
    for (const key of ['imagePublicId', 'mobileImagePublicId', 'videoPublicId']) {
      if (body[key] !== null && !mediaExists(state, body[key])) {
        errors.add(key, 'That file is not in the media library. Upload it again.');
      }
    }
    if (body.videoPublicId !== null && body.imagePublicId === null && body.mobileImagePublicId === null) {
      errors.add('imagePublicId', 'A video needs a still image too, for phones and people who prefer less motion.');
    } else if (rules.needsArtwork && body.isEnabled === true && body.imagePublicId === null && body.mobileImagePublicId === null) {
      // As HomeContentWriteHandlers: a hero or banner item is its picture, and the storefront draws nothing without one.
      errors.add('imagePublicId', 'Add a picture (or a video with its still) before switching this on. It can be saved switched off until then.');
    }
    if (body.departmentId !== null && !DEPARTMENTS.some((department) => department.id === body.departmentId)) {
      errors.add('departmentId', 'Choose a department that exists.');
    }
    if (body.productSlug !== null && !PRODUCT_SLUGS.includes(body.productSlug)) {
      errors.add('productSlug', 'No product has that address.');
    }
    if (body.iconKey !== null && !BENEFIT_ICONS.includes(body.iconKey)) {
      errors.add('iconKey', 'Choose one of the icons offered.');
    }
    checkInt(errors, 'sortOrder', body.sortOrder, 0, 999);
    if (typeof body.isEnabled !== 'boolean') errors.add('isEnabled', 'Say whether the item is shown.');
    checkSchedule(errors, body);

    if (errors.any) {
      validationProblem(json, response, errors.errors);
      return true;
    }
    if (body.rowVersion !== section.rowVersion) {
      stale(problem, response);
      return true;
    }
    if (item === null && section.items.length >= rules.maxItems) {
      problem(
        response,
        409,
        'This change cannot be applied',
        `This block holds up to ${rules.maxItems} items. Remove one first.`,
        'section_full',
      );
      return true;
    }

    const values = Object.fromEntries(ITEM_KEYS.filter((key) => key !== 'rowVersion').map((key) => [key, body[key]]));
    if (item === null) {
      item = { id: content.nextItemId++, ...values };
      section.items.push(item);
    } else {
      Object.assign(item, values);
    }
    section.rowVersion = nextRowVersion();
    json(response, createItemMatch ? 201 : 200, { id: item.id, rowVersion: section.rowVersion });
    return true;
  }

  // ---- announcements -----------------------------------------------------------------------------
  if (path === '/api/admin/content/announcements' && method === 'GET') {
    const rows = [...content.announcements].sort((a, b) => a.sortOrder - b.sortOrder || a.id - b.id);
    json(response, 200, rows.map(announcementDto));
    return true;
  }

  const announcementMatch = path.match(/^\/api\/admin\/content\/announcements\/(\d+)$/);
  if ((path === '/api/admin/content/announcements' && method === 'POST') || (announcementMatch && (method === 'PUT' || method === 'DELETE'))) {
    let row = null;
    if (announcementMatch) {
      row = content.announcements.find((candidate) => candidate.id === Number(announcementMatch[1])) ?? null;
      if (!row) {
        problem(response, 404, 'Not found', 'That message is not there.', 'not_found');
        return true;
      }
    }

    if (method === 'DELETE') {
      if (ifMatch(request) !== row.version) {
        stale(problem, response);
        return true;
      }
      content.announcements = content.announcements.filter((candidate) => candidate !== row);
      noContent(response);
      return true;
    }

    const body = await readJson();
    if (body === null) return true;

    const errors = new Errors();
    missingKeys(body, method === 'PUT' ? [...ANNOUNCEMENT_KEYS, 'version'] : ANNOUNCEMENT_KEYS, errors);
    if (errors.any) {
      validationProblem(json, response, errors.errors);
      return true;
    }

    if (typeof body.message !== 'string' || body.message.trim().length === 0) {
      errors.add('message', 'Write the message.');
    } else if (body.message.trim().length > 200) {
      errors.add('message', 'At most 200 characters.');
    } else if (/[\u0000-\u001f\u007f]/.test(body.message)) {
      errors.add('message', 'One line only, with no line breaks.');
    }
    checkLink(errors, 'linkUrl', body.linkUrl, true);
    checkInt(errors, 'sortOrder', body.sortOrder, 0, 999);
    if (typeof body.isActive !== 'boolean') errors.add('isActive', 'Say whether the message is shown.');
    checkSchedule(errors, body);

    if (errors.any) {
      validationProblem(json, response, errors.errors);
      return true;
    }

    if (row === null) {
      if (content.announcements.length >= 20) {
        problem(
          response,
          409,
          'This change cannot be applied',
          'The bar holds up to 20 messages. Remove one first.',
          'too_many_announcements',
        );
        return true;
      }
      row = { id: content.nextAnnouncementId++ };
      content.announcements.push(row);
    } else if (body.version !== row.version) {
      stale(problem, response);
      return true;
    }

    Object.assign(row, {
      message: body.message.trim(),
      linkUrl: body.linkUrl,
      sortOrder: body.sortOrder,
      isActive: body.isActive,
      startsAt: body.startsAt,
      endsAt: body.endsAt,
      version: nextRowVersion(),
    });
    json(response, method === 'POST' ? 201 : 200, { id: row.id, version: row.version });
    return true;
  }

  // ---- mega-menu cards (#215) --------------------------------------------------------------------
  if (path === '/api/admin/content/mega-menu' && method === 'GET') {
    json(response, 200, megaMenuDto(state));
    return true;
  }

  const cardMatch = path.match(/^\/api\/admin\/content\/mega-menu\/cards\/(\d+)$/);
  if ((path === '/api/admin/content/mega-menu/cards' && method === 'POST') || (cardMatch && (method === 'PUT' || method === 'DELETE'))) {
    let card = null;
    if (cardMatch) {
      card = content.megaMenuCards.find((candidate) => candidate.id === Number(cardMatch[1])) ?? null;
      if (!card) {
        problem(response, 404, 'Not found', 'That card is not there.', 'not_found');
        return true;
      }
    }

    if (method === 'DELETE') {
      if (ifMatch(request) !== card.rowVersion) {
        stale(problem, response);
        return true;
      }
      content.megaMenuCards = content.megaMenuCards.filter((candidate) => candidate !== card);
      noContent(response);
      return true;
    }

    const body = await readJson();
    if (body === null) return true;

    const errors = new Errors();
    missingKeys(body, method === 'PUT' ? [...MEGA_MENU_CARD_KEYS, 'rowVersion'] : MEGA_MENU_CARD_KEYS, errors);
    if (errors.any) {
      validationProblem(json, response, errors.errors);
      return true;
    }

    if (!Number.isInteger(body.departmentId) || !DEPARTMENTS.some((department) => department.id === body.departmentId)) {
      errors.add('departmentId', 'Choose the department whose menu shows this card.');
    }
    checkText(errors, 'title', body.title, MEGA_MENU.title, true);
    checkText(errors, 'subtitle', body.subtitle, MEGA_MENU.subtitle);
    checkText(errors, 'buttonLabel', body.buttonLabel, MEGA_MENU.buttonLabel, true);
    if (body.linkUrl === null || (typeof body.linkUrl === 'string' && body.linkUrl.trim().length === 0)) {
      errors.add('linkUrl', 'Say where the card goes: a page on this shop such as /new-arrivals.');
    } else {
      checkLink(errors, 'linkUrl', body.linkUrl, true);
    }
    checkInt(errors, 'sortOrder', body.sortOrder, 0, 999);
    if (typeof body.isActive !== 'boolean') errors.add('isActive', 'Say whether the card is shown.');
    checkSchedule(errors, body);
    if (body.imagePublicId !== null && !mediaExists(state, body.imagePublicId)) {
      errors.add('imagePublicId', 'That file is not in the media library. Upload it again.');
    } else if (body.isActive === true && body.imagePublicId === null) {
      errors.add('imagePublicId', 'A card needs an image before it can be shown. Save it switched off until you have one.');
    }

    if (errors.any) {
      validationProblem(json, response, errors.errors);
      return true;
    }

    if (card !== null && body.rowVersion !== card.rowVersion) {
      stale(problem, response);
      return true;
    }

    const moving = card === null || card.departmentId !== body.departmentId;
    const held = content.megaMenuCards.filter((candidate) => candidate.departmentId === body.departmentId).length;
    if (moving && held >= MEGA_MENU.maxCardsPerDepartment) {
      problem(
        response,
        409,
        'This change cannot be applied',
        'This department already holds 6 cards. Remove one you no longer need first.',
        'too_many_mega_menu_cards',
      );
      return true;
    }

    if (card === null) {
      card = { id: content.nextMegaMenuCardId++ };
      content.megaMenuCards.push(card);
    }

    Object.assign(card, {
      departmentId: body.departmentId,
      title: body.title.trim(),
      subtitle: typeof body.subtitle === 'string' && body.subtitle.trim().length > 0 ? body.subtitle.trim() : null,
      buttonLabel: body.buttonLabel.trim(),
      linkUrl: body.linkUrl.trim(),
      imagePublicId: body.imagePublicId,
      sortOrder: body.sortOrder,
      isActive: body.isActive,
      startsAt: body.startsAt,
      endsAt: body.endsAt,
      rowVersion: nextRowVersion(),
    });
    json(response, method === 'POST' ? 201 : 200, { id: card.id, rowVersion: card.rowVersion });
    return true;
  }

  // ---- size guides -------------------------------------------------------------------------------
  if (path === '/api/admin/content/size-guides' && method === 'GET') {
    const guides = [...content.sizeGuides].sort((a, b) => a.name.localeCompare(b.name));
    json(response, 200, guides.map(sizeGuideListDto));
    return true;
  }

  const guideMatch = path.match(/^\/api\/admin\/content\/size-guides\/(\d+)$/);
  let guide = null;
  if (guideMatch) {
    guide = content.sizeGuides.find((candidate) => candidate.id === Number(guideMatch[1])) ?? null;
    if (!guide) {
      problem(response, 404, 'Not found', 'That size guide is not there.', 'not_found');
      return true;
    }
  }

  if (guideMatch && method === 'GET') {
    json(response, 200, sizeGuideDto(state, guide));
    return true;
  }

  if (guideMatch && method === 'DELETE') {
    if (ifMatch(request) !== guide.rowVersion) {
      stale(problem, response);
      return true;
    }
    if (guide.categoryCount > 0 || guide.productCount > 0) {
      problem(
        response,
        409,
        'This change cannot be applied',
        'This size guide is still in use.',
        'size_guide_in_use',
        { categoryCount: guide.categoryCount, productCount: guide.productCount },
      );
      return true;
    }
    content.sizeGuides = content.sizeGuides.filter((candidate) => candidate !== guide);
    noContent(response);
    return true;
  }

  if ((path === '/api/admin/content/size-guides' && method === 'POST') || (guideMatch && method === 'PUT')) {
    const body = await readJson();
    if (body === null) return true;

    const errors = new Errors();
    missingKeys(body, method === 'PUT' ? [...SIZE_GUIDE_KEYS, 'rowVersion'] : SIZE_GUIDE_KEYS, errors);
    if (errors.any) {
      validationProblem(json, response, errors.errors);
      return true;
    }

    checkText(errors, 'name', body.name, 100, true);
    checkTable(errors, body.tableJson);

    const intro = sanitise(typeof body.introHtml === 'string' ? body.introHtml : null);
    const tips = sanitise(typeof body.measuringTipsHtml === 'string' ? body.measuringTipsHtml : null);
    if (body.introHtml !== null && typeof body.introHtml !== 'string') errors.add('introHtml', 'Must be text.');
    if (body.measuringTipsHtml !== null && typeof body.measuringTipsHtml !== 'string') errors.add('measuringTipsHtml', 'Must be text.');
    if ((intro.html ?? '').length > LIMITS.maxHtmlLength) errors.add('introHtml', `At most ${LIMITS.maxHtmlLength} characters.`);
    if ((tips.html ?? '').length > LIMITS.maxHtmlLength) errors.add('measuringTipsHtml', `At most ${LIMITS.maxHtmlLength} characters.`);
    if (body.imagePublicId !== null && !mediaExists(state, body.imagePublicId)) {
      errors.add('imagePublicId', 'That file is not in the media library. Upload it again.');
    }

    if (errors.any) {
      validationProblem(json, response, errors.errors);
      return true;
    }

    if (guide === null) {
      guide = { id: content.nextSizeGuideId++, categoryCount: 0, productCount: 0 };
      content.sizeGuides.push(guide);
    } else if (body.rowVersion !== guide.rowVersion) {
      stale(problem, response);
      return true;
    }

    Object.assign(guide, {
      name: body.name.trim(),
      introHtml: intro.html,
      tableJson: body.tableJson,
      measuringTipsHtml: tips.html,
      imagePublicId: body.imagePublicId,
      updatedAtUtc: new Date().toISOString(),
      rowVersion: nextRowVersion(),
    });

    json(response, method === 'POST' ? 201 : 200, {
      id: guide.id,
      rowVersion: guide.rowVersion,
      removedFromIntro: intro.removed,
      removedFromTips: tips.removed,
    });
    return true;
  }

  return false;
}

function checkTable(errors, tableJson) {
  if (typeof tableJson !== 'string') {
    errors.add('tableJson', 'Send the size table.');
    return;
  }

  let table;
  try {
    table = JSON.parse(tableJson);
  } catch {
    errors.add('tableJson', 'The size table could not be read.');
    return;
  }

  const { columns, rows } = table ?? {};
  if (!Array.isArray(columns) || !Array.isArray(rows)) {
    errors.add('tableJson', 'The size table needs columns and rows.');
    return;
  }
  if (columns.length < 1 || columns.length > LIMITS.maxColumns) {
    errors.add('tableJson', `Use 1 to ${LIMITS.maxColumns} columns.`);
  }
  if (rows.length < 1 || rows.length > LIMITS.maxRows) {
    errors.add('tableJson', `Use 1 to ${LIMITS.maxRows} rows.`);
  }
  if (columns.length * rows.length > LIMITS.maxCells) {
    errors.add('tableJson', `At most ${LIMITS.maxCells} cells.`);
  }
  if (columns.some((heading) => typeof heading !== 'string' || heading.trim().length === 0)) {
    errors.add('tableJson', 'Every column needs a heading.');
  }
  if (rows.some((row) => !Array.isArray(row) || row.length !== columns.length)) {
    errors.add('tableJson', 'Every row must have one cell per column.');
  }
  const cells = [...columns, ...rows.flat()];
  if (cells.some((cell) => typeof cell !== 'string')) {
    errors.add('tableJson', 'Every cell must be text.');
  } else if (cells.some((cell) => cell.length > LIMITS.maxCellLength)) {
    errors.add('tableJson', `Each cell holds at most ${LIMITS.maxCellLength} characters.`);
  }
}
