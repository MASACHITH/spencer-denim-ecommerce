import AxeBuilder from '@axe-core/playwright';
import { Page, expect, test } from '@playwright/test';

import { apiURL } from '../playwright.config';
import { horizontalOverflow, undersized } from './support/measure';

/**
 * PDP-08 in a real browser (#189): the rating summary and reviews on the product page, the sign-in prompt, the
 * write-a-review form, and that nothing per-user reaches the server-rendered HTML.
 *
 * **What is real and what is not.** The storefront is the built SSR server; the API is
 * `e2e/support/review-stub.mjs` inside the catalogue stub, which mirrors the review DTOs and refuses what
 * `ReviewRules` refuses. The real routes are pinned against SQL Server by `ReviewSubmissionEndpointTests`
 * (#126: a stub alone cannot prove a screen is alive).
 */

const BREAKPOINTS = [320, 375, 390, 768, 1024, 1280, 1440, 1920] as const;

/** The fixture product with three published reviews (one verified, one with markup, one long word). */
const REVIEWED = '/product/test-product-3';

async function hydrated(page: Page): Promise<void> {
  await page.waitForFunction(() => !!document.querySelector('sd-root')?.hasAttribute('ng-version'));
}

/** Every browser `/api` call to the stub, cookie and all. */
async function proxy(page: Page): Promise<void> {
  await page.route('**/api/**', async (route) => {
    const url = new URL(route.request().url());
    const response = await route.fetch({ url: `${apiURL}${url.pathname}${url.search}` });
    await route.fulfill({ response });
  });
}

/** A signed-in customer of this test's own, as the stub knows them. */
async function signIn(page: Page, options: { approval?: 'on' | 'off'; verified?: number[]; firstName?: string } = {}): Promise<string> {
  const token = `rv-${Math.random().toString(36).slice(2)}`;
  const query = new URLSearchParams({
    token,
    approval: options.approval ?? 'on',
    verified: (options.verified ?? []).join(','),
    firstName: options.firstName ?? 'Nimal',
  });

  expect((await page.request.get(`${apiURL}/__test__/review-customer?${query}`)).ok()).toBe(true);
  await page.context().addCookies([{ name: 'sd_customer_auth', value: token, domain: 'localhost', path: '/' }]);
  return token;
}

const reviews = (page: Page) => page.locator('[data-testid="reviews"]');

test.describe('the reviews everyone sees', () => {
  test('shows the summary, the badge, and markup as text', async ({ page }) => {
    await proxy(page);
    await page.goto(REVIEWED);
    await hydrated(page);

    await expect(page.locator('[data-testid="reviews-count"]')).toHaveText(/Based on 3 reviews/);
    await expect(page.locator('[data-testid="review-301"] [data-testid="review-verified"]')).toHaveText('Verified buyer');
    await expect(page.locator('[data-testid="review-302"] [data-testid="review-verified"]')).toHaveCount(0);

    const markup = page.locator('[data-testid="review-302"]');
    await expect(markup).toContainText('<img src=x onerror="window.__reviewXss=1">');
    await expect(markup).toContainText('<b>Good</b> shirt');
    await expect(markup.locator('img, b')).toHaveCount(0);
    expect(await page.evaluate(() => (window as unknown as { __reviewXss?: number }).__reviewXss)).toBeUndefined();
  });

  test('is in the server-rendered HTML, with nothing about the visitor in it', async ({ page }) => {
    await signIn(page);

    // The cookie travels with this request; the SSR server must not act on it.
    const response = await page.request.get(REVIEWED);
    const html = await response.text();

    expect(html).toContain('Exactly as pictured');
    expect(html).toContain('Verified buyer');
    expect(html).not.toContain('Sign in to write a review');
    expect(html).not.toContain('data-testid="review-form"');
    expect(html).not.toContain('Write a review');
    expect(html).not.toContain('Edit your review');
  });

  test('offers a signed-out shopper a way to sign in that comes back to the product', async ({ page }) => {
    await proxy(page);
    await page.goto(REVIEWED);
    await hydrated(page);

    const link = page.locator('[data-testid="reviews-sign-in"] a');
    await expect(link).toHaveText(/Sign in to write a review/);
    await link.click();
    await expect(page).toHaveURL(/\/account\/sign-in\?returnTo=%2Fproduct%2Ftest-product-3/);
  });

  test('a product with no reviews says so', async ({ page }) => {
    await proxy(page);
    await page.goto('/product/test-product-30');
    await hydrated(page);

    await expect(page.locator('[data-testid="reviews-none"]')).toHaveText('No reviews yet.');
  });
});

