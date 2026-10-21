import AxeBuilder from '@axe-core/playwright';
import { Locator, Page, expect, test } from '@playwright/test';

import { megaMenuApiURL, megaMenuURL } from '../playwright.config';
import { horizontalOverflow, undersized } from './support/measure';

/**
 * The mega-menu promo card on the storefront (GLB-05, ADM-17; #215), on a pair of its own whose stub can put admin
 * cards on the departments feed (`/__test__/mega-menu-cards`). Serial, because the switch is process-wide.
 *
 * What is proved here, and only here, is what a browser and a server render can show: the fallback is today's card
 * exactly, an admin card replaces it in the **server-rendered** HTML, every word is escaped there, a hostile link
 * never reaches an `href`, the picture is responsive, desktop hover/focus and the keyboard reach it, the phone's
 * drawer is unchanged, nothing scrolls sideways at the eight widths, and axe finds nothing serious.
 */
test.describe.configure({ mode: 'serial' });
test.use({ baseURL: megaMenuURL });

const BREAKPOINTS = [320, 375, 390, 768, 1024, 1280, 1440, 1920] as const;

type CardState = 'none' | 'empty' | 'card' | 'external' | 'hostile' | 'long' | 'closing';

async function useCards(page: Page, state: CardState): Promise<void> {
  const switched = await page.request.get(`${megaMenuApiURL}/__test__/mega-menu-cards?state=${state}`);
  expect(switched.ok(), 'the stub refused the mega-menu switch').toBe(true);
  expect(((await switched.json()) as { state: string }).state).toBe(state);

  // The browser's own /api calls go to the same stub, so the hydrated page agrees with the server.
  await page.route('**/api/**', async (route) => {
    const url = new URL(route.request().url());
    const response = await route.fetch({ url: `${megaMenuApiURL}${url.pathname}${url.search}` });
    await route.fulfill({ response });
  });
}

/** The server's own HTML for a page, before any script runs. */
async function serverHtml(page: Page, path = '/'): Promise<string> {
  const response = await page.request.get(`${megaMenuURL}${path}`);
  expect(response.ok()).toBe(true);
  return response.text();
}

function department(page: Page, name: string): Locator {
  return page.locator('header').getByRole('link', { name, exact: true }).first();
}

async function openWomen(page: Page): Promise<Locator> {
  await page.setViewportSize({ width: 1280, height: 900 });
  await page.goto('/');
  await department(page, 'Women').hover();
  const menu = page.locator('#mega-women');
  await expect(menu).toBeVisible();
  return menu;
}

test.afterEach(async ({ page, request }) => {
  await page.unrouteAll({ behavior: 'ignoreErrors' });
  await request.get(`${megaMenuApiURL}/__test__/mega-menu-cards?state=none`);
});

test.describe('the fallback: no card means today’s card', () => {
  for (const state of ['none', 'empty'] as const) {
    test(`with ${state === 'none' ? 'no promoCards field' : 'an empty promoCards list'}, every department draws "New in …" to New Arrivals`, async ({ page }) => {
      await useCards(page, state);

      const html = await serverHtml(page);
      for (const name of ['Women', 'Men', 'Kids']) {
        expect(html).toContain(`New in ${name}`);
      }

      const menu = await openWomen(page);
      const promo = menu.locator('.mega__promo');
      await expect(promo).toHaveAttribute('href', '/new-arrivals');
      await expect(promo).toContainText('New in Women');
      await expect(promo).toContainText('Everyday essentials and occasion pieces, cut for Sri Lankan weather.');
      await expect(promo).toContainText('Shop now');
      // The stub's departments have no picture, so the frame shows its neutral placeholder.
      await expect(promo).toContainText('Promo image');
      await expect(promo).not.toHaveAttribute('target', /.+/);
    });
  }
});

