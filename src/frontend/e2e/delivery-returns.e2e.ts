import AxeBuilder from '@axe-core/playwright';
import { Page, expect, test } from '@playwright/test';

import { apiURL } from '../playwright.config';
import { horizontalOverflow, undersized } from './support/measure';

/**
 * The product page's delivery and returns panel (PDP-05, issue #33) in a real browser against the SSR server:
 * fees from the rate feed, in the server-rendered HTML; native disclosures operable from the keyboard with the
 * names a screen reader announces; axe; no sideways scroll at the eight widths; pickup only when listed; a
 * changed fee shown as sent; a hostile text inert; and a failed feed leaving the product page whole.
 *
 * **Serial**, because `/__test__/delivery-returns` switches the shared stub. Every switch is put back to
 * `default` after each test; no other spec asserts on the panel, and every state is layout-safe.
 */
test.describe.configure({ mode: 'serial' });

const PRODUCT = '/product/test-product-1';

const BREAKPOINTS = [320, 375, 390, 768, 1024, 1280, 1440, 1920] as const;

async function panelState(page: Page, state: 'default' | 'pickup' | 'changed' | 'hostile' | 'fail' | 'wide'): Promise<void> {
  const response = await page.request.get(`${apiURL}/__test__/delivery-returns?state=${state}`);
  expect(response.ok(), 'the delivery-returns switch did not answer').toBe(true);
}

async function useStubApi(page: Page): Promise<void> {
  await page.route('**/api/**', async (route) => {
    const url = new URL(route.request().url());
    const response = await route.fetch({ url: `${apiURL}${url.pathname}${url.search}` });
    await route.fulfill({ response });
  });
}

async function openBoth(page: Page): Promise<void> {
  for (const id of ['pdp-delivery', 'pdp-returns']) {
    const details = page.locator(`[data-testid="${id}"]`);
    if ((await details.getAttribute('open')) === null) {
      await details.locator('summary').click();
    }
    await expect(details).toHaveAttribute('open', '');
  }
}

test.afterEach(async ({ page }) => {
  await panelState(page, 'default');
});

test('the fees are in the server-rendered HTML, drawn from the rate feed', async ({ request }) => {
  const html = await (await request.get(PRODUCT)).text();

  expect(html).toContain('Within Sri Lanka');
  expect(html).toContain('Rs. 400, free over Rs. 10,000, 1–2 working days');
  expect(html).toContain('Gampaha and Kalutara');
  expect(html).toContain('Rs. 950, 1 working day');
  expect(html).toContain('We also deliver worldwide');
  expect(html).toContain('Return or exchange within 14 days of delivery');

  // Same for every visitor: the HTML names no district of the visitor's.
  expect(html).not.toContain('your district: ');
});

test('both disclosures open from the keyboard and carry the names a screen reader announces', async ({ page }) => {
  await useStubApi(page);
  await page.goto(PRODUCT);

  const delivery = page.locator('[data-testid="pdp-delivery"]');
  const returns = page.locator('[data-testid="pdp-returns"]');

  await expect(delivery.locator('summary')).toHaveText('Delivery');
  await expect(returns.locator('summary')).toHaveText('Returns and exchanges');
  await expect(delivery).not.toHaveAttribute('open', '');

  await delivery.locator('summary').focus();
  await page.keyboard.press('Enter');
  await expect(delivery).toHaveAttribute('open', '');

  await returns.locator('summary').focus();
  await page.keyboard.press(' ');
  await expect(returns).toHaveAttribute('open', '');

  // The Sri Lankan rates are a named region; each method's list is named by the method.
  const region = page.getByRole('region', { name: 'Within Sri Lanka' });
  await expect(region).toBeVisible();
  await expect(region.locator('dl[aria-labelledby="pdp-rates-STANDARD"]')).toBeVisible();
  await expect(page.locator('#pdp-rates-STANDARD')).toHaveText('Standard delivery');
  await expect(page.locator('[data-testid="pdp-delivery-estimate"]')).toContainText('1–5 working days');

  // Closing with the keyboard works too.
  await delivery.locator('summary').focus();
  await page.keyboard.press('Enter');
  await expect(delivery).not.toHaveAttribute('open', '');
});

test('axe finds nothing in the open panel', async ({ page }) => {
  await useStubApi(page);
  await page.goto(PRODUCT);
  await openBoth(page);

  const results = await new AxeBuilder({ page })
    .include('[data-testid="pdp-delivery"]')
    .include('[data-testid="pdp-returns"]')
    .analyze();

  expect(results.violations.map((violation) => `${violation.id}: ${violation.help}`)).toEqual([]);
});

for (const width of BREAKPOINTS) {
  test(`no sideways scroll and full-size disclosure targets at ${width}px with the panel open`, async ({ page }) => {
    await page.setViewportSize({ width, height: 900 });
    await panelState(page, 'pickup');
    await useStubApi(page);
    await page.goto(PRODUCT);
    await openBoth(page);

    expect(await horizontalOverflow(page)).toBe(0);
    expect(await undersized(page, '[data-testid="pdp-delivery"] > summary, [data-testid="pdp-returns"] > summary')).toEqual([]);

    // Every rate line stays inside the viewport.
    const widest = await page.locator('[data-testid="pdp-delivery"] .rates__row').evaluateAll((rows) =>
      Math.max(...rows.map((row) => row.getBoundingClientRect().right)),
    );
    expect(widest).toBeLessThanOrEqual(width);
  });
}

