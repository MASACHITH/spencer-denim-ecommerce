import AxeBuilder from '@axe-core/playwright';
import { Page, expect, test } from '@playwright/test';

import { adminURL, adminWorktreeRoot } from '../playwright.config';
import { horizontalOverflow, undersized, unreachable } from './support/measure';

/**
 * ADM-12 in the admin application (#190): the inventory list, a single stock change, the bulk update, the
 * low-stock threshold and the movement history.
 *
 * It runs against `e2e/support/admin-catalog-stub.mjs` (inventory routes in `admin-inventory-stub.mjs`).
 * **Playwright never starts the .NET API**, so nothing here observes a backend change — the conditional
 * updates, the races and the ledger are proved against SQL Server in `AdminInventoryEndpointTests`,
 * `InventoryConcurrencyTests` and `OrderStockMovementTests` (#126). What this file proves is the screen: the
 * requests it sends, what it shows for each answer, focus, no sideways scroll at any project width with every
 * panel open, 44 px controls that are on screen and not buried, heading order and axe.
 */

const BREAKPOINTS = [320, 375, 390, 768, 1024, 1280, 1440, 1920] as const;

test.beforeAll(async ({ request }) => {
  const response = await request.get(`${adminURL}/__worktree`);
  expect(response.ok(), `the admin stub at ${adminURL} did not answer /__worktree`).toBe(true);

  const { root } = (await response.json()) as { root: string };

  expect(
    root.replace(/\\/g, '/').toLowerCase(),
    `the server on ${adminURL} is serving ${root}, not this worktree (doc 14)`,
  ).toBe(adminWorktreeRoot.replace(/\\/g, '/').toLowerCase());
});

/** A state of this test's own, filled with inventory fixtures, then the page opened. */
async function open(page: Page, path = '/inventory', set: 'some' | 'many' | 'none' = 'some'): Promise<string> {
  const state = `inventory-${Math.random().toString(36).slice(2)}`;
  await page.context().addCookies([{ name: 'e2e_state', value: state, url: adminURL }]);

  const headers = { Cookie: `e2e_state=${state}` };
  expect((await page.request.get(`${adminURL}/__test__/inventory?set=${set}`, { headers })).ok()).toBe(true);
  expect((await page.request.get(`${adminURL}/__test__/reporting?set=some`, { headers })).ok()).toBe(true);

  await page.goto(`${adminURL}${path}`);
  return state;
}

/** Narrows the stub's grant-everything session to `permissions`, as a custom role would be. */
async function asRole(page: Page, permissions: string[]): Promise<void> {
  await page.route('**/api/admin/auth/whoami', async (route) => {
    const response = await route.fetch();
    const session = await response.json();
    await route.fulfill({ response, json: { ...session, grantsAllPermissions: false, permissions } });
  });
}

const row = (page: Page, variantId: number) => page.locator(`[data-testid="variant-${variantId}"]`);

