import AxeBuilder from '@axe-core/playwright';
import { Page, expect, test } from '@playwright/test';

import { adminURL, adminWorktreeRoot } from '../playwright.config';
import { horizontalOverflow, undersized } from './support/measure';

/**
 * Owner decision 2026-10-21 in the admin application: the Overseas delivery screen, the Payment methods switch,
 * and the destination country on the orders, invoice, customers and dashboard screens.
 *
 * Against `e2e/support/admin-delivery-stub.mjs`, which mirrors `OverseasDeliveryAdminDto` and
 * `PaymentProvidersAdminDto` field for field and is no more permissive than `OverseasDeliveryRules` and
 * `PaymentProviderSwitchHandler`. The endpoints themselves are proved against SQL Server in the backend suite.
 */

const BREAKPOINTS = [320, 375, 390, 768, 1024, 1280, 1440, 1920] as const;

test.beforeAll(async ({ request }) => {
  const response = await request.get(`${adminURL}/__worktree`);
  expect(response.ok(), `the admin stub at ${adminURL} did not answer /__worktree`).toBe(true);

  const { root } = (await response.json()) as { root: string };
  expect(root.replace(/\\/g, '/').toLowerCase()).toBe(adminWorktreeRoot.replace(/\\/g, '/').toLowerCase());
});

const states = new WeakMap<Page, string>();

/** A state of this test's own, so parallel workers do not edit each other's regions. */
async function freshState(page: Page): Promise<void> {
  const state = `overseas-${Math.random().toString(36).slice(2)}`;
  states.set(page, state);
  await page.context().addCookies([{ name: 'e2e_state', value: state, url: adminURL }]);
}

/** The cookie header the stub keys its state on, for calls made outside the page. */
function stateHeaders(page: Page): Record<string, string> {
  return { Cookie: `e2e_state=${states.get(page)}` };
}

/** A GET to the stub, in this test's state. */
async function stub(page: Page, path: string): Promise<unknown> {
  const response = await page.request.get(`${adminURL}${path}`, { headers: stateHeaders(page) });
  expect(response.ok(), `${path} answered ${response.status()}`).toBe(true);
  return response.json();
}

/** A PUT to the API outside the page, in this test's state, with the antiforgery token. */
interface Answer {
  readonly status: number;
  readonly body: { errors?: Record<string, string[]> } | null;
}

async function apiPut(page: Page, path: string, data: unknown): Promise<Answer> {
  const token = (await stub(page, '/api/admin/auth/antiforgery-token')) as { token: string };
  const response = await page.request.put(`${adminURL}${path}`, {
    headers: { ...stateHeaders(page), 'X-XSRF-TOKEN': token.token },
    data,
  });

  return { status: response.status(), body: await response.json().catch(() => null) };
}

/** The stub's role for this state, and the session the screen reads, narrowed to match. */
async function asRole(page: Page, role: 'super' | 'manager' | 'viewer' | 'none'): Promise<void> {
  await stub(page, `/__test__/delivery-role?role=${role}`);

  const permissions = {
    super: ['Settings.View', 'Settings.Edit', 'Settings.Payments'],
    manager: ['Settings.View', 'Settings.Edit'],
    viewer: ['Settings.View'],
    none: [],
  }[role];

  await page.route('**/api/admin/auth/whoami', async (route) => {
    const response = await route.fetch();
    const session = await response.json();
    await route.fulfill({ response, json: { ...session, grantsAllPermissions: false, permissions } });
  });
}

async function openOverseas(page: Page): Promise<void> {
  await page.goto(`${adminURL}/settings/overseas-delivery`);
  await expect(page.getByRole('heading', { level: 1, name: 'Overseas delivery' })).toBeVisible();
}

async function seriousAxe(page: Page): Promise<unknown[]> {
  const results = await new AxeBuilder({ page })
    .withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa'])
    .analyze();

  return results.violations
    .filter((violation) => violation.impact === 'serious' || violation.impact === 'critical')
    .map((violation) => ({ id: violation.id, nodes: violation.nodes.map((node) => node.target) }));
}

