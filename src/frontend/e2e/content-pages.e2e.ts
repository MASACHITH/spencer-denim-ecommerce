import { BrowserContext, Page, expect, test } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';

import { contentPagesApiURL, contentPagesURL } from '../playwright.config';
import {
  carriesAny,
  holdScripts,
  isSameDocument,
  markDocument,
  recordRequests,
} from './support/hold-scripts';
import { horizontalOverflow, undersized, unreachable } from './support/measure';

/**
 * Browser checks for the content pages and the contact form (W2-11, GLB-10).
 *
 * `content-page.spec.ts`, `contact-panel.spec.ts` and the guard specs pin the states and the markup in
 * jsdom. What needs a real browser is what jsdom has no opinion about: **the HTML the server puts on the
 * wire before a line of JavaScript has run** — which for a policy page is the whole point, because a
 * crawler reads that and nothing else — the status codes an unpublished page and a mistyped address
 * answer, layout at the eight project widths, real tap targets, and what a thumb actually hits where the
 * WhatsApp float overlaps the form (docs/04 §4).
 *
 * The content comes from `support/catalog-api-stub.mjs`, whose `/__test__/content-pages` switch names a
 * published set so a test names a *state* and that file owns what the state's bytes are.
 *
 * **It runs on its own pair of servers, serially, and both halves of that are load-bearing.**
 * `/__test__/content-pages` and `/__test__/site-settings` set module-level variables in one stub
 * process, and `fullyParallel` is on. On the main pair, a test here that published all six pages
 * published them for every test running at that moment in every other file — the W2-11 tester measured
 * the full suite at 856/7 and then 859/4 with a *different* set of failures, against 863/0 at
 * `--workers=1`, and no product defect behind any of it. So the pair isolates this file from the rest
 * of the suite (`contentPagesURL`, declared in `playwright.config.ts` with the reasoning), and the
 * serial mode isolates these tests from each other.
 *
 * The cost of serial mode is that a failure skips the tests after it, which is a real loss in a file
 * this size and is the reason to say so here: when one of these fails, re-run the file alone before
 * concluding anything about the ones that did not run.
 */
test.describe.configure({ mode: 'serial' });

// Every test in this file talks to its own storefront, not the shared one.
test.use({ baseURL: contentPagesURL });

/** docs/04 §4 and the project's non-negotiable list. */
const BREAKPOINTS = [320, 375, 390, 768, 1024, 1280, 1440, 1920] as const;

/** Sets which pages the stub reports as published. The switch is reset by every test that uses it. */
async function publish(
  page: Page,
  state: 'none' | 'all' | 'store' | 'contact' | 'custom' | 'hostile',
): Promise<void> {
  const response = await page.request.get(`${contentPagesApiURL}/__test__/content-pages?state=${state}`);
  expect(response.ok(), 'the content-pages switch did not answer').toBe(true);
}

/** Fails the content feed, so the storefront cannot say whether an address is a page (#30). */
async function failContentFeed(page: Page): Promise<void> {
  const response = await page.request.get(`${contentPagesApiURL}/__test__/content-pages?state=all&status=500`);
  expect(response.ok()).toBe(true);
}

/** Sets which contact details the stub reports, for the Contact page's own panel. */
async function contactDetails(page: Page, state: 'none' | 'set' | 'map' | 'evil'): Promise<void> {
  const response = await page.request.get(`${contentPagesApiURL}/__test__/site-settings?contact=${state}`);
  expect(response.ok()).toBe(true);
}

/**
 * Sends the browser's own `/api` calls to the stub, so a form submitted in the page reaches it.
 *
 * `route.fetch` + `route.fulfill` rather than a proxy, for the reason `bag.e2e.ts` gives: the request
 * carries the page's cookies and the answer comes back as though from the page's own origin.
 */
async function useStubApi(page: Page): Promise<void> {
  await page.route('**/api/**', async (route) => {
    const url = new URL(route.request().url());
    const response = await route.fetch({ url: `${contentPagesApiURL}${url.pathname}${url.search}` });
    await route.fulfill({ response });
  });
}

test.describe('an unpublished page does not leak (GLB-10)', () => {
  test.beforeEach(async ({ page }) => {
    await publish(page, 'none');
  });

  /**
   * **The defining constraint of this task, on the wire.**
   *
   * All six system pages ship with empty bodies, and five of them ship unpublished — doc 11 P13: their
   * copy is the client's, and Privacy and Terms need a PDPA review. So on the day the shop opens,
   * `/privacy` is an address the shop does not have, and it must answer a **real 404** rather than the
   * home page at 200, which is #28's own defect, and rather than a 503, which would tell a crawler to
   * come back.
   *
   * Contact is the exception in the seed — it ships *on* the shop, because its content is the form and
   * the settings rather than copy (W2-11) — but it is in this loop all the same, because what the loop
   * measures is the behaviour of an unpublished page and the stub is holding all six unpublished for it.
   * The state is named by the `beforeEach`, not inherited from the seeder.
   */
  for (const slug of ['about', 'contact', 'returns', 'shipping', 'privacy', 'terms']) {
    test(`/${slug} answers a real 404 while it is unpublished`, async ({ page }) => {
      const response = await page.goto(`/${slug}`);

      expect(response?.status(), `/${slug} did not answer 404`).toBe(404);
      await expect(page.locator('h1')).toContainText('could not find that page');

      // A 404 must never be indexed, whatever a crawler makes of the status code.
      await expect(page.locator('meta[name="robots"]')).toHaveAttribute('content', 'noindex');
    });
  }

  /**
   * And the footer does not link one, which is the other half of "must not appear in navigation".
   *
   * Six hard-coded links would have been six 404s on every page of the shop, which is worse than no
   * link at all — the same argument ADM-22 made about three social icons pointing at the networks'
   * own front pages.
   */
  test('the footer links no policy page while none is published', async ({ page }) => {
    await page.goto('/');

    const footer = page.locator('footer');
    await expect(footer).toBeVisible();

    for (const slug of ['about', 'contact', 'returns', 'shipping', 'privacy', 'terms']) {
      await expect(
        footer.locator(`a[href="/${slug}"]`),
        `the footer links /${slug}, which answers 404`,
      ).toHaveCount(0);
    }
  });

  /**
   * `sitemap.xml` does not offer one either. A draft privacy policy that a crawler indexes is the
   * sharpest form of this leak.
   */
  test('sitemap.xml offers no unpublished page', async ({ page }) => {
    const response = await page.request.get('/sitemap.xml');

    expect(response.status()).toBe(200);

    const xml = await response.text();
    for (const slug of ['about', 'contact', 'returns', 'shipping', 'privacy', 'terms']) {
      expect(xml, `sitemap.xml offers /${slug}, which answers 404`).not.toContain(`/${slug}</loc>`);
    }

    // And it is not an empty document: the catalogue is still in it, so a short sitemap would be a
    // failure of this test rather than of the shop.
    expect(xml).toContain('<loc>');
  });
});