test.describe('the inventory list', () => {
  test('shows each variant’s figures and status, and filters by status, department and category', async ({ page }) => {
    await open(page);

    await expect(page.getByRole('heading', { level: 1, name: 'Inventory' })).toBeVisible();
    await expect(page.locator('[data-testid="inventory-paging"]')).toContainText('1–5 of 5');
    await expect(page.locator('[data-testid="status-1"]')).toHaveText('In stock');
    await expect(page.locator('[data-testid="status-2"]')).toHaveText('Low stock');
    await expect(page.locator('[data-testid="status-3"]')).toHaveText('Out of stock');
    await expect(page.locator('[data-testid="threshold-4"]')).toHaveText('50');
    await expect(page.locator('[data-testid="threshold-1"]')).toHaveText('5 (default)');
    // A threshold of 0 never reports low, however little is left.
    await expect(page.locator('[data-testid="status-5"]')).toHaveText('In stock');

    await page.locator('[data-testid="inventory-status"]').selectOption('OutOfStock');
    await page.locator('[data-testid="inventory-apply"]').click();
    await expect(page.locator('[data-testid="inventory-paging"]')).toContainText('1–1 of 1');
    await expect(row(page, 3)).toBeVisible();

    await page.locator('[data-testid="inventory-clear"]').click();
    await page.locator('[data-testid="inventory-department"]').selectOption({ label: 'Women' });
    await page.locator('[data-testid="inventory-category"]').selectOption({ label: 'Dresses' });
    await page.locator('[data-testid="inventory-apply"]').click();

    // Dresses includes the Maxi dresses below it.
    await expect(page.locator('[data-testid="inventory-paging"]')).toContainText('1–1 of 1');
    await expect(row(page, 4)).toBeVisible();
  });

  test('pages and sorts on the server', async ({ page }) => {
    await open(page, '/inventory', 'many');

    await expect(page.locator('[data-testid="inventory-paging"]')).toContainText('1–25 of 35');
    await page.locator('[data-testid="inventory-next"]').click();
    await expect(page.locator('[data-testid="inventory-paging"]')).toContainText('26–35 of 35');

    await page.locator('[data-testid="inventory-sort"]').selectOption('Available:Asc');
    await page.locator('[data-testid="inventory-apply"]').click();
    await expect(page.locator('[data-testid="inventory-paging"]')).toContainText('1–25 of 35');
    await expect(page.locator('tbody tr').first().locator('[data-testid^="available-"]')).toHaveText('0');
  });

  test('is reached from the dashboard, already filtered to low stock', async ({ page }) => {
    await open(page, '/');

    await expect(page.locator('[data-testid="dashboard-inventory-link"]')).toBeVisible();
    await page.locator('[data-testid="low-stock-inventory"]').click();

    await expect(page).toHaveURL(/\/inventory\?status=LowStock$/);
    await expect(page.locator('[data-testid="inventory-status"]')).toHaveValue('LowStock');
    await expect(page.locator('[data-testid="inventory-paging"]')).toContainText('1–2 of 2');
  });

  test('shows a viewer the stock and the history, and nothing that changes it', async ({ page }) => {
    await asRole(page, ['Inventory.View']);
    await open(page);

    await expect(row(page, 1)).toBeVisible();
    await expect(page.locator('[data-testid="inventory-read-only"]')).toBeVisible();
    await expect(page.locator('[data-testid="change-1"]')).toHaveCount(0);
    await expect(page.locator('[data-testid="select-1"]')).toHaveCount(0);

    await page.locator('[data-testid="history-1"]').click();
    await expect(page.locator('[data-testid="movement-900"]')).toContainText('Held by an order, order SD-2026-000123');
  });

  test('tells a role without Inventory.View, and fetches no stock', async ({ page }) => {
    await asRole(page, ['Dashboard.View']);
    let asked = false;
    page.on('request', (request) => {
      asked ||= request.url().includes('/api/admin/inventory');
    });

    await open(page);

    await expect(page.locator('[data-testid="inventory-forbidden"]')).toBeVisible();
    expect(asked).toBe(false);
  });
});

