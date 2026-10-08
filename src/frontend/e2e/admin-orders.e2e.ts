import AxeBuilder from '@axe-core/playwright';
import { Page, expect, test } from '@playwright/test';

import { adminURL, adminWorktreeRoot } from '../playwright.config';
import { horizontalOverflow, undersized, unreachable } from './support/measure';

/**
 * ADM-09 in the admin application: the order list, the order detail with doc 11 P9's workflow on it, the
 * invoice and the couriers screen.
 *
 * It runs against `e2e/support/admin-catalog-stub.mjs`, which serves `dist/admin` and answers
 * `/api/admin/*` from the same origin. The endpoints themselves are proved against a real SQL Server in
 * `AdminOrderEndpointTests` — and that split is issue #126 rather than tidiness: **Playwright never
 * starts the .NET API**, so nothing in this file can observe a backend change. What it proves is the
 * screen, and specifically the three things no jsdom spec can measure: that nothing scrolls sideways at
 * 320 px, that every control clears 44 px, and that no control is buried.
 */

/** docs/04 §4 and the project's non-negotiable list. */
const BREAKPOINTS = [320, 375, 390, 768, 1024, 1280, 1440, 1920] as const;

const PAID_ORDER = 'SD-2026-000123';
const PICKUP_ORDER = 'SD-2026-000124';
const EXPIRED_ORDER = 'SD-2026-000125';
const EXPIRED_TOKEN = '66666666-6666-4666-8666-666666666666';

/** W3-04b: the only kind of order doc 11 P10 flow A can cancel. */
const UNPAID_ORDER = 'SD-2026-000126';
const UNPAID_TOKEN = '77777777-7777-4777-8777-777777777777';

/**
 * **The server has to be this worktree's own before a single answer from it is trusted.**
 *
 * `reuseExistingServer` is on outside CI and the admin port is derived from the worktree path, which makes
 * a collision unlikely rather than impossible — and when one happens another worktree's `dist/admin` is
 * served against these tests. A run of 113 failures is what that looked like the first time.
 */
test.beforeAll(async ({ request }) => {
  const response = await request.get(`${adminURL}/__worktree`);
  expect(response.ok(), `the admin stub at ${adminURL} did not answer /__worktree`).toBe(true);

  const { root } = (await response.json()) as { root: string };

  expect(
    root.replace(/\\/g, '/').toLowerCase(),
    `the server on ${adminURL} is serving ${root}, not this worktree — a port that answers is not proof `
      + 'that it is yours (doc 14)',
  ).toBe(adminWorktreeRoot.replace(/\\/g, '/').toLowerCase());
});

/**
 * Opens a screen with a state of this test's own.
 *
 * The stub keys its state on an `e2e_state` cookie, so parallel workers do not move each other's orders —
 * `admin-catalog.e2e.ts` learned that the expensive way, and this screen writes more than that one does.
 */
async function open(page: Page, path: string): Promise<string> {
  const state = `orders-${Math.random().toString(36).slice(2)}`;

  await page.context().addCookies([{ name: 'e2e_state', value: state, url: adminURL }]);
  await page.goto(`${adminURL}${path}`);

  return state;
}

async function openList(page: Page): Promise<string> {
  const state = await open(page, '/orders');

  // The page fetches on construction, so the heading alone is not enough: a row asserted before the
  // response arrives is a row that is legitimately absent.
  await expect(page.getByRole('heading', { level: 1, name: 'Orders' })).toBeVisible();
  await expect(page.locator(`[data-testid="order-${PAID_ORDER}"]`)).toBeVisible();

  return state;
}

async function openDetail(page: Page, token: string, number: string): Promise<string> {
  const state = await open(page, `/orders/${token}`);

  await expect(page.locator('[data-testid="order-number"]')).toContainText(number);

  return state;
}

async function stateCookie(page: Page): Promise<string> {
  const cookies = await page.context().cookies(adminURL);

  return cookies.find((cookie) => cookie.name === 'e2e_state')?.value ?? 'shared';
}

/** Arms the stub's next transition. `ok` is the default; `wrong-status` is the 409 the real API answers. */
async function armOutcome(page: Page, outcome: 'ok' | 'wrong-status'): Promise<void> {
  const response = await page.request.get(
    `${adminURL}/__test__/transition-outcome?outcome=${outcome}`,
    { headers: { Cookie: `e2e_state=${await stateCookie(page)}` } },
  );

  expect(response.ok()).toBe(true);
}

/** Fills in the four Store.* legal keys, or clears them. */
async function armLegalDetails(page: Page, set: boolean): Promise<void> {
  const response = await page.request.get(
    `${adminURL}/__test__/store-legal-details?set=${set ? 'yes' : 'no'}`,
    { headers: { Cookie: `e2e_state=${await stateCookie(page)}` } },
  );

  expect(response.ok()).toBe(true);
}

