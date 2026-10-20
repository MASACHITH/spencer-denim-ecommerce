import { Page, expect, test } from '@playwright/test';

import { adminURL } from '../playwright.config';
import {
  expectFitsEveryWidth,
  expectHeadingOrder,
  expectNoAxeViolations,
  ownState,
  stubSwitch,
  trustOnlyThisWorktree,
} from './support/admin-content-e2e';

/**
 * #192 / GLB-03: the announcement bar screen, against `e2e/support/admin-content-stub.mjs`.
 */
trustOnlyThisWorktree();

/**
 * A wall-clock time relative to today in the shop's zone (UTC+05:30), never a calendar date, so the
 * test does not change its answer as the calendar moves (the `shopMidnight` rule).
 */
function shopMidnight(days: number): string {
  const shopToday = new Date(Date.now() + 330 * 60 * 1000).toISOString().slice(0, 10);
  const day = new Date(Date.parse(`${shopToday}T00:00:00Z`) + days * 24 * 60 * 60 * 1000);
  return `${day.toISOString().slice(0, 10)}T00:00`;
}

async function open(page: Page): Promise<void> {
  await ownState(page, 'ann');
  await page.goto(`${adminURL}/content/announcements`);
  await expect(page.getByRole('heading', { level: 1, name: 'Announcement bar' })).toBeVisible();
  await expect(page.locator('[data-testid="ann-1"]')).toBeVisible();
}

test.describe('the announcement bar screen (#192)', () => {
  test('lists the message that is live', async ({ page }) => {
    await open(page);

    await expect(page.locator('[data-testid="ann-1"]')).toContainText('Free delivery');
    await expect(page.locator('[data-testid="ann-status-1"]')).toHaveText('On the shop now');
  });

  test('adds a scheduled message, counting its characters', async ({ page }) => {
    await open(page);

    await page.locator('[data-testid="ann-message"]').fill('Linen week starts soon');
    await expect(page.locator('[data-testid="ann-count"]')).toContainText('22 of 200');
    await page.locator('[data-testid="ann-link"]').fill('/sale');
    await page.locator('[data-testid="ann-starts"]').fill(shopMidnight(7));
    await page.locator('[data-testid="ann-ends"]').fill(shopMidnight(14));
    await page.locator('[data-testid="ann-save"]').click();

    await expect(page.locator('[data-testid="ann-said"]')).toContainText('added');
    const row = page.locator('[data-testid="ann-list"] li', { hasText: 'Linen week starts soon' });
    await expect(row).toContainText('Scheduled');
  });

  test('stops at 200 characters', async ({ page }) => {
    await open(page);

    await page.locator('[data-testid="ann-message"]').fill('x'.repeat(250));
    await expect(page.locator('[data-testid="ann-message"]')).toHaveValue('x'.repeat(200));
    await expect(page.locator('[data-testid="ann-count"]')).toContainText('200 of 200');
  });

  test('puts refused fields against their controls', async ({ page }) => {
    await open(page);

    await page.locator('[data-testid="ann-message"]').fill('Hello');
    await page.locator('[data-testid="ann-link"]').fill('//evil.example');
    await page.locator('[data-testid="ann-starts"]').fill(shopMidnight(14));
    await page.locator('[data-testid="ann-ends"]').fill(shopMidnight(7));
    await page.locator('[data-testid="ann-save"]').click();

    await expect(page.locator('[data-testid="ann-error-linkUrl"]')).toBeVisible();
    await expect(page.locator('[data-testid="ann-error-endsAt"]')).toContainText('after the start');
  });

  test('refuses an empty message', async ({ page }) => {
    await open(page);

    await page.locator('[data-testid="ann-save"]').click();
    await expect(page.locator('[data-testid="ann-error-message"]')).toContainText('Write the message');
  });

  test('edits a message, then removes it on the second press', async ({ page }) => {
    await open(page);

    await page.locator('[data-testid="ann-edit-1"]').click();
    await page.locator('[data-testid="ann-message"]').fill('Free delivery island-wide');
    await page.locator('[data-testid="ann-active"]').uncheck();
    await page.locator('[data-testid="ann-save"]').click();
    await expect(page.locator('[data-testid="ann-1"]')).toContainText('Free delivery island-wide');
    await expect(page.locator('[data-testid="ann-status-1"]')).toHaveText('Off');

    await page.locator('[data-testid="ann-remove-1"]').click();
    await page.locator('[data-testid="ann-remove-1"]').click();
    await expect(page.locator('[data-testid="ann-empty"]')).toBeVisible();
  });

  test('says when someone else changed the message', async ({ page }) => {
    await open(page);

    await page.locator('[data-testid="ann-edit-1"]').click();
    await stubSwitch(page, '/__test__/content-bump?kind=announcement&id=1');
    await page.locator('[data-testid="ann-save"]').click();

    await expect(page.locator('[data-testid="ann-problem"]')).toContainText('Someone else changed this message');
  });

  test('shows the refusal to a role without Content.View', async ({ page }) => {
    await ownState(page, 'ann-deny');
    await stubSwitch(page, '/__test__/content-permission?deny=view');
    await page.goto(`${adminURL}/content/announcements`);

    await expect(page.locator('[data-testid="ann-problem"]')).toContainText('does not include the announcement bar');
    await expect(page.locator('[data-testid="ann-message"]')).toHaveCount(0);
  });

  test('starts with its h1 and skips no heading level', async ({ page }) => {
    await open(page);
    await expectHeadingOrder(page);
  });

  test('has no axe violations', async ({ page }) => {
    await open(page);
    await expectNoAxeViolations(page);
  });

  test('fits at every width with every control reachable', async ({ page }) => {
    await open(page);

    await expectFitsEveryWidth(page, [
      '[data-testid="ann-message"]',
      '[data-testid="ann-link"]',
      '[data-testid="ann-order"]',
      '[data-testid="ann-starts"]',
      '[data-testid="ann-ends"]',
      '[data-testid="ann-active-field"]',
      '[data-testid="ann-save"]',
      '[data-testid="ann-edit-1"]',
      '[data-testid="ann-remove-1"]',
    ]);
  });
});
