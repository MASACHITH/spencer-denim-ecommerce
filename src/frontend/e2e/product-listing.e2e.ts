import { Page, expect, test } from '@playwright/test';

import { DEEP_CATALOGUE_TOTAL, apiURL, deepCatalogueURL, emptyCatalogueURL } from '../playwright.config';

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

    expect(html).toContain('<title>Jeans | Women | Colombo Wardrobe</title>');
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

  test('the page carries no second copy of the catalogue, and no internal address', async ({
    request,
  }) => {
    const html = await (await request.get(LISTING)).text();
    const state = /<script[^>]*id="ng-state"[^>]*>([\s\S]*?)<\/script>/.exec(html)?.[1] ?? '';

    // Angular's transfer cache is off (see app.config.ts): it stored every SSR response under a
    // key the browser cannot use, so the payload shipped twice — with API_INTERNAL_URL in it.
    expect(state).not.toContain('/api/catalog/');
    // Taken from the config rather than written out. It was the literal `localhost:4421`, which
    // matches nothing at all once the ports are moved by the environment — the check would have
    // gone on passing over HTML full of internal addresses.
    expect(html).not.toContain(new URL(apiURL).host);
    expect(html).not.toContain('API_INTERNAL_URL');
  });

  test('the listing names the one address it should be indexed at', async ({ request }) => {
    const html = await (await request.get('/WOMEN/jeans?colour=blue&pages=2')).text();
    const canonical = /<link rel="canonical" href="([^"]*)"/.exec(html)?.[1];

    // Upper case and "how much is loaded" are not different pages.
    expect(canonical).toBe('/women/jeans?colour=blue');
  });

  test('a filtered listing is rendered filtered, not rendered then filtered', async ({ request }) => {
    const html = await (await request.get(`${LISTING}?colour=blue`)).text();

    expect(html).toContain('30');
    expect(html).toContain('Colour: Blue');
  });
});

test.describe('addresses that are not a department', () => {
  /**
   * `/:department` matches any single segment. Every header and footer link that is not built yet
   * used to render the listing's not-found state at HTTP 200 — the copy told a shopper that a
   * *department* no longer existed when they had clicked "Bag".
   */
  // `/account` left `UNBUILT_PATHS` when W3-01 built the account pages. It used to redirect home; it
  // now opens the order history, which sends a visitor who is not signed in to sign in. The property
  // this block is about is unchanged: it is not the listing's "department not found".
  for (const path of ['/account']) {
    test(`${path} opens the account area, not a listing`, async ({ page }) => {
      const response = await page.goto(path);

      expect(response?.status()).toBe(200);
      await expect(page).toHaveURL(/\/account\/sign-in\?returnTo=%2Faccount%2Forders$/);

      await expect(page.locator('sd-listing-page')).toHaveCount(0);
      await expect(page.locator('sd-sign-in-page')).toHaveCount(1);
      await expect(page.getByText('could not find that page')).toHaveCount(0);
    });
  }

  /**
   * **The content pages left that list in W2-11 (GLB-10), and the answer they give changed.**
   *
   * They used to redirect home, which was the price of fixing #28 before GLB-10 landed: the shell
   * linked to all of them on every page, and a 404 on a link in your own footer is worse than a soft
   * 404. Both halves of that are gone now — they are real pages, and the footer links one only once it
   * is published — so an unpublished one is an address the shop genuinely does not have and answers a
   * **real 404**. The home page at HTTP 200 for an address that is not the home page is #28 itself.
   *
   * What has not changed is the thing this describe block exists for: it must not be *this listing's*
   * not-found state, which told a shopper a department had gone when they clicked "Privacy policy".
   */
  for (const path of ['/about', '/privacy', '/terms', '/contact']) {
    test(`${path} is a real 404 while the client has published nothing`, async ({ page }) => {
      const response = await page.goto(path);

      expect(response?.status()).toBe(404);
      await expect(page).toHaveURL(path);

      await expect(page.locator('sd-listing-page')).toHaveCount(0);
      await expect(page.locator('sd-home-page')).toHaveCount(0);
      await expect(page.getByText('could not find that page')).toHaveCount(1);
    });
  }

  /**
   * `/bag` was in the list above until W2-09. It is a real page now (CART-01), and what keeps it off
   * the listing is the **route order** — `bag` is listed ahead of `:department` — rather than the
   * department guard. Worth its own case rather than a deletion: swap the two routes and `/bag` renders
   * the listing's not-found state at HTTP 200 again, which is the bug the deleted case was written for.
   */
  test('/bag is the bag page, not a department that does not exist', async ({ page }) => {
    const response = await page.goto('/bag');

    expect(response?.status()).toBe(200);
    await expect(page).toHaveURL('/bag');
    await expect(page.locator('sd-bag-page')).toHaveCount(1);
    await expect(page.locator('sd-listing-page')).toHaveCount(0);
    await expect(page.getByText('could not find that page')).toHaveCount(0);
  });

  test('a category from another department is a real 404, not a 200 that says so', async ({
    request,
  }) => {
    // /women/shirts names a Men's category. It used to render as the Women department page:
    // an <h1>Women</h1> over "0 products" while /women itself had products.
    const response = await request.get('/women/shirts');
    const html = await response.text();

    expect(response.status()).toBe(404);
    expect(html).toContain('We could not find that page');
    expect(html).toContain('<meta name="robots" content="noindex">');
    expect(html).not.toContain('No products found');
  });

  test('a 404 asks the catalogue for nothing', async ({ page }) => {
    const calls: string[] = [];
    page.on('request', (request) => {
      if (request.url().includes('/api/catalog/products')) {
        calls.push(request.url());
      }
    });

    await page.goto('/women/shirts');
    await expect(page.getByRole('heading', { level: 1 })).toHaveText('We could not find that page');

    expect(calls).toEqual([]);
  });
});

