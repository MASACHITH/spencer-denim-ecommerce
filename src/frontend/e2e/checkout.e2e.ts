import { Browser, Page, expect, test } from '@playwright/test';

import { apiURL } from '../playwright.config';
import {
  edgesWithoutRing,
  horizontalOverflow,
  undersized,
  unreachable,
} from './support/measure';

/**
 * Browser checks for checkout and the order success page (CHK-01..04).
 *
 * `checkout-page.spec.ts` and `order-page.spec.ts` already pin the markup, the field rules, every
 * refusal and the summary arithmetic in jsdom. What needs a real browser is the part jsdom has no
 * opinion about: layout at eight real viewports, a real focus model, a real cookie jar, the keyboard,
 * and — the one that matters most — **the HTML the server puts on the wire before a line of JavaScript
 * has run**, because a checkout document holds a name, an email address, a phone number and a street
 * address, and what is in that document is a privacy question rather than a styling one.
 *
 * The API is `support/catalog-api-stub.mjs`, and **it refuses what the real API refuses**: 400 with a
 * `ValidationProblem` naming every missing field (including the nested `address.line1` form), 400 on
 * an email that is not an address or a delivery option with no rate, 409 with `blockers` on a bag that
 * cannot be checked out, 409 with `totals` when the total has moved (G-10), 409 with `shortLines` when
 * the stock has gone, and **200** rather than 201 on a repeated `checkoutKey`. A stub that answered 200
 * to all of those would make every failure path in this file unreachable, which is exactly how a search
 * stub once hid a 400 for a whole task.
 */

/** docs/04 §4 and the project's non-negotiable list. */
const BREAKPOINTS = [320, 375, 390, 768, 1024, 1280, 1440, 1920] as const;

/** Doc 08 §9's seeded districts, as the stub holds them. */
const COLOMBO = 1;
const JAFFNA = 10;

/** Doc 08 §9's enabled methods. */
const STANDARD = 1;
const EXPRESS = 2;

/**
 * Sends the browser's own `/api` calls to the stub, cart and checkout included.
 *
 * `route.fetch` + `route.fulfill` rather than a proxy, for the reason `bag.e2e.ts` gives: the response's
 * `Set-Cookie` is handed back to the page as though it came from its own origin, which is what makes the
 * bag survive from the product page to checkout at all.
 */
async function useStubApi(page: Page): Promise<void> {
  await page.route('**/api/**', async (route) => {
    const url = new URL(route.request().url());
    const response = await route.fetch({ url: `${apiURL}${url.pathname}${url.search}` });
    await route.fulfill({ response });
  });
}

/** Puts a variant's stock where a test needs it. Reset per variant in `afterEach`. */
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
 * Per variant, never `reset=all`: the stub's override map is shared and this suite is `fullyParallel`,
 * so clearing the whole map would wipe a neighbour's override out from under it. `bag.e2e.ts` learned
 * that the expensive way — six failures in a full run, every one of them green on its own.
 */
async function resetStock(page: Page, variantId: number): Promise<void> {
  await page.request.get(`${apiURL}/__test__/cart-variant?reset=variant&variantId=${variantId}`);
}

/** The variant id the stub derives from a product index: Indigo/S is the first variant of each. */
function indigoSmall(index: number): number {
  return index * 1000 + 1;
}

/**
 * **A product index of its own per test, and never a multiple of 11.**
 *
 * The stub sells out every product whose index is a multiple of 11 (`availableFor`), so a test using one
 * finds its only line `OutOfStock` and Place order correctly disabled — which is how index 22 made all
 * eight success-page breakpoints fail with "expected enabled" against perfectly good code. A product per
 * test is the other half of the rule: the stub's stock switch is one shared map and this suite is
 * `fullyParallel`, so two tests overriding one variant are really testing each other (`bag.e2e.ts` paid
 * for that lesson first, with six failures that all passed in isolation).
 */
function usableIndex(index: number): number {
  if (index % 11 === 0) {
    throw new Error(`product index ${index} is sold out in the stub; pick another`);
  }

  return index;
}

/**
 * Adds one Indigo/S to the bag from a product page, the way a shopper does.
 *
 * Hydration first, and `toBeEnabled()` is not it: the Add to Bag button is server-rendered enabled when
 * the URL settles a variant, so an actionability check passes on a button whose handler does not exist
 * yet. Recording the visit (PDP-07) is the last thing the product page does, so the `localStorage` entry
 * appearing is hydration having happened — the same gate `bag.e2e.ts` uses and for the same reason.
 */
async function addToBag(page: Page, index: number, size = 10): Promise<void> {
  const slug = `test-product-${usableIndex(index)}`;
  await page.goto(`/product/${slug}?colour=1&size=${size}`);
  await expect
    .poll(() => page.evaluate(() => localStorage.getItem('sd.recently-viewed') ?? ''))
    .toContain(slug);

  const add = page.locator('[data-testid="add-to-bag"]');
  await expect(add).toBeEnabled();
  await add.click();

  await expect(page.locator('sd-mini-bag [role="dialog"] .mini-bag__line')).toHaveCount(1);
}

/**
 * Opens `/checkout` and waits until the page has applied **its own** data.
 *
 * The page is server-rendered, so the form is in the document before a line of JavaScript has run —
 * waiting for an input proves the server answered, not that the page is alive. And the transfer state
 * can make the summary look right while the browser's own `GET /api/checkout` is still in flight; when
 * that lands it re-renders, and anything a test had typed into a `[value]`-bound input is overwritten.
 * `bag.e2e.ts` measured exactly that failure (about one full-suite run in three), so the gate here is
 * the response itself.
 */
