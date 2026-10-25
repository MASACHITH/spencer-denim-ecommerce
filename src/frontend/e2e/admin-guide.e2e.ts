import { Page, expect, test } from '@playwright/test';

import { adminURL } from '../playwright.config';
import {
  expectFitsEveryWidth,
  expectHeadingOrder,
  expectNoAxeViolations,
  ownState,
  trustOnlyThisWorktree,
} from './support/admin-content-e2e';
import { horizontalOverflow } from './support/measure';

/**
 * OPS-04 (#199): the admin user guide, against `e2e/support/admin-content-stub.mjs` (it only has to answer the
 * session and the alert banner; the guide itself is static).
 */
trustOnlyThisWorktree();

async function open(page: Page, fragment = ''): Promise<void> {
  await ownState(page, 'guide');
  await page.goto(`${adminURL}/guide${fragment}`);
  await expect(page.getByRole('heading', { level: 1, name: 'User guide' })).toBeVisible();
}

test.describe('the user guide (#199)', () => {
  test('is reached from the sidebar and from the dashboard, titled for the tab', async ({ page }) => {
    await ownState(page, 'guide');
    await page.setViewportSize({ width: 1280, height: 900 });
    await page.goto(`${adminURL}/`);
    await expect(page.getByTestId('dashboard-guide-link')).toBeVisible();

    await page.getByTestId('admin-sidebar').getByRole('link', { name: 'User guide' }).click();

    await expect(page).toHaveURL(/\/guide$/);
    await expect(page).toHaveTitle(/^User guide — /);
  });

  test('lists every section once, in a sensible heading order, with no accessibility violations', async ({ page }) => {
    // axe walks the whole guide in one pass, and the guide is a long page of tables: 30 s is not enough on a busy
    // machine, and a timeout is not a violation.
    test.setTimeout(150_000);
    await open(page);

    const sections = await page.locator('section.gs').count();
    expect(sections).toBeGreaterThan(10);
    await expect(page.locator('section.gs > h2')).toHaveCount(sections);
    await expectHeadingOrder(page);
    await expectNoAxeViolations(page);
  });

  test('fits every width with no sideways scroll, and its controls are big enough', async ({ page }) => {
    await open(page);

    await expectFitsEveryWidth(page, ['#guide-search', '.guide__print']);
  });

  test('keeps wide tables inside themselves at 320 px', async ({ page }) => {
    await open(page);
    await page.setViewportSize({ width: 320, height: 800 });

    const tables = page.locator('.gs__table');
    expect(await tables.count()).toBeGreaterThan(3);
    expect(await horizontalOverflow(page)).toBe(0);
    for (const table of await tables.all()) {
      await expect(table).toHaveAttribute('tabindex', '0');
      await expect(table).toHaveAttribute('role', 'region');
    }
  });

  test('narrows to a search and says how many sections are left', async ({ page }) => {
    await open(page);
    const all = await page.locator('section.gs').count();

    await page.getByLabel('Search the guide').fill('bank transfer');

    const found = await page.locator('section.gs').count();
    expect(found).toBeGreaterThan(0);
    expect(found).toBeLessThan(all);
    await expect(page.getByTestId('guide-count')).toHaveText(`${found} of ${all} sections match your search.`);
    await expect(page.locator('.guide__toc-link:visible')).toHaveCount(found);
  });

  test('says so, plainly, when nothing matches', async ({ page }) => {
    await open(page);

    await page.getByLabel('Search the guide').fill('zzzqqqxxx');

    await expect(page.getByTestId('guide-empty')).toContainText('zzzqqqxxx');
    await expect(page.locator('section.gs')).toHaveCount(0);
  });

  test('jumps to a section from the contents list and puts focus on its heading', async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 800 });
    await open(page);
    const link = page.locator('.guide__toc-link').nth(6);
    const title = (await link.textContent())?.trim() ?? '';

    await link.click();

    const heading = page.getByRole('heading', { level: 2, name: title, exact: true });
    await expect(heading).toBeFocused();
    await expect(heading).toBeInViewport();
    await expect(page).toHaveURL(/\/guide$/);
  });

  test('opens the contents list on a phone, jumps, and closes it', async ({ page }) => {
    await page.setViewportSize({ width: 375, height: 800 });
    await open(page);
    const toggle = page.getByRole('button', { name: 'Show contents' });

    await expect(page.locator('.guide__toc-link').first()).toBeHidden();
    await toggle.click();
    await expect(page.getByRole('button', { name: 'Hide contents' })).toHaveAttribute('aria-expanded', 'true');
    const link = page.locator('.guide__toc-link').nth(4);
    const title = (await link.textContent())?.trim() ?? '';
    await link.click();

    await expect(page.getByRole('button', { name: 'Show contents' })).toHaveAttribute('aria-expanded', 'false');
    await expect(page.getByRole('heading', { level: 2, name: title, exact: true })).toBeInViewport();
  });

  test('opens straight at a section named in the address', async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 800 });
    await open(page, '#photos-and-video');

    await expect(page.locator('#photos-and-video > h2')).toBeInViewport();
    await expect(page.locator('#photos-and-video > h2')).toBeFocused();
  });

  test('sends its "Open" links to real admin screens', async ({ page }) => {
    await open(page);

    const hrefs = await page.locator('.gs__screen').evaluateAll((links) => links.map((a) => a.getAttribute('href')));
    expect(hrefs.length).toBeGreaterThan(10);
    expect(hrefs.every((href) => href?.startsWith('/'))).toBe(true);
  });

  test('prints without the application frame', async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await open(page);
    await page.emulateMedia({ media: 'print' });

    await expect(page.locator('#admin-sidebar')).toBeHidden();
    await expect(page.locator('.admin-header')).toBeHidden();
    await expect(page.locator('.guide__tools')).toBeHidden();
    await expect(page.locator('section.gs').first()).toBeVisible();
  });
});
