import AxeBuilder from '@axe-core/playwright';
import { Page, expect, test } from '@playwright/test';

import { apiURL } from '../playwright.config';
import { edgesWithoutRing, horizontalOverflow, undersized } from './support/measure';

/**
 * GLB-02 and ADM-22 on the storefront: the header's logo and its fallback, and the footer's social links.
 *
 * **Serial, and not by preference.** The settings are a mutable fixture on the shared stub
 * (`/__test__/site-settings`), because the thing under test is what the shell does when the logo *changes* —
 * a parallel worker switching it mid-test would be measuring somebody else's state. The same arrangement, and
 * the same reason, as `catalog-availability.e2e.ts`.
 *
 * **What this file can see that the unit suite cannot.** jsdom has no layout and no network, so it cannot see
 * an image that does not load, a logo that grows the header past `--header-height`, a wide logo that pushes
 * the navigation off the end of the row, or a focus ring an ancestor clips away (lesson L4). All four are
 * measured here.
 */
test.describe.configure({ mode: 'serial' });

/** docs/04 §4 and the project's non-negotiable list. */
const BREAKPOINTS = [320, 375, 390, 768, 1024, 1280, 1440, 1920] as const;

/** The states the stub can be put into, named rather than built here. */
type LogoState = 'none' | 'wide' | 'tall';
type SocialState = 'none' | 'one' | 'all';

/**
 * Puts the stub into a named state **before** the page is opened.
 *
 * The switch is made from the test process against the stub directly, not through the page: the storefront
 * reads these settings during the *server* render, so the state has to be in place before `goto`. Setting it
 * afterwards would measure the hydrated client and prove nothing about the served HTML.
 */
async function useSettings(
  page: Page,
  logo: LogoState,
  social: SocialState,
  options: { whatsApp?: string; status?: number; delayMs?: number } = {},
): Promise<void> {
  const query = new URLSearchParams({ logo, social });
  if (options.whatsApp) {
    query.set('whatsApp', options.whatsApp);
  }
  if (options.status) {
    query.set('status', String(options.status));
  }
  if (options.delayMs) {
    query.set('delay', String(options.delayMs));
  }

  const switched = await page.request.get(`${apiURL}/__test__/site-settings?${query}`);
  expect(switched.ok(), 'the stub refused the settings switch').toBe(true);

  // The browser's own /api calls go to the same stub, so the hydrated page agrees with the server.
  await page.route('**/api/**', async (route) => {
    const url = new URL(route.request().url());
    const response = await route.fetch({ url: `${apiURL}${url.pathname}${url.search}` });
    await route.fulfill({ response });
  });
}

/**
 * Restores the honest default, so a failure here cannot leak into another file's run.
 *
 * It clears the fault modes as well as the values, because a run that ended mid-fault would otherwise hand
 * the next one a stub that answers 500 to every page's settings read — the hazard `catalog-api-stub.mjs`
 * records for the departments fault, which is why that one is reset by its own readiness probe.
 */
test.afterEach(async ({ page, request }) => {
  // The route handler goes first, and it is not boilerplate: the "answer that never comes" test ends while
  // the stub is still holding a reply, so the browser's `**/api/**` handler is mid-`route.fetch` at teardown
  // and throws "Test ended" — a worker-level error that fails the run with every test passing. Playwright's
  // own remedy, and the only one that does not involve making the test wait for a request it is proving the
  // page does not wait for.
  await page.unrouteAll({ behavior: 'ignoreErrors' });

  await request.get(`${apiURL}/__test__/site-settings?logo=none&social=none`);
});

