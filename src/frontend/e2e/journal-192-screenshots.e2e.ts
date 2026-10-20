import { expect, test } from '@playwright/test';

import { adminURL, brandingApiURL, brandingURL } from '../playwright.config';
import { ownState, trustOnlyThisWorktree } from './support/admin-content-e2e';

/**
 * Journal evidence for #192, not a regression test: the three new admin screens and the storefront header
 * with a 200-character announcement, at 1440 and 390 px. Skipped unless `E2E_JOURNAL_SHOTS` names the folder
 * to write into, so the suite never writes into docs/ on its own.
 */
const out = process.env['E2E_JOURNAL_SHOTS'];

test.describe.configure({ mode: 'serial' });
trustOnlyThisWorktree();

for (const width of [1440, 390]) {
  test(`screenshots at ${width}px`, async ({ page }) => {
    test.skip(!out, 'E2E_JOURNAL_SHOTS is not set');

    await page.setViewportSize({ width, height: 900 });

    await ownState(page, 'home');
    await page.goto(`${adminURL}/content/home`);
    await expect(page.locator('[data-testid="section-Newsletter"]')).toBeVisible();
    await page.screenshot({ path: `${out}/admin-homepage-${width}.png`, fullPage: false });

    await ownState(page, 'announcements');
    await page.goto(`${adminURL}/content/announcements`);
    await expect(page.getByRole('heading', { level: 1 })).toBeVisible();
    await page.screenshot({ path: `${out}/admin-announcements-${width}.png`, fullPage: false });

    await ownState(page, 'size-guides');
    await page.goto(`${adminURL}/content/size-guides`);
    await page.locator('a[href*="/content/size-guides/"]:not([href$="/new"])').first().click();
    await expect(page.getByRole('heading', { level: 1 })).toBeVisible();
    await page.screenshot({ path: `${out}/admin-size-guide-${width}.png`, fullPage: false });

    await page.request.get(`${brandingApiURL}/__test__/announcements?state=long`);
    await page.goto(`${brandingURL}/`);
    await expect(page.getByTestId('announcement')).toBeVisible();
    await page.screenshot({ path: `${out}/storefront-long-announcement-${width}.png`, fullPage: false });
    await page.request.get(`${brandingApiURL}/__test__/announcements?state=default`);
  });
}