test.describe('a catalogue with no active departments', () => {
  /**
   * `GET /api/catalog/departments` answers `200 []` when nothing is active — the state of a
   * freshly migrated database before anyone runs the seeder, which the README says is off by
   * default and deliberate. `catchError` does not see a successful response, so before the fix the
   * route guards refused every department and the first boot of a new environment gave a
   * storefront whose every listing link silently went home.
   */
  test.use({ baseURL: emptyCatalogueURL });

  test('a department still renders, from the seeded fallback', async ({ page }) => {
    const response = await page.goto('/women');

    expect(response?.status()).toBe(200);
    await expect(page).toHaveURL(`${emptyCatalogueURL}/women`);
    await expect(page.getByRole('heading', { level: 1 })).toHaveText('Women');
  });

  test('the menu and the routes agree about which departments exist', async ({ page }) => {
    await page.goto('/');

    const links = await page
      .locator('.site-nav__list a.site-nav__link')
      .evaluateAll((nodes) => nodes.map((node) => (node as HTMLAnchorElement).pathname));

    expect(links).toEqual(['/women', '/men', '/kids', '/new-arrivals', '/sale']);

    for (const link of links.slice(0, 3)) {
      const response = await page.goto(link);
      expect(response?.status(), `${link} should render, not redirect`).toBe(200);
      await expect(page).toHaveURL(`${emptyCatalogueURL}${link}`);
    }
  });

  test('a category under a fallback department is still a page, not the home page', async ({
    page,
  }) => {
    await page.goto('/women/jeans');

    await expect(page).toHaveURL(`${emptyCatalogueURL}/women/jeans`);
    await expect(page.getByRole('heading', { level: 1 })).toHaveText('Jeans');
  });
});

