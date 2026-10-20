import AxeBuilder from '@axe-core/playwright';
import { Page, expect, test } from '@playwright/test';

import { adminURL, adminWorktreeRoot } from '../playwright.config';
import { horizontalOverflow, undersized, unreachable } from './support/measure';

/**
 * ADM-14 in the admin application: the sale-campaign screen (doc 11 P3 section A).
 *
 * It runs against `e2e/support/admin-catalog-stub.mjs`, which serves `dist/admin` and answers
 * `/api/admin/*` from the same origin. The endpoints themselves are proved against a real SQL Server in
 * `AdminSaleCampaignEndpointTests` — and that split is issue #126 rather than tidiness: **Playwright
 * never starts the .NET API**, so nothing in this file can observe a backend change, a stored row or the
 * real recompute. What it proves is the screen, and specifically the things no jsdom spec can measure:
 * that nothing scrolls sideways at 320 px, that every control clears 44 px, that no control is buried,
 * and that the heading order is right — which axe does not check, because a nested heading jump is
 * best practice rather than a WCAG failure.
 */

/** docs/04 §4 and the project's non-negotiable list. */
const BREAKPOINTS = [320, 375, 390, 768, 1024, 1280, 1440, 1920] as const;

/**
 * **The server has to be this worktree's own before a single answer from it is trusted.**
 *
 * `reuseExistingServer` is on outside CI and the admin port is derived from the worktree path, which
 * makes a collision unlikely rather than impossible — and when one happens another worktree's
 * `dist/admin` is served against these tests.
 */
test.beforeAll(async ({ request }) => {
  const response = await request.get(`${adminURL}/__worktree`);
  expect(response.ok(), `the admin stub at ${adminURL} did not answer /__worktree`).toBe(true);

  const { root } = (await response.json()) as { root: string };

  expect(
    root.replace(/\\/g, '/').toLowerCase(),
    `the server on ${adminURL} is serving ${root}, not this worktree — a port that answers is not proof `
      + 'that it is yours (doc 14)',
  ).toBe(adminWorktreeRoot.replace(/\\/g, '/').toLowerCase());
});

/**
 * **A campaign window relative to today, never a calendar date.**
 *
 * The status is derived by the stub against its own clock (`campaignStatus` in
 * `admin-catalog-stub.mjs`, as `CampaignWindow.StatusOf` does on the real API), so a hard-coded window
 * is a test that changes its answer as the calendar moves: `2026-10-19T00:00` read "Starts later" until
 * that very day, then "Live", then "Ended" a week later. `page.clock` cannot help, because the clock that
 * decides is the stub's Node process, not the browser's.
 *
 * The value is what a `datetime-local` box holds — the shop's wall-clock time, `yyyy-MM-ddTHH:mm` with no
 * offset — at midnight `days` from today in the shop's time zone (UTC+05:30, no daylight saving, which is
 * `ColomboTime.Offset` and the stub's own arithmetic). Midnight a whole day ahead is in the future at any
 * moment of today, so a window starting there is "Scheduled" whenever this runs.
 */
function shopMidnight(days: number): string {
  const shopToday = new Date(Date.now() + 330 * 60 * 1000).toISOString().slice(0, 10);
  const day = new Date(Date.parse(`${shopToday}T00:00:00Z`) + days * 24 * 60 * 60 * 1000);

  return `${day.toISOString().slice(0, 10)}T00:00`;
}

/** A week ahead to a fortnight ahead: always in the future, and a window that ends after it starts. */
const STARTS = shopMidnight(7);
const ENDS = shopMidnight(14);

async function open(page: Page): Promise<void> {
  const state = `campaign-${Math.random().toString(36).slice(2)}`;

  await page.context().addCookies([{ name: 'e2e_state', value: state, url: adminURL }]);
  await page.goto(`${adminURL}/marketing/sale-campaigns`);

  // The page fetches on construction, so the heading alone is not enough: the empty sentence asserted
  // before the response arrives is a sentence that is legitimately absent. The category select is waited
  // for as well, because it is filled by a second round of requests.
  await expect(page.getByRole('heading', { level: 1, name: 'Sale campaigns' })).toBeVisible();
  await expect(page.locator('[data-testid="campaign-empty"]')).toBeVisible();
  await expect(page.locator('[data-testid="target-category"] option')).not.toHaveCount(1);
}

