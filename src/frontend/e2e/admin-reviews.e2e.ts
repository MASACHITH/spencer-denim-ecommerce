import AxeBuilder from '@axe-core/playwright';
import { Page, expect, test } from '@playwright/test';

import { adminURL, adminWorktreeRoot } from '../playwright.config';
import { horizontalOverflow, undersized } from './support/measure';

/**
 * ADM-16 in a real browser (#189): the moderation list, approve / hide / show again with a note, a stale
 * decision, what each role is offered, and the review approval switch on the Settings screen.
 *
 * The API is `e2e/support/admin-review-stub.mjs`, which mirrors the DTOs and refuses what the real routes
 * refuse; the real routes, their audit rows and the rating recompute are pinned against SQL Server by
 * `AdminReviewEndpointTests`.
 */

const BREAKPOINTS = [320, 375, 390, 768, 1024, 1280, 1440, 1920] as const;

test.beforeAll(async ({ request }) => {
  const response = await request.get(`${adminURL}/__worktree`);
  expect(response.ok(), `the admin stub at ${adminURL} did not answer /__worktree`).toBe(true);
  const { root } = (await response.json()) as { root: string };
  expect(root.replace(/\\/g, '/').toLowerCase()).toBe(adminWorktreeRoot.replace(/\\/g, '/').toLowerCase());
});

async function open(page: Page, set: 'some' | 'many' | 'none' = 'some', path = '/reviews'): Promise<string> {
  const state = `reviews-${Math.random().toString(36).slice(2)}`;
  await page.context().addCookies([{ name: 'e2e_state', value: state, url: adminURL }]);
  const headers = { Cookie: `e2e_state=${state}` };
  expect((await page.request.get(`${adminURL}/__test__/reviews?set=${set}`, { headers })).ok()).toBe(true);
  await page.goto(`${adminURL}${path}`);
  return state;
}

async function asRole(page: Page, permissions: string[]): Promise<void> {
  await page.route('**/api/admin/auth/whoami', async (route) => {
    const response = await route.fetch();
    const session = await response.json();
    await route.fulfill({ response, json: { ...session, grantsAllPermissions: false, permissions } });
  });
}

const card = (page: Page, id: number) => page.locator(`[data-testid="review-${id}"]`);

async function expectAccessible(page: Page, name: string): Promise<void> {
  const results = await new AxeBuilder({ page }).analyze();
  expect(
    results.violations.map((violation) => `${violation.id} (${violation.impact}): ${violation.help} at ${violation.nodes.map((node) => node.target.join(' ')).join(', ')}`),
    `axe findings, ${name}`,
  ).toEqual([]);
}

test.describe('the moderation list', () => {
  test('shows the queue oldest first, with counts, the badge and markup as text', async ({ page }) => {
    await open(page);

    await expect(page.getByRole('heading', { level: 1, name: 'Reviews' })).toBeVisible();
    await expect(page.locator('[data-testid="reviews-counts"]')).toContainText('3 waiting · 1 shown · 1 hidden');
    await expect(page.locator('[data-testid="reviews-paging"]')).toHaveText('1–3 of 3');

    const ids = await page.locator('li[data-testid^="review-"]').evaluateAll((items) => items.map((item) => item.getAttribute('data-testid')));
    expect(ids).toEqual(['review-1', 'review-2', 'review-5']);

    await expect(page.locator('[data-testid="review-verified-1"]')).toHaveText('Verified buyer');
    await expect(page.locator('[data-testid="review-body-2"]')).toHaveText('<img src=x onerror="window.__adminXss=1"> Runs a size small.');
    await expect(card(page, 2).locator('img, b')).toHaveCount(0);
    expect(await page.evaluate(() => (window as unknown as { __adminXss?: number }).__adminXss)).toBeUndefined();
  });

  test('filters by status, buyer and words', async ({ page }) => {
    await open(page);

    await page.locator('[data-testid="reviews-status"]').selectOption('All');
    await page.locator('[data-testid="reviews-verified"]').selectOption('Verified');
    await page.locator('[data-testid="reviews-apply"]').click();
    await expect(page.locator('[data-testid="reviews-paging"]')).toHaveText('1–1 of 1');
    await expect(card(page, 1)).toBeVisible();

    await page.locator('[data-testid="reviews-clear"]').click();
    await page.locator('[data-testid="reviews-status"]').selectOption('Hidden');
    await page.locator('[data-testid="reviews-search"]').fill('phone');
    await page.locator('[data-testid="reviews-apply"]').click();
    await expect(card(page, 4)).toBeVisible();
    await expect(page.locator('[data-testid="review-moderated-4"]')).toContainText('Personal data');

    await page.locator('[data-testid="reviews-search"]').fill('nothing matches this');
    await page.locator('[data-testid="reviews-apply"]').click();
    await expect(page.locator('[data-testid="reviews-empty"]')).toBeVisible();
  });
});