test.describe('changing stock', () => {
  test('removes units with a reason, patches the row, and gives focus back to the button', async ({ page }) => {
    await open(page);

    const trigger = page.locator('[data-testid="change-1"]');
    await trigger.click();
    await expect(page.locator('#inv-editor-title')).toBeFocused();
    await expect(page.locator('#inv-editor-title')).toContainText('SD-0101-WHT-M');

    await page.locator('[data-testid="stock-action"]').selectOption('remove');
    await page.locator('[data-testid="stock-quantity"]').fill('3');
    await page.locator('[data-testid="stock-reason"]').fill('Damaged in store');
    await expect(page.locator('[data-testid="stock-preview"]')).toContainText('After saving: 9 on hand');

    const sent = page.waitForRequest((request) => request.url().endsWith('/api/admin/inventory/adjustments'));
    await page.locator('[data-testid="stock-save"]').click();
    expect((await sent).postDataJSON()).toEqual({
      reason: 'Damaged in store',
      rows: [{ variantId: 1, kind: 'Adjust', quantity: -3, expectedOnHand: null }],
    });

    await expect(page.locator('[data-testid="inventory-editor"]')).toHaveCount(0);
    await expect(page.locator('[data-testid="on-hand-1"]')).toHaveText('9');
    await expect(page.locator('[data-testid="available-1"]')).toHaveText('5');
    await expect(trigger).toBeFocused();

    await page.locator('[data-testid="history-1"]').click();
    await expect(page.locator('[data-testid="history-table"] tbody tr').first()).toContainText('Damaged in store');
  });

  test('refuses before sending only a missing reason; the reserved floor is the server’s to decide', async ({ page }) => {
    const state = await open(page);
    let sent = 0;
    page.on('request', (request) => {
      sent += request.url().endsWith('/adjustments') ? 1 : 0;
    });

    await page.locator('[data-testid="change-1"]').click();
    await page.locator('[data-testid="stock-action"]').selectOption('remove');
    await page.locator('[data-testid="stock-quantity"]').fill('9');
    await page.locator('[data-testid="stock-save"]').click();

    await expect(page.locator('[data-testid="stock-reason-error"]')).toContainText('Give a reason');
    await expect(page.locator('[data-testid="stock-quantity-error"]')).toHaveCount(0);
    await expect(page.locator('[data-testid="stock-preview"]')).toContainText('When this page loaded, 4 units are reserved');
    expect(sent).toBe(0);

    // With the reserved figure still true on the server, its refusal is shown and the row refreshed.
    await page.locator('[data-testid="stock-reason"]').fill('Lost');
    await page.locator('[data-testid="stock-save"]').click();
    await expect(page.locator('[data-testid="stock-refusal"]')).toContainText('4 units are reserved for unpaid orders');
    expect(sent).toBe(1);

    // The figure on screen goes stale: an order's units are released on the server. The same removal is
    // no longer blocked by the page's old figure — the server accepts it.
    expect((await page.request.get(`${adminURL}/__test__/inventory-release?variantId=1`, { headers: { Cookie: `e2e_state=${state}` } })).ok()).toBe(true);
    await page.locator('[data-testid="stock-save"]').click();
    await expect(page.locator('[data-testid="inventory-editor"]')).toHaveCount(0);
    await expect(page.locator('[data-testid="on-hand-1"]')).toHaveText('3');
    await expect(page.locator('[data-testid="change-1"]')).toBeFocused();
  });

  test('a stock-take against a count that moved is refused with the new figure, then saved against it', async ({ page }) => {
    const state = await open(page);
    await page.locator('[data-testid="change-2"]').click();
    await page.locator('[data-testid="stock-action"]').selectOption('count');
    await page.locator('[data-testid="stock-quantity"]').fill('10');
    await page.locator('[data-testid="stock-reason"]').fill('Stock-take');

    // Somebody else books a delivery while the count is being typed.
    const other = await page.request.post(`${adminURL}/api/admin/inventory/adjustments`, {
      headers: { Cookie: `e2e_state=${state}`, 'X-XSRF-TOKEN': 'e2e-antiforgery-token' },
      data: { reason: 'Delivery', rows: [{ variantId: 2, kind: 'Receive', quantity: 1, expectedOnHand: null }] },
    });
    expect(other.ok()).toBe(true);

    await page.locator('[data-testid="stock-save"]').click();
    await expect(page.locator('[data-testid="stock-refusal"]')).toContainText('On hand is now 4, not the 3');
    await expect(page.locator('[data-testid="on-hand-2"]')).toHaveText('4');

    await page.locator('[data-testid="stock-save"]').click();
    await expect(page.locator('[data-testid="inventory-editor"]')).toHaveCount(0);
    await expect(page.locator('[data-testid="on-hand-2"]')).toHaveText('10');
  });

  test('a bulk update applies row by row and lists the rows it did not change', async ({ page }) => {
    await open(page);

    await page.locator('[data-testid="select-1"]').check();
    await page.locator('[data-testid="select-3"]').check();
    await expect(page.locator('[data-testid="bulk-bar"]')).toContainText('2 selected');

    await page.locator('[data-testid="bulk-open"]').click();
    await expect(page.locator('#inv-editor-title')).toBeFocused();
    await page.locator('[data-testid="bulk-action"]').selectOption('remove');
    await page.locator('[data-testid="bulk-quantity"]').fill('2');
    await page.locator('[data-testid="bulk-reason"]').fill('Shelf audit');
    await page.locator('[data-testid="bulk-apply"]').click();

    await expect(page.locator('[data-testid="bulk-result"]')).toContainText('1 saved, 1 not changed');
    await expect(page.locator('#inv-bulk-result')).toBeFocused();
    await expect(page.locator('[data-testid="bulk-row-3"]')).toContainText('2 units are reserved for unpaid orders');
    await expect(page.locator('[data-testid="on-hand-1"]')).toHaveText('10');
    await expect(page.locator('[data-testid="on-hand-3"]')).toHaveText('2');

    // The row that was not changed stays selected, ready for another try.
    await expect(page.locator('[data-testid="select-3"]')).toBeChecked();
    await expect(page.locator('[data-testid="select-1"]')).not.toBeChecked();
  });

  test('a threshold is set, and put back to the shop default', async ({ page }) => {
    await open(page);

    await page.locator('[data-testid="set-threshold-1"]').click();
    await page.locator('[data-testid="threshold-own"]').check();
    await page.locator('[data-testid="threshold-value"]').fill('10');
    await page.locator('[data-testid="threshold-save"]').click();
    await expect(page.locator('[data-testid="threshold-1"]')).toHaveText('10');
    await expect(page.locator('[data-testid="status-1"]')).toHaveText('Low stock');

    await page.locator('[data-testid="set-threshold-1"]').click();
    await page.locator('[data-testid="threshold-default"]').check();
    const sent = page.waitForRequest((request) => request.url().endsWith('/api/admin/inventory/1/threshold'));
    await page.locator('[data-testid="threshold-save"]').click();
    expect((await sent).postDataJSON()).toEqual({ lowStockThreshold: null });
    await expect(page.locator('[data-testid="threshold-1"]')).toHaveText('5 (default)');
  });
});

