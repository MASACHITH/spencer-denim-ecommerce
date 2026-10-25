import { Locator, Page, expect, test } from '@playwright/test';

import { apiURL } from '../playwright.config';

/**
 * Where a navigation leaves the page (`core/platform/navigation-scroll.ts`, and the skip link in `app.ts`).
 *
 * The bug this exists for: the router had no scroll handling at all, so a shopper who clicked "About us" or
 * "Returns and exchanges" in the footer arrived on the new page still scrolled to the bottom of the old one,
 * looking at a footer under an empty-looking page. A real browser is needed because the whole question is a
 * scroll offset, which jsdom does not have.
 *
 * The rule, and each half of it is checked against the thing that would break it:
 *
 *  - **A link that opens a different page opens it at the top**, whichever link it is, at 390 and 1280 px, and
 *    **with no wait after the page loads** (the only gate is Angular's own `ng-version`, so a click in the first
 *    moments is what is measured), also on a slow 3G connection with a 4x slower CPU.
 *  - **Nothing else moves the page.** A filter, the sort, a removed chip, Load More, a colour or a size changes only
 *    the query string and keeps the shopper where they are. Back, Forward and a reload are the browser's, as
 *    they were: Back returns to the earlier position, Forward to a page that was left at its top, and a reload
 *    comes back at the same offset.
 *  - **The skip link stays on the page.** `index.html` has `<base href="/">`, so a plain `href="#main"` was a
 *    full load of the home page from any other page.
 *
 * Every link test sets a marker on `window` before the click and checks it afterwards: a link that fell back to a
 * real page load would also "land at the top", and would pass for the wrong reason.
 *
 * The state changes are made with a DOM `click()` and not Playwright's own, because Playwright scrolls the target
 * into view before it acts, which would move the page itself and let a test pass for the wrong reason.
 */

const LISTING = '/women/jeans';
const PRODUCT = '/product/test-product-1';

const VIEWPORTS = [
  { name: '390 px', width: 390, height: 844 },
  { name: '1280 px', width: 1280, height: 800 },
] as const;

/**
 * The footer links that exist on the main stub. About us, Delivery and shipping, Returns and exchanges and
 * Contact us are published pages, which only the content-pages server pair publishes, and its switch has one
 * writer: they are covered in `content-pages.e2e.ts` ("a footer link opens the page at the top").
 */
const FOOTER_LINKS = ['Women', 'Men', 'Kids', 'New Arrivals', 'Sale', 'Track your order'] as const;

/**
 * The browser's own `/api` calls (a filter, the sort, "Load More", a suggestion) go to the storefront's origin,
 * which in production is a reverse proxy in front of the API. Here it is the stub, fetched from the test process so
 * no cross-origin rule is involved. The same helper `product-listing.e2e.ts` uses.
 */
test.beforeEach(async ({ page }) => {
  await page.route('**/api/catalog/**', async (route) => {
    const url = new URL(route.request().url());
    const response = await route.fetch({ url: `${apiURL}${url.pathname}${url.search}` });
    await route.fulfill({ response });
  });
});

/**
 * The page's own bootstrap is the only gate: `ng-version` is stamped on the root element when Angular starts, which
 * is the moment the page begins to handle its own events. **There is deliberately no wait after it**, because the
 * first version of this fix scrolled nothing for the first two seconds after a load (the router skips scrolling
 * while the freshly hydrated app is not yet stable) and a test that waited would never have found that.
 */
async function loaded(page: Page, timeout = 30_000): Promise<void> {
  await page.waitForFunction(() => document.querySelector('sd-root')?.hasAttribute('ng-version'), undefined, {
    timeout,
  });
}

/**
 * For a link that belongs to the page's own content (a breadcrumb, a product card, a home section, the quick view)
 * and not to the shell. **Not a wait for the router to settle**: the fix has none, and the shell's links are
 * measured with none. It is the readiness of the link itself. The route's component is lazy, and on a server-rendered
 * page it attaches its listeners when the load's own navigation ends, which is after the start-up requests have
 * answered; before that a click on its links is a plain link, which loads a fresh page (that also lands at the top,
 * and is not the router's doing). The marker the link tests set reports that, so they wait for it to be over.
 */