/** Adds a courier through the real screen, so the dispatch form has something to choose. */
async function addCourier(page: Page, name: string): Promise<void> {
  await open(page, '/orders/couriers');

  await expect(page.getByRole('heading', { level: 1, name: 'Couriers' })).toBeVisible();

  await page.locator('[data-testid="courier-name"]').fill(name);
  await page
    .locator('[data-testid="courier-template"]')
    .fill('https://track.example.test/{trackingNumber}');
  await page.locator('[data-testid="courier-save"]').click();

  await expect(page.locator('[data-testid="couriers"]')).toContainText(name);
}

test.describe('the order list (doc 11 P9 step 1, doc 11 §6.6)', () => {
  test('shows what a staff member needs to triage a row', async ({ page }) => {
    await openList(page);

    const row = page.locator(`[data-testid="order-${PAID_ORDER}"]`);

    await expect(row).toContainText(PAID_ORDER);
    await expect(row).toContainText('Nimali Perera');
    await expect(row).toContainText('Standard delivery');
    await expect(page.locator(`[data-testid="total-${PAID_ORDER}"]`)).toContainText('15,596.60');
    await expect(page.locator(`[data-testid="status-${PAID_ORDER}"]`)).toContainText('Paid');

    // The order number is the link, so the whole row heading is the control a person aims at. The href
    // carries the application's own base href (`<base href="/admin/">`), which is what the reverse proxy
    // mounts it under — the stub serves either spelling, and the generated one is the honest assertion.
    await expect(page.locator(`[data-testid="open-${PAID_ORDER}"]`)).toHaveAttribute(
      'href',
      '/admin/orders/44444444-4444-4444-8444-444444444444',
    );
  });

  /**
   * **Doc 11 P9's pickup variant.** A pickup order reads as "Ready for collection" and "Collected", and
   * the wording comes from the server so the list and the detail cannot disagree about one order.
   */
  test('reads a pickup order in the shop’s words rather than the courier’s', async ({ page }) => {
    await openList(page);

    await expect(page.locator(`[data-testid="status-${PICKUP_ORDER}"]`)).toContainText('Processing');
    await expect(page.locator(`[data-testid="order-${PICKUP_ORDER}"]`)).toContainText('Collect in store');
  });

  /**
   * **Doc 11 §6.6, and the reason this screen exists at all.** What a customer on the telephone has is
   * the order number — before this screen the only order screen was the bank-transfer approval queue, and
   * an order left it the moment it was paid.
   */
  test('finds one order by the number the customer quotes', async ({ page }) => {
    await openList(page);

    await page.locator('[data-testid="order-search"]').fill(PAID_ORDER);
    await page.locator('[data-testid="order-search-submit"]').click();

    await expect(page.locator(`[data-testid="order-${PAID_ORDER}"]`)).toBeVisible();
    await expect(page.locator(`[data-testid="order-${PICKUP_ORDER}"]`)).toHaveCount(0);

    // And Clear puts everything back, so a staff member is not stuck in a filtered view.
    await page.locator('[data-testid="order-search-clear"]').click();
    await expect(page.locator(`[data-testid="order-${PICKUP_ORDER}"]`)).toBeVisible();
  });

  test('says what a search that finds nothing means, and what to try instead', async ({ page }) => {
    await openList(page);

    await page.locator('[data-testid="order-search"]').fill('SD-1999-999999');
    await page.locator('[data-testid="order-search-submit"]').click();

    const empty = page.locator('[data-testid="orders-empty"]');

    await expect(empty).toContainText('SD-1999-999999');
    await expect(empty).toContainText('email');
  });

  /**
   * **Doc 11 P9 step 1 on the screen: the Paid tab is labelled oldest first because the server said so.**
   */
  test('labels the Paid tab oldest first, from the server’s own answer', async ({ page }) => {
    await openList(page);

    await page.locator('[data-testid="filter-Paid"]').click();

    await expect(page.locator('[data-testid="sort-oldest"]')).toHaveAttribute('aria-pressed', 'true');
    await expect(page.locator('[data-testid="sort-newest"]')).toHaveAttribute('aria-pressed', 'false');

    // Another tab is newest first, which is the other half of the rule.
    await page.locator('[data-testid="filter-Shipped"]').click();
    await expect(page.locator('[data-testid="sort-newest"]')).toHaveAttribute('aria-pressed', 'true');
  });

  /**
   * **Focus survives a filter change**, which destroys every row.
   *
   * Destroying the focused node drops focus to `<body>` just as `[disabled]` does (#109, read from the
   * other direction), so the screen re-homes it on to the live region — and only when it was actually
   * lost, because yanking the caret out of the search box would be its own defect.
   */
  test('keeps focus somewhere real when a filter destroys the rows', async ({ page }) => {
    await openList(page);

    const filter = page.locator('[data-testid="filter-Expired"]');
    await filter.focus();
    await filter.click();

    await expect(page.locator(`[data-testid="order-${EXPIRED_ORDER}"]`)).toBeVisible();

    expect(await page.evaluate(() => document.activeElement?.tagName)).not.toBe('BODY');
  });

  test('leaves the caret in the search box when a search succeeds', async ({ page }) => {
    await openList(page);

    const box = page.locator('[data-testid="order-search"]');
    await box.fill(PAID_ORDER);
    await box.press('Enter');

    await expect(page.locator(`[data-testid="order-${PICKUP_ORDER}"]`)).toHaveCount(0);

    // The box survives the load, so focus must not have been taken off it: re-homing unconditionally
    // would interrupt a staff member mid-sentence, which is why the rule is "only if it was lost".
    expect(
      await page.evaluate(() => document.activeElement?.getAttribute('data-testid')),
    ).toBe('order-search');
  });
});