test.describe('the header wordmark and logo (GLB-02)', () => {
  /**
   * **The fallback, and it is the normal state.** Nothing is uploaded on a fresh deployment, so the text
   * wordmark is what most visitors see — which is why it is the first thing asserted and why the assertion
   * includes the *absence* of an image. "The wordmark is there" would also pass for a header that drew both.
   */
  test('with no logo set the header shows the text wordmark and no image', async ({ page }) => {
    await useSettings(page, 'none', 'none');
    await page.goto('/');

    await expect(page.locator('[data-testid="wordmark-text"]')).toHaveText('Colombo Wardrobe');
    await expect(page.locator('[data-testid="logo"]')).toHaveCount(0);

    // And the anchor is still the way home, with a real tap target.
    const wordmark = page.locator('[data-testid="wordmark"]');
    await expect(wordmark).toHaveAttribute('href', '/');

    const box = await wordmark.boundingBox();
    expect(box!.height).toBeGreaterThanOrEqual(44);
  });

  /**
   * The served HTML carries it, not just the hydrated page. `page.goto` with JavaScript still on would not
   * tell the two apart, so the markup is read off the response body — which is what a crawler and a
   * first-paint both see (GLB-11).
   */
  test('the wordmark is in the server-rendered HTML, not added by hydration', async ({ page }) => {
    await useSettings(page, 'none', 'none');

    const response = await page.goto('/');
    const html = await response!.text();

    expect(html).toContain('data-testid="wordmark-text"');
    expect(html).not.toContain('data-testid="logo"');
  });

  test('once a logo is set the header draws it instead of the text', async ({ page }) => {
    await useSettings(page, 'wide', 'none');

    const response = await page.goto('/');

    // Server-rendered, for the same reason: a logo that only appears after hydration is a logo that flashes.
    expect(await response!.text()).toContain('data-testid="logo"');

    const logo = page.locator('[data-testid="logo"]');
    await expect(logo).toBeVisible();
    await expect(page.locator('[data-testid="wordmark-text"]')).toHaveCount(0);

    // **The image really loaded.** `toBeVisible` passes for a broken image — it has a box — so this asks the
    // element itself, which is the only thing that can tell a drawn logo from a broken-image glyph. This is
    // the assertion the whole fallback exists to make unnecessary.
    expect(
      await logo.evaluate((img: HTMLImageElement) => img.complete && img.naturalWidth > 0),
      'the logo element did not decode an image',
    ).toBe(true);
  });

  /**
   * **An admin can upload any image without breaking the layout**, which is the property that makes this
   * screen safe to hand to a client. The header sizes the logo; the file does not.
   *
   * `tall` is a 200x600 portrait image — the shape that would grow the header and silently invalidate
   * `--header-height` in tokens.css, whose value anchor jumps and focused controls clear.
   */
  test('a tall logo does not grow the header', async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 900 });

    await useSettings(page, 'none', 'none');
    await page.goto('/');
    const withoutLogo = (await page.locator('sd-site-header').boundingBox())!.height;

    await useSettings(page, 'tall', 'none');
    await page.goto('/');
    const withTallLogo = (await page.locator('sd-site-header').boundingBox())!.height;

    expect(
      Math.abs(withTallLogo - withoutLogo),
      `the header is ${withTallLogo}px with a 200x600 logo and ${withoutLogo}px without one; `
        + 'the logo must be sized by the header, not by the file',
    ).toBeLessThanOrEqual(2);

    // And the image is letterboxed rather than stretched: the drawn box keeps its own aspect ratio.
    const drawn = await page.locator('[data-testid="logo"]').boundingBox();
    expect(drawn!.height).toBeLessThanOrEqual(40);
  });

  /**
   * A wide logo must not push the navigation off the end. From 1024 px the header bar is a `nowrap` flex
   * row, so an unbounded image would do exactly that — and the five nav entries are the whole shop.
   */
  test('a wide logo leaves the navigation in place at every desktop width', async ({ page }) => {
    await useSettings(page, 'wide', 'none');

    for (const width of [1024, 1280, 1440, 1920] as const) {
      await page.setViewportSize({ width, height: 900 });
      await page.goto('/');

      // `allInnerTexts` returns *rendered* text and the links are `text-transform: uppercase`, so the
      // comparison is case-insensitive: the question here is which entries are on the row, not how the
      // stylesheet cases them. (The first version of this test asserted the source casing and failed on
      // "WOMEN" — a correct page and a wrong test.)
      const labels = await page.locator('sd-main-nav .site-nav__link').allInnerTexts();
      expect(
        labels.map((label) => label.trim().toLowerCase()),
        `navigation at ${width}px`,
      ).toEqual(['women', 'men', 'kids', 'new arrivals', 'sale']);

      expect(await horizontalOverflow(page), `horizontal overflow at ${width}px`).toBeLessThanOrEqual(0);
    }
  });

  /**
   * A logo whose asset has no alt text falls back to the shop's name, because this image *is* the site's name
   * (GLB-12, WCAG 2.2 AA 1.1.1). `wide` is the state whose `altText` is null, which is the ordinary case —
   * nothing forces a staff user to type one.
   */
  test('a logo with no alt text is still labelled, and axe agrees', async ({ page }) => {
    await useSettings(page, 'wide', 'none');
    await page.goto('/');

    await expect(page.locator('[data-testid="logo"]')).toHaveAttribute('alt', 'Colombo Wardrobe');

    const results = await new AxeBuilder({ page })
      .withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa'])
      .include('header')
      .analyze();

    const serious = results.violations.filter(
      (violation) => violation.impact === 'serious' || violation.impact === 'critical',
    );

    expect(serious, JSON.stringify(serious, null, 2)).toEqual([]);
  });

  /**
   * Lesson L4: a focus ring that an ancestor clips away computes as present. The wordmark is the first
   * focusable thing in the header and the logo changes what is inside it, so the ring is re-measured for real
   * in both states.
   */
  for (const logo of ['none', 'wide'] as const) {
    test(`the wordmark keeps a visible focus ring with the logo ${logo}`, async ({ page }) => {
      await page.setViewportSize({ width: 1280, height: 900 });
      await useSettings(page, logo, 'none');
      await page.goto('/');

      const wordmark = page.locator('[data-testid="wordmark"]');
      await wordmark.focus();

      expect(await edgesWithoutRing(page, wordmark)).toEqual([]);
    });
  }

  /** Every breakpoint, in both states, for overflow and the tap target. */
  for (const logo of ['none', 'wide'] as const) {
    test(`the header fits every breakpoint with the logo ${logo}`, async ({ page }) => {
      await useSettings(page, logo, 'none');

      for (const width of BREAKPOINTS) {
        await page.setViewportSize({ width, height: 900 });
        await page.goto('/');

        expect(await horizontalOverflow(page), `horizontal overflow at ${width}px`).toBeLessThanOrEqual(0);

        // #80: `undersized` returns [] both when everything passes and when the selector matched nothing, so
        // the guard comes first — per clause, because a compound selector's guard is satisfied by its first.
        const selector = '[data-testid="wordmark"], [data-testid="bag-toggle"]';
        for (const clause of selector.split(',').map((part) => part.trim())) {
          const rendered = await page.evaluate(
            (one) =>
              [...document.querySelectorAll<HTMLElement>(one)].filter(
                (element) => element.getClientRects().length > 0,
              ).length,
            clause,
          );

          expect(
            rendered,
            `nothing rendered matches "${clause}" at ${width}px, so measuring it could not fail`,
          ).toBeGreaterThan(0);
        }

        expect(await undersized(page, selector), `tap targets at ${width}px`).toEqual([]);
      }
    });
  }
});

