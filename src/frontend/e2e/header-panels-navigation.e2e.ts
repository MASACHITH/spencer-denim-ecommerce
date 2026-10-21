import { Page, expect, test } from '@playwright/test';

import { apiURL } from '../playwright.config';

/**
 * #118 tester pass: the header still tidies its panels up when the shopper really goes somewhere —
 * a search run from the panel, the browser's Back button, a link in the mobile drawer — including when
 * that happens before the router's first navigation has ended.
 *
 * The #118 fix closes panels only on a navigation "away" (`previousNavigation !== null`). A navigation the
 * shopper starts *during* start-up supersedes the router's initial one and so becomes the first to
 * succeed: it has no previous navigation either, although it is plainly a navigation away.
 */

async function useStubApi(page: Page): Promise<void> {
  for (const pattern of ['**/api/catalog/**', '**/api/search/**']) {
    await page.route(pattern, async (route) => {
      if (new URL(route.request().url()).pathname === '/api/search/submissions') {
        await route.fulfill({ status: 204 });
        return;
      }
      const url = new URL(route.request().url());
      const response = await route.fetch({ url: `${apiURL}${url.pathname}${url.search}` });
      await route.fulfill({ response });
    });
  }
}

/** Holds every lazy chunk asked for after start-up until `release` is called (the #118 e2e's gate). */
async function holdRouteChunks(page: Page): Promise<() => void> {
  let release!: () => void;
  const gate = new Promise<void>((resolve) => (release = resolve));
  let bootstrapped = false;
  page.on('request', (request) => {
    if (new URL(request.url()).pathname === '/api/cart') {
      bootstrapped = true;
    }
  });
  await page.route(/\.js$/, async (route) => {
    if (bootstrapped) {
      await gate;
    }
    await route.continue();
  });
  return release;
}

const panel = (page: Page) => page.locator('#header-search-panel');
const panelBox = (page: Page) => panel(page).locator('input[role="combobox"]');

test.beforeEach(async ({ page }) => {
  await useStubApi(page);
});

test.describe('header panels close on a real navigation away (#118 tester)', () => {
  test('a search run from the panel closes it on the results page (768 px)', async ({ page }) => {
    await page.setViewportSize({ width: 768, height: 900 });
    await page.goto('/');
    await page.waitForLoadState('networkidle');

    await page.getByTestId('search-toggle').click();
    await expect(panel(page)).toBeVisible();
    await panelBox(page).fill('jeans');
    await panelBox(page).press('Enter');

    await expect(page).toHaveURL(/\/search\?q=jeans/);
    await expect(panel(page)).toBeHidden();
  });

  test('the Back button closes an open search panel (768 px)', async ({ page }) => {
    await page.setViewportSize({ width: 768, height: 900 });
    await page.goto('/');
    await page.waitForLoadState('networkidle');

    await page.getByTestId('search-toggle').click();
    await panelBox(page).fill('jeans');
    await panelBox(page).press('Enter');
    await expect(page).toHaveURL(/\/search\?q=jeans/);

    await page.getByTestId('search-toggle').click();
    await expect(panel(page)).toBeVisible();

    await page.goBack();
    await expect(page).toHaveURL(/\/$/);
    await expect(panel(page)).toBeHidden();
  });

  test('the Back button closes the open mobile drawer (375 px)', async ({ page }) => {
    await page.setViewportSize({ width: 375, height: 800 });
    await page.goto('/');
    await page.waitForLoadState('networkidle');
    await page.goto('/search?q=jeans');
    await page.waitForLoadState('networkidle');

    // A client-side navigation first, so Back is handled by the router and not by a full reload.
    await page.getByTestId('search-toggle').click();
    await panelBox(page).fill('linen');
    await panelBox(page).press('Enter');
    await expect(page).toHaveURL(/q=linen/);

    await page.getByTestId('menu-toggle').click();
    await expect(page.locator('#storefront-menu')).toBeVisible();

    await page.goBack();
    await expect(page).toHaveURL(/q=jeans/);
    await expect(page.locator('#storefront-menu')).toBeHidden();
  });

  for (const width of [375, 768] as const) {
    test(`a search run from the panel during start-up still closes it on the results page (${width} px)`, async ({
      page,
    }) => {
      // KNOWN DEFECT (tester, 2026-10-21): fails on feature/78-118-search, passes on origin/main. The search
      // is the first navigation to succeed, so `isNavigationAway` says "not away" and the panel stays open
      // over the results page. Remove `test.fail()` once the panel is closed by the search itself.
      test.fail();
      const release = await holdRouteChunks(page);
      await page.setViewportSize({ width, height: 900 });
      await page.goto('/');

      await page.getByTestId('search-toggle').click();
      await expect(panel(page)).toBeVisible();
      await panelBox(page).click();
      await panelBox(page).pressSequentially('jeans', { delay: 20 });
      await panelBox(page).press('Enter');

      release();

      await expect(page).toHaveURL(/\/search\?q=jeans/);
      await expect(page.locator('h1')).toBeVisible();
      await expect(panel(page)).toBeHidden();
    });
  }
});
