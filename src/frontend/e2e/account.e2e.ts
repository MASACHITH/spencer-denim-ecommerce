import { Page, Route, expect, test } from '@playwright/test';

import { apiURL } from '../playwright.config';
import { horizontalOverflow, undersized } from './support/measure';

/**
 * ACC-01 and ACC-02 in a real browser: the account pages and the courier tracking block (W3-01).
 *
 * **Why this file exists.** The independent tester found three defects in W3-01 that no jsdom spec could
 * see, because each one is about layout or about two pages working together: the order history scrolled
 * 127 / 100 / 92 px sideways at 320 / 375 / 390 px with a 60-character tracking number; the tracking link
 * on `/order/{token}` was 26 px tall; and the confirmation page could not survive the Blocker 2 fix
 * without a route back from sign-in. This file measures each of those on the screen.
 *
 * **What is real and what is not.** The storefront is the built SSR server, and every catalogue, bag and
 * order call goes to `e2e/support/catalog-api-stub.mjs` exactly as the other browser suites do. The
 * account API (`/api/account/*`) is answered **in the browser** by {@link AccountFake} below, because the
 * stub has no account surface and the account pages read it only in the browser (`signed-in.guard.ts`
 * lets the server render the shell). That fake is deliberately dumb — it says what the real API says for
 * the cases used here and nothing more — and the real API's behaviour is pinned against SQL Server by
 * the paired backend tests: `PreRegistrationTakeoverTests` (sign-in required to confirm),
 * `CustomerAuthEndpointTests` (one 401 for a locked account), `CustomerOrderHistoryTests` and
 * `SignedInBagAndOrderTests` (the history and the tracking it carries).
 */

/** docs/04 §4 and the project's non-negotiable list. */
const BREAKPOINTS = [320, 375, 390, 768, 1024, 1280, 1440, 1920] as const;

/** The tester's own figure: a tracking number one unbroken word of sixty characters. */
const LONG_TRACKING = 'CW' + '7'.repeat(30) + 'LK' + '0'.repeat(26);

const GOOD_PASSWORD = 'Correct-Horse-Battery-1!';

/** The real server's sentence for every refused sign-in, locked accounts included (W3-01 Major 4). */
const SIGN_IN_REFUSAL =
  'That email address and password do not match an account. After several attempts, sign-in pauses '
  + 'for a few minutes; try again later or use Forgot password.';

/** The real server's sentence for a confirmation link opened signed out (W3-01 Blocker 2). */
const SIGN_IN_FIRST =
  'Sign in to your account, then open this link again. If you cannot sign in, use Forgot password to '
  + 'set a password first.';

const CUSTOMER = {
  userId: 7,
  email: 'nimal@example.invalid',
  firstName: 'Nimal',
  lastName: 'Perera',
  emailConfirmed: false,
};

async function hydrated(page: Page): Promise<void> {
  await page.waitForFunction(() => !!document.querySelector('sd-root')?.hasAttribute('ng-version'));
}

/**
 * The account API, answered in the browser, with only the behaviour these tests depend on.
 *
 * `confirmCalls` records each confirm-email answer, so a test can show the first was refused for want of
 * a session and the second, after signing in, succeeded — with the same token.
 */
class AccountFake {
  signedIn = false;
  readonly confirmCalls: { status: number; token: string }[] = [];

  constructor(private readonly orders: readonly object[] = []) {}

  async install(page: Page): Promise<void> {
    // Everything else to the stub, as the other suites do. Registered first: Playwright tries the most
    // recently registered route first, so the account route below wins for its own paths.
    await page.route('**/api/**', async (route) => {
      const url = new URL(route.request().url());
      const response = await route.fetch({ url: `${apiURL}${url.pathname}${url.search}` });
      await route.fulfill({ response });
    });

    await page.route('**/api/account/**', (route) => this.answer(route));
  }