test.describe('a published page is served (GLB-10)', () => {
  test.beforeEach(async ({ page }) => {
    await publish(page, 'all');

    // The browser's own `/api` calls go to the same stub, so the hydrated page agrees with the server —
    // the arrangement `site-branding.e2e.ts` uses and for the same reason. Without it the storefront
    // origin answers 404 for `/api/...` (it proxies nothing; Caddy does in production), which is how
    // the missing page-body handover was found: the browser re-fetched the page and replaced a rendered
    // policy page with a 404.
    await useStubApi(page);
  });

  test.afterEach(async ({ page }) => {
    await publish(page, 'none');
  });

  /**
   * **The server-rendered HTML carries the page**, before a line of JavaScript has run.
   *
   * This is what a crawler reads and the only thing it reads, so it is checked on the raw bytes rather
   * than on the hydrated DOM — the distinction W2-10 built the rest of the SEO work on.
   */
  test('the body is in the HTML on the wire, with the title and the description', async ({ page }) => {
    const response = await page.goto('/about');

    expect(response?.status()).toBe(200);

    const html = (await response?.text()) ?? '';
    expect(html).toContain('Our story');
    expect(html).toContain('We began in');
    expect(html).toContain('<h2>Our story</h2>');

    await expect(page).toHaveTitle(/About us/);
    await expect(page.locator('meta[name="description"]')).toHaveAttribute(
      'content',
      'How Colombo Wardrobe began, and what we make.',
    );
    await expect(page.locator('link[rel="canonical"]')).toHaveAttribute('href', '/about');

    // No `noindex`: a published policy page is exactly what should be indexed.
    await expect(page.locator('meta[name="robots"]')).toHaveCount(0);
  });

  test('the footer links every published page, and sitemap.xml offers them', async ({ page }) => {
    await page.goto('/');

    const footer = page.locator('footer');
    for (const slug of ['about', 'contact', 'returns', 'shipping', 'privacy', 'terms']) {
      // At least one, not exactly one: /privacy is linked twice on purpose — once in the About column
      // and once inside the newsletter's consent wording, which is GLB-10's own TODO in `site-footer.ts`
      // being answered. An exact count would fail on a deliberate second link.
      await expect(footer.locator(`a[href="/${slug}"]`).first()).toBeVisible();
    }

    // **The sitemap is deliberately not asserted here, and it is covered rather than forgotten.**
    // `SitemapDocument` caches its rendered document for CACHE_SECONDS (fifteen minutes) inside the SSR
    // process, which outlives a Playwright test — so whichever test publishes first decides what every
    // later one sees, and an assertion either way would be an assertion about test order. The chain is
    // proved in two places that can be deterministic: `ContentPageEndpointTests` shows the feed
    // excluding every unpublished page against a real database, and `sitemap-xml.spec.ts` shows the
    // document listing exactly what the feed says and dropping a slug that is not a slug. The
    // unpublished half above runs before any publish in this file and is the one direction that is
    // safe to measure here.
  });

  /**
   * W2-10's rule, which this route must not break: a mistyped address answers a real 404 rather than
   * quietly showing the home page. `/abuot` is one segment, which is exactly the space the content
   * route claims.
   */
  test('a mistyped single segment still answers a real 404', async ({ page }) => {
    const response = await page.goto('/abuot');

    expect(response?.status()).toBe(404);
    await expect(page.locator('h1')).toContainText('could not find that page');
  });

  test('no page scrolls sideways at any of the eight widths', async ({ page }) => {
    await page.goto('/about');

    for (const width of BREAKPOINTS) {
      await page.setViewportSize({ width, height: 900 });
      await expect(page.locator('h1')).toBeVisible();

      expect(await horizontalOverflow(page), `horizontal scroll at ${width}px`).toBeLessThanOrEqual(1);
    }
  });

  /**
   * A shipping page is mostly a table of zones and fees, and a wide one must not make the whole
   * document scroll (GLB-09). The wrapper a body would need cannot be added — the sanitiser's
   * allow-list permits no markup around the table — so the table itself scrolls.
   */
  test('a wide table inside a body scrolls itself rather than the page', async ({ page }) => {
    await page.setViewportSize({ width: 320, height: 720 });
    await page.goto('/about');

    const table = page.locator('.rich--page table');
    await expect(table).toBeVisible();

    // **Measured, not read off a declaration.** The first version asserted
    // `getComputedStyle(table).overflowX === 'auto'`, which is a statement about a stylesheet rather
    // than about the screen — the shape of assertion this project has been caught by before ("every
    // control is 44 px" passing while 17 of them were buried). What the requirement says is that the
    // *table* scrolls and the *document* does not, so that is what is measured.
    //
    // The fixture's table is deliberately five columns of long text, because a table that fits every
    // viewport makes this assertion unable to fail — which the first fixture did.
    const measured = await page.evaluate(() => {
      const element = document.querySelector('.rich--page table') as HTMLElement | null;

      return {
        heading: document.querySelector('h1')?.textContent ?? 'none',
        scrollWidth: element?.scrollWidth ?? -1,
        clientWidth: element?.clientWidth ?? -1,
      };
    });

    expect(
      measured.scrollWidth,
      `the table does not scroll itself: ${JSON.stringify(measured)}`,
    ).toBeGreaterThan(measured.clientWidth);

    expect(await horizontalOverflow(page), 'the document scrolls sideways').toBeLessThanOrEqual(1);
  });

  /**
   * **Hydration does not re-fetch the page, and must not be able to take it away.**
   *
   * The server renders the page and hands its answer to the browser with it. Without that, the browser
   * asked again on hydration — and a second ask that fails renders the missing state, so a transient
   * blip replaced a perfectly good server-rendered policy page with a 404 in the shopper's browser,
   * seconds after they had read it. This is `ServerHandover`'s own recorded lesson, one feature over.
   *
   * The assertion is on the *requests*: the body is in the handover, so there is no second call to fail.
   */
  test('hydration does not ask for the page again', async ({ page }) => {
    const asked: string[] = [];
    page.on('request', (request) => {
      if (request.url().includes('/api/content/pages/')) {
        asked.push(request.url());
      }
    });

    await page.goto('/about');
    await expect(page.locator('h1')).toContainText('About us');

    // Long enough for hydration to have run and for any second fetch to have been made.
    await page.waitForTimeout(1_500);

    expect(asked, 'the browser re-fetched a page the server had already rendered').toEqual([]);
    await expect(page.locator('h1')).toContainText('About us');
  });

  test('the page has no axe violations', async ({ page }) => {
    await page.goto('/about');
    await expect(page.locator('h1')).toBeVisible();

    const results = await new AxeBuilder({ page })
      .withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa'])
      .analyze();

    expect(
      results.violations,
      JSON.stringify(results.violations.map((violation) => violation.id), null, 2),
    ).toEqual([]);
  });
});