test.describe('the order detail and doc 11 P9’s workflow', () => {
  test('shows what was bought, who to telephone, the money and the timeline', async ({ page }) => {
    await openDetail(page, '44444444-4444-4444-8444-444444444444', PAID_ORDER);

    await expect(page.locator('[data-testid="order-lines"]')).toContainText('SKU-470');
    await expect(page.locator('[data-testid="order-lines"]')).toContainText('Indigo');
    await expect(page.locator('[data-testid="order-lines"]')).toContainText('Slim jeans');
    await expect(page.locator('[data-testid="order-total"]')).toContainText('15,596.60');

    await expect(page.locator('[data-testid="order-delivery"]')).toContainText('14 Galle Road');
    await expect(page.locator('[data-testid="order-delivery"]')).toContainText('Kollupitiya');

    // Every payment attempt, with the staff member who reviewed it named by email rather than by id.
    await expect(page.locator('[data-testid="order-payments"]')).toContainText('Bank transfer');
    await expect(page.locator('[data-testid="order-payments"]')).toContainText('staff@example.com');
    await expect(page.locator('[data-testid="order-payments"]')).toContainText('CBC-778812');

    // The timeline, oldest first, with placement at the top.
    const history = page.locator('[data-testid="order-history"]');
    await expect(history).toContainText('Order placed');
    await expect(history).toContainText('Awaiting payment → Paid');
  });

  /**
   * **A System transition says "by the system" rather than leaving a gap.**
   *
   * Doc 08 §4 writes a history row for every transition no user made, precisely so the timeline is
   * complete — so a null user is a fact to render, not a failed lookup to hide.
   */
  test('names the system as the actor on a transition nobody made', async ({ page }) => {
    await openDetail(page, EXPIRED_TOKEN, EXPIRED_ORDER);

    await expect(page.locator('[data-testid="order-history"]')).toContainText('by the system');
  });

  /**
   * **With no courier configured, the screen says what to go and do.**
   *
   * `sales.Couriers` is seeded empty on purpose — which carriers the client uses is their information —
   * so this is the normal state of a new installation, and a disabled button with no explanation is the
   * failure mode this task was told to avoid.
   */
  test('says to add a courier first rather than offering a dead control', async ({ page }) => {
    await openDetail(page, '44444444-4444-4444-8444-444444444444', PAID_ORDER);

    await page.locator('[data-testid="start-processing"]').click();
    await expect(page.locator('[data-testid="order-status"]')).toContainText('Processing');

    const blocked = page.locator('[data-testid="order-blocked"]');

    await expect(blocked).toContainText('courier');
    await expect(blocked).toContainText('Settings');

    // And no dispatch form at all, rather than one that cannot be submitted.
    await expect(page.locator('[data-testid="submit-dispatch"]')).toHaveCount(0);
  });

  test('ships an order once a courier exists, and renders the tracking link', async ({ page }) => {
    await addCourier(page, 'xunit-e2e-carrier');
    await page.goto(`${adminURL}/orders/44444444-4444-4444-8444-444444444444`);
    await expect(page.locator('[data-testid="order-number"]')).toContainText(PAID_ORDER);

    await page.locator('[data-testid="start-processing"]').click();
    await expect(page.locator('[data-testid="submit-dispatch"]')).toBeVisible();

    // The tracking number alone is refused, by the screen, without a request — a round trip to be told a
    // box is empty is a round trip.
    let posted = 0;
    page.on('request', (request) => {
      if (request.url().includes('/mark-shipped')) {
        posted += 1;
      }
    });

    await page.locator('[data-testid="tracking-number"]').fill('TRACK-E2E-1');
    await page.locator('[data-testid="submit-dispatch"]').click();

    await expect(page.locator('[data-testid="dispatch-error"]')).toContainText('courier');
    expect(posted, 'a dispatch with no courier must not reach the server').toBe(0);

    await page.locator('[data-testid="courier"]').selectOption({ label: 'xunit-e2e-carrier' });
    await page.locator('[data-testid="submit-dispatch"]').click();

    await expect(page.locator('[data-testid="order-status"]')).toContainText('Shipped');
    await expect(page.locator('[data-testid="order-said"]')).toContainText('shipped');

    // The dispatch is on the screen, with the link rendered from the courier's current template
    // (doc 08 §3.5) — and the order's own snapshotted name beside it.
    const dispatch = page.locator('[data-testid="order-dispatch"]');
    await expect(dispatch).toContainText('xunit-e2e-carrier');
    await expect(dispatch).toContainText('TRACK-E2E-1');
    await expect(page.locator('[data-testid="tracking-link"]')).toHaveAttribute(
      'href',
      'https://track.example.test/TRACK-E2E-1',
    );

    // And the correction form comes up with that courier already selected, which is what the dispatch's
    // id is on the contract for.
    await page.reload();
    await expect(page.locator('[data-testid="courier"]')).toHaveValue(/^\d+$/);
  });

  /**
   * **Doc 11 P9's pickup variant: Ready for collection, then Mark as collected, and no courier anywhere.**
   */
  test('takes a pickup order to collection and then to collected', async ({ page }) => {
    await openDetail(page, '55555555-5555-4555-8555-555555555555', PICKUP_ORDER);

    await expect(page.locator('[data-testid="submit-dispatch"]')).toHaveCount(0);
    await expect(page.locator('[data-testid="order-blocked"]')).toHaveCount(0);

    await page.locator('[data-testid="ready-for-collection"]').click();
    await expect(page.locator('[data-testid="order-status"]')).toContainText('Ready for collection');

    // "Mark as collected", not "Mark as delivered": the same transition, the right verb.
    const collected = page.locator('[data-testid="mark-delivered"]');
    await expect(collected).toContainText('Mark as collected');

    await collected.click();
    await expect(page.locator('[data-testid="order-status"]')).toContainText('Collected');
  });

  /**
   * **Issue #124: the route to an order that has already expired.**
   *
   * The bank-transfer queue lists `Pending` payments and expiry closes the payment in the same
   * transaction as the order, so an expired order is not in that queue at all — and a staff member who
   * got an `order_expired` refusal there and pressed Refresh lost the only route to the transaction
   * permanently. Searching the number the customer quotes reaches this screen, and a reload cannot
   * destroy it.
   */
  test('reaches an expired order by its number and offers Accept late payment', async ({ page }) => {
    await openList(page);

    await page.locator('[data-testid="order-search"]').fill(EXPIRED_ORDER);
    await page.locator('[data-testid="order-search-submit"]').click();

    await page.locator(`[data-testid="open-${EXPIRED_ORDER}"]`).click();

    await expect(page.locator('[data-testid="order-number"]')).toContainText(EXPIRED_ORDER);
    await expect(page.locator('[data-testid="accept-late"]')).toBeVisible();
    await expect(page.locator('[data-testid="accept-late-hint"]')).toContainText('re-checks stock');

    // **And a reload does not lose it**, which is the half of #124 the tester added: the old dead end was
    // one keystroke wide.
    await page.reload();

    await expect(page.locator('[data-testid="order-number"]')).toContainText(EXPIRED_ORDER);
    await expect(page.locator('[data-testid="accept-late"]')).toBeVisible();
  });

  /**
   * A refused transition tells the staff member where the order actually is.
   */
  test('says where the order went when somebody else moved it first', async ({ page }) => {
    await openDetail(page, '44444444-4444-4444-8444-444444444444', PAID_ORDER);
    await armOutcome(page, 'wrong-status');

    await page.locator('[data-testid="start-processing"]').click();

    const refusal = page.locator('[data-testid="order-refusal"]');

    await expect(refusal).toContainText('already started processing');
    await expect(refusal).toContainText('This order is now: Processing.');

    // And focus is still on something real: the refusal re-reads the order, which replaces the panel the
    // button was in.
    expect(await page.evaluate(() => document.activeElement?.tagName)).not.toBe('BODY');
  });

  /**
   * Doc 11 P10 flow A: cancel an unpaid order, with a reason.
   *
   * **The reason is required and the browser never sends a blank one**, which is the half a jsdom spec
   * cannot see: the form is a real form, Enter submits it, and the message appears with no request made.
   */
  test('cancels an unpaid order, and refuses to do it without a reason', async ({ page }) => {
    await openDetail(page, UNPAID_TOKEN, UNPAID_ORDER);

    await expect(page.locator('[data-testid="cancel-order"]')).toBeVisible();

    // Pressed with the box empty: a message, and the order has not moved.
    await page.locator('[data-testid="cancel-order"]').click();

    await expect(page.locator('[data-testid="cancel-error"]')).toContainText('Say why');
    await expect(page.locator('[data-testid="order-status"]')).toContainText('Awaiting payment');

    await page
      .locator('[data-testid="cancel-reason-input"]')
      .fill('Customer telephoned to cancel.');
    await page.locator('[data-testid="cancel-order"]').click();

    await expect(page.locator('[data-testid="order-status"]')).toContainText('Cancelled');
    await expect(page.locator('[data-testid="cancel-reason"]')).toContainText(
      'Customer telephoned to cancel.',
    );

    // **The form is gone**, which is the assertion that matters: a Cancel form left on a cancelled order
    // is a control that can only ever fail.
    await expect(page.locator('[data-testid="cancel-order"]')).toHaveCount(0);

    // And focus is on something real. The applied cancel destroyed the control that was pressed, which
    // is #109's other half — `aria-disabled` keeps the press from throwing focus to the body, and
    // `afterNextRender` re-homes it when the node itself goes.
    expect(await page.evaluate(() => document.activeElement?.tagName)).not.toBe('BODY');

    // **The one edge out of Cancelled still has a control.** Doc 08 §4.1 allows Cancelled → Paid, and
    // cancelling closed this order's bank payment to Cancelled — which `CanAcceptLatePayment` ignored
    // until the tester widened it, so a shop could cancel an order and then be unable to accept the
    // transfer that arrived the next day. `AdminOrderEndpointTests` asserts both ends on a real row.
    await expect(page.locator('[data-testid="accept-late"]')).toBeVisible();
  });

  /**
   * Doc 11 P10's table: a paid order is refunded, never cancelled — and there is no refund screen.
   */
  test('offers no cancel on a paid order, and no refund either', async ({ page }) => {
    await openDetail(page, '44444444-4444-4444-8444-444444444444', PAID_ORDER);

    await expect(page.locator('[data-testid="cancel-order"]')).toHaveCount(0);
    await expect(page.locator('[data-testid="cancel-reason-input"]')).toHaveCount(0);

    // Nothing offers a refund either, because nothing writes one (W3-05). A screen that said "Refund"
    // and then could not would be worse than the internal note, which is the honest place to record what
    // was done by hand.
    await expect(page.getByRole('button', { name: /refund/i })).toHaveCount(0);
  });

  /**
   * **The internal note is always sent, and an empty box clears it.**
   *
   * The endpoint refuses a body that leaves the field out rather than reading its absence as a clear —
   * the distinction four defects in this project came from collapsing — so the screen's half of that
   * guarantee is sending the key every time.
   */
  test('saves and clears the internal note', async ({ page }) => {
    await openDetail(page, '44444444-4444-4444-8444-444444444444', PAID_ORDER);

    await page.locator('[data-testid="internal-note"]').fill('Customer rang on the 8th.');
    await page.locator('[data-testid="save-note"]').click();

    await expect(page.locator('[data-testid="order-said"]')).toContainText('saved');

    await page.reload();
    await expect(page.locator('[data-testid="internal-note"]')).toHaveValue('Customer rang on the 8th.');

    await page.locator('[data-testid="internal-note"]').fill('');
    await page.locator('[data-testid="save-note"]').click();

    await expect(page.locator('[data-testid="order-said"]')).toContainText('cleared');
  });

  /**
   * **Nothing on this screen promises an email, a notification or an acknowledgement.**
   *
   * `ops.EmailOutbox` is specified (doc 08 §3.8) and unbuilt; W3-06 owns it. Checkout's earlier attempt
   * to promise an email nothing could send was a Major finding and W3-03's "we will be in touch" was a
   * Minor for the same reason, so this is asserted over the rendered text rather than left to review.
   */
  test('promises no email anywhere, because nothing can send one', async ({ page }) => {
    await addCourier(page, 'xunit-e2e-email-check');
    await page.goto(`${adminURL}/orders/44444444-4444-4444-8444-444444444444`);
    await expect(page.locator('[data-testid="order-number"]')).toContainText(PAID_ORDER);

    await page.locator('[data-testid="start-processing"]').click();
    await expect(page.locator('[data-testid="submit-dispatch"]')).toBeVisible();

    const text = ((await page.locator('body').textContent()) ?? '').toLowerCase();

    // **Phrases, not the bare word.** "Email" appears on this screen as the *label* of the customer's
    // own address, which is a field a staff member reads off and telephones about — refusing the word
    // would be refusing the panel. What must not appear is a sentence claiming something was or will be
    // sent, which is what checkout was marked Major for and W3-03 Minor for.
    for (const promise of [
      'email has been sent',
      'email will be sent',
      'we have emailed',
      'we will email',
      'an email is on its way',
      'has been notified',
      'will be notified',
      'notification has been sent',
      'we will be in touch',
      'confirmation email',
      'email confirmation',
    ]) {
      expect(
        text.includes(promise),
        `the order screen must not promise "${promise}": ops.EmailOutbox is W3-06 and unbuilt`,
      ).toBe(false);
    }

    // What it *does* say is the honest thing doc 11 P9 asks for about a corrected tracking number.
    await page.locator('[data-testid="courier"]').selectOption({ label: 'xunit-e2e-email-check' });
    await page.locator('[data-testid="tracking-number"]').fill('TRACK-E2E-2');
    await page.locator('[data-testid="submit-dispatch"]').click();

    await expect(page.locator('[data-testid="order-status"]')).toContainText('Shipped');
    await expect(page.locator('body')).toContainText('Telephone or WhatsApp them today');
  });
});