async function openCheckout(page: Page, options: { lines: number }): Promise<void> {
  const read = page.waitForResponse(
    (response) =>
      new URL(response.url()).pathname === '/api/checkout' && response.request().method() === 'GET',
  );

  await page.goto('/checkout');
  await read;

  if (options.lines === 0) {
    await expect(page.locator('.checkout__empty')).toBeVisible();
    return;
  }

  await expect(page.locator('.checkout__line')).toHaveCount(options.lines);
  await expect(page.locator('#co-firstName')).toBeVisible();
}

/** Chooses a district and waits for the re-quote the change triggers. */
async function chooseDistrict(page: Page, districtId: number): Promise<void> {
  const read = page.waitForResponse(
    (response) =>
      new URL(response.url()).pathname === '/api/checkout'
      && new URL(response.url()).searchParams.get('districtId') === String(districtId),
  );

  await page.locator('#co-district').selectOption(String(districtId));
  await read;
}

/**
 * The re-quote a method change triggers, **armed but not awaited** — so a caller can start it before the
 * interaction that causes it. A waiter registered afterwards misses a response that has already landed,
 * which is a timeout rather than a failure and tells you nothing about the page.
 */
function quoteFor(page: Page, methodId: number): Promise<unknown> {
  return page.waitForResponse(
    (response) =>
      new URL(response.url()).pathname === '/api/checkout'
      && new URL(response.url()).searchParams.get('deliveryMethodId') === String(methodId),
  );
}

/** Chooses a delivery method and waits for the re-quote, so the fee on screen is the quoted one. */
async function chooseMethod(page: Page, methodId: number): Promise<void> {
  const read = page.waitForResponse(
    (response) =>
      new URL(response.url()).pathname === '/api/checkout'
      && new URL(response.url()).searchParams.get('deliveryMethodId') === String(methodId),
  );

  await page.locator(`input[name="deliveryMethodId"][value="${methodId}"]`).check();
  await read;
}

/** Fills in the contact and address fields with something valid. */
async function fillDetails(page: Page): Promise<void> {
  await page.locator('#co-firstName').fill('Nimali');
  await page.locator('#co-lastName').fill('Perera');
  await page.locator('#co-email').fill('nimali@example.com');
  await page.locator('#co-phone').fill('0771234567');
  await page.locator('#co-recipient').fill('Nimali Perera');
  await page.locator('#co-address-phone').fill('0771234567');
  await page.locator('#co-line1').fill('14 Galle Road');
  await page.locator('#co-city').fill('Kollupitiya');
}

/** A bag, a checkout, a district and a method: everything short of pressing Place order. */
async function readyToPlace(page: Page, index: number): Promise<void> {
  await addToBag(page, index);
  await openCheckout(page, { lines: 1 });
  await chooseDistrict(page, COLOMBO);
  await chooseMethod(page, STANDARD);
  await fillDetails(page);
  await expect(page.locator('.checkout__place')).not.toHaveAttribute('aria-disabled', 'true');
}

/** The money figures, read off the rendered DOM rather than taken from a fixture. */
async function sums(page: Page): Promise<Record<string, number>> {
  return page.evaluate(() => {
    const read = (name: string): number => {
      const node = document.querySelector(`[data-sum="${name}"]`);
      if (!node) {
        return 0;
      }
      const matched = /[\d,]+(?:\.\d+)?/.exec((node.textContent ?? '').replace('Rs.', ''));
      return matched ? Number(matched[0].replace(/,/g, '')) : 0;
    };

    return {
      subtotal: read('subtotal'),
      savings: read('savings'),
      promo: read('promo'),
      delivery: read('delivery'),
      deliveryDiscount: read('delivery-discount'),
      total: read('total'),
    };
  });
}

/**
 * Measures a selector, refusing to pass when it matched nothing.
 *
 * **Issue #80**: `undersized` returns `[]` both when every control clears the 44 px floor and when the
 * selector matched nothing at all, so `expect(await undersized(page, sel)).toEqual([])` passes on a page
 * with no such element. #80 fixes the helper and is deliberately not fixed here — but a call site added
 * today must not be one of the vacuous ones, so every clause is counted **separately**: the guard
 * everyone reaches for (`count() > 0` on the whole compound selector) is satisfied by the first clause
 * alone while a later one matches nothing, which is live today in `search.e2e.ts:576`.
 */
async function everyClauseIsBigEnough(page: Page, clauses: readonly string[]): Promise<void> {
  for (const clause of clauses) {
    await expect(
      page.locator(clause).first(),
      `nothing matched ${clause}, so nothing was measured`,
    ).toBeVisible();

    // **Big enough to press *and* on the screen to press.** `undersized` answers only the first, which
    // is how QV-01 found a 48 px Add to Bag eight pixels below the fold passing a loop like this one.
    // `unreachable` is its helper, reused rather than reinvented, and it throws rather than passing when
    // its selector matches nothing.
    expect(await unreachable(page, clause), clause).toEqual([]);
  }

  expect(await undersized(page, clauses.join(', '))).toEqual([]);
}

/** A visitor with a bag, and the HTML the server put on the wire for their checkout. */
async function checkoutHtmlFor(browser: Browser, baseURL: string, index: number): Promise<string> {
  const context = await browser.newContext();
  const page = await context.newPage();
  await useStubApi(page);
  await addToBag(page, index);

  const html = await (await page.request.get(`${baseURL}/checkout`)).text();
  await context.close();

  return html;
}