test.describe('#30, in the single-segment space (GLB-10)', () => {
  test.afterEach(async ({ page }) => {
    await publish(page, 'none');
  });

  /**
   * **A page the storefront could not look up answers 503, not 404.**
   *
   * The content feed did not answer, so the storefront cannot say whether `/returns` is a published
   * policy page. A 404 with `noindex` would drop a live one out of the index for the length of an API
   * wobble, where a 503 with `Retry-After` is retried and keeps the URL — which is #30's own lesson, in
   * the one place this feature can repeat it.
   */
  test('a single segment answers 503 with Retry-After when the content feed is down', async ({
    page,
  }) => {
    await failContentFeed(page);

    const response = await page.goto('/returns');

    expect(response?.status()).toBe(503);
    expect(response?.headers()['retry-after']).toBeDefined();
    await expect(page.locator('h1')).toContainText('could not load this page just now');

    // A 503 asking to be deindexed is the exact outcome the status code exists to avoid.
    await expect(page.locator('meta[name="robots"]')).toHaveCount(0);
  });
});

test.describe('the contact form (GLB-10)', () => {
  test.beforeEach(async ({ page }) => {
    await publish(page, 'contact');
    await contactDetails(page, 'set');
    await useStubApi(page);
  });

  test.afterEach(async ({ page }) => {
    await publish(page, 'none');
    await contactDetails(page, 'none');
  });

  /**
   * The form works, and **nothing it says promises an email**.
   *
   * Doc 11 P15's notifications are "none in v1" and there is no mail transport in this build. Checkout
   * shipped a promise of an email nothing could send and it was a Major finding, so the confirmation is
   * read and checked rather than assumed.
   */
  test('an enquiry is sent, confirmed, and no email is promised anywhere', async ({ page }) => {
    await page.goto('/contact');

    await page.fill('#contact-name', 'Nimal Perera');
    await page.fill('#contact-email', 'nimal@example.com');
    await page.fill('#contact-message', 'Is the linen shirt true to size?');
    await page.click('[data-testid="contact-submit"]');

    const confirmation = page.locator('[data-testid="contact-sent"]');
    await expect(confirmation).toBeVisible();

    // **Nothing in this system sends an email**, so nothing on this page may say one is coming (the
    // brief's own words, after checkout shipped a promise nothing could keep and it was a Major
    // finding).
    //
    // The first version of this list held five needles and **none of them matched "we will send you an
    // email"**, which is the most natural phrasing of the very failure it exists to prevent — found by
    // the W2-11 tester. So it now matches on the phrases a person would actually write, in both the
    // "we send" and the "you receive" directions, plus the Sinhala-English "mail you" that is common
    // here. Each needle is lower-case and the page text is lower-cased once.
    const wholePage = ((await page.locator('body').textContent()) ?? '').toLowerCase();
    for (const promise of [
      'email you',
      'emailed',
      'e-mailed',
      'mail you',
      'send you an email',
      'send you a mail',
      'sending you an email',
      'will send an email',
      'send a confirmation',
      'confirmation email',
      'confirmation has been sent',
      'check your inbox',
      'check your email',
      'in your inbox',
      'reply by email',
      'respond by email',
      'an email is on its way',
      'you will hear from us by email',
    ]) {
      expect(wholePage, `the page promises "${promise}"`).not.toContain(promise);
    }

    // And the one that cannot be written as a phrase: a reply time. Nothing measures it and nobody
    // agreed to it.
    expect(wholePage).not.toMatch(/within \d+ (working )?(hour|day)/);
    expect(wholePage).not.toContain('within one working day');

    // The enquiry really reached the API, which is the half a jsdom test cannot see.
    const submissions = await (await page.request.get(`${contentPagesApiURL}/__test__/contact-submissions`)).json();
    expect(submissions.at(-1)).toMatchObject({ name: 'Nimal Perera', discarded: false });
  });

  /**
   * **Focus is re-homed when the form is replaced by its confirmation.**
   *
   * The submit button the visitor pressed is inside the block that is removed, so destroying it would
   * drop focus to `<body>` — the same harm as `[disabled]` (#109) from the other direction, and the
   * rule the project records as necessary and not sufficient.
   */
  test('focus lands on the confirmation rather than on the body', async ({ page }) => {
    await page.goto('/contact');

    await page.fill('#contact-name', 'Nimal Perera');
    await page.fill('#contact-email', 'nimal@example.com');
    await page.fill('#contact-message', 'A question.');
    await page.click('[data-testid="contact-submit"]');

    await expect(page.locator('[data-testid="contact-sent"]')).toBeVisible();

    const focused = await page.evaluate(() => ({
      tag: document.activeElement?.tagName ?? 'none',
      testid: document.activeElement?.getAttribute('data-testid') ?? '',
    }));

    expect(focused.tag, 'focus fell to the body when the form was replaced').not.toBe('BODY');
    expect(focused.testid).toBe('contact-sent');
  });

  /**
   * The submit button carries `aria-disabled` rather than `disabled` (#109).
   *
   * Playwright's `toBeDisabled()` honours `aria-disabled`, so this asserts the same property a
   * screen reader reads — and the handler refuses the second activation, which is what makes the
   * attribute honest rather than decorative.
   */
  test('a refused enquiry keeps the message the visitor typed', async ({ page }) => {
    await page.goto('/contact');

    await page.fill('#contact-name', 'Nimal Perera');
    await page.fill('#contact-email', 'not-an-address');
    await page.fill('#contact-message', 'Four paragraphs of carefully typed question.');
    await page.click('[data-testid="contact-submit"]');

    await expect(page.locator('#contact-email')).toHaveAttribute('aria-invalid', 'true');

    // The one thing a visitor fears on a long form: the message is still there.
    await expect(page.locator('#contact-message')).toHaveValue(
      'Four paragraphs of carefully typed question.',
    );
    await expect(page.locator('[data-testid="contact-sent"]')).toHaveCount(0);
  });

  /** The honeypot is not reachable by a person: not in the tab order, not in the accessibility tree. */
  test('the honeypot is out of the tab order and out of the accessibility tree', async ({ page }) => {
    await page.goto('/contact');

    const honeypot = page.locator('#contact-website');
    await expect(honeypot).toHaveAttribute('tabindex', '-1');
    await expect(honeypot).toHaveAttribute('autocomplete', 'off');

    expect(
      await honeypot.evaluate((input: HTMLElement) =>
        input.closest('[aria-hidden="true"]') !== null,
      ),
      'the honeypot is not inside an aria-hidden wrapper, so a screen reader would offer it',
    ).toBe(true);

    // And it is off-screen rather than `display: none`, because some form-fillers skip a field they
    // can see is not rendered — which is the whole point of the technique.
    expect(
      await honeypot.evaluate((input: HTMLElement) => getComputedStyle(input).display),
    ).not.toBe('none');
  });

  /**
   * **A press of Enter before the page is alive must not put an enquiry in the address (#123).**
   *
   * The form is server rendered, so it is on the screen while the bundle downloads. With no handler yet,
   * the browser used to submit it itself: a `GET` to `/contact` with the shopper's name, email address,
   * telephone number and whatever they wrote — which is the one field on the site where people type
   * something private — in the query string, and so in history, `Referer` and every access log. The form
   * is `method="dialog"`, whose native submit does nothing. The shared account is
   * `pre-hydration-submit.e2e.ts`; this test lives here because the form needs this file's server pair.
   */
  test('an enquiry submitted before the page is alive stays out of the address', async ({ page }) => {
    const release = await holdScripts(page);
    const requests = recordRequests(page);

    await page.goto('/contact', { waitUntil: 'commit' });
    await expect(page.locator('#contact-name')).toBeVisible();
    await markDocument(page);

    await page.fill('#contact-name', 'Nimali Perera');
    await page.fill('#contact-email', 'nimali@example.com');
    await page.fill('#contact-message', 'A private question about an order.');
    await page.locator('#contact-email').press('Enter');
    await page.locator('[data-testid="contact-submit"]').click();

    release();

    // Retried until the component is alive, answering an empty name with the server's own refusal. Pressing
    // Enter before then is the very thing under test, so it is safe to repeat.
    await expect(async () => {
      await page.fill('#contact-name', '');
      await page.locator('#contact-name').press('Enter');
      await expect(page.locator('#contact-name')).toHaveAttribute('aria-invalid', 'true', {
        timeout: 1_000,
      });
    }).toPass();

    expect(
      requests.filter((url) =>
        carriesAny(url, ['Nimali', 'Perera', 'nimali@example.com', 'private question']),
      ),
      'the enquiry reached an address',
    ).toEqual([]);
    expect(new URL(page.url()).pathname + new URL(page.url()).search).toBe('/contact');
    expect(await isSameDocument(page), 'the document was replaced by a native form submission').toBe(true);
  });

  test('the contact details the client has set are shown, and the map is framed', async ({ page }) => {
    await contactDetails(page, 'map');
    await page.goto('/contact');

    await expect(page.locator('.contact__address')).toContainText('42 Galle Road');
    await expect(page.locator('.contact__hours')).toContainText('Monday to Friday');
    // Scoped to the panel: the footer's contact column draws the same number, and it should — so an
    // unscoped count of 1 was asserting that the footer had stopped working.
    await expect(page.locator('.contact__details a[href="tel:+94112345678"]')).toHaveCount(1);

    const map = page.locator('iframe.contact__map');
    await expect(map).toHaveCount(1);

    // **'allow-scripts', and this assertion used to read `toHaveAttribute('sandbox', '')`.** An empty
    // sandbox blocks script, and both permitted map providers draw their tiles with script, so the
    // frame rendered as a blank bordered box and this test locked that in. The W2-11 tester measured
    // it against the live endpoints: 0 tiles with `sandbox=""`, 7 Google tiles and 1 OSM tile with
    // `allow-scripts`.
    //
    // It is asserted as an exact value rather than with `toContain`, because the risk runs both ways:
    // too few tokens and the map is blank, too many and a frame on the shop's own page gains
    // same-origin access, top-level navigation or a form. A real provider cannot be loaded here — the
    // e2e run has no network and the stub serves the URL — so what this can prove is the policy, and
    // the rendering evidence lives in the component's comment with the numbers behind it.
    await expect(map).toHaveAttribute('sandbox', 'allow-scripts');
    await expect(map).toHaveAttribute('referrerpolicy', 'no-referrer');
    await expect(map).toHaveAttribute('title', /map/i);

    // The tokens that must not appear, named one by one, because `allow-scripts` plus any of these is
    // a different and much worse thing than `allow-scripts` on its own.
    const sandbox = (await map.getAttribute('sandbox'))?.split(/\s+/) ?? [];
    for (const forbidden of [
      'allow-same-origin',
      'allow-top-navigation',
      'allow-top-navigation-by-user-activation',
      'allow-forms',
      'allow-popups',
      'allow-modals',
      'allow-downloads',
    ]) {
      expect(sandbox, `the map frame must not be granted ${forbidden}`).not.toContain(forbidden);
    }
  });

  /**
   * **A map address on a host that is not a map provider is not framed at all.**
   *
   * This is the control that matters on `Contact.MapEmbedUrl`: the value becomes an `iframe src` inside
   * a page on the shop's own origin, so whoever can edit settings could otherwise frame a
   * credential-harvesting form under the shop's address bar. The API checks the host on write and on
   * read; the storefront checks it again in the browser's own parser, which is the layer this measures.
   */
  test('a map address on an unknown host is refused rather than framed', async ({ page }) => {
    await contactDetails(page, 'evil');
    await page.goto('/contact');

    await expect(page.locator('h1')).toBeVisible();
    await expect(page.locator('iframe')).toHaveCount(0);
    expect(await page.content()).not.toContain('evil.example');
  });

  /**
   * Nothing is invented when the client has supplied nothing, which is the state a fresh shop is in.
   *
   * The footer keeps a documented placeholder — an empty contact column on every page reads as a broken
   * shop — and the Contact page shows nothing, because it is the one place an invented address could
   * send a person to the wrong building.
   */
  test('no address, no hours and no map when the client has set none', async ({ page }) => {
    await contactDetails(page, 'none');
    await page.goto('/contact');

    await expect(page.locator('[data-testid="contact-submit"]')).toBeVisible();
    await expect(page.locator('.contact__address')).toHaveCount(0);
    await expect(page.locator('.contact__hours')).toHaveCount(0);
    await expect(page.locator('iframe')).toHaveCount(0);
  });

  test('every control is a 44 px target, on screen, at every width', async ({ page }) => {
    await page.goto('/contact');

    for (const width of BREAKPOINTS) {
      await page.setViewportSize({ width, height: 900 });
      await expect(page.locator('[data-testid="contact-submit"]')).toBeVisible();

      expect(await horizontalOverflow(page), `horizontal scroll at ${width}px`).toBeLessThanOrEqual(1);

      // Three distinct questions, and the honeypot is excluded from all of them because it is
      // deliberately 1 px and deliberately unreachable.
      const selector =
        '.contact__submit, .contact__whatsapp, .contact__link, .contact__input, .contact__again';

      expect(
        await undersized(page, selector),
        `controls under 44 px at ${width}px`,
      ).toEqual([]);

      expect(
        await unreachable(page, '.contact__submit'),
        `the submit button cannot be brought on screen at ${width}px`,
      ).toEqual([]);

      // **The fourth question, which none of the three above asks: is anything too *tall*.**
      //
      // 44px is a floor and every check here was a floor, so a control stretched to twice its height
      // passed all of them. One was: from 768px up, the subject input sat in a grid row beside a field
      // with a two-line hint and absorbed the spare height, measuring 71-103px against its siblings'
      // 48. It is a layout defect that looks like a rendering accident and reads as one in a
      // screenshot, so it needs a number. Raised by the W2-11 tester.
      //
      // The textarea is excluded by selector rather than by a height allowance: it is *meant* to be
      // 10rem, and an allowance wide enough for it would be wide enough to hide this.
      const tooTall = await page.evaluate(() =>
        Array.from(document.querySelectorAll('.contact__input:not(.contact__textarea)'))
          .map((element) => ({
            id: element.id,
            height: Math.round((element as HTMLElement).getBoundingClientRect().height),
          }))
          .filter((measured) => measured.height > 60),
      );

      expect(tooTall, `a single-line field is stretched at ${width}px`).toEqual([]);
    }
  });

  /**
   * **docs/04 §4's standing rule, applied to the thing this page adds.**
   *
   * The floating WhatsApp button owns the bottom-right corner site-wide (owner, 2026-10-06), and the
   * rule that decision ends with is that anything placed in a viewport corner is checked with
   * `document.elementFromPoint` across scroll offsets before it lands — "because a CSS assertion says
   * nothing about what a thumb hits".
   *
   * The Contact page's form is the case: a full-width submit button would run under the float at some
   * scroll offsets. It is left-aligned and `fit-content` instead, which is a decision about this corner
   * rather than a style preference, and this is what makes that claim checkable. The assertion is that
   * the element hit **is** the button — "some other thing is on top" is the same dead button.
   *
   * Everything else about the shape of this scan is `quick-view.e2e.ts`'s and `wishlist.e2e.ts`'s: the
   * sticky-header band, requiring the whole box on screen, one `page.evaluate`. Copying it is
   * deliberate — the controls have to be measured the same way or the comparison means nothing.
   */
  for (const [width, height] of [
    [320, 720],
    [390, 844],
  ] as const) {
    test(`${width}x${height}: the WhatsApp float never covers the form's own controls`, async ({
      page,
    }) => {
      await page.setViewportSize({ width, height });
      await page.goto('/contact');

      await expect(page.locator('[data-testid="contact-submit"]')).toBeVisible();
      await expect(page.locator('.wa-float')).toBeVisible();

      const { covered, sampled, offsets } = await page.evaluate(() => {
        const problems: { scrollY: number; label: string; hit: string; probe: string }[] = [];
        const root = document.documentElement;
        let counted = 0;
        let steps = 0;

        const targets = '.contact__submit, .contact__whatsapp, .contact__link';

        for (let offset = 0; offset <= 40 * 40; offset += 40) {
          window.scrollTo(0, offset);
          steps += 1;

          const header = document.querySelector<HTMLElement>('.site-header');
          const headerBottom = header ? header.getBoundingClientRect().bottom : 0;

          [...document.querySelectorAll<HTMLElement>(targets)].forEach((control) => {
            const box = control.getBoundingClientRect();

            if (
              box.top < headerBottom ||
              box.bottom > root.clientHeight ||
              box.left < 0 ||
              box.right > root.clientWidth
            ) {
              return;
            }

            counted += 1;

            const x = box.x + box.width / 2;
            const y = box.y + box.height / 2;
            const hit = document.elementFromPoint(x, y);

            if (hit !== control && !control.contains(hit)) {
              problems.push({
                scrollY: Math.round(window.scrollY),
                label: `${control.tagName}.${control.className}`.slice(0, 50),
                hit: `${hit?.tagName ?? 'nothing'}.${hit?.className ?? ''}`.slice(0, 70),
                probe: `x=${Math.round(x)} y=${Math.round(y)} vw=${root.clientWidth} vh=${root.clientHeight}`,
              });
            }
          });
        }

        window.scrollTo(0, 0);

        return { covered: problems, sampled: counted, offsets: steps };
      });

      expect(offsets, 'no scroll offset was sampled, so this could not fail').toBeGreaterThan(5);
      expect(sampled, 'no control centre was ever tested, so this could not fail').toBeGreaterThan(5);
      expect(covered, JSON.stringify(covered.slice(0, 8), null, 2)).toEqual([]);
    });
  }

  test('the contact page has no axe violations', async ({ page }) => {
    await contactDetails(page, 'map');
    await page.goto('/contact');
    await expect(page.locator('[data-testid="contact-submit"]')).toBeVisible();

    const results = await new AxeBuilder({ page })
      .withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa'])
      .analyze();

    expect(
      results.violations,
      JSON.stringify(results.violations.map((violation) => violation.id), null, 2),
    ).toEqual([]);
  });
});

