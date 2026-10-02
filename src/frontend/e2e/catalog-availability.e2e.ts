import { APIRequestContext, Page, expect, test } from '@playwright/test';

import { LIVE_ONLY_LISTING, liveOnlyApiURL, liveOnlyCatalogueURL } from '../playwright.config';
import { horizontalOverflow, undersized } from './support/measure';

/**
 * What the storefront answers for a real listing while the departments feed is unhealthy (#30).
 *
 * The route guards accept only the slugs the catalogue tree knows, and when that feed cannot be
 * read the tree is `core/navigation/navigation.data.ts`, a frozen copy of the seed. A category that
 * exists live and not in that copy used to answer **404 with `<meta name="robots" content=
 * "noindex">`** — so a page that is perfectly fine was told to leave the index for as long as the
 * API was unhappy, and a 2 s timeout is easy to trip on a loaded VPS. The honest answer is **503
 * with `Retry-After`**: a crawler retries a 503 and keeps the URL, where it drops a 404.
 *
 * Only a real server can show this. The status code and the `robots` meta are what reaches the
 * wire before a line of JavaScript has run, and a unit test asserting that a function returned
 * `{ status: 503 }` proves the function, not the answer.
 *
 * This file owns the sixth server pair in `playwright.config.ts`: a catalogue with **Women › Coats**
 * in it, which the frozen fallback has never heard of, and a stub whose fault is switched per test.
 * One switchable stub rather than five more SSR servers — which is why these run serially.
 */
test.describe.configure({ mode: 'serial' });

/**
 * Every unhealthy mode, all five of which leave the storefront on the fallback tree. The first four
 * are #30's table and all four used to answer 404 + noindex; `garbage` is the fifth shape
 * `NavigationService` guards against and was only ever measured in a unit test.
 */
const UNHEALTHY = [
  ['the feed answers 500', 'status'],
  ['the connection is destroyed', 'destroy'],
  ['the answer comes 12 s after the 2 s timeout', 'slow'],
  ['the feed answers 200 with an empty array', 'empty'],
  ['the feed answers 200 with something that is not a tree', 'garbage'],
] as const;

/** A listing both trees have, so it is served from the fallback with nothing live to read. */
const SEEDED_LISTING = '/women/dresses';

/** A category the live tree does not have either: Shirts belongs to Men. A real 404. */
const MISSING_LISTING = '/women/shirts';

async function fault(request: APIRequestContext, mode: string): Promise<void> {
  const response = await request.get(`${liveOnlyApiURL}/__test__/departments-fault?mode=${mode}`);
  expect(response.ok(), `the stub accepted the "${mode}" fault`).toBe(true);
}

/**
 * Set on the way **in**, for every test, because `afterEach` does not run for a test that was
 * skipped — and in a serial file one failure skips the rest, so a cleanup on the way out leaves the
 * fault set for the next run (which reuses the stub). No test may depend on what the last one left.
 * The stub's readiness probe resets it too, so even a reused process starts healthy.
 */
test.beforeEach(async ({ request }) => {
  await fault(request, 'none');
});

test.describe('a healthy departments feed', () => {
  test('serves the live-only listing, and does not ask a crawler to drop it', async ({
    request,
  }) => {
    const response = await request.get(`${liveOnlyCatalogueURL}${LIVE_ONLY_LISTING}`);

    expect(response.status()).toBe(200);

    const html = await response.text();
    expect(html).toContain('Coats');
    expect(html).not.toContain('noindex');
    expect(html).not.toContain('We could not find that page');
  });

  /**
   * The fix must not have turned 404s off. `/women/shirts` names a Men's category, the live tree
   * says so, and a page the catalogue really does not have is gone rather than unavailable.
   */
  test('still answers a real 404, with noindex, for a category the live tree does not have', async ({
    request,
  }) => {
    const response = await request.get(`${liveOnlyCatalogueURL}${MISSING_LISTING}`);

    expect(response.status()).toBe(404);

    const html = await response.text();
    expect(html).toContain('We could not find that page');
    expect(html).toContain('noindex');
    expect(response.headers()['retry-after']).toBeUndefined();
  });
});

