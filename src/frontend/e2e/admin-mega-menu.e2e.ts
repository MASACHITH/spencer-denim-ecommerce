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
 * #215 / GLB-05: the mega-menu card screen, against `e2e/support/admin-content-stub.mjs`. The stub starts as a
 * fresh database does: one switched-off card per department in today's words.
 */
trustOnlyThisWorktree();

/** A wall-clock time relative to today in the shop's zone (UTC+05:30), never a calendar date. */
function shopMidnight(days: number): string {
  const shopToday = new Date(Date.now() + 330 * 60 * 1000).toISOString().slice(0, 10);
  const day = new Date(Date.parse(`${shopToday}T00:00:00Z`) + days * 24 * 60 * 60 * 1000);
  return `${day.toISOString().slice(0, 10)}T00:00`;
}

async function open(page: Page): Promise<void> {
  await ownState(page, 'mm');
  await page.goto(`${adminURL}/content/mega-menu`);
  await expect(page.getByRole('heading', { level: 1, name: 'Mega menu cards' })).toBeVisible();
  await expect(page.locator('[data-testid="mm-card-1"]')).toBeVisible();
}

async function uploadPicture(page: Page): Promise<void> {
  await page.locator('[data-testid="mm-image-file"]').setInputFiles(fakeFile('linen.webp', 'image/webp'));
  await page.locator('[data-testid="mm-image-alt"]').fill('A model in a linen dress');
  await page.locator('[data-testid="mm-image-upload"]').click();
  await expect(page.locator('[data-testid="mm-image-preview"]')).toContainText('A model in a linen dress');
}