/**
 * **The render layer, against markup that never went through the sanitiser.**
 *
 * Everything else in this file uses already-clean fixtures, because the API cleans on save — so nothing
 * in the repository was exercising the *second* line of defence. `IHtmlSanitiser`'s remarks say it
 * exists on purpose: the body is bound with `[innerHTML]` and never `bypassSecurityTrustHtml`, so
 * Angular's DOM sanitiser runs again in the browser and during the server render, and that is the layer
 * that still holds for a row written by a support script, a data fix or a restore from a spreadsheet,
 * none of which comes through the API. An untested second line of defence is a hope.
 *
 * The W2-11 tester built a fixture of this shape outside the repository and measured 9 raw payloads, 0
 * rendering dangerously, with the server-rendered bytes clean on the wire too. This is that, inside the
 * repository, so it keeps being measured. The stub's `hostile` state holds the payloads and says why
 * each is there.
 *
 * **Both halves are checked, and the first one is the one a crawler sees.** The server-rendered HTML is
 * read before any JavaScript runs — `page.content()` after a `domcontentloaded` navigation with
 * scripting off — and then the hydrated DOM is read, and a counter the payloads all try to increment is
 * checked to be untouched. A test that only looked at the hydrated DOM would miss a `<script>` that had
 * already executed.
 */
