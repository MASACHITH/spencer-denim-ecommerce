import AxeBuilder from '@axe-core/playwright';
import { Page, expect, test } from '@playwright/test';

import { adminURL, adminWorktreeRoot } from '../playwright.config';
import { horizontalOverflow, undersized, unreachable } from './support/measure';

/**
 * The admin shell's alert banner (doc 08 §3.8 N2; doc 11 §4.3, §6.1 step 1, §7 G-37).
 *
 * It runs against `e2e/support/admin-catalog-stub.mjs`, which serves `dist/admin` and answers
 * `/api/admin/*` from the same origin. **Issue #126: Playwright never starts the .NET API**, so nothing
 * here can observe a backend change — the endpoint's behaviour, and R10 in particular, is proved against
 * a real SQL Server in `AdminAlertEndpointTests` and `AdminAlertsBehaviourTests`, and the payload the
 * stub answers with is pinned against the real DTO by `AdminAlertShapeTests`. What this file proves is
 * the screen: that the banner is on every admin page, that it says what can actually be done, and that
 * acknowledging one does not throw a staff member's focus away or leave them with nothing announced.
 */

/** docs/04 §4 and the project's non-negotiable list. */
const BREAKPOINTS = [320, 375, 390, 768, 1024, 1280, 1440, 1920] as const;

/**
 * **The server has to be this worktree's own before a single answer from it is trusted.**
 *
 * `reuseExistingServer` is on outside CI and the admin port is derived from the worktree path, which
 * makes a collision unlikely rather than impossible. `admin-catalog.e2e.ts` and `admin-settings.e2e.ts`
 * make the same check for the same reason: a port that answers is not proof that it is yours (doc 14).
 */
test.beforeAll(async ({ request }) => {
  const response = await request.get(`${adminURL}/__worktree`);
  expect(response.ok(), `the admin stub at ${adminURL} did not answer /__worktree`).toBe(true);

  const { root } = (await response.json()) as { root: string };

  expect(
    root.replace(/\\/g, '/').toLowerCase(),
    `the server on ${adminURL} is serving ${root}, not this worktree`,
  ).toBe(adminWorktreeRoot.replace(/\\/g, '/').toLowerCase());
});

/**
 * Seeds this browser context's alerts and opens an admin page.
 *
 * The stub keys its state on an `e2e_state` cookie, so parallel workers do not clear each other's
 * alerts — `admin-catalog.e2e.ts` learned that the expensive way. **Nothing is seeded by default**, which
 * is what keeps this suite's other files measuring the pages they were written against.
 */
async function openWithAlerts(
  page: Page,
  set: 'dead' | 'dead-no-order' | 'many' | 'none',
  path = '/orders',
): Promise<void> {
  const state = `alerts-${Math.random().toString(36).slice(2)}`;

  await page.context().addCookies([{ name: 'e2e_state', value: state, url: adminURL }]);

  const seeded = await page.request.get(`${adminURL}/__test__/alerts?set=${set}`, {
    headers: { cookie: `e2e_state=${state}` },
  });
  expect(seeded.ok()).toBe(true);

  await page.goto(`${adminURL}${path}`);

  // The live region exists before any alert loads, so waiting for it is waiting for the shell rather
  // than for the fetch.
  await expect(page.locator('[data-testid="alert-announcement"]')).toHaveCount(1);
}

