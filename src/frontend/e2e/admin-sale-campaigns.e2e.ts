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
      'at least one department, category or product',
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

  /** The screen says how overlapping campaigns price a product, and where the banner shows (#191). */
  test('says that campaigns never add up and where the banner shows', async ({ page }) => {
    await open(page);

    const note = page.locator('[data-testid="campaign-scope-note"]');

    await expect(note).toContainText('never add up');
    await expect(note).toContainText('Sale page');
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
      '[data-testid="banner-alt"]',
      '[data-testid="banner-file"]',
      '[data-testid="banner-upload"]',
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

/**
 * #191: individual products, through a search-as-you-type combobox over the paged product list, and the
 * campaign banner, through the media library's upload.
 *
 * The stub pages `GET /api/admin/products` with the real bounds and `/__test__/many-products` adds 25
 * "Linen Shirt" rows to this browser context, so a search really does span three pages (26 matches, with
 * the seeded "Linen Shirt Dress"). Every assertion
 * about the keyboard is made with real key presses, because the ARIA 1.2 pattern is only right if focus
 * stays in the box while the highlight moves.
 */
test.describe('individual products and the banner (#191)', () => {
  const search = '[data-testid="product-search"]';

  async function withManyProducts(page: Page): Promise<void> {
    await open(page);

    const added = await page.request.get(`${adminURL}/__test__/many-products?count=25`);
    expect(added.ok()).toBe(true);

    await page.locator('[data-testid="target-type"]').selectOption('Product');
    await expect(page.locator(search)).toBeVisible();
  }

  async function searchFor(page: Page, term: string): Promise<void> {
    await page.locator(search).fill(term);
    await expect(page.locator('[data-testid="product-search-status"]')).not.toContainText('Searching');
  }

  test('finds, pages through and adds products with the keyboard alone', async ({ page }) => {
    await withManyProducts(page);

    await page.locator(search).focus();
    await page.keyboard.type('linen shirt');

    const status = page.locator('[data-testid="product-search-status"]');
    // 25 added rows and the seeded "Linen Shirt Dress", which the same term matches.
    await expect(status).toHaveText('26 products match. Showing 1 to 10.');

    const box = page.locator(search);
    await expect(box).toHaveAttribute('aria-expanded', 'true');
    await expect(box).toHaveAttribute('aria-activedescendant', /campaign-product-option-/);
    await expect(page.locator('[role="option"]')).toHaveCount(10);

    // Down once: the second option is highlighted, and focus has not left the box.
    await page.keyboard.press('ArrowDown');
    const second = await page.locator('[role="option"]').nth(1).getAttribute('id');
    await expect(box).toHaveAttribute('aria-activedescendant', second!);
    await expect(page.locator('[role="option"]').nth(1)).toHaveAttribute('aria-selected', 'true');
    await expect(box).toBeFocused();

    await page.keyboard.press('Enter');

    await expect(page.locator('[data-testid="campaign-targets"]')).toContainText(
      'Linen Shirt 002 (SD-T-002)',
    );
    await expect(page.locator('[data-testid="campaign-said"]')).toContainText('Linen Shirt 002 added');
    await expect(box).toBeFocused();

    // Page Down asks the server for the next ten.
    await page.keyboard.press('PageDown');
    await expect(status).toHaveText('26 products match. Showing 11 to 20.');
    await expect(page.locator('[role="option"]').first()).toContainText('Linen Shirt 011');

    await page.keyboard.press('Enter');
    await expect(page.locator('[data-testid="campaign-targets"]')).toContainText('Linen Shirt 011');

    // The last page, by the button this time.
    await page.locator('[data-testid="product-results-next"]').click();
    await expect(status).toHaveText('26 products match. Showing 21 to 26.');
    await expect(page.locator('[role="option"]')).toHaveCount(6);
    await expect(page.locator('[data-testid="product-results-next"]')).toHaveAttribute('aria-disabled', 'true');

    // Escape closes the list and leaves the term; a second Escape clears it.
    await box.focus();
    await page.keyboard.press('Escape');
    await expect(box).toHaveAttribute('aria-expanded', 'false');
    await expect(box).toHaveValue('linen shirt');
    await page.keyboard.press('Escape');
    await expect(box).toHaveValue('');
  });

  test('marks a product already chosen and refuses it a second time', async ({ page }) => {
    await withManyProducts(page);
    await searchFor(page, 'Linen Shirt 003');

    await page.locator('[role="option"]').first().click();
    await expect(page.locator('[data-testid="campaign-targets"] li')).toHaveCount(1);

    await page.locator('[role="option"]').first().click();

    await expect(page.locator('[data-testid="campaign-targets"] li')).toHaveCount(1);
    await expect(page.locator('[data-testid="error-targets"]')).toContainText('already on this campaign');
    await expect(page.locator('[role="option"]').first()).toContainText('already on this campaign');
  });

  test('says plainly when nothing matches', async ({ page }) => {
    await withManyProducts(page);
    await searchFor(page, 'nothing-like-this');

    await expect(page.locator('[data-testid="product-search-status"]')).toContainText('No product matches');
    await expect(page.locator(search)).toHaveAttribute('aria-expanded', 'false');
  });

  /**
   * Removing chips from the keyboard: focus goes to the chip that took the removed one's place, and to
   * the "Add" control when the last one goes, never to the top of the page.
   */
  test('removes product chips from the keyboard without losing focus', async ({ page }) => {
    await withManyProducts(page);
    await searchFor(page, 'linen shirt');

    await page.locator('[role="option"]').nth(0).click();
    await page.locator('[role="option"]').nth(1).click();
    await expect(page.locator('[data-testid="campaign-targets"] li')).toHaveCount(2);

    const first = page.locator('[data-testid="campaign-targets"] button').first();
    await first.focus();
    await page.keyboard.press('Enter');

    await expect(page.locator('[data-testid="campaign-targets"] li')).toHaveCount(1);
    await expect(page.locator('[data-testid="campaign-targets"] button').first()).toBeFocused();
    await expect(page.locator('[data-testid="campaign-said"]')).toContainText('removed. 1 chosen.');

    await page.keyboard.press('Space');

    await expect(page.locator('[data-testid="campaign-no-targets"]')).toBeVisible();
    await expect(page.locator('[data-testid="target-type"]')).toBeFocused();
  });

  test('saves products with a category, lists them, and shows them as chips on edit', async ({ page }) => {
    await withManyProducts(page);

    await page.locator('[data-testid="campaign-name"]').fill('E2EPRODUCTS');
    await page.locator('[data-testid="campaign-value"]').fill('15');
    await page.locator('[data-testid="campaign-starts"]').fill(STARTS);
    await page.locator('[data-testid="campaign-ends"]').fill(ENDS);

    await searchFor(page, 'Linen Shirt 004');
    await page.locator('[role="option"]').first().click();

    await page.locator('[data-testid="target-type"]').selectOption('Category');
    await page.locator('[data-testid="target-category"]').selectOption({ label: 'Women › Dresses' });
    await page.locator('[data-testid="add-target"]').click();

    await page.locator('[data-testid="campaign-save"]').click();
    await expect(page.locator('[data-testid="campaign-said"]')).toContainText('is saved');

    const row = page.locator('[data-testid="targets-1"]');
    await expect(row).toContainText('Linen Shirt 004');
    await expect(row).toContainText('Women › Dresses');

    await page.locator('[data-testid="edit-1"]').click();

    await expect(page.locator('[data-testid="campaign-targets"] li')).toHaveCount(2);
    await expect(page.locator('[data-testid="campaign-targets"]')).toContainText('Linen Shirt 004');
  });

  test('uploads a banner, saves it, and takes it off again', async ({ page }) => {
    await open(page);
    await fillCampaign(page, 'E2EBANNER');

    await page.locator('[data-testid="banner-file"]').setInputFiles({
      name: 'banner.png',
      mimeType: 'image/png',
      buffer: Buffer.from('89504e470d0a1a0a', 'hex'),
    });
    await page.locator('[data-testid="banner-alt"]').fill('Linen Week: 20% off dresses');
    await page.locator('[data-testid="banner-upload"]').click();

    const preview = page.locator('[data-testid="banner-preview"]');
    await expect(preview).toContainText('Linen Week: 20% off dresses');
    await expect(preview.locator('img')).toHaveAttribute('alt', 'Linen Week: 20% off dresses');

    await page.locator('[data-testid="campaign-save"]').click();
    await expect(page.locator('[data-testid="campaign-said"]')).toContainText('is saved');
    await expect(page.locator('[data-testid="banner-1"]')).toContainText('Linen Week: 20% off dresses');

    await page.locator('[data-testid="edit-1"]').click();
    await expect(page.locator('[data-testid="banner-preview"]')).toBeVisible();

    await page.locator('[data-testid="banner-remove"]').click();
    await expect(page.locator('[data-testid="banner-empty"]')).toBeVisible();
    await expect(page.locator('[data-testid="banner-file"]')).toBeFocused();

    await page.locator('[data-testid="campaign-save"]').click();
    await expect(page.locator('[data-testid="campaign-said"]')).toContainText('is saved');
    await expect(page.locator('[data-testid="banner-1"]')).toContainText('None');
  });

  test('asks for a description before uploading a banner', async ({ page }) => {
    await open(page);

    await page.locator('[data-testid="banner-file"]').setInputFiles({
      name: 'banner.png',
      mimeType: 'image/png',
      buffer: Buffer.from('89504e470d0a1a0a', 'hex'),
    });
    await page.locator('[data-testid="banner-upload"]').click();

    await expect(page.locator('[data-testid="banner-error"]')).toContainText('Describe the picture first');
    await expect(page.locator('[data-testid="banner-alt"]')).toHaveAttribute('aria-invalid', 'true');
    await expect(page.locator('[data-testid="banner-empty"]')).toBeVisible();
  });

  test('has no axe violations with the product list open and a banner chosen', async ({ page }) => {
    await withManyProducts(page);
    await searchFor(page, 'linen shirt');

    await page.locator('[data-testid="banner-file"]').setInputFiles({
      name: 'banner.png',
      mimeType: 'image/png',
      buffer: Buffer.from('89504e470d0a1a0a', 'hex'),
    });
    await page.locator('[data-testid="banner-alt"]').fill('A described banner');
    await page.locator('[data-testid="banner-upload"]').click();
    await expect(page.locator('[data-testid="banner-preview"]')).toBeVisible();

    await page.locator('[role="option"]').first().click();
    await expect(page.locator(search)).toHaveAttribute('aria-expanded', 'true');

    const results = await new AxeBuilder({ page }).analyze();

    expect(results.violations).toEqual([]);
  });

  test('fits at every width with the product list open and every new control reachable', async ({ page }) => {
    await withManyProducts(page);
    await searchFor(page, 'linen shirt');
    await page.locator('[role="option"]').first().click();

    const controls = [
      search,
      '[data-testid="product-results-next"]',
      '[data-testid="product-results-previous"]',
      '[data-testid="drop-target-Product-400"]',
      '[data-testid="banner-alt"]',
      '[data-testid="banner-file"]',
      '[data-testid="banner-upload"]',
    ] as const;

    for (const width of BREAKPOINTS) {
      await page.setViewportSize({ width, height: 900 });

      expect(await horizontalOverflow(page), `horizontal overflow at ${width}px`).toBe(0);

      for (const control of controls) {
        await expect(page.locator(control), `${control} at ${width}px`).toHaveCount(1);
        expect(await undersized(page, control), `tap target ${control} at ${width}px`).toEqual([]);
        expect(await unreachable(page, control), `reachability of ${control} at ${width}px`).toEqual([]);
      }

      // Every option is a 44 px row a finger can hit.
      expect(await undersized(page, '[role="option"]'), `options at ${width}px`).toEqual([]);
    }
  });
});

/**
 * Tester additions (#191): a product name far longer than any seeded one, with no space to break at, at
 * every width; and a campaign built past the 200-target cap entirely through the picker, which the screen
 * must let the server refuse in words rather than hang on or lose.
 */
test.describe('the product picker at its limits (#191 tester)', () => {
  const search = '[data-testid="product-search"]';
  const LONG_NAME = `Ultra${'Longunbrokenproductname'.repeat(8)}Shirt`;

  test('a very long product name never makes the page scroll sideways, in the list or as a chip', async ({ page }) => {
    // KNOWN DEFECT (#191 tester): the list option and the chip wrap, but the form's live region
    // (`campaign-said`, "<name> added. 1 chosen.") has no `overflow-wrap`, so a name with no space to break
    // at widens the whole page (665 px at 320 for a 74-character name). Remove this line with the fix.
    test.fail(true, 'campaign-said does not wrap a long unbroken product name');
    await open(page);

    // The admin app renders in the browser, so the product list can be rewritten on its way in.
    await page.route('**/api/admin/products?**', async (route) => {
      const response = await route.fetch();
      const body = (await response.json()) as { items: { name: string; productCode: string }[] };

      body.items = body.items.map((item, index) =>
        index === 0 ? { ...item, name: LONG_NAME, productCode: `SD-${'X'.repeat(40)}` } : item,
      );

      await route.fulfill({ response, json: body });
    });

    await page.locator('[data-testid="target-type"]').selectOption('Product');
    await page.locator(search).fill('linen');
    await expect(page.locator('[role="option"]').first()).toContainText(LONG_NAME.slice(0, 30));

    await page.locator('[role="option"]').first().click();
    await expect(page.locator('[data-testid="campaign-targets"]')).toContainText(LONG_NAME.slice(0, 30));

    // Leave the list open over the chip, the widest state the form has.
    await page.locator(search).fill('linen ');
    await expect(page.locator('[role="option"]').first()).toBeVisible();

    for (const width of BREAKPOINTS) {
      await page.setViewportSize({ width, height: 900 });

      expect(await horizontalOverflow(page), `horizontal overflow at ${width}px`).toBe(0);

      const chipButton = page.locator('[data-testid="campaign-targets"] button').first();
      expect(await unreachable(page, '[data-testid="campaign-targets"] button'), `chip at ${width}px`).toEqual([]);

      const box = await chipButton.boundingBox();
      expect(box!.x + box!.width, `chip button right edge at ${width}px`).toBeLessThanOrEqual(width);
    }
  });

  test('201 products chosen through the picker are refused by the server in words, and 200 save', async ({ page }) => {
    test.setTimeout(180_000);
    await open(page);

    const added = await page.request.get(`${adminURL}/__test__/many-products?count=200`);
    expect(added.ok()).toBe(true);

    await page.locator('[data-testid="target-type"]').selectOption('Product');
    const box = page.locator(search);
    const status = page.locator('[data-testid="product-search-status"]');

    await box.focus();
    await page.keyboard.type('linen shirt');
    // 200 added rows and the seeded "Linen Shirt Dress".
    await expect(status).toHaveText('201 products match. Showing 1 to 10.');

    // Every result, by keyboard: Enter on each, Down to the next, Page Down at the end of a page.
    for (let pageNumber = 1; pageNumber <= 21; pageNumber++) {
      const count = await page.locator('[role="option"]').count();

      for (let index = 0; index < count; index++) {
        await page.keyboard.press('Enter');
        await page.keyboard.press('ArrowDown');
      }

      if (pageNumber < 21) {
        await page.keyboard.press('PageDown');
        await expect(status).toContainText(`Showing ${pageNumber * 10 + 1} to`);
      }
    }

    const chips = page.locator('[data-testid="campaign-targets"] li');
    await expect(chips).toHaveCount(201);

    await page.locator('[data-testid="campaign-name"]').fill('E2E201');
    await page.locator('[data-testid="campaign-value"]').fill('20');
    await page.locator('[data-testid="campaign-starts"]').fill(STARTS);
    await page.locator('[data-testid="campaign-ends"]').fill(ENDS);
    await page.locator('[data-testid="campaign-save"]').click();

    await expect(page.locator('[data-testid="error-targets"]')).toContainText('at most 200');
    await expect(chips).toHaveCount(201);

    // One off, from the keyboard: focus lands on the chip that took its place, and 200 save.
    await page.locator('[data-testid="campaign-targets"] button').last().focus();
    await page.keyboard.press('Enter');
    await expect(chips).toHaveCount(200);
    await expect(page.locator('[data-testid="campaign-targets"] button').last()).toBeFocused();

    await page.locator('[data-testid="campaign-save"]').click();
    await expect(page.locator('[data-testid="campaign-said"]')).toContainText('is saved');
    await expect(page.locator('[data-testid="campaigns"]')).toContainText('E2E201');
  });
});