  private async answer(route: Route): Promise<void> {
    const request = route.request();
    const path = new URL(request.url()).pathname;
    const body = request.postDataJSON() as Record<string, string> | null;

    if (path === '/api/account/session') {
      return json(route, 200, { customer: this.signedIn ? CUSTOMER : null });
    }

    if (path === '/api/account/login' && request.method() === 'POST') {
      if (body?.['password'] !== GOOD_PASSWORD) {
        return problem(route, 401, {
          title: 'We could not sign you in',
          detail: SIGN_IN_REFUSAL,
          code: 'invalid_credentials',
        });
      }

      this.signedIn = true;
      return json(route, 200, CUSTOMER);
    }

    if (path === '/api/account/confirm-email' && request.method() === 'POST') {
      const token = body?.['token'] ?? '';

      if (!this.signedIn) {
        this.confirmCalls.push({ status: 401, token });
        return problem(route, 401, {
          title: 'Sign in to confirm your email address',
          detail: SIGN_IN_FIRST,
          code: 'sign_in_required',
        });
      }

      this.confirmCalls.push({ status: 200, token });
      return json(route, 200, {
        customer: { ...CUSTOMER, emailConfirmed: true },
        linkedOrderNumbers: ['SD-2026-00041'],
      });
    }

    if (path === '/api/account/orders') {
      if (!this.signedIn) {
        return problem(route, 401, {});
      }

      return json(route, 200, {
        orders: this.orders,
        page: 1,
        pageSize: 10,
        totalCount: this.orders.length,
        hasMore: false,
      });
    }

    return problem(route, 404, {});
  }
}

function json(route: Route, status: number, body: unknown): Promise<void> {
  return route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
}

function problem(route: Route, status: number, body: object): Promise<void> {
  return route.fulfill({
    status,
    contentType: 'application/problem+json',
    body: JSON.stringify({ status, ...body }),
  });
}

function shippedSummary(trackingNumber: string, trackingUrl: string | null): object {
  return {
    orderNumber: 'SD-2026-00041',
    token: '0b6f3c1e-7a51-4c4e-9d5c-3c1f0f7d9a11',
    status: 'Shipped',
    statusLabel: 'Shipped',
    placedAtUtc: '2026-10-08T04:30:00Z',
    grandTotal: 4850,
    currency: 'LKR',
    itemCount: 1,
    firstLineThumbnailUrl: null,
    tracking: {
      courierName: 'Test Courier',
      trackingNumber,
      trackingUrl,
      shippedAtUtc: '2026-10-09T04:30:00Z',
    },
  };
}

test.describe('the order history (ACC-02)', () => {
  /**
   * The tester's measurement, repeated at every breakpoint: 127 / 100 / 92 px of sideways scroll at
   * 320 / 375 / 390 before the fix. Linked and unlinked, because the number is a link when the courier
   * has online tracking and plain text when it does not, and both are one long word.
   */
  for (const linked of [true, false]) {
    test(`a 60-character tracking number ${linked ? 'link' : 'as text'} never scrolls the page sideways`, async ({
      page,
    }) => {
      const fake = new AccountFake([
        shippedSummary(LONG_TRACKING, linked ? `https://courier.test/track?id=${LONG_TRACKING}` : null),
      ]);
      fake.signedIn = true;
      await fake.install(page);

      for (const width of BREAKPOINTS) {
        await page.setViewportSize({ width, height: 900 });
        await page.goto('/account/orders');
        await hydrated(page);

        const tracking = page.locator('[data-testid="order-tracking"]');
        await expect(tracking).toBeVisible();
        await expect(tracking).toContainText(LONG_TRACKING);

        expect(await horizontalOverflow(page), `sideways scroll at ${width}px`).toBe(0);

        // And the block itself stays inside its card, which a page-level check could miss if an
        // ancestor clipped it.
        const fits = await tracking.evaluate((element) => element.scrollWidth <= element.clientWidth);
        expect(fits, `the tracking block overflows its own box at ${width}px`).toBe(true);
      }
    });
  }

  test('the tracking link on the list is a 44 px target', async ({ page }) => {
    const fake = new AccountFake([shippedSummary('CT-9981-AB', 'https://courier.test/track?id=CT-9981-AB')]);
    fake.signedIn = true;
    await fake.install(page);

    for (const width of [320, 1280]) {
      await page.setViewportSize({ width, height: 900 });
      await page.goto('/account/orders');
      await hydrated(page);
      await expect(page.locator('[data-testid="order-tracking-link"]')).toBeVisible();

      expect(await undersized(page, '[data-testid="order-tracking-link"]'), `at ${width}px`).toEqual([]);
    }
  });

  test('signed out, the history sends the shopper to sign in and back again', async ({ page }) => {
    const fake = new AccountFake([shippedSummary('CT-9981-AB', null)]);
    await fake.install(page);

    await page.goto('/account/orders');
    await hydrated(page);
    await expect(page).toHaveURL(/\/account\/sign-in\?returnTo=%2Faccount%2Forders$/);

    await page.locator('[data-testid="sign-in-email"]').fill(CUSTOMER.email);
    await page.locator('[data-testid="sign-in-password"]').fill(GOOD_PASSWORD);
    await page.locator('[data-testid="sign-in-submit"]').click();

    await expect(page).toHaveURL(/\/account\/orders$/);
    await expect(page.locator('[data-testid="order-tracking"]')).toContainText('CT-9981-AB');
  });
});

