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
 * #192 / ADM-17: the Homepage screen, against `e2e/support/admin-content-stub.mjs`.
 *
 * Playwright never starts the .NET API, so what this proves is the screen against a stub that holds the
 * server's rules (whole-form bodies, the block's field list, link rules, the section row version): that
 * a save sends what the server accepts, that its refusals land where the staff member is looking, and the
 * layout and accessibility checks no jsdom spec can make.
 */
trustOnlyThisWorktree();

async function open(page: Page): Promise<void> {
  await ownState(page, 'home');
  await page.goto(`${adminURL}/content/home`);
  await expect(page.getByRole('heading', { level: 1, name: 'Homepage' })).toBeVisible();
  await expect(page.locator('[data-testid="section-Newsletter"]')).toBeVisible();
}

test.describe('the Homepage screen (#192)', () => {
  test('shows the ten blocks in the proposal order, with what is live', async ({ page }) => {
    await open(page);

    const order = await page
      .locator('[data-testid^="section-"]')
      .evaluateAll((nodes) => nodes.map((node) => node.getAttribute('data-testid')));
    expect(order).toEqual([
      'section-HeroVideo',
      'section-ShopByDepartment',
      'section-NewArrivals',
      'section-TrendingItems',
      'section-CampaignBanner',
      'section-ActivewearFeature',
      'section-SaleSection',
      'section-SocialGallery',
      'section-Benefits',
      'section-Newsletter',
    ]);
    await expect(page.locator('[data-testid="HeroVideo-status"]')).toHaveText('On the shop now');
    await expect(page.locator('[data-testid="Newsletter-status"]')).toHaveText('Off');
    await expect(page.locator('[data-testid="ShopByDepartment-items"]')).toContainText('Women');
  });

  test('saves a block, and the change is there after a reload', async ({ page }) => {
    await open(page);

    await page.locator('[data-testid="HeroVideo-title"]').fill('Summer linen');
    await page.locator('[data-testid="HeroVideo-overlay"]').selectOption('light');
    await page.locator('[data-testid="HeroVideo-save"]').click();
    await expect(page.locator('[data-testid="HeroVideo-said"]')).toContainText('is saved');

    await page.reload();
    await expect(page.locator('[data-testid="HeroVideo-title"]')).toHaveValue('Summer linen');
    await expect(page.locator('[data-testid="HeroVideo-overlay"]')).toHaveValue('light');
  });

  test('puts a refused link against its own control', async ({ page }) => {
    await open(page);

    await page.locator('[data-testid="NewArrivals-listingUrl"]').fill('https://elsewhere.example/new');
    await page.locator('[data-testid="NewArrivals-save"]').click();

    await expect(page.locator('[data-testid="NewArrivals-error-listingUrl"]')).toContainText('starting with /');
    await expect(page.locator('[data-testid="NewArrivals-listingUrl"]')).toHaveAttribute('aria-invalid', 'true');
  });

  test('switches trending items to hand-picked and adds an item naming a product', async ({ page }) => {
    await open(page);

    await page.locator('[data-testid="TrendingItems-mode"]').selectOption('Manual');
    await page.locator('[data-testid="TrendingItems-save"]').click();
    await expect(page.locator('[data-testid="TrendingItems-said"]')).toContainText('is saved');

    await page.locator('[data-testid="TrendingItems-add-item"]').click();
    await page.locator('[data-testid="TrendingItems-item-productSlug"]').fill('not-a-product');
    await page.locator('[data-testid="TrendingItems-item-save"]').click();
    await expect(page.locator('[data-testid="TrendingItems-item-error-title"]')).toContainText('Required');
    await expect(page.locator('[data-testid="TrendingItems-item-error-productSlug"]')).toContainText('No product');

    await page.locator('[data-testid="TrendingItems-item-title"]').fill('The wrap dress');
    await page.locator('[data-testid="TrendingItems-item-productSlug"]').fill('wrap-dress');
    await page.locator('[data-testid="TrendingItems-item-save"]').click();
    await expect(page.locator('[data-testid="TrendingItems-items"]')).toContainText('The wrap dress');
  });

  test('adds a benefit with an icon, then removes it on the second press', async ({ page }) => {
    await open(page);

    await page.locator('[data-testid="Benefits-add-item"]').click();
    await page.locator('[data-testid="Benefits-item-title"]').fill('Easy returns');
    await page.locator('[data-testid="Benefits-item-icon"]').selectOption('returns');
    await page.locator('[data-testid="Benefits-item-save"]').click();

    const list = page.locator('[data-testid="Benefits-items"]');
    await expect(list).toContainText('Easy returns');

    const added = list.locator('li', { hasText: 'Easy returns' });
    const remove = added.locator('[data-testid^="Benefits-item-remove-"]');
    await remove.click();
    await expect(remove).toContainText('Yes, remove it');
    await remove.click();
    await expect(list).not.toContainText('Easy returns');
  });

  test('uploads an image for the social gallery, with an outside link it allows', async ({ page }) => {
    await open(page);

    await page.locator('[data-testid="SocialGallery-add-item"]').click();
    await page.locator('[data-testid="SocialGallery-item-linkUrl"]').fill('https://instagram.com/colombowardrobe');
    await page.locator('[data-testid="SocialGallery-item-save"]').click();
    await expect(page.locator('[data-testid="SocialGallery-item-image-error"]')).toContainText('Required');

    await page.locator('[data-testid="SocialGallery-item-image-file"]').setInputFiles(fakeFile('look.webp', 'image/webp'));
    await page.locator('[data-testid="SocialGallery-item-image-upload"]').click();
    await expect(page.locator('[data-testid="SocialGallery-item-image-upload-error"]')).toContainText('Describe it');

    await page.locator('[data-testid="SocialGallery-item-image-alt"]').fill('A customer in a linen shirt');
    await page.locator('[data-testid="SocialGallery-item-image-upload"]').click();
    await expect(page.locator('[data-testid="SocialGallery-item-image-preview"]')).toContainText('A customer in a linen shirt');

    await page.locator('[data-testid="SocialGallery-item-save"]').click();
    await expect(page.locator('[data-testid="SocialGallery-items"]')).toContainText('A customer in a linen shirt');
  });

  test('adds a hero video, and refuses one with no still image', async ({ page }) => {
    await open(page);

    await page.locator('[data-testid="HeroVideo-item-edit-1"]').click();
    await page.locator('[data-testid="HeroVideo-item-video-file"]').setInputFiles(fakeFile('clip.mp4', 'video/mp4'));
    await page.locator('[data-testid="HeroVideo-item-video-poster"]').setInputFiles(fakeFile('poster.webp', 'image/webp'));
    await page.locator('[data-testid="HeroVideo-item-video-alt"]').fill('Linen in the wind');
    await page.locator('[data-testid="HeroVideo-item-video-upload"]').click();
    await expect(page.locator('[data-testid="HeroVideo-item-video-preview"]')).toBeVisible();

    await page.locator('[data-testid="HeroVideo-item-image-remove"]').click();
    await page.locator('[data-testid="HeroVideo-item-save"]').click();
    await expect(page.locator('[data-testid="HeroVideo-item-image-error"]')).toContainText('still image');

    await page.locator('[data-testid="HeroVideo-item-cancel"]').click();
    await page.locator('[data-testid="HeroVideo-item-edit-1"]').click();
    await page.locator('[data-testid="HeroVideo-item-video-file"]').setInputFiles(fakeFile('clip.mp4', 'video/mp4'));
    await page.locator('[data-testid="HeroVideo-item-video-poster"]').setInputFiles(fakeFile('poster.webp', 'image/webp'));
    await page.locator('[data-testid="HeroVideo-item-video-alt"]').fill('Linen in the wind');
    await page.locator('[data-testid="HeroVideo-item-video-upload"]').click();
    await page.locator('[data-testid="HeroVideo-item-save"]').click();
    await expect(page.locator('[data-testid="HeroVideo-said"]')).toContainText('item is saved');
  });

  test('says when someone else changed the block, and reloads', async ({ page }) => {
    await open(page);

    await stubSwitch(page, '/__test__/content-bump?kind=section&id=9');
    await page.locator('[data-testid="Benefits-title"]').fill('Mine');
    await page.locator('[data-testid="Benefits-save"]').click();

    await expect(page.locator('[data-testid="Benefits-problem"]')).toContainText('Someone else changed this block');
    await page.locator('[data-testid="Benefits-reload"]').click();
    await expect(page.locator('[data-testid="Benefits-problem"]')).toHaveCount(0);
  });

  test('shows the refusal to a role without Content.View', async ({ page }) => {
    await ownState(page, 'home-deny');
    await stubSwitch(page, '/__test__/content-permission?deny=view');
    await page.goto(`${adminURL}/content/home`);

    await expect(page.locator('[data-testid="home-problem"]')).toContainText('does not include the homepage');
    await expect(page.locator('[data-testid^="section-"]')).toHaveCount(0);
  });

  test('says a role that can look but not change is refused on save', async ({ page }) => {
    await open(page);
    await stubSwitch(page, '/__test__/content-permission?deny=write');

    await page.locator('[data-testid="Benefits-save"]').click();
    await expect(page.locator('[data-testid="Benefits-problem"]')).toContainText('Content permission');
  });

  test('starts with its h1 and skips no heading level', async ({ page }) => {
    await open(page);
    await page.locator('[data-testid="Benefits-add-item"]').click();
    await expectHeadingOrder(page);
  });

  test('has no axe violations, with an item form open', async ({ page }) => {
    await open(page);
    await page.locator('[data-testid="HeroVideo-item-edit-1"]').click();
    await expectNoAxeViolations(page);
  });

  test('fits at every width with every control reachable', async ({ page }) => {
    await open(page);
    await page.locator('[data-testid="HeroVideo-item-edit-1"]').click();

    await expectFitsEveryWidth(page, [
      '[data-testid="HeroVideo-title"]',
      '[data-testid="HeroVideo-overlay"]',
      '[data-testid="HeroVideo-startsAt"]',
      '[data-testid="HeroVideo-enabled-field"]',
      '[data-testid="HeroVideo-save"]',
      '[data-testid="HeroVideo-item-edit-1"]',
      '[data-testid="HeroVideo-item-title"]',
      '[data-testid="HeroVideo-item-image-upload"]',
      '[data-testid="HeroVideo-item-image-remove"]',
      '[data-testid="HeroVideo-item-video-poster"]',
      '[data-testid="HeroVideo-item-save"]',
      '[data-testid="TrendingItems-mode"]',
      '[data-testid="NewArrivals-listingUrl"]',
      '[data-testid="Benefits-item-remove-3"]',
      '[data-testid="Benefits-add-item"]',
    ]);
  });
});
