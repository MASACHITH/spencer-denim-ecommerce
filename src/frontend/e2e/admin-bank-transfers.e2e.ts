import AxeBuilder from '@axe-core/playwright';
import { Page, expect, test } from '@playwright/test';

import { adminURL, adminWorktreeRoot } from '../playwright.config';
import { horizontalOverflow, undersized, unreachable } from './support/measure';

/**
 * PAY-01 / ADM-09 in the admin application: "Orders → Bank transfers awaiting approval", and the bank
 * details form that is its precondition (doc 11 P7 steps 4, 6 and 8).
 *
 * It runs against `e2e/support/admin-catalog-stub.mjs`, which serves `dist/admin` and answers
 * `/api/admin/*` from the same origin. The endpoints themselves are proved against a real SQL Server in
 * `AdminBankTransferEndpointTests`; what this file proves is **the screen** — and specifically the three
 * things no jsdom spec can measure: that nothing scrolls sideways at 320 px, that every control clears
 * 44 px, and that no control is buried.
 */

/** docs/04 §4 and the project's non-negotiable list. */
const BREAKPOINTS = [320, 375, 390, 768, 1024, 1280, 1440, 1920] as const;

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
 * Opens the queue with a state of this test's own.
 *
 * The stub keys its state on an `e2e_state` cookie, so parallel workers do not decide each other's
 * transfers — `admin-catalog.e2e.ts` learned that the expensive way, and this screen writes more than that
 * one does.
 */
async function openQueue(page: Page, state = `bt-${Math.random().toString(36).slice(2)}`): Promise<string> {
  await page.context().addCookies([{ name: 'e2e_state', value: state, url: adminURL }]);

  await page.goto(`${adminURL}/orders/bank-transfers`);

  // The page fetches on construction, so the heading alone is not enough: a row asserted before the
  // response arrives is a row that is legitimately absent.
  await expect(
    page.getByRole('heading', { level: 1, name: 'Bank transfers awaiting approval' }),
  ).toBeVisible();
  await expect(page.locator('[data-testid="row-SD-2026-000123"]')).toBeVisible();

  return state;
}

/** Arms the stub's next decision. `ok` is the default; the others are the 409s the real API answers. */
async function armOutcome(page: Page, outcome: 'ok' | 'expired' | 'paid' | 'short'): Promise<void> {
  const response = await page.request.get(
    `${adminURL}/__test__/bank-transfer-outcome?outcome=${outcome}`,
    { headers: { Cookie: `e2e_state=${await stateCookie(page)}` } },
  );

  expect(response.ok()).toBe(true);
}

async function stateCookie(page: Page): Promise<string> {
  const cookies = await page.context().cookies(adminURL);

  return cookies.find((cookie) => cookie.name === 'e2e_state')?.value ?? 'shared';
}