test.describe('a body that never went through the sanitiser still renders safely', () => {
  test.beforeEach(async ({ page }) => {
    await publish(page, 'hostile');
  });

  test.afterEach(async ({ page }) => {
    await publish(page, 'none');
  });

  test('nothing executes, and nothing dangerous reaches the DOM', async ({ page }) => {
    const consoleErrors: string[] = [];
    page.on('pageerror', (error) => consoleErrors.push(error.message));

    await page.goto('/hostile');

    // The page itself rendered: the copy around the payloads is on it, so this is not passing because
    // the page failed to load.
    await expect(page.locator('h1')).toHaveText('Hostile body');
    await expect(page.locator('.rich--page')).toContainText('Before.');
    await expect(page.locator('.rich--page')).toContainText('After.');

    // Not one of the nine payloads ran. They all increment the same counter, so one assertion covers
    // them and names the failure unambiguously when it happens.
    expect(await page.evaluate(() => (window as unknown as { __xss?: number }).__xss ?? 0)).toBe(0);

    // And nothing dangerous is in the hydrated tree, by element and by attribute rather than by
    // searching the HTML text — an `onerror=` inside a text node is harmless and would fail a string
    // search, which is how this kind of test ends up weakened to make it pass.
    const dangerous = await page.evaluate(() => {
      const root = document.querySelector('.rich--page');
      if (!root) {
        return ['the body did not render at all'];
      }

      const found: string[] = [];
      for (const element of Array.from(root.querySelectorAll('*'))) {
        const name = element.tagName.toLowerCase();
        if (['script', 'iframe', 'object', 'embed', 'form', 'input', 'style', 'svg', 'math'].includes(name)) {
          found.push(`<${name}>`);
        }

        for (const attribute of Array.from(element.attributes)) {
          const attributeName = attribute.name.toLowerCase();
          if (attributeName.startsWith('on') || attributeName === 'style') {
            found.push(`${name}[${attributeName}]`);
          }

          // Only the attributes a browser *navigates or loads from*. `srcset` is excluded and checked
          // separately below, because Angular keeps it — see that assertion.
          if (
            ['href', 'src', 'action', 'formaction', 'xlink:href'].includes(attributeName) &&
            /^\s*(javascript|data|vbscript)\s*:/i.test(attribute.value)
          ) {
            found.push(`${name}[${attributeName}]=${attribute.value.slice(0, 20)}`);
          }
        }
      }

      return found;
    });

    expect(dangerous, 'the rendered page holds something a browser would act on').toEqual([]);
    expect(consoleErrors, 'the page threw while rendering a hostile body').toEqual([]);

    // ---------------------------------------------------------------------------------------------
    // **The one thing the render layer does NOT remove, measured rather than assumed.**
    //
    // Angular's DOM sanitiser keeps `srcset`, including a `javascript:` candidate inside it, and this
    // assertion is here to say so rather than to be quietly dropped from the list above. It was found
    // by writing this test: the first version failed on `img[srcset]=javascript:…`.
    //
    // **It is not script execution**, which the counter above has already established: a `srcset`
    // candidate is parsed as an image URL, `javascript:` is not a valid one, and the browser simply
    // fails to load it. What it *is* is a second URL on an element the shop renders, which is exactly
    // why the server's allow-list refuses the attribute (`HtmlSanitiser.AllowedAttributes`) — so a row
    // written through the API never carries one, and this can only be reached by a row written around
    // the API. The residue is one more third-party request from a page an attacker already controls
    // the images of, which is the residue the sanitiser's own remarks record for `src`.
    //
    // Recorded here because an unmeasured second line of defence is a hope, and a measured one with a
    // known hole is a fact somebody can act on.
    const keptBySrcset = await page.evaluate(
      () =>
        Array.from(document.querySelectorAll('.rich--page img[srcset]')).map((image) =>
          image.getAttribute('srcset'),
        ),
    );

    expect(keptBySrcset).toEqual(['javascript:window.__xss = 1']);
  });

  /**
   * The same question asked of the **bytes on the wire**, which is what a crawler and a no-JavaScript
   * visitor get, and the only half that a stored `<script>` could already have used by the time the
   * hydrated DOM is inspected.
   */
  test('the server-rendered HTML carries no script and no handler', async ({ browser }) => {
    const context = await browser.newContext({ javaScriptEnabled: false, baseURL: contentPagesURL });
    const page = await context.newPage();

    try {
      await page.goto('/hostile');

      // The body region's markup, read off the parsed document rather than sliced out of
      // `page.content()`. **Scoped, because the document around it legitimately carries scripts** — the
      // application's own bundle tags — so a whole-document assertion would be asserting the wrong
      // thing and would have to be weakened until it proved nothing. And read through the DOM because
      // slicing the HTML text was worse than weak: the first version searched from the first occurrence
      // of `rich--page`, which is in the inline stylesheet, and failed on the word `<style` in a CSS
      // block.
      //
      // Scripting is off in this context, so nothing has run and the DOM is exactly what the server
      // sent, parsed. That is the distinction this test exists for: a stored `<script>` would already
      // have executed by the time the hydrated DOM in the test above is inspected.
      const region = await page.locator('.rich--page').innerHTML();

      expect(region).toContain('Before.');
      expect(region).toContain('After.');

      for (const fragment of [
        '<script',
        '<iframe',
        '<form',
        '<object',
        '<embed',
        '<style',
        'onerror',
        'onbegin',
        'onload',
      ]) {
        expect(region.toLowerCase(), `the server put "${fragment}" on the wire`).not.toContain(fragment);
      }

      // **`srcset` is kept, on the wire as well as in the hydrated DOM.** Pinned rather than passed
      // over: Angular's sanitiser does not strip it, so the one attribute the server's allow-list
      // refuses is the one that survives this layer. See the previous test for what that is and is not
      // — the counter there shows nothing executes, because a `srcset` candidate is parsed as an image
      // URL and `javascript:` is not a valid one.
      expect(region.toLowerCase()).toContain('srcset');

      // **A `javascript:` href is rewritten rather than removed**, which is Angular's documented
      // behaviour: the value becomes `unsafe:javascript:…`, a scheme no browser knows, so the link is
      // inert and the original is still legible to anyone inspecting the page. That is why this cannot
      // be a plain "does not contain javascript:" — it would fail on the neutralised form and the
      // obvious way to make it pass would be to delete the check.
      //
      // So: every occurrence must be the neutralised one. A bare `javascript:` anywhere still fails.
      const live = region
        .toLowerCase()
        .replaceAll('unsafe:javascript:', '')
        .replace(/srcset="[^"]*"/g, '');

      expect(live, 'a live javascript: URL reached the server-rendered page').not.toContain('javascript:');
      expect(region.toLowerCase()).toContain('unsafe:javascript:');
    } finally {
      await context.close();
    }
  });
});

