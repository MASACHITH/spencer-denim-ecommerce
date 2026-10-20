import { Page, expect, test } from '@playwright/test';

import { adminURL } from '../playwright.config';
import {
  expectFitsEveryWidth,
  expectHeadingOrder,
  expectNoAxeViolations,
  fakeFile,
  ownState,
  stubSwitch,
  trustOnlyThisWorktree,
} from './support/admin-content-e2e';

/**
 * #192 / PDP-04: the size-guide list and editor, against `e2e/support/admin-content-stub.mjs`.
 */
trustOnlyThisWorktree();

async function openList(page: Page): Promise<void> {
  await ownState(page, 'sg');
  await page.goto(`${adminURL}/content/size-guides`);
  await expect(page.getByRole('heading', { level: 1, name: 'Size guides' })).toBeVisible();
  await expect(page.locator('[data-testid="sg-table"]')).toBeVisible();
}

async function openGuide(page: Page, id: number): Promise<void> {
  await ownState(page, 'sg');
  await page.goto(`${adminURL}/content/size-guides/${id}`);
  await expect(page.locator('[data-testid="sg-name"]')).not.toHaveValue('');
}

test.describe('the size-guide screens (#192)', () => {
  test('lists the guides with how many categories and products use them', async ({ page }) => {
    await openList(page);

    await expect(page.locator('[data-testid="sg-categories-1"]')).toHaveText('2');
    await expect(page.locator('[data-testid="sg-products-1"]')).toHaveText('5');
    await expect(page.locator('[data-testid="sg-categories-2"]')).toHaveText('0');
  });

  test('refuses to remove a guide in use, in words, and removes an unused one', async ({ page }) => {
    await openList(page);

    await page.locator('[data-testid="sg-remove-1"]').click();
    await page.locator('[data-testid="sg-remove-1"]').click();
    await expect(page.locator('[data-testid="sg-problem"]')).toContainText('still used by 2 categories and 5 products');

    await page.locator('[data-testid="sg-remove-2"]').click();
    await page.locator('[data-testid="sg-remove-2"]').click();
    await expect(page.locator('[data-testid="sg-2"]')).toHaveCount(0);
  });

  test('creates a guide with a table and an image, and reports what the sanitiser removed', async ({ page }) => {
    await ownState(page, 'sg');
    await page.goto(`${adminURL}/content/size-guides/new`);
    await expect(page.getByRole('heading', { level: 1, name: 'Add a size guide' })).toBeVisible();

    await page.locator('[data-testid="sg-name"]').fill('Jeans');
    await page.locator('[data-testid="sg-intro"]').fill('<p>Fits true.</p><script>alert(1)</script>');
    await page.locator('[data-testid="sg-col-0"]').fill('Waist');
    await page.locator('[data-testid="sg-add-col"]').click();
    await page.locator('[data-testid="sg-col-1"]').fill('Inches');
    await page.locator('[data-testid="sg-cell-0-0"]').fill('S');
    await page.locator('[data-testid="sg-cell-0-1"]').fill('28');
    await page.locator('[data-testid="sg-add-row"]').click();
    await page.locator('[data-testid="sg-cell-1-0"]').fill('M');
    await page.locator('[data-testid="sg-cell-1-1"]').fill('30');

    await page.locator('[data-testid="sg-image-file"]').setInputFiles(fakeFile('measure.webp', 'image/webp'));
    await page.locator('[data-testid="sg-image-alt"]').fill('Where to measure your waist');
    await page.locator('[data-testid="sg-image-upload"]').click();
    await expect(page.locator('[data-testid="sg-image-preview"]')).toBeVisible();

    await page.locator('[data-testid="sg-save"]').click();

    await expect(page).toHaveURL(/\/content\/size-guides\/3$/);
    await expect(page.locator('[data-testid="sg-removed-intro"]')).toContainText('<script>');
    await expect(page.locator('[data-testid="sg-cell-1-1"]')).toHaveValue('30');
    await expect(page.locator('[data-testid="sg-image-preview"]')).toContainText('Where to measure your waist');
  });

  test('puts a blank column heading refusal under the table', async ({ page }) => {
    await openGuide(page, 1);

    await page.locator('[data-testid="sg-add-col"]').click();
    await page.locator('[data-testid="sg-save"]').click();

    await expect(page.locator('[data-testid="sg-error-tableJson"]')).toContainText('heading');
  });

  test('says when someone else changed the guide', async ({ page }) => {
    await openGuide(page, 1);

    await stubSwitch(page, '/__test__/content-bump?kind=size-guide&id=1');
    await page.locator('[data-testid="sg-save"]').click();

    await expect(page.locator('[data-testid="sg-problem"]')).toContainText('Someone else changed this size guide');
    await page.locator('[data-testid="sg-reload"]').click();
    await expect(page.locator('[data-testid="sg-problem"]')).toHaveCount(0);
  });

  test('shows the refusal to a role without Content.View', async ({ page }) => {
    await ownState(page, 'sg-deny');
    await stubSwitch(page, '/__test__/content-permission?deny=view');
    await page.goto(`${adminURL}/content/size-guides`);

    await expect(page.locator('[data-testid="sg-problem"]')).toContainText('does not include size guides');
    await expect(page.locator('[data-testid="sg-add"]')).toHaveCount(0);
  });

  test('both screens start with their h1 and skip no heading level', async ({ page }) => {
    await openList(page);
    await expectHeadingOrder(page);
    await page.goto(`${adminURL}/content/size-guides/1`);
    await expect(page.locator('[data-testid="sg-name"]')).not.toHaveValue('');
    await expectHeadingOrder(page);
  });

  test('the list has no axe violations', async ({ page }) => {
    await openList(page);
    await expectNoAxeViolations(page);
  });

  test('the editor has no axe violations', async ({ page }) => {
    await openGuide(page, 1);
    await expectNoAxeViolations(page);
  });

  test('the list fits at every width', async ({ page }) => {
    await openList(page);
    await expectFitsEveryWidth(page, ['[data-testid="sg-add"]', '[data-testid="sg-open-1"]', '[data-testid="sg-remove-1"]']);
  });

  test('the editor fits at every width, the wide table scrolling inside its own region', async ({ page }) => {
    await openGuide(page, 1);
    for (let index = 0; index < 6; index += 1) {
      await page.locator('[data-testid="sg-add-col"]').click();
    }

    await expectFitsEveryWidth(page, [
      '[data-testid="sg-name"]',
      '[data-testid="sg-intro"]',
      '[data-testid="sg-col-0"]',
      '[data-testid="sg-cell-0-0"]',
      '[data-testid="sg-remove-row-0"]',
      '[data-testid="sg-add-col"]',
      '[data-testid="sg-add-row"]',
      '[data-testid="sg-tips"]',
      '[data-testid="sg-image-upload"]',
      '[data-testid="sg-save"]',
    ]);
  });
});