test.describe('the Overseas delivery screen', () => {
  test.beforeEach(async ({ page }) => {
    await freshState(page);
  });

  test('edits a region and shows the new fee on every country in it', async ({ page }) => {
    await openOverseas(page);

    await expect(page.locator('[data-testid="country-effective-GB"]')).toContainText('9,500.00');

    await page.locator('[data-testid="region-fee-EUROPE"]').fill('11000');
    await page.locator('[data-testid="region-min-EUROPE"]').fill('6');
    await page.locator('[data-testid="region-max-EUROPE"]').fill('12');
    await page.locator('[data-testid="region-save-EUROPE"]').click();

    await expect(page.locator('[data-testid="overseas-said"]')).toContainText('Europe is saved');
    await expect(page.locator('[data-testid="country-effective-GB"]')).toContainText('11,000.00');
    await expect(page.locator('[data-testid="country-effective-GB"]')).toContainText('6–12 days');
    await expect(page.locator('[data-testid="country-effective-FR"]')).toContainText('11,000.00');

    // It persisted, not only redrew.
    await page.reload();
    await expect(page.locator('[data-testid="region-fee-EUROPE"]')).toHaveValue('11000');
  });

  test('overrides one country, then disables another, and says why it cannot be ordered to', async ({ page }) => {
    await openOverseas(page);

    await page.locator('[data-testid="country-edit-JP"]').click();
    await page.locator('[data-testid="country-fee-JP"]').fill('8000');
    await page.locator('[data-testid="country-min-JP"]').fill('3');
    await page.locator('[data-testid="country-max-JP"]').fill('6');
    await page.locator('[data-testid="country-save-JP"]').click();

    await expect(page.locator('[data-testid="country-effective-JP"]')).toContainText('8,000.00');
    await expect(page.locator('[data-testid="country-effective-JP"]')).toContainText('3–6 days');
    await expect(page.locator('[data-testid="country-effective-JP"]')).toContainText('Own settings');
    // The rest of its region keeps the region's fee.
    await expect(page.locator('[data-testid="country-effective-IN"]')).toContainText('6,500.00');

    await page.locator('[data-testid="country-edit-GB"]').click();
    await page.locator('[data-testid="country-enabled-GB"]').uncheck();
    await page.locator('[data-testid="country-save-GB"]').click();

    await expect(page.locator('[data-testid="country-effective-GB"]')).toContainText('Not delivered to');
    await expect(page.locator('[data-testid="country-effective-GB"]')).toContainText('Switched off for this country');
  });

  test('switching a region off or International delivery off refuses every country it covers', async ({ page }) => {
    await openOverseas(page);

    await page.locator('[data-testid="region-enabled-ASIA"]').uncheck();
    await page.locator('[data-testid="region-save-ASIA"]').click();
    await expect(page.locator('[data-testid="country-effective-JP"]')).toContainText('Its region is switched off');

    await page.locator('[data-testid="overseas-international-switch"]').uncheck();
    await expect(page.locator('[data-testid="overseas-said"]')).toContainText('switched off');
    await expect(page.locator('[data-testid="country-effective-GB"]')).toContainText('International delivery is switched off');
  });

  test('a region with no fee is not priced, and a country can still price itself', async ({ page }) => {
    await openOverseas(page);

    await page.locator('[data-testid="region-fee-OCEANIA"]').fill('');
    await page.locator('[data-testid="region-save-OCEANIA"]').click();
    await expect(page.locator('[data-testid="country-effective-AU"]')).toContainText('No fee set');

    await page.locator('[data-testid="country-edit-AU"]').click();
    await page.locator('[data-testid="country-fee-AU"]').fill('14000');
    await page.locator('[data-testid="country-save-AU"]').click();
    await expect(page.locator('[data-testid="country-effective-AU"]')).toContainText('14,000.00');
  });

  test('refuses a bad fee and bad days before sending, and the server refuses the same', async ({ page }) => {
    await openOverseas(page);

    await page.locator('[data-testid="region-fee-EUROPE"]').fill('2000000');
    await page.locator('[data-testid="region-min-EUROPE"]').fill('20');
    await page.locator('[data-testid="region-max-EUROPE"]').fill('5');
    await page.locator('[data-testid="region-save-EUROPE"]').click();

    await expect(page.locator('[data-testid="region-error-fee-EUROPE"]')).toContainText('Rs. 1,000,000');
    await expect(page.locator('[data-testid="region-error-days-EUROPE"]')).toContainText('fewest no more than the most');

    // The stub holds the line too: a client that skipped its own check is still refused, field by field.
    const refused = await apiPut(page, '/api/admin/delivery/overseas/regions/EUROPE', {
      fee: 10.555,
      estimatedDaysMin: 0,
      estimatedDaysMax: 5,
      isEnabled: true,
      rowVersion: 'x',
    });
    expect(refused.status).toBe(400);
    expect(Object.keys(refused.body?.errors ?? {}).sort()).toEqual(['estimatedDays', 'fee']);
  });

  test('says somebody else changed a region and reloads with their values', async ({ page }) => {
    await openOverseas(page);

    // Somebody else saves Europe first, behind this page's back.
    const current = (await stub(page, '/api/admin/delivery/overseas')) as {
      regions: { code: string; rowVersion: string }[];
    };
    const europe = current.regions.find((region) => region.code === 'EUROPE')!;
    const other = await apiPut(page, '/api/admin/delivery/overseas/regions/EUROPE', {
      fee: 9900,
      estimatedDaysMin: 7,
      estimatedDaysMax: 14,
      isEnabled: true,
      rowVersion: europe.rowVersion,
    });
    expect(other.status).toBe(200);

    await page.locator('[data-testid="region-fee-EUROPE"]').fill('12000');
    await page.locator('[data-testid="region-save-EUROPE"]').click();

    await expect(page.locator('[data-testid="overseas-problem"]')).toContainText('reloaded');
    await expect(page.locator('[data-testid="region-fee-EUROPE"]')).toHaveValue('9900');
  });

  test('shows the figures but no controls to a role with Settings.View only, and the API agrees', async ({ page }) => {
    await asRole(page, 'viewer');
    await openOverseas(page);

    await expect(page.locator('[data-testid="region-EUROPE"]')).toContainText('9,500.00');
    await expect(page.locator('[data-testid="region-fee-EUROPE"]')).toHaveCount(0);
    await expect(page.locator('[data-testid="country-edit-GB"]')).toHaveCount(0);
    await expect(page.locator('[data-testid="overseas-international-switch"]')).toHaveCount(0);

    const refused = await apiPut(page, '/api/admin/delivery/overseas/international', {
      isEnabled: false,
      rowVersion: 'x',
    });
    expect(refused.status).toBe(403);
  });

  test('explains a refusal to a role without Settings.View', async ({ page }) => {
    await asRole(page, 'none');
    await openOverseas(page);

    await expect(page.locator('[data-testid="overseas-problem"]')).toContainText('You do not have permission');
  });

  test('passes axe, and fits every breakpoint with no sideways scroll and real tap targets', async ({ page }) => {
    await openOverseas(page);
    await page.locator('[data-testid="country-edit-GB"]').click();

    expect(await seriousAxe(page)).toEqual([]);

    for (const width of BREAKPOINTS) {
      await page.setViewportSize({ width, height: 900 });
      await expect(page.locator('[data-testid="region-save-EUROPE"]')).toBeVisible();

      expect(await horizontalOverflow(page), `horizontal overflow at ${width}px`).toBeLessThanOrEqual(0);
      expect(
        await undersized(page, '[data-testid="region-save-EUROPE"], [data-testid="country-save-GB"], [data-testid="country-edit-JP"]'),
        `tap targets at ${width}px`,
      ).toEqual([]);
    }
  });
});