test.describe('the mega-menu card screen (#215)', () => {
  test('a fresh shop lists today’s card for each department, switched off, and says the menu keeps its fallback', async ({ page }) => {
    await open(page);

    for (const [id, name, slug] of [[1, 'Women', 'women'], [2, 'Men', 'men'], [3, 'Kids', 'kids']] as const) {
      await expect(page.locator(`[data-testid="mm-card-${id}"]`)).toContainText(`New in ${name}`);
      await expect(page.locator(`[data-testid="mm-status-${id}"]`)).toHaveText('Off');
      await expect(page.locator(`[data-testid="mm-fallback-${slug}"]`)).toContainText(`New in ${name}`);
    }
  });

  test('a card cannot go on without a picture; with one it goes on the menu', async ({ page }) => {
    await open(page);

    await page.locator('[data-testid="mm-edit-1"]').click();
    await expect(page.locator('[data-testid="mm-department"]')).toBeFocused();
    await page.locator('[data-testid="mm-active"]').check();
    await page.locator('[data-testid="mm-save"]').click();
    await expect(page.locator('[data-testid="mm-image-error"]')).toContainText('needs an image');

    await uploadPicture(page);
    await page.locator('[data-testid="mm-title"]').fill('Linen days');
    await page.locator('[data-testid="mm-link"]').fill('/women/dresses?material=linen');
    await page.locator('[data-testid="mm-save"]').click();

    await expect(page.locator('[data-testid="mm-said"]')).toContainText('saved');
    await expect(page.locator('[data-testid="mm-card-1"]')).toContainText('Linen days');
    await expect(page.locator('[data-testid="mm-status-1"]')).toHaveText('On the menu now');
    await expect(page.locator('[data-testid="mm-fallback-women"]')).toHaveCount(0);
  });

  test('the status pill stays inside its card at every width (#215 tester: 7 px over at 390)', async ({ page }) => {
    await open(page);
    await page.locator('[data-testid="mm-edit-1"]').click();
    await uploadPicture(page);
    await page.locator('[data-testid="mm-active"]').check();
    await page.locator('[data-testid="mm-save"]').click();
    await expect(page.locator('[data-testid="mm-status-1"]')).toHaveText('On the menu now');

    for (const width of [320, 375, 390, 768]) {
      await page.setViewportSize({ width, height: 900 });
      const pill = await page.locator('[data-testid="mm-status-1"]').boundingBox();
      const card = await page.locator('[data-testid="mm-card-1"]').boundingBox();
      expect(pill!.x + pill!.width, `pill right edge at ${width}px`).toBeLessThanOrEqual(card!.x + card!.width);
    }
  });

  test('adds a scheduled card for a department, starting from today’s words', async ({ page }) => {
    await open(page);

    await page.locator('[data-testid="mm-add-men"]').click();
    await expect(page.locator('[data-testid="mm-title"]')).toHaveValue('New in Men');
    await expect(page.locator('[data-testid="mm-link"]')).toHaveValue('/new-arrivals');

    await page.locator('[data-testid="mm-title"]').fill('Monsoon shirts');
    await uploadPicture(page);
    await page.locator('[data-testid="mm-starts"]').fill(shopMidnight(7));
    await page.locator('[data-testid="mm-ends"]').fill(shopMidnight(14));
    await page.locator('[data-testid="mm-save"]').click();

    await expect(page.locator('[data-testid="mm-said"]')).toContainText('added');
    const card = page.locator('[data-testid="mm-dept-men"] li', { hasText: 'Monsoon shirts' });
    await expect(card).toContainText('Scheduled');
  });

  test('puts refused fields against their controls and keeps the form', async ({ page }) => {
    await open(page);

    await page.locator('[data-testid="mm-edit-2"]').click();
    await page.locator('[data-testid="mm-link"]').fill('javascript:alert(1)');
    await page.locator('[data-testid="mm-title"]').fill('');
    await page.locator('[data-testid="mm-starts"]').fill(shopMidnight(14));
    await page.locator('[data-testid="mm-ends"]').fill(shopMidnight(7));
    await page.locator('[data-testid="mm-save"]').click();

    await expect(page.locator('[data-testid="mm-error-linkUrl"]')).toBeVisible();
    await expect(page.locator('[data-testid="mm-error-title"]')).toContainText('required');
    await expect(page.locator('[data-testid="mm-error-endsAt"]')).toContainText('after the start');
    await expect(page.locator('[data-testid="mm-link"]')).toHaveValue('javascript:alert(1)');
  });

  test('stops the boxes at the server’s widths', async ({ page }) => {
    await open(page);

    await page.locator('[data-testid="mm-title"]').fill('t'.repeat(150));
    await expect(page.locator('[data-testid="mm-title"]')).toHaveValue('t'.repeat(100));
    await page.locator('[data-testid="mm-button"]').fill('b'.repeat(60));
    await expect(page.locator('[data-testid="mm-button"]')).toHaveValue('b'.repeat(40));
  });

  test('a department holds six cards, and then the Add button goes', async ({ page }) => {
    await open(page);

    for (let count = 1; count < 6; count += 1) {
      await page.locator('[data-testid="mm-add-kids"]').click();
      await page.locator('[data-testid="mm-active"]').uncheck();
      await page.locator('[data-testid="mm-title"]').fill(`Kids card ${count}`);
      await page.locator('[data-testid="mm-save"]').click();
      await expect(page.locator('[data-testid="mm-said"]')).toContainText('added');
    }

    await expect(page.locator('[data-testid="mm-add-kids"]')).toHaveCount(0);
    await expect(page.locator('[data-testid="mm-dept-kids"]')).toContainText('the most it can');
  });

  test('says when someone else changed the card, and reloads', async ({ page }) => {
    await open(page);

    await page.locator('[data-testid="mm-edit-3"]').click();
    await stubSwitch(page, '/__test__/content-bump?kind=card&id=3');
    await page.locator('[data-testid="mm-save"]').click();

    await expect(page.locator('[data-testid="mm-problem"]')).toContainText('Someone else changed this card');
    await page.locator('[data-testid="mm-reload"]').click();
    await expect(page.locator('[data-testid="mm-problem"]')).toHaveCount(0);
  });

  test('removes a card on the second press, from the keyboard', async ({ page }) => {
    await open(page);

    await page.locator('[data-testid="mm-remove-2"]').focus();
    await page.keyboard.press('Enter');
    await expect(page.locator('[data-testid="mm-keep-2"]')).toBeVisible();
    await page.keyboard.press('Enter');

    await expect(page.locator('[data-testid="mm-said"]')).toContainText('removed');
    await expect(page.locator('[data-testid="mm-card-2"]')).toHaveCount(0);
    await expect(page.locator('[data-testid="mm-empty-men"]')).toBeVisible();
  });

  test('shows the refusal to a role without Content.View', async ({ page }) => {
    await ownState(page, 'mm-deny');
    await stubSwitch(page, '/__test__/content-permission?deny=view');
    await page.goto(`${adminURL}/content/mega-menu`);

    await expect(page.locator('[data-testid="mm-problem"]')).toContainText('does not include the mega menu cards');
    await expect(page.locator('[data-testid="mm-title"]')).toHaveCount(0);
  });

  test('a role that may read but not write is told so on save', async ({ page }) => {
    await open(page);
    await stubSwitch(page, '/__test__/content-permission?deny=write');

    await page.locator('[data-testid="mm-edit-1"]').click();
    await page.locator('[data-testid="mm-save"]').click();
    await expect(page.locator('[data-testid="mm-problem"]')).toContainText('does not allow this');
  });

  test('is reachable from the dashboard', async ({ page }) => {
    await ownState(page, 'mm-dash');
    await page.goto(`${adminURL}/`);
    await page.locator('[data-testid="dashboard-mega-menu-link"]').click();
    await expect(page.getByRole('heading', { level: 1, name: 'Mega menu cards' })).toBeVisible();
  });

  test('starts with its h1 and skips no heading level', async ({ page }) => {
    await open(page);
    await expectHeadingOrder(page);
  });

  test('has no axe violations, empty or with a card on', async ({ page }) => {
    await open(page);
    await expectNoAxeViolations(page);

    await page.locator('[data-testid="mm-edit-1"]').click();
    await uploadPicture(page);
    await page.locator('[data-testid="mm-active"]').check();
    await page.locator('[data-testid="mm-save"]').click();
    await expect(page.locator('[data-testid="mm-status-1"]')).toHaveText('On the menu now');
    await expectNoAxeViolations(page);
  });

  test('fits at every width with every control reachable', async ({ page }) => {
    await open(page);

    await expectFitsEveryWidth(page, [
      '[data-testid="mm-department"]',
      '[data-testid="mm-title"]',
      '[data-testid="mm-subtitle"]',
      '[data-testid="mm-button"]',
      '[data-testid="mm-link"]',
      '[data-testid="mm-image-upload"]',
      '[data-testid="mm-order"]',
      '[data-testid="mm-starts"]',
      '[data-testid="mm-ends"]',
      '[data-testid="mm-active-field"]',
      '[data-testid="mm-save"]',
      '[data-testid="mm-edit-1"]',
      '[data-testid="mm-remove-1"]',
      '[data-testid="mm-add-women"]',
    ]);
  });
});
