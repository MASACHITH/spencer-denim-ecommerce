import { Page, expect, test } from '@playwright/test';

import { apiURL } from '../playwright.config';

/**
 * Where a navigation leaves the page (the router's scroll handling, `app.config.ts`).
 *
 * The bug this exists for: the router had no scroll handling at all, so a shopper who clicked "About us" or
 * "Returns and exchanges" in the footer arrived on the new page still scrolled to the bottom of the old one,
 * looking at a footer under an empty-looking page. A real browser is needed because the whole question is a
 * scroll offset, which jsdom does not have.
 *
 * Two halves, and each is checked against the thing that would break it:
 *
 *  - **A new page starts at the top** (a footer link at two widths), Back returns to where the shopper was, and
 *    the "Skip to content" link, a plain `#main` anchor, still goes to the content instead of the top.
 *  - **A change of state on the page the shopper is already on does not move them**: a filter, the sort, Load
 *    More, and choosing a size. Each of those is a router navigation, so each would otherwise scroll to the top.
 *    A different department is a different page and does go to the top.
 *
 * The state changes are made with a DOM `click()` and not Playwright's own, because Playwright scrolls the
 * target into view before it acts, which would move the page itself and let a test pass for the wrong reason.
 */

const LISTING = '/women/jeans';
const PRODUCT = '/product/test-product-1';

/**
 * The footer links that exist on the main stub. About us, Delivery and shipping, Returns and exchanges and
 * Contact us are published pages, which only the content-pages server pair publishes, and its switch has one
 * writer: they are covered in `content-pages.e2e.ts` ("a footer link opens the page at the top").
 */
const FOOTER_LINKS = ['Women', 'Men', 'Kids', 'New Arrivals', 'Sale', 'Track your order'] as const;

const VIEWPORTS = [
  { name: '390 px', width: 390, height: 844 },
  { name: '1280 px', width: 1280, height: 800 },
] as const;

/**
 * How long a freshly hydrated page is given to become stable. **Measured, and it matters:** the router does not
 * emit a scroll while the application is still hydrating (`RouterScroller.scheduleScrollEvent` returns early
 * until `ApplicationRef.whenStable()` has resolved once), so a click in the first moments after load is not
 * scrolled at all. At 390 px, from 8,000 px down the home page, a footer click with no wait left the shopper at
 * 930 px, and the same click after 2 s, 5 s, 10 s and 20 s all landed at 0. A test that clicked inside that
 * window would fail for a reason that is not the fix, and, worse, a test that asserts "did NOT jump to the top"
 * would pass inside it whether or not the opt-outs exist. So every test settles first.
 */
const SETTLE_MS = 3000;

/**
 * Waits until the page has hydrated, then until it has settled. A click on server-rendered markup has no handler
 * yet and is replayed rather than handled; `ng-version` is the gate `home-page.e2e.ts` uses. See `SETTLE_MS` for
 * the second wait.
 */
async function hydrated(page: Page): Promise<void> {
  await expect
    .poll(() => page.evaluate(() => window.getComputedStyle(document.body).visibility))
    .toBe('visible');
  await page.waitForFunction(() => {
    const root = document.querySelector('sd-root');
    return !!root && root.hasAttribute('ng-version');
  });
  await page.waitForLoadState('networkidle');
  await page.waitForTimeout(SETTLE_MS);
}

/**
 * The browser's own `/api` calls (a filter, the sort, "Load More") go to the storefront's origin, which in
 * production is a reverse proxy in front of the API. Here it is the stub, fetched from the test process so no
 * cross-origin rule is involved. The same helper `product-listing.e2e.ts` uses.
 */
test.beforeEach(async ({ page }) => {
  await page.route('**/api/catalog/**', async (route) => {
    const url = new URL(route.request().url());
    const response = await route.fetch({ url: `${apiURL}${url.pathname}${url.search}` });
    await route.fulfill({ response });
  });
});

const scrollY = (page: Page): Promise<number> => page.evaluate(() => Math.round(window.scrollY));

async function scrollTo(page: Page, y: number): Promise<void> {
  await page.evaluate((target) => window.scrollTo(0, target), y);
  await expect.poll(() => scrollY(page)).toBeGreaterThanOrEqual(y - 2);
}

async function scrollToEnd(page: Page): Promise<number> {
  await page.evaluate(() => window.scrollTo(0, document.documentElement.scrollHeight));
  await expect.poll(() => scrollY(page)).toBeGreaterThan(300);
  return scrollY(page);
}

for (const viewport of VIEWPORTS) {
  test.describe(`a footer link opens its page at the top, at ${viewport.name}`, () => {
    test.use({ viewport: { width: viewport.width, height: viewport.height } });

    for (const label of FOOTER_LINKS) {
      test(label, async ({ page }) => {
        await page.goto('/');
        await hydrated(page);

        const scrolledTo = await scrollToEnd(page);
        expect(scrolledTo, 'the home page must be long enough to scroll, or this proves nothing').toBeGreaterThan(300);
        const from = page.url();

        await page
          .getByRole('contentinfo')
          .getByRole('link', { name: label, exact: true })
          .first()
          .click();

        await expect(page).not.toHaveURL(from);
        await expect
          .poll(() => scrollY(page), { message: `"${label}" should open scrolled to the top` })
          .toBe(0);
        await expect(page.getByRole('heading', { level: 1 }).first()).toBeInViewport();
      });
    }
  });
}