async function contentLoaded(page: Page, timeout = 30_000): Promise<void> {
  await loaded(page, timeout);
  await page.waitForLoadState('networkidle', { timeout });
}

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

/** A value that a full page load would lose. */
async function mark(page: Page): Promise<void> {
  await page.evaluate(() => {
    (window as unknown as { __kept: boolean }).__kept = true;
  });
}

async function wasKept(page: Page): Promise<boolean> {
  return page.evaluate(() => (window as unknown as { __kept?: boolean }).__kept === true);
}

/**
 * A DOM click for a poll that repeats it until the page has answered. **Not `count()` and then `evaluate()`:** the
 * element can be gone between the two (a removed chip is removed by the very click that worked), and `evaluate`
 * then waits for an element that never comes back, and the poll, which cannot interrupt its callback, runs out of
 * time holding the last URL it saw. That was the occasional flaky run of the chip test (about one in ten to
 * twenty), in which the page had in fact moved on. An element that is not there is simply not clicked.
 */
async function clickIfPresent(target: Locator): Promise<void> {
  await target.evaluate((element: HTMLElement) => element.click(), undefined, { timeout: 1_000 }).catch(() => undefined);
}

/**
 * Runs `click` from a page that is scrolled down, and checks the page it opens starts at the top, is a client-side
 * navigation (the marker survives), and has its heading on screen.
 */
async function opensAtTop(
  page: Page,
  label: string,
  click: () => Promise<void>,
  timeout = 5_000,
): Promise<void> {
  const from = page.url();
  await mark(page);

  await click();

  await expect(page, `"${label}" should go to another page`).not.toHaveURL(from, { timeout });
  await expect
    .poll(() => scrollY(page), { message: `"${label}" should open scrolled to the top`, timeout })
    .toBe(0);
  expect(await wasKept(page), `"${label}" did a full page load, not a client-side navigation`).toBe(true);

  // The page is there and its top is on screen. Not "the heading is on screen": at 390 px a department's hero or a
  // product's gallery is taller than the phone, so the heading is rightly below the fold at scroll offset 0.
  await expect(page.getByRole('heading', { level: 1 }).first()).toBeAttached({ timeout });
  await expect(page.locator('main#main')).toBeInViewport({ timeout });
}

const header = (page: Page): Locator => page.locator('header').first();

/**
 * Applies a change of state and waits for the page to answer it, applying it again until it does.
 *
 * Why it is a loop: the route's component is lazy, and on a server-rendered page the load's own navigation ends
 * only after the start-up requests have answered, so for a moment after `ng-version` the page's listeners are not
 * attached and a change dispatched in that moment is lost (a dispatched `change` event is not replayed). The
 * changes passed in are idempotent (set a value, never toggle), so applying one twice is the same as once.
 */
async function applyUntil(page: Page, apply: () => Promise<void>, answered: RegExp): Promise<void> {
  await expect
    .poll(
      async () => {
        await apply();
        return page.url();
      },
      { timeout: 15_000 },
    )
    .toMatch(answered);
}

/** Sets a select to a value it does not hold now, and returns that value; setting it again changes nothing. */
async function otherOption(select: Locator): Promise<string> {
  return select.evaluate((element: HTMLSelectElement) => {
    const other = [...element.options].map((option) => option.value).find((value) => value !== element.value);
    return other ?? element.value;
  });
}

async function chooseOption(select: Locator, value: string): Promise<void> {
  await select.evaluate((element: HTMLSelectElement, chosen: string) => {
    element.value = chosen;
    element.dispatchEvent(new Event('change', { bubbles: true }));
  }, value);
}

/** Ticks a checkbox that is not ticked; a tick that is already there is left alone. */
async function tick(box: Locator): Promise<void> {
  await box.evaluate((element: HTMLInputElement) => {
    if (!element.checked) {
      element.click();
    }
  });
}