test.describe('signing in (ACC-01)', () => {
  /**
   * W3-01 Major 4 as the shopper sees it: a refusal — and a locked account gets exactly this one — keeps
   * what was typed, says the server's sentence, and moves focus to it. No "locked" wording exists to
   * show, so none can leak.
   */
  test('a refused sign-in keeps the address, says the one sentence, and announces it', async ({ page }) => {
    const fake = new AccountFake();
    await fake.install(page);

    await page.goto('/account/sign-in');
    await hydrated(page);

    await page.locator('[data-testid="sign-in-email"]').fill(CUSTOMER.email);
    await page.locator('[data-testid="sign-in-password"]').fill('Wrong-Password-9!');
    await page.locator('[data-testid="sign-in-submit"]').click();

    const alert = page.locator('[data-testid="sign-in-alert"]');
    await expect(alert).toHaveText(SIGN_IN_REFUSAL);
    await expect(alert).toBeFocused();
    await expect(page.locator('[data-testid="sign-in-email"]')).toHaveValue(CUSTOMER.email);
    await expect(page).toHaveURL(/\/account\/sign-in$/);
    await expect(page.locator('body')).not.toContainText(/locked/i);
  });
});

test.describe('confirming an email address (ACC-01, D5)', () => {
  /**
   * The Blocker 2 fix, end to end in the browser: a confirmation link opened signed out is refused
   * before the token is looked at, the page sends the shopper to sign in with a way back that carries
   * the token, and after signing in the same link confirms and names the linked order.
   */
  test('signed out, the link sends the shopper to sign in and brings them back to confirm', async ({ page }) => {
    const fake = new AccountFake();
    await fake.install(page);

    await page.goto('/account/confirm-email?token=7.a-live-confirmation-token');
    await hydrated(page);

    const alert = page.locator('[data-testid="confirm-email-alert"]');
    await expect(alert).toHaveText(SIGN_IN_FIRST);
    await expect(page.locator('[data-testid="confirm-email-forgot"]')).toBeVisible();
    await expect(page.locator('body')).not.toContainText('Ask for a new link');

    await page.locator('[data-testid="confirm-email-sign-in"]').click();
    await expect(page).toHaveURL(/\/account\/sign-in\?returnTo=/);

    await page.locator('[data-testid="sign-in-email"]').fill(CUSTOMER.email);
    await page.locator('[data-testid="sign-in-password"]').fill(GOOD_PASSWORD);
    await page.locator('[data-testid="sign-in-submit"]').click();

    await expect(page.locator('[data-testid="confirm-email-status"]')).toContainText('confirmed');
    await expect(page.locator('[data-testid="confirm-email-linked"]')).toContainText('SD-2026-00041');

    // The same token both times: refused for want of a session, then accepted with one.
    expect(fake.confirmCalls).toEqual([
      { status: 401, token: '7.a-live-confirmation-token' },
      { status: 200, token: '7.a-live-confirmation-token' },
    ]);

    // And the token is not left in the address bar once it has been used.
    await expect(page).toHaveURL(/\/account\/confirm-email$/);
  });

  test('the sign-in step fits every breakpoint', async ({ page }) => {
    const fake = new AccountFake();
    await fake.install(page);

    for (const width of BREAKPOINTS) {
      await page.setViewportSize({ width, height: 900 });
      await page.goto('/account/confirm-email?token=7.a-live-confirmation-token');
      await hydrated(page);
      await expect(page.locator('[data-testid="confirm-email-sign-in"]')).toBeVisible();

      expect(await horizontalOverflow(page), `sideways scroll at ${width}px`).toBe(0);
      expect(
        await undersized(page, '[data-testid="confirm-email-sign-in"], [data-testid="confirm-email-forgot"]'),
        `at ${width}px`,
      ).toEqual([]);
    }
  });
});