test.describe('the stub is no more permissive than the API', () => {
  test('refuses what the validators refuse', async ({ page }) => {
    const state = await open(page);
    const post = (data: unknown) =>
      page.request.post(`${adminURL}/api/admin/inventory/adjustments`, {
        headers: { Cookie: `e2e_state=${state}`, 'X-XSRF-TOKEN': 'e2e-antiforgery-token' },
        data,
      });

    const cases: readonly [unknown, string][] = [
      [{ rows: [{ variantId: 1, kind: 'Adjust', quantity: 1, expectedOnHand: null }] }, 'reason'],
      [{ reason: ' ', rows: [{ variantId: 1, kind: 'Adjust', quantity: 1, expectedOnHand: null }] }, 'reason'],
      [{ reason: 'x', rows: [{ variantId: 1, kind: 'Adjust', quantity: 1 }] }, 'rows[0].expectedOnHand'],
      [{ reason: 'x', rows: [{ variantId: 1, kind: '2', quantity: 1, expectedOnHand: null }] }, 'rows[0].kind'],
      [{ reason: 'x', rows: [{ variantId: 1, kind: 'Adjust', quantity: 0, expectedOnHand: null }] }, 'rows[0].quantity'],
      [{ reason: 'x', rows: [{ variantId: 1, kind: 'Adjust', quantity: 100001, expectedOnHand: null }] }, 'rows[0].quantity'],
      [{ reason: 'x', rows: [{ variantId: 1, kind: 'Adjust', quantity: 1.5, expectedOnHand: null }] }, 'rows[0].quantity'],
      [{ reason: 'x', rows: [{ variantId: 1, kind: 'SetCount', quantity: 5, expectedOnHand: null }] }, 'rows[0].expectedOnHand'],
      [{ reason: 'x', rows: [{ variantId: 1, kind: 'Receive', quantity: 5, expectedOnHand: 3 }] }, 'rows[0].expectedOnHand'],
      [{ reason: 'x', rows: [{ variantId: 1, kind: 'Receive', quantity: 1, expectedOnHand: null }, { variantId: 1, kind: 'Receive', quantity: 1, expectedOnHand: null }] }, 'rows[1].variantId'],
      [{ reason: 'x', rows: [] }, 'rows'],
      // D2: names only — no signed numbers, no comma lists — and 1.0 is not a whole number.
      [{ reason: 'x', rows: [{ variantId: 1, kind: '+2', quantity: 1, expectedOnHand: null }] }, 'rows[0].kind'],
      [{ reason: 'x', rows: [{ variantId: 1, kind: 'Receive, Adjust', quantity: 1, expectedOnHand: 5 }] }, 'rows[0].kind'],
    ];

    for (const [data, field] of cases) {
      const response = await post(data);
      expect(response.status(), JSON.stringify(data)).toBe(400);
      expect(Object.keys((await response.json()).errors), JSON.stringify(data)).toContain(field);
    }

    // `1.0` has to travel as raw JSON text: a JS object would serialise it as `1`.
    const fraction = await page.request.post(`${adminURL}/api/admin/inventory/adjustments`, {
      headers: { Cookie: `e2e_state=${state}`, 'X-XSRF-TOKEN': 'e2e-antiforgery-token', 'Content-Type': 'application/json' },
      data: '{"reason":"x","rows":[{"variantId":1,"kind":"Adjust","quantity":1.0,"expectedOnHand":null}]}',
    });
    expect(fraction.status()).toBe(400);
    expect(Object.keys((await fraction.json()).errors)).toContain('rows[0].quantity');

    const threshold = await page.request.put(`${adminURL}/api/admin/inventory/1/threshold`, {
      headers: { Cookie: `e2e_state=${state}`, 'X-XSRF-TOKEN': 'e2e-antiforgery-token', 'Content-Type': 'application/json' },
      data: '{"lowStockThreshold":3.0}',
    });
    expect(threshold.status()).toBe(400);

    for (const query of ['status=1', 'status=+1', 'status=LowStock,OutOfStock', 'sort=Sku,OnHand', 'direction=Asc,Desc']) {
      const list = await page.request.get(`${adminURL}/api/admin/inventory?${query}`, { headers: { Cookie: `e2e_state=${state}` } });
      expect(list.status(), query).toBe(400);
    }
  });
});

