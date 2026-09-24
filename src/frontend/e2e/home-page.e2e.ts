import { Page, expect, test } from '@playwright/test';

import { apiURL, faultyContentApiURL, faultyContentURL } from '../playwright.config';
import { edgesWithoutRing, horizontalOverflow, undersized } from './support/measure';

/**
 * Browser checks for the storefront homepage (HOME-01..10).
 *
 * `home-page.spec.ts` already pins the section ordering, the empty and disabled cases, the
 * trending links and every branch of the newsletter form in jsdom. What needs a real browser is
 * layout at a real viewport, a real focus model, `prefers-reduced-motion` — which is the whole of
 * HOME-01's video rule — and the HTML the server puts on the wire before a line of script has run.
 *
 * The content comes from `support/catalog-api-stub.mjs`, which is deliberately **richer** than the
 * seeded database: the seeder switches the four artwork sections off because W1-04 has not landed,
 * and a stub that mirrored it would leave the hero, both banners and the gallery untested in the
 * one place their behaviour can be seen.
 */

/** docs/04 §4 and the project's non-negotiable list. */
const BREAKPOINTS = [320, 375, 390, 768, 1024, 1280, 1440, 1920] as const;

/** The width from which the hero may be a video rather than a still (docs/04 §4). */
const VIDEO_WIDTHS = [768, 1024, 1280, 1440, 1920] as const;

const PHONE_WIDTHS = [320, 375, 390] as const;

/**
 * The browser's own `/api` calls go to the storefront's origin, which in production is a reverse
 * proxy in front of the API. Here it is the stub, fetched from the test process.
 */
async function useStubApi(page: Page, origin = apiURL): Promise<void> {
  await page.route('**/api/**', async (route) => {
    const url = new URL(route.request().url());
    const response = await route.fetch({ url: `${origin}${url.pathname}${url.search}` });
    await route.fulfill({ response });
  });
}

/**
 * Waits until the page has hydrated.
 *
 * The homepage is server-rendered, so its headings are on screen before any script has run, and
 * an interaction at that moment is replayed rather than handled. Nothing on this page changes
 * visibly when it hydrates — that is the point of it — so there is no rendered state to wait for.
 * Angular stamps `ng-version` on the root element when it bootstraps, which is the moment the page
 * starts handling its own events.
 */
async function hydrated(page: Page): Promise<void> {
  await expect.poll(() => page.evaluate(() => window.getComputedStyle(document.body).visibility)).toBe(
    'visible',
  );
  await page.waitForFunction(() => {
    const root = document.querySelector('sd-root');
    return !!root && root.hasAttribute('ng-version');
  });
}

test.describe('the page the server sends (HOME-01..10)', () => {
  /**
   * The homepage is the page a crawler and a first-time visitor read, and it is entirely the
   * client's content. If the sections were fetched after hydration, the HTML on the wire would be
   * an empty shell — which is what the transfer-cache bug did to the listing in W2-03.
   */
  test('the server-rendered HTML already carries the sections and the products', async ({
    request,
  }) => {
    const response = await request.get('/');
    expect(response.status()).toBe(200);

    const html = await response.text();

    expect(html).toContain('New season linen'); // HOME-01
    expect(html).toContain('Shop by department'); // HOME-02
    expect(html).toContain('New arrivals'); // HOME-03
    expect(html).toContain('Trending now'); // HOME-04
    expect(html).toContain('Mid-season sale'); // HOME-05
    expect(html).toContain('Activewear'); // HOME-06
    expect(html).toContain('Follow us'); // HOME-08
    expect(html).toContain('Island-wide delivery'); // HOME-09
    expect(html).toContain('Newsletter'); // HOME-10

    // HOME-03's row is real product cards from the catalogue, in the HTML.
    expect(html).toContain('Test Product 1');
    expect(html).toContain('Rs.');

    // HOME-04's tiles carry the filtered listing each one opens.
    expect(html).toContain('/women/jeans?fit=wide-leg');
  });

  /**
   * The strip above the header is the client's copy too (GLB-03), from the same service.
   *
   * The line asserted here is deliberately **not** one of `PLACEHOLDER_ANNOUNCEMENTS`. The first
   * version of this test looked for the free-delivery line, which is both what the stub serves
   * *and* what the storefront falls back to when the content call fails — so it passed identically
   * whether the feed worked or not, which is no test at all.
   */
  test('the announcement strip is the one the content service sent', async ({ request }) => {
    const html = await (await request.get('/')).text();

    expect(html).toContain('Stitched in Sri Lanka since 2011');
  });

  test('one h1, and it names the store', async ({ page }) => {
    await page.goto('/');

    await expect(page.locator('h1')).toHaveCount(1);
    await expect(page.locator('h1')).toHaveText('Spencer Denim');
  });

  /**
   * The section is switched on and has nothing in it, which is a state the client can reach in
   * admin at any moment. It must draw nothing at all — not a heading, not an empty frame, not a
   * bordered gap.
   */
  test('a section that is on with nothing in it draws nothing', async ({ page }) => {
    await page.goto('/');

    await expect(page.getByText('Nothing to see')).toHaveCount(0);
    await expect(page.locator('sd-feature-banner')).toHaveCount(2);
  });

  test('a tile whose stored link is not safe never reaches an href', async ({ page }) => {
    await page.goto('/');

    const hrefs = await page.locator('a').evaluateAll((links) =>
      links.map((link) => link.getAttribute('href') ?? ''),
    );

    expect(hrefs.some((href) => href.toLowerCase().startsWith('javascript:'))).toBe(false);
    await expect(page.getByText('Unsafe')).toHaveCount(0);
  });
});