test.describe('the tracking block on the order link (doc 11 P11 step 14)', () => {
  async function shippedOrder(page: Page, number: string, link: string | null): Promise<string> {
    const query = new URLSearchParams({ number, ...(link === null ? { url: 'none' } : {}) });
    const created = await page.request.get(`${apiURL}/__test__/shipped-order?${query}`);
    expect(created.status()).toBe(200);

    return ((await created.json()) as { publicId: string }).publicId;
  }

  /** The tester measured 26 px. docs/04's rule is 44 px for every control a thumb has to hit. */
  test('the tracking link is a 44 px target at every breakpoint', async ({ page }) => {
    const token = await shippedOrder(page, 'CT-9981-AB', 'https://courier.test/track?id=CT-9981-AB');

    for (const width of BREAKPOINTS) {
      await page.setViewportSize({ width, height: 900 });
      await page.goto(`/order/${token}`);
      await hydrated(page);
      await expect(page.locator('[data-testid="order-page-tracking-link"]')).toBeVisible();

      expect(await undersized(page, '[data-testid="order-page-tracking-link"]'), `at ${width}px`).toEqual([]);
    }
  });

  for (const linked of [true, false]) {
    test(`a 60-character number ${linked ? 'link' : 'as text'} never scrolls the order page sideways`, async ({
      page,
    }) => {
      const token = await shippedOrder(
        page,
        LONG_TRACKING,
        linked ? `https://courier.test/track?id=${LONG_TRACKING}` : null,
      );

      for (const width of BREAKPOINTS) {
        await page.setViewportSize({ width, height: 900 });
        await page.goto(`/order/${token}`);
        await hydrated(page);
        await expect(page.locator('[data-testid="order-page-tracking"]')).toContainText(LONG_TRACKING);

        expect(await horizontalOverflow(page), `sideways scroll at ${width}px`).toBe(0);
      }
    });
  }

  /**
   * The heading follows the status, on the server render as well as after hydration: this page is a
   * guest's permanent link, and it said "Your order is placed ... waiting for payment" for a shipped one.
   */
  test('a shipped order is not called placed or waiting for payment', async ({ page }) => {
    const token = await shippedOrder(page, 'CT-9981-AB', null);

    const response = await page.goto(`/order/${token}`);
    expect(await response?.text()).not.toContain('waiting for payment');

    await hydrated(page);
    await expect(page.locator('[data-testid="order-headline"]')).toHaveText('Your order');
    await expect(page.locator('[data-testid="order-head-sentence"]')).toContainText('This order is shipped.');
    await expect(page.locator('body')).not.toContainText('waiting for payment');
  });
});