test.describe('deciding', () => {
  test('approve with a note, then hide, then show again', async ({ page }) => {
    await open(page);

    await page.locator('[data-testid="review-note-1"]').fill('Checked against the order');
    await page.locator('[data-testid="approve-1"]').click();
    await expect(page.locator('[data-testid="review-status-1"]')).toHaveText('Approved');
    await expect(page.locator('[data-testid="review-said-1"]')).toContainText('counts towards the rating');
    await expect(page.locator('[data-testid="review-moderated-1"]')).toContainText('Checked against the order');
    await expect(page.locator('[data-testid="approve-1"]')).toHaveCount(0);

    await page.locator('[data-testid="hide-1"]').click();
    await expect(page.locator('[data-testid="review-status-1"]')).toHaveText('Hidden');
    await expect(page.locator('[data-testid="approve-1"]')).toHaveText('Show again');

    await page.locator('[data-testid="approve-1"]').click();
    await expect(page.locator('[data-testid="review-status-1"]')).toHaveText('Approved');
  });

  test('a review changed since the page loaded is not overwritten, and the card shows the change', async ({ page }) => {
    const state = await open(page);
    await expect(card(page, 2)).toBeVisible();

    const headers = { Cookie: `e2e_state=${state}` };
    expect((await page.request.get(`${adminURL}/__test__/review-edited?id=2&title=Rewritten%20by%20author`, { headers })).ok()).toBe(true);

    await page.locator('[data-testid="approve-2"]').click();
    await expect(page.locator('[data-testid="review-said-2"]')).toContainText('Somebody changed this review');
    await expect(page.locator('[data-testid="review-title-2"]')).toHaveText('Rewritten by author');
    await expect(page.locator('[data-testid="review-status-2"]')).toHaveText('Pending');

    // The fresh version is on the card now, so deciding again works.
    await page.locator('[data-testid="approve-2"]').click();
    await expect(page.locator('[data-testid="review-status-2"]')).toHaveText('Approved');
  });
});

test.describe('what each role is offered', () => {
  test('a view-only role sees the reviews and no actions', async ({ page }) => {
    await asRole(page, ['Reviews.View']);
    await open(page);

    await expect(card(page, 1)).toBeVisible();
    await expect(page.locator('[data-testid="reviews-read-only"]')).toBeVisible();
    await expect(page.locator('[data-testid^="approve-"], [data-testid^="hide-"], [data-testid^="review-note-"]')).toHaveCount(0);
  });

  test('Reviews.Edit hides but does not approve', async ({ page }) => {
    await asRole(page, ['Reviews.View', 'Reviews.Edit']);
    await open(page);

    await expect(page.locator('[data-testid="hide-1"]')).toBeVisible();
    await expect(page.locator('[data-testid="approve-1"]')).toHaveCount(0);
  });

  test('a role without Reviews.View is told, and the dashboard does not link it', async ({ page }) => {
    await asRole(page, ['Orders.View', 'Dashboard.View']);
    await open(page);

    await expect(page.locator('[data-testid="reviews-forbidden"]')).toBeVisible();
    await page.goto(`${adminURL}/`);
    await expect(page.locator('[data-testid="dashboard-reviews-link"]')).toHaveCount(0);
  });
});

test.describe('the approval switch', () => {
  test('is on by default, saves off, and reads back', async ({ page }) => {
    await open(page, 'none', '/settings');

    const box = page.locator('[data-testid="field-requireApproval"]');
    await expect(box).toBeChecked();
    await box.uncheck();
    await page.locator('[data-testid="save-reviews"]').click();
    await expect(page.locator('[data-testid="announcement"]')).toContainText('appear as soon as they are written');

    await page.reload();
    await expect(page.locator('[data-testid="field-requireApproval"]')).not.toBeChecked();
  });
});

test.describe('layout and accessibility', () => {
  test('the list passes axe, and nothing scrolls sideways at the eight widths', async ({ page }) => {
    await open(page);
    await expect(card(page, 1)).toBeVisible();
    await page.locator('[data-testid="approve-5"]').click();
    await expect(page.locator('[data-testid="review-said-5"]')).toBeVisible();

    await expectAccessible(page, 'reviews list');

    for (const width of BREAKPOINTS) {
      await page.setViewportSize({ width, height: 900 });
      expect(await horizontalOverflow(page), `horizontal overflow at ${width}px`).toBe(0);
      expect(await undersized(page, '[data-testid="approve-1"]'), `approve at ${width}px`).toEqual([]);
      expect(await undersized(page, '[data-testid="hide-1"]'), `hide at ${width}px`).toEqual([]);
    }
  });

  test('the settings panel passes axe', async ({ page }) => {
    await open(page, 'none', '/settings');
    await expect(page.locator('[data-testid="reviews-panel"]')).toBeVisible();
    await expectAccessible(page, 'settings with the reviews panel');
  });
});
