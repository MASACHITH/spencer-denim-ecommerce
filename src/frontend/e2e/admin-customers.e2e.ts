import AxeBuilder from '@axe-core/playwright';
import { Page, expect, test } from '@playwright/test';

import { adminURL, adminWorktreeRoot } from '../playwright.config';
import { horizontalOverflow, undersized, unreachable } from './support/measure';

/**
 * ADM-15 and ADM-21 in the admin application: the customer list, a customer's detail with their order
 * history, and the newsletter subscriber list with its CSV export.
 *
 * It runs against `e2e/support/admin-catalog-stub.mjs` (customer routes in `admin-customer-stub.mjs`).
 * **Playwright never starts the .NET API**, so nothing here observes a backend change — the endpoints are
 * proved against SQL Server in `AdminCustomerEndpointTests` and `AdminNewsletterEndpointTests` (#126). What
 * this file proves is the screens: no sideways scroll at any project width, 44 px controls that are on
 * screen and not buried, heading order, axe, and that the downloaded file is the one a shop owner can open.
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

/** A state of this test's own, filled with `set` customers and subscribers, then the page opened. */
async function open(
  page: Page,
  path: string,
  customers: 'some' | 'many' | 'none' = 'some',
  subscribers: 'some' | 'none' = 'some',
): Promise<void> {
  const state = `customers-${Math.random().toString(36).slice(2)}`;
  await page.context().addCookies([{ name: 'e2e_state', value: state, url: adminURL }]);

  const headers = { Cookie: `e2e_state=${state}` };
  expect((await page.request.get(`${adminURL}/__test__/customers?set=${customers}`, { headers })).ok()).toBe(true);
  expect((await page.request.get(`${adminURL}/__test__/subscribers?set=${subscribers}`, { headers })).ok()).toBe(true);

  await page.goto(`${adminURL}${path}`);
}

test.describe('the customer list', () => {
  test('finds a customer and opens their detail and order history', async ({ page }) => {
    await open(page, '/customers');

    await expect(page.getByRole('heading', { level: 1, name: 'Customers' })).toBeVisible();
    await expect(page.locator('[data-testid="customer-41"]')).toContainText('Dilani Wickramasinghe');
    await expect(page.locator('[data-testid="unconfirmed-42"]')).toBeVisible();

    await page.locator('[data-testid="customer-search"]').fill('dilani@');
    await page.locator('[data-testid="customer-search-submit"]').click();
    await expect(page.locator('[data-testid="customers-paging"]')).toContainText('1–1 of 1');

    await page.locator('[data-testid="open-customer-41"]').click();
    await expect(page.getByRole('heading', { level: 1, name: 'Dilani Wickramasinghe' })).toBeVisible();
    await expect(page.locator('[data-testid="address-7"]')).toContainText('14 Galle Road');
    await expect(page.locator('[data-testid="customer-order-SD-2026-000123"]')).toBeVisible();

    // The history links to the order screen that already exists, rather than rebuilding it.
    await page.locator('[data-testid="open-order-SD-2026-000123"]').click();
    await expect(page).toHaveURL(/\/orders\/44444444-4444-4444-8444-444444444444$/);
    await expect(page.locator('[data-testid="order-number"]')).toContainText('SD-2026-000123');
  });

  test('filters by default district from the server’s own list', async ({ page }) => {
    await open(page, '/customers');
    await expect(page.locator('[data-testid="customer-41"]')).toBeVisible();

    await page.locator('[data-testid="customer-district"]').selectOption({ label: 'Gampaha' });

    await expect(page.locator('[data-testid="customers-paging"]')).toContainText('1–1 of 1');
    await expect(page.locator('[data-testid="customer-42"]')).toHaveCount(0);
  });

  test('pages through many customers and keeps focus somewhere real', async ({ page }) => {
    await open(page, '/customers', 'many');
    await expect(page.locator('[data-testid="customers-paging"]')).toContainText('1–25 of 30');

    await page.locator('[data-testid="customers-next"]').focus();
    await page.keyboard.press('Enter');

    await expect(page.locator('[data-testid="customers-paging"]')).toContainText('26–30 of 30');

    // The Next button was replaced while the page loaded; focus went to the live region, not to <body>.
    expect(await page.evaluate(() => document.activeElement?.getAttribute('data-testid'))).toBe(
      'customers-status',
    );
  });

  test('a new installation says nobody has registered', async ({ page }) => {
    await open(page, '/customers', 'none');

    await expect(page.locator('[data-testid="customers-empty"]')).toContainText('No customer has registered yet.');
  });
});