test.describe('the queue (doc 11 P7 step 4)', () => {
  test('shows the order, the customer, the amount, the reference and the slip', async ({ page }) => {
    await openQueue(page);

    const row = page.locator('[data-testid="row-SD-2026-000123"]');

    await expect(row).toContainText('SD-2026-000123');
    await expect(row).toContainText('Nimali Perera');
    await expect(row).toContainText('nimali@example.com');
    await expect(page.locator('[data-testid="amount-SD-2026-000123"]')).toContainText('15,596.60');
    await expect(page.locator('[data-testid="reference-SD-2026-000123"]')).toContainText('CBC-778812');

    // The row without a reference says so rather than leaving a blank, because a blank reads as a bug.
    await expect(page.locator('[data-testid="reference-SD-2026-000124"]')).toContainText('None given');
  });

  test('says how long is left, and how overdue an overdue one is', async ({ page }) => {
    await openQueue(page);

    await expect(page.locator('[data-testid="time-left-SD-2026-000123"]')).toContainText('19 h');
    await expect(page.locator('[data-testid="time-left-SD-2026-000124"]')).toContainText('Overdue by 37 min');
  });

  /**
   * Doc 11 P7 step 8 — **and the first version of this test asserted the defect**, with a comment claiming
   * the overdue row offered "the action that will actually work".
   *
   * Every row in this queue has a `Pending` payment, so every one is still `PendingPayment` with stock
   * reserved, so every one is approvable whatever its deadline says. The expiry job runs every five
   * minutes over `TOP (50)`: an overdue row is normally minutes from being swept, longer with a backlog,
   * and never if the sweep is failing. The old screen offered only Accept late payment there, the server
   * refused it with `order_not_closed`, and because the clock never goes back under zero, Refresh could
   * not get staff out of the dead end — while the reserved stock stayed sellable to somebody else.
   */
  test('offers Approve on every queued row, however overdue', async ({ page }) => {
    await openQueue(page);

    await expect(page.locator('[data-testid="time-left-SD-2026-000124"]')).toContainText('Overdue by');

    for (const number of ['SD-2026-000123', 'SD-2026-000124']) {
      await expect(page.locator(`[data-testid="approve-${number}"]`)).toBeVisible();
      await expect(page.locator(`[data-testid="accept-late-${number}"]`)).toHaveCount(0);
    }
  });

  /**
   * The slip is a **download**, not a preview.
   */
  test('serves the slip as an attachment that nothing caches', async ({ page }) => {
    await openQueue(page);

    const href = await page.locator('[data-testid="slip-SD-2026-000123"]').getAttribute('href');
    expect(href).toContain('/slip');

    const response = await page.request.get(`${adminURL}${href}`);

    expect(response.status()).toBe(200);

    // A slip is a stranger's bank document and a PDF is a programmable container; `attachment` plus
    // `nosniff` keeps it from rendering inside the admin's own session, and `no-store` keeps it out of a
    // shop computer's disk cache.
    expect(response.headers()['content-disposition']).toBe('attachment');
    expect(response.headers()['x-content-type-options']).toBe('nosniff');
    expect(response.headers()['cache-control']).toContain('no-store');
  });
});

