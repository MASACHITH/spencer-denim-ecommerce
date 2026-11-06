import AxeBuilder from '@axe-core/playwright';
import { Page, expect, test } from '@playwright/test';

import { apiURL } from '../playwright.config';
import { horizontalOverflow } from './support/measure';

/**
 * The newsletter's unsubscribe link in a real browser (#262, doc 11 P15 step 3): `/unsubscribe?token=...`.
 *
 * `unsubscribe-page.spec.ts` pins the states in jsdom. What needs a browser: the server-rendered page asks for a press
 * and sends nothing by itself (a mail scanner that opens the link must not unsubscribe anyone), the token reaches the
 * API from the link and then leaves the address bar, and the page is `noindex`, readable at every width and clean under
 * axe. The API is `catalog-api-stub.mjs`, which answers 204 for every token as the real endpoint does and records what
 * it was sent; the real behaviour is pinned against SQL Server in `AdminNewsletterEndpointTests`.
 */

const BREAKPOINTS = [320, 375, 390, 768, 1024, 1280, 1440, 1920] as const;

async function useStubApi(page: Page): Promise<void> {
  await page.route('**/api/**', async (route) => {
    const url = new URL(route.request().url());
    const response = await route.fetch({ url: `${apiURL}${url.pathname}${url.search}` });
    await route.fulfill({ response });
  });
}

async function hydrated(page: Page): Promise<void> {
  await page.waitForFunction(() => !!document.querySelector('sd-root')?.hasAttribute('ng-version'));
}

/** The tokens the stub has been sent, filtered to this test's own — the suite is fullyParallel. */
async function sent(page: Page, token: string): Promise<string[]> {
  const response = await page.request.get(`${apiURL}/__test__/newsletter-unsubscribes`);
  return ((await response.json()) as string[]).filter((candidate) => candidate === token);
}

test.beforeEach(async ({ page }) => {
  await useStubApi(page);
});

test('the link asks for one press, sends its token, says it is done and drops the token from the address', async ({
  page,
}) => {
  const token = crypto.randomUUID();

  await page.goto(`/unsubscribe?token=${token}`);
  await hydrated(page);

  await expect(page.getByRole('heading', { level: 1 })).toHaveText('Unsubscribe from our newsletter');
  await expect(page.locator('meta[name="robots"]')).toHaveAttribute('content', /noindex/);
  // Opening the link is not a request to unsubscribe.
  expect(await sent(page, token)).toEqual([]);

  await page.locator('[data-testid="unsubscribe-confirm"]').click();

  await expect(page.getByRole('heading', { level: 1 })).toHaveText('You have been unsubscribed');
  await expect(page.getByRole('heading', { level: 1 })).toBeFocused();
  await expect(page).toHaveURL(/\/unsubscribe$/);
  expect(await sent(page, token)).toEqual([token]);
});

test('a link with no token says so, offers the Contact page and sends nothing', async ({ page }) => {
  await page.goto('/unsubscribe');
  await hydrated(page);

  await expect(page.locator('[data-testid="unsubscribe-missing"]')).toBeVisible();
  await expect(page.locator('[data-testid="unsubscribe-missing"] a')).toHaveAttribute('href', '/contact');
  await expect(page.locator('[data-testid="unsubscribe-confirm"]')).toHaveCount(0);
});

test('the page never scrolls sideways and passes axe, before and after the press', async ({ page }) => {
  await page.goto(`/unsubscribe?token=${crypto.randomUUID()}`);
  await hydrated(page);

  for (const width of BREAKPOINTS) {
    await page.setViewportSize({ width, height: 900 });
    expect(await horizontalOverflow(page), `${width}px before`).toBe(0);
  }
  expect((await new AxeBuilder({ page }).include('sd-unsubscribe-page').analyze()).violations).toEqual([]);

  await page.locator('[data-testid="unsubscribe-confirm"]').click();
  await expect(page.locator('[data-testid="unsubscribe-done"]')).toBeVisible();

  for (const width of BREAKPOINTS) {
    await page.setViewportSize({ width, height: 900 });
    expect(await horizontalOverflow(page), `${width}px after`).toBe(0);
  }
  expect((await new AxeBuilder({ page }).include('sd-unsubscribe-page').analyze()).violations).toEqual([]);
});