test.describe('the footer social links (GLB-07, ADM-22)', () => {
  /**
   * **The defect this task removes.** The footer used to carry three icons pointing at facebook.com,
   * instagram.com and tiktok.com — the networks' own front pages, not this shop's profiles. A social icon on a
   * live shop that does not go to the shop makes the shop look abandoned.
   *
   * The list element must be absent rather than empty: an empty `<ul>` keeps its `margin-top` and leaves a
   * gap under the opening hours, which is a visual defect only a real layout can show.
   */
  test('with nothing configured the footer shows no social icons and no gap', async ({ page }) => {
    await useSettings(page, 'none', 'none');
    await page.goto('/');

    await expect(page.locator('[data-testid="social-links"]')).toHaveCount(0);
    await expect(page.locator('.site-footer__social a')).toHaveCount(0);

    // Nothing anywhere on the page links to a network's front page, which is what the placeholders were.
    const hrefs = await page.locator('footer a').evaluateAll((links) =>
      links.map((link) => link.getAttribute('href') ?? ''),
    );

    for (const href of hrefs) {
      expect(href).not.toMatch(/^https:\/\/(www\.)?(facebook|instagram|tiktok)\.com\/?$/);
    }
  });

  test('only the profiles the client supplied are drawn, and they open safely', async ({ page }) => {
    await useSettings(page, 'none', 'one');
    await page.goto('/');

    const links = page.locator('.site-footer__social a');
    await expect(links).toHaveCount(1);

    await expect(links.first()).toHaveAttribute('href', 'https://www.instagram.com/colombowardrobe');
    await expect(links.first()).toHaveAttribute('rel', /noopener/);
    await expect(links.first()).toHaveAttribute('target', '_blank');

    // An accessible name, because the icon is aria-hidden and the link would otherwise have none.
    await expect(links.first().locator('.sr-only')).toHaveText('Colombo Wardrobe on Instagram');
  });

  test('all three are drawn when all three are supplied', async ({ page }) => {
    await useSettings(page, 'none', 'all');
    await page.goto('/');

    const hrefs = await page.locator('.site-footer__social a').evaluateAll((links) =>
      links.map((link) => link.getAttribute('href') ?? ''),
    );

    expect(hrefs).toEqual([
      'https://www.facebook.com/colombowardrobe',
      'https://www.instagram.com/colombowardrobe',
      'https://www.tiktok.com/@colombowardrobe',
    ]);
  });

  test('the social icons meet the tap minimum at every breakpoint', async ({ page }) => {
    await useSettings(page, 'none', 'all');

    for (const width of BREAKPOINTS) {
      await page.setViewportSize({ width, height: 900 });
      await page.goto('/');

      const rendered = await page.evaluate(
        () =>
          [...document.querySelectorAll<HTMLElement>('.site-footer__social a')].filter(
            (element) => element.getClientRects().length > 0,
          ).length,
      );

      // #80 again: three links have to be on screen before measuring them can mean anything.
      expect(rendered, `no social links rendered at ${width}px`).toBe(3);

      expect(await undersized(page, '.site-footer__social a'), `tap targets at ${width}px`).toEqual([]);
      expect(await horizontalOverflow(page), `horizontal overflow at ${width}px`).toBeLessThanOrEqual(0);
    }
  });
});

