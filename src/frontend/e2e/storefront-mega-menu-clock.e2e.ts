import { Page, expect, test } from '@playwright/test';

import { megaMenuApiURL, megaMenuURL } from '../playwright.config';

/**
 * Tester pass, #215 round 2: the mega menu now re-checks a card's window in the browser, against the browser's own
 * clock. The server picks the card at render time; a browser whose clock is ahead (or a page hydrated after the
 * window closed) picks the fallback. Hydration must survive the disagreement: no NG05xx hydration error in the
 * console, and the menu still opens with exactly one card in it.
 */
test.describe.configure({ mode: 'serial' });
test.use({ baseURL: megaMenuURL });

async function useClosing(page: Page): Promise<void> {
  const switched = await page.request.get(`${megaMenuApiURL}/__test__/mega-menu-cards?state=closing`);
  expect(switched.ok()).toBe(true);

  await page.route('**/api/**', async (route) => {
    const url = new URL(route.request().url());
    const response = await route.fetch({ url: `${megaMenuApiURL}${url.pathname}${url.search}` });
    await route.fulfill({ response });
  });
}

test.afterEach(async ({ page, request }) => {
  await page.unrouteAll({ behavior: 'ignoreErrors' });
  await request.get(`${megaMenuApiURL}/__test__/mega-menu-cards?state=none`);
});

for (const [label, offsetMs] of [
  ['a day ahead', 24 * 60 * 60 * 1000],
  ['a day behind', -24 * 60 * 60 * 1000],
] as const) {
  test(`a browser clock ${label} of the server hydrates without error and draws one card`, async ({ page }) => {
    const errors: string[] = [];
    page.on('console', (message) => {
      if (message.type() === 'error') {
        errors.push(message.text());
      }
    });
    page.on('pageerror', (error) => errors.push(error.message));

    await useClosing(page);
    await page.clock.install({ time: new Date(Date.now() + offsetMs) });
    await page.setViewportSize({ width: 1280, height: 900 });

    const response = await page.goto('/');
    const html = (await response?.text()) ?? '';
    expect(html, 'the server drew the live card').toContain('Closing soon');

    await page.waitForFunction(() => document.querySelector('[ngh]') === null || document.readyState === 'complete');
    await page.waitForTimeout(500);

    const women = page.locator('header').getByRole('link', { name: 'Women', exact: true }).first();
    await women.hover();
    const menu = page.locator('#mega-women');
    await expect(menu).toBeVisible();
    await expect(menu.locator('.mega__promo')).toHaveCount(1);
    await expect(menu.locator('.mega__promo')).toContainText('New in Women');

    expect(errors.filter((text) => /NG0?5\d\d|hydration/i.test(text)), errors.join('\n')).toEqual([]);
  });
}
