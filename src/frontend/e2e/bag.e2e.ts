import { Browser, Page, expect, test } from '@playwright/test';

import { apiURL } from '../playwright.config';
import { edgesWithoutRing, horizontalOverflow, undersized } from './support/measure';

/**
 * Browser checks for the bag page and the mini bag (CART-01, CART-02).
 *
 * `bag-page.spec.ts`, `mini-bag.spec.ts` and `cart.store.spec.ts` already pin the markup, the
 * quantity rules, the line states and every failure path in jsdom. What needs a real browser is the
 * part jsdom has no opinion about: layout at eight real viewports, a real focus model, a real cookie
 * jar, and — the one that matters most here — **the HTML the server puts on the wire before a line of
 * JavaScript has run**, because a bag is per-visitor and what is and is not in that document is a
 * security question rather than a styling one.
 *
 * The cart comes from `support/catalog-api-stub.mjs`, which refuses what the real API refuses: 415 on
 * a body that is not JSON, 400 on one it cannot read or a quantity outside 1–10, 400 on a variant the
 * shop does not sell, 409 on one that is sold out, and a 404 for a variant id in the path that is not
 * an integer. A stub that answered 200 to all of those would make every failure path in this file
 * unreachable, which is exactly how a search stub once hid a 400.
 */

/** docs/04 §4 and the project's non-negotiable list. */
const BREAKPOINTS = [320, 375, 390, 768, 1024, 1280, 1440, 1920] as const;

/**
 * A product page with a colourway and a size settled, so Add to Bag has a variant to send.
 *
 * **Every test that moves stock uses a product of its own**, and that is not tidiness: the stub's
 * stock switch is one shared map, the suite is `fullyParallel`, and the first version of this file
 * had five tests overriding and resetting variant 1001 at the same time. Six of them failed in the
 * full run and every one of them passed on its own — the classic shape of a test that is really
 * testing its neighbours.
 */
function sizedProduct(index: number, size = 10): string {
  return `/product/test-product-${index}?colour=1&size=${size}`;
}

/**
 * The size whose Indigo variant has four in stock, which is the only one with room for the quantity
 * stepper to have **both** buttons enabled at once: the stub gives a variant
 * `2 + colourIndex + sizeIndex`, so Indigo/S has two, and at a quantity of two the minus is enabled
 * and the plus is at its ceiling and disabled. A focus test that skipped the disabled one would be a
 * control short of the row it claims to cover.
 */
const ROOMY_SIZE = 12;

/**
 * The variant id the stub derives from a product index (`index * 1000 + n`): Indigo/S is the first
 * variant of every product, with two in stock (`availableFor` returns `2 + colourIndex + sizeIndex`).
 * Index 11 and its multiples are wholly sold out, so they are avoided here.
 */
function indigoSmall(index: number): number {
  return index * 1000 + 1;
}

/**
 * Sends the browser's own `/api` calls to the stub, cart included.
 *
 * `route.fetch` + `route.fulfill` rather than a proxy in the SSR server: the request is made by
 * Playwright with the page's cookies and the response, `Set-Cookie` and all, is handed back to the
 * page as though it came from its own origin. That is load-bearing for the bag and is asserted
 * directly in "the bag survives a reload" below — if the cookie did not round-trip, every add would
 * create a new bag and that test would fail rather than this file quietly testing a shop with no
 * persistence.
 */
async function useStubApi(page: Page): Promise<void> {
  await page.route('**/api/**', async (route) => {
    const url = new URL(route.request().url());
    const response = await route.fetch({ url: `${apiURL}${url.pathname}${url.search}` });
    await route.fulfill({ response });
  });
}

/** Puts a variant's stock, or its very existence, where a test needs it. Reset in `afterEach`. */
async function setStock(
  page: Page,
  variantId: number,
  options: { available?: number; sellable?: boolean },
): Promise<void> {
  const params = new URLSearchParams({ variantId: String(variantId) });
  if (options.available !== undefined) {
    params.set('available', String(options.available));
  }
  if (options.sellable !== undefined) {
    params.set('sellable', options.sellable ? '1' : '0');
  }

  const response = await page.request.get(`${apiURL}/__test__/cart-variant?${params}`);
  expect(response.ok()).toBe(true);
}

/**
 * Puts one variant back as the catalogue has it.
 *
 * Per variant, never `reset=all`: clearing the whole map would wipe a parallel test's override out
 * from under it, which is the other half of the interference described on `sizedProduct`.
 */
