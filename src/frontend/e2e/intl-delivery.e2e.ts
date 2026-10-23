import AxeBuilder from '@axe-core/playwright';
import { Page, Route, expect, test } from '@playwright/test';

import { apiURL } from '../playwright.config';
import { horizontalOverflow } from './support/measure';

/**
 * Owner decision 2026-10-21: Sri Lanka plus worldwide delivery, in a real browser.
 *
 * The header's country selector, the bag's overseas estimate, checkout's country-first form and the
 * account address book's overseas shape. The catalogue, bag, checkout, order and country calls go to
 * `e2e/support/catalog-api-stub.mjs`, whose overseas rules are `DestinationRules`' and
 * `OverseasDeliveryRate`'s copied field for field (no more permissive than the API); the account address
 * calls are answered by {@link AddressBookFake} below, which refuses exactly what
 * `CustomerAccountValidator.ValidateAddress` refuses for the shapes used here. The real behaviour is pinned
 * against SQL Server by the backend's endpoint tests.
 */

const BREAKPOINTS = [320, 375, 390, 768, 1024, 1280, 1440, 1920] as const;

const COUNTRY_KEY = 'sd.delivery-country';

const INTERNATIONAL = 9;

async function useStubApi(page: Page): Promise<void> {
  await page.route('**/api/**', async (route) => {
    const url = new URL(route.request().url());
    const response = await route.fetch({ url: `${apiURL}${url.pathname}${url.search}` });
    await route.fulfill({ response });
  });
}

async function hydrated(page: Page): Promise<void> {
  await page.waitForFunction(() => !!document.querySelector('sd-root')?.hasAttribute('ng-version'));
}

