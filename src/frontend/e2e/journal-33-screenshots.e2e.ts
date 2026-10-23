import { Page, expect, test } from '@playwright/test';

import { adminURL, contentPagesApiURL, contentPagesURL } from '../playwright.config';

/**
 * Journal evidence for #33, not a regression test: the product page's delivery and returns panel open, the
 * Shipping page with its live fees, and the admin editor, at 1440 and 390 px. Skipped unless
 * `E2E_JOURNAL_SHOTS` names the folder to write into, so the suite never writes into docs/ on its own.
 */
const out = process.env['E2E_JOURNAL_SHOTS'];

test.describe.configure({ mode: 'serial' });

async function useStubApi(page: Page): Promise<void> {
  await page.route('**/api/**', async (route) => {
    const url = new URL(route.request().url());
    const response = await route.fetch({ url: `${contentPagesApiURL}${url.pathname}${url.search}` });
    await route.fulfill({ response });
  });
}

for (const width of [1440, 390]) {
  test(`screenshots at ${width}px`, async ({ page }) => {
    test.skip(!out, 'E2E_JOURNAL_SHOTS is not set');

    await page.setViewportSize({ width, height: 900 });
    await page.request.get(`${contentPagesApiURL}/__test__/content-pages?state=all`);
    await page.request.get(`${contentPagesApiURL}/__test__/delivery-returns?state=default`);
    await useStubApi(page);

    try {
      await page.goto(`${contentPagesURL}/product/test-product-1`);
      for (const id of ['pdp-delivery', 'pdp-returns']) {
        await page.locator(`[data-testid="${id}"] summary`).click();
      }
      const panel = page.locator('[data-testid="pdp-delivery"]');
      await expect(panel).toHaveAttribute('open', '');
      await panel.scrollIntoViewIfNeeded();
      await page.screenshot({ path: `${out}/pdp-delivery-returns-${width}.png`, fullPage: true });

      await page.goto(`${contentPagesURL}/shipping`);
      await expect(page.locator('[data-testid="shipping-rates"]')).toBeVisible();
      await page.screenshot({ path: `${out}/shipping-page-${width}.png`, fullPage: true });

      await page.goto(`${contentPagesURL}/returns`);
      await page.screenshot({ path: `${out}/returns-page-${width}.png`, fullPage: true });
    } finally {
      await page.request.get(`${contentPagesApiURL}/__test__/content-pages?state=none`);
    }

    await page.unrouteAll();
    await page.context().addCookies([{ name: 'e2e_state', value: `pdp-shots-${width}`, url: adminURL }]);
    await page.goto(`${adminURL}/settings`);
    const editor = page.locator('[data-testid="pdp-texts-panel"]');
    await expect(editor).toBeVisible();
    await editor.scrollIntoViewIfNeeded();
    await editor.screenshot({ path: `${out}/admin-pdp-texts-${width}.png` });
  });
}