/**
 * Issue #33: the Shipping and Returns pages as the shop ships them (published, with the owner's starting terms),
 * the live fee list under Shipping, and the product page's links to both.
 */
test.describe('the Shipping and Returns pages and the product page links (#33)', () => {
  test.beforeEach(async ({ page }) => {
    await publish(page, 'all');
    await useStubApi(page);
  });

  test.afterEach(async ({ page }) => {
    await publish(page, 'none');
  });

  test('the product page links to both published pages, and the links land on them', async ({ page }) => {
    await page.goto('/product/test-product-1');

    const delivery = page.locator('[data-testid="pdp-delivery"]');
    await delivery.locator('summary').click();
    const shipping = page.getByRole('link', { name: 'Delivery details' });
    await expect(shipping).toHaveAttribute('href', '/shipping');

    const returns = page.locator('[data-testid="pdp-returns"]');
    await returns.locator('summary').click();
    await expect(page.getByRole('link', { name: 'Returns and exchanges policy' })).toHaveAttribute('href', '/returns');

    await shipping.click();
    await expect(page.getByRole('heading', { level: 1, name: 'Delivery and shipping' })).toBeVisible();
  });

  test('the Shipping page carries its copy and the live Sri Lankan fees under it', async ({ page }) => {
    await page.goto('/shipping');

    await expect(page.getByRole('heading', { level: 2, name: 'Delivery worldwide' })).toBeVisible();
    const rates = page.getByRole('region', { name: 'Current delivery fees in Sri Lanka' });
    await expect(rates).toBeVisible();
    await expect(rates).toContainText('Rs. 400, free on orders of Rs. 10,000 or more after discounts, 1–2 working days');
    await expect(rates).toContainText('Rest of Sri Lanka');
    await expect(rates).toContainText('for your country if you are ordering from overseas');
  });

  test('the Returns page carries its copy and no fee list', async ({ page }) => {
    await page.goto('/returns');

    await expect(page.getByRole('heading', { level: 2, name: 'The short version' })).toBeVisible();
    await expect(page.locator('main')).toContainText('original payment method');
    await expect(page.locator('[data-testid="shipping-rates"]')).toHaveCount(0);
  });

  test('neither page scrolls sideways at any of the eight widths', async ({ page }) => {
    for (const path of ['/shipping', '/returns']) {
      for (const width of BREAKPOINTS) {
        await page.setViewportSize({ width, height: 900 });
        await page.goto(path);
        await expect(page.locator('h1')).toBeVisible();
        expect(await horizontalOverflow(page), `${path} at ${width}px`).toBe(0);
      }
    }
  });

  test('the Shipping page has no axe violations', async ({ page }) => {
    await page.goto('/shipping');
    await expect(page.locator('[data-testid="shipping-rates"]')).toBeVisible();

    const results = await new AxeBuilder({ page }).include('main').analyze();
    expect(results.violations.map((violation) => `${violation.id}: ${violation.help}`)).toEqual([]);
  });
});

