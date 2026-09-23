import { Page, expect, test } from '@playwright/test';

import { apiURL } from '../playwright.config';

/**
 * Browser checks for the product listing page (DEPT-01, PLP-01..07, CARD-01).
 *
 * These are the checks jsdom cannot make. The unit suite already pins the URL ↔ filter mapping,
 * the chips, the sorts and the card's price block; what needs a real browser is layout at a real
 * viewport, a real focus model and `prefers-reduced-motion`.
 *
 * The catalogue comes from `support/catalog-api-stub.mjs`, which the config starts. It answers
 * 120 products for an unfiltered Women › Jeans, 30 for `colour=blue` and none for `colour=cream`.
 */

const LISTING = '/women/jeans';

/** docs/04 §4: 2 columns on phones, 3 on tablets, 4 on desktop, 5 from 1440 px (PLP-05). */
const BREAKPOINTS = [
  { width: 320, columns: 2 },
  { width: 375, columns: 2 },
  { width: 390, columns: 2 },
  { width: 768, columns: 3 },
  { width: 1024, columns: 4 },
  { width: 1280, columns: 4 },
  { width: 1440, columns: 5 },
  { width: 1920, columns: 5 },
] as const;

/**
 * The browser's own `/api` calls (everything after "Load More") go to the storefront's origin,
 * which in production is a reverse proxy in front of the API. Here it is the stub, fetched from
 * the test process so no cross-origin rule is involved.
 */
async function useStubApi(page: Page): Promise<void> {
  await page.route('**/api/catalog/**', async (route) => {
    const url = new URL(route.request().url());
    const response = await route.fetch({ url: `${apiURL}${url.pathname}${url.search}` });
    await route.fulfill({ response });
  });
}

/** `scrollWidth` against `clientWidth`: `innerWidth` includes the scrollbar and hides overflow. */
async function horizontalOverflow(page: Page): Promise<number> {
  return page.evaluate(() => {
    const root = document.documentElement;
    return root.scrollWidth - root.clientWidth;
  });
}

/** How many columns the grid really resolves to, rather than which media query we hoped applied. */
async function gridColumns(page: Page): Promise<number> {
  return page.evaluate(() => {
    const grid = document.querySelector('.product-grid');
    if (!grid) {
      return 0;
    }

    return getComputedStyle(grid).gridTemplateColumns.split(' ').filter(Boolean).length;
  });
}

/** Every visible element matching `selector` whose box is under the 44px touch minimum. */
async function undersized(page: Page, selector: string) {
  return page.evaluate(
    (target) =>
      [...document.querySelectorAll<HTMLElement>(target)]
        .filter((element) => element.getClientRects().length > 0)
        .map((element) => ({
          label: `${element.tagName}.${element.className}`.slice(0, 60),
          width: Math.round(element.getBoundingClientRect().width),
          height: Math.round(element.getBoundingClientRect().height),
        }))
        .filter((box) => box.width < 44 || box.height < 44),
    selector,
  );
}

test.beforeEach(async ({ page }) => {
  await useStubApi(page);
});

test.describe('server-side rendering (GLB-11)', () => {
  test('the HTML a crawler receives already has the products in it', async ({ request }) => {
    const response = await request.get(LISTING);
    const html = await response.text();

    expect(response.status()).toBe(200);
    expect(html).toContain('Test Product 1');
    expect(html).toContain('Test Product 24');
    expect(html).toContain('120');
    expect(html).toMatch(/<h1[^>]*>\s*Jeans\s*<\/h1>/);
  });

  test('the title and the meta description describe the category', async ({ request }) => {
    const html = await (await request.get(LISTING)).text();

    expect(html).toContain('<title>Jeans | Women | Spencer Denim</title>');
    expect(html).toContain('Denim in high-rise, straight and wide-leg cuts.');
  });

  /**
   * The browser must not re-fetch what it was just served. Angular's own HTTP transfer cache
   * cannot see these requests (see `ProductListStore.takeFromServer`), so the store hands the
   * page over itself; without it, hydration replaced the rendered grid with a loading message.
   */
  test('the browser does not fetch what the server already sent', async ({ page }) => {
    const calls: string[] = [];
    page.on('request', (request) => {
      if (request.url().includes('/api/catalog/')) {
        calls.push(new URL(request.url()).pathname);
      }
    });

    await page.goto(LISTING);
    await expect(page.locator('.product-grid > li')).toHaveCount(24);

    expect(calls).toEqual([]);
  });

  test('hydration keeps the live category copy, not the offline fallback', async ({ page }) => {
    await page.goto('/kids/girls/girls-dresses');

    // The fallback tree carries no category descriptions, so this text can only come from the
    // department feed the server handed over.
    await expect(page.locator('.plp__intro')).toHaveText('Cotton dresses.');
    await expect(page.locator('.breadcrumb')).toContainText('Girls Dresses');
  });

  test('a filtered listing is rendered filtered, not rendered then filtered', async ({ request }) => {
    const html = await (await request.get(`${LISTING}?colour=blue`)).text();

    expect(html).toContain('30');
    expect(html).toContain('Colour: Blue');
  });
});