async function resetStock(page: Page, variantId: number): Promise<void> {
  await page.request.get(`${apiURL}/__test__/cart-variant?reset=variant&variantId=${variantId}`);
}

/**
 * Adds one Indigo/S from a product page, the way a shopper does.
 *
 * **Hydration first, and `toBeEnabled()` is not it.** `page.goto` resolves on `load`, which is before
 * Angular has claimed the server's DOM, and the Add to Bag button is *server-rendered enabled* when the
 * URL settles a variant — so an actionability check passes on a button whose click handler does not
 * exist yet, the click goes nowhere, and the wait below times out. The product page's own suite solves
 * this the same way and says why: recording the visit (PDP-07) is the last thing the page does, so the
 * `localStorage` entry appearing is hydration having happened.
 */
async function addFromProductPage(page: Page, index = 1, size = 10): Promise<void> {
  const slug = `test-product-${index}`;
  await page.goto(sizedProduct(index, size));
  await expect
    .poll(() => page.evaluate(() => localStorage.getItem('sd.recently-viewed') ?? ''))
    .toContain(slug);

  const add = page.locator('[data-testid="add-to-bag"]');
  await expect(add).toBeEnabled();
  await add.click();

  // The mini bag opening is the page's own confirmation that the add landed.
  await expect(page.locator('sd-mini-bag [role="dialog"] .mini-bag__line')).toHaveCount(1);
}

/**
 * Opens `/bag` and waits until it is **hydrated**, which the lines themselves cannot tell you.
 *
 * The bag page is server-rendered (that is the point of it), so `.bag__line` is in the document before a
 * line of JavaScript has run — waiting for it proves the server answered, not that the page is alive.
 * Under a full-suite load that gap is wide enough to lose an event: the typed-quantity test filled the
 * box, the `change` listener was not attached yet, and the box kept `99`. One failure in a 603-test run,
 * never reproducible on its own.
 *
 * The header's bag badge is the signal, because it is the one thing on this page that **cannot** be
 * server-rendered: `StorefrontCounts` takes the count up inside `afterNextRender`, which never runs on
 * the server (deliberately — a count in the markup would make every page per-visitor). So a badge with a
 * number in it is hydration, observed rather than assumed.
 *
 * That only works for a bag with something in it, because an empty bag draws no badge. `items === 0` is
 * handled so the helper cannot hang, but it is explicitly **not** a hydration gate — see the branch.
 */
async function openBagPage(page: Page, items: number): Promise<void> {
  // **Armed before the navigation**, because the badge alone is not enough.
  //
  // The badge proves the shell hydrated, and it can go green from the *transfer state* — the bag the
  // server rendered — while the page's own browser-side `GET /api/cart` is still in flight. When that
  // response lands it re-renders the lines, and Angular's `[value]="line.quantity"` binding overwrites
  // whatever is in the quantity box. Measured: the typed-quantity test filled `99`, that read landed, the
  // box was rewritten to `1`, and the handler had never run. About one full-suite run in three.
  //
  // Waiting for the read to arrive means the page has applied its own data before a test touches it.
  const read = page.waitForResponse(
    (response) =>
      new URL(response.url()).pathname === '/api/cart' && response.request().method() === 'GET',
  );

  await page.goto('/bag');
  await read;
  await expect(page.locator('.bag__line')).toHaveCount(items);

  const badge = page.locator('[data-testid="bag-toggle"] .count');
  if (items === 0) {
    // An empty bag renders no badge at all (`@if (bagCount() > 0)`), so waiting for it to read "0" would
    // wait for ever — which is what this branch exists to prevent.
    //
    // **It is not a hydration gate, and it does not pretend to be one.** Nothing an empty bag puts on
    // screen differs before and after hydration: the badge is absent either way, and the shell's
    // screen-reader text says "Bag, 0 items" in the server's own markup. So a test that needs to
    // *interact* with an empty bag has to find its own signal; this only asserts the state is on screen.
    // No caller needs that today, and saying so is better than a gate that is not one.
    await expect(badge).toHaveCount(0);
    await expect(page.locator('.bag__empty')).toBeVisible();
    return;
  }

  await expect(badge).toHaveText(String(items));
}

/**
 * Measures a selector, refusing to pass when it matched nothing.
 *
 * **Issue #80**: `undersized` returns `[]` both when every control passes the 44 px floor and when the
 * selector matched nothing at all, so `expect(await undersized(page, sel)).toEqual([])` passes on a
 * page with no such element. #80 fixes the helper and is deliberately not fixed here — but a call site
 * added today must not be one of the vacuous ones, so every clause is counted **separately**: the
 * guard everyone reaches for (`count() > 0` on the whole compound selector) is satisfied by the first
 * clause alone while a later one matches nothing, which is live today in `search.e2e.ts:576`.
 */