test.describe('layout and accessibility', () => {
  test('no sideways scroll, 44 px controls on screen, with each panel open, at every width', async ({ page }) => {
    await open(page);
    await expect(row(page, 1)).toBeVisible();

    await measureAtEveryWidth(page, ['[data-testid="inventory-apply"]', '[data-testid="inventory-next"]', '[data-testid="change-4"]', '[data-testid="history-4"]']);

    await page.locator('[data-testid="change-4"]').click();
    await measureAtEveryWidth(page, ['[data-testid="stock-action"]', '[data-testid="stock-quantity"]', '[data-testid="stock-reason"]', '[data-testid="stock-save"]', '[data-testid="stock-cancel"]']);
    await page.locator('[data-testid="stock-cancel"]').click();

    await page.locator('[data-testid="set-threshold-4"]').click();
    await measureAtEveryWidth(page, ['[data-testid="threshold-save"]', '[data-testid="threshold-cancel"]']);
    await page.locator('[data-testid="threshold-cancel"]').click();

    await page.locator('[data-testid="history-1"]').click();
    await expect(page.locator('[data-testid="movement-900"]')).toBeVisible();
    await measureAtEveryWidth(page, ['[data-testid="history-close"]', '[data-testid="history-older"]']);
    await page.locator('[data-testid="history-close"]').click();

    await page.locator('[data-testid="select-4"]').check();
    await page.locator('[data-testid="bulk-open"]').click();
    await measureAtEveryWidth(page, ['[data-testid="bulk-apply"]', '[data-testid="bulk-cancel"]', '[data-testid="bulk-reason"]', '[data-testid="bulk-clear"]']);
  });

  test('passes axe and puts its one h1 first, with each panel open', async ({ page }) => {
    await open(page);
    await expect(row(page, 1)).toBeVisible();
    await expectAccessible(page, 'list');

    await page.locator('[data-testid="change-1"]').click();
    await page.locator('[data-testid="stock-save"]').click();
    await expect(page.locator('[data-testid="stock-reason-error"]')).toBeVisible();
    await expectAccessible(page, 'stock form with errors');
    await page.locator('[data-testid="stock-cancel"]').click();

    await page.locator('[data-testid="set-threshold-1"]').click();
    await page.locator('[data-testid="threshold-own"]').check();
    await expectAccessible(page, 'threshold form');
    await page.locator('[data-testid="threshold-cancel"]').click();

    await page.locator('[data-testid="history-1"]').click();
    await expect(page.locator('[data-testid="movement-900"]')).toBeVisible();
    await expectAccessible(page, 'history');
    await page.locator('[data-testid="history-close"]').click();

    await page.locator('[data-testid="select-1"]').check();
    await page.locator('[data-testid="select-3"]').check();
    await page.locator('[data-testid="bulk-open"]').click();
    await page.locator('[data-testid="bulk-action"]').selectOption('remove');
    await page.locator('[data-testid="bulk-quantity"]').fill('2');
    await page.locator('[data-testid="bulk-reason"]').fill('Shelf audit');
    await page.locator('[data-testid="bulk-apply"]').click();
    await expect(page.locator('[data-testid="bulk-result"]')).toBeVisible();
    await expectAccessible(page, 'bulk result');
  });
});

