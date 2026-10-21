import { expect, test } from '@playwright/test';

import { adminURL, megaMenuApiURL, megaMenuURL } from '../playwright.config';
import { fakeFile, ownState, trustOnlyThisWorktree } from './support/admin-content-e2e';

/**
 * Journal evidence for #215, not a regression test: the mega-menu card screen with a card on, and the storefront's
 * Women menu with that card (desktop) or the drawer (phone), at 1440 and 390 px. Skipped unless
 * `E2E_JOURNAL_SHOTS` names the folder to write into, so the suite never writes into docs/ on its own.
 */
const out = process.env['E2E_JOURNAL_SHOTS'];

test.describe.configure({ mode: 'serial' });
trustOnlyThisWorktree();

for (const width of [1440, 390]) {
  test(`screenshots at ${width}px`, async ({ page }) => {
    test.skip(!out, 'E2E_JOURNAL_SHOTS is not set');

    await page.setViewportSize({ width, height: 900 });

    await ownState(page, 'mm-shots');
    await page.goto(`${adminURL}/content/mega-menu`);
    await page.locator('[data-testid="mm-edit-1"]').click();
    await page.locator('[data-testid="mm-image-file"]').setInputFiles(fakeFile('linen.webp', 'image/webp'));
    await page.locator('[data-testid="mm-image-alt"]').fill('A model in a linen dress');
    await page.locator('[data-testid="mm-image-upload"]').click();
    await page.locator('[data-testid="mm-title"]').fill('Linen days');
    await page.locator('[data-testid="mm-subtitle"]').fill('Breathable pieces for the heat.');
    await page.locator('[data-testid="mm-link"]').fill('/women/dresses?material=linen');
    await page.locator('[data-testid="mm-active"]').check();
    await page.locator('[data-testid="mm-save"]').click();
    await expect(page.locator('[data-testid="mm-status-1"]')).toHaveText('On the menu now');
    await page.locator('[data-testid="mm-edit-1"]').click();
    await page.evaluate(() => window.scrollTo(0, 0));
    await page.screenshot({ path: `${out}/admin-mega-menu-${width}.png`, fullPage: true });

    await page.request.get(`${megaMenuApiURL}/__test__/mega-menu-cards?state=card`);
    await page.goto(`${megaMenuURL}/`);
    if (width >= 1024) {
      await page.locator('header').getByRole('link', { name: 'Women', exact: true }).first().hover();
      await expect(page.locator('#mega-women')).toBeVisible();
    } else {
      await page.locator('[data-testid="menu-toggle"]').click();
      await page.locator('#storefront-menu').getByRole('button', { name: /Women/ }).first().click();
    }
    await page.screenshot({ path: `${out}/storefront-mega-menu-${width}.png`, fullPage: false });
    await page.request.get(`${megaMenuApiURL}/__test__/mega-menu-cards?state=none`);
  });
}