test.describe('the Payment methods switch', () => {
  test.beforeEach(async ({ page }) => {
    await freshState(page);
  });

  async function openSettings(page: Page): Promise<void> {
    await page.goto(`${adminURL}/settings`);
    await expect(page.getByRole('heading', { level: 1, name: 'Settings' })).toBeVisible();
  }

  test('says checkout refuses every order while nothing is on, and switches bank transfer back on', async ({ page }) => {
    await openSettings(page);

    const panel = page.locator('[data-testid="payment-methods-panel"]');
    await expect(panel).toBeVisible();
    await expect(page.locator('[data-testid="payment-methods-none"]')).toContainText('Checkout will refuse every order');

    await page.locator('[data-testid="payment-method-switch-BankTransfer"]').check();

    await expect(page.locator('[data-testid="payment-method-state-BankTransfer"]')).toContainText('offered at checkout');
    await expect(page.locator('[data-testid="payment-methods-none"]')).toHaveCount(0);

    // And off again, the owner's "block only for now".
    await page.locator('[data-testid="payment-method-switch-BankTransfer"]').uncheck();
    await expect(page.locator('[data-testid="payment-methods-none"]')).toBeVisible();
  });

  test('refuses to switch on PayPal until it is ready, then accepts it', async ({ page }) => {
    await openSettings(page);

    // A click, not check(): the refused switch goes straight back to what the server holds, which is the
    // behaviour under test.
    await page.locator('[data-testid="payment-method-switch-PayPal"]').click();
    await expect(page.locator('[data-testid="payment-method-error-PayPal"]')).toContainText('not ready');
    await expect(page.locator('[data-testid="payment-method-switch-PayPal"]')).not.toBeChecked();

    await stub(page, '/__test__/paypal-ready?ready=true');
    await page.reload();

    await page.locator('[data-testid="payment-method-switch-PayPal"]').check();
    await expect(page.locator('[data-testid="payment-method-state-PayPal"]')).toContainText('offered at checkout');
  });

  test('is not drawn for a Store Manager, and the API refuses one', async ({ page }) => {
    await asRole(page, 'manager');
    await openSettings(page);

    await expect(page.locator('[data-testid="payment-methods-panel"]')).toHaveCount(0);

    const refused = await apiPut(page, '/api/admin/settings/payment-providers/BankTransfer', {
      isEnabled: true,
      rowVersion: 'x',
    });
    expect(refused.status).toBe(403);
  });

  test('the bank-transfer queue keeps working with bank transfer switched off', async ({ page }) => {
    await page.goto(`${adminURL}/orders/bank-transfers`);
    await expect(page.getByRole('heading', { level: 1 })).toBeVisible();
    await expect(page.locator('[role="alert"]', { hasText: 'Something went wrong' })).toHaveCount(0);
  });

  test('passes axe and fits every breakpoint', async ({ page }) => {
    await openSettings(page);
    await expect(page.locator('[data-testid="payment-methods-panel"]')).toBeVisible();

    expect(await seriousAxe(page)).toEqual([]);

    for (const width of BREAKPOINTS) {
      await page.setViewportSize({ width, height: 900 });
      expect(await horizontalOverflow(page), `horizontal overflow at ${width}px`).toBeLessThanOrEqual(0);
    }
  });
});