/** Opens the phone's menu drawer, or does nothing at a width that shows the navigation in the header. */
async function openMenuIfPhone(page: Page): Promise<void> {
  const toggle = page.locator('[data-testid="menu-toggle"]');
  if (await toggle.isVisible()) {
    await toggle.click();
    await expect(page.locator('#storefront-menu')).toBeVisible();
  }
}

/** The link to a department, wherever this width keeps it: the header, or the phone's drawer. */
function departmentLink(page: Page, name: string): Locator {
  return page.locator('header, #storefront-menu').getByRole('link', { name, exact: true }).first();
}

for (const viewport of VIEWPORTS) {
  test.describe(`a link opens its page at the top, at ${viewport.name}, with no wait after load`, () => {
    test.use({ viewport: { width: viewport.width, height: viewport.height } });

    for (const label of FOOTER_LINKS) {
      test(`footer: ${label}`, async ({ page }) => {
        await page.goto('/');
        await loaded(page);
        expect(await scrollToEnd(page), 'the home page must be long enough to scroll').toBeGreaterThan(300);

        await opensAtTop(page, label, () =>
          page.getByRole('contentinfo').getByRole('link', { name: label, exact: true }).first().click(),
        );
      });
    }

    // On the phone, Men and Kids in the drawer are accordions that expand to their categories, not links; the plain
    // links are New Arrivals and Sale. The categories inside them are links, and are the mega menu's at 1280 px.
    const headerLabels = viewport.width < 768 ? ['New Arrivals', 'Sale'] : ['Men', 'Kids', 'New Arrivals', 'Sale'];

    for (const label of headerLabels) {
      test(`header: ${label}`, async ({ page }) => {
        await page.goto('/');
        await loaded(page);
        await scrollTo(page, 900);

        await opensAtTop(page, label, async () => {
          await openMenuIfPhone(page);
          await departmentLink(page, label).click();
        });
      });
    }

    test('header: the wordmark, from a listing', async ({ page }) => {
      await page.goto(LISTING);
      await loaded(page);
      await scrollTo(page, 900);

      await opensAtTop(page, 'wordmark', () => page.locator('header a[href="/"]').first().click());
    });

    test('breadcrumb: Women, from a category', async ({ page }) => {
      await page.goto(LISTING);
      await contentLoaded(page);
      await scrollTo(page, 600);

      await opensAtTop(page, 'breadcrumb Women', () =>
        page
          .getByRole('navigation', { name: /breadcrumb/i })
          .getByRole('link', { name: 'Women', exact: true })
          .evaluate((link: HTMLElement) => link.click()),
      );
    });

    test('product card: the product name', async ({ page }) => {
      await page.goto(LISTING);
      await contentLoaded(page);
      await scrollTo(page, 600);

      await opensAtTop(page, 'product card', () => page.locator('.card__name a').first().click());
      await expect(page).toHaveURL(/\/product\//);
    });

    test('quick view: "full details"', async ({ page }) => {
      await page.goto(LISTING);
      await contentLoaded(page);
      await scrollTo(page, 600);

      const dialog = page.locator('#quick-view');
      const trigger = page.locator('[data-testid="card-quick-view"]').first();
      await expect
        .poll(
          async () => {
            if (!(await dialog.isVisible())) {
              await trigger.click();
            }
            return dialog.isVisible();
          },
          { timeout: 15_000 },
        )
        .toBe(true);

      await opensAtTop(page, 'quick view full details', () => dialog.locator('.qv__full').click());
      await expect(page).toHaveURL(/\/product\//);
    });

    test('a home section: the first link in a section', async ({ page }) => {
      await page.goto('/');
      await contentLoaded(page);
      await scrollTo(page, 900);

      const link = page
        .locator('sd-department-tiles a, sd-feature-banner a, sd-product-row a')
        .filter({ visible: true })
        .first();

      await opensAtTop(page, 'home section link', () => link.evaluate((anchor: HTMLElement) => anchor.click()));
    });

    test('a search suggestion in the header', async ({ page }) => {
      await page.goto('/');
      await loaded(page);
      await scrollTo(page, 900);

      const toggle = page.locator('[data-testid="search-toggle"]');
      if (await toggle.isVisible()) {
        await toggle.click();
      }

      // The header has three comboboxes (the delivery country, the district, the search); this is the search.
      const box = header(page).locator('[role="combobox"][id^="sd-search"]').filter({ visible: true }).first();
      await box.click();
      await box.pressSequentially('jeans', { delay: 20 });
      // The suggestions of the search box, and not the options of the country selector beside it.
      const option = header(page).locator('sd-header-search .suggestions [role="option"]').first();
      await expect(option).toBeVisible();

      await opensAtTop(page, 'search suggestion', () => option.click());
    });
  });
}

test.describe('a link opens its page at the top, on slow 3G with a 4x slower CPU', () => {
  test.use({ viewport: { width: 390, height: 844 } });
  test.setTimeout(300_000);

  /** The next page's code downloads at 400 kbit/s with a 2 s round trip: a navigation takes a good while. */
  const SLOW_NAVIGATION_MS = 120_000;

  /**
   * The condition under which the first version of this fix failed everywhere: a slow connection and a slow CPU
   * stretch the time between the page bootstrapping and the app being stable, so a router that waits for stability
   * waits a long time. Chrome's DevTools "Slow 3G" figures: 400 kbit/s each way and a 2 s round trip.
   */
  async function throttle(page: Page): Promise<void> {
    const cdp = await page.context().newCDPSession(page);
    await cdp.send('Network.enable');
    await cdp.send('Network.emulateNetworkConditions', {
      offline: false,
      latency: 2000,
      downloadThroughput: (400 * 1024) / 8,
      uploadThroughput: (400 * 1024) / 8,
    });
    await cdp.send('Emulation.setCPUThrottlingRate', { rate: 4 });
  }

  for (const label of ['Women', 'Sale', 'Track your order']) {
    test(`footer: ${label}`, async ({ page }) => {
      await throttle(page);
      await page.goto('/', { timeout: 240_000 });
      await loaded(page, 240_000);
      await scrollToEnd(page);

      await opensAtTop(page, label, () =>
        page.getByRole('contentinfo').getByRole('link', { name: label, exact: true }).first().click(),
        SLOW_NAVIGATION_MS,
      );
    });
  }

  test('product card', async ({ page }) => {
    await throttle(page);
    await page.goto(LISTING, { timeout: 240_000 });
    await contentLoaded(page, 240_000);
    // Not 600: with the images not yet downloaded the listing is only just taller than a phone.
    await scrollTo(page, 300);

    await opensAtTop(page, 'product card', () => page.locator('.card__name a').first().click(), SLOW_NAVIGATION_MS);
  });
});

test.describe('Back, Forward and a reload are the browser\'s', () => {
  for (const viewport of VIEWPORTS) {
    test(`Back returns to where the shopper was and Forward goes to the top, at ${viewport.name}`, async ({
      page,
    }) => {
      await page.setViewportSize({ width: viewport.width, height: viewport.height });
      await page.goto('/');
      await loaded(page);
      const before = await scrollToEnd(page);

      await page.getByRole('contentinfo').getByRole('link', { name: 'Sale', exact: true }).first().click();
      await expect.poll(() => scrollY(page)).toBe(0);
      await expect(page).toHaveURL(/\/sale/);

      await page.goBack();
      await expect(page).toHaveURL(/\/$/);
      // A little tolerance: the server's page and the hydrated one can differ by a few pixels of layout.
      await expect
        .poll(() => scrollY(page), { message: 'Back should restore the earlier position' })
        .toBeGreaterThan(before - 60);

      await page.goForward();
      await expect(page).toHaveURL(/\/sale/);
      await expect.poll(() => scrollY(page), { message: 'Forward should open the Sale page at its top' }).toBe(0);
    });

    test(`a reload comes back at the same offset, at ${viewport.name}`, async ({ page }) => {
      await page.setViewportSize({ width: viewport.width, height: viewport.height });
      await page.goto('/');
      await loaded(page);
      const before = await scrollToEnd(page);

      await page.reload();
      await loaded(page);

      await expect
        .poll(() => scrollY(page), { message: 'a reload at the bottom should come back at the bottom' })
        .toBeGreaterThan(before - 5);
    });
  }

  test('a reload of a listing three pages deep comes back at the same offset', async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 800 });
    await page.goto(`${LISTING}?pages=3`);
    await loaded(page);
    await scrollTo(page, 2500);

    await page.reload();
    await loaded(page);

    await expect.poll(() => scrollY(page)).toBeGreaterThan(2490);
  });

  test('a footer click, a reload and then Back still returns to the page above', async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 844 });
    await page.goto('/');
    await loaded(page);
    await scrollToEnd(page);

    await page.getByRole('contentinfo').getByRole('link', { name: 'Sale', exact: true }).first().click();
    await expect.poll(() => scrollY(page)).toBe(0);
    await page.reload();
    await loaded(page);

    await page.goBack();
    await expect(page).toHaveURL(/\/$/);
    await expect.poll(() => scrollY(page), { message: 'Back after a reload should restore a position' }).toBeGreaterThan(0);
  });

  test('a direct visit with a #fragment scrolls to it, under the header', async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 800 });
    await page.goto('/#newsletter-title');
    await loaded(page);

    const target = page.locator('#newsletter-title');
    await expect.poll(() => scrollY(page)).toBeGreaterThan(300);
    await expect(target).toBeInViewport();
    const gap = await page.evaluate(() => {
      const bottom = document.querySelector('header')?.getBoundingClientRect().bottom ?? 0;
      return Math.round((document.querySelector('#newsletter-title')?.getBoundingClientRect().top ?? 0) - bottom);
    });
    expect(gap, 'the fragment target is hidden behind the header').toBeGreaterThanOrEqual(-1);
  });
});