test.describe('the three decisions (doc 11 P7 steps 6 and 8)', () => {
  test('approves, reports the invoice number, and keeps the row on screen', async ({ page }) => {
    await openQueue(page);

    const approve = page.locator('[data-testid="approve-SD-2026-000123"]');
    await approve.focus();
    await approve.click();

    await expect(page.locator('[data-testid="done-SD-2026-000123"]')).toContainText('INV-2026-000045');

    // **The row keeps its place.** Removing the node the staff member's focus is inside does the same harm
    // as disabling it (issue #109's second half), so this is the assertion that the decided row is still
    // there — and that focus is still on something real rather than on <body>.
    await expect(page.locator('[data-testid="row-SD-2026-000123"]')).toBeVisible();

    expect(await page.evaluate(() => document.activeElement?.tagName)).not.toBe('BODY');

    // The live region said what happened, which is the only confirmation a screen-reader user gets.
    await expect(page.locator('[data-testid="announcement"]')).toContainText('INV-2026-000045');
  });

  test('refuses a rejection with no reason, without a request', async ({ page }) => {
    await openQueue(page);

    let posted = 0;
    page.on('request', (request) => {
      if (request.url().includes('/reject')) {
        posted += 1;
      }
    });

    await page.locator('[data-testid="reject-SD-2026-000123"]').click();

    await expect(page.locator('[data-testid="refused-SD-2026-000123"]')).toContainText('Say why');
    expect(posted, 'a rejection with no reason must not reach the server').toBe(0);

    await page.locator('[data-testid="note-SD-2026-000123"]').fill('The amount is Rs. 500 short.');
    await page.locator('[data-testid="reject-SD-2026-000123"]').click();

    await expect(page.locator('[data-testid="done-SD-2026-000123"]')).toContainText(
      'stays open until its deadline',
    );
  });

  /**
   * Doc 11 P7's business rule: "if the expiry job or another staff member acted first, the approver is told
   * and the screen reloads." Told *what to do next* is the part that matters.
   */
  test('turns Approve into Accept late payment when the order expired underneath it', async ({ page }) => {
    await openQueue(page);
    await armOutcome(page, 'expired');

    await page.locator('[data-testid="approve-SD-2026-000123"]').click();

    await expect(page.locator('[data-testid="refused-SD-2026-000123"]')).toContainText(
      'Accept late payment',
    );

    await expect(page.locator('[data-testid="accept-late-SD-2026-000123"]')).toBeVisible();
    await expect(page.locator('[data-testid="approve-SD-2026-000123"]')).toHaveCount(0);

    // And the offered action then works.
    await armOutcome(page, 'ok');
    await page.locator('[data-testid="accept-late-SD-2026-000123"]').click();

    await expect(page.locator('[data-testid="done-SD-2026-000123"]')).toContainText('INV-2026-000045');
  });

  test('names every short line when a late acceptance is refused', async ({ page }) => {
    await openQueue(page);

    // Accept late payment is reached the only way it can be reached now: the server refuses the approval
    // because expiry has won, and *that* is what puts the button there.
    await armOutcome(page, 'expired');
    await page.locator('[data-testid="approve-SD-2026-000124"]').click();
    await expect(page.locator('[data-testid="accept-late-SD-2026-000124"]')).toBeVisible();

    await armOutcome(page, 'short');
    await page.locator('[data-testid="accept-late-SD-2026-000124"]').click();

    const refused = page.locator('[data-testid="refused-SD-2026-000124"]');

    // Both, not just the first: a staff member told about one of two will press the button again and be
    // told about the other (doc 11 P7 step 8).
    await expect(refused).toContainText('Slim jeans');
    await expect(refused).toContainText('0 left, 1 needed');
    await expect(refused).toContainText('Denim jacket');
    await expect(refused).toContainText('1 left, 2 needed');

    // **And the row still offers Accept late payment.** An expired order can never be approved, and a
    // screen that offers Approve here sends staff round a loop: Approve → order_expired → Accept late →
    // short_stock → Approve. Measured in the browser because it is a rendering decision.
    await expect(page.locator('[data-testid="accept-late-SD-2026-000124"]')).toBeVisible();
    await expect(page.locator('[data-testid="approve-SD-2026-000124"]')).toHaveCount(0);
  });

  /**
   * Issue #109, measured rather than asserted from the attribute alone.
   */
  test('an in-flight decision keeps the focus on the button that was pressed', async ({ page }) => {
    await openQueue(page);

    // The response is held until the test lets it go, so the in-flight state is observable rather than a
    // race against it.
    // A concise body rather than `() => {}`: the e2e folder is outside `ng lint`'s scope today, so an
    // empty-function error here would go unseen until somebody widened it (checkout.e2e.ts has one).
    let release = (): void => undefined;
    const held = new Promise<void>((resolve) => {
      release = resolve;
    });

    await page.route('**/bank-transfers/*/approve', async (route) => {
      await held;
      await route.continue();
    });

    const approve = page.locator('[data-testid="approve-SD-2026-000123"]');
    await approve.focus();
    await approve.click();

    await expect(approve).toHaveAttribute('aria-disabled', 'true');

    // `disabled` is the thing that must not be there: disabling the focused node drops focus to <body>.
    expect(await approve.evaluate((button: HTMLButtonElement) => button.disabled)).toBe(false);
    expect(await page.evaluate(() => document.activeElement?.getAttribute('data-testid'))).toBe(
      'approve-SD-2026-000123',
    );

    // Playwright's toBeDisabled honours aria-disabled, so this is the assertion that reads the way a
    // reviewer expects while the element stays focusable.
    await expect(approve).toBeDisabled();

    release();
    await expect(page.locator('[data-testid="done-SD-2026-000123"]')).toBeVisible();
  });
});

test.describe('the bank details form (PAY-01, doc 11 P7 preconditions)', () => {
  async function openSettings(page: Page): Promise<void> {
    await page.context().addCookies([
      { name: 'e2e_state', value: `bank-${Math.random().toString(36).slice(2)}`, url: adminURL },
    ]);

    await page.goto(`${adminURL}/settings`);

    await expect(page.getByRole('heading', { level: 1, name: 'Settings' })).toBeVisible();
    await expect(page.getByRole('heading', { level: 2, name: 'Bank transfer' })).toBeVisible();
  }

  test('saves the bank details and keeps the grouping that was typed', async ({ page }) => {
    await openSettings(page);

    await page.locator('[data-testid="field-bankName"]').fill('Example Bank PLC');
    await page.locator('[data-testid="field-branch"]').fill('Example Branch');
    await page.locator('[data-testid="field-accountName"]').fill('Example Shop (Pvt) Ltd');
    await page.locator('[data-testid="field-accountNumber"]').fill('0000 1111 2222');

    await page.locator('[data-testid="save-bank"]').click();

    await expect(page.locator('[data-testid="announcement"]')).toContainText('Bank details saved');

    // Re-read after the save, so what is on screen is what the server kept — including the spacing, which
    // a shopper copies into online banking.
    await page.reload();
    await expect(page.getByRole('heading', { level: 2, name: 'Bank transfer' })).toBeVisible();
    await expect(page.locator('[data-testid="field-accountNumber"]')).toHaveValue('0000 1111 2222');
  });

  test('shows the API message against the account-number box it refused', async ({ page }) => {
    await openSettings(page);

    await page.locator('[data-testid="field-accountNumber"]').fill('to be confirmed');
    await page.locator('[data-testid="save-bank"]').click();

    await expect(page.locator('[data-testid="error-accountNumber"]')).toContainText(
      'digits, spaces and dashes',
    );
    await expect(page.locator('[data-testid="field-accountNumber"]')).toHaveAttribute(
      'aria-invalid',
      'true',
    );
  });
});