test.describe('the destination on orders, invoices, customers', () => {
  test.beforeEach(async ({ page }) => {
    await freshState(page);
    await stub(page, '/__test__/overseas-order');
  });

  test('the order list shows the district for Sri Lanka and the country for abroad', async ({ page }) => {
    await page.goto(`${adminURL}/orders`);

    await expect(page.locator('[data-testid="destination-SD-2026-000123"]')).toContainText('Colombo');
    await expect(page.locator('[data-testid="destination-SD-2026-000130"]')).toContainText('United States');
    await expect(page.locator('[data-testid="destination-SD-2026-000130"]')).toContainText('Overseas');

    expect(await seriousAxe(page)).toEqual([]);
    for (const width of BREAKPOINTS) {
      await page.setViewportSize({ width, height: 900 });
      expect(await horizontalOverflow(page), `horizontal overflow at ${width}px`).toBeLessThanOrEqual(0);
    }
  });

  test('the order detail and invoice print an international address with its country', async ({ page }) => {
    await page.goto(`${adminURL}/orders/99999999-9999-4999-8999-999999999999`);

    await expect(page.locator('[data-testid="order-delivery"]')).toContainText('Austin, TX, 78701');
    await expect(page.locator('[data-testid="order-country"]')).toContainText('United States');
    await expect(page.locator('[data-testid="order-district"]')).toHaveCount(0);

    for (const width of BREAKPOINTS) {
      await page.setViewportSize({ width, height: 900 });
      expect(await horizontalOverflow(page), `horizontal overflow at ${width}px`).toBeLessThanOrEqual(0);
    }

    await page.goto(`${adminURL}/orders/99999999-9999-4999-8999-999999999999/invoice`);
    await expect(page.locator('[data-testid="invoice-country"]')).toContainText('United States');
  });

  test('the customers list shows the country and the detail an overseas address', async ({ page }) => {
    await stub(page, '/__test__/customers?set=some');
    await page.goto(`${adminURL}/customers`);

    await expect(page.locator('[data-testid="country-41"]')).toContainText('Sri Lanka');
    await expect(page.locator('[data-testid="country-42"]')).toContainText('Canada');

    await page.goto(`${adminURL}/customers/42`);
    await expect(page.locator('[data-testid="address-9"]')).toContainText('Toronto, Ontario');
    await expect(page.locator('[data-testid="address-country-9"]')).toContainText('Canada');

    expect(await seriousAxe(page)).toEqual([]);
    for (const width of BREAKPOINTS) {
      await page.setViewportSize({ width, height: 900 });
      expect(await horizontalOverflow(page), `horizontal overflow at ${width}px`).toBeLessThanOrEqual(0);
    }
  });
});
