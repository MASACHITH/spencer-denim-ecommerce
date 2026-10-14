import AxeBuilder from '@axe-core/playwright';
import { Page, expect, test } from '@playwright/test';

import { adminURL, adminWorktreeRoot } from '../playwright.config';
import { horizontalOverflow, undersized, unreachable } from './support/measure';

/**
 * ADM-13 in the admin application: the promo-codes screen.
 *
 * It runs against `e2e/support/admin-catalog-stub.mjs`, which serves `dist/admin` and answers
 * `/api/admin/*` from the same origin. The endpoints themselves are proved against a real SQL Server in
 * the integration suite — and that split is issue #126 rather than tidiness: **Playwright never starts
 * the .NET API**, so nothing in this file can observe a backend change. What it proves is the screen, and
 * specifically the three things no jsdom spec can measure: that nothing scrolls sideways at 320 px, that
 * every control clears 44 px, and that no control is buried.
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

async function open(page: Page): Promise<string> {
  const state = `promo-${Math.random().toString(36).slice(2)}`;

  await page.context().addCookies([{ name: 'e2e_state', value: state, url: adminURL }]);
  await page.goto(`${adminURL}/marketing/promo-codes`);

  // The page fetches on construction, so the heading alone is not enough: the empty sentence asserted
  // before the response arrives is a sentence that is legitimately absent.
  await expect(page.getByRole('heading', { level: 1, name: 'Promo codes' })).toBeVisible();
  await expect(page.locator('[data-testid="promo-empty"]')).toBeVisible();

  return state;
}

/** Adds a code through the form, which is the only way a code exists at all. */
async function addCode(page: Page, code: string): Promise<void> {
  await page.locator('[data-testid="promo-code"]').fill(code);
  await page.locator('[data-testid="promo-value"]').fill('10');
  await page.locator('[data-testid="promo-starts"]').fill('2026-10-13T00:00');
  await page.locator('[data-testid="promo-save"]').click();

  await expect(page.locator('[data-testid="promo-said"]')).toContainText('is saved');
}