test.describe('the banner is in the shell (ADM-04 does not exist yet)', () => {
  /**
   * **The design decision, asserted.** Doc 08 §3.8 calls this "the dashboard banner" and ADM-04's
   * dashboard is a scaffold placeholder; doc 11 §6.1 step 1's morning routine starts from these banners,
   * so a dead email has to reach a Super Admin wherever in the panel they are.
   */
  for (const path of ['/', '/orders', '/settings', '/audit-log']) {
    test(`shows on ${path}`, async ({ page }) => {
      await openWithAlerts(page, 'dead', path);

      await expect(page.locator('[data-testid="alert-banner"]')).toBeVisible();
      await expect(page.locator('[data-testid="alert-1"]')).toContainText('BankTransferInstructions');
    });
  }

  /**
   * **The banner does not put a heading in front of the page's `<h1>`.**
   *
   * `accessibility.e2e.ts`' own rule, from #76 and #20's SH11: the first heading in the DOM order of the
   * document says what the page is. The banner renders above `<main>`, so an `<h2>` in it would be
   * precisely that defect — department headings and a "Menu" before the page said what it was — and axe
   * would not catch it, because nested heading order is a best-practice rule and the gate is set at
   * serious and critical. Measured over the DOM order of every heading, not over what is visible.
   */
  for (const path of ['/', '/orders', '/orders/bank-transfers', '/orders/couriers', '/catalog', '/products', '/products/new', '/settings', '/content/pages', '/content/pages/new', '/enquiries', '/audit-log', '/security', '/users', '/users/new', '/roles']) {
    test(`${path} still says what the page is before anything else`, async ({ page }) => {
      await openWithAlerts(page, 'many', path);

      await expect(page.locator('[data-testid="alert-banner"]')).toBeVisible();
      await expect(page.getByRole('heading', { level: 1 })).toBeAttached();

      const headings = await page.evaluate(() =>
        [...document.querySelectorAll('h1, h2, h3, h4, h5, h6')].map(
          (element) => `${element.tagName} ${(element.textContent ?? '').trim().slice(0, 40)}`,
        ),
      );

      expect(headings.length, 'the page has no headings at all').toBeGreaterThan(0);
      expect(headings[0], `headings were ${JSON.stringify(headings)}`).toMatch(/^H1 /);
    });
  }

  test('shows nothing at all when there are no alerts', async ({ page }) => {
    await openWithAlerts(page, 'none');

    await expect(page.locator('[data-testid="alert-banner"]')).toHaveCount(0);

    // The live region survives, which is what lets a confirmation be written somewhere the banner's own
    // disappearance cannot destroy.
    await expect(page.locator('[data-testid="alert-announcement"]')).toHaveCount(1);
  });

  test('is not on the sign-in page, where there is no session to ask about', async ({ page }) => {
    await page.goto(`${adminURL}/login`);

    await expect(page.getByRole('heading', { level: 1 })).toBeVisible();
    await expect(page.locator('[data-testid="alert-banner"]')).toHaveCount(0);
  });
});

test.describe('what it tells a Super Admin they can do (doc 11 §7 G-37, G-42)', () => {
  /**
   * **The sentence that must stay honest.** There is no resend action anywhere in this build — doc 11
   * §6.1 step 6 says "resend from the order" and that action is G-42, unspecified — so the banner sends
   * them to the order and the telephone, which the order screen really can give them.
   */
  test('sends them to the order and the telephone, and never to a resend button', async ({ page }) => {
    await openWithAlerts(page, 'dead');

    const item = page.locator('[data-testid="alert-1"]');

    await expect(item).toContainText('Open the order');
    await expect(item).toContainText('telephone or WhatsApp');
    await expect(item).toContainText('no way to resend an email');
    await expect(item).not.toContainText(/resend it/i);

    // `/admin/` is the application's base href (angular.json), so the router link resolves under it.
    // Asserted as the rendered href rather than as the route array, because the href is what a staff
    // member's browser follows and what a middle-click opens.
    await expect(page.locator('[data-testid="alert-order-1"]')).toHaveAttribute(
      'href',
      '/admin/orders/44444444-4444-4444-8444-444444444444',
    );
  });

  test('says there is nothing to open for a message that is not about an order', async ({ page }) => {
    await openWithAlerts(page, 'dead-no-order');

    await expect(page.locator('[data-testid="alert-1"]')).toContainText('request a new link');
    await expect(page.locator('[data-testid="alert-order-1"]')).toHaveCount(0);
  });

  test('never says acknowledging fixes anything', async ({ page }) => {
    await openWithAlerts(page, 'dead');

    const banner = page.locator('[data-testid="alert-banner"]');

    await expect(banner).toContainText('records that you have seen it');
    await expect(banner).toContainText('does not fix the problem');
  });

  test('says when it is not showing them all', async ({ page }) => {
    await openWithAlerts(page, 'many');

    await expect(page.locator('[data-testid="alert-banner"]')).toContainText('more are waiting');
    await expect(page.locator('li[data-testid^="alert-"]')).toHaveCount(10);
  });
});