test.describe('the "Skip to content" link keeps the shopper on the page', () => {
  const PAGES = [
    { name: 'a listing', path: LISTING },
    { name: 'a product page', path: PRODUCT },
    { name: 'the search results', path: '/search?q=test' },
    { name: 'the home page', path: '/' },
  ] as const;

  for (const viewport of VIEWPORTS) {
    for (const entry of PAGES) {
      test(`${entry.name}, at ${viewport.name}`, async ({ page }) => {
        await page.setViewportSize({ width: viewport.width, height: viewport.height });
        await page.goto(entry.path);
        await loaded(page);
        await scrollTo(page, 500);
        await mark(page);
        const from = page.url();

        await page.locator('.skip-link').focus();
        await page.keyboard.press('Enter');

        expect(await wasKept(page), 'the skip link did a full page load').toBe(true);
        expect(page.url(), 'the skip link left the page').toBe(from);
        await expect.poll(() => page.evaluate(() => document.activeElement?.id)).toBe('main');

        const { mainTop, headerBottom } = await page.evaluate(() => ({
          mainTop: Math.round(document.querySelector('main#main')?.getBoundingClientRect().top ?? -1),
          headerBottom: Math.round(document.querySelector('header')?.getBoundingClientRect().bottom ?? 0),
        }));

        expect(mainTop, 'the first of the content is hidden behind the header').toBeGreaterThanOrEqual(headerBottom - 1);
        expect(await scrollY(page), 'the skip link did not leave the place it was pressed at').toBeLessThan(500);
      });
    }
  }
});