test.describe('New Arrivals and Sale (DEPT-02, DEPT-03)', () => {
  for (const entry of [
    { path: '/new-arrivals', heading: 'New Arrivals', title: 'New Arrivals | Colombo Wardrobe' },
    { path: '/sale', heading: 'Sale', title: 'Sale | Colombo Wardrobe' },
  ]) {
    test(`${entry.path} is a listing, not a missing page`, async ({ page, request }) => {
      const response = await request.get(entry.path);
      const html = await response.text();

      expect(response.status()).toBe(200);
      expect(html).toContain(`<title>${entry.title}</title>`);
      expect(html).not.toContain('We could not find that page');

      await page.goto(entry.path);
      await expect(page.getByRole('heading', { level: 1 })).toHaveText(entry.heading);
      await expect(page.locator('.product-grid > li').first()).toBeVisible();
    });
  }

  test('every main navigation entry leads somewhere that exists', async ({ page }) => {
    await page.goto('/');

    const links = await page
      .locator('.site-nav__list a.site-nav__link')
      .evaluateAll((nodes) => nodes.map((node) => (node as HTMLAnchorElement).pathname));

    expect(links).toEqual(['/women', '/men', '/kids', '/new-arrivals', '/sale']);

    for (const link of links) {
      await page.goto(link);
      await expect(page.locator('h1')).not.toHaveText('We could not find that page');
      await expect(page).toHaveURL(link);
    }
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

  test('the filter panel exists once, not once per container', async ({ page, request }) => {
    // Rendering the sidebar and the drawer both shipped every control twice: 202 inputs in the
    // DOM at every width, and the same doubled in the HTML the server sends.
    const html = await (await request.get(LISTING)).text();
    // Opening tags only: the closing tag would count every panel twice.
    expect((html.match(/<sd-listing-filters[\s>]/g) ?? []).length).toBe(1);

    for (const width of [390, 1280]) {
      await page.setViewportSize({ width, height: 900 });
      await page.goto(LISTING);
      await expect(page.locator('.product-grid > li').first()).toBeVisible();

      await expect(page.locator('sd-listing-filters')).toHaveCount(1);
    }
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

/**
 * The media pipeline builds a WebP rendition of every public image at four widths, and for a
 * while nothing used them: the API sent the rendition URLs, the cards rendered them, and the
 * browser still fetched the 1200 px file for a 200 px card because no `<img>` carried a `sizes`.
 * One page of this listing weighed about 1,780 KB where it should have weighed about 330 KB.
 *
 * Neither half of that is visible in jsdom — the unit suite has no layout, so every `sizes` is
 * true there — which is why the check lives here, against a real viewport and a real image the
 * browser really chose.
 */
test.describe('card imagery comes from the renditions (CARD-01)', () => {
  test('every picture in the grid is a rendition, never an original', async ({ page }) => {
    await page.goto(LISTING);
    await expect(page.locator('.product-grid > li').first()).toBeVisible();

    const images = await page.locator('.product-grid img').evaluateAll((nodes) =>
      nodes.map((node) => ({
        src: node.getAttribute('src') ?? '',
        srcset: node.getAttribute('srcset'),
      })),
    );

    // The card drops an `<img>` whose file would not load, so an empty grid would pass every
    // assertion below without a picture having been rendered at all.
    expect(images.length).toBeGreaterThan(0);

    for (const image of images) {
      expect(image.src).toContain('/renditions/');
      expect(image.src).not.toContain('/originals/');
    }

    // The stub gives every card a candidate list, so every card has to render one: a `srcset`
    // the component quietly drops is exactly the regression this test exists for.
    expect(images.filter((image) => image.srcset !== null)).toHaveLength(images.length);

    for (const image of images) {
      const candidates = image.srcset!.split(',').map((candidate) => candidate.trim());

      expect(candidates.length).toBeGreaterThan(1);
      for (const candidate of candidates) {
        expect(candidate).toMatch(/^\S*\/renditions\/\S+\.webp \d+w$/);
      }
    }
  });

  test('the file the browser really fetches is the one a card is wide enough for', async ({
    page,
  }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await page.goto(LISTING);

    const first = page.locator('.product-grid img').first();
    await expect(first).toBeVisible();
    // The choice is only the browser's to make if it was given one; without this the assertion
    // below passes on any component that renders the card URL and no candidates at all.
    await expect(first).toHaveAttribute('srcset', /\/thumb\.webp 160w/);
    await first.evaluate((image: HTMLImageElement) => image.decode());

    // A card is about 206 px wide here, so the 600 px rendition is the smallest that covers it.
    // Without a `sizes` the browser assumes the image fills the 1280 px viewport and takes the
    // 1200 px one instead, which is the whole of the bug.
    expect(await first.evaluate((image: HTMLImageElement) => image.currentSrc)).toContain(
      '/card.webp',
    );
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

test.describe('a shared or reloaded deep listing (PLP-07)', () => {
  /**
   * The restore path is the whole reason `pages` is in the URL, and no browser test went past
   * `pages=2` — a single request, where the arithmetic cannot go wrong. `toRestoreRequests` shrank
   * its last request to the rows still wanted, and because the API's offset is
   * `(Page - 1) * PageSize` against that request's own page size, the smaller last page moved its
   * window backwards: `?pages=5` served rows 25–48 where it meant 97–120, and the shopper got 120
   * cards of which 96 were distinct.
   */
  test.use({ baseURL: deepCatalogueURL });

  for (const loadedPages of [2, 5, 6, 17]) {
    test(`?pages=${loadedPages} shows every product once`, async ({ page }) => {
      await page.goto(`/women/jeans?pages=${loadedPages}`);

      const shown = Math.min(loadedPages * 24, DEEP_CATALOGUE_TOTAL);
      const cards = page.locator('.product-grid > li');
      await expect(cards).toHaveCount(shown);

      const names = await page
        .locator('.card__name a')
        .evaluateAll((nodes) => nodes.map((node) => node.textContent?.trim() ?? ''));

      expect(new Set(names).size, `${loadedPages} pages repeated a product`).toBe(shown);
      await expect(page.locator('.plp__more-count').first()).toHaveText(
        `Showing ${shown} of ${DEEP_CATALOGUE_TOTAL} products`,
      );
    });
  }

  test('the server renders the deep listing without repeating one either', async ({ request }) => {
    const html = await (await request.get('/women/jeans?pages=5')).text();
    const names = [...html.matchAll(/>(Test Product \d+)</g)].map((match) => match[1]);

    expect(names.length).toBe(120);
    expect(new Set(names).size).toBe(120);
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