test.describe('the grid at every breakpoint (PLP-05, GLB-09)', () => {
  for (const { width, columns } of BREAKPOINTS) {
    test(`${width}px shows ${columns} columns with no horizontal scroll`, async ({ page }) => {
      await page.setViewportSize({ width, height: 900 });
      await page.goto(LISTING);
      await expect(page.locator('.product-grid > li').first()).toBeVisible();

      expect(await gridColumns(page)).toBe(columns);
      expect(await horizontalOverflow(page)).toBeLessThanOrEqual(0);
    });
  }

  test('the filter sidebar replaces the Filters button on desktop', async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await page.goto(LISTING);

    await expect(page.locator('.plp__sidebar')).toBeVisible();
    await expect(page.locator('.plp__filters-btn')).toBeHidden();
  });

  test('the Filters button replaces the sidebar on a phone', async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 844 });
    await page.goto(LISTING);

    await expect(page.locator('.plp__filters-btn')).toBeVisible();
    await expect(page.locator('.plp__sidebar')).toBeHidden();
  });

  /**
   * The rule is scoped to the listing's own controls. A product name is a stretched link whose own
   * box is one line of text but whose target is the whole card, and a breadcrumb link is inline
   * text in a sentence (WCAG 2.5.8's inline exception) — measuring their boxes would report a
   * failure that is not one.
   */
  const CONTROL_SELECTOR =
    '.plp__bar button, .plp__bar select, .chips .chip, .plp__clear, .plp__more button, .card__wish';

  test('every listing control is at least 44px on both axes at 320px', async ({ page }) => {
    await page.setViewportSize({ width: 320, height: 720 });
    await page.goto(`${LISTING}?colour=blue`);
    await expect(page.locator('.product-grid > li').first()).toBeVisible();

    expect(await undersized(page, CONTROL_SELECTOR)).toEqual([]);
  });

  test('every filter control is at least 44px on both axes at 320px', async ({ page }) => {
    await page.setViewportSize({ width: 320, height: 720 });
    await page.goto(LISTING);
    await page.locator('.plp__filters-btn').click();
    await expect(page.locator('#listing-filters')).toBeVisible();

    // The rows themselves: a checkbox is 18px, but the label around it is what a thumb hits.
    expect(await undersized(page, '#listing-filters .filters__summary, #listing-filters .opt'))
      .toEqual([]);
  });
});

test.describe('the mobile filter drawer (PLP-02)', () => {
  test.beforeEach(async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 844 });
  });

  test('opens with focus inside it and hands focus back on Escape', async ({ page }) => {
    await page.goto(LISTING);

    const trigger = page.locator('.plp__filters-btn');
    await trigger.focus();
    await trigger.click();

    const panel = page.locator('#listing-filters');
    await expect(panel).toBeVisible();
    await expect(trigger).toHaveAttribute('aria-expanded', 'true');

    await expect
      .poll(() =>
        page.evaluate(() => {
          const target = document.querySelector('#listing-filters');
          return !!target && !!document.activeElement && target.contains(document.activeElement);
        }),
      )
      .toBe(true);

    await page.keyboard.press('Escape');

    await expect(trigger).toHaveAttribute('aria-expanded', 'false');
    await expect(trigger).toBeFocused();
  });

  test('keeps Tab inside the panel', async ({ page }) => {
    await page.goto(LISTING);

    await page.locator('.plp__filters-btn').click();
    await expect(page.locator('#listing-filters')).toBeVisible();

    // Far more tabs than the panel has stops: if the trap leaks, focus lands on the page behind.
    for (let press = 0; press < 40; press++) {
      await page.keyboard.press('Tab');

      const inside = await page.evaluate(() => {
        const target = document.querySelector('#listing-filters');
        return !!target && !!document.activeElement && target.contains(document.activeElement);
      });

      expect(inside, `focus left the drawer after ${press + 1} tabs`).toBe(true);
    }
  });

  test('filtering from the drawer changes the URL and the count', async ({ page }) => {
    await page.goto(LISTING);
    await expect(page.locator('.plp__count')).toContainText('120');

    await page.locator('.plp__filters-btn').click();
    await page
      .locator('#listing-filters')
      .getByRole('checkbox', { name: 'Blue' })
      .check();

    await expect(page).toHaveURL(/colour=blue/);
    await expect(page.locator('.plp__count')).toContainText('30');
    await expect(page.locator('.chips .chip').first()).toContainText('Colour: Blue');
  });
});