test.describe('the invoice (doc 11 P9 step 3)', () => {
  test('warns before printing when the company’s details are not filled in', async ({ page }) => {
    await open(page, '/orders/44444444-4444-4444-8444-444444444444/invoice');

    await expect(page.locator('[data-testid="invoice-number"]')).toContainText('INV-2026-000045');

    const warning = page.locator('[data-testid="seller-incomplete"]');

    await expect(warning).toContainText('registered name and address');
    await expect(warning).toContainText('Settings');

    // **And nothing is printed in their place.** An invented registered address on a legal document is
    // worse than a blank somebody can see and ask about.
    await expect(page.locator('[data-testid="seller-legal-name"]')).toHaveCount(0);

    // The document still renders, because a staff member may want a packing copy today.
    await expect(page.locator('[data-testid="invoice-line-SKU-470"]')).toContainText('Slim jeans');
    await expect(page.locator('[data-testid="invoice-total"]')).toContainText('15,596.60');

    // No VAT line: prices already include VAT (doc 08 D2), and the document says so rather than leaving
    // a customer to wonder.
    await expect(page.locator('[data-testid="invoice"]')).toContainText('All prices include VAT');
  });

  test('prints the company’s details once the client has filled them in', async ({ page }) => {
    await open(page, '/orders');
    await armLegalDetails(page, true);

    await page.goto(`${adminURL}/orders/44444444-4444-4444-8444-444444444444/invoice`);

    await expect(page.locator('[data-testid="seller-legal-name"]')).toContainText(
      'A Registered Company (Pvt) Ltd',
    );
    await expect(page.locator('[data-testid="seller-incomplete"]')).toHaveCount(0);
    await expect(page.locator('[data-testid="invoice"]')).toContainText('BR-0000');
  });

  test('is not offered on an order that has not been paid', async ({ page }) => {
    await openDetail(page, EXPIRED_TOKEN, EXPIRED_ORDER);

    await expect(page.locator('[data-testid="print-invoice"]')).toHaveCount(0);
    await expect(page.locator('[data-testid="no-invoice"]')).toContainText('no invoice yet');
  });
});