/**
 * Sends the `/api` calls of **every tab** of a browser context to the stub. `useStubApi` routes one page; a link that
 * opens a new tab needs the new tab to find the same stub, and a route on the context is what both tabs share.
 */
async function useStubApiInContext(context: BrowserContext): Promise<void> {
  await context.route('**/api/**', async (route) => {
    const url = new URL(route.request().url());
    const response = await route.fetch({ url: `${contentPagesApiURL}${url.pathname}${url.search}` });
    await route.fulfill({ response });
  });
}

/**
 * Adds one Indigo/S to the bag from a product page, the way a shopper does. The hydration gate is the one
 * `checkout.e2e.ts` documents at length: Add to Bag is server-rendered enabled before its handler exists, so the
 * `localStorage` entry that the page writes last is what proves the page is alive.
 */
async function addOneToBag(page: Page, index: number): Promise<void> {
  const slug = `test-product-${index}`;
  await page.goto(`/product/${slug}?colour=1&size=10`);
  await expect
    .poll(() => page.evaluate(() => localStorage.getItem('sd.recently-viewed') ?? ''))
    .toContain(slug);

  const add = page.locator('[data-testid="add-to-bag"]');
  await expect(add).toBeEnabled();
  await add.click();
  await expect(page.locator('sd-mini-bag [role="dialog"] .mini-bag__line')).toHaveCount(1);
}

/** Opens `/checkout` and waits for the page's own read of the checkout, so nothing typed is overwritten by it. */
async function openCheckoutPage(page: Page): Promise<void> {
  const read = page.waitForResponse(
    (response) =>
      new URL(response.url()).pathname === '/api/checkout' && response.request().method() === 'GET',
  );

  await page.goto('/checkout');
  await read;
  await expect(page.locator('.checkout__line')).toHaveCount(1);
  await expect(page.locator('#co-firstName')).toBeVisible();
}

/**
 * Presses each consent link of a page that holds a half-typed form and proves, in a real browser, what the shopper
 * needs: the link opens a **new tab** showing the page, and the original tab is still on the same address with
 * every value still in its field. `values` maps a field selector to what was typed into it.
 */
async function followConsentLinks(
  page: Page,
  context: BrowserContext,
  scope: string,
  values: Readonly<Record<string, string>>,
): Promise<void> {
  const startedAt = page.url();

  for (const { href, title } of [
    { href: '/terms', title: 'Terms and conditions' },
    { href: '/privacy', title: 'Privacy policy' },
  ]) {
    const link = page.locator(`${scope} a[href="${href}"]`);

    await expect(link).toHaveAttribute('target', '_blank');
    await expect(link).toHaveAttribute('rel', /noopener/);
    await expect(link).toHaveAccessibleName(/opens in a new tab/);

    const [popup] = await Promise.all([context.waitForEvent('page'), link.click()]);
    await popup.waitForLoadState('domcontentloaded');

    await expect(popup).toHaveURL(new RegExp(`${href}$`));
    await expect(popup.getByRole('heading', { level: 1, name: title })).toBeVisible();

    // The tab the shopper was typing in is where they left it, with everything they typed.
    expect(page.url()).toBe(startedAt);
    for (const [selector, value] of Object.entries(values)) {
      await expect(page.locator(selector), `${selector} after following ${href}`).toHaveValue(value);
    }

    await popup.close();
  }
}

/**
 * Issue #252: the About us, Privacy policy and Terms and conditions pages, drawn with the words the shop ships.
 *
 * The state is `store`, which serves `support/store-pages.json` - a file `StorePagesCopyTests` keeps equal to
 * `PolicyPageContent` - so what is measured here is the real copy: its headings, its two long lists and its longest
 * sentences, at a phone's width and a desktop's. The rest of this file measures the pages with a fixture built to
 * carry every allowed element and a table wider than 320 px; neither stands in for the other.
 *
 * **What is not asserted, deliberately.** Opening a page from the footer lands at the *top* only when the router
 * scrolls there, and on this branch it does not (nothing in the storefront configures scroll restoration; that is a
 * separate fix in flight). Asserting it here would pin a defect or fail on one, so the footer test below proves only
 * what is true: the link reaches the page. A full page load of the same address does open at the top, and that is
 * asserted.
 */