test.describe('the hero (HOME-01)', () => {
  /**
   * Autoplay is a request a browser may refuse, and a muted video is the one form of it Chrome
   * grants. The element is never in the server's HTML — the server always renders the still — so
   * it is always created by Angular, and on that path a bare `muted` *attribute* does not set the
   * `muted` **property**: it stayed `false`, Chrome refused, and the hero was a frozen poster.
   *
   * This asserts the property, not the attribute, because the attribute was there all along.
   */
  for (const width of VIDEO_WIDTHS) {
    test(`${width}px: the hero video is muted, which is what lets it autoplay`, async ({ page }) => {
      await page.setViewportSize({ width, height: 900 });
      await useStubApi(page);
      await page.goto('/');
      await hydrated(page);

      const video = page.locator('[data-testid="hero-video"]');
      await expect(video).toHaveCount(1);
      await expect(page.locator('[data-testid="hero-image"]')).toHaveCount(0);

      expect(await video.evaluate((element: HTMLVideoElement) => element.muted)).toBe(true);
      expect(await video.evaluate((element: HTMLVideoElement) => element.autoplay)).toBe(true);
      expect(await video.evaluate((element: HTMLVideoElement) => element.loop)).toBe(true);
    });
  }

  /**
   * The video really runs, and the control really stops it.
   *
   * <b>WCAG 2.2.2</b>: anything that moves on its own for more than five seconds needs a way to
   * stop it, and a looping hero video is exactly that. <b>And the control has to tell the truth
   * about what the video is doing.</b> Its label used to come from a signal hard-coded to
   * "playing", so with autoplay refused it read "Pause" over a stopped video and pressing Pause
   * started it. The label comes from the element's own events now, and this reads it back against
   * `element.paused` rather than against itself.
   */
  test('1280px: the video plays, and the control stops it and says so', async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await useStubApi(page);
    await page.goto('/');
    await hydrated(page);

    const video = page.locator('[data-testid="hero-video"]');
    const toggle = page.locator('[data-testid="hero-toggle"]');
    const paused = () => video.evaluate((element: HTMLVideoElement) => element.paused);

    await expect(toggle).toBeVisible();

    // Actually running, not merely present and configured.
    await expect.poll(paused).toBe(false);
    await expect
      .poll(() => video.evaluate((element: HTMLVideoElement) => element.currentTime))
      .toBeGreaterThan(0);
    await expect(toggle).toHaveAttribute('aria-label', /pause/i);

    await toggle.click();

    await expect.poll(paused).toBe(true);
    await expect(toggle).toHaveAttribute('aria-label', /play/i);

    await toggle.click();

    await expect.poll(paused).toBe(false);
    await expect(toggle).toHaveAttribute('aria-label', /pause/i);
  });

  for (const width of PHONE_WIDTHS) {
    test(`${width}px: a phone gets the still, not the video`, async ({ page }) => {
      await page.setViewportSize({ width, height: 900 });
      await useStubApi(page);
      await page.goto('/');
      await hydrated(page);

      await expect(page.locator('[data-testid="hero-video"]')).toHaveCount(0);
      await expect(page.locator('[data-testid="hero-image"]')).toHaveCount(1);
      await expect(page.locator('[data-testid="hero-toggle"]')).toHaveCount(0);
    });
  }

  /**
   * The rule that matters most, at the widths where the video would otherwise play. Measured on
   * the DOM the browser built under the media query, not on a computed style.
   */
  for (const width of [768, 1280, 1920] as const) {
    test(`${width}px: reduced motion gets the still and no video element at all`, async ({
      browser,
    }) => {
      const context = await browser.newContext({
        reducedMotion: 'reduce',
        viewport: { width, height: 900 },
      });
      const page = await context.newPage();
      await useStubApi(page);

      await page.goto('/');
      await hydrated(page);

      await expect(page.locator('[data-testid="hero-video"]')).toHaveCount(0);
      await expect(page.locator('video')).toHaveCount(0);
      await expect(page.locator('[data-testid="hero-image"]')).toHaveCount(1);

      await context.close();
    });
  }

  test('the hero call to action opens the listing its content names', async ({ page }) => {
    await useStubApi(page);
    await page.goto('/');
    await hydrated(page);

    await expect(page.locator('.hero__cta')).toHaveAttribute('href', '/new-arrivals');
  });
});

