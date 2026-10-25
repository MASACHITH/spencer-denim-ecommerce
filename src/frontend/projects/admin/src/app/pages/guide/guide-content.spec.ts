import { Route } from '@angular/router';

import { routes } from '../../app.routes';
import { GUIDE_SECTIONS } from './content';
import { GUIDE_GROUPS, GuideBlock } from './guide.model';
import { parseGuideText } from './guide-text';

/**
 * The guide is a document staff follow literally, so its shape is checked as a whole: no duplicate anchors,
 * no link that goes nowhere, no table that lost a cell, and no admin screen that has been added without a
 * word about it. (What the guide *says* is checked against the code by a reviewer, not by a unit test.)
 */

/** Every string a block carries, markup included. */
function textsOf(block: GuideBlock): string[] {
  switch (block.kind) {
    case 'text':
    case 'heading':
    case 'callout':
      return [block.text];
    case 'steps':
    case 'list':
      return [...(block.title ? [block.title] : []), ...block.items];
    case 'table':
      return [block.caption, ...block.head, ...block.rows.flat()];
  }
}

const allTexts = GUIDE_SECTIONS.flatMap((section) => [
  section.title,
  section.summary,
  ...section.blocks.flatMap(textsOf),
]);

const ids = GUIDE_SECTIONS.map((section) => section.id);

/** Admin paths the guide links to: from `[label](/path)` markup and from each section's "Open" links. */
const linkedPaths = new Set<string>([
  ...GUIDE_SECTIONS.flatMap((section) => (section.screens ?? []).map((screen) => screen.path)),
  ...allTexts.flatMap((text) =>
    parseGuideText(text).flatMap((part) => (part.kind === 'link' && part.path.startsWith('/') ? [part.path] : [])),
  ),
]);

function routePaths(list: readonly Route[]): string[] {
  return list.flatMap((route) => [route.path ?? '', ...routePaths(route.children ?? [])]);
}

/** `/orders/SD-1/invoice` is a real address for the pattern `orders/:orderPublicId/invoice`. */
function matchesARoute(path: string): boolean {
  const wanted = path.replace(/^\//, '').split(/[?#]/)[0];

  return routePaths(routes).some((pattern) => {
    if (pattern === '**') {
      return false;
    }

    const want = wanted.split('/');
    const have = pattern.split('/');

    return want.length === have.length && have.every((segment, i) => segment.startsWith(':') || segment === want[i]);
  });
}

/**
 * Routes a reader is not sent to by the guide, with the reason. Everything else a signed-in staff member
 * can open must be linked from the guide, so a new screen cannot ship undocumented.
 */
const NOT_LINKED = new Map<string, string>([
  ['', 'the dashboard: the guide is reached from it'],
  ['guide', 'the guide itself'],
  ['login', 'the sign-in page, described in words'],
  ['login/two-factor', 'the second sign-in step, described in words'],
  ['**', 'the fallback redirect'],
]);

describe('the user guide content', () => {
  it('has sections', () => {
    expect(GUIDE_SECTIONS.length).toBeGreaterThan(10);
  });

  it('gives every section a unique, anchor-safe id', () => {
    expect(new Set(ids).size).toBe(ids.length);
    expect(ids.filter((id) => !/^[a-z0-9]+(-[a-z0-9]+)*$/.test(id))).toEqual([]);
  });

  it('uses only the known groups and keeps them in the contents order', () => {
    const order = GUIDE_SECTIONS.map((section) => GUIDE_GROUPS.indexOf(section.group));

    expect(order).not.toContain(-1);
    expect(order).toEqual([...order].sort((a, b) => a - b));
  });

  it('uses every group at least once', () => {
    const used = new Set(GUIDE_SECTIONS.map((section) => section.group));

    expect(GUIDE_GROUPS.filter((group) => !used.has(group))).toEqual([]);
  });

  it('has no empty titles, summaries, steps, lists or cells', () => {
    const empty = GUIDE_SECTIONS.flatMap((section) => [
      ...(section.title.trim() && section.summary.trim() ? [] : [section.id]),
      ...section.blocks.flatMap((block) => (textsOf(block).every((text) => text.trim().length > 0) ? [] : [section.id])),
      ...(section.blocks.length > 0 ? [] : [section.id]),
    ]);

    expect(empty).toEqual([]);
  });

  it('keeps every table rectangular, with a header for each column', () => {
    const ragged = GUIDE_SECTIONS.flatMap((section) =>
      section.blocks.flatMap((block) =>
        block.kind === 'table' && (block.rows.length === 0 || block.rows.some((row) => row.length !== block.head.length))
          ? [`${section.id}: ${block.caption}`]
          : [],
      ),
    );

    expect(ragged).toEqual([]);
  });

  it('numbers real steps: a steps block has at least two', () => {
    const short = GUIDE_SECTIONS.flatMap((section) =>
      section.blocks.flatMap((block) => (block.kind === 'steps' && block.items.length < 2 ? [section.id] : [])),
    );

    expect(short).toEqual([]);
  });

  it('links only to screens that exist', () => {
    const dead = [...linkedPaths].filter((path) => !matchesARoute(path));

    expect(dead).toEqual([]);
  });

  it('links to guide sections that exist', () => {
    const dead = allTexts.flatMap((text) =>
      parseGuideText(text).flatMap((part) =>
        part.kind === 'link' && part.path.startsWith('#') && !ids.includes(part.path.slice(1)) ? [part.path] : [],
      ),
    );

    expect(dead).toEqual([]);
  });

  it('documents every screen: each admin route is linked from the guide or is excused above', () => {
    const missing = routePaths(routes)
      .filter((path) => !path.includes(':') && !NOT_LINKED.has(path))
      .filter((path) => !linkedPaths.has(`/${path}`));

    expect(missing).toEqual([]);
  });

  it('carries no mis-encoded characters (UTF-8 read as Windows-1252)', () => {
    expect(allTexts.filter((text) => /â€|Ã|Â|�/.test(text))).toEqual([]);
  });

  it('keeps raw HTML out of the words (tags may appear only inside code spans, where they are shown as text)', () => {
    const outsideCode = (text: string) => text.replace(/`[^`]*`/g, '');

    expect(allTexts.filter((text) => /<\/?[a-z][^>]*>/i.test(outsideCode(text)))).toEqual([]);
  });
});