test('store pickup is shown only when the feed lists a pickup point', async ({ page }) => {
  await useStubApi(page);
  await page.goto(PRODUCT);
  await openBoth(page);
  await expect(page.locator('[data-testid="delivery-pickup"]')).toHaveCount(0);

  await panelState(page, 'pickup');
  await page.goto(PRODUCT);
  await openBoth(page);

  const pickup = page.locator('[data-testid="delivery-pickup"]');
  await expect(pickup).toContainText('Colombo Wardrobe Flagship, Colombo 3');
  await expect(pickup).toContainText('Free, ready in 1–2 working days');
});

test('a fee changed in the rate table is the fee the page shows', async ({ page }) => {
  await panelState(page, 'changed');
  await useStubApi(page);
  await page.goto(PRODUCT);
  await openBoth(page);

  const standard = page.locator('[data-testid="pdp-delivery"] [data-testid="delivery-method-STANDARD"]');
  await expect(standard).toContainText('Rs. 375, free over Rs. 8,000, 1–2 working days');
  await expect(standard).not.toContainText('Rs. 400');
});

test('a hostile text written round the API renders inert', async ({ page }) => {
  await panelState(page, 'hostile');
  await useStubApi(page);
  await page.goto(PRODUCT);
  await openBoth(page);

  await expect(page.locator('[data-testid="pdp-delivery-text"]')).toContainText('Fast.');
  expect(await page.evaluate(() => (window as unknown as { __pdpXss?: number }).__pdpXss)).toBeUndefined();
  await expect(page.locator('[data-testid="pdp-delivery-text"] script')).toHaveCount(0);
  await expect(page.locator('[data-testid="pdp-delivery-text"] [onerror]')).toHaveCount(0);
});

test('a failed feed draws no panel and leaves the product page whole', async ({ page }) => {
  await panelState(page, 'fail');
  await useStubApi(page);
  const response = await page.goto(PRODUCT);

  expect(response?.status()).toBe(200);
  await expect(page.locator('[data-testid="add-to-bag"]')).toBeVisible();
  await expect(page.locator('[data-testid="pdp-delivery"]')).toHaveCount(0);
});

test('no link is offered to a Shipping or Returns page that is not published', async ({ page }) => {
  // The shared stub publishes no content pages (content-pages.e2e.ts covers the published case).
  await useStubApi(page);
  await page.goto(PRODUCT);
  await openBoth(page);

  await expect(page.locator('[data-testid="pdp-shipping-link"]')).toHaveCount(0);
  await expect(page.locator('[data-testid="pdp-returns-link"]')).toHaveCount(0);
});

// Tester (#33): the admin may save any markup the server's allow-list keeps — headings, tables, images with a width
// attribute, long links — and the panel must still never scroll sideways.
for (const width of BREAKPOINTS) {
  test(`allow-listed wide markup in the delivery text does not scroll sideways at ${width}px`, async ({ page }) => {
    // KNOWN DEFECT (tester, #33): an <img> with a large width attribute (both allow-listed by HtmlSanitiser) widens
    // the product page's column: 322 px of sideways scroll at 320, 258 at 768, 130 at 1024, 38 at 1440. Removing the
    // image alone removes the overflow; the table, heading and long link are fine. Expected to fail until the
    // panel caps images (or the sanitiser drops img/width from these texts); remove this line with the fix.
    test.fail(width <= 1440, 'Known #33 defect: an allow-listed wide image in the delivery text scrolls sideways');

    await page.setViewportSize({ width, height: 900 });
    await panelState(page, 'wide');
    await useStubApi(page);
    await page.route('https://example.test/**', (route) => route.fulfill({ status: 404, body: '' }));
    await page.goto(PRODUCT);
    await openBoth(page);

    await expect(page.locator('[data-testid="pdp-delivery-text"] table')).toHaveCount(1);

    // Name what sticks out, so a failure says which element to fix.
    const offenders = await page.locator('[data-testid="pdp-delivery-text"] *').evaluateAll((nodes, limit) =>
      nodes
        .filter((node) => node.getBoundingClientRect().right > limit)
        .map((node) => `${node.tagName.toLowerCase()} right=${Math.round(node.getBoundingClientRect().right)}`),
      width,
    );
    expect(offenders).toEqual([]);
    expect(await horizontalOverflow(page)).toBe(0);

    const right = await page.locator('[data-testid="pdp-delivery-text"]').evaluate((element) =>
      Math.max(...[element, ...Array.from(element.querySelectorAll('*'))].map((node) => node.getBoundingClientRect().right)),
    );
    expect(right).toBeLessThanOrEqual(width);
  });
}

test('a hostile text is not in the server-rendered HTML either', async ({ request, page }) => {
  await panelState(page, 'hostile');
  const html = await (await request.get(PRODUCT)).text();

  expect(html).toContain('Fast.');
  expect(html).not.toMatch(/<script>window\.__pdpXss/);
  expect(html).not.toMatch(/<img[^>]*onerror/i);
});