test.describe('the trending tiles (HOME-04)', () => {
  test('each card opens the matching filtered listing', async ({ page }) => {
    await page.goto('/');

    const tiles = page.locator('[data-testid="trending-tile"]');
    await expect(tiles).toHaveCount(3);

    expect(await tiles.evaluateAll((links) => links.map((link) => link.getAttribute('href')))).toEqual([
      '/women/jeans?fit=wide-leg',
      '/men/shirts?material=linen',
      '/sale?minDiscountPercent=50',
    ]);
  });

  /**
   * The href being right is not the same as the click working: `routerLink` given a whole URL as
   * one command percent-encodes the `?`, and the tile then lands on a path literally called
   * "jeans?fit=wide-leg". This follows the link and reads the address bar.
   */
  test('clicking a card lands on that listing with the filter applied', async ({ page }) => {
    await useStubApi(page);
    await page.goto('/');
    await hydrated(page);

    await page.locator('[data-testid="trending-tile"]').first().click();

    await expect(page).toHaveURL(/\/women\/jeans\?fit=wide-leg$/);
    await expect(page.locator('h1')).toHaveText('Jeans');
  });

  test('a product row links to the rest of the listing it was drawn from', async ({ page }) => {
    await page.goto('/');

    const links = page.locator('[data-testid="section-link"]');
    await expect(links).toHaveCount(2);
    expect(await links.evaluateAll((all) => all.map((link) => link.getAttribute('href')))).toEqual([
      '/new-arrivals',
      '/sale',
    ]);
  });
});

test.describe('the social gallery (HOME-08)', () => {
  test('an external profile opens in a new tab and says so', async ({ page }) => {
    await page.goto('/');

    const link = page.locator('[data-testid="social-link"]').first();

    await expect(link).toHaveAttribute('href', 'https://instagram.com/example');
    await expect(link).toHaveAttribute('target', '_blank');
    await expect(link).toHaveAttribute('rel', 'noopener noreferrer');
    await expect(link).toContainText('opens in a new tab');
  });
});

test.describe('the newsletter signup (HOME-10)', () => {
  test('an address is accepted and confirmed by the server, not by the page', async ({ page }) => {
    await useStubApi(page);
    await page.goto('/');
    await hydrated(page);

    await page.locator('[data-testid="newsletter-email"]').fill('shopper@example.com');
    await page.locator('[data-testid="newsletter-submit"]').click();

    await expect(page.locator('[data-testid="newsletter-done"]')).toContainText(
      'Thank you. Your email address is on the list.',
    );
  });

  test('an address the API refuses is reported, and the form stays', async ({ page }) => {
    await useStubApi(page);
    await page.goto('/');
    await hydrated(page);

    await page.locator('[data-testid="newsletter-email"]').fill('not-an-address');
    await page.locator('[data-testid="newsletter-submit"]').click();

    await expect(page.locator('[data-testid="newsletter-error"]')).toContainText(
      'Enter an email address',
    );
    await expect(page.locator('[data-testid="newsletter-email"]')).toBeVisible();
    await expect(page.locator('[data-testid="newsletter-done"]')).toHaveCount(0);
  });
});