test.describe('the couriers screen', () => {
  test('starts empty and says what that stops', async ({ page }) => {
    await open(page, '/orders/couriers');

    await expect(page.locator('[data-testid="couriers-empty"]')).toContainText(
      'no delivery order can be marked as shipped',
    );
  });

  /**
   * **Nothing on this screen names a courier.**
   *
   * W3-03 shipped a real bank's name and the client's own legal name as placeholder text and both were
   * Major findings. The only example allowed is the placeholder shape, which nobody could mistake for a
   * carrier's address — and this is measured on the rendered page, because a `placeholder` attribute
   * renders.
   */
  test('shows no invented courier name and no invented address', async ({ page }) => {
    await open(page, '/orders/couriers');

    await expect(page.locator('[data-testid="courier-name"]')).toHaveValue('');
    await expect(page.locator('[data-testid="courier-template"]')).toHaveValue('');

    for (const testId of ['courier-name', 'courier-template', 'courier-phone']) {
      expect(
        await page.locator(`[data-testid="${testId}"]`).getAttribute('placeholder'),
        `${testId} must not render a placeholder that could be mistaken for a value`,
      ).toBeNull();
    }

    const text = (await page.locator('body').textContent()) ?? '';

    expect(text).toContain('{trackingNumber}');
    expect(
      text,
      'no host may appear on this screen: which carriers the client uses is their information',
    ).not.toMatch(/https?:\/\/[a-z0-9-]+(\.[a-z0-9-]+)+/i);
  });

  test('refuses a tracking address with no placeholder, naming the box', async ({ page }) => {
    await open(page, '/orders/couriers');

    await page.locator('[data-testid="courier-name"]').fill('xunit-no-placeholder');
    await page.locator('[data-testid="courier-template"]').fill('https://example.test/track');
    await page.locator('[data-testid="courier-save"]').click();

    await expect(page.locator('[data-testid="error-template"]')).toContainText('{trackingNumber}');
    await expect(page.locator('[data-testid="courier-template"]')).toHaveAttribute(
      'aria-invalid',
      'true',
    );
  });

  test('gives the active checkbox a target a finger can hit', async ({ page }) => {
    await open(page, '/orders/couriers');

    // The box itself: 24 px, which is WCAG 2.2 SC 2.5.8's floor. The shared `.adm-check` input is 20 px
    // and the browser suite measured it, which is how this screen came to have its own rule.
    const box = await page.locator('[data-testid="courier-active"]').boundingBox();
    expect(box).not.toBeNull();
    expect(box!.width, 'the checkbox is at least 24 px wide').toBeGreaterThanOrEqual(24);
    expect(box!.height, 'the checkbox is at least 24 px tall').toBeGreaterThanOrEqual(24);

    // And tapping the label toggles it, which is what makes the 44 px row the real control rather than a
    // claim about one.
    await page.locator('[data-testid="courier-active-field"]').click();
    await expect(page.locator('[data-testid="courier-active"]')).not.toBeChecked();
  });

  test('adds a courier and then deactivates it', async ({ page }) => {
    await addCourier(page, 'xunit-deactivate-me');

    await page.locator('[data-testid="edit-1"]').click();
    await expect(page.locator('[data-testid="courier-name"]')).toHaveValue('xunit-deactivate-me');

    await page.locator('[data-testid="courier-active"]').uncheck();
    await page.locator('[data-testid="courier-save"]').click();

    await expect(page.locator('[data-testid="courier-1"]')).toContainText('Not in use');
  });
});

