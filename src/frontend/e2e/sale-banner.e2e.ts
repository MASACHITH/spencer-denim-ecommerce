import AxeBuilder from '@axe-core/playwright';
import { Page, expect, test } from '@playwright/test';

import { contentPagesApiURL, contentPagesURL } from '../playwright.config';
import { horizontalOverflow } from './support/measure';

/**
 * #191: the live sale campaign's banner at the top of the Sale page (DEPT-03).
 *
 * **Where it goes, and why there.** Neither docs/04 nor doc 11 places a sale campaign's banner: the
 * homepage's "Campaign banner" block (HOME-05) is staff-managed homepage content with its own image, not
 * a sale campaign's, and doc 08 ties `SaleCampaigns` to the Sale page and the Sale section. So it takes
 * the safest placement — the Sale page's own header slot, the full-width promo slot a department or
 * category uses for its artwork (PLP-01) — and is recorded as an owner decision in the #191 journal.
 *
 * **These run on the content-pages pair of servers, serially.** The banner is switched on the stub
 * (`/__test__/sale-banner`), and the storefront reads it during the server render, which Playwright's
 * request interception cannot reach — so the switch has to live in the stub, and a stub switch on the
 * shared pair would put a banner on every other suite's Sale page. The content-pages suite shares this
 * pair but never visits the Sale page, and this switch touches nothing it reads.
 */
test.describe.configure({ mode: 'serial' });

test.use({ baseURL: contentPagesURL });

/** docs/04 §4 and the project's non-negotiable list. */
const BREAKPOINTS = [320, 375, 390, 768, 1024, 1280, 1440, 1920] as const;

const LIVE_ALT = 'Linen Week: 20% off shirts and blouses';

async function banner(page: Page, state: 'none' | 'live' | 'no-alt' | 'fail' | 'slow' | 'hostile'): Promise<void> {
  const response = await page.request.get(`${contentPagesApiURL}/__test__/sale-banner?state=${state}`);
  expect(response.ok(), 'the sale-banner switch did not answer').toBe(true);
}

/** Sends the browser's own `/api` calls to the same stub, as `content-pages.e2e.ts` does. */
async function useStubApi(page: Page): Promise<void> {
  await page.route('**/api/**', async (route) => {
    const url = new URL(route.request().url());
    const response = await route.fetch({ url: `${contentPagesApiURL}${url.pathname}${url.search}` });
    await route.fulfill({ response });
  });
}

test.afterEach(async ({ page }) => {
  await banner(page, 'none');
});

test.describe('the Sale page banner (#191)', () => {
  test('is in the server-rendered HTML, with its description and its candidate widths', async ({ page }) => {
    await banner(page, 'live');

    const response = await page.request.get('/sale');
    expect(response.ok()).toBe(true);

    const html = await response.text();

    expect(html).toContain('data-testid="sale-banner"');
    expect(html).toContain(`alt="${LIVE_ALT}"`);
    expect(html).toContain('1200w');
  });

  test('heads the Sale page, between the heading and the product count', async ({ page }) => {
    await banner(page, 'live');
    await useStubApi(page);

    // The browser must not ask again: the server handed the banner over with the page.
    const asked: string[] = [];
    page.on('request', (request) => {
      if (request.url().includes('/api/content/sale-banner')) {
        asked.push(request.url());
      }
    });

    await page.goto('/sale');

    const image = page.locator('[data-testid="sale-banner"] img');

    await expect(image).toBeVisible();
    await expect(image).toHaveAttribute('alt', LIVE_ALT);
    await expect(image).toHaveAttribute('sizes', /100vw/);
    await expect(image).toHaveAttribute('srcset', /600w.*1200w.*1600w/);
    await expect(image).toHaveAttribute('width', '1600');
    await expect(image).toHaveAttribute('height', '500');
    await expect(image).toHaveAttribute('fetchpriority', 'high');

    const order = await page
      .locator('h1, [data-testid="sale-banner"], .plp__bar')
      .evaluateAll((nodes) =>
        nodes.map((node) =>
          node.tagName === 'H1' ? 'h1' : node.classList.contains('plp__bar') ? 'bar' : 'banner',
        ),
      );
    expect(order).toEqual(['h1', 'banner', 'bar']);

    // Products are still there underneath it.
    await expect(page.locator('.product-grid > li').first()).toBeVisible();
    expect(asked).toEqual([]);
  });

  test('is not drawn when no live campaign has a banner', async ({ page }) => {
    await banner(page, 'none');
    await useStubApi(page);

    await page.goto('/sale');

    await expect(page.locator('h1')).toHaveText('Sale');
    await expect(page.locator('.product-grid > li').first()).toBeVisible();
    await expect(page.locator('[data-testid="sale-banner"]')).toHaveCount(0);
  });

  test('is decorative when its picture has no description', async ({ page }) => {
    await banner(page, 'no-alt');
    await useStubApi(page);

    await page.goto('/sale');

    const image = page.locator('[data-testid="sale-banner"] img');
    await expect(image).toHaveAttribute('alt', '');
    await expect(image).not.toHaveAttribute('srcset', /./);
  });

  test('leaves the Sale page whole when the banner cannot be read', async ({ page }) => {
    await banner(page, 'fail');
    await useStubApi(page);

    const response = await page.goto('/sale');

    expect(response?.status()).toBe(200);
    await expect(page.locator('h1')).toHaveText('Sale');
    await expect(page.locator('.product-grid > li').first()).toBeVisible();
    await expect(page.locator('[data-testid="sale-banner"]')).toHaveCount(0);
  });

  test('belongs to the Sale page only', async ({ page }) => {
    await banner(page, 'live');
    await useStubApi(page);

    await page.goto('/women');
    await expect(page.locator('h1')).toHaveText('Women');
    await expect(page.locator('[data-testid="sale-banner"]')).toHaveCount(0);

    await page.goto('/new-arrivals');
    await expect(page.locator('h1')).toHaveText('New Arrivals');
    await expect(page.locator('[data-testid="sale-banner"]')).toHaveCount(0);
  });

  test('has no axe violations with the banner on', async ({ page }) => {
    await banner(page, 'live');
    await useStubApi(page);

    await page.goto('/sale');
    await expect(page.locator('[data-testid="sale-banner"] img')).toBeVisible();

    const results = await new AxeBuilder({ page }).analyze();

    expect(results.violations).toEqual([]);
  });

  test('fits every width without scrolling sideways, and never spills out of the page', async ({ page }) => {
    await banner(page, 'live');
    await useStubApi(page);

    await page.goto('/sale');
    const image = page.locator('[data-testid="sale-banner"] img');
    await expect(image).toBeVisible();

    for (const width of BREAKPOINTS) {
      await page.setViewportSize({ width, height: 900 });

      expect(await horizontalOverflow(page), `horizontal overflow at ${width}px`).toBe(0);

      const box = await image.boundingBox();
      expect(box, `banner box at ${width}px`).not.toBeNull();
      expect(box!.x, `banner left edge at ${width}px`).toBeGreaterThanOrEqual(0);
      expect(box!.x + box!.width, `banner right edge at ${width}px`).toBeLessThanOrEqual(width);

      // Drawn at the slot's 16:5, so a phone gets a strip rather than a picture taller than the screen.
      expect(Math.abs(box!.width / box!.height - 16 / 5), `aspect at ${width}px`).toBeLessThan(0.05);
    }
  });
});