test.describe('Back and Forward', () => {
  test.use({ viewport: { width: 1280, height: 800 } });

  test('Back returns the shopper to where they left the page', async ({ page }) => {
    await page.goto('/');
    await hydrated(page);
    const before = await scrollToEnd(page);

    await page.getByRole('contentinfo').getByRole('link', { name: 'Sale', exact: true }).first().click();
    await expect.poll(() => scrollY(page)).toBe(0);

    await page.goBack();

    // A little tolerance: the server's page and the hydrated one can differ by a few pixels of layout.
    await expect
      .poll(() => scrollY(page), { message: 'Back should restore the earlier position' })
      .toBeGreaterThan(before - 60);
  });
});

test.describe('the "Skip to content" link', () => {
  test.use({ viewport: { width: 1280, height: 800 } });

  /**
   * It is a plain `#main` anchor, and it must still work with the router's scroll handling on. **This test cannot
   * tell "scrolled to the anchor" from "scrolled to the top", and says so rather than pretending to:** the header
   * keeps `scroll-padding-top` equal to its own height (`site-header.ts`, `trackHeaderHeight`), so scrolling
   * `main` into view lands at 0, exactly where a scroll to the top lands. What it can check, and what the skip
   * link exists for, is that the page leaves where the shopper was, that `main` is on screen under the header,
   * and that keyboard focus moves into `main`, so the next Tab starts in the content and not in the header.
   */
  test('takes the shopper to the content and moves focus into it', async ({ page }) => {
    await page.goto(LISTING);
    await hydrated(page);
    await scrollTo(page, 400);

    await page.locator('.skip-link').focus();
    await page.keyboard.press('Enter');

    await expect(page).toHaveURL(/#main$/);
    await expect(page.locator('main#main')).toBeInViewport();
    await expect.poll(() => page.evaluate(() => document.activeElement?.id)).toBe('main');
    expect(await scrollY(page), 'the skip link left the shopper where they were').toBeLessThan(400);
  });
});

test.describe('a change of state does not move the shopper', () => {
  test.use({ viewport: { width: 1280, height: 800 } });

  test('ticking a filter keeps the position', async ({ page }) => {
    await page.goto(LISTING);
    await hydrated(page);
    await expect(page.locator('.plp__count')).toContainText('120');
    await scrollTo(page, 900);

    await page
      .locator('.plp__sidebar')
      .getByRole('checkbox', { name: 'Blue' })
      .evaluate((box: HTMLInputElement) => box.click());

    await expect(page).toHaveURL(/colour=blue/);
    await expect(page.locator('.plp__count')).toContainText('30');
    expect(await scrollY(page), 'a filter threw the shopper to the top').toBeGreaterThan(700);
  });

  test('changing the sort keeps the position', async ({ page }) => {
    await page.goto(LISTING);
    await hydrated(page);
    await scrollTo(page, 900);

    await page.locator('#listing-sort').evaluate((select: HTMLSelectElement) => {
      const other = [...select.options].map((option) => option.value).find((value) => value !== select.value);
      select.value = other ?? select.value;
      select.dispatchEvent(new Event('change', { bubbles: true }));
    });

    await expect(page).toHaveURL(/sort=/);
    expect(await scrollY(page), 'the sort threw the shopper to the top').toBeGreaterThan(700);
  });

  test('Load More appends and keeps the position', async ({ page }) => {
    await page.goto(LISTING);
    await hydrated(page);

    const cards = page.locator('.product-grid > li');
    const first = await cards.count();
    const before = await scrollToEnd(page);

    await page.getByRole('button', { name: 'Load More' }).evaluate((button: HTMLElement) => button.click());

    await expect.poll(() => cards.count()).toBeGreaterThan(first);
    expect(await scrollY(page), 'Load More threw the shopper to the top').toBeGreaterThanOrEqual(before - 30);
  });

  /** The counterpart: a different department is a different page, and starts at its top like any link. */
  test('choosing another department goes to the top of that page', async ({ page }) => {
    await page.goto(LISTING);
    await hydrated(page);
    await scrollTo(page, 900);

    await page
      .locator('.plp__sidebar')
      .getByRole('radio', { name: 'Men', exact: true })
      .evaluate((radio: HTMLInputElement) => radio.click());

    await expect(page).toHaveURL(/\/men/);
    await expect.poll(() => scrollY(page), { message: 'a new department should open at its top' }).toBe(0);
  });
});

test.describe('on the product page', () => {
  test.use({ viewport: { width: 390, height: 844 } });

  test('choosing a size does not carry the shopper back to the top', async ({ page }) => {
    await page.goto(PRODUCT);
    await hydrated(page);
    await scrollTo(page, 500);

    const size = page.locator('.sd-size:not([disabled])').first();
    await size.evaluate((button: HTMLElement) => button.click());

    await expect(size).toHaveAttribute('aria-pressed', 'true');
    await expect(page).toHaveURL(/size=/);
    expect(await scrollY(page), 'choosing a size threw the shopper to the top').toBeGreaterThan(400);
  });
});