/** A product that the stub always has in stock, as `checkout.e2e.ts` picks them. */
async function addToBag(page: Page, index: number): Promise<void> {
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

/** Opens checkout for a remembered country and waits for the International option to be priced. */
async function openOverseasCheckout(page: Page, country: string): Promise<void> {
  await page.evaluate(([key, value]) => localStorage.setItem(key, value), [COUNTRY_KEY, country]);

  const priced = page.waitForResponse((response) => {
    const url = new URL(response.url());
    return (
      url.pathname === '/api/checkout'
      && url.searchParams.get('countryCode') === country
      && url.searchParams.get('deliveryMethodId') === String(INTERNATIONAL)
    );
  });

  await page.goto('/checkout');
  await priced;
  await hydrated(page);
  await expect(page.locator('#co-country')).toHaveValue(country);
}

async function fillOverseas(page: Page, phone = '+44 20 7946 0958'): Promise<void> {
  await page.locator('#co-firstName').fill('Amelia');
  await page.locator('#co-email').fill('amelia@example.co.uk');
  await page.locator('#co-phone').fill(phone);
  await page.locator('#co-recipient').fill('Amelia Jones');
  await page.locator('#co-address-phone').fill(phone);
  await page.locator('#co-line1').fill('10 Downing Street');
  await page.locator('#co-city').fill('London');
}

test.describe('the header country selector (GLB-06)', () => {
  test.beforeEach(async ({ page }) => {
    await useStubApi(page);
  });

  test('lists the countries, hides the district for an overseas one and remembers it', async ({ page }) => {
    await page.goto('/');
    await hydrated(page);

    const country = page.locator('[data-testid="country-select"]').first();
    await expect(country).toHaveValue('LK');
    await expect(page.locator('[data-testid="district-select"]').first()).toBeAttached();

    await country.selectOption('GB');

    await expect(page.locator('[data-testid="district-select"]')).toHaveCount(0);
    expect(await page.evaluate((key) => localStorage.getItem(key), COUNTRY_KEY)).toBe('GB');

    await page.reload();
    await hydrated(page);
    await expect(page.locator('[data-testid="country-select"]').first()).toHaveValue('GB');

    await page.locator('[data-testid="country-select"]').first().selectOption('LK');
    await expect(page.locator('[data-testid="district-select"]').first()).toBeAttached();
  });

  for (const country of ['LK', 'US'] as const) {
    test(`the header with ${country} chosen never scrolls sideways and passes axe`, async ({ page }) => {
      await page.goto('/');
      await page.evaluate(([key, value]) => localStorage.setItem(key, value), [COUNTRY_KEY, country]);
      await page.reload();
      await hydrated(page);
      await expect(page.locator('[data-testid="country-select"]').first()).toHaveValue(country);

      for (const width of BREAKPOINTS) {
        await page.setViewportSize({ width, height: 900 });
        expect(await horizontalOverflow(page), `${width}px`).toBe(0);
      }

      const results = await new AxeBuilder({ page }).include('sd-site-header').analyze();
      expect(results.violations).toEqual([]);
    });
  }
});

test.describe('the bag estimate', () => {
  test.beforeEach(async ({ page }) => {
    await useStubApi(page);
  });

  test('prices delivery to the overseas country chosen in the header, and Sri Lanka is unchanged', async ({
    page,
  }) => {
    await addToBag(page, 21);
    await page.goto('/bag');
    await hydrated(page);

    await expect(page.locator('[data-testid="bag-overseas-estimate"]')).toHaveCount(0);
    await expect(page.getByText('Delivery is worked out at checkout')).toBeVisible();

    await page.locator('[data-testid="country-select"]').first().selectOption('GB');

    await expect(page.locator('[data-testid="bag-overseas-estimate"]')).toHaveText(
      /Delivery to United Kingdom: Rs\. 9,800, 7–14 working days\./,
    );

    await page.locator('[data-testid="country-select"]').first().selectOption('AQ');
    await expect(page.locator('[data-testid="bag-overseas-estimate"]')).toHaveText(
      /We cannot deliver to Antarctica at the moment\./,
    );
  });
});

test.describe('checkout overseas (CHK-01)', () => {
  test.beforeEach(async ({ page }) => {
    await useStubApi(page);
  });

  test('a UK order: no district, International delivery at the UK fee, postcode required', async ({ page }) => {
    await addToBag(page, 29);
    await openOverseasCheckout(page, 'GB');

    await expect(page.locator('#co-district')).toHaveCount(0);
    await expect(page.locator(`input[name="deliveryMethodId"][value="${INTERNATIONAL}"]`)).toBeChecked();
    await expect(page.locator('[data-sum="delivery"]')).toHaveText(/9,800/);
    await expect(page.locator('input[name="deliveryMethodId"]')).toHaveCount(1);

    await fillOverseas(page);
    // Forced: the button is `aria-disabled` while the form is incomplete, and pressing it is what raises
    // the field messages (the page refuses the press rather than disabling the control, #109).
    await page.locator('.checkout__place').click({ force: true });
    await expect(page.locator('#co-postcode-error')).toHaveText('Enter the postcode for United Kingdom.');

    await page.locator('#co-postcode').fill('SW1A 2AA');
    const placed = page.waitForURL(/\/order\//);
    await page.locator('.checkout__place').click();
    await placed;

    await expect(page.locator('[data-testid="order-country"]')).toHaveText('United Kingdom');
    await expect(page.locator('[data-testid="order-overseas-address"]')).toHaveText(/London\s+SW1A 2AA/);
  });

  test('a US order needs the state, and a phone with its country code', async ({ page }) => {
    await addToBag(page, 23);
    await openOverseasCheckout(page, 'US');

    await fillOverseas(page, '202 555 0143');
    await page.locator('#co-postcode').fill('20500');
    await page.locator('.checkout__place').click({ force: true });

    await expect(page.locator('#co-region-error')).toHaveText('Enter the state, province or region.');
    await expect(page.locator('#co-address-phone-error')).toContainText('Include the country code');

    await page.locator('#co-address-phone').fill('+1 202 555 0143');
    await page.locator('#co-region').fill('District of Columbia');

    const placed = page.waitForURL(/\/order\//);
    await page.locator('.checkout__place').click();
    await placed;

    await expect(page.locator('[data-testid="order-overseas-address"]')).toHaveText(
      /London, District of Columbia\s+20500/,
    );
    await expect(page.locator('[data-testid="order-country"]')).toHaveText('United States');
  });

  test('a UAE order needs neither a postcode nor a state', async ({ page }) => {
    await addToBag(page, 24);
    await openOverseasCheckout(page, 'AE');

    await fillOverseas(page, '+971 4 123 4567');
    await expect(page.locator('.checkout__place')).not.toHaveAttribute('aria-disabled', 'true');

    const placed = page.waitForURL(/\/order\//);
    await page.locator('.checkout__place').click();
    await placed;

    await expect(page.locator('[data-testid="order-country"]')).toHaveText('United Arab Emirates');
  });

  test('a country the shop does not deliver to is named, and nothing can be placed', async ({ page }) => {
    await addToBag(page, 25);
    await page.evaluate(([key, value]) => localStorage.setItem(key, value), [COUNTRY_KEY, 'AQ']);

    const read = page.waitForResponse(
      (response) => new URL(response.url()).searchParams.get('countryCode') === 'AQ',
    );
    await page.goto('/checkout');
    await read;
    await hydrated(page);

    await expect(page.getByText('We cannot deliver to Antarctica at the moment.')).toBeVisible();
    await expect(page.locator('.checkout__place')).toHaveAttribute('aria-disabled', 'true');
  });

  test('switching back to Sri Lanka on the page brings the district back', async ({ page }) => {
    await addToBag(page, 26);
    await openOverseasCheckout(page, 'GB');

    const read = page.waitForResponse(
      (response) =>
        new URL(response.url()).pathname === '/api/checkout'
        && !new URL(response.url()).searchParams.has('countryCode'),
    );
    await page.locator('#co-country').selectOption('LK');
    await read;

    await expect(page.locator('#co-district')).toBeVisible();
    await expect(page.locator('#co-region')).toHaveCount(0);
  });

  test('the overseas form never scrolls sideways and passes axe, at every breakpoint', async ({ page }) => {
    await addToBag(page, 27);
    await openOverseasCheckout(page, 'US');
    await page.locator('.checkout__place').click({ force: true });
    await expect(page.locator('#co-region-error')).toBeVisible();

    for (const width of BREAKPOINTS) {
      await page.setViewportSize({ width, height: 900 });
      expect(await horizontalOverflow(page), `${width}px`).toBe(0);
    }

    const results = await new AxeBuilder({ page }).include('sd-checkout-page').analyze();
    expect(results.violations).toEqual([]);
  });
});

/**
 * The address book's API, answered in the browser for a signed-in customer, refusing what the real API
 * refuses for these shapes: a Sri Lankan address needs a district; an overseas one follows
 * `DestinationRules` (region and postcode where the country always uses them, a phone with its country
 * code).
 */
class AddressBookFake {
  private readonly book: Record<string, unknown>[] = [];
  private nextId = 50;

  async install(page: Page): Promise<void> {
    await useStubApi(page);
    await page.route('**/api/account/**', (route) => this.answer(route));
  }

  private async answer(route: Route): Promise<void> {
    const request = route.request();
    const path = new URL(request.url()).pathname;

    if (path === '/api/account/session') {
      return json(route, 200, {
        customer: {
          userId: 7,
          email: 'nimal@example.invalid',
          firstName: 'Nimal',
          lastName: 'Perera',
          emailConfirmed: true,
        },
      });
    }

    if (path === '/api/account/addresses' && request.method() === 'GET') {
      return json(route, 200, this.book);
    }

    if (path === '/api/account/addresses' && request.method() === 'POST') {
      const body = request.postDataJSON() as Record<string, string | number | boolean | null>;
      const errors = refusals(body);

      if (Object.keys(errors).length > 0) {
        return route.fulfill({
          status: 400,
          contentType: 'application/problem+json',
          body: JSON.stringify({ title: 'One or more validation errors occurred.', status: 400, errors }),
        });
      }

      const country = COUNTRIES[String(body['countryCode'] ?? 'LK')];
      this.book.push({
        id: this.nextId++,
        label: body['label'] ?? null,
        recipientName: body['recipientName'],
        phone: body['phone'],
        line1: body['line1'],
        line2: body['line2'] ?? null,
        city: body['city'],
        districtId: country.domestic ? body['districtId'] : null,
        districtName: country.domestic ? 'Colombo' : null,
        districtIsActive: true,
        postalCode: body['postalCode'] ?? null,
        landmark: country.domestic ? (body['landmark'] ?? null) : null,
        isDefault: this.book.length === 0,
        countryCode: body['countryCode'] ?? 'LK',
        countryName: country.name,
        region: country.domestic ? null : (body['region'] ?? null),
      });

      return json(route, 201, this.book);
    }

    return json(route, 404, {});
  }
}

const COUNTRIES: Record<string, { name: string; domestic: boolean; postal: boolean; region: boolean }> = {
  LK: { name: 'Sri Lanka', domestic: true, postal: false, region: false },
  GB: { name: 'United Kingdom', domestic: false, postal: true, region: false },
  US: { name: 'United States', domestic: false, postal: true, region: true },
  AE: { name: 'United Arab Emirates', domestic: false, postal: false, region: false },
  AQ: { name: 'Antarctica', domestic: false, postal: false, region: false },
};

function refusals(body: Record<string, string | number | boolean | null>): Record<string, string[]> {
  const errors: Record<string, string[]> = {};
  const text = (key: string): string => (typeof body[key] === 'string' ? (body[key] as string).trim() : '');

  for (const [key, message] of [
    ['recipientName', 'Enter the name the courier should ask for.'],
    ['phone', 'Enter a phone number the courier can call.'],
    ['line1', 'Enter the first line of the address.'],
    ['city', 'Enter the town or city.'],
  ] as const) {
    if (!text(key)) {
      errors[key] = [message];
    }
  }

  const country = COUNTRIES[String(body['countryCode'] ?? 'LK')];
  if (!country) {
    errors['countryCode'] = ['Choose the country your order is going to.'];
    return errors;
  }

  if (country.domestic) {
    if (typeof body['districtId'] !== 'number' || body['districtId'] <= 0) {
      errors['districtId'] = ['Choose a district.'];
    }
    return errors;
  }

  if (!text('region') && country.region) {
    errors['region'] = ['Enter the state, province or region.'];
  }

  const postal = text('postalCode');
  if (!postal && country.postal) {
    errors['postalCode'] = [`Enter the postcode for ${country.name}.`];
  } else if (postal && !/^[A-Za-z0-9](?:[A-Za-z0-9 -]*[A-Za-z0-9])?$/.test(postal)) {
    errors['postalCode'] = ['A postcode can use only letters, numbers, spaces and hyphens.'];
  }

  if (text('phone') && !(text('phone').startsWith('+') || text('phone').startsWith('00'))) {
    errors['phone'] = ['Include the country code, for example +44 20 7946 0958.'];
  }

  return errors;
}

function json(route: Route, status: number, body: unknown): Promise<void> {
  return route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
}

test.describe('the account address book abroad (ACC-02)', () => {
  test('saves a US address with its state, shows the country, and refuses a missing state', async ({ page }) => {
    await new AddressBookFake().install(page);
    await page.goto('/account/addresses');
    await hydrated(page);

    const country = page.locator('[data-testid="address-field-country"]');
    await expect(country).toHaveValue('LK');
    await country.selectOption('US');

    await expect(page.locator('[data-testid="address-field-district"]')).toHaveCount(0);

    await page.locator('[data-testid="address-field-recipient"]').fill('Ann Lee');
    await page.locator('[data-testid="address-field-phone"]').fill('+1 202 555 0143');
    await page.locator('[data-testid="address-field-line1"]').fill('1600 Pennsylvania Avenue NW');
    await page.locator('[data-testid="address-field-city"]').fill('Washington');
    await page.locator('[data-testid="address-field-postal"]').fill('20500');
    await page.locator('[data-testid="address-save"]').click();

    await expect(page.locator('#ad-region-error')).toHaveText('Enter the state, province or region.');

    await page.locator('[data-testid="address-field-region"]').fill('DC');
    await page.locator('[data-testid="address-save"]').click();

    await expect(page.locator('[data-testid="address-country"]')).toHaveText('United States');

    for (const width of BREAKPOINTS) {
      await page.setViewportSize({ width, height: 900 });
      expect(await horizontalOverflow(page), `${width}px`).toBe(0);
    }

    const results = await new AxeBuilder({ page }).include('main').analyze();
    expect(results.violations).toEqual([]);
  });
});
