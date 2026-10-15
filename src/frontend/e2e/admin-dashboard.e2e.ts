import AxeBuilder from '@axe-core/playwright';
import { Page, expect, test } from '@playwright/test';

import { adminURL, adminWorktreeRoot } from '../playwright.config';
import { horizontalOverflow, undersized, unreachable } from './support/measure';

/**
 * ADM-04, ADM-05, ADM-07, ADM-08 (the dashboard) and ADM-19 (the reports) in the admin application.
 *
 * It runs against `e2e/support/admin-catalog-stub.mjs` (reporting routes in `admin-reporting-stub.mjs`).
 * **Playwright never starts the .NET API**, so nothing here proves a figure: the revenue rule, Colombo days,
 * the previous period and the reconciliation are proved against SQL Server in `AdminDashboardEndpointTests`,
 * `AdminReportEndpointTests`, `AdminInventoryReportTests` and `ReportingReconciliationTests` (#126). What this
 * file proves is the screens: no sideways scroll at any project width — charts and wide report tables are the
 * classic cause — 44 px controls that are reachable and not buried, heading order, axe, a text equivalent for
 * every chart, what a narrower role is not offered, and the downloaded file.
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

/** A state of this test's own, with the reporting fixtures (`some`) or a new installation's zeros (`none`). */
async function open(page: Page, path: string, set: 'some' | 'none' = 'some'): Promise<string> {
  const state = `reporting-${Math.random().toString(36).slice(2)}`;
  await page.context().addCookies([{ name: 'e2e_state', value: state, url: adminURL }]);

  const headers = { Cookie: `e2e_state=${state}` };
  expect((await page.request.get(`${adminURL}/__test__/reporting?set=${set}`, { headers })).ok()).toBe(true);

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

test.describe('the dashboard', () => {
  test('shows the KPIs against the previous period and every widget as figures', async ({ page }) => {
    await open(page, '/');

    await expect(page.getByRole('heading', { level: 1, name: 'Dashboard' })).toBeVisible();
    await expect(page.locator('[data-testid="kpi-gross"]')).toContainText('Rs. 28,200.00');
    await expect(page.locator('[data-testid="kpi-gross"]')).toContainText('Up from none in the previous period');
    await expect(page.locator('[data-testid="dashboard-period"]')).toContainText('(30 days), compared with');

    for (const table of ['departments', 'categories', 'products', 'statuses', 'methods', 'districts']) {
      await expect(page.locator(`[data-testid="dashboard-${table}"] tbody tr`).first()).toBeVisible();
    }

    await expect(page.locator('[data-testid="dashboard-buyers"]')).toContainText('not the default district on a customer account');
    await expect(page.locator('[data-testid="low-stock"] tbody tr')).not.toHaveCount(0);

    // No traffic and no conversion rate: the analytics tables they need do not exist.
    await expect(page.locator('main')).not.toContainText(/traffic|conversion|visits/i);
  });

  test('a new installation shows real zeros and says nothing is low, rather than inventing figures', async ({ page }) => {
    await open(page, '/', 'none');

    await expect(page.locator('[data-testid="kpi-gross"]')).toContainText('Rs. 0.00');
    await expect(page.locator('[data-testid="kpi-gross"]')).toContainText('No change from the previous period');
    await expect(page.locator('[data-testid="kpi-aov"]')).toContainText('—');
    await expect(page.locator('[data-testid="low-stock-summary"]')).toContainText('No variant is low on stock');
    await expect(page.locator('[data-testid="trend-chart"]')).toHaveAttribute('aria-label', /none in any month/);
  });

  test('the trend has a table of its figures, reached from the keyboard', async ({ page }) => {
    await open(page, '/');
    await expect(page.locator('[data-testid="trend-chart"]')).toHaveAttribute('aria-label', /Highest:/);

    await page.locator('summary', { hasText: 'Show the figures as a table' }).focus();
    await page.keyboard.press('Enter');

    const rows = page.locator('[data-testid="trend-table"] tbody tr');
    await expect(rows).toHaveCount(12);
    await expect(rows.last()).toContainText('Rs. 28,200.00');

    await page.locator('[data-testid="trend-week"]').click();
    await expect(rows).toHaveCount(52);
  });

  test('a custom period is sent as Colombo days and a backwards one is refused on the page', async ({ page }) => {
    await open(page, '/');
    await expect(page.locator('[data-testid="kpi-gross"]')).toBeVisible();

    await page.locator('[data-testid="dashboard-preset"]').selectOption('custom');
    await page.locator('[data-testid="dashboard-from"]').fill('2026-10-10');
    await page.locator('[data-testid="dashboard-to"]').fill('2026-10-01');
    await page.locator('[data-testid="dashboard-apply"]').click();
    await expect(page.locator('[data-testid="dashboard-period-error"]')).toContainText('before the start date');

    const request = page.waitForRequest((candidate) => candidate.url().includes('/api/admin/dashboard?'));
    await page.locator('[data-testid="dashboard-from"]').fill('2026-09-01');
    await page.locator('[data-testid="dashboard-to"]').fill('2026-09-30');
    await page.locator('[data-testid="dashboard-apply"]').click();

    const url = new URL((await request).url());
    expect(url.searchParams.get('from')).toBe('2026-09-01');
    expect(url.searchParams.get('to')).toBe('2026-09-30');
    await expect(page.locator('[data-testid="dashboard-period"]')).toContainText('(30 days)');
  });

  test('recent orders search the order list and open the existing order screen', async ({ page }) => {
    await open(page, '/');

    const first = page.locator('[data-testid="recent-orders"] a').first();
    await expect(first).toBeVisible();
    const number = (await first.textContent())!.trim();

    await page.locator('[data-testid="recent-search"]').fill(number);
    await page.locator('[data-testid="recent-search-submit"]').click();
    await expect(page.locator('[data-testid="recent-orders"] li')).toHaveCount(1);

    await page.locator('[data-testid="recent-orders"] a').first().click();
    await expect(page).toHaveURL(/\/orders\/[0-9a-f-]{36}$/);
    await expect(page.locator('[data-testid="order-number"]')).toContainText(number);
  });

  test('a role with only Dashboard.View is offered no recent orders and no Reports link', async ({ page }) => {
    await asRole(page, ['Dashboard.View']);
    await open(page, '/');

    await expect(page.locator('[data-testid="kpi-gross"]')).toBeVisible();
    await expect(page.getByRole('heading', { name: 'Recent orders' })).toHaveCount(0);
    await expect(page.locator('[data-testid="dashboard-reports-link"]')).toHaveCount(0);
    await expect(page.locator('[data-testid="low-stock-report"]')).toHaveCount(0);
  });

  test('a role without Dashboard.View is told so and nothing is fetched', async ({ page }) => {
    const asked: string[] = [];
    page.on('request', (request) => {
      if (request.url().includes('/api/admin/dashboard')) {
        asked.push(request.url());
      }
    });

    await asRole(page, ['Customers.View']);
    await open(page, '/');

    await expect(page.locator('[data-testid="dashboard-forbidden"]')).toBeVisible();
    expect(asked).toEqual([]);
  });

  test('the low-stock link opens the inventory report already filtered', async ({ page }) => {
    await open(page, '/');

    await page.locator('[data-testid="low-stock-report"]').click();
    await expect(page).toHaveURL(/\/reports\?report=inventory&status=LowStock$/);
    await expect(page.locator('[data-testid="inventory-status"]')).toHaveValue('LowStock');
    await expect(page.locator('[data-testid="inventory-table"] tbody tr').first()).toContainText('Low stock');
  });
});

test.describe('the reports', () => {
  test('the sales report totals its rows and exports the same as CSV', async ({ page }) => {
    const state = await open(page, '/reports?report=sales');
    await expect(page.locator('[data-testid="sales-totals"]')).toContainText('Rs. 28,200.00');

    await page.locator('[data-testid="sales-group"]').selectOption('Week');
    await page.locator('[data-testid="sales-apply"]').click();
    await expect(page.locator('[data-testid="sales-table"] thead')).toContainText('Week');

    const [download] = await Promise.all([page.waitForEvent('download'), page.locator('[data-testid="sales-export"]').click()]);
    expect(download.suggestedFilename()).toMatch(/^sales-report-\d{4}-\d{2}-\d{2}-to-\d{4}-\d{2}-\d{2}\.csv$/);

    const bytes = await read(download);
    expect([...bytes.subarray(0, 3)]).toEqual([0xef, 0xbb, 0xbf]);
    expect(bytes.subarray(3).toString('utf8')).toContain('"Total"');

    const exports = await page.request.get(`${adminURL}/__test__/reporting/exports`, { headers: { Cookie: `e2e_state=${state}` } });
    expect(((await exports.json()) as { exports: number }).exports).toBe(1);

    // The button kept focus throughout: never disabled, never removed (#109).
    expect(await page.evaluate(() => document.activeElement?.getAttribute('data-testid'))).toBe('sales-export');
  });

  test('the customers report export neutralises a formula and keeps Sinhala intact', async ({ page }) => {
    await open(page, '/reports?report=customers');
    await expect(page.locator('[data-testid="customers-table"] tbody tr')).toHaveCount(3);

    const [download] = await Promise.all([page.waitForEvent('download'), page.locator('[data-testid="customers-export"]').click()]);
    const text = (await read(download)).subarray(3).toString('utf8');

    expect(text).toContain('"\'=HYPERLINK(""http://evil.example"";""x"")@example.com"');
    expect(text).toContain('"\'-2+3"');
    expect(text).toContain('"නිමාලි පෙරේරා"');
  });

  test('the inventory report pages and keeps focus on Next while the next page loads', async ({ page }) => {
    await open(page, '/reports?report=inventory');
    await expect(page.locator('[data-testid="inventory-paging"]')).toContainText('1–25 of 30');

    await page.locator('[data-testid="inventory-next"]').focus();
    await page.keyboard.press('Enter');
    await expect(page.locator('[data-testid="inventory-paging"]')).toContainText('26–30 of 30');

    expect(await page.evaluate(() => document.activeElement?.getAttribute('data-testid'))).toBe('inventory-next');
    await expect(page.locator('[data-testid="inventory-next"]')).toHaveAttribute('aria-disabled', 'true');
  });

  test('a role that can view reports is offered no Export button at all', async ({ page }) => {
    await asRole(page, ['Reports.View']);

    for (const report of ['sales', 'inventory', 'customers']) {
      await open(page, `/reports?report=${report}`);
      await expect(page.locator('[data-testid="reports-no-export"]')).toBeVisible();
      await expect(page.locator(`[data-testid="${report}-export"]`)).toHaveCount(0);
    }
  });

  test('Order Staff (Dashboard.View, no Reports.View) are told reports are not theirs', async ({ page }) => {
    await asRole(page, ['Dashboard.View', 'Orders.View', 'Orders.Edit', 'Orders.Approve', 'Customers.View', 'Inventory.View']);
    await open(page, '/reports');

    await expect(page.locator('[data-testid="reports-forbidden"]')).toBeVisible();
  });
});

test.describe('the screens themselves', () => {
  test('the dashboard has no sideways scroll and no undersized, unreachable or buried control', async ({ page }) => {
    await open(page, '/');
    await expect(page.locator('[data-testid="low-stock"]')).toBeVisible();
    await expect(page.locator('[data-testid="recent-orders"]')).toBeVisible();

    await measureAtEveryWidth(page, [
      '[data-testid="dashboard-preset"]',
      '[data-testid="trend-month"]',
      '[data-testid="trend-week"]',
      '[data-testid="trend-revenue"]',
      '[data-testid="trend-orders"]',
      '[data-testid="recent-search"]',
      '[data-testid="recent-search-submit"]',
      '[data-testid="low-stock-report"]',
      '[data-testid="dashboard-reports-link"]',
      '[data-testid="dashboard-logout"]',
    ]);
  });

  for (const report of ['sales', 'inventory', 'customers'] as const) {
    test(`the ${report} report has no sideways scroll and no undersized, unreachable or buried control`, async ({ page }) => {
      await open(page, `/reports?report=${report}`);
      await expect(page.locator(`[data-testid="${report}-table"]`)).toBeVisible();

      const controls = {
        sales: ['[data-testid="sales-from"]', '[data-testid="sales-to"]', '[data-testid="sales-group"]', '[data-testid="sales-apply"]', '[data-testid="sales-export"]'],
        inventory: ['[data-testid="inventory-status"]', '[data-testid="inventory-search"]', '[data-testid="inventory-apply"]', '[data-testid="inventory-export"]', '[data-testid="inventory-prev"]', '[data-testid="inventory-next"]'],
        customers: ['[data-testid="customers-from"]', '[data-testid="customers-to"]', '[data-testid="customers-search"]', '[data-testid="customers-apply"]', '[data-testid="customers-export"]', '[data-testid="customers-prev"]', '[data-testid="customers-next"]'],
      }[report];

      await measureAtEveryWidth(page, [...controls, `[data-testid="report-tab-${report}"]`]);
    });
  }

  /** axe passed a wrongly ranked heading once, so heading order is asserted here, not left to it. */
  test('every screen passes axe and its headings go h1 then h2 with nothing skipped', async ({ page }) => {
    for (const [path, ready] of [
      ['/', '[data-testid="low-stock"]'],
      ['/reports?report=sales', '[data-testid="sales-table"]'],
      ['/reports?report=inventory', '[data-testid="inventory-table"]'],
      ['/reports?report=customers', '[data-testid="customers-table"]'],
    ] as const) {
      await open(page, path);
      await expect(page.locator(ready)).toBeVisible();

      const levels = await page.locator('main h1, main h2, main h3, main h4').evaluateAll((headings) =>
        headings.map((heading) => Number(heading.tagName.slice(1))),
      );

      expect(levels[0], `first heading on ${path}`).toBe(1);
      expect(levels.filter((level) => level === 1), `h1 count on ${path}`).toHaveLength(1);
      for (let index = 1; index < levels.length; index++) {
        expect(levels[index] - levels[index - 1], `heading ${index} skips a level on ${path}: ${levels.join(',')}`).toBeLessThanOrEqual(1);
      }

      const results = await new AxeBuilder({ page }).analyze();
      const blocking = results.violations.filter((violation) => ['serious', 'critical'].includes(violation.impact ?? ''));

      expect(blocking.map((violation) => `${violation.id}: ${violation.help}`), `serious or critical axe findings on ${path}`).toEqual([]);
    }
  });
});

async function read(download: { createReadStream(): Promise<NodeJS.ReadableStream> }): Promise<Buffer> {
  const stream = await download.createReadStream();
  const chunks: Buffer[] = [];

  for await (const chunk of stream) {
    chunks.push(chunk as Buffer);
  }

  return Buffer.concat(chunks);
}

/**
 * The three questions at the eight project widths: big enough, on screen, not buried. Each clause must match
 * exactly one element first, so a renamed test id cannot make the sweep pass by matching nothing (#80).
 */
async function measureAtEveryWidth(page: Page, clauses: readonly string[]): Promise<void> {
  for (const width of BREAKPOINTS) {
    await page.setViewportSize({ width, height: 900 });

    expect(await horizontalOverflow(page), `horizontal overflow at ${width}px`).toBe(0);

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