test.describe('a change of state does not move the shopper', () => {
  test.use({ viewport: { width: 1280, height: 800 } });

  test('ticking a filter keeps the position', async ({ page }) => {
    await page.goto(LISTING);
    await loaded(page);
    await expect(page.locator('.plp__count')).toContainText('120');
    await scrollTo(page, 900);
    await mark(page);

    const blue = page.locator('.plp__sidebar').getByRole('checkbox', { name: 'Blue' });
    await applyUntil(page, () => tick(blue), /colour=blue/);

    await expect(page.locator('.plp__count')).toContainText('30');
    expect(await scrollY(page), 'a filter threw the shopper to the top').toBeGreaterThan(700);
    expect(await wasKept(page)).toBe(true);
  });

  test('changing the sort keeps the position', async ({ page }) => {
    await page.goto(LISTING);
    await loaded(page);
    await scrollTo(page, 900);

    const sort = page.locator('#listing-sort');
    const value = await otherOption(sort);
    await applyUntil(page, () => chooseOption(sort, value), /sort=/);

    expect(await scrollY(page), 'the sort threw the shopper to the top').toBeGreaterThan(700);
  });

  test('removing a filter chip keeps the position', async ({ page }) => {
    await page.goto(`${LISTING}?colour=blue`);
    await loaded(page);
    await scrollTo(page, 900);

    const chip = page.getByRole('button', { name: /Remove filter: Colour: Blue/ }).first();
    await expect
      .poll(
        async () => {
          await clickIfPresent(chip);
          return page.url();
        },
        { timeout: 15_000 },
      )
      .not.toMatch(/colour=blue/);

    expect(await scrollY(page), 'removing a chip threw the shopper to the top').toBeGreaterThan(700);
  });

  test('Load More appends and keeps the position', async ({ page }) => {
    await page.goto(LISTING);
    await loaded(page);

    const cards = page.locator('.product-grid > li');
    const first = await cards.count();
    const before = await scrollToEnd(page);

    // Clicked only while nothing has been appended yet, so a click the page was not ready for is repeated and a
    // click it did answer is not.
    await expect
      .poll(
        async () => {
          if ((await cards.count()) === first) {
            await clickIfPresent(page.getByRole('button', { name: 'Load More' }));
          }
          return cards.count();
        },
        { timeout: 15_000 },
      )
      .toBeGreaterThan(first);

    expect(await scrollY(page), 'Load More threw the shopper to the top').toBeGreaterThanOrEqual(before - 30);
  });

  /**
   * The same page spelled another way: capitals, and a trailing slash. The listing writes its own canonical path, so
   * the first state change moves the address from one spelling to the other. A rule that compared the text of the
   * path would scroll to the top there, for a page the shopper never left.
   */
  for (const spelling of ['/WOMEN/Jeans', '/women/jeans/']) {
    test(`a filter keeps the position on ${spelling}, the same page spelled another way`, async ({ page }) => {
      await page.goto(`${spelling}?pages=2`);
      await loaded(page);
      await scrollTo(page, 900);

      await applyUntil(page, () => tick(page.locator('.plp__sidebar').getByRole('checkbox', { name: 'Blue' })), /colour=blue/);

      expect(await scrollY(page), `a filter on ${spelling} threw the shopper to the top`).toBeGreaterThan(700);
    });
  }

  test('choosing another department goes to the top of that page', async ({ page }) => {
    await page.goto(LISTING);
    await loaded(page);
    await scrollTo(page, 900);

    await applyUntil(page, () => tick(page.locator('.plp__sidebar').getByRole('radio', { name: 'Men', exact: true })), /\/men/);

    await expect.poll(() => scrollY(page), { message: 'a new department should open at its top' }).toBe(0);
  });

  /**
   * A category inside the same department is a different page too. A rule that compared only the department (the
   * first segment of the path) would leave the shopper where they were here.
   */
  test('switching category inside a department goes to the top, and back again', async ({ page }) => {
    await page.goto(LISTING);
    await loaded(page);
    await scrollTo(page, 900);

    await page
      .getByRole('navigation', { name: /breadcrumb/i })
      .getByRole('link', { name: 'Women', exact: true })
      .evaluate((link: HTMLElement) => link.click());
    await expect(page).toHaveURL(/\/women$/);
    await expect.poll(() => scrollY(page), { message: 'category to department should open at the top' }).toBe(0);

    await scrollTo(page, 900);
    await applyUntil(
      page,
      () => tick(page.locator('.plp__sidebar').getByRole('radio', { name: 'Jeans', exact: true })),
      /\/women\/jeans/,
    );
    await expect.poll(() => scrollY(page), { message: 'department to category should open at the top' }).toBe(0);
  });
});