test.describe('acknowledging (doc 08 §3.8: audited, and resolves nothing)', () => {
  /**
   * **The three defects this project has already shipped, in one test.**
   *
   * Issue #109: `[disabled]` on a button whose state changes under the user throws focus to `<body>`.
   * And a message written into a node that the same update removes is invisible — a branch last week
   * shipped that twice and a blind customer pressed Cancel and heard nothing at all. Acknowledging a
   * banner is exactly that shape of update.
   */
  test('keeps the alert on screen, moves focus to the outcome, and announces it', async ({ page }) => {
    await openWithAlerts(page, 'dead');

    const button = page.locator('[data-testid="alert-ack-1"]');
    await expect(button).toBeVisible();

    // Never `disabled`: `aria-disabled` plus a refusal in the handler is the rule (issue #109).
    await expect(button).not.toHaveAttribute('disabled', /.*/);

    await page.locator('[data-testid="alert-note-1"]').fill('Rang the customer');
    await button.click();

    const outcome = page.locator('[data-testid="alert-done-1"]');

    await expect(outcome).toBeVisible();
    await expect(outcome).toContainText('Acknowledged at');
    await expect(outcome).toContainText('The problem itself is not resolved');

    // The item is still there — not torn out from under the focus — and the button it held is gone.
    await expect(page.locator('[data-testid="alert-1"]')).toBeVisible();
    await expect(button).toHaveCount(0);

    // Focus is on the outcome, not on `<body>`.
    await expect(outcome).toBeFocused();

    // And the confirmation is in the live region, which the acknowledgement did not destroy.
    await expect(page.locator('[data-testid="alert-announcement"]')).toContainText('Acknowledged');
  });

  test('is gone on the next page load, and the one that was cleared does not come back', async ({ page }) => {
    await openWithAlerts(page, 'dead');

    await page.locator('[data-testid="alert-ack-1"]').click();
    await expect(page.locator('[data-testid="alert-done-1"]')).toBeVisible();

    await page.goto(`${adminURL}/settings`);
    await expect(page.locator('[data-testid="alert-announcement"]')).toHaveCount(1);
    await expect(page.locator('[data-testid="alert-banner"]')).toHaveCount(0);
  });

  /**
   * ADM-22: `role="status"` announces a change, so two identical sentences in a row would be read out
   * once. Here there is no "…ing" sentence to alternate with, so the trailing space is the guarantee.
   */
  test('re-announces when two alerts produce the same sentence', async ({ page }) => {
    await openWithAlerts(page, 'many');

    const region = page.locator('[data-testid="alert-announcement"]');

    await page.locator('[data-testid="alert-ack-1"]').click();
    await expect(page.locator('[data-testid="alert-done-1"]')).toBeVisible();
    const first = await region.textContent();

    await page.locator('[data-testid="alert-ack-2"]').click();
    await expect(page.locator('[data-testid="alert-done-2"]')).toBeVisible();
    const second = await region.textContent();

    // Different strings — so the region changed and the reader hears it again — with the same words.
    expect(second).not.toBe(first);
    expect(second?.trim()).not.toBe('');
  });
});

test.describe('the banner at every breakpoint', () => {
  /**
   * **Three questions, not one** (doc 04 §4 and this project's own list): is every control big enough,
   * is it on screen, and is it not buried under something else. axe answers none of the three.
   *
   * Each selector is guarded on its own, because a compound selector's guard is satisfied by its first
   * clause while a later one matches nothing (issue #80).
   */
  for (const width of BREAKPOINTS) {
    test(`is usable at ${width}px with no horizontal scroll`, async ({ page }) => {
      await page.setViewportSize({ width, height: 900 });
      await openWithAlerts(page, 'many');

      await expect(page.locator('[data-testid="alert-banner"]')).toBeVisible();

      expect(await horizontalOverflow(page), `horizontal overflow at ${width}px`).toBe(0);

      for (const selector of [
        '[data-testid="alert-ack-1"]',
        '[data-testid="alert-note-1"]',
        '[data-testid="alert-order-1"]',
      ]) {
        const count = await page.locator(selector).count();
        expect(count, `${selector} is not on the page at ${width}px`).toBeGreaterThan(0);

        expect(await undersized(page, selector), `${selector} is under 44px at ${width}px`).toEqual([]);
        expect(await unreachable(page, selector), `${selector} is buried at ${width}px`).toEqual([]);
      }
    });
  }

  test('has no axe violations', async ({ page }) => {
    await openWithAlerts(page, 'many');

    const results = await new AxeBuilder({ page }).analyze();

    test.info().annotations.push({
      type: 'axe',
      description: JSON.stringify(
        results.violations.map((violation) => ({
          id: violation.id,
          impact: violation.impact,
          targets: violation.nodes.map((node) => node.target.join(' ')),
        })),
        null,
        2,
      ),
    });

    expect(results.violations).toEqual([]);
  });
});