/**
 * Tester additions (#191): the 2 s timeout under a hung API, escaping of a hostile description in the
 * server render and the transfer state, and the browser-side fetch on a client-side navigation.
 */
test.describe('the Sale page banner under attack (#191 tester)', () => {
  // A hung answer may still be in flight through the route when a test ends; drop it quietly so it is
  // not reported against the next test.
  test.afterEach(async ({ page }) => {
    await page.unrouteAll({ behavior: 'ignoreErrors' });
  });

  test('a hung banner API costs the server render at most its timeout, and the page has no banner', async ({ page }) => {
    await banner(page, 'slow');

    const started = Date.now();
    const response = await page.request.get('/sale', { timeout: 20_000 });
    const elapsed = Date.now() - started;

    expect(response.ok()).toBe(true);
    const html = await response.text();

    // The stub holds the answer for 8 s; the storefront must give up at 2 s and render without it.
    expect(elapsed, `the Sale page took ${elapsed} ms`).toBeLessThan(6_000);
    expect(html).not.toContain('data-testid="sale-banner"');
    expect(html).toContain('<h1');
  });

  test('a hung banner API does not hold the browser either, and the products render', async ({ page }) => {
    await banner(page, 'slow');
    await useStubApi(page);

    // The server render times out, hands nothing over, and the browser asks again after hydration: the
    // listing must be there at once whatever that second ask does.
    await page.goto('/sale', { timeout: 20_000 });
    await expect(page.locator('.product-grid > li').first()).toBeVisible({ timeout: 4_000 });
    await expect(page.locator('[data-testid="sale-banner"]')).toHaveCount(0);
  });

  test('a description written to break out of the attribute is escaped, in the HTML and after hydration', async ({ page }) => {
    await banner(page, 'hostile');

    const html = await (await page.request.get('/sale')).text();
    expect(html).toContain('data-testid="sale-banner"');
    expect(html).not.toContain('<img src=x onerror');
    expect(html).not.toContain('<script>window.__pwned');

    await useStubApi(page);
    await page.goto('/sale');

    const image = page.locator('[data-testid="sale-banner"] img');
    await expect(image).toBeVisible();
    await expect(image).toHaveAttribute('alt', /onerror/);
    expect(await page.evaluate(() => (window as unknown as { __pwned?: number }).__pwned)).toBeUndefined();
    await expect(page.locator('img[src="x"]')).toHaveCount(0);
  });

  test('arriving at the Sale page by a client-side link asks for the banner in the browser and draws it', async ({ page }) => {
    await banner(page, 'live');
    await useStubApi(page);

    await page.goto('/women');
    await expect(page.locator('h1')).toHaveText('Women');

    const asked = page.waitForRequest((request) => request.url().includes('/api/content/sale-banner'));
    await page.locator('a[href="/sale"]').first().click();
    await asked;

    await expect(page.locator('h1')).toHaveText('Sale');
    await expect(page.locator('[data-testid="sale-banner"] img')).toHaveAttribute('alt', LIVE_ALT);

    // Leaving and coming back asks again: the server's handover is taken once, so a later visit is fresh.
    await page.locator('a[href="/women"]').first().click();
    await expect(page.locator('h1')).toHaveText('Women');
    await banner(page, 'none');
    await page.locator('a[href="/sale"]').first().click();
    await expect(page.locator('h1')).toHaveText('Sale');
    await expect(page.locator('[data-testid="sale-banner"]')).toHaveCount(0);
  });
});