test.describe('newsletter subscribers', () => {
  test('filters by status and says the export is subscribed-only and audited', async ({ page }) => {
    await open(page, '/marketing/newsletter');

    await expect(page.getByRole('heading', { level: 1, name: 'Newsletter subscribers' })).toBeVisible();
    await expect(page.locator('[data-testid="newsletter-counts"]')).toContainText('3 subscribed · 1 unsubscribed');

    await page.locator('[data-testid="newsletter-status"]').selectOption('Unsubscribed');
    await page.locator('[data-testid="newsletter-apply"]').click();

    await expect(page.locator('[data-testid="newsletter-paging"]')).toContainText('1–1 of 1');
    await expect(page.locator('[data-testid="subscriber-2"]')).toContainText('kasun@example.com');
    await expect(page.locator('#newsletter-export-hint')).toContainText('audit log');
  });

  /**
   * **The file a shop owner would open**: downloaded through the real button, with the list filtered to
   * Unsubscribed — and still holding subscribed addresses only, the formula cell neutralised, the Sinhala
   * address intact behind a UTF-8 byte-order mark.
   */
  test('downloads a CSV of subscribed addresses that Excel can open safely', async ({ page }) => {
    await open(page, '/marketing/newsletter');
    await expect(page.locator('[data-testid="subscriber-1"]')).toBeVisible();

    await page.locator('[data-testid="newsletter-status"]').selectOption('Unsubscribed');
    await page.locator('[data-testid="newsletter-apply"]').click();
    await expect(page.locator('[data-testid="newsletter-paging"]')).toContainText('1–1 of 1');

    const [download] = await Promise.all([
      page.waitForEvent('download'),
      page.locator('[data-testid="newsletter-export"]').click(),
    ]);

    expect(download.suggestedFilename()).toBe('newsletter-subscribers-2026-10-14.csv');

    const stream = await download.createReadStream();
    const chunks: Buffer[] = [];
    for await (const chunk of stream) {
      chunks.push(chunk as Buffer);
    }

    const bytes = Buffer.concat(chunks);
    expect([...bytes.subarray(0, 3)]).toEqual([0xef, 0xbb, 0xbf]);

    const text = bytes.subarray(3).toString('utf8');
    expect(text).toContain('"nimali@example.com"');
    expect(text).toContain('"\'=1+2@example.com"');
    expect(text).toContain('"නිමාලි@example.lk"');
    expect(text).not.toContain('kasun@example.com');

    await expect(page.locator('[data-testid="newsletter-status-message"]')).toContainText(
      'subscribed addresses only',
    );

    // The button kept focus throughout: it was never disabled or removed (#109).
    expect(await page.evaluate(() => document.activeElement?.getAttribute('data-testid'))).toBe('newsletter-export');
  });

  test('refuses a to-date before the from-date and keeps the list', async ({ page }) => {
    await open(page, '/marketing/newsletter');
    await expect(page.locator('[data-testid="subscriber-1"]')).toBeVisible();

    await page.locator('[data-testid="newsletter-from"]').fill('2026-10-02');
    await page.locator('[data-testid="newsletter-to"]').fill('2026-10-01');
    await page.locator('[data-testid="newsletter-apply"]').click();

    await expect(page.locator('[data-testid="newsletter-date-error"]')).toBeVisible();
    await expect(page.locator('[data-testid="subscriber-1"]')).toBeVisible();
  });
});

test.describe('the screens themselves', () => {
  test('the customer list has no sideways scroll and no undersized or buried control', async ({ page }) => {
    await open(page, '/customers', 'many');
    await expect(page.locator('[data-testid="customer-41"]')).toBeVisible();

    await measureAtEveryWidth(page, [
      '[data-testid="customer-search"]',
      '[data-testid="customer-district"]',
      '[data-testid="customer-search-submit"]',
      '[data-testid="customers-prev"]',
      '[data-testid="customers-next"]',
      '[data-testid="open-customer-41"]',
      '[data-testid="open-customer-42"]',
    ]);
  });

  test('the customer detail has no sideways scroll and no undersized or buried control', async ({ page }) => {
    await open(page, '/customers/41');
    await expect(page.locator('[data-testid="customer-order-SD-2026-000123"]')).toBeVisible();

    await measureAtEveryWidth(page, [
      '.customer-back a',
      '[data-testid="open-order-SD-2026-000123"]',
      '[data-testid="customer-orders-prev"]',
      '[data-testid="customer-orders-next"]',
    ]);
  });

  test('the newsletter screen has no sideways scroll and no undersized or buried control', async ({ page }) => {
    await open(page, '/marketing/newsletter');
    await expect(page.locator('[data-testid="subscriber-1"]')).toBeVisible();

    await measureAtEveryWidth(page, [
      '[data-testid="newsletter-search"]',
      '[data-testid="newsletter-status"]',
      '[data-testid="newsletter-from"]',
      '[data-testid="newsletter-to"]',
      '[data-testid="newsletter-apply"]',
      '[data-testid="newsletter-clear"]',
      '[data-testid="newsletter-export"]',
      '[data-testid="newsletter-prev"]',
      '[data-testid="newsletter-next"]',
    ]);
  });

  /** axe does not check heading order strictly, so it is asserted here: the first heading is the only h1. */
  test('every screen passes axe and puts its one h1 first', async ({ page }) => {
    for (const [path, ready] of [
      ['/customers', '[data-testid="customer-41"]'],
      ['/customers/41', '[data-testid="customer-order-SD-2026-000123"]'],
      ['/marketing/newsletter', '[data-testid="subscriber-1"]'],
    ] as const) {
      await open(page, path);
      await expect(page.locator(ready)).toBeVisible();

      const levels = await page.locator('main h1, main h2, main h3').evaluateAll((headings) =>
        headings.map((heading) => heading.tagName),
      );
      expect(levels[0], `first heading on ${path}`).toBe('H1');
      expect(levels.filter((level) => level === 'H1'), `h1 count on ${path}`).toHaveLength(1);

      const results = await new AxeBuilder({ page }).analyze();
      const blocking = results.violations.filter((violation) =>
        ['serious', 'critical'].includes(violation.impact ?? ''),
      );

      expect(
        blocking.map((violation) => `${violation.id}: ${violation.help}`),
        `serious or critical axe findings on ${path}`,
      ).toEqual([]);
    }
  });
});

/**
 * The three questions at the eight project widths: big enough, on screen, not buried. Each clause must
 * match exactly one element first, so a renamed test id cannot make the sweep pass by matching nothing (#80).
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