for (const [label, mode] of UNHEALTHY) {
  test.describe(`when ${label}`, () => {
    test.beforeEach(async ({ request }) => {
      await fault(request, mode);
    });

    test('answers 503 with Retry-After for the live-only listing, never 404 + noindex', async ({
      request,
    }) => {
      const response = await request.get(`${liveOnlyCatalogueURL}${LIVE_ONLY_LISTING}`);

      expect(response.status()).toBe(503);
      expect(Number(response.headers()['retry-after'])).toBeGreaterThan(0);

      const html = await response.text();
      // The whole point: the URL keeps its place in the index.
      expect(html).not.toContain('noindex');
      // And it is not dressed up as a page that no longer exists.
      expect(html).not.toContain('We could not find that page');
      expect(html).toContain('Try again');
      // Never a status code, a stack or the API's own words in front of a shopper.
      expect(html).not.toContain('Forced failure');
    });

    test('still serves a listing the fallback tree does have', async ({ request }) => {
      const response = await request.get(`${liveOnlyCatalogueURL}${SEEDED_LISTING}`);

      expect(response.status()).toBe(200);
      expect(await response.text()).not.toContain('noindex');
    });

    /**
     * **A tree the render would not route on is not handed to the browser.**
     *
     * `tap(give)` used to sit above `timeout` and above the vetting, so a `200 []` was handed over
     * before anything judged it. The browser then found a non-null value in `TransferState` and made
     * **no request at all**, which stranded the session on the 503 — where a 500, a destroyed
     * connection and a timeout all recovered, because those hand over nothing. Checked for every
     * mode rather than for the two that were broken, so the property is "only a live tree travels"
     * rather than "these particular bodies do not".
     */
    test('hands the browser no tree it could not route on', async ({ request }) => {
      const html = await (await request.get(`${liveOnlyCatalogueURL}${LIVE_ONLY_LISTING}`)).text();

      expect(html).not.toContain('catalog:departments');
    });
  });
}

/**
 * **The one address class that may answer 503**, and the reason the guard asks the catalogue rather
 * than a hand-written list.
 *
 * `canMatch` runs on `:department/:category`, which matches *any* two segments, so a 503 route that
 * only asked "is the deepest segment unknown?" answered 503 for every two- and three-segment
 * address on this origin for the length of an outage. Measured on that version: `/api/catalog/
 * departments`, `/api/catalog/products`, `/en/women`, `/si/women`, `/assets/x`, `/static/x`,
 * `/account/orders`, `/bag/checkout` and `/wp-admin/setup-config.php` all answered 503 where `main`
 * answers 302 to the home page. `/api/...` is the sharp one: `server.ts` does not proxy `/api`
 * (Caddy does, in production), so a direct container hit, a proxy misconfiguration or an uptime
 * monitor would be handed a 503 HTML page by the storefront itself.
 *
 * So the guard requires a **department the tree in hand already holds**. That is an allow-list, and
 * it is the catalogue's own, so there is nothing to keep correct by hand: a page added to the header
 * tomorrow, a locale prefix, a scanner's probe and `/.well-known/...` are outside it by
 * construction.
 */