test.describe('the promo-codes screen (ADM-13)', () => {
  test('adds a code and lists it', async ({ page }) => {
    await open(page);

    await addCode(page, 'E2EFIRST');

    await expect(page.locator('[data-testid="promo-codes"]')).toContainText('E2EFIRST');
    await expect(page.locator('[data-testid="promo-codes"]')).toContainText('10% off');

    // A code with no total limit says so rather than printing a blank.
    await expect(page.locator('[data-testid="promo-codes"]')).toContainText('no limit');
  });

  /**
   * The amount box is not rendered for a free-delivery code, because the API refuses a value sent with
   * one — a box that could be filled in would be a box whose contents refuse the save.
   */
  test('hides the amount for a free-delivery code', async ({ page }) => {
    await open(page);

    await page.locator('[data-testid="promo-type"]').selectOption('FreeDelivery');

    await expect(page.locator('[data-testid="promo-value"]')).toHaveCount(0);
    await expect(page.locator('[data-testid="promo-cap"]')).toHaveCount(0);

    await page.locator('[data-testid="promo-code"]').fill('E2ESHIP');
    await page.locator('[data-testid="promo-starts"]').fill('2026-10-13T00:00');
    await page.locator('[data-testid="promo-save"]').click();

    await expect(page.locator('[data-testid="promo-codes"]')).toContainText('Free delivery');
  });

  /**
   * The server's field message lands against its own control, which is the only thing that says where to
   * look. The stub refuses what the API refuses, so this path is reachable in a browser at all (#126).
   */
  test('shows the server message against the field it named', async ({ page }) => {
    await open(page);

    await page.locator('[data-testid="promo-code"]').fill('E2EBAD');
    await page.locator('[data-testid="promo-value"]').fill('150');
    await page.locator('[data-testid="promo-starts"]').fill('2026-10-13T00:00');
    await page.locator('[data-testid="promo-save"]').click();

    await expect(page.locator('[data-testid="error-value"]')).toContainText('up to 100');
    await expect(page.locator('[data-testid="promo-value"]')).toHaveAttribute('aria-invalid', 'true');
  });

  test('refuses an end before the start, against the end field', async ({ page }) => {
    await open(page);

    await page.locator('[data-testid="promo-code"]').fill('E2EWINDOW');
    await page.locator('[data-testid="promo-value"]').fill('10');
    await page.locator('[data-testid="promo-starts"]').fill('2026-10-20T00:00');
    await page.locator('[data-testid="promo-ends"]').fill('2026-10-13T00:00');
    await page.locator('[data-testid="promo-save"]').click();

    await expect(page.locator('[data-testid="error-ends"]')).toContainText('after the start');
  });

  /**
   * Remove takes two presses and the confirmation replaces the label on the same button, so focus stays
   * where the staff member put it — #109 by removal is the defect this shape avoids.
   */
  test('asks once before removing, and keeps focus on the same button', async ({ page }) => {
    await open(page);

    await addCode(page, 'E2EGONE');

    const remove = page.locator('[data-testid="remove-1"]');

    await remove.click();
    await expect(remove).toContainText('Yes, remove it');

    // The same element still has focus: nothing was added or taken away around it.
    await expect(remove).toBeFocused();

    await remove.click();

    await expect(page.locator('[data-testid="promo-empty"]')).toBeVisible();
    await expect(page.locator('[data-testid="promo-said"]')).toContainText('can be used again');
  });

  /**
   * Doc 11 P3 step 7's "exclude sale items", in a browser.
   *
   * **The label is the assertion.** Doc 11 §7 G-07 separates a line a sale campaign is pricing, which
   * this flag removes, from a line that only carries a markdown, which it keeps — and both are "on sale"
   * in a shopper's words. A checkbox reading only "Exclude sale items" would be read as covering both.
   * The round trip itself is proved against a real SQL Server in `AdminPromoCodeEndpointTests`; nothing
   * here can observe the API (#126).
   */
  test('offers the sale-item exclusion, saying which items it skips', async ({ page }) => {
    await open(page);

    const label = page.locator('[data-testid="promo-exclude-sale-field"]');

    await expect(label).toContainText('sale campaign');
    await expect(page.locator('#promo-exclude-sale-hint')).toContainText('marked down');

    await expect(page.locator('[data-testid="promo-exclude-sale"]')).not.toBeChecked();

    await page.locator('[data-testid="promo-exclude-sale-field"]').click();
    await expect(page.locator('[data-testid="promo-exclude-sale"]')).toBeChecked();

    await addCode(page, 'E2EEXCL');

    await expect(page.locator('[data-testid="excludes-sale-1"]')).toContainText('sale campaign');
  });

  /** The save button carries `aria-disabled`, never `disabled` (#109). */
  test('never gives the save button the disabled attribute', async ({ page }) => {
    await open(page);

    await expect(page.locator('[data-testid="promo-save"]')).not.toHaveAttribute('disabled', '');
  });

  test('names no example promo code anywhere on the page', async ({ page }) => {
    await open(page);

    const html = await page.content();

    expect(html).not.toContain('TUASL10');
    expect(html).not.toContain('FREESHIP');

    // And no input offers one as a placeholder, which is how W3-03 shipped a real bank's name.
    const placeholders = await page.locator('input[placeholder]').count();
    expect(placeholders).toBe(0);
  });

  test('has no axe violations', async ({ page }) => {
    await open(page);
    await addCode(page, 'E2EAXE');

    const results = await new AxeBuilder({ page }).analyze();

    expect(results.violations).toEqual([]);
  });

  /**
   * Every control at every width, measured rather than eyeballed.
   *
   * The selectors are named one by one and each one is asserted to match exactly once first, so a test
   * that measured nothing — a renamed test id, a control behind a conditional — fails rather than
   * passing vacuously. That is `admin-orders.e2e.ts`'s own rule and the reason it has it.
   */
  test('fits at every width with every control reachable', async ({ page }) => {
    await open(page);
    await addCode(page, 'E2EWIDE');

    const controls = [
      '[data-testid="promo-code"]',
      '[data-testid="promo-type"]',
      '[data-testid="promo-value"]',
      '[data-testid="promo-starts"]',
      '[data-testid="promo-save"]',
      '[data-testid="promo-exclude-sale-field"]',
      '[data-testid="promo-active-field"]',
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
});
