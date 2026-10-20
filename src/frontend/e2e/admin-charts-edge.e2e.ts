import { Page, Route, expect, test } from '@playwright/test';

import { adminURL, adminWorktreeRoot } from '../playwright.config';
import { horizontalOverflow } from './support/measure';

/**
 * #186 tester: the edges of the admin charts that the developer's specs do not reach.
 *
 * Every response below is the stub's own answer (`admin-reporting-stub.mjs`, which mirrors the API's DTOs field
 * for field) with only some values changed, so no shape here is invented: very large rupee figures, a very long
 * department and category name, a single category, a period where every breakdown is zero, and the API failing.
 * Each is measured at the project's eight widths with every "Show the figures as a table" opened, because the
 * folded tables are the widest thing on these pages and the sweep in `admin-dashboard.e2e.ts` keeps them closed.
 */

const BREAKPOINTS = [320, 375, 390, 768, 1024, 1280, 1440, 1920] as const;

const LONG_NAME = 'Traditional handloom sarees, osariya and lama saree sets with matching blouses (Kandyan)';

test.beforeAll(async ({ request }) => {
  const response = await request.get(`${adminURL}/__worktree`);
  expect(response.ok(), `the admin stub at ${adminURL} did not answer /__worktree`).toBe(true);

  const { root } = (await response.json()) as { root: string };

  expect(root.replace(/\\/g, '/').toLowerCase()).toBe(adminWorktreeRoot.replace(/\\/g, '/').toLowerCase());
});

async function open(page: Page, path: string, set: 'some' | 'none' = 'some'): Promise<void> {
  const state = `charts-edge-${Math.random().toString(36).slice(2)}`;
  await page.context().addCookies([{ name: 'e2e_state', value: state, url: adminURL }]);
  expect((await page.request.get(`${adminURL}/__test__/reporting?set=${set}`, { headers: { Cookie: `e2e_state=${state}` } })).ok()).toBe(true);
  await page.goto(`${adminURL}${path}`);
}

/** Answers `pattern` with the stub's own body, changed by `change`. */
async function reshape<T>(page: Page, pattern: string | RegExp, change: (body: T) => T): Promise<void> {
  await page.route(pattern, async (route: Route) => {
    const response = await route.fetch();
    const body = (await response.json()) as T;
    await route.fulfill({ response, json: change(body) });
  });
}

/** Opens every folded table on the page, so the sweep measures them too. */
async function openEveryTable(page: Page): Promise<void> {
  await page.evaluate(() => document.querySelectorAll('details').forEach((details) => (details.open = true)));
}

async function noSidewaysScrollAtAnyWidth(page: Page, what: string): Promise<void> {
  for (const width of BREAKPOINTS) {
    await page.setViewportSize({ width, height: 900 });
    await openEveryTable(page);
    // Let Chart.js's resize observer settle on the new width before measuring.
    await page.waitForTimeout(150);
    expect(await horizontalOverflow(page), `${what} scrolls sideways at ${width}px`).toBeLessThanOrEqual(0);
  }
}

async function noNaNOrInfinity(page: Page, what: string): Promise<void> {
  const text = await page.locator('main').innerText();
  expect(text, `${what} prints NaN`).not.toMatch(/NaN/);
  expect(text, `${what} prints Infinity`).not.toMatch(/Infinity/);
  const labels = await page.locator('canvas[role="img"]').evaluateAll((canvases) => canvases.map((canvas) => canvas.getAttribute('aria-label') ?? ''));
  for (const label of labels) {
    expect(label, `${what}: a chart summary`).not.toMatch(/NaN|Infinity|undefined/);
  }
}

type Json = Record<string, unknown>;