test.describe('addresses that are not listings are never a 503', () => {
  test.beforeEach(async ({ request }) => {
    await fault(request, 'status');
  });

  /**
   * **These answered 200 at the home page when this suite was written, and now answer 404.** The
   * change is #28's, not #30's, and the invariant this block exists for is untouched: none of them is
   * a 503, and none of them carries `Retry-After`.
   *
   * Reaching the home page was itself the defect #28 records — a soft 404, the wrong page under the
   * wrong URL at a status code that tells a crawler it is the right one. `/wp-admin/setup-config.php`
   * is the clearest case: a scanner's probe was answered with the shop's front page. 404 is the honest
   * answer and it is strictly better for every path in this list; what mattered here was only that the
   * 503 route does not claim them, and it still does not.
   */
  for (const path of [
    '/api/catalog/departments',
    '/api/catalog/products',
    '/en/women',
    '/si/women',
    '/assets/x',
    '/static/x',
    '/account/orders',
    '/bag/checkout',
    '/wp-admin/setup-config.php',
  ]) {
    test(`${path} is a 404, not a 503`, async ({ request }) => {
      const response = await request.get(`${liveOnlyCatalogueURL}${path}`);

      expect(response.status()).toBe(404);
      expect(response.headers()['retry-after']).toBeUndefined();
      expect(await response.text()).toContain('We could not find that page');
    });
  }

  /**
   * **A mistyped department during an outage is a 404, and that is deliberate rather than an oversight
   * of #30's reasoning.**
   *
   * #30's ambiguity is real for a **category**: the client can create one (ADM-11, W1-12), so a slug
   * the frozen fallback has never heard of may be a live, indexed listing and a 404 would drop it.
   * It does not arise for a **department**: nothing creates one at runtime — the admin API has no
   * create-department endpoint, and W1-12 settled that a department's name and slug are not editable
   * (docs/02 §I2) — so the seeded list is closed and `core/navigation/navigation.data.ts` mirrors it.
   * A single segment the tree in hand does not have is not a department whichever tree that was.
   *
   * The assumption, written down because it is the one way this could become wrong: if a later
   * migration seeds a sixth department, that fallback has to gain it in the same change.
   */
  test('a mistyped department is a 404 during an outage, not a 503', async ({ request }) => {
    const response = await request.get(`${liveOnlyCatalogueURL}/not-a-dept`);

    expect(response.status()).toBe(404);
    expect(response.headers()['retry-after']).toBeUndefined();
  });

  /** And the unbuilt pages the shell links to still go home, outage or not (#28). */
  for (const path of ['/wishlist', '/account', '/about', '/privacy']) {
    test(`${path} still reaches the home page during an outage`, async ({ request }) => {
      const response = await request.get(`${liveOnlyCatalogueURL}${path}`);

      expect(response.status()).toBe(200);
      expect(new URL(response.url()).pathname).toBe('/');
    });
  }

  /**
   * `/bag` was in the list above until W2-09 built the page (CART-01). It belongs in this block more
   * than it did before, not less: the whole point here is that an address which is **not a listing** is
   * never answered 503, and the bag is now a real page that has to survive a catalogue outage **at its
   * own URL** rather than by being redirected away from it.
   *
   * It does not read the catalogue at all — it reads `/api/cart`, which in this fixture is the same
   * unhealthy stub — so this also says the bag page does not inherit the catalogue's failure. A bag
   * cannot be loaded here, and the right answer to that is still a 200 with the page on it.
   */
  test('/bag is a 200 at its own address during an outage, never a 503', async ({ request }) => {
    const response = await request.get(`${liveOnlyCatalogueURL}/bag`);

    expect(response.status()).toBe(200);
    expect(response.headers()['retry-after']).toBeUndefined();
    expect(new URL(response.url()).pathname).toBe('/bag');
    expect(await response.text()).toContain('<sd-bag-page');
  });

  /**
   * A crawler's well-known files, kept as a canary rather than as protection.
   *
   * Nothing claims a single segment any more, so these cannot be 503 by construction — but
   * `/robots.txt` is why that matters and the reason belongs on the record: there is no such file in
   * the build, so the request reaches the router, and a crawler that gets 5xx for `robots.txt`
   * treats the **whole site** as disallowed until it can read one. `/favicon.ico` is the one of
   * these that really exists (`projects/storefront/public/`), so it is served statically at 200 and
   * never reaches Angular at all; the other two fall through to `**`.
   */
  test('a crawler’s well-known files are never swept into the 503 route', async ({ request }) => {
    const answers: Record<string, number> = {};
    for (const path of ['/robots.txt', '/favicon.ico']) {
      answers[path] = (await request.get(`${liveOnlyCatalogueURL}${path}`)).status();
    }

    expect(answers).toEqual({ '/robots.txt': 200, '/favicon.ico': 200 });

    // `sitemap.xml` is the one of the three that legitimately may be a 503 — see the test below.
    const sitemap = await request.get(`${liveOnlyCatalogueURL}/sitemap.xml`);
    expect([200, 503]).toContain(sitemap.status());
    if (sitemap.status() === 503) {
      expect(Number(sitemap.headers()['retry-after'])).toBeGreaterThan(0);
    }
  });

  /**
   * **The two files are not the same case, and the difference is worth stating.**
   *
   * `robots.txt` must be a 200 whatever is wrong, because a crawler answered 5xx for it treats the
   * **whole site** as disallowed until it can read one. It is a generated constant — it needs nothing
   * from the catalogue — so Express answers it from the request alone.
   *
   * `sitemap.xml` is a catalogue read, so it can fail, and when it does **503 with `Retry-After` is
   * the correct answer**: an empty `urlset` is a valid document that says the shop has no pages and a
   * crawler cannot tell it from the truth. This pair's fault is on `/departments` only, so the sitemap
   * feed here is healthy; `seo.e2e.ts` measures the 503 on the pair whose product queries all fail.
   */
  test('robots.txt is a real file and still 200 while the departments feed is down', async ({
    request,
  }) => {
    const response = await request.get(`${liveOnlyCatalogueURL}/robots.txt`);

    expect(response.status()).toBe(200);
    expect(response.headers()['content-type']).toContain('text/plain');
    expect(await response.text()).toContain('User-agent: *');
  });

});

/**
 * The browser half. A 503 can only be set while the server renders, so what the shopper gets after
 * hydration has to be checked separately: a blank page, or a silent bounce to the home page, would
 * be its own defect.
 */