test.describe('an admin card', () => {
  test('replaces the fallback in the server-rendered HTML, with its picture, alt text and responsive sources', async ({ page }) => {
    await useCards(page, 'card');

    const html = await serverHtml(page);
    expect(html).toContain('Linen days');
    expect(html).toContain('Breathable pieces for the heat.');
    expect(html).toContain('Shop linen');
    expect(html).toContain('alt="A model in a linen dress"');
    expect(html).not.toContain('New in Women');
    // Men and Kids have no card, so they keep theirs.
    expect(html).toContain('New in Men');
    expect(html).toContain('New in Kids');

    const menu = await openWomen(page);
    const promo = menu.locator('.mega__promo');
    await expect(promo).toHaveAttribute('href', '/women/dresses?material=linen');
    const image = promo.locator('img');
    await expect(image).toHaveAttribute('alt', 'A model in a linen dress');
    await expect(image).toHaveAttribute('srcset', /600w, .* 1200w/);
    await expect(image).toHaveAttribute('sizes', /1440px/);
    // The browser chose one of the card's own renditions and it was served as a picture. (The stub's renditions
    // are a placeholder pixel that does not decode, so `naturalWidth` says nothing here.)
    const chosen = await image.evaluate((element: HTMLImageElement) => element.currentSrc);
    expect(chosen).toMatch(/\/media\/public\/renditions\/[0-9a-f-]+\/(card|large)\.webp$/);
    const served = await page.request.get(chosen);
    expect(served.status()).toBe(200);
    expect(served.headers()['content-type']).toBe('image/webp');

    await promo.click();
    await expect(page).toHaveURL(/\/women\/dresses\?material=linen$/);
    await expect(page.locator('#mega-women')).toBeHidden();
  });

  test('is reached from the keyboard: focus opens the menu and Tab arrives at the card', async ({ page }) => {
    await useCards(page, 'card');
    await page.setViewportSize({ width: 1280, height: 900 });
    await page.goto('/');

    await department(page, 'Women').focus();
    await expect(page.locator('#mega-women')).toBeVisible();

    const promo = page.locator('#mega-women').locator('.mega__promo');
    let reached = false;
    for (let step = 0; step < 40 && !reached; step += 1) {
      await page.keyboard.press('Tab');
      reached = await promo.evaluate((element) => element === document.activeElement);
    }
    expect(reached, 'Tab never reached the promo card').toBe(true);
    await expect(page.locator('#mega-women')).toBeVisible();
    expect(await undersized(page, '#mega-women .mega__promo .mega__promo-cta')).toEqual([]);

    await page.keyboard.press('Enter');
    await expect(page).toHaveURL(/\/women\/dresses\?material=linen$/);
  });

  test('pointing off the site is an ordinary anchor that opens in a new tab', async ({ page }) => {
    await useCards(page, 'external');

    const promo = (await openWomen(page)).locator('.mega__promo');
    await expect(promo).toHaveAttribute('href', 'https://instagram.com/colombowardrobe');
    await expect(promo).toHaveAttribute('target', '_blank');
    await expect(promo).toHaveAttribute('rel', 'noopener noreferrer');
    await expect(promo).toContainText('See it on Instagram');
  });

  test('is escaped in the server HTML and in the browser, and a javascript: link is skipped', async ({ page }) => {
    await useCards(page, 'hostile');

    // The raw strings also travel in the transfer-state JSON, which Angular escapes for a script element; what
    // must never appear is any of them as live markup or a live link.
    const html = await serverHtml(page);
    expect(html).not.toContain('<script>window.__xss');
    expect(html).not.toContain('<img src=x onerror');
    expect(html).not.toContain('<svg onload');
    expect(html).not.toMatch(/href="\s*javascript:/i);
    expect(html).toContain('&lt;img src=x onerror=');
    expect(html).toContain('Linen &lt;b&gt;days&lt;/b&gt;');
    expect(html).toContain('&lt;/span&gt;&lt;script&gt;window.__xss=2&lt;/script&gt;');
    expect(html).toContain('{{constructor.constructor(');

    const menu = await openWomen(page);
    const promos = menu.locator('.mega__promo');
    await expect(promos).toHaveCount(1);
    await expect(promos).toHaveAttribute('href', '/women/dresses');
    await expect(promos).toContainText('<img src=x onerror="window.__xss=1">Linen <b>days</b>');
    await expect(promos.locator('b, script, svg[onload], img[src="x"]')).toHaveCount(0);
    expect(await page.evaluate(() => (window as unknown as { __xss?: number }).__xss)).toBeUndefined();
    expect(await page.locator('a[href^="javascript:"]').count()).toBe(0);
  });

  test('has no serious or critical axe violation with the menu open', async ({ page }) => {
    await useCards(page, 'card');
    await openWomen(page);

    const results = await new AxeBuilder({ page })
      .withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa'])
      .analyze();
    const serious = results.violations.filter((violation) => violation.impact === 'serious' || violation.impact === 'critical');
    expect(serious, JSON.stringify(serious, null, 2)).toEqual([]);
  });
});

test.describe('a card whose window closes while the page is open (#215 tester)', () => {
  test('is dropped the next time the menu opens, without a page load', async ({ page }) => {
    await useCards(page, 'closing');

    const menu = await openWomen(page);
    await expect(menu.locator('.mega__promo')).toContainText('Closing soon');

    await page.mouse.move(0, 800);
    await expect(menu).toBeHidden();
    await page.waitForTimeout(4_500);

    await department(page, 'Women').hover();
    await expect(menu).toBeVisible();
    await expect(menu.locator('.mega__promo')).toContainText('New in Women');
    await expect(menu.locator('.mega__promo')).toHaveAttribute('href', '/new-arrivals');
  });
});

test.describe('the longest card the API allows', () => {
  test('never scrolls the page sideways, menu open on desktop and drawer open on phones', async ({ page }) => {
    await useCards(page, 'long');
    await page.goto('/');

    for (const width of BREAKPOINTS) {
      await page.setViewportSize({ width, height: 900 });

      if (width >= 1024) {
        await department(page, 'Women').hover();
        await expect(page.locator('#mega-women')).toBeVisible();
        await expect(page.locator('#mega-women').locator('.mega__promo')).toContainText('L'.repeat(100));
      } else {
        await expect(page.locator('#mega-women')).toBeHidden();
      }

      expect(await horizontalOverflow(page), `horizontal overflow at ${width}px`).toBe(0);
      await page.mouse.move(0, 800);
    }
  });

  test('the phone drawer is unchanged: departments expand to their categories, with no card in it', async ({ page }) => {
    await useCards(page, 'card');
    await page.setViewportSize({ width: 390, height: 844 });
    await page.goto('/');

    await page.locator('[data-testid="menu-toggle"]').click();
    const drawer = page.locator('#storefront-menu');
    await expect(drawer).toBeVisible();
    await expect(drawer).not.toContainText('Linen days');
    await expect(drawer.locator('.mega__promo')).toHaveCount(0);

    await drawer.getByRole('button', { name: /Women/ }).first().click();
    await expect(drawer.getByRole('link', { name: 'Dresses', exact: true })).toBeVisible();
    expect(await horizontalOverflow(page)).toBe(0);
  });
});