async function expectAccessible(page: Page, name: string): Promise<void> {
  const levels = await page.locator('main h1, main h2, main h3').evaluateAll((headings) => headings.map((heading) => heading.tagName));
  expect(levels[0], `first heading, ${name}`).toBe('H1');
  expect(levels.filter((level) => level === 'H1'), `h1 count, ${name}`).toHaveLength(1);

  const results = await new AxeBuilder({ page }).analyze();
  expect(
    results.violations.map((violation) => `${violation.id} (${violation.impact}): ${violation.help} at ${violation.nodes.map((node) => node.target.join(' ')).join(', ')}`),
    `axe findings, ${name}`,
  ).toEqual([]);
}

/**
 * The three questions at the eight project widths: big enough, on screen, not buried. Each clause must match
 * exactly one element first, so a renamed test id cannot make the sweep pass by matching nothing (#80).
 */
async function measureAtEveryWidth(page: Page, clauses: readonly string[]): Promise<void> {
  for (const width of BREAKPOINTS) {
    await page.setViewportSize({ width, height: 900 });

    expect(await horizontalOverflow(page), `horizontal overflow at ${width}px`).toBe(0);

    // D4a: from 1280 up the list fits its card; below that it may scroll inside it, never the page.
    if (width >= 1280) {
      const inner = await page.locator('[data-testid="inventory-table"]').locator('xpath=..').evaluate((element) => element.scrollWidth - element.clientWidth);
      expect(inner, `the table scrolls inside its card at ${width}px`).toBeLessThanOrEqual(0);
    }

    for (const clause of clauses) {
      await expect(page.locator(clause), `${clause} at ${width}px`).toHaveCount(1);
      expect(await undersized(page, clause), `tap target ${clause} at ${width}px`).toEqual([]);
      expect(await unreachable(page, clause), `reachability of ${clause} at ${width}px`).toEqual([]);

      const covered = await page.evaluate((selector) => {
        const element = document.querySelector<HTMLElement>(selector);
        if (!element) {
          return 'missing';
        }

        element.scrollIntoView({ block: 'center', inline: 'center', behavior: 'instant' });

        const box = element.getBoundingClientRect();
        const topmost = document.elementFromPoint(box.left + box.width / 2, box.top + box.height / 2);

        return topmost === null || element.contains(topmost) || topmost.contains(element)
          ? null
          : (topmost as HTMLElement).outerHTML.slice(0, 120);
      }, clause);

      expect(covered, `${clause} is covered at ${width}px`).toBeNull();
    }
  }
}
