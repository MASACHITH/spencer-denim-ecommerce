import { Page, Route, expect, test } from '@playwright/test';

import { adminURL, adminWorktreeRoot } from '../playwright.config';
import { horizontalOverflow } from './support/measure';

/**
 * #186b: the customers report's buyers chart — first-time and returning buyers stacked, one bar per day, week or
 * month — against the stub (`admin-reporting-stub.mjs`, `buyersBody`), which mirrors `CustomerBuyersDto` field for
 * field. The figures themselves are proved against SQL Server in `AdminCustomerBuyersTests`; here the question is
 * whether the screen shows exactly what the API sent, paints it in the theme's colours, and fits at every width
 * with its figures table open.
 */

const BREAKPOINTS = [320, 375, 390, 768, 1024, 1280, 1440, 1920] as const;

test.beforeAll(async ({ request }) => {
  const response = await request.get(`${adminURL}/__worktree`);
  expect(response.ok(), `the admin stub at ${adminURL} did not answer /__worktree`).toBe(true);

  const { root } = (await response.json()) as { root: string };

  expect(root.replace(/\\/g, '/').toLowerCase()).toBe(adminWorktreeRoot.replace(/\\/g, '/').toLowerCase());
});

async function open(page: Page, set: 'some' | 'none' = 'some'): Promise<string> {
  const state = `customers-chart-${Math.random().toString(36).slice(2)}`;
  await page.context().addCookies([{ name: 'e2e_state', value: state, url: adminURL }]);
  expect((await page.request.get(`${adminURL}/__test__/reporting?set=${set}`, { headers: { Cookie: `e2e_state=${state}` } })).ok()).toBe(true);
  await page.goto(`${adminURL}/reports?report=customers`);
  return state;
}

interface Point {
  start: string;
  end: string;
  buyers: number;
  firstTimeBuyers: number;
  returningBuyers: number;
}

interface Buyers {
  interval: string;
  buyers: number;
  firstTimeBuyers: number;
  returningBuyers: number;
  points: Point[];
}

/** Counts the canvas pixels within a few levels of a theme token's colour. */
async function pixelsOf(page: Page, token: string): Promise<number> {
  return page.locator('[data-testid="customers-chart"]').evaluate((canvas: HTMLCanvasElement, name) => {
    const { data } = canvas.getContext('2d')!.getImageData(0, 0, canvas.width, canvas.height);
    const colour = getComputedStyle(document.documentElement).getPropertyValue(name).trim();
    const [r, g, b] = [1, 3, 5].map((offset) => parseInt(colour.slice(offset, offset + 2), 16));
    let count = 0;
    for (let index = 0; index < data.length; index += 4) {
      if (data[index + 3] > 200 && Math.abs(data[index] - r) < 6 && Math.abs(data[index + 1] - g) < 6 && Math.abs(data[index + 2] - b) < 6) {
        count++;
      }
    }
    return count;
  }, token);
}

async function noSidewaysScrollAtAnyWidth(page: Page, what: string): Promise<void> {
  for (const width of BREAKPOINTS) {
    await page.setViewportSize({ width, height: 900 });
    await page.evaluate(() => document.querySelectorAll('details').forEach((details) => (details.open = true)));
    await page.waitForTimeout(150);
    expect(await horizontalOverflow(page), `${what} scrolls sideways at ${width}px with every table open`).toBeLessThanOrEqual(0);
  }
}