test.describe('the screen itself', () => {
  /**
   * The project's two non-negotiables, measured at every breakpoint.
   *
   * <b>Three different questions, and this test asks all three</b> (W1-03's lesson): big enough, on screen,
   * and not buried. `undersized` answers the first; `unreachable` answers the second and *throws* when its
   * selector matches nothing, which is the guard `undersized` lacks (#80); and the occlusion check is its
   * own rectangle intersection, because axe does not enforce tap size at this project's gate and a control
   * can be 44 px and still sit under an opaque bar.
   */
  test('has no sideways scroll and no undersized or buried control at any width', async ({ page }) => {
    await openQueue(page);

    // Accept late payment is measured too, and it only exists once the server has refused an approval as
    // expired — so this arranges that state rather than quietly dropping the control from the sweep.
    await armOutcome(page, 'expired');
    await page.locator('[data-testid="approve-SD-2026-000124"]').click();
    await expect(page.locator('[data-testid="accept-late-SD-2026-000124"]')).toBeVisible();

    // Every clause as its own clause. `undersized` returns [] both when everything passes and when the
    // selector matched nothing (#80), so a renamed test id would make this pass silently.
    const clauses = [
      '[data-testid="refresh"]',
      '[data-testid="approve-SD-2026-000123"]',
      '[data-testid="reject-SD-2026-000123"]',
      '[data-testid="note-SD-2026-000123"]',
      '[data-testid="slip-SD-2026-000123"]',
      '[data-testid="accept-late-SD-2026-000124"]',
    ];

    for (const width of BREAKPOINTS) {
      await page.setViewportSize({ width, height: 900 });

      expect(await horizontalOverflow(page), `horizontal overflow at ${width}px`).toBe(0);

      for (const clause of clauses) {
        await expect(page.locator(clause), `${clause} at ${width}px`).toHaveCount(1);

        expect(await undersized(page, clause), `tap target ${clause} at ${width}px`).toEqual([]);

        // Throws when nothing matches, so this one cannot pass vacuously.
        expect(await unreachable(page, clause), `reachability of ${clause} at ${width}px`).toEqual([]);
      }

      // Not buried. A control can clear 44 px, be inside the viewport, and still be under something —
      // which is the W1-03 defect that passed a 44 px sweep with 17 controls under an opaque sticky bar.
      for (const clause of clauses) {
        const covered = await page.evaluate((selector) => {
          const element = document.querySelector<HTMLElement>(selector);
          if (!element) {
            return 'missing';
          }

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
  });

  test('has no serious or critical axe findings', async ({ page }) => {
    await openQueue(page);

    const results = await new AxeBuilder({ page }).analyze();
    const blocking = results.violations.filter((violation) =>
      ['serious', 'critical'].includes(violation.impact ?? ''),
    );

    expect(
      blocking.map((violation) => `${violation.id}: ${violation.help}`),
      'serious or critical axe findings on the bank-transfer queue',
    ).toEqual([]);
  });

  test('says so when nothing is waiting', async ({ page }) => {
    await page.context().addCookies([
      { name: 'e2e_state', value: `bt-empty-${Math.random().toString(36).slice(2)}`, url: adminURL },
    ]);

    // Both rows decided, so the queue the next read returns is genuinely empty — and the empty state is
    // what a shop sees on a good morning rather than an edge case.
    await page.route('**/bank-transfers?*', async (route) => {
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ rows: [], page: 1, pageSize: 20, totalCount: 0 }),
      });
    });

    await page.goto(`${adminURL}/orders/bank-transfers`);

    await expect(page.locator('[data-testid="queue-empty"]')).toContainText('Nothing is waiting');
  });
});