test.describe('the About, Privacy and Terms pages (#252)', () => {
  const PAGES = [
    { slug: 'about', title: 'About us', section: 'Delivery and returns' },
    { slug: 'privacy', title: 'Privacy policy', section: 'Who receives your data' },
    { slug: 'terms', title: 'Terms and conditions', section: 'Placing an order' },
  ] as const;

  const WIDTHS = [
    { width: 390, height: 844 },
    { width: 1280, height: 900 },
  ] as const;

  test.beforeEach(async ({ page }) => {
    await publish(page, 'store');
    await useStubApi(page);
  });

  test.afterEach(async ({ page }) => {
    await publish(page, 'none');
  });

  for (const { slug, title, section } of PAGES) {
    test(`/${slug}: the title is the one heading, the copy is in the HTML on the wire, and nothing is a placeholder`, async ({
      page,
    }) => {
      const response = await page.goto(`/${slug}`);

      expect(response?.status()).toBe(200);

      // Before any script has run: what a crawler reads.
      const html = (await response?.text()) ?? '';
      expect(html).toContain(`<h2>${section}</h2>`);

      await expect(page.locator('h1')).toHaveCount(1);
      await expect(page.getByRole('heading', { level: 1, name: title })).toBeVisible();
      await expect(page.getByRole('heading', { level: 2, name: section })).toBeVisible();

      // Substantial, and finished: a draft shows its work in a journal and a pull request, never on the page.
      const body = (await page.locator('main').innerText()).replace(/\s+/g, ' ');
      expect(body.split(' ').length, 'the page body is short').toBeGreaterThan(200);
      expect(body).not.toMatch(/lorem|placeholder|\bTODO\b|\bTBD\b|\bdraft\b|\{\{|\}\}|\[[A-Z ]+\]/i);
    });

    for (const { width, height } of WIDTHS) {
      test(`/${slug} at ${width}px: nothing scrolls sideways and nothing is clipped`, async ({ page }) => {
        await page.setViewportSize({ width, height });
        await page.goto(`/${slug}`);
        await expect(page.locator('h1')).toBeVisible();

        expect(await horizontalOverflow(page), `horizontal scroll at ${width}px`).toBeLessThanOrEqual(1);

        // The longest unbroken text in the copy must wrap inside the column, not past it.
        const clipped = await page.evaluate(() => {
          const main = document.querySelector('main');
          const limit = document.documentElement.clientWidth;
          return [...(main?.querySelectorAll('h1, h2, p, li') ?? [])]
            .filter((node) => node.getBoundingClientRect().right > limit + 1)
            .map((node) => (node.textContent ?? '').slice(0, 60));
        });
        expect(clipped, `text past the right edge at ${width}px`).toEqual([]);
      });

      test(`/${slug} at ${width}px has no axe violations`, async ({ page }) => {
        await page.setViewportSize({ width, height });
        await page.goto(`/${slug}`);
        await expect(page.locator('h1')).toBeVisible();

        const results = await new AxeBuilder({ page })
          .withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa'])
          .analyze();

        expect(
          results.violations,
          JSON.stringify(results.violations.map((violation) => violation.id), null, 2),
        ).toEqual([]);
      });
    }

    test(`/${slug}: opened by address it starts at the top`, async ({ page }) => {
      await page.setViewportSize({ width: 390, height: 844 });
      await page.goto(`/${slug}`);
      await expect(page.locator('h1')).toBeVisible();

      expect(await page.evaluate(() => window.scrollY)).toBe(0);
    });

    for (const { width, height } of WIDTHS) {
      test(`the footer link to /${slug} reaches the page at ${width}px`, async ({ page }) => {
        await page.setViewportSize({ width, height });
        await page.goto('/');

        const link = page.locator('footer').locator(`a[href="/${slug}"]`).first();
        await link.scrollIntoViewIfNeeded();
        await link.click();

        await expect(page).toHaveURL(new RegExp(`/${slug}$`));
        await expect(page.getByRole('heading', { level: 1, name: title })).toBeVisible();
        await expect(page.getByRole('heading', { level: 2, name: section })).toBeVisible();
      });
    }
  }

  test('no copy carries an address of its own, because the client can edit a slug', async ({
    page,
  }) => {
    // The copy writes no address of its own (a slug is the client's to edit), so no link inside any body may exist.
    for (const { slug } of PAGES) {
      await page.goto(`/${slug}`);
      await expect(page.locator('h1')).toBeVisible();
      await expect(page.locator('main a'), `${slug} carries a link inside its copy`).toHaveCount(0);
    }
  });

  test('the Privacy policy names the three cookies the shop sets, and no more', async ({ page }) => {
    await page.goto('/privacy');

    for (const cookie of ['sd_cart', 'sd_customer_auth', 'sd_vs']) {
      await expect(page.locator('main strong', { hasText: cookie })).toHaveCount(1);
    }
    await expect(page.locator('main')).not.toContainText('sd_admin');
  });

  test('the Terms say PayPal for everyone and bank transfer for Sri Lanka only', async ({ page }) => {
    await page.goto('/terms');

    const main = page.locator('main');
    await expect(main).toContainText('PayPal is available for every order');
    await expect(main).toContainText('For a delivery within Sri Lanka we may also offer payment by bank transfer');
    await expect(main).not.toContainText(/cash on delivery/i);
  });
  test('checkout: the terms and privacy links open new tabs, and the half-typed form survives', async ({
    page,
    context,
  }) => {
    await useStubApiInContext(context);
    await page.setViewportSize({ width: 390, height: 844 });
    await addOneToBag(page, 2);
    await openCheckoutPage(page);

    await page.locator('#co-firstName').fill('Nimali');
    await page.locator('#co-lastName').fill('Perera');
    await page.locator('#co-email').fill('nimali@example.com');
    await page.locator('#co-line1').fill('14 Galle Road');
    await page.locator('#co-city').fill('Kollupitiya');

    await followConsentLinks(page, context, '.checkout__fineprint', {
      '#co-firstName': 'Nimali',
      '#co-lastName': 'Perera',
      '#co-email': 'nimali@example.com',
      '#co-line1': '14 Galle Road',
      '#co-city': 'Kollupitiya',
    });

    // And the sentences read as the shop means them, in the same capitals, with the new-tab hint in the link name.
    const fineprint = (await page.locator('.checkout__fineprint').innerText()).replace(/\s+/g, ' ');
    expect(fineprint).toContain('By placing this order you agree to our terms and conditions');
    expect(fineprint).toContain('How we use your details is explained in our privacy policy');
  });

  test('register: the terms and privacy links open new tabs, and the half-typed form survives', async ({
    page,
    context,
  }) => {
    await useStubApiInContext(context);
    await page.setViewportSize({ width: 390, height: 844 });
    await page.goto('/account/register');
    await expect(page.locator('[data-testid="register-consent"] a').first()).toBeVisible();

    // Hydrated first (the gate account.e2e.ts uses): text typed before the handlers exist is not in the page's state.
    await page.waitForFunction(() => !!document.querySelector('sd-root')?.hasAttribute('ng-version'));
    await page.locator('[data-testid="register-first-name"]').fill('Nimal');
    await page.locator('[data-testid="register-email"]').fill('nimal@example.invalid');

    await followConsentLinks(page, context, '[data-testid="register-consent"]', {
      '[data-testid="register-first-name"]': 'Nimal',
      '[data-testid="register-email"]': 'nimal@example.invalid',
    });

    const consent = (await page.locator('[data-testid="register-consent"]').innerText()).replace(/\s+/g, ' ');
    expect(consent).toContain('By creating an account you agree to our terms and conditions');
    expect(consent).toContain('How we use your details is explained in our privacy policy');
  });
});