test.describe('the screens themselves', () => {
  /**
   * The project's two non-negotiables, measured at every breakpoint.
   *
   * <b>Three different questions, and this test asks all three</b> (W1-03's lesson): big enough, on
   * screen, and not buried. `undersized` answers the first; `unreachable` answers the second and
   * *throws* when its selector matches nothing, which is the guard `undersized` lacks (#80); and the
   * occlusion check is its own `elementFromPoint` probe, because axe does not enforce tap size at this
   * project's gate and a control can be 44 px and still sit under an opaque bar.
   */
  test('the order list has no sideways scroll and no undersized or buried control', async ({ page }) => {
    await openList(page);

    const clauses = [
      '[data-testid="order-search"]',
      '[data-testid="order-search-submit"]',
      '[data-testid="filter-all"]',
      '[data-testid="filter-Paid"]',
      '[data-testid="sort-oldest"]',
      '[data-testid="orders-prev"]',
      '[data-testid="orders-next"]',
      `[data-testid="open-${PAID_ORDER}"]`,
    ];

    await measureAtEveryWidth(page, clauses);
  });

  /**
   * **The transition buttons are measured <i>before</i> anything is pressed, which the first version of
   * this test did not do.**
   *
   * It clicked Start processing and then swept — so `start-processing` itself, and every other control
   * that only exists in a particular status, was never measured at 320 px. A sweep that silently drops
   * the controls it is about is the W1-03 lesson with the audit done and the subject missing.
   */
  test('the order detail measures its transition buttons before any are pressed', async ({ page }) => {
    await openDetail(page, '44444444-4444-4444-8444-444444444444', PAID_ORDER);

    await measureAtEveryWidth(page, [
      '[data-testid="start-processing"]',
      '[data-testid="internal-note"]',
      '[data-testid="save-note"]',
      '[data-testid="print-invoice"]',
    ]);
  });

  /** The pickup order's own action, which only exists while it is Processing. */
  test('the pickup detail measures Ready for collection and Mark as collected', async ({ page }) => {
    await openDetail(page, '55555555-5555-4555-8555-555555555555', PICKUP_ORDER);

    await measureAtEveryWidth(page, ['[data-testid="ready-for-collection"]']);

    await page.locator('[data-testid="ready-for-collection"]').click();
    await expect(page.locator('[data-testid="mark-delivered"]')).toBeVisible();

    await measureAtEveryWidth(page, ['[data-testid="mark-delivered"]']);
  });

  /**
   * The cancel form and its reason box, which only exist on an unpaid order.
   *
   * **Measured before anything is pressed**, which is W3-04's own lesson: its first sweep clicked Start
   * processing and so never measured the controls that only exist beforehand.
   *
   * **And each selector is guarded individually.** A compound selector's guard is satisfied by its first
   * clause while a later one matches nothing (#80), which is why these are two entries rather than one
   * comma-separated string.
   */
  test('the unpaid detail measures the cancel form before it is used', async ({ page }) => {
    await openDetail(page, UNPAID_TOKEN, UNPAID_ORDER);

    await measureAtEveryWidth(page, [
      '[data-testid="cancel-order"]',
      '[data-testid="cancel-reason-input"]',
    ]);
  });

  /** Accept late payment and its reason box, which only exist on an expired order. */
  test('the expired detail measures Accept late payment and its reason box', async ({ page }) => {
    await openDetail(page, EXPIRED_TOKEN, EXPIRED_ORDER);

    await measureAtEveryWidth(page, [
      '[data-testid="accept-late"]',
      '[data-testid="accept-late-note"]',
    ]);
  });

  /** The refusal's own Reload control, which only exists after a refused transition. */
  test('the refusal’s reload control is measured too', async ({ page }) => {
    await openDetail(page, '44444444-4444-4444-8444-444444444444', PAID_ORDER);
    await armOutcome(page, 'wrong-status');

    await page.locator('[data-testid="start-processing"]').click();
    await expect(page.locator('[data-testid="refusal-reload"]')).toBeVisible();

    await measureAtEveryWidth(page, ['[data-testid="refusal-reload"]']);
  });

  test('the dispatch form has no sideways scroll and no undersized or buried control', async ({ page }) => {
    await addCourier(page, 'xunit-measure-carrier');
    await page.goto(`${adminURL}/orders/44444444-4444-4444-8444-444444444444`);
    await expect(page.locator('[data-testid="order-number"]')).toContainText(PAID_ORDER);

    // Start processing, so the dispatch form's controls are on the screen. The transition buttons that
    // this destroys are measured by the test above, before it is pressed.
    await page.locator('[data-testid="start-processing"]').click();
    await expect(page.locator('[data-testid="submit-dispatch"]')).toBeVisible();

    await measureAtEveryWidth(page, [
      '[data-testid="courier"]',
      '[data-testid="tracking-number"]',
      '[data-testid="submit-dispatch"]',
    ]);
  });

  test('the couriers screen has no sideways scroll and no undersized or buried control', async ({ page }) => {
    await addCourier(page, 'xunit-measure-couriers');

    await measureAtEveryWidth(page, [
      '[data-testid="courier-name"]',
      '[data-testid="courier-template"]',
      '[data-testid="courier-phone"]',
      '[data-testid="courier-sort"]',

      // **The label, not the box.** `for=` makes the label part of the control, so the row is what a
      // person aims at — and the row is what carries the project's 44 px. The box itself is 24 px, which
      // is WCAG 2.2 SC 2.5.8's own minimum and is asserted separately below.
      '[data-testid="courier-active-field"]',
      '[data-testid="courier-save"]',
      '[data-testid="edit-1"]',
    ]);
  });

  test('the invoice has no sideways scroll at any width', async ({ page }) => {
    await open(page, '/orders/44444444-4444-4444-8444-444444444444/invoice');
    await expect(page.locator('[data-testid="invoice-number"]')).toBeVisible();

    await measureAtEveryWidth(page, ['[data-testid="print"]']);
  });

  test('has no serious or critical axe findings on any of the four screens', async ({ page }) => {
    await addCourier(page, 'xunit-axe-carrier');

    for (const path of [
      '/orders',
      '/orders/44444444-4444-4444-8444-444444444444',
      '/orders/44444444-4444-4444-8444-444444444444/invoice',
      '/orders/couriers',
    ]) {
      await page.goto(`${adminURL}${path}`);

      // Each screen fetches on construction, so a scan run before the response arrives is a scan of a
      // loading message.
      await expect(page.locator('h1')).toBeVisible();

      const results = await new AxeBuilder({ page }).analyze();
      const blocking = results.violations.filter((violation) =>
        ['serious', 'critical'].includes(violation.impact ?? ''),
      );

      expect(
        blocking.map((violation) => `${violation.id}: ${violation.help}`),
        `serious or critical axe findings on ${path}`,
      ).toEqual([]);
    }
  });
});