test.describe('on the search results', () => {
  test.use({ viewport: { width: 1280, height: 800 } });

  /** The search page has its own `apply()`; its state changes need their own proof. */
  test('changing the sort keeps the position', async ({ page }) => {
    await page.goto('/search?q=test&pages=2');
    await loaded(page);
    await expect(page.locator('.product-grid > li').first()).toBeVisible();
    await scrollTo(page, 900);

    const sort = page.locator('#search-sort');
    const value = await otherOption(sort);
    await applyUntil(page, () => chooseOption(sort, value), /sort=/);

    expect(await scrollY(page), 'the sort on the search results threw the shopper to the top').toBeGreaterThan(700);
  });

  test('ticking a filter and removing its chip keep the position', async ({ page }) => {
    await page.goto('/search?q=test&pages=2');
    await loaded(page);
    await expect(page.locator('.product-grid > li').first()).toBeVisible();
    await scrollTo(page, 900);

    await applyUntil(page, () => tick(page.locator('.search__sidebar').getByRole('checkbox', { name: 'Blue' })), /colour=blue/);
    expect(await scrollY(page), 'a filter on the search results threw the shopper to the top').toBeGreaterThan(700);

    const chip = page.getByRole('button', { name: /Remove filter: Colour: Blue/ }).first();
    await expect
      .poll(
        async () => {
          await clickIfPresent(chip);
          return page.url();
        },
        { timeout: 15_000 },
      )
      .not.toMatch(/colour=blue/);
    expect(await scrollY(page), 'removing a chip on the search results threw the shopper to the top').toBeGreaterThan(
      700,
    );
  });

  test('Load More keeps the position', async ({ page }) => {
    await page.goto('/search?q=test');
    await loaded(page);
    const cards = page.locator('.product-grid > li');
    await expect(cards.first()).toBeVisible();
    const first = await cards.count();
    const before = await scrollToEnd(page);

    await expect
      .poll(
        async () => {
          if ((await cards.count()) === first) {
            await clickIfPresent(page.getByRole('button', { name: 'Load More' }));
          }
          return cards.count();
        },
        { timeout: 15_000 },
      )
      .toBeGreaterThan(first);
    expect(await scrollY(page), 'Load More on the search results threw the shopper to the top').toBeGreaterThanOrEqual(
      before - 30,
    );
  });
});