/**
 * Fills in the form and adds one category target.
 *
 * `Women › Dresses` is the stub's nearest thing to doc 11 line 450's "Women › Shirts & Blouses", and it
 * has products under it — which is what makes the difference between this and the no-products case
 * below observable at all.
 */
async function fillCampaign(page: Page, name: string, categoryLabel = 'Women › Dresses'): Promise<void> {
  await page.locator('[data-testid="campaign-name"]').fill(name);
  await page.locator('[data-testid="campaign-value"]').fill('20');
  await page.locator('[data-testid="campaign-starts"]').fill(STARTS);
  await page.locator('[data-testid="campaign-ends"]').fill(ENDS);
  await page.locator('[data-testid="target-category"]').selectOption({ label: categoryLabel });
  await page.locator('[data-testid="add-target"]').click();

  await expect(page.locator('[data-testid="campaign-targets"]')).toContainText(categoryLabel);
}

async function addCampaign(page: Page, name: string, categoryLabel?: string): Promise<void> {
  await fillCampaign(page, name, categoryLabel);
  await page.locator('[data-testid="campaign-save"]').click();

  await expect(page.locator('[data-testid="campaign-said"]')).toContainText('is saved');
}

test.describe('the sale-campaign screen (ADM-14)', () => {
  test('creates a campaign on a category and lists it', async ({ page }) => {
    await open(page);

    await addCampaign(page, 'E2ELINEN');

    await expect(page.locator('[data-testid="campaigns"]')).toContainText('E2ELINEN');
    await expect(page.locator('[data-testid="campaigns"]')).toContainText('20% off');
    await expect(page.locator('[data-testid="targets-1"]')).toContainText('Women › Dresses');

    // What the recompute did, which is the only answer to "has anything changed price".
    await expect(page.locator('[data-testid="campaign-said"]')).toContainText('repriced');
  });

  /**
   * **The warning that stops a staff member creating a campaign that silently does nothing.**
   *
   * `Traditional wear` has no products in the stub's catalogue, so the save succeeds and the API answers
   * that its targets reach nothing. It has to be readable on the page, not only announced.
   */
  test('says when a campaign reaches no product at all', async ({ page }) => {
    await open(page);

    await addCampaign(page, 'E2EEMPTY', 'Women › Traditional wear');

    await expect(page.locator('[data-testid="campaign-warnings"]')).toContainText(
      'do not reach any product',
    );
    await expect(page.locator('[data-testid="campaign-said"]')).toContainText(
      'do not reach any product',
    );
  });

  /**
   * The campaign ceiling is 90, not the promo code's 100, and the server's sentence lands against its
   * own control — which is the only thing that says where to look. The stub refuses what the API
   * refuses, so this path is reachable in a browser at all (#126).
   */
  test('shows the server message against the field it named', async ({ page }) => {
    await open(page);

    await fillCampaign(page, 'E2EBAD');
    await page.locator('[data-testid="campaign-value"]').fill('95');
    await page.locator('[data-testid="campaign-save"]').click();

    await expect(page.locator('[data-testid="error-value"]')).toContainText('up to 90');
    await expect(page.locator('[data-testid="campaign-value"]')).toHaveAttribute(
      'aria-invalid',
      'true',
    );
  });

  test('refuses an end before the start, against the end field', async ({ page }) => {
    await open(page);

    await fillCampaign(page, 'E2EWINDOW');
    // A week before the start, wherever the start is: the refusal is about the order, not the calendar.
    await page.locator('[data-testid="campaign-ends"]').fill(shopMidnight(0));
    await page.locator('[data-testid="campaign-save"]').click();

    await expect(page.locator('[data-testid="error-ends"]')).toContainText('after the start');
  });

  /**
   * Saving with nothing chosen is refused on the screen, before a request, because the chips only exist
   * here until Save — and this is the one mistake the form invites.
   */
  test('refuses to save with nothing chosen', async ({ page }) => {
    await open(page);

    await page.locator('[data-testid="campaign-name"]').fill('E2ENOTHING');
    await page.locator('[data-testid="campaign-value"]').fill('20');
    await page.locator('[data-testid="campaign-starts"]').fill(STARTS);
    await page.locator('[data-testid="campaign-ends"]').fill(ENDS);
    await page.locator('[data-testid="campaign-save"]').click();

    await expect(page.locator('[data-testid="error-targets"]')).toContainText(
      'at least one department or category',
    );
    await expect(page.locator('[data-testid="campaign-empty"]')).toBeVisible();
  });

  /** The subtree checkbox is a category target's one choice, and the chip and the row both say so. */
  test('narrows a category to itself when the subtree box is cleared', async ({ page }) => {
    await open(page);

    await page.locator('[data-testid="target-subcategories-field"]').click();
    await expect(page.locator('[data-testid="target-subcategories"]')).not.toBeChecked();

    await addCampaign(page, 'E2ENARROW');

    await expect(page.locator('[data-testid="targets-1"]')).toContainText('this category only');
  });

  /** A nested category is offered under its whole path, which is how two of the same name are told apart. */
  test('offers every category under its full path', async ({ page }) => {
    await open(page);

    const labels = await page
      .locator('[data-testid="target-category"] option')
      .allTextContents();

    expect(labels).toContain('Women › Dresses');
    expect(labels).toContain('Women › Dresses › Midi dresses');
    expect(labels).toContain('Men › Shirts');
  });

  /**
   * The derived status, which doc 11 line 474 says is computed and never stored. A campaign whose window
   * is in the future says so, and the form has no status control to contradict it. The window is a week
   * from today (`STARTS`), so this holds on whatever day it runs.
   */
  test('shows the derived status and offers no status control', async ({ page }) => {
    await open(page);

    await addCampaign(page, 'E2ESTATUS');

    await expect(page.locator('[data-testid="status-1"]')).toContainText('Starts later');
    await expect(page.locator('select[name="status"]')).toHaveCount(0);
  });

  /**
   * Remove takes two presses and the confirmation replaces the label on the same button, so focus stays
   * where the staff member put it — #109 by removal is the defect this shape avoids.
   */
  test('asks once before removing, and keeps focus on the same button', async ({ page }) => {
    await open(page);

    await addCampaign(page, 'E2EGONE');

    const remove = page.locator('[data-testid="remove-1"]');

    await remove.click();
    await expect(remove).toContainText('Yes, remove it');

    // The same element still has focus: nothing was added or taken away around it.
    await expect(remove).toBeFocused();

    await remove.click();

    await expect(page.locator('[data-testid="campaign-empty"]')).toBeVisible();
    await expect(page.locator('[data-testid="campaign-said"]')).toContainText('put back');
  });

  /** The save button carries `aria-disabled`, never `disabled` (#109). */
  test('never gives the save button the disabled attribute', async ({ page }) => {
    await open(page);

    await expect(page.locator('[data-testid="campaign-save"]')).not.toHaveAttribute('disabled', '');
  });

  /** The screen says what it does not do, so nobody hunts for an option that is not there. */
  test('says that product targets and banners are not on this screen', async ({ page }) => {
    await open(page);

    const note = page.locator('[data-testid="campaign-scope-note"]');

    await expect(note).toContainText('individual products');
    await expect(note).toContainText('banner');
  });

  test('names no example campaign or discount anywhere on the page', async ({ page }) => {
    await open(page);

    // Every box starts empty: a window or a percentage this screen invented would be a sale the client
    // never agreed to, which is what W3-03 shipped with a real bank's name as placeholder text.
    await expect(page.locator('[data-testid="campaign-name"]')).toHaveValue('');
    await expect(page.locator('[data-testid="campaign-value"]')).toHaveValue('');
    await expect(page.locator('[data-testid="campaign-starts"]')).toHaveValue('');
    await expect(page.locator('[data-testid="campaign-ends"]')).toHaveValue('');

    expect(await page.locator('input[placeholder]').count()).toBe(0);
  });

  /**
   * **The heading order, checked here because axe does not fail one.**
   *
   * An `<h2>` above a page's `<h1>` passed the axe gate last week: a nested heading jump is best practice
   * rather than a WCAG failure, so it has to be asserted rather than assumed. The shell contributes no
   * heading, so the first one on the page is this page's own.
   */
  test('starts with its h1, before any other heading', async ({ page }) => {
    await open(page);
    await addCampaign(page, 'E2EHEAD');

    const levels = await page
      .locator('h1, h2, h3, h4, h5, h6')
      .evaluateAll((nodes) => nodes.map((node) => node.tagName.toLowerCase()));

    expect(levels[0]).toBe('h1');
    expect(levels.filter((tag) => tag === 'h1')).toHaveLength(1);

    // And no level is skipped on the way down, which is the half axe does not measure either.
    for (let index = 1; index < levels.length; index += 1) {
      const previous = Number(levels[index - 1].slice(1));
      const current = Number(levels[index].slice(1));

      expect(current - previous, `${levels[index - 1]} is followed by ${levels[index]}`).toBeLessThan(
        2,
      );
    }
  });

  test('has no axe violations', async ({ page }) => {
    await open(page);
    await addCampaign(page, 'E2EAXE');

    const results = await new AxeBuilder({ page }).analyze();

    expect(results.violations).toEqual([]);
  });

  /**
   * Every control at every width, measured rather than eyeballed.
   *
   * The selectors are named one by one and each is asserted to match exactly once first, so a test that
   * measured nothing — a renamed test id, a control behind a conditional — fails rather than passing
   * vacuously (#80). That is `admin-orders.e2e.ts`' own rule and the reason it has it.
   */
  test('fits at every width with every control reachable', async ({ page }) => {
    await open(page);
    await addCampaign(page, 'E2EWIDE');

    const controls = [
      '[data-testid="campaign-name"]',
      '[data-testid="campaign-type"]',
      '[data-testid="campaign-value"]',
      '[data-testid="campaign-starts"]',
      '[data-testid="campaign-ends"]',
      '[data-testid="campaign-description"]',
      '[data-testid="campaign-active-field"]',
      '[data-testid="target-type"]',
      '[data-testid="target-category"]',
      '[data-testid="target-subcategories-field"]',
      '[data-testid="add-target"]',
      '[data-testid="campaign-save"]',
      '[data-testid="edit-1"]',
      '[data-testid="remove-1"]',
    ] as const;

    for (const width of BREAKPOINTS) {
      await page.setViewportSize({ width, height: 900 });

      expect(await horizontalOverflow(page), `horizontal overflow at ${width}px`).toBe(0);

      for (const control of controls) {
        await expect(page.locator(control), `${control} at ${width}px`).toHaveCount(1);

        expect(await undersized(page, control), `tap target ${control} at ${width}px`).toEqual([]);
        expect(await unreachable(page, control), `reachability of ${control} at ${width}px`).toEqual(
          [],
        );
      }
    }
  });

  /** A chip's Remove button is a control too, and it is the one most likely to be too small. */
  test('keeps a chosen target chip removable at every width', async ({ page }) => {
    await open(page);
    await fillCampaign(page, 'E2ECHIP');

    const chip = '[data-testid="drop-target-Category-10"]';

    for (const width of BREAKPOINTS) {
      await page.setViewportSize({ width, height: 900 });

      expect(await horizontalOverflow(page), `horizontal overflow at ${width}px`).toBe(0);
      await expect(page.locator(chip), `${chip} at ${width}px`).toHaveCount(1);
      expect(await undersized(page, chip), `tap target ${chip} at ${width}px`).toEqual([]);
      expect(await unreachable(page, chip), `reachability of ${chip} at ${width}px`).toEqual([]);
    }
  });
});