test.describe('when the content service is down', () => {
  /**
   * The homepage's content service is not the catalogue, and the catalogue is what a shopper came
   * for. A 500 there must be a working page at a real 200, never a blank screen and never a
   * status code on the screen.
   */
  test('the page still renders the shell and the way into the shop', async ({ browser }) => {
    const context = await browser.newContext({ baseURL: faultyContentURL });
    const page = await context.newPage();
    await useStubApi(page, faultyContentApiURL);

    const response = await page.goto('/');
    expect(response?.status()).toBe(200);

    await expect(page.locator('h1')).toHaveText('Spencer Denim');
    await expect(page.getByText('We could not load the rest of the home page')).toBeVisible();

    const body = (await page.locator('body').textContent()) ?? '';
    expect(body).not.toContain('500');
    expect(body).not.toContain('Internal Server Error');

    // The department links come from the navigation feed, which is healthy here, and would come
    // from its own seeded fallback if it were not.
    await expect(page.locator('.fallback__links a')).toHaveCount(5);

    await context.close();
  });

  test('the header and the footer are still there', async ({ browser }) => {
    const context = await browser.newContext({ baseURL: faultyContentURL });
    const page = await context.newPage();
    await page.goto('/');

    await expect(page.locator('header')).toBeVisible();
    await expect(page.locator('footer')).toBeVisible();

    await context.close();
  });
});

test.describe('responsive (GLB-09)', () => {
  for (const width of BREAKPOINTS) {
    test(`${width}px: no horizontal scroll`, async ({ page }) => {
      await page.setViewportSize({ width, height: 900 });
      await page.goto('/');
      await expect(page.locator('h1')).toHaveCount(1);

      expect(await horizontalOverflow(page)).toBeLessThanOrEqual(0);
    });
  }

  /**
   * The controls, not the links: a stretched card link is measured by the card it covers, and a
   * section's "view all" is checked separately below against the same 44 px.
   */
  for (const width of [320, 768, 1280] as const) {
    test(`${width}px: every control on the page is at least 44px`, async ({ page }) => {
      await page.setViewportSize({ width, height: 900 });
      await page.goto('/');

      expect(await undersized(page, 'sd-home-page button, sd-home-page input')).toEqual([]);
    });
  }

  /**
   * The exemption the shell's touch-target test now stands on, proved rather than asserted: a
   * product name's own box is one line of text, and the thing a thumb lands on anywhere inside the
   * card is that same link, through its stretched pseudo-element.
   */
  test('a product card name is a stretched link whose target is the whole card', async ({ page }) => {
    await page.setViewportSize({ width: 375, height: 900 });
    await page.goto('/');

    const hit = await page.evaluate(() => {
      const card = document.querySelector('sd-product-card .card');
      const link = card?.querySelector('.card__name a');
      if (!card || !link) {
        return null;
      }

      const box = card.getBoundingClientRect();
      card.scrollIntoView({ block: 'center', behavior: 'instant' });
      const after = card.getBoundingClientRect();

      // The middle of the photograph, which is nowhere near the text of the link.
      const target = document.elementFromPoint(after.x + after.width / 2, after.y + after.height / 4);

      return {
        linkHeight: Math.round(link.getBoundingClientRect().height),
        cardHeight: Math.round(box.height),
        hitIsTheLink: target === link || link.contains(target),
      };
    });

    expect(hit).not.toBeNull();
    // The link's own box really is small, which is why the blanket sweep reported it...
    expect(hit!.linkHeight).toBeLessThan(44);
    // ...and the card really is what a thumb hits, which is why that report was not a defect.
    expect(hit!.cardHeight).toBeGreaterThanOrEqual(44);
    expect(hit!.hitIsTheLink).toBe(true);
  });

  test('the tile and section links are full-height targets', async ({ page }) => {
    await page.setViewportSize({ width: 375, height: 900 });
    await page.goto('/');

    expect(
      await undersized(
        page,
        '[data-testid="section-link"], [data-testid="department-tile"], [data-testid="trending-tile"]',
      ),
    ).toEqual([]);
  });
});