test.describe('checkout (CHK-01..04)', () => {
  test.beforeEach(async ({ page }) => {
    await useStubApi(page);
  });

  test.describe('getting there', () => {
    /**
     * The bag's Proceed to checkout button, which W3-02 added. The bag page's own suite pins the
     * disabled state; what needs a browser is that the link actually arrives somewhere.
     */
    test('the bag leads to checkout', async ({ page }) => {
      await addToBag(page, 2);
      await page.goto('/bag');

      const proceed = page.locator('a.bag__checkout');
      await expect(proceed).toBeVisible();
      await proceed.click();

      await expect(page).toHaveURL(/\/checkout$/);
      await expect(page.locator('#co-firstName')).toBeVisible();
    });

    /** G-08: the button is a disabled control, not a link, while a line cannot be bought. */
    test('the bag refuses to lead to checkout while a line cannot be bought', async ({ page }) => {
      const index = 3;
      const variantId = indigoSmall(index);

      await addToBag(page, index);
      await setStock(page, variantId, { available: 0 });
      await page.goto('/bag');

      await expect(page.locator('a.bag__checkout')).toHaveCount(0);
      await expect(page.locator('button.bag__checkout')).toBeDisabled();
      await expect(page.locator('#bag-checkout-blocked')).toContainText('before you check out');

      await resetStock(page, variantId);
    });

    test('an empty bag says so rather than showing a form', async ({ page }) => {
      await openCheckout(page, { lines: 0 });

      await expect(page.locator('.checkout__empty')).toContainText('bag is empty');
      await expect(page.locator('#co-firstName')).toHaveCount(0);
    });
  });

  test.describe('the order summary', () => {
    /**
     * **The ladder has to add up on screen, and three `toContain`s cannot say that.** The figures are
     * read back out of the rendered DOM and put through the identity, because this project has already
     * shipped a summary in which Subtotal − Discount did not equal Total.
     */
    test('adds up: Subtotal − Savings + Delivery − Free delivery = Total', async ({ page }) => {
      await addToBag(page, 4);
      await openCheckout(page, { lines: 1 });
      await chooseDistrict(page, COLOMBO);
      await chooseMethod(page, STANDARD);

      const { subtotal, savings, promo, delivery, deliveryDiscount, total } = await sums(page);

      expect(total).toBeGreaterThan(0);
      expect(subtotal - savings - promo + delivery - deliveryDiscount).toBe(total);
    });

    /**
     * **Null is not zero.** Doc 08 §6 step 10 has no fee without a district, and a Delivery row reading
     * `Rs. 0` would be a promise of free delivery. Measured on the screen, because that is where the
     * promise would be made.
     */
    test('asks for a district rather than printing a delivery fee of zero', async ({ page }) => {
      await addToBag(page, 5);
      await openCheckout(page, { lines: 1 });

      await expect(page.locator('[data-sum="delivery-pending"]')).toContainText('Choose a district');
      await expect(page.locator('[data-sum="total-pending"]')).toContainText('Choose a district');
      await expect(page.locator('[data-sum="delivery"]')).toHaveCount(0);
      await expect(page.locator('[data-sum="total"]')).toHaveCount(0);
    });

    /** Doc 08 §9's Colombo Standard rate, which is the figure the API would have given. */
    test('prices Standard to Colombo from the seeded zone rate', async ({ page }) => {
      await addToBag(page, 6);
      await openCheckout(page, { lines: 1 });
      await chooseDistrict(page, COLOMBO);

      const standard = page.locator('.checkout__choice', { hasText: 'Standard delivery' });
      await expect(standard).toContainText('Rs. 400');
      await expect(standard).toContainText('Estimated 1–2 days');
      await expect(standard).toContainText('Free above Rs. 10,000');
    });

    /**
     * Doc 08 §9 makes Express unavailable island-wide, with a row saying so rather than no row. The
     * option is listed and unselectable, which is what stops a shopper who used it last week reading a
     * silently shorter list as a bug.
     */
    test('offers Express in Colombo and refuses it island-wide', async ({ page }) => {
      await addToBag(page, 7);
      await openCheckout(page, { lines: 1 });

      await chooseDistrict(page, COLOMBO);
      await expect(page.locator(`input[name="deliveryMethodId"][value="${EXPRESS}"]`)).toBeEnabled();

      await chooseDistrict(page, JAFFNA);
      const express = page.locator(`input[name="deliveryMethodId"][value="${EXPRESS}"]`);
      await expect(express).toBeDisabled();
      await expect(page.locator('.checkout__choice', { hasText: 'Express delivery' })).toContainText(
        'Not available here',
      );
    });

    /**
     * Changing the district must drop the chosen method rather than carry it over: doc 11 P6's "this
     * delivery option is no longer available, choose another", applied before the shopper can submit it.
     */
    test('a district change clears the chosen delivery method', async ({ page }) => {
      await addToBag(page, 8);
      await openCheckout(page, { lines: 1 });
      await chooseDistrict(page, COLOMBO);
      await chooseMethod(page, EXPRESS);

      await expect(page.locator('[data-sum="total"]')).toBeVisible();

      await chooseDistrict(page, JAFFNA);

      await expect(page.locator(`input[name="deliveryMethodId"][value="${EXPRESS}"]`)).not.toBeChecked();
      await expect(page.locator('[data-sum="total-pending"]')).toBeVisible();

      // `aria-disabled`, not `disabled`: the button stays reachable and says why it will not work, which
      // is the bag page's pattern and the fix for a focused control that `disabled` throws to BODY.
      await expect(page.locator('.checkout__place')).toHaveAttribute('aria-disabled', 'true');
      await expect(page.locator('#checkout-place-blocked')).toBeVisible();
    });
  });

  test.describe('placing the order', () => {
    test('a guest can place an order and lands on the success page', async ({ page }) => {
      await readyToPlace(page, 9);

      const placed = page.waitForResponse(
        (response) =>
          new URL(response.url()).pathname === '/api/checkout/orders'
          && response.request().method() === 'POST',
      );

      await page.locator('.checkout__place').click();
      expect((await placed).status()).toBe(201);

      await expect(page).toHaveURL(/\/order\/[0-9a-f-]{36}$/);
      await expect(page.locator('[data-order-number]')).toHaveText(/^SD-\d{4}-\d{6}$/);
      await expect(page.locator('h1')).toContainText('order is placed');

      // CHK-04 says the order number and that the order is awaiting payment.
      //
      // **The three `not.toContain` assertions that used to be here are gone, and that is W3-03 landing.**
      // They read "no account number, no upload, no 'we have emailed'" and were a correct boundary marker
      // for a page that genuinely had nothing true to say about money. Doc 11 P7 is now built, so the
      // first two describe exactly what the page must show, and asserting their absence would hold the
      // feature out of it. `bank-transfer.e2e.ts` asserts the panel positively, in the browser, and keeps
      // the third assertion — nothing sends an email (`ops.EmailOutbox` is W3-06), so nothing may claim
      // one was sent.
      const body = (await page.locator('body').textContent())?.toLowerCase() ?? '';
      expect(body).toContain('awaiting payment');
      expect(body).not.toContain('we have emailed');
    });

    /** The bag is emptied by the placement, so the header badge must stop counting the items. */
    test('the bag is empty afterwards and the badge says so', async ({ page }) => {
      await readyToPlace(page, 10);

      // Armed before the click, for the reason `quoteFor` gives: the page re-reads the bag as soon as the
      // order is placed, and a waiter registered afterwards can miss a response that has already landed.
      const reread = page.waitForResponse(
        (response) =>
          new URL(response.url()).pathname === '/api/cart' && response.request().method() === 'GET',
      );

      await page.locator('.checkout__place').click();
      await expect(page).toHaveURL(/\/order\//);
      await reread;

      await expect(page.locator('[data-testid="bag-toggle"] .count')).toHaveCount(0);

      await page.goto('/bag');
      await expect(page.locator('.bag__empty')).toBeVisible();
    });

    /**
     * **The success page survives a reload**, which is the whole reason it reads the order back by its
     * token instead of being handed it. A confirmation rendered from the POST's response would be a blank
     * page here.
     */
    test('the success page can be reloaded and shared', async ({ page }) => {
      await readyToPlace(page, 12);

      await page.locator('.checkout__place').click();
      await expect(page).toHaveURL(/\/order\//);

      const orderNumber = await page.locator('[data-order-number]').textContent();
      const url = page.url();

      await page.reload();
      await expect(page.locator('[data-order-number]')).toHaveText(orderNumber!.trim());

      // And the browser's back button does not return to a checkout whose bag is gone: the navigation
      // replaced it.
      await page.goBack();
      await expect(page).not.toHaveURL(/\/checkout$/);

      await page.goto(url);
      await expect(page.locator('[data-order-number]')).toHaveText(orderNumber!.trim());
    });

    /** A token that names no order is a real 404, not a 200 saying "not found". */
    test('an order link that names no order answers 404', async ({ page }) => {
      const response = await page.goto('/order/11111111-2222-4333-8444-555555555555');

      expect(response?.status()).toBe(404);
      await expect(page.locator('h1')).toContainText('could not find that order');
    });

    /**
     * **The double submit, in a browser.** The button disables itself on the first press, so the
     * interesting case is the one a flaky connection produces: the same `checkoutKey` arriving twice.
     * The second answer is a 200 carrying the first order, and the page says so rather than claiming a
     * second order was placed.
     */
    test('re-submitting the same checkout says nothing was placed twice', async ({ page }) => {
      await readyToPlace(page, 13);

      // The body the page is about to send, captured from the request itself rather than rebuilt — a
      // hand-written copy would be a test of the test.
      const first = page.waitForRequest(
        (request) =>
          new URL(request.url()).pathname === '/api/checkout/orders' && request.method() === 'POST',
      );

      await page.locator('.checkout__place').click();
      const body = (await first).postData();
      await expect(page).toHaveURL(/\/order\//);

      const replay = await page.request.post(`${apiURL}/api/checkout/orders`, {
        data: JSON.parse(body!),
        headers: { 'Content-Type': 'application/json' },
      });

      // 200, not 201: the request created nothing.
      expect(replay.status()).toBe(200);
      const replayed = await replay.json();
      expect(replayed.isDuplicateSubmission).toBe(true);

      // **The 200 is the whole signal, and the order page deliberately shows nothing about it.**
      // `isDuplicateSubmission` describes a submission, not an order, so reading the order back by its
      // token answers `false` — which is why the page has no notice to find. The first version of this
      // test looked for one and could not have passed.
      expect(replayed.orderNumber).toBe(await page.locator('[data-order-number]').textContent());

      await page.goto(`/order/${replayed.publicId}`);
      await expect(page.locator('[data-order-number]')).toHaveText(replayed.orderNumber);
      await expect(page.locator('body')).not.toContainText('already placed');
    });

    /** The button refuses a second press while the first is in flight. */
    test('the button says it is working and cannot be pressed twice', async ({ page }) => {
      await readyToPlace(page, 14);

      // Held open, so the in-flight state is observable rather than a race to look at.
      let release: () => void = () => {};
      const held = new Promise<void>((resolve) => {
        release = resolve;
      });

      await page.route('**/api/checkout/orders', async (route) => {
        await held;
        const url = new URL(route.request().url());
        await route.fulfill({
          response: await route.fetch({ url: `${apiURL}${url.pathname}${url.search}` }),
        });
      });

      await page.locator('.checkout__place').click();

      await expect(page.locator('.checkout__place')).toHaveAttribute('aria-disabled', 'true');
      await expect(page.locator('.checkout__place')).toContainText('Placing your order');

      release();
      await expect(page).toHaveURL(/\/order\//);
    });
  });

  test.describe('when the shop refuses it', () => {
    /**
     * Doc 08 §5.4 step 3: the order is refused whole and the shopper is told which item is short and how
     * many are left.
     *
     * **The stub's fault switch is not a convenience, it is the only way in from a browser.** Both the
     * API and the stub re-read the bag immediately before claiming the stock, so a line that has already
     * gone is caught first as G-08's "adjust your bag" and answered with `blockers` — which is the right
     * answer, and is the next test. The `shortLines` answer exists only when the stock goes in the window
     * *between* that read and the claim, which is a race one browser cannot create. Its reachability is
     * proved where races belong, in `CheckoutConcurrencyTests` (four shoppers, one unit); what this proves
     * is that the page renders the refusal, against a body the stub builds with its own code rather than
     * one written out in a test.
     */
    test('names the sold-out line and places nothing', async ({ page }) => {
      await readyToPlace(page, 15);

      const fault = await page.request.get(`${apiURL}/__test__/checkout-stock-fault?mode=short`);
      expect(fault.ok()).toBe(true);

      await page.locator('.checkout__place').click();

      // `.checkout__error`, not `[role="alert"]`: the shell has live regions of its own (the newsletter
      // status among them), so a bare role selector is a strict-mode violation rather than a test.
      await expect(page.locator('.checkout__error')).toContainText('sold out');
      await expect(page.locator('.checkout__error')).toContainText('nothing has been charged');
      // **Names the line, not just "something sold out".** The stub's fault leaves one fewer than the bag
      // asked for, so a bag of one reads "sold out" and a bag of two reads "only 1 left"; the per-variant
      // wording of both is pinned in `checkout-page.spec.ts`, and what matters here is that the figures
      // reached the right line on a real screen.
      const short = page.locator('.checkout__summary .checkout__notice--warn');
      await expect(short).toContainText('Test Product 15');
      await expect(short).toContainText('Indigo / S');
      await expect(short).toContainText('sold out');
      await expect(page).toHaveURL(/\/checkout$/);
    });

    /**
     * G-08's answer, which is the one a shopper actually meets when the stock goes: the whole checkout is
     * refused with the reason, nothing is trimmed, and the way back to the bag is on the page.
     */
    test('refuses the whole order when a line has gone, and points back to the bag', async ({
      page,
    }) => {
      const index = 31;
      const variantId = indigoSmall(index);

      await readyToPlace(page, index);
      await setStock(page, variantId, { available: 0 });

      await page.locator('.checkout__place').click();

      await expect(page.locator('.checkout__error')).toContainText('cannot be bought right now');
      await expect(page).toHaveURL(/\/checkout$/);

      // The way back is on the blocker notice at the top of the page, not in the summary: the summary's
      // link belongs to the sold-out list, which this refusal does not produce.
      await expect(page.locator('.checkout__notice--warn a[href="/bag"]').first()).toBeVisible();

      await resetStock(page, variantId);
    });

    /**
     * G-10: "we never place an order at a total the customer has not seen". The request is rewritten in
     * flight to carry a stale total, which is what a campaign ending mid-checkout looks like from the
     * server's side.
     */
    test('shows the new total and does not retry when the price has moved', async ({ page }) => {
      await readyToPlace(page, 16);

      await page.route('**/api/checkout/orders', async (route) => {
        const sent = JSON.parse(route.request().postData() ?? '{}');
        const url = new URL(route.request().url());

        await route.fulfill({
          response: await route.fetch({
            url: `${apiURL}${url.pathname}${url.search}`,
            // One rupee away from the quote: enough to be refused, close enough that nothing else
            // about the request changes.
            postData: JSON.stringify({ ...sent, expectedGrandTotal: sent.expectedGrandTotal + 1 }),
          }),
        });
      });

      await page.locator('.checkout__place').click();

      await expect(page.locator('.checkout__error')).toContainText('total has changed');
      await expect(page).toHaveURL(/\/checkout$/);
      await expect(page.locator('[data-sum="total"]')).toBeVisible();
    });

    /**
     * **The server's field errors land against the right inputs.** The request is rewritten to drop two
     * fields, so the 400 is the API's own `ValidationProblem` with the nested `address.line1` key — the
     * shape the page has to be able to place.
     */
    test('puts each server field error against its own input', async ({ page }) => {
      await readyToPlace(page, 17);

      await page.route('**/api/checkout/orders', async (route) => {
        const sent = JSON.parse(route.request().postData() ?? '{}');
        const url = new URL(route.request().url());

        await route.fulfill({
          response: await route.fetch({
            url: `${apiURL}${url.pathname}${url.search}`,
            postData: JSON.stringify({
              ...sent,
              email: 'not-an-address',
              address: { ...sent.address, line1: '   ' },
            }),
          }),
        });
      });

      await page.locator('.checkout__place').click();

      await expect(page.locator('#co-email-error')).toContainText('email address');
      await expect(page.locator('#co-email')).toHaveAttribute('aria-invalid', 'true');
      await expect(page.locator('#co-line1-error')).toContainText('street address');
      await expect(page).toHaveURL(/\/checkout$/);

      // Correcting the field clears its message, so the form does not keep scolding a fixed value.
      await page.locator('#co-email').fill('nimali@example.lk');
      await expect(page.locator('#co-email-error')).toHaveCount(0);
      await expect(page.locator('#co-email')).not.toHaveAttribute('aria-invalid', 'true');
    });
  });

  test.describe('a refused submission', () => {
    /**
     * **It used to be announced to nobody.** Pressing Place order with nothing filled in drew three red
     * field messages, said nothing, and left focus on BODY — WCAG 2.2 4.1.3, which axe cannot see (it
     * reported zero violations on this page). In a real browser, because "focus stayed on BODY" is
     * something only a real browser can be asked about.
     */
    test('is announced, names the district, and takes focus', async ({ page }) => {
      await addToBag(page, 24);
      await openCheckout(page, { lines: 1 });

      // **Reachable, which `disabled` was not.** A disabled button is out of the tab order, carried no
      // reason, and throws a focused control's focus to BODY (ADM-22).
      const place = page.locator('.checkout__place');
      await expect(place).toHaveAttribute('aria-disabled', 'true');
      await expect(page.locator('#checkout-place-blocked')).toBeVisible();

      // The DOM property, not `toBeDisabled()`: Playwright treats `aria-disabled` as disabled for its
      // own actionability, which is the right default and the wrong question here. What matters is that
      // the element is a real, focusable control.
      expect(await place.evaluate((node: HTMLButtonElement) => node.disabled)).toBe(false);

      // Pressed with the keyboard, which is the user this fix is for — and which also sidesteps
      // Playwright's actionability wait on an `aria-disabled` control.
      await place.focus();
      await expect(place).toBeFocused();
      await page.keyboard.press('Enter');

      const refused = page.locator('#checkout-refused');
      await expect(refused).toBeVisible();
      await expect(refused).toHaveAttribute('role', 'alert');

      // The district is the field that used to be named nowhere: `missingFields()` set
      // `deliveryMethodId` and returned, and there is no delivery-method control until a district exists.
      await expect(refused).toContainText('Choose the district your order is going to');
      await expect(refused).toBeFocused();

      // And the entry takes you to the control, which is the other half of naming the blocking field.
      await refused.getByRole('button', { name: /Choose the district/ }).click();
      await expect(page.locator('#co-district')).toBeFocused();
    });

    /** It is computed from the live errors, so it empties itself rather than going stale. */
    test('clears itself as the fields are filled in', async ({ page }) => {
      await addToBag(page, 25);
      await openCheckout(page, { lines: 1 });

      // `force`, because Playwright treats `aria-disabled` as disabled and would wait for ever: the
      // press being a no-op that *answers* is the behaviour under test.
      await page.locator('.checkout__place').click({ force: true });
      await expect(page.locator('#checkout-refused')).toBeVisible();

      await chooseDistrict(page, COLOMBO);
      await chooseMethod(page, STANDARD);
      await fillDetails(page);

      await expect(page.locator('#checkout-refused')).toHaveCount(0);
      await expect(page.locator('.checkout__place')).not.toHaveAttribute('aria-disabled', 'true');
    });
  });

  test.describe('the keyboard', () => {
    /**
     * **Driven for real**, which is the point: every control reached with Tab, the radios chosen with
     * the arrow keys the way a radio group works, and the order placed with Enter from inside a field
     * rather than by clicking the button. That last part is why the submit button is associated with the
     * form by `form=` rather than nested in it — without that, Enter does nothing.
     */
    test('a whole order can be placed without a mouse', async ({ page }) => {
      await addToBag(page, 18);
      await openCheckout(page, { lines: 1 });

      // The district first, because the delivery options do not exist until it is chosen.
      await page.locator('#co-district').focus();
      await chooseDistrict(page, COLOMBO);

      // Tab from the district to the first delivery radio, and choose it with the keyboard.
      //
      // **Every waiter is armed before the key press, never after.** A response that lands between the
      // press and the `waitForResponse` call is one the waiter never sees, and the test then waits for a
      // re-quote that has already happened until it times out. That is what this test did first.
      await page.locator('#co-district').focus();
      await page.keyboard.press('Tab');
      await expect(page.locator(`input[name="deliveryMethodId"][value="${STANDARD}"]`)).toBeFocused();

      const chosen = quoteFor(page, STANDARD);
      await page.keyboard.press('Space');
      await chosen;
      await expect(
        page.locator(`input[name="deliveryMethodId"][value="${STANDARD}"]`),
      ).toBeChecked();

      // ArrowDown moves within the radio group, which is how a radio group is meant to work — and it
      // selects as it moves, so it has to be moved back.
      const toExpress = quoteFor(page, EXPRESS);
      await page.keyboard.press('ArrowDown');
      await toExpress;
      await expect(page.locator(`input[name="deliveryMethodId"][value="${EXPRESS}"]`)).toBeChecked();

      const backToStandard = quoteFor(page, STANDARD);
      await page.keyboard.press('ArrowUp');
      await backToStandard;
      await expect(
        page.locator(`input[name="deliveryMethodId"][value="${STANDARD}"]`),
      ).toBeChecked();

      // The fields, typed rather than filled, so the input events are the ones a keyboard produces.
      for (const [selector, value] of [
        ['#co-firstName', 'Nimali'],
        ['#co-email', 'nimali@example.com'],
        ['#co-phone', '0771234567'],
        ['#co-recipient', 'Nimali Perera'],
        ['#co-address-phone', '0771234567'],
        ['#co-line1', '14 Galle Road'],
        ['#co-city', 'Kollupitiya'],
      ] as const) {
        await page.locator(selector).focus();
        await page.keyboard.type(value);
      }

      await expect(page.locator('.checkout__place')).not.toHaveAttribute('aria-disabled', 'true');

      // **Enter from inside a field**, not a click on the button.
      await page.locator('#co-city').focus();
      await page.keyboard.press('Enter');

      await expect(page).toHaveURL(/\/order\//);
      await expect(page.locator('[data-order-number]')).toHaveText(/^SD-\d{4}-\d{6}$/);
    });

    /**
     * Every focusable control shows a ring **on the screen**, measured by photographing the strip just
     * outside each edge with the control focused and again with it blurred. The obvious assertion —
     * reading `outlineWidth` off the computed style — is blind to an ancestor clipping the ring away,
     * which is lesson L4 and has already passed over a cropped ring on this project.
     */
    test('every control shows a focus ring on the screen', async ({ page }) => {
      await page.setViewportSize({ width: 1280, height: 900 });
      await addToBag(page, 19);
      await openCheckout(page, { lines: 1 });
      await chooseDistrict(page, COLOMBO);
      await chooseMethod(page, STANDARD);
      await fillDetails(page);

      for (const selector of ['#co-firstName', '#co-email', '#co-district', '#co-line1']) {
        const control = page.locator(selector);
        await control.focus();
        expect(
          await edgesWithoutRing(page, control),
          `${selector} has no visible focus ring on these edges`,
        ).toEqual([]);
      }

      // **The button has to be reached with Tab, not with `focus()`.** Chrome grants `:focus-visible` on
      // a programmatic focus to controls that take typed input and *not* to a button, so `.focus()` left
      // the Place order button focused with no ring and the test reported a missing ring that a keyboard
      // user would never meet. Tabbing from the last field is how a shopper gets there anyway.
      await page.locator('#co-note').focus();
      await page.keyboard.press('Tab');

      const place = page.locator('.checkout__place');
      await expect(place).toBeFocused();
      expect(
        await edgesWithoutRing(page, place),
        'the Place order button has no visible focus ring on these edges',
      ).toEqual([]);

      // **The radio's ring is painted on its row**, by `.checkout__choice:has(:focus-visible)`: a 2.5 px
      // outline around a 20 px dot inside a bordered box is almost invisible, which is lesson L4 from the
      // other side. So the strips are photographed around the row while the radio is the thing focused
      // and blurred. Measuring the radio itself reported "no ring on any edge" for a ring that was
      // perfectly visible — which is how `edgesWithoutRing`'s `ringAround` came to exist.
      const radio = page.locator(`input[name="deliveryMethodId"][value="${STANDARD}"]`);
      const row = page.locator('.checkout__choice', { hasText: 'Standard delivery' });
      await radio.focus();
      expect(
        await edgesWithoutRing(page, radio, { ringAround: row }),
        'the chosen delivery row has no visible focus ring on these edges',
      ).toEqual([]);
    });
  });

  test.describe('responsive layout', () => {
    for (const width of BREAKPOINTS) {
      test(`has no horizontal scroll at ${width} px`, async ({ page }) => {
        await page.setViewportSize({ width, height: 900 });
        await addToBag(page, 20);
        await openCheckout(page, { lines: 1 });
        await chooseDistrict(page, COLOMBO);
        await chooseMethod(page, STANDARD);
        await fillDetails(page);

        expect(await horizontalOverflow(page), `${width} px overflows`).toBe(0);

        // And with a message on screen, which adds rows to both columns at once — the state the first
        // version of this layout broke at 320.
        await page.locator('#co-email').fill('nope');
        await expect(page.locator('#co-email-error')).toBeVisible();
        expect(await horizontalOverflow(page), `${width} px overflows with an error shown`).toBe(0);

        // **And with the refusal summary up**, which is a block of full-width buttons the layout did not
        // have before. It is measured here because the summary's box takes its grid from
        // `.checkout__notice` and carries no `min-width: 0` of its own — this is what says that is safe.
        await page.locator('.checkout__place').click({ force: true });
        await expect(page.locator('#checkout-refused')).toBeVisible();
        expect(
          await horizontalOverflow(page),
          `${width} px overflows with the refusal summary shown`,
        ).toBe(0);
      });
    }

    for (const width of [320, 768, 1280] as const) {
      test(`every control clears 44 px at ${width} px`, async ({ page }) => {
        await page.setViewportSize({ width, height: 900 });
        await addToBag(page, 21);
        await openCheckout(page, { lines: 1 });
        await chooseDistrict(page, COLOMBO);
        await chooseMethod(page, STANDARD);

        await everyClauseIsBigEnough(page, [
          '.checkout__input',
          '.checkout__choice',
          '.checkout__place',
        ]);
      });
    }

    /**
     * **The same 44 px floor, measured on the page in its *refused* state**, which is where this suite
     * had a hole a new control walked straight through.
     *
     * The test above runs on the un-refused page, so the error summary — up to five focusable buttons
     * that move focus to a field — did not exist when it measured, and shipped at 22 px tall. axe does
     * not enforce SC 2.5.8 either, so nothing in the pipeline saw it. A state a control only exists in
     * has to be a state the gate visits, or the next control added there slips through the same way.
     */
    for (const width of [320, 768, 1280] as const) {
      test(`every control clears 44 px with the form refused at ${width} px`, async ({ page }) => {
        await page.setViewportSize({ width, height: 900 });
        await addToBag(page, 26);
        await openCheckout(page, { lines: 1 });
        await chooseDistrict(page, COLOMBO);
        await chooseMethod(page, STANDARD);

        // Nothing filled in, so every field is named in the summary. `force`, because the button is
        // `aria-disabled` and Playwright honours that for actionability.
        await page.locator('.checkout__place').click({ force: true });
        await expect(page.locator('#checkout-refused')).toBeVisible();

        await everyClauseIsBigEnough(page, [
          '.checkout__input',
          '.checkout__choice',
          '.checkout__place',
          '[data-testid="refused-field"]',
        ]);
      });
    }

    for (const width of BREAKPOINTS) {
      test(`the success page has no horizontal scroll at ${width} px`, async ({ page }) => {
        await page.setViewportSize({ width, height: 900 });
        await readyToPlace(page, 29);
        await page.locator('.checkout__place').click();
        await expect(page).toHaveURL(/\/order\//);
        await expect(page.locator('[data-order-number]')).toBeVisible();

        expect(await horizontalOverflow(page), `${width} px overflows`).toBe(0);
      });
    }
  });

  test.describe('the document the server sends', () => {
    /**
     * It is server-rendered, which is the point: the summary and the form are in the HTML rather than a
     * spinner, so a shopper on a slow connection sees their order before JavaScript arrives.
     */
    test('carries the form and the summary, not a spinner', async ({ browser, baseURL }) => {
      const html = await checkoutHtmlFor(browser, baseURL!, 23);

      expect(html).toContain('id="co-firstName"');
      expect(html).toContain('Order summary');
      expect(html).not.toContain('Loading your checkout');
    });

    test('asks not to be indexed', async ({ browser, baseURL }) => {
      const html = await checkoutHtmlFor(browser, baseURL!, 24);

      expect(html).toMatch(/<meta[^>]+name="robots"[^>]+content="noindex"/);
    });

    /**
     * GLB-11's rule on the page that fetches the most per-visitor data. **The storefront has leaked
     * `API_INTERNAL_URL` into the HTML once already**, which is why this is measured on every such page
     * rather than once.
     */
    test('never names the internal API address', async ({ browser, baseURL }) => {
      const html = await checkoutHtmlFor(browser, baseURL!, 25);

      expect(html).not.toContain(new URL(apiURL).host);
      expect(html).not.toContain('API_INTERNAL_URL');
    });

    /**
     * And it ships the checkout **once**. The transfer state exists so the browser's first render matches
     * the server's; a second copy of the same payload is the mistake that put the catalogue in a listing
     * twice.
     *
     * These two keys exist nowhere but in a serialised `CheckoutSessionDto`, so one of each is exactly
     * one copy — counting a product name instead would only ever produce a bound somebody has to guess.
     */
    test('carries the checkout once, not twice', async ({ browser, baseURL }) => {
      const html = await checkoutHtmlFor(browser, baseURL!, 26);

      expect(html.split('"promoDiscountClamped"').length - 1).toBe(1);
      expect(html.split('"deliveryOptions"').length - 1).toBe(1);
    });

    /**
     * The cart token is not in the document, and neither is the idempotency key's being there a leak —
     * but the token is: it travels in an `HttpOnly` cookie precisely so a script cannot read it, and a
     * copy in the HTML would make the flag decorative.
     */
    test('never writes the cart token into the HTML', async ({ browser, baseURL }) => {
      const context = await browser.newContext();
      const page = await context.newPage();
      await useStubApi(page);
      await addToBag(page, 27);

      const token = (await context.cookies()).find((cookie) => cookie.name === 'sd_cart')?.value;
      expect(token).toBeTruthy();

      const html = await (await page.request.get(`${baseURL}/checkout`)).text();
      expect(html.toLowerCase()).not.toContain(token!.toLowerCase());
      expect(html).not.toContain('sd_cart');

      await context.close();
    });

    /**
     * A shared cache holding this document would hand one shopper another shopper's name, address and
     * phone number. Measured on the wire, because that is the only place the header either is or is not.
     */
    test('forbids caching the checkout and the order', async ({ browser, baseURL }) => {
      const context = await browser.newContext();
      const page = await context.newPage();
      await useStubApi(page);
      await addToBag(page, 28);

      const checkout = await page.request.get(`${baseURL}/checkout`);
      const cacheControl = checkout.headers()['cache-control'] ?? '';

      expect(cacheControl).toContain('no-store');
      expect(cacheControl).toContain('private');
      expect(checkout.headers()['vary']).toBe('Cookie');

      await context.close();
    });
  });
});
