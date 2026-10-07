import AxeBuilder from '@axe-core/playwright';
import { Page, expect, test } from '@playwright/test';

import { apiURL } from '../playwright.config';
import { horizontalOverflow, undersized, unreachable } from './support/measure';

/**
 * PAY-01 in the storefront: the "How to pay" panel on the order page (doc 11 P7 steps 1 and 3).
 *
 * **This is the half of the money a shopper sees.** The account to transfer to, the exact amount, the
 * order number to quote as the reference, the deadline, and the optional reference and slip. The API is
 * proved against a real SQL Server in `AdminBankTransferEndpointTests`; what this file proves is the
 * screen, and in particular the three things no jsdom spec can measure — nothing scrolls sideways at
 * 320 px, every control clears 44 px, and nothing is buried.
 *
 * It runs against `e2e/support/catalog-api-stub.mjs`, which places real orders and answers
 * `/api/orders/{token}` with the pay block.
 */

/** docs/04 §4 and the project's non-negotiable list. */
const BREAKPOINTS = [320, 375, 390, 768, 1024, 1280, 1440, 1920] as const;

const COLOMBO = 1;
const STANDARD = 1;

/**
 * Waits until the application has bootstrapped.
 *
 * **An interaction before this point goes into static HTML.** The order page is server-rendered, so the
 * reference box and the Save button are on screen — and actionable, as far as Playwright is concerned —
 * before any handler of theirs exists. `ng-version` on the root element is the moment Angular bootstrapped,
 * which is `home-page.e2e.ts`' gate and the same reasoning: nothing on this page *looks* different when it
 * hydrates, so there is no rendered state to wait for and the framework's own marker is the honest signal.
 *
 * **It is a first-order gate and not a guarantee, which is worth being explicit about.** Bootstrapped is
 * not the same as *this component has hydrated and is listening*, and a fill that lands in between is
 * written into the DOM and then overwritten by the component's first change detection, which puts the
 * server's value back. That is a real flake this file hit, it survived adding this gate, and the place it
 * is dealt with is the interaction itself — see the retry in the reference test. A property worth asserting
 * is "clearing the reference removes it", not "the first click after a reload is the one that does it".
 *
 * (`__ngContext__` on the bound input looked like the exact signal. Probed: it never appears on a
 * server-rendered element in a production build, so it is not available as a gate here.)
 */
async function hydrated(page: Page): Promise<void> {
  await page.waitForFunction(() => !!document.querySelector('sd-root')?.hasAttribute('ng-version'));
}

/**
 * Sends the browser's own relative `/api` calls to the stub.
 *
 * **Without this the page posts to the storefront's own origin and the SSR server answers 404 with an
 * HTML body**, which is how the first version of this file failed — a five-second locator timeout saying
 * only "the mini bag has no line". `API_BASE_URL` is empty in the browser by design (same origin behind
 * the reverse proxy in a deployment), so the browser tests have to route it themselves.
 *
 * `route.fetch` + `route.fulfill` rather than a proxy, for the reason `bag.e2e.ts` gives: the response's
 * `Set-Cookie` is handed back to the page as though it came from its own origin, which is what makes the
 * bag survive from the product page to checkout at all. Copied from `checkout.e2e.ts`, where it is
 * private — see the note on `placeAnOrder` about extracting both.
 */
async function useStubApi(page: Page): Promise<void> {
  await page.route('**/api/**', async (route) => {
    const url = new URL(route.request().url());
    const response = await route.fetch({ url: `${apiURL}${url.pathname}${url.search}` });
    await route.fulfill({ response });
  });
}