test.describe('focus is visible (GLB-12, lesson L4)', () => {
  /**
   * Measured in pixels rather than read off a declaration. Two of these sit inside a container
   * that clips its overflow — the hero and the gallery cell — which is exactly the shape that
   * cropped three sides off a thumbnail's ring in W2-05 while a computed-style assertion passed.
   */
  const CONTROLS = [
    ['a department tile', '[data-testid="department-tile"]'],
    ['a trending tile', '[data-testid="trending-tile"]'],
    ['a section link', '[data-testid="section-link"]'],
    ['a gallery link', '[data-testid="social-link"]'],
    ['the newsletter box', '[data-testid="newsletter-email"]'],
    ['the newsletter button', '[data-testid="newsletter-submit"]'],
  ] as const;

  for (const [name, selector] of CONTROLS) {
    for (const width of [375, 1280] as const) {
      test(`${width}px: ${name} keeps its whole ring`, async ({ page }) => {
        await page.setViewportSize({ width, height: 900 });
        await useStubApi(page);
        await page.goto('/');
        await hydrated(page);

        const control = page.locator(selector).first();
        await control.focus();
        expect(await control.evaluate((element) => element.matches(':focus-visible'))).toBe(true);

        expect(await edgesWithoutRing(page, control)).toEqual([]);
      });
    }
  }

  /** Both hero controls sit over a photograph inside a box that clips its overflow. */
  for (const [name, selector] of [
    ['the hero call to action', '.hero__cta'],
    ['the hero pause control', '[data-testid="hero-toggle"]'],
  ] as const) {
    test(`1280px: ${name} keeps its whole ring`, async ({ page }) => {
      await page.setViewportSize({ width: 1280, height: 900 });
      await useStubApi(page);
      await page.goto('/');
      await hydrated(page);

      const control = page.locator(selector).first();
      await control.focus();
      expect(await control.evaluate((element) => element.matches(':focus-visible'))).toBe(true);

      expect(await edgesWithoutRing(page, control)).toEqual([]);
    });
  }

  /**
   * Under reduced motion the ring must still be visible: the blanket 0.01ms override in the reset
   * puts every animatable property on a near-instant transition, and lesson L3 is what happens
   * when something is acted on before it has finished being revealed.
   */
  test('1280px: the ring is still visible under reduced motion', async ({ browser }) => {
    const context = await browser.newContext({
      reducedMotion: 'reduce',
      viewport: { width: 1280, height: 900 },
    });
    const page = await context.newPage();
    await useStubApi(page);
    await page.goto('/');
    await hydrated(page);

    const control = page.locator('[data-testid="department-tile"]').first();
    await control.focus();

    expect(await edgesWithoutRing(page, control)).toEqual([]);

    await context.close();
  });
});

test.describe('the page does not break itself', () => {
  test('no console errors and no failed requests on a full render', async ({ page }) => {
    const problems: string[] = [];

    page.on('console', (message) => {
      if (message.type() === 'error') {
        problems.push(`console: ${message.text()}`);
      }
    });
    page.on('requestfailed', (request) => {
      // The hero's data: video is a stand-in the browser cannot decode; nothing else may fail.
      if (!request.url().startsWith('data:')) {
        problems.push(`request: ${request.url()}`);
      }
    });

    await useStubApi(page);
    await page.goto('/');
    await hydrated(page);

    expect(problems).toEqual([]);
  });

  /**
   * The server fetched the homepage while rendering and hands it to the browser, so hydration
   * must not fetch it again — and must not replace a page of real content with a loading state on
   * its way past.
   */
  test('hydration does not fetch the homepage a second time', async ({ page }) => {
    const calls: string[] = [];
    page.on('request', (request) => {
      if (request.url().includes('/api/content/home')) {
        calls.push(request.url());
      }
    });

    await useStubApi(page);
    await page.goto('/');
    await hydrated(page);

    await expect(page.locator('[data-testid="department-tile"]')).toHaveCount(3);
    expect(calls).toEqual([]);
  });
});