test.describe('writing a review', () => {
  test('a signed-in customer writes one and is told it waits for approval', async ({ page }) => {
    await proxy(page);
    await signIn(page);
    await page.goto('/product/test-product-40');
    await hydrated(page);

    await page.locator('[data-testid="reviews-write"]').click();
    await expect(page.locator('#rv-form-title')).toBeFocused();

    await page.locator('[data-testid="review-submit"]').click();
    await expect(page.locator('[data-testid="review-error-rating"]')).toHaveText('Choose a star rating from 1 to 5.');

    await page.locator('label[for="rv-rating-4"]').click();
    await page.locator('[data-testid="review-title"]').fill('Good everyday shirt');
    await page.locator('[data-testid="review-body"]').fill('Breathable.\nSleeves a little long.');
    await page.locator('[data-testid="review-submit"]').click();

    await expect(page.locator('[data-testid="reviews-said"]')).toHaveText(/will appear once we have checked it/);
    await expect(page.locator('[data-testid="reviews-my-status"]')).toHaveText(/waiting to be checked/);
    await expect(page.locator('[data-testid="reviews-none"]')).toBeVisible();

    // Editing brings the saved text back.
    await page.locator('[data-testid="reviews-edit"]').click();
    await expect(page.locator('[data-testid="review-title"]')).toHaveValue('Good everyday shirt');
    await expect(page.locator('#rv-rating-4')).toBeChecked();
  });

  test('a review published at once appears with its badge, and the count moves', async ({ page }) => {
    await proxy(page);
    await signIn(page, { approval: 'off', verified: [41], firstName: 'Kamala' });
    await page.goto('/product/test-product-41');
    await hydrated(page);

    await page.locator('[data-testid="reviews-write"]').click();
    await expect(page.locator('[data-testid="review-form"]')).toContainText('Verified buyer badge');
    await page.locator('label[for="rv-rating-5"]').click();
    await page.locator('[data-testid="review-body"]').fill('<script>alert(1)</script> Lovely.');
    await page.locator('[data-testid="review-submit"]').click();

    await expect(page.locator('[data-testid="reviews-said"]')).toHaveText(/now on this page/);
    await expect(page.locator('[data-testid="reviews-count"]')).toHaveText(/Based on 1 review/);
    const item = page.locator('[data-testid="reviews-list"] li').first();
    await expect(item).toContainText('<script>alert(1)</script> Lovely.');
    await expect(item.locator('[data-testid="review-verified"]')).toBeVisible();
  });

  test('the server refusing a field is shown beside it', async ({ page }) => {
    await proxy(page);
    await signIn(page);
    await page.goto('/product/test-product-42');
    await hydrated(page);

    await page.locator('[data-testid="reviews-write"]').click();
    await page.locator('label[for="rv-rating-3"]').click();
    // A direction override the browser happily types and the server refuses.
    await page.locator('[data-testid="review-body"]').fill('Nice ‮txet');
    await page.locator('[data-testid="review-submit"]').click();

    await expect(page.locator('[data-testid="review-error-body"]')).toHaveText(/hidden control character/);
  });
});

test.describe('layout and accessibility', () => {
  test('the reviews and the open form pass axe', async ({ page }) => {
    await proxy(page);
    await signIn(page);
    await page.goto(REVIEWED);
    await hydrated(page);
    await page.locator('[data-testid="reviews-write"]').click();
    await expect(page.locator('[data-testid="review-form"]')).toBeVisible();

    const results = await new AxeBuilder({ page }).include('[data-testid="reviews"]').analyze();
    expect(results.violations.map((violation) => `${violation.id}: ${violation.help} at ${violation.nodes.map((node) => node.target.join(' ')).join(', ')}`)).toEqual([]);
  });

  test('nothing scrolls sideways at the eight widths, and the stars are big enough to tap', async ({ page }) => {
    await proxy(page);
    await signIn(page);
    await page.goto(REVIEWED);
    await hydrated(page);
    await page.locator('[data-testid="reviews-write"]').click();
    await expect(page.locator('[data-testid="review-form"]')).toBeVisible();

    for (const width of BREAKPOINTS) {
      await page.setViewportSize({ width, height: 900 });
      expect(await horizontalOverflow(page), `horizontal overflow at ${width}px`).toBe(0);
      expect(await undersized(page, 'label[for="rv-rating-5"]'), `star tap target at ${width}px`).toEqual([]);
      expect(await undersized(page, '[data-testid="review-submit"]'), `submit at ${width}px`).toEqual([]);
    }

    await expect(reviews(page)).toBeVisible();
  });
});