test.describe('the page a shopper sees', () => {
  /**
   * The browser's own catalogue calls go to the storefront's origin, which in production is a proxy
   * in front of the API. Here it is this pair's stub, so the page asks the same unhealthy feed the
   * server did rather than repairing itself against the healthy one.
   */
  async function useFaultyStub(page: Page): Promise<void> {
    await page.route('**/api/catalog/**', async (route) => {
      const url = new URL(route.request().url());
      const response = await route.fetch({ url: `${liveOnlyApiURL}${url.pathname}${url.search}` });
      await route.fulfill({ response });
    });
  }

  test.beforeEach(async ({ page, request }) => {
    await fault(request, 'status');
    await useFaultyStub(page);
  });

  test('explains itself, keeps the shell, and offers a way on', async ({ page }) => {
    await page.goto(`${liveOnlyCatalogueURL}${LIVE_ONLY_LISTING}`);

    await expect(page.getByRole('heading', { level: 1 })).toHaveText(
      'We could not load this page just now',
    );
    await expect(page.getByRole('button', { name: 'Try again' })).toBeVisible();
    await expect(page.getByRole('link', { name: 'Back to the home page' })).toBeVisible();
    // The menu is still there, from the fallback, so the shopper is not stranded.
    await expect(page.locator('header').getByRole('link', { name: 'Women' }).first()).toBeVisible();
    // And the address bar still says what was asked for: no silent redirect home.
    expect(new URL(page.url()).pathname).toBe(LIVE_ONLY_LISTING);
  });

  test('Try again serves the listing once the feed recovers', async ({ page, request }) => {
    await page.goto(`${liveOnlyCatalogueURL}${LIVE_ONLY_LISTING}`);
    await expect(page.getByRole('button', { name: 'Try again' })).toBeVisible();

    await fault(request, 'none');
    await page.getByRole('button', { name: 'Try again' }).click();

    await expect(page.getByRole('heading', { level: 1 })).toHaveText('Coats');
    expect(new URL(page.url()).pathname).toBe(LIVE_ONLY_LISTING);
  });

  /** docs/04 §4 and the project's non-negotiable list. */
  for (const width of [320, 375, 390, 768, 1024, 1280, 1440, 1920] as const) {
    test(`fits ${width}px with no horizontal scroll`, async ({ page }) => {
      await page.setViewportSize({ width, height: 900 });
      await page.goto(`${liveOnlyCatalogueURL}${LIVE_ONLY_LISTING}`);
      await expect(page.getByRole('button', { name: 'Try again' })).toBeVisible();

      expect(await horizontalOverflow(page)).toBeLessThanOrEqual(0);
    });
  }

  /**
   * **Every control on the page, not the two the component happens to style as buttons.**
   *
   * This used to read `.unavailable__actions .btn`, which is the two action buttons — and the five
   * department links outside that selector were 20 px tall (57x20, 33x20, 33x20, 96x20, 32x20 at
   * 320 px) while the suite stayed green. An assertion scoped to what the component does cannot
   * report what the component got wrong (MEMORY.md, lesson of the W2-03 round).
   */
  test('every control it draws meets the 44px touch minimum at 320px', async ({ page }) => {
    await page.setViewportSize({ width: 320, height: 720 });
    await page.goto(`${liveOnlyCatalogueURL}${LIVE_ONLY_LISTING}`);
    await expect(page.getByRole('button', { name: 'Try again' })).toBeVisible();

    expect(await undersized(page, '.unavailable a, .unavailable button')).toEqual([]);
  });
});

/**
 * Hydration after a render that fell back: the browser asks the feed itself, because the render
 * handed over nothing it could route on. If the catalogue has recovered by then, the shopper gets
 * the real listing without touching anything.
 *
 * The recovery is made deterministic without mocking: the fault is cleared **inside** the route
 * handler for the browser's own departments call, so the outage ends exactly when the browser asks.
 * If the browser does not ask — which is what handing over `[]` caused — the handler never runs, the
 * feed stays sick, and the page stays on the 503. That is the assertion.
 */
test.describe('a transient outage does not strand the session', () => {
  for (const [label, mode] of UNHEALTHY) {
    test(`recovers on hydration after ${label}`, async ({ page, request }) => {
      await fault(request, mode);

      // Registered first, so the more specific route below takes precedence.
      await page.route('**/api/catalog/**', async (route) => {
        const url = new URL(route.request().url());
        const response = await route.fetch({
          url: `${liveOnlyApiURL}${url.pathname}${url.search}`,
        });
        await route.fulfill({ response });
      });
      await page.route('**/api/catalog/departments', async (route) => {
        await fault(request, 'none');
        const response = await route.fetch({ url: `${liveOnlyApiURL}/api/catalog/departments` });
        await route.fulfill({ response });
      });

      const response = await page.goto(`${liveOnlyCatalogueURL}${LIVE_ONLY_LISTING}`);
      expect(response?.status()).toBe(503);

      await expect(page.getByRole('heading', { level: 1 })).toHaveText('Coats');
    });
  }
});