test.describe('the admin charts at their edges (#186 tester)', () => {
  test('very large rupee figures, a very long name and a single category fit at every width, and the legend matches the table', async ({ page }) => {
    await reshape<Json>(page, /\/api\/admin\/dashboard\?/, (body) => {
      const dashboard = body as unknown as {
        kpis: Record<string, { current: number | null; previous: number | null; change: number | null; percentChange: number | null }>;
        departments: { id: number; name: string; units: number; revenue: number; sharePercent: number }[];
        topCategories: { id: number; name: string; units: number; revenue: number; sharePercent: number }[];
      };
      dashboard.kpis['grossRevenue'] = { current: 98_765_432.1, previous: 12_345_678.9, change: 86_419_753.2, percentChange: 700 };
      dashboard.departments = [
        { id: 1, name: LONG_NAME, units: 1_234, revenue: 12_345_678.9, sharePercent: 100 },
        { id: 2, name: 'Men', units: 0, revenue: 0, sharePercent: 0 },
      ];
      dashboard.topCategories = [{ id: 11, name: LONG_NAME, units: 1_234, revenue: 12_345_678.9, sharePercent: 100 }];
      return body;
    });

    await open(page, '/');
    await expect(page.locator('[data-testid="dashboard-departments-chart"]')).toBeVisible();

    const legend = page.locator('[data-testid="dashboard-departments-chart-legend"] li').first();
    await expect(legend).toContainText(LONG_NAME);
    await expect(legend).toContainText('12,345,678.90');
    await expect(legend).toContainText('100.0%');
    await expect(page.locator('[data-testid="dashboard-departments"] tbody tr').first()).toContainText('12,345,678.90');
    await expect(page.locator('[data-testid="dashboard-categories"] tbody tr')).toHaveCount(1);
    await expect(page.locator('[data-testid="dashboard-categories-chart"]')).toHaveAttribute('aria-label', /Largest: .*12,345,678\.90 \(100\.0%\)/);
    await expect(page.locator('[data-testid="kpi-gross"]')).toContainText('98,765,432.10');

    await noNaNOrInfinity(page, 'the dashboard with large figures');
    await noSidewaysScrollAtAnyWidth(page, 'the dashboard with large figures');
  });

  test('a period where every breakdown is zero says so in words, with no NaN and no empty doughnut', async ({ page }) => {
    await reshape<Json>(page, /\/api\/admin\/dashboard\?/, (body) => {
      const dashboard = body as unknown as {
        orderStatuses: { count: number }[];
        paymentMethods: { paidOrders: number; revenue: number }[];
        departments: { revenue: number; units: number; sharePercent: number }[];
        topCategories: unknown[];
      };
      dashboard.orderStatuses.forEach((row) => (row.count = 0));
      dashboard.paymentMethods.forEach((row) => ((row.paidOrders = 0), (row.revenue = 0)));
      dashboard.departments.forEach((row) => ((row.revenue = 0), (row.units = 0), (row.sharePercent = 0)));
      dashboard.topCategories = [];
      return body;
    });

    await open(page, '/');
    await expect(page.locator('[data-testid="dashboard-statuses-chart-empty"]')).toContainText('No orders were placed in these days');
    await expect(page.locator('[data-testid="dashboard-methods-chart-empty"]')).toContainText('No orders were paid in these days');
    await expect(page.locator('[data-testid="dashboard-departments-chart-empty"]')).toBeVisible();
    await expect(page.locator('[data-testid="dashboard-categories-chart-empty"]')).toBeVisible();
    await expect(page.locator('[data-testid="dashboard-statuses-chart"]')).toHaveCount(0);

    // The folded tables still say 0 and 0.0%, never NaN%.
    await openEveryTable(page);
    await expect(page.locator('[data-testid="dashboard-statuses"] tbody tr').first()).toContainText('0.0%');
    await noNaNOrInfinity(page, 'the all-zero dashboard');
  });

  test('when the dashboard request fails the page still renders and says why, with a retry', async ({ page }) => {
    await page.route(/\/api\/admin\/dashboard\?/, (route) =>
      route.fulfill({ status: 500, contentType: 'application/problem+json', body: JSON.stringify({ type: 'about:blank', title: 'Server error', status: 500 }) }),
    );

    await open(page, '/');
    await expect(page.getByRole('heading', { level: 1, name: 'Dashboard' })).toBeVisible();
    await expect(page.locator('[data-testid="dashboard-problem"]')).toBeVisible();
    await expect(page.locator('[data-testid="dashboard-problem"] button')).toHaveText('Try again');
    // The Today banner asks the same endpoint for today; it says it could not read the figure rather than 0.
    await expect(page.locator('[data-testid="today-revenue"]')).toContainText('—');
    // The trend is its own request and still draws.
    await expect(page.locator('[data-testid="trend-chart"]')).toBeVisible();
    await noNaNOrInfinity(page, 'the dashboard after a failure');
    await noSidewaysScrollAtAnyWidth(page, 'the dashboard after a failure');
  });

  test('when the trend request fails the card says why and offers a retry that works', async ({ page }) => {
    let fail = true;
    await page.route(/\/api\/admin\/dashboard\/trend\?/, (route) =>
      fail
        ? route.fulfill({ status: 500, contentType: 'application/problem+json', body: JSON.stringify({ type: 'about:blank', title: 'Server error', status: 500 }) })
        : route.continue(),
    );

    await open(page, '/');
    await expect(page.locator('[data-testid="trend-problem"]')).toBeVisible();
    fail = false;
    await page.locator('[data-testid="trend-problem"] button').click();
    await expect(page.locator('[data-testid="trend-chart"]')).toBeVisible();
  });

  test('Monthly and Weekly switch the chart, its table and its wording, and say which is pressed', async ({ page }) => {
    await open(page, '/');
    await expect(page.locator('[data-testid="trend-chart"]')).toBeVisible();
    await expect(page.locator('[data-testid="trend-month"]')).toHaveAttribute('aria-pressed', 'true');
    await openEveryTable(page);
    await expect(page.locator('[data-testid="trend-table"] thead th').first()).toHaveText('Month');
    const months = await page.locator('[data-testid="trend-table"] tbody tr').count();

    await page.locator('[data-testid="trend-week"]').focus();
    await page.keyboard.press('Enter');
    await expect(page.locator('[data-testid="trend-week"]')).toHaveAttribute('aria-pressed', 'true');
    await expect(page.locator('[data-testid="trend-month"]')).toHaveAttribute('aria-pressed', 'false');
    await expect(page.locator('[data-testid="trend-window"]')).toContainText('Weekly');
    await expect(page.locator('[data-testid="trend-chart"]')).toHaveAttribute('aria-label', /by week/);
    await openEveryTable(page);
    await expect(page.locator('[data-testid="trend-table"] thead th').first()).toHaveText('Week starting');
    expect(await page.locator('[data-testid="trend-table"] tbody tr').count()).toBeGreaterThan(months);

    await page.locator('[data-testid="trend-month"]').click();
    await expect(page.locator('[data-testid="trend-chart"]')).toHaveAttribute('aria-label', /by month/);
  });

  test('the Traffic page and both reports have no sideways scroll at any width with every table open', async ({ page }) => {
    await reshape<Json>(page, /\/api\/admin\/reports\/sales\?/, (body) => {
      const report = body as unknown as { rows: { grossRevenue: number }[] };
      report.rows.forEach((row, index) => (row.grossRevenue = 10_000_000 + index * 1_234_567.89));
      return body;
    });

    for (const [path, ready] of [
      ['/traffic', '[data-testid="traffic-days-chart"]'],
      ['/reports?report=sales', '[data-testid="sales-chart"]'],
      ['/reports?report=inventory', '[data-testid="inventory-chart"]'],
    ] as const) {
      await page.setViewportSize({ width: 1440, height: 900 });
      await open(page, path);
      await expect(page.locator(ready)).toBeVisible();
      await noNaNOrInfinity(page, path);
      await noSidewaysScrollAtAnyWidth(page, path);
    }
  });

  test('the Traffic page with no visits says so instead of drawing zeros', async ({ page }) => {
    await open(page, '/traffic', 'none');
    await expect(page.locator('[data-testid="traffic-empty"]')).toBeVisible();
    await expect(page.locator('canvas')).toHaveCount(0);
    await noNaNOrInfinity(page, 'the empty Traffic page');
  });

  test('the reports with no data say so instead of drawing an empty chart', async ({ page }) => {
    await open(page, '/reports?report=sales', 'none');
    await expect(page.locator('[data-testid="sales-chart-empty"]')).toBeVisible();
    await noNaNOrInfinity(page, 'the empty sales report');

    await open(page, '/reports?report=inventory', 'none');
    await expect(page.locator('[data-testid="inventory-chart-empty"]')).toContainText('There are no active variants yet');
    await noNaNOrInfinity(page, 'the empty inventory report');
  });
});