async function everyClauseIsBigEnough(page: Page, clauses: readonly string[]): Promise<void> {
  for (const clause of clauses) {
    await expect(
      page.locator(clause).first(),
      `nothing matched ${clause}, so nothing was measured`,
    ).toBeVisible();
  }

  expect(await undersized(page, clauses.join(', '))).toEqual([]);
}

test.describe('the bag (CART-01, CART-02)', () => {
  test.describe('adding, and what happens next', () => {
    test.beforeEach(async ({ page }) => {
      await useStubApi(page);
    });

    test('Add to Bag opens the mini bag with the item in it', async ({ page }) => {
      await addFromProductPage(page);

      const drawer = page.locator('sd-mini-bag [role="dialog"]');
      await expect(drawer).toBeVisible();

      // The **dialog's accessible name**, not a heading element inside it. W2-10 turned the shared
      // drawer's title from an `h2` into a `p` (#76: the drawer's markup precedes the page's own `h1`,
      // so an `h2` there made every page announce "Menu" before it said what the page was), and a
      // `getByRole('heading')` here went from passing to not finding anything. The name is what
      // actually matters to a screen reader, it comes from the drawer's `aria-labelledby`, and
      // asserting it is robust to which element carries the text.
      await expect(page.getByRole('dialog', { name: 'Your bag' })).toBeVisible();
      await expect(drawer.locator('.mini-bag__line')).toContainText('Test Product 1');
      await expect(drawer.locator('.mini-bag__line')).toContainText('Indigo');
      await expect(drawer.locator('.mini-bag__foot')).toContainText('Subtotal');

      // And the header count follows, which is the number a shopper actually watches (GLB-04).
      await expect(page.locator('[data-testid="bag-toggle"]')).toContainText('1');
    });

    test('the drawer says delivery is worked out at checkout rather than inventing a fee', async ({
      page,
    }) => {
      await addFromProductPage(page);

      const foot = page.locator('sd-mini-bag .mini-bag__foot');
      await expect(foot).toContainText('Delivery is calculated at checkout');
      await expect(foot.getByRole('link', { name: 'View bag' })).toHaveAttribute('href', '/bag');
    });

    /**
     * The real cookie round trip. Without it every add would create a new bag and the shop would have
     * no persistence at all — and nothing else in this file would notice.
     */
    test('the bag survives a reload, which is the whole point of the cookie', async ({ page }) => {
      await addFromProductPage(page);

      await openBagPage(page, 1);
      await expect(page.locator('.bag__line')).toContainText('Test Product 1');

      const cookies = await page.context().cookies();
      const cart = cookies.find((cookie) => cookie.name === 'sd_cart');
      expect(cart, 'the bag cookie was never stored, so nothing can persist').toBeDefined();
      expect(cart?.httpOnly).toBe(true);
      expect(cart?.sameSite).toBe('Lax');
      expect(cart?.path).toBe('/');
    });

    test('a second add of the same variant adds to the line rather than making a new one', async ({
      page,
    }) => {
      await addFromProductPage(page);
      await page.keyboard.press('Escape');

      await page.locator('[data-testid="add-to-bag"]').click();
      await expect(page.locator('sd-mini-bag .mini-bag__line')).toHaveCount(1);
      await expect(page.locator('sd-mini-bag .mini-bag__line')).toContainText('Qty 2');
    });

    /**
     * Sold out is a 409 from the API and a sentence on the page, not a silent failure and not a
     * generic apology. The stock is taken away between the page load and the click, which is the only
     * way this state exists.
     */
    test('a size that sells out between the page load and the click says so', async ({ page }) => {
      const product = 2;
      const slug = `test-product-${product}`;
      await page.goto(sizedProduct(product));

      // The same hydration gate `addFromProductPage` uses, and for the same reason — this test cannot
      // call that helper, because the whole point is to click Add to Bag *after* the stock has gone, so
      // the add must not be made for it. `toBeEnabled()` alone was the hole: the button is
      // server-rendered enabled here, so the click could land before its handler existed.
      await expect
        .poll(() => page.evaluate(() => localStorage.getItem('sd.recently-viewed') ?? ''))
        .toContain(slug);
      await expect(page.locator('[data-testid="add-to-bag"]')).toBeEnabled();

      try {
        await setStock(page, indigoSmall(product), { available: 0 });
        await page.locator('[data-testid="add-to-bag"]').click();

        await expect(page.locator('.pdp__add-error')).toContainText('sold out');
        await expect(page.locator('sd-mini-bag [role="dialog"]')).toBeHidden();
      } finally {
        await resetStock(page, indigoSmall(product));
      }
    });
  });

  test.describe('the bag page', () => {
    test.beforeEach(async ({ page }) => {
      await useStubApi(page);
      await addFromProductPage(page);
      await openBagPage(page, 1);
    });

    test('shows the line, its colour and size, and a total that is the items only', async ({
      page,
    }) => {
      const row = page.locator('.bag__line');
      await expect(row).toContainText('Test Product 1');
      await expect(row).toContainText('Indigo');
      await expect(row.locator('.bag__attrs')).toContainText('S');

      const summary = page.locator('.bag__summary');
      await expect(summary).toContainText('Subtotal (1 item)');
      await expect(summary).toContainText('Calculated at checkout');
      await expect(summary).toContainText('Total (items only)');
    });

    /**
     * **W3-02 changed half of this test, and that is the point of it.**
     *
     * It used to assert that the bag had neither a promo field nor a checkout button, because neither
     * had anything behind it. Checkout now exists, so the button is a real link and asserting its
     * absence would be asserting that the shop is still unfinished. The promo field is unchanged: ADM-13
     * is not built, and a field that refused every code a shopper typed would teach them the shop is
     * broken.
     */
    test('offers checkout, and still has no promo field', async ({ page }) => {
      await expect(page.locator('a.bag__checkout')).toHaveAttribute('href', '/checkout');

      // ADM-13 is not built, so there is nothing to type a code into.
      await expect(page.locator('input[type="text"]')).toHaveCount(0);
    });

    test('steps the quantity and the subtotal follows', async ({ page }) => {
      const before = await page.locator('.bag__total').innerText();

      await page.getByRole('button', { name: /^Increase quantity/ }).click();
      await expect(page.locator('.qty__input')).toHaveValue('2');
      await expect(page.locator('.bag__total')).not.toHaveText(before);
      await expect(page.locator('.bag__summary')).toContainText('Subtotal (2 items)');
    });

    test('clamps a typed quantity that is over the stock and never shows a number the bag does not hold', async ({
      page,
    }) => {
      const input = page.locator('.qty__input');
      await expect(input).toHaveValue('1');

      await input.fill('99');
      await input.blur();

      // **The change landing is asserted first, and the box second, deliberately.** The box is re-synced
      // from the bag whenever a write settles — that is what stops a *refused* change leaving a number the
      // bag does not hold — so "the box reads 1" is the correct answer to a PATCH that did not take
      // effect. Asserting the box first turned any hiccup into a bare `expected "2", received "1"` with
      // nothing to act on. Waiting on the subtotal says which step failed.
      await expect(page.locator('.bag__summary')).toContainText('Subtotal (2 items)');

      // `.bag__error`, not `[role="alert"]`: the site footer carries a permanently present, empty
      // `role="alert"` for the newsletter form (GLB-07), so a page-wide count is 1 on every page and an
      // assertion of 0 fails on a perfectly healthy bag. It did, twice, before this was narrowed.
      await expect(page.locator('.bag__error')).toHaveCount(0);

      // Product 1's Indigo/S has two in stock, so the ceiling is 2.
      await expect(input).toHaveValue('2');
    });

    test('removes a line and shows the empty state', async ({ page }) => {
      await page.getByRole('button', { name: /^Remove / }).click();

      await expect(page.locator('.bag__line')).toHaveCount(0);
      await expect(page.locator('.bag__empty')).toContainText('Your bag is empty');
      await expect(page.locator('[data-testid="bag-toggle"]')).toContainText('Bag, 0 items');
    });

    /**
     * Move to wishlist saves to this browser, because W2-08 is not built and doc 08 §1.7 already says
     * a guest wishlist is `localStorage`. What must not happen is the item disappearing from both.
     */
    test('move to wishlist puts the product on the wishlist and takes it out of the bag', async ({
      page,
    }) => {
      await page.getByRole('button', { name: /^Move / }).click();

      await expect(page.locator('.bag__line')).toHaveCount(0);
      // Scoped to the header: the footer links to /wishlist too, and an unscoped locator is a strict
      // mode violation rather than an assertion about the count badge.
      await expect(page.locator('.site-header__tools a[href="/wishlist"]')).toContainText('1');
      expect(await page.evaluate(() => localStorage.getItem('sd.wishlist.local'))).toContain(
        'test-product-1',
      );
    });

    /**
     * Regression for issue #267: `onBagClick` takes this click over with a plain `href="/bag"` (see the
     * component doc above it), and the bag toggle is clicked here while the shopper is already on `/bag` —
     * exactly the shape `NavigationScroll`'s "a link to the page already shown" handling looks for. Without
     * `onBagClick`'s own `stopPropagation`, that handler would focus `main` underneath the drawer that had
     * just opened on it (found by the independent tester of #267, traced to this exact click). **Not a
     * scroll-position assertion**: the mini bag's own focus management already moves scroll and focus when
     * it opens, for reasons that have nothing to do with this fix, so asserting against `main` specifically
     * is what isolates `NavigationScroll`'s handling from that.
     */
    test('opening the mini bag from the bag page itself does not focus main the way a same-page link does', async ({
      page,
    }) => {
      await page.locator('[data-testid="bag-toggle"]').click();

      await expect(page.locator('sd-mini-bag [role="dialog"]')).toBeVisible();
      const activeId = await page.evaluate(() => document.activeElement?.id);
      expect(activeId, "NavigationScroll's same-page-link handling should not have run").not.toBe('main');
    });
  });

  test.describe('lines the shop can no longer sell', () => {
    test.beforeEach(async ({ page }) => {
      await useStubApi(page);
    });

    test('a line that sold out keeps its place, says so, and leaves the subtotal', async ({
      page,
    }) => {
      const product = 3;
      await addFromProductPage(page, product);

      try {
        await setStock(page, indigoSmall(product), { available: 0 });
        await openBagPage(page, 1);

        const row = page.locator('.bag__line');
        await expect(row).toHaveCount(1);
        await expect(row).toContainText('Out of stock');
        await expect(page.locator('.bag__summary')).toContainText('Subtotal (0 items)');
        await expect(page.locator('.bag__summary')).toContainText('cannot be bought');

        // Still removable, which is the only thing a shopper can do about it.
        await expect(page.getByRole('button', { name: /^Remove / })).toBeEnabled();
      } finally {
        await resetStock(page, indigoSmall(product));
      }
    });

    /**
     * A product the shop has withdrawn. The line must stay — an item that vanished out of a bag with
     * no message is the worst available behaviour — and it must carry **no price**, because there is
     * no offer left to quote.
     */
    test('a withdrawn product stays in the bag, unpriced, with no quantity control', async ({
      page,
    }) => {
      const product = 4;
      await addFromProductPage(page, product);

      try {
        await setStock(page, indigoSmall(product), { sellable: false });
        await openBagPage(page, 1);

        const row = page.locator('.bag__line');
        await expect(row).toHaveCount(1);
        await expect(row).toContainText(`Test Product ${product}`);
        await expect(row).toContainText('no longer available');
        await expect(row.locator('.qty')).toHaveCount(0);
        await expect(row.locator('.bag__qty-fixed')).toContainText('Qty 1');
        await expect(row).not.toContainText('Rs.');
        await expect(page.locator('.bag__summary')).toContainText('Total (items only)');
      } finally {
        await resetStock(page, indigoSmall(product));
      }
    });

    test('a line short of stock keeps the quantity the shopper chose and says how many are left', async ({
      page,
    }) => {
      const product = 6;
      await addFromProductPage(page, product);

      try {
        // Two in the bag, then only one left.
        await openBagPage(page, 1);
        await page.getByRole('button', { name: /^Increase quantity/ }).click();
        await expect(page.locator('.qty__input')).toHaveValue('2');

        await setStock(page, indigoSmall(product), { available: 1 });
        await page.reload();

        await expect(page.locator('.qty__input')).toHaveValue('2');

        // Scoped to the line, because W3-02 added a second `.bag__notice--warn` to the summary — the
        // one that says to adjust the bag before checking out — and a bare class selector is now a
        // strict-mode violation rather than a test. The line's own notice is the subject here.
        await expect(page.locator('.bag__line .bag__notice')).toContainText('Only 1 left');

        // And the two notices are about different things, which is worth pinning: the line says how
        // many are left, the summary says what to do about it (G-08).
        await expect(page.locator('#bag-checkout-blocked')).toContainText('before you check out');
        await expect(page.locator('button.bag__checkout')).toBeDisabled();
      } finally {
        await resetStock(page, indigoSmall(product));
      }
    });
  });

  /**
   * What the server actually puts on the wire, read with no JavaScript at all.
   *
   * These are fetched through `page.request`, which does not run the page, so every assertion is about
   * the server-rendered document rather than about what hydration later produced.
   */
  test.describe('the server-rendered document', () => {
    /** A bag the SSR server can see: the cookie the API would have issued, set by hand. */
    async function visitorWithABag(browser: Browser, baseURL: string): Promise<string> {
      const context = await browser.newContext();
      const page = await context.newPage();
      await useStubApi(page);
      await addFromProductPage(page);

      const cart = (await context.cookies()).find((cookie) => cookie.name === 'sd_cart');
      expect(cart, 'no bag cookie, so there is no bag for the server to render').toBeDefined();

      const response = await page.request.get(`${baseURL}/bag`);
      expect(response.status()).toBe(200);
      const html = await response.text();

      await context.close();
      return html;
    }

    test('renders the bag page on the server, with the line in the HTML', async ({
      browser,
      baseURL,
    }) => {
      const html = await visitorWithABag(browser, baseURL!);

      expect(html).toContain('Test Product 1');
      expect(html).toContain('Your bag');
      // A spinner in the markup would mean the page renders empty and fills in afterwards.
      expect(html).not.toContain('Loading your bag');
    });

    test('asks not to be indexed', async ({ browser, baseURL }) => {
      const html = await visitorWithABag(browser, baseURL!);

      expect(html).toMatch(/<meta[^>]+name="robots"[^>]+content="noindex"/);
    });

    /**
     * **The cart token is not in the document.** It travels in an `HttpOnly` cookie precisely so a
     * script cannot read it; a copy in the HTML — in the body, in a `data-` attribute, or in Angular's
     * transfer state — would hand it to any script on the page and make the flag decorative.
     */
    test('never writes the cart token into the HTML or the transfer state', async ({
      browser,
      baseURL,
    }) => {
      const context = await browser.newContext();
      const page = await context.newPage();
      await useStubApi(page);
      await addFromProductPage(page);

      const token = (await context.cookies()).find((cookie) => cookie.name === 'sd_cart')?.value;
      expect(token).toBeTruthy();

      const html = await (await page.request.get(`${baseURL}/bag`)).text();
      expect(html.toLowerCase()).not.toContain(token!.toLowerCase());
      expect(html).not.toContain('sd_cart');

      await context.close();
    });

    /**
     * GLB-11's rule, applied to the one page that fetches per-visitor data: the document must not name
     * the machine it was rendered on. The storefront has leaked `API_INTERNAL_URL` into the HTML once
     * already.
     */
    test('never names the internal API address', async ({ browser, baseURL }) => {
      const html = await visitorWithABag(browser, baseURL!);

      expect(html).not.toContain(new URL(apiURL).host);
      expect(html).not.toContain('API_INTERNAL_URL');
    });

    /**
     * And it ships the bag **once**. The transfer state exists so the browser's first render matches
     * the server's; a second copy of the same payload would be the mistake that put the catalogue in a
     * listing twice.
     */
    test('carries the bag once, not twice', async ({ browser, baseURL }) => {
      const html = await visitorWithABag(browser, baseURL!);

      // The *payload*, not the words: a product name legitimately appears several times in one row
      // (the link, and the aria-labels on Remove and Move to wishlist), so counting it only ever
      // produces a bound somebody has to guess — the first version of this test guessed 8 and the
      // real figure was 9. These two keys exist nowhere but in a serialised `CartDto`, so one of
      // each is exactly one copy of the bag, and `withNoHttpTransferCache` plus the single explicit
      // handover is what keeps it that way.
      expect(html.split('"itemsSubtotal"').length - 1).toBe(1);
      expect(html.split('"unitPriceAtAdd"').length - 1).toBe(1);
    });

    /**
     * A shared cache holding this document would hand one shopper another shopper's bag. Measured on
     * the wire, because that is the only place the header either is or is not.
     */
    test('forbids caching the bag page', async ({ browser, baseURL }) => {
      const context = await browser.newContext();
      const page = await context.newPage();
      await useStubApi(page);
      await addFromProductPage(page);

      const response = await page.request.get(`${baseURL}/bag`);
      const cacheControl = response.headers()['cache-control'] ?? '';

      expect(cacheControl).toContain('no-store');
      expect(cacheControl).toContain('private');
      expect(response.headers()['vary'] ?? '').toContain('Cookie');

      await context.close();
    });

    /**
     * Every **other** page stays visitor-independent, which is what keeps them cacheable. The bag
     * count is taken up after the first render for exactly this reason.
     */
    test('a page that is not the bag carries no bag contents and no no-store', async ({
      browser,
      baseURL,
    }) => {
      const context = await browser.newContext();
      const page = await context.newPage();
      await useStubApi(page);
      await addFromProductPage(page);

      const response = await page.request.get(`${baseURL}/product/test-product-1`);
      const html = await response.text();

      // On the *payload*, not on the class name: Angular inlines the mini bag's stylesheet into every
      // document, so `mini-bag__line` is in the head of every page whether a line was rendered or
      // not — the first version of this assertion failed for exactly that reason and would have gone
      // on failing for a correct page. These two keys exist nowhere but in a serialised `CartDto`.
      expect(html).not.toContain('"itemsSubtotal"');
      expect(html).not.toContain('"unitPriceAtAdd"');
      // And no rendered line: the class with an opening quote only occurs on an element.
      expect(html).not.toContain('class="mini-bag__line');
      expect(response.headers()['cache-control'] ?? '').not.toContain('no-store');

      await context.close();
    });

    /**
     * The bag link is a real link, so it works before a line of script has run. That is why the header
     * control is an anchor and not a button.
     */
    test('the header bag control is a link to a real page in the server HTML', async ({
      baseURL,
      request,
    }) => {
      const html = await (await request.get(`${baseURL}/`)).text();

      expect(html).toMatch(/<a[^>]+href="\/bag"/);
    });
  });

  test.describe('the keyboard alone', () => {
    test.beforeEach(async ({ page }) => {
      await useStubApi(page);
      await addFromProductPage(page);
      await page.keyboard.press('Escape');
      await expect(page.locator('sd-mini-bag [role="dialog"]')).toBeHidden();
    });

    test('opens the mini bag from the header control and traps focus inside it', async ({
      page,
    }) => {
      const toggle = page.locator('[data-testid="bag-toggle"]');
      await toggle.focus();
      await expect(toggle).toHaveAttribute('aria-expanded', 'false');

      await page.keyboard.press('Enter');
      await expect(toggle).toHaveAttribute('aria-expanded', 'true');

      const panel = page.locator('sd-mini-bag [role="dialog"]');
      await expect(panel).toBeVisible();

      // Focus moved into the panel, and ten Tabs cannot get out of it.
      expect(await focusIsInside(page)).toBe(true);
      for (let press = 0; press < 10; press++) {
        await page.keyboard.press('Tab');
        expect(await focusIsInside(page)).toBe(true);
      }
    });

    test('Escape closes the mini bag and puts focus back on the bag control', async ({ page }) => {
      const toggle = page.locator('[data-testid="bag-toggle"]');
      await toggle.focus();
      await page.keyboard.press('Enter');
      await expect(page.locator('sd-mini-bag [role="dialog"]')).toBeVisible();

      await page.keyboard.press('Escape');
      await expect(page.locator('sd-mini-bag [role="dialog"]')).toBeHidden();
      await expect(toggle).toBeFocused();
    });

    test('a line can be removed from the mini bag with the keyboard alone', async ({ page }) => {
      await page.locator('[data-testid="bag-toggle"]').focus();
      await page.keyboard.press('Enter');

      const remove = page.locator('sd-mini-bag .mini-bag__remove');
      await remove.focus();
      await page.keyboard.press('Enter');

      await expect(page.locator('sd-mini-bag .mini-bag__line')).toHaveCount(0);
      await expect(page.locator('sd-mini-bag')).toContainText('Your bag is empty');
    });

    test('the bag page can be driven from the keyboard, and announces what changed', async ({
      page,
    }) => {
      await openBagPage(page, 1);

      const increase = page.getByRole('button', { name: /^Increase quantity/ });
      await increase.focus();
      await page.keyboard.press('Enter');
      await expect(page.locator('.qty__input')).toHaveValue('2');

      // The live region outside the closed drawer is the one that speaks here. Exactly one of the two
      // ever holds text, so this also proves nothing is announced twice.
      const spoken = page.locator('[role="status"][aria-live="polite"]');
      await expect(spoken.filter({ hasText: 'quantity changed to 2' })).toHaveCount(1);

      const remove = page.getByRole('button', { name: /^Remove / });
      await remove.focus();
      await page.keyboard.press('Enter');
      await expect(spoken.filter({ hasText: 'removed from your bag' })).toHaveCount(1);
      await expect(spoken.filter({ hasText: 'Your bag is now empty' })).toHaveCount(1);
    });
  });

  /**
   * Its own block, because it needs a line with **room above and below** the quantity it is sitting
   * at: the stub gives Indigo/S two, so at a quantity of two the minus is enabled and the plus is at
   * its ceiling and disabled, and a disabled control cannot take focus at all.
   */
  test.describe('the focus ring on the bag page', () => {
    test.beforeEach(async ({ page }) => {
      await useStubApi(page);
      await addFromProductPage(page, 1, ROOMY_SIZE);
      await page.keyboard.press('Escape');
    });

    /**
     * A focus ring that an ancestor clips away is a focus ring nobody can see (lesson L4). The three
     * quantity controls sit edge to edge, which is precisely where a ring gets painted underneath its
     * neighbour.
     */
    test('every control on the bag page shows a focus ring on all four edges', async ({ page }) => {
      await page.setViewportSize({ width: 1280, height: 900 });
      await openBagPage(page, 1);

      // **One press of Tab**: Chromium only matches
      // `:focus-visible` on a button when the last interaction was a keyboard one, so a bare
      // programmatic `focus()` measures a ring the shopper never sees — all four edges came back
      // missing on the minus button for that reason alone. And the **minus is disabled at a quantity
      // of one**, so it cannot take focus until the quantity is two; a loop that skipped it silently
      // would be one control short of the row it claims to cover.
      await page.getByRole('button', { name: /^Increase quantity/ }).click();
      await expect(page.locator('.qty__input')).toHaveValue('2');

      // Tab **after** the click, not before: the click is a mouse interaction and puts Chromium back
      // into mouse modality, so a Tab pressed earlier is undone by it. Measured — the ring check
      // reported all four edges missing on a button that paints one perfectly.
      await page.keyboard.press('Tab');

      for (const selector of [
        '.qty__step >> nth=0',
        '.qty__input',
        '.qty__step >> nth=1',
        '.bag__actions button >> nth=0',
        '.bag__actions button >> nth=1',
        '.bag__name',
      ]) {
        const control = page.locator(selector).first();
        await control.focus();
        await expect(control).toBeFocused();

        // The guard that stops the measurement below being vacuous: a control that is not
        // `:focus-visible` paints no ring at all, so "no edges missing" would be meaningless.
        expect(
          await control.evaluate((element) => element.matches(':focus-visible')),
          `${selector} is focused but not :focus-visible, so there is no ring to measure`,
        ).toBe(true);

        expect(
          await edgesWithoutRing(page, control),
          `${selector} has an invisible ring edge`,
        ).toEqual([]);
      }
    });
  });

  test.describe('every breakpoint', () => {
    for (const width of BREAKPOINTS) {
      test(`the bag page fits at ${width} px with no horizontal scroll`, async ({ page }) => {
        await useStubApi(page);
        await addFromProductPage(page);

        await page.setViewportSize({ width, height: 900 });
        await openBagPage(page, 1);

        expect(await horizontalOverflow(page)).toBeLessThanOrEqual(0);
      });

      test(`every bag control is at least 44 px at ${width} px`, async ({ page }) => {
        await useStubApi(page);
        await addFromProductPage(page);

        await page.setViewportSize({ width, height: 900 });
        await openBagPage(page, 1);

        // Each clause counted separately — see `everyClauseIsBigEnough` and issue #80.
        await everyClauseIsBigEnough(page, [
          '.qty__step',
          '.qty__input',
          '.bag__actions button',
          '.bag__continue',
        ]);
      });

      test(`the mini bag fits at ${width} px with no horizontal scroll`, async ({ page }) => {
        await useStubApi(page);
        await page.setViewportSize({ width, height: 900 });
        await addFromProductPage(page);

        await expect(page.locator('sd-mini-bag [role="dialog"]')).toBeVisible();
        expect(await horizontalOverflow(page)).toBeLessThanOrEqual(0);

        await everyClauseIsBigEnough(page, ['.mini-bag__remove', '.mini-bag__foot .btn']);
      });
    }
  });
});

/** Whether focus is inside the open mini bag's panel. */
async function focusIsInside(page: Page): Promise<boolean> {
  return page.evaluate(() => {
    // The mini bag's own panel, not any `[role="dialog"]`: the mobile navigation drawer is mounted and
    // hidden on every page and carries the same role, so a bare role query answers about the wrong
    // element — a mistake the product page's suite has already made once.
    const panel = document.querySelector('sd-mini-bag [role="dialog"]');
    return !!panel && !!document.activeElement && panel.contains(document.activeElement);
  });
}