test.describe('on the product page', () => {
  test.use({ viewport: { width: 390, height: 844 } });

  test('choosing a size and a colour does not carry the shopper back to the top', async ({ page }) => {
    await page.goto(PRODUCT);
    await loaded(page);
    await scrollTo(page, 500);

    // A size or a colour is chosen only while it is not chosen yet: choosing the chosen size again clears it.
    const size = page.locator('.sd-size:not([disabled])').first();
    await applyUntil(
      page,
      () =>
        size.evaluate((button: HTMLElement) => {
          if (button.getAttribute('aria-pressed') !== 'true') {
            button.click();
          }
        }),
      /size=/,
    );
    await expect(size).toHaveAttribute('aria-pressed', 'true');
    expect(await scrollY(page), 'choosing a size threw the shopper to the top').toBeGreaterThan(400);

    const colours = page.locator('.pdp__group[aria-labelledby="pdp-colour-label"] button:not([disabled])');
    if ((await colours.count()) > 1) {
      await applyUntil(
        page,
        () =>
          colours.nth(1).evaluate((button: HTMLElement) => {
            if (button.getAttribute('aria-pressed') !== 'true') {
              button.click();
            }
          }),
        /colour=/,
      );
      expect(await scrollY(page), 'choosing a colour threw the shopper to the top').toBeGreaterThan(400);
    }
  });
});