/**
 * The three questions, at the eight project widths.
 *
 * Every clause is asserted to match **exactly one** element first, because `undersized` returns `[]` both
 * when everything passes and when the selector matched nothing (#80) — so a renamed test id would
 * otherwise make the whole sweep pass silently, which is the test-that-cannot-fail shape this project has
 * shipped five of.
 */
async function measureAtEveryWidth(page: Page, clauses: readonly string[]): Promise<void> {
  for (const width of BREAKPOINTS) {
    await page.setViewportSize({ width, height: 900 });

    expect(await horizontalOverflow(page), `horizontal overflow at ${width}px`).toBe(0);

    for (const clause of clauses) {
      await expect(page.locator(clause), `${clause} at ${width}px`).toHaveCount(1);

      expect(await undersized(page, clause), `tap target ${clause} at ${width}px`).toEqual([]);

      // Throws when nothing matches, so this one cannot pass vacuously.
      expect(await unreachable(page, clause), `reachability of ${clause} at ${width}px`).toEqual([]);

      // Not buried. A control can clear 44 px, be inside the viewport, and still be under something —
      // the W1-03 defect that passed a 44 px sweep with 17 controls under an opaque sticky bar.
      //
      // **It scrolls the control into view itself**, and that is not redundant: `elementFromPoint`
      // answers `null` for a point outside the viewport, so probing a control below the fold would read
      // as "not covered" — which is the one case this check exists for, because a sticky bar only buries
      // what has been scrolled under it. `unreachable` above happens to scroll too, so relying on that
      // would make this check silently depend on the order of two clauses.
      const covered = await page.evaluate((selector) => {
        const element = document.querySelector<HTMLElement>(selector);
        if (!element) {
          return 'missing';
        }

        element.scrollIntoView({ block: 'center', inline: 'center', behavior: 'instant' });

        const box = element.getBoundingClientRect();
        const x = box.left + (box.width / 2);
        const y = box.top + (box.height / 2);
        const topmost = document.elementFromPoint(x, y);

        return topmost === null || element.contains(topmost) || topmost.contains(element)
          ? null
          : (topmost as HTMLElement).outerHTML.slice(0, 120);
      }, clause);

      expect(covered, `${clause} is covered at ${width}px`).toBeNull();
    }
  }
}