test.describe("the customers report's buyers chart (#186b)", () => {
  test('shows exactly the figures the API sent: the chart, its one-sentence name and its table', async ({ page }) => {
    const answer = page.waitForResponse((response) => response.url().includes('/api/admin/reports/customers/buyers'));
    await open(page);
    const body = (await (await answer).json()) as Buyers;

    // The stub's fixture: three buyers on two days; one had paid before the period.
    expect(body.interval).toBe('Day');
    expect([body.buyers, body.firstTimeBuyers, body.returningBuyers]).toEqual([3, 2, 1]);

    const canvas = page.locator('[data-testid="customers-chart"]');
    await expect(canvas).toBeVisible();
    await expect(canvas).toHaveAttribute('role', 'img');
    await expect(canvas).toHaveAttribute('aria-label', /^Buyers by day: 3 buyers in all, 2 first-time and 1 returning; the most in one day was 3 buyers, .+\.$/);
    await expect(page.locator('[data-testid="customers-chart-keys"]')).toContainText('First-time buyers');
    await expect(page.locator('[data-testid="customers-chart-keys"]')).toContainText('Returning buyers');
    await expect(page.locator('[data-testid="customers-chart-totals"]')).toHaveText('3 buyers: 2 first-time, 1 returning');

    await page.locator('[data-testid="customers-chart-card"] summary').click();
    const rows = page.locator('[data-testid="customers-chart-table"] tbody tr');
    await expect(rows).toHaveCount(body.points.length + 1);

    const cells = await rows.evaluateAll((trs) => trs.map((tr) => [...tr.children].slice(1).map((cell) => cell.textContent!.trim())));
    expect(cells).toEqual([
      ...body.points.map((point) => [String(point.buyers), String(point.firstTimeBuyers), String(point.returningBuyers)]),
      ['3', '2', '1'],
    ]);

    // The same buyers as the report's own count, which sits right above it.
    await expect(page.locator('[data-testid="customers-summary"]')).toContainText('3 buyers');
  });

  test('Chart.js painted both parts of the bars, in the theme tokens', async ({ page }) => {
    await open(page);
    await expect(page.locator('[data-testid="customers-chart"]')).toBeVisible();
    await page.waitForTimeout(200);

    expect(await pixelsOf(page, '--chart-1'), 'first-time buyers in --chart-1').toBeGreaterThan(100);
    expect(await pixelsOf(page, '--chart-2'), 'returning buyers in --chart-2').toBeGreaterThan(100);
  });

  test('fits at every width with its table open, with large figures over a whole year by month', async ({ page }) => {
    await page.route(/\/api\/admin\/reports\/customers\/buyers\?/, async (route: Route) => {
      const response = await route.fetch();
      const body = (await response.json()) as Buyers;
      body.points.forEach((point, index) => {
        point.firstTimeBuyers = 120_000 + index * 1_234;
        point.returningBuyers = 98_000 + index;
        point.buyers = point.firstTimeBuyers + point.returningBuyers;
      });
      await route.fulfill({ response, json: body });
    });

    await open(page);
    await expect(page.locator('[data-testid="customers-chart"]')).toBeVisible();
    await noSidewaysScrollAtAnyWidth(page, 'the customers report, 30 days');

    // A whole year: the server groups by month, and the labels are date ranges.
    await page.setViewportSize({ width: 1440, height: 900 });
    const yearAgo = await page.evaluate(() => {
      const today = new Date(Date.now() + 330 * 60 * 1000);
      today.setUTCDate(today.getUTCDate() - 364);
      return today.toISOString().slice(0, 10);
    });
    await page.locator('[data-testid="customers-from"]').fill(yearAgo);
    await page.locator('[data-testid="customers-from"]').dispatchEvent('change');
    const monthly = page.waitForResponse((response) => response.url().includes('/api/admin/reports/customers/buyers'));
    await page.locator('[data-testid="customers-apply"]').click();
    expect(((await (await monthly).json()) as Buyers).interval).toBe('Month');
    await expect(page.locator('[data-testid="customers-chart-card"] h3')).toHaveText('Buyers by month, first-time and returning');

    const text = await page.locator('main').innerText();
    expect(text).not.toMatch(/NaN|Infinity|undefined/);
    await noSidewaysScrollAtAnyWidth(page, 'the customers report, a year by month');
  });

  test('when nobody paid it says so once, and draws no chart', async ({ page }) => {
    await open(page, 'none');
    await expect(page.locator('[data-testid="customers-empty"]')).toBeVisible();
    await expect(page.locator('[data-testid="customers-chart-card"]')).toHaveCount(0);
    await expect(page.locator('canvas')).toHaveCount(0);
  });

  test('when only the chart fails, the table stays and the card says why', async ({ page }) => {
    await page.route(/\/api\/admin\/reports\/customers\/buyers\?/, (route) => route.fulfill({ status: 500, json: { title: 'Server error' } }));
    await open(page);

    await expect(page.locator('[data-testid="customers-table"]')).toBeVisible();
    await expect(page.locator('[data-testid="customers-chart-problem"]')).toContainText('The buyers chart could not be loaded.');
  });
});