/**
 * Places an order through the real controls and lands on its page.
 *
 * Deliberately the real flow rather than a fabricated token: the panel is rendered from the order the
 * placement wrote, so the amount on the panel and the amount in the summary come from one place — which
 * is what makes "the panel shows the exact amount" an assertion rather than two literals agreeing.
 *
 * <b>These five steps duplicate `checkout.e2e.ts`'s private `readyToPlace` and should be one helper.</b>
 * They are copied rather than extracted because extracting them means editing a 1,050-line file that 700
 * passing browser tests depend on, and this task has no budget to re-run that suite enough times to be
 * sure. The selectors are the page's real ids, taken from that file; if they drift, both files break
 * together and whoever fixes them should extract this into `support/checkout-flow.ts` while they are
 * there. Recorded in the journal as owed.
 *
 * `index` picks the stub's product. Multiples of 11 are sold out there, so every caller below avoids them.
 */
async function placeAnOrder(
  page: Page,
  index: number,
  options: { readonly afterBag?: () => Promise<void> } = {},
): Promise<{ orderNumber: string; total: number }> {
  if (index % 11 === 0) {
    throw new Error(`product index ${index} is sold out in the stub; pick another`);
  }

  await useStubApi(page);

  await page.goto(`/product/test-product-${index}?colour=1&size=10`);

  // Hydration, and `toBeEnabled()` is not it: Add to Bag is server-rendered enabled once the URL settles a
  // variant, so an actionability check passes on a button whose handler does not exist yet. The
  // recently-viewed write is the signal that the component is alive — `checkout.e2e.ts` records why.
  await expect
    .poll(() => page.evaluate(() => localStorage.getItem('sd.recently-viewed') ?? ''))
    .toContain(`test-product-${index}`);

  // Armed before the click, and asserted: a locator that times out after five seconds says only "the
  // mini bag has no line", which is the symptom of a dozen different causes. The response says which.
  const added = page.waitForResponse(
    (response) =>
      new URL(response.url()).pathname === '/api/cart/items' && response.request().method() === 'POST',
  );

  await page.locator('[data-testid="add-to-bag"]').click();

  const addResponse = await added;
  expect(
    addResponse.status(),
    `adding product ${index} to the bag answered ${addResponse.status()} from ${addResponse.url()}`,
  ).toBe(200);

  await expect(page.locator('sd-mini-bag [role="dialog"] .mini-bag__line')).toHaveCount(1);

  // **The hook runs here, and the position is the whole point.** The stub's fixture switches are keyed on
  // the requesting bag, so a test that arms one before the first add-to-bag has no bag to arm it on and
  // gets a 400 — which is how this was found. Here the cookie exists and the order has not been placed,
  // which is the only window in which "the shop has no bank details *for this order*" can be set.
  await options.afterBag?.();

  const quoted = page.waitForResponse(
    (response) =>
      new URL(response.url()).pathname === '/api/checkout' && response.request().method() === 'GET',
  );

  await page.goto('/checkout');
  await quoted;
  await expect(page.locator('#co-firstName')).toBeVisible();

  // Each change re-quotes, and each waiter is armed **before** the interaction that causes it: a waiter
  // registered afterwards misses a response that has already landed, which is a timeout that says nothing.
  const districtQuote = page.waitForResponse(
    (response) =>
      new URL(response.url()).pathname === '/api/checkout'
      && new URL(response.url()).searchParams.get('districtId') === String(COLOMBO),
  );

  await page.locator('#co-district').selectOption(String(COLOMBO));
  await districtQuote;

  const methodQuote = page.waitForResponse(
    (response) =>
      new URL(response.url()).pathname === '/api/checkout'
      && new URL(response.url()).searchParams.get('deliveryMethodId') === String(STANDARD),
  );

  await page.locator(`input[name="deliveryMethodId"][value="${STANDARD}"]`).check();
  await methodQuote;

  await page.locator('#co-firstName').fill('Nimali');
  await page.locator('#co-lastName').fill('Perera');
  await page.locator('#co-email').fill('nimali@example.com');
  await page.locator('#co-phone').fill('0771234567');
  await page.locator('#co-recipient').fill('Nimali Perera');
  await page.locator('#co-address-phone').fill('0771234567');
  await page.locator('#co-line1').fill('14 Galle Road');
  await page.locator('#co-city').fill('Kollupitiya');

  await expect(page.locator('.checkout__place')).not.toHaveAttribute('aria-disabled', 'true');
  await page.locator('.checkout__place').click();
  await expect(page).toHaveURL(/\/order\//);

  const orderNumber = (await page.locator('[data-order-number]').textContent())!.trim();
  const total = await page.evaluate(() => {
    const node = document.querySelector('[data-sum="total"]');
    const matched = /[\d,]+(?:\.\d+)?/.exec((node?.textContent ?? '').replace('Rs.', ''));

    return matched ? Number(matched[0].replace(/,/g, '')) : 0;
  });

  expect(total, 'the order page showed no total, so there is nothing to compare the panel against')
    .toBeGreaterThan(0);

  // Handed back hydrated, so no caller has to remember: every test here types or clicks on this page.
  await hydrated(page);

  return { orderNumber, total };
}

/**
 * Elements whose box reaches past the viewport, widest first.
 *
 * <b>Because "74 px of overflow" tells you nothing.</b> `horizontalOverflow` answers whether the page
 * scrolls sideways, which is the assertion the project's non-negotiable wants — but a failure then sends
 * the reader to a screenshot to guess. This names the elements, so the message is the diagnosis.
 *
 * Only the deepest offender in a chain is interesting: a child that overflows makes every ancestor
 * overflow too, so an element whose own children already overflow is left out.
 */
async function offenders(page: Page): Promise<readonly { tag: string; width: number; right: number }[]> {
  return page.evaluate(() => {
    const limit = document.documentElement.clientWidth;

    return [...document.querySelectorAll<HTMLElement>('body *')]
      .filter((element) => {
        const box = element.getBoundingClientRect();
        if (box.width === 0 || box.right <= limit + 1) {
          return false;
        }

        return ![...element.children].some(
          (child) => child.getBoundingClientRect().right > limit + 1,
        );
      })
      .map((element) => ({
        tag: `${element.tagName.toLowerCase()}.${element.className || '(none)'}`.slice(0, 60),
        width: Math.round(element.getBoundingClientRect().width),
        right: Math.round(element.getBoundingClientRect().right),
      }))
      .sort((left, right) => right.right - left.right)
      .slice(0, 4);
  });
}

test.describe('the pay panel (doc 11 P7 step 1)', () => {
  test('shows the bank details, the amount and the order number as the reference', async ({ page }) => {
    const { orderNumber, total } = await placeAnOrder(page, 70);

    await expect(page.locator('[data-testid="pay-panel"]')).toBeVisible();

    // **The reference is the order number**, which is the only thing tying an anonymous bank credit to
    // this order (P7 step 5 has staff matching "amount and reference").
    await expect(page.locator('[data-testid="pay-reference"]')).toHaveText(orderNumber);

    // The amount on the panel is **the same string as the summary's total**, read off the page rather than
    // re-formatted here. The first version formatted `total` with two decimal places and failed against
    // "Rs. 10,190": `formatLkr` drops a trailing `.00`, so the test was asserting its own idea of the
    // shop's money format instead of the page's. Comparing the two rendered strings is the assertion that
    // was wanted all along — one number, one formatter, two places it has to agree.
    const summaryTotal = (await page.locator('[data-sum="total"]').textContent())!.trim();
    await expect(page.locator('[data-testid="pay-amount"]')).toHaveText(summaryTotal);

    // And it really is the order's total rather than two empty strings agreeing.
    expect(summaryTotal).toContain(String(Math.trunc(total)).slice(0, 2));

    await expect(page.locator('[data-testid="pay-account-number"]')).toHaveText('8001 2345 6789');
    await expect(page.locator('[data-testid="pay-panel"]')).toContainText('Commercial Bank of Ceylon PLC');
    await expect(page.locator('[data-testid="pay-panel"]')).toContainText('Kollupitiya');
    await expect(page.locator('[data-testid="pay-notes"]')).toContainText('Cash-machine deposits');

    // The deadline, as a date and time rather than a ticking countdown: a figure counted down in the
    // browser would be wrong on a device with the wrong clock, about the shopper's own money.
    await expect(page.locator('[data-testid="pay-due"]')).not.toBeEmpty();
  });

  /**
   * ACC-03's emails are W3-06 and nothing sends one. The page must not say otherwise.
   */
  test('claims no email has been sent', async ({ page }) => {
    await placeAnOrder(page, 71);

    const body = (await page.locator('body').textContent())!.toLowerCase();

    expect(body).not.toContain('we have emailed');
    expect(body).not.toContain('check your inbox');
    expect(body).not.toContain('sent you an email');

    // What it does say instead: keep the link.
    expect(body).toContain('link to your order');
  });

  /**
   * Doc 11 P7's precondition unmet, which is a real state a live shop can be in.
   *
   * **This test used to be serial, with a `finally` that restored a global switch, and both were wrong.**
   * The bank details genuinely are a property of the shop rather than of a visitor, so a module-level flag
   * in the stub looked like the honest model — until this test turned it off and a test running at the same
   * time saw a shop with no bank details and no pay panel. `fullyParallel` is on and
   * `test.describe.configure({ mode: 'serial' })` serialises **one describe**, not the file, which was the
   * mistaken assumption. The switch is now keyed on the requesting bag and read once at placement, so
   * nothing has to be serialised and nothing has to be restored: the bag belongs to this test's context and
   * dies with it.
   */
  test('says the details are being confirmed when the shop has not set them', async ({ page }) => {
    await placeAnOrder(page, 72, {
      // After the bag exists — see the hook's own comment — and asserted, because a silently-refused
      // fixture call would leave a shop *with* bank details and this test would then be asserting the
      // absence of a panel that never had a reason to be absent.
      afterBag: async () => {
        const armed = await page.request.get(`${apiURL}/__test__/bank-details?set=no`);
        expect(armed.ok(), `arming the bank-details switch answered ${armed.status()}`).toBe(true);
      },
    });

    await expect(page.locator('[data-testid="pay-pending-details"]')).toContainText(
      'confirming the payment details',
    );

    // **Not a half-filled form.** A shopper who saw a bank name and an empty account number might
    // transfer to whatever they found, and the money would then have to be traced by hand.
    await expect(page.locator('[data-testid="pay-account-number"]')).toHaveCount(0);
    await expect(page.locator('[data-testid="pay-reference"]')).toHaveCount(0);
    await expect(page.locator('[data-testid="slip-input"]')).toHaveCount(0);

    // And nothing has been charged, which is the other thing a shopper needs to hear.
    await expect(page.locator('[data-testid="pay-pending-details"]')).toContainText(
      'nothing has been charged',
    );
  });
});

test.describe('the reference and the slip (doc 11 P7 step 3)', () => {
  test('saves a reference, shows it back, and clears it again', async ({ page }) => {
    await placeAnOrder(page, 73);

    await page.locator('[data-testid="reference-input"]').fill('  CBC-778812  ');
    await page.locator('[data-testid="save-reference"]').click();

    await expect(page.locator('[data-testid="pay-announcement"]')).toContainText('reference is saved');

    // Trimmed, because a reference with spaces round it will not match a bank statement. Asserted by
    // reloading, so what is checked is what the server kept rather than what the box holds.
    await page.reload();
    await hydrated(page);
    await expect(page.locator('[data-testid="reference-input"]')).toHaveValue('CBC-778812');

    // **Bounded retry, which is `accessibility.e2e.ts`' pattern and for the same reason.** Clearing the box
    // immediately after a reload races the component's hydration: the empty value is written into the DOM,
    // the first change detection writes the saved reference back over it, and the click then saves the old
    // value again — the announcement reads "saved" and the test fails having proved nothing about clearing.
    // Each attempt is harmless (it either re-saves the same reference or removes it), and what is asserted
    // is the property that matters: clearing the box and saving removes the reference.
    const announcement = page.locator('[data-testid="pay-announcement"]');
    let removed = false;

    for (let attempt = 0; attempt < 5 && !removed; attempt += 1) {
      await page.locator('[data-testid="reference-input"]').fill('');
      await page.locator('[data-testid="save-reference"]').click();

      try {
        await expect(announcement).toContainText('reference was removed', { timeout: 3_000 });
        removed = true;
      } catch {
        // Lost to hydration on a loaded runner. Try again — a bound rather than a while-true, so a page
        // that genuinely cannot clear a reference fails on the assertion below with a diagnosis.
      }
    }

    expect(removed, 'clearing the reference was never reported back to the shopper').toBe(true);

    await page.reload();
    await expect(page.locator('[data-testid="reference-input"]')).toHaveValue('');
  });

  test('uploads a slip, shows which file is on the order, and offers it back', async ({ page }) => {
    await placeAnOrder(page, 74);

    await page.locator('[data-testid="slip-input"]').setInputFiles({
      name: 'deposit-slip.jpg',
      mimeType: 'image/jpeg',
      buffer: Buffer.from([0xff, 0xd8, 0xff, 0xe0, 0x00, 0x10, 0x4a, 0x46, 0x49, 0x46]),
    });

    await expect(page.locator('[data-testid="pay-announcement"]')).toContainText('deposit-slip.jpg');

    const current = page.locator('[data-testid="slip-current"]');
    await expect(current).toContainText('deposit-slip.jpg');
    await expect(current).toContainText('replaces it');

    // The customer's own copy, served as a download that nothing caches — the same rules as the staff
    // route, because it is the same file.
    const href = await current.locator('a').getAttribute('href');
    const response = await page.request.get(`${apiURL}${href}`);

    expect(response.status()).toBe(200);
    expect(response.headers()['content-disposition']).toBe('attachment');
    expect(response.headers()['x-content-type-options']).toBe('nosniff');
  });

  test('refuses an oversized file in the browser, before it is uploaded', async ({ page }) => {
    await placeAnOrder(page, 75);

    let uploaded = 0;
    page.on('request', (request) => {
      if (request.url().includes('/payment-slip') && request.method() === 'POST') {
        uploaded += 1;
      }
    });

    await page.locator('[data-testid="slip-input"]').setInputFiles({
      name: 'huge.jpg',
      mimeType: 'image/jpeg',
      buffer: Buffer.alloc(6 * 1024 * 1024, 1),
    });

    await expect(page.locator('[data-testid="pay-error"]')).toContainText('larger than 5 MB');

    // Refused before the bytes go up: on a Sri Lankan mobile connection, uploading six megabytes to be
    // told no is a worse experience than a sentence before it starts. The server's check is still the one
    // that matters.
    expect(uploaded, 'an oversized file must not be uploaded').toBe(0);

    // The box is emptied, so choosing the same file again fires a change event rather than doing nothing.
    expect(await page.locator('[data-testid="slip-input"]').inputValue()).toBe('');
  });

  test('the save button keeps the focus it was pressed with', async ({ page }) => {
    await placeAnOrder(page, 76);

    // A concise body rather than `() => {}`: the e2e folder is outside `ng lint`'s scope today, so an
    // empty-function error here would go unseen until somebody widened it (checkout.e2e.ts has one).
    let release = (): void => undefined;
    const held = new Promise<void>((resolve) => {
      release = resolve;
    });

    // **`fallback`, not `continue`.** Registered after `useStubApi`'s catch-all, so Playwright tries this
    // handler first; `continue()` would send the request to the storefront's own origin, where there is no
    // API, while `fallback()` hands it to the forwarder underneath.
    await page.route('**/payment-reference', async (route) => {
      await held;
      await route.fallback();
    });

    await page.locator('[data-testid="reference-input"]').fill('CBC-1');

    const save = page.locator('[data-testid="save-reference"]');
    await save.focus();
    await save.click();

    await expect(save).toHaveAttribute('aria-disabled', 'true');

    // Issue #109: `disabled` on the focused node drops focus to <body>, and six controls in this project
    // have done it. `aria-disabled` says the same thing to assistive technology and keeps the focus.
    expect(await save.evaluate((button: HTMLButtonElement) => button.disabled)).toBe(false);
    expect(await page.evaluate(() => document.activeElement?.getAttribute('data-testid'))).toBe(
      'save-reference',
    );

    release();
    await expect(page.locator('[data-testid="pay-announcement"]')).toContainText('reference is saved');
  });
});

test.describe('the panel itself', () => {
  /**
   * <b>Three different questions, and this asks all three</b> (W1-03's lesson): big enough, on screen, and
   * not buried. `undersized` answers the first; `unreachable` answers the second and throws when nothing
   * matches, which is the guard `undersized` lacks (#80); occlusion gets its own rectangle check, because
   * axe does not enforce tap size at this project's gate and a control can be 44 px and still sit under
   * something.
   */
  test('has no sideways scroll and no undersized or buried control at any width', async ({ page }) => {
    await placeAnOrder(page, 80);

    const clauses = [
      '[data-testid="copy-reference"]',
      '[data-testid="copy-pay-account-number"]',
      '[data-testid="reference-input"]',
      '[data-testid="save-reference"]',
      '[data-testid="slip-input"]',
    ];

    for (const width of BREAKPOINTS) {
      await page.setViewportSize({ width, height: 900 });

      expect(
        await horizontalOverflow(page),
        `horizontal overflow at ${width}px, widest offenders: ${JSON.stringify(await offenders(page))}`,
      ).toBe(0);

      for (const clause of clauses) {
        await expect(page.locator(clause), `${clause} at ${width}px`).toHaveCount(1);

        expect(await undersized(page, clause), `tap target ${clause} at ${width}px`).toEqual([]);
        expect(await unreachable(page, clause), `reachability of ${clause} at ${width}px`).toEqual([]);

        const covered = await page.evaluate((selector) => {
          const element = document.querySelector<HTMLElement>(selector);
          if (!element) {
            return 'missing';
          }

          const box = element.getBoundingClientRect();
          const topmost = document.elementFromPoint(
            box.left + (box.width / 2),
            box.top + (box.height / 2),
          );

          return topmost === null || element.contains(topmost) || topmost.contains(element)
            ? null
            : (topmost as HTMLElement).outerHTML.slice(0, 120);
        }, clause);

        expect(covered, `${clause} is covered at ${width}px`).toBeNull();
      }
    }
  });

  test('has no serious or critical axe findings', async ({ page }) => {
    await placeAnOrder(page, 78);

    const results = await new AxeBuilder({ page }).analyze();
    const blocking = results.violations.filter((violation) =>
      ['serious', 'critical'].includes(violation.impact ?? ''),
    );

    expect(
      blocking.map((violation) => `${violation.id}: ${violation.help}`),
      'serious or critical axe findings on the order page with its pay panel',
    ).toEqual([]);
  });

  /**
   * The account number has to be selectable as one run of characters, because a shopper who cannot copy it
   * will retype it — and a transposed digit sends real money somewhere else.
   */
  test('the account number can be selected whole', async ({ page }) => {
    await placeAnOrder(page, 79);

    const selectable = await page
      .locator('[data-testid="pay-account-number"]')
      .evaluate((element) => globalThis.getComputedStyle(element).userSelect);

    expect(selectable).toBe('all');
  });
});