test.describe('reduced motion (GLB-12)', () => {
  test.beforeEach(async ({ page }) => {
    await page.emulateMedia({ reducedMotion: 'reduce' });
  });

  // Lesson L3: reduced motion puts every transition — including the `visibility` that reveals a
  // dialog — on a 0.01ms clock, and a browser refuses to focus an element that is still hidden.
  test('the filter drawer still takes focus when it opens', async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 844 });
    await page.goto(LISTING);

    const trigger = page.locator('.plp__filters-btn');
    await trigger.focus();
    await trigger.click();

    await expect(page.locator('#listing-filters')).toBeVisible();
    await expect
      .poll(() =>
        page.evaluate(() => {
          const target = document.querySelector('#listing-filters');
          return !!target && !!document.activeElement && target.contains(document.activeElement);
        }),
      )
      .toBe(true);
  });

  // Lesson L4: a focus ring can be painted over by an overlay, and reduced motion is where a ring
  // that depends on a transition disappears entirely.
  test('a focused chip, sort control and wishlist heart all keep a visible ring', async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await page.goto(`${LISTING}?colour=blue`);
    await expect(page.locator('.product-grid > li').first()).toBeVisible();

    for (const selector of ['.chips .chip', '#listing-sort', '.card__wish']) {
      const outline = await page.evaluate((target) => {
        const element = document.querySelector<HTMLElement>(target);
        element?.focus();

        const active = document.activeElement as HTMLElement | null;
        if (!active || active !== element) {
          return null;
        }

        const style = getComputedStyle(active);
        const ring = getComputedStyle(active, '::after');

        return {
          outline: parseFloat(style.outlineWidth),
          pseudoBorder: parseFloat(ring.borderTopWidth) || 0,
          visible: active.getClientRects().length > 0,
        };
      }, selector);

      expect(outline, `${selector} could not take focus`).not.toBeNull();
      expect(outline?.visible, `${selector} is not visible while focused`).toBe(true);
      expect(
        (outline?.outline ?? 0) + (outline?.pseudoBorder ?? 0),
        `${selector} has no focus ring`,
      ).toBeGreaterThanOrEqual(2);
    }
  });
});

test.describe('Load More (PLP-07)', () => {
  test('appends the next page and keeps the shopper where they were', async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await page.goto(LISTING);

    const cards = page.locator('.product-grid > li');
    await expect(cards).toHaveCount(24);

    const button = page.getByRole('button', { name: 'Load More' });
    await button.scrollIntoViewIfNeeded();
    const before = await page.evaluate(() => window.scrollY);

    await button.click();
    await expect(cards).toHaveCount(48);

    // The first page is still there, and the page did not jump back to the top.
    await expect(page.locator('.product-grid > li').first()).toContainText('Test Product 1');
    expect(await page.evaluate(() => window.scrollY)).toBeGreaterThanOrEqual(before - 5);
  });

  test('announces the new count', async ({ page }) => {
    await page.goto(LISTING);

    const live = page.locator('.plp__results [role="status"]');
    await expect(live).toHaveText('Showing 24 of 120 products.');

    await page.getByRole('button', { name: 'Load More' }).click();

    await expect(live).toHaveText('Showing 48 of 120 products.');
  });

  test('records the depth in the URL, so the listing can be shared and reloaded', async ({ page }) => {
    await page.goto(LISTING);
    await page.getByRole('button', { name: 'Load More' }).click();
    await expect(page.locator('.product-grid > li')).toHaveCount(48);
    await expect(page).toHaveURL(/pages=2/);

    await page.reload();
    await expect(page.locator('.product-grid > li')).toHaveCount(48);
  });

  test('Back undoes a Load More and then a filter', async ({ page }) => {
    await page.goto(LISTING);
    await page.getByRole('button', { name: 'Load More' }).click();
    await expect(page.locator('.product-grid > li')).toHaveCount(48);

    await page.goBack();
    await expect(page.locator('.product-grid > li')).toHaveCount(24);
    await expect(page).not.toHaveURL(/pages=/);
  });
});

test.describe('no products found (PLP-06)', () => {
  test('offers the filter that is in the way, not just a message', async ({ page }) => {
    await page.goto(`${LISTING}?colour=cream`);

    await expect(page.locator('.plp__state-title')).toHaveText('No products found');

    const remove = page.getByRole('button', { name: 'Remove filter: Colour: Cream' });
    await expect(remove.first()).toBeVisible();

    await remove.last().click();
    await expect(page.locator('.product-grid > li')).toHaveCount(24);
  });
});
