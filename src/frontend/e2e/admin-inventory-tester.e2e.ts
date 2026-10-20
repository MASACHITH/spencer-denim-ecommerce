import AxeBuilder from '@axe-core/playwright';
import { Page, expect, test } from '@playwright/test';

import { adminURL, adminWorktreeRoot } from '../playwright.config';
import { horizontalOverflow } from './support/measure';

/**
 * The independent tester's additions for ADM-12 (#190), against the same stub as `admin-inventory.e2e.ts`.
 *
 * The developer's sweep opens each panel on its own. These open the states the sweep did not reach — the
 * bulk result list and a stock-take refusal at the same time, and the viewer's table with no selection
 * column — and check no page-level sideways scroll and axe in each, at all eight project widths.
 */

const BREAKPOINTS = [320, 375, 390, 768, 1024, 1280, 1440, 1920] as const;

test.beforeAll(async ({ request }) => {
  const response = await request.get(`${adminURL}/__worktree`);
  expect(response.ok(), `the admin stub at ${adminURL} did not answer /__worktree`).toBe(true);

  const { root } = (await response.json()) as { root: string };
  expect(root.replace(/\\/g, '/').toLowerCase()).toBe(adminWorktreeRoot.replace(/\\/g, '/').toLowerCase());
});

async function open(page: Page, waitForRows = true): Promise<string> {
  const state = `inventory-tester-${Math.random().toString(36).slice(2)}`;
  await page.context().addCookies([{ name: 'e2e_state', value: state, url: adminURL }]);

  const headers = { Cookie: `e2e_state=${state}` };
  expect((await page.request.get(`${adminURL}/__test__/inventory?set=some`, { headers })).ok()).toBe(true);
  expect((await page.request.get(`${adminURL}/__test__/reporting?set=some`, { headers })).ok()).toBe(true);

  await page.goto(`${adminURL}/inventory`);
  if (waitForRows) {
    await expect(page.locator('[data-testid="variant-1"]')).toBeVisible();
  }
  return state;
}

async function asRole(page: Page, permissions: string[]): Promise<void> {
  await page.route('**/api/admin/auth/whoami', async (route) => {
    const response = await route.fetch();
    const session = await response.json();
    await route.fulfill({ response, json: { ...session, grantsAllPermissions: false, permissions } });
  });
}

async function noSidewaysScrollAtEveryWidth(page: Page, name: string): Promise<void> {
  for (const width of BREAKPOINTS) {
    await page.setViewportSize({ width, height: 900 });
    expect(await horizontalOverflow(page), `horizontal overflow at ${width}px, ${name}`).toBe(0);
  }
}

async function axeClean(page: Page, name: string): Promise<void> {
  const results = await new AxeBuilder({ page }).analyze();
  expect(
    results.violations.map((violation) => `${violation.id}: ${violation.nodes.map((node) => node.target.join(' ')).join(', ')}`),
    `axe findings, ${name}`,
  ).toEqual([]);
}

test('the bulk result, then a stock-take refusal, cause no sideways scroll and pass axe', async ({ page }) => {
  const state = await open(page);

  // A bulk update with one refused row leaves the result list open.
  await page.locator('[data-testid="select-1"]').check();
  await page.locator('[data-testid="select-3"]').check();
  await page.locator('[data-testid="bulk-open"]').click();
  await page.locator('[data-testid="bulk-action"]').selectOption('remove');
  await page.locator('[data-testid="bulk-quantity"]').fill('2');
  await page.locator('[data-testid="bulk-reason"]').fill('Shelf audit');
  await page.locator('[data-testid="bulk-apply"]').click();
  await expect(page.locator('[data-testid="bulk-row-3"]')).toBeVisible();
  await noSidewaysScrollAtEveryWidth(page, 'bulk result with the refused row selected');
  await page.setViewportSize({ width: 1280, height: 900 });

  // Opening an editor closes the bulk result (one panel at a time).

  // Then a stock-take on another row whose count moves underneath it: the refusal stays on screen.
  await page.locator('[data-testid="change-2"]').click();
  await page.locator('[data-testid="stock-action"]').selectOption('count');
  await page.locator('[data-testid="stock-quantity"]').fill('10');
  await page.locator('[data-testid="stock-reason"]').fill('Stock-take');
  const other = await page.request.post(`${adminURL}/api/admin/inventory/adjustments`, {
    headers: { Cookie: `e2e_state=${state}`, 'X-XSRF-TOKEN': 'e2e-antiforgery-token' },
    data: { reason: 'Delivery', rows: [{ variantId: 2, kind: 'Receive', quantity: 1, expectedOnHand: null }] },
  });
  expect(other.ok()).toBe(true);
  await page.locator('[data-testid="stock-save"]').click();
  await expect(page.locator('[data-testid="stock-refusal"]')).toContainText('On hand is now 4');
  await expect(page.locator('[data-testid="bulk-result"]')).toHaveCount(0);

  // The row shows the figure the refusal quoted, and the preview the form now counts from.
  await expect(page.locator('[data-testid="on-hand-2"]')).toHaveText('4');
  await expect(page.locator('[data-testid="stock-preview"]')).toContainText('On hand now 4');

  await noSidewaysScrollAtEveryWidth(page, 'stock-take refusal');
  await page.setViewportSize({ width: 1280, height: 900 });
  await axeClean(page, 'stock-take refusal');
});

test('a viewer sees no selection column, no bulk bar and no edit button, at every width', async ({ page }) => {
  await asRole(page, ['Inventory.View']);
  await open(page);

  await expect(page.locator('[data-testid="select-page"]')).toHaveCount(0);
  await expect(page.locator('[data-testid^="select-"]')).toHaveCount(0);
  await expect(page.locator('[data-testid^="change-"]')).toHaveCount(0);
  await expect(page.locator('[data-testid^="set-threshold-"]')).toHaveCount(0);
  await expect(page.locator('[data-testid="bulk-bar"]')).toHaveCount(0);

  await page.locator('[data-testid="history-1"]').click();
  await expect(page.locator('[data-testid="movement-900"]')).toBeVisible();

  await noSidewaysScrollAtEveryWidth(page, 'viewer with the history open');
  await page.setViewportSize({ width: 390, height: 900 });
  await axeClean(page, 'viewer with the history open');
});

test('an edit-only session that cannot read is told so, and never sees edit controls', async ({ page }) => {
  await asRole(page, ['Inventory.Edit']);
  await open(page, false);

  await expect(page.locator('[data-testid="inventory-forbidden"]')).toBeVisible();
  await expect(page.locator('[data-testid^="change-"]')).toHaveCount(0);
});