test.describe('the WhatsApp button (GLB-08, ADM-22)', () => {
  test('uses the configured number when the client has set one', async ({ page }) => {
    await useSettings(page, 'none', 'none', { whatsApp: '94771234567' });
    await page.goto('/');

    await expect(page.locator('sd-whatsapp-button a')).toHaveAttribute(
      'href',
      /^https:\/\/wa\.me\/94771234567\?text=/,
    );
  });

  /**
   * **And the button is still there when nothing is configured**, which is a deliberate difference from the
   * social links. An unset social address hides one icon in the footer; GLB-08's floating button is on every
   * page, and removing it over a setting nobody has filled in would take a scoped feature off the shop. The
   * placeholder number in `SITE_CONFIG` predates this task and is flagged in the journal as the thing to
   * replace with the client's real one.
   */
  test('keeps its documented placeholder number when nothing is configured', async ({ page }) => {
    await useSettings(page, 'none', 'none');
    await page.goto('/');

    await expect(page.locator('sd-whatsapp-button a')).toHaveAttribute('href', /^https:\/\/wa\.me\/\d{7,15}\?/);
  });
});

/**
 * **W2-10's lesson, measured: a shell element must never take the page down.**
 *
 * The settings feed is read during the server render of every page, so a 500 from it is the single most
 * dangerous failure this slice introduces. The page has to render at a real 200 with the text wordmark — the
 * same rendering an unconfigured shop gets, which is why there is no second path to go wrong.
 */
test.describe('when the settings cannot be read', () => {
  test('the page still renders, with the wordmark and no social icons', async ({ page }) => {
    // **The fault is on the stub, not on a `page.route` handler**, and the first version of this test got
    // that wrong: the storefront reads these settings during the *server* render, so routing the browser's
    // call faults a request the browser never makes — the server hands its answer over with the page. The
    // test passed the logo through and asserted the wordmark, and failed with "element not found", which is
    // the right outcome for a test that was measuring the wrong thing.
    //
    // Note that the settings the stub holds are the configured ones: this is a shop that *has* a logo and
    // three social links, and still shows none of them because the read failed. A fault over an empty
    // payload would have proved nothing.
    await useSettings(page, 'wide', 'all', { status: 500 });

    const response = await page.goto('/');

    expect(response!.status(), 'the storefront answered a 5xx of its own').toBe(200);

    await expect(page.locator('h1')).toBeVisible();
    await expect(page.locator('[data-testid="wordmark-text"]')).toHaveText('Colombo Wardrobe');
    await expect(page.locator('.site-footer__social a')).toHaveCount(0);

    // And the rest of the shell is intact: the navigation and the bag are still there.
    await expect(page.locator('sd-main-nav .site-nav__link')).toHaveCount(5);
    await expect(page.locator('[data-testid="bag-toggle"]')).toBeVisible();
  });

  test('a page whose settings call never answers still renders', async ({ page }) => {
    // An answer that never comes, which `catchError` cannot see — the W2-03 lesson the two-second timeout
    // exists for. Held for longer than SITE_SETTINGS_TIMEOUT_MS, on the stub rather than in the browser, for
    // the reason above.
    await useSettings(page, 'wide', 'all', { delayMs: 5_000 });

    const response = await page.goto('/', { waitUntil: 'load' });

    expect(response!.status()).toBe(200);
    await expect(page.locator('[data-testid="wordmark-text"]')).toHaveText('Colombo Wardrobe');
  });
});
