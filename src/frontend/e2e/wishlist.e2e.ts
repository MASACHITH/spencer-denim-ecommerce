import { Page, expect, test } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';

import { apiURL } from '../playwright.config';
import { horizontalOverflow, undersized, unreachable } from './support/measure';

/**
 * Browser checks for the wishlist (W2-08, WISH-01).
 *
 * `local-wishlist.spec.ts`, `local-wishlist-list.spec.ts`, `wishlist.store.spec.ts` and
 * `wishlist-page.spec.ts` pin the storage rules, the store's states and the markup in jsdom. What
 * needs a real browser is what jsdom has no opinion about: **the HTML the server puts on the wire
 * before a line of JavaScript has run** (a wishlist is in the shopper's own browser, so the server
 * must not pretend to know it), a real `localStorage` with a real reload, layout at the eight project
 * widths, real tap targets, and — issue #111 — what a thumb actually hits where the WhatsApp float
 * overlaps a card.
 *
 * The products come from `support/catalog-api-stub.mjs`, whose `/api/wishlist` answers in the order
 * asked with a `null` product for anything it does not sell, exactly as the real endpoint does.
 */

/** docs/04 §4 and the project's non-negotiable list. */
const BREAKPOINTS = [320, 375, 390, 768, 1024, 1280, 1440, 1920] as const;

const LISTING = '/women/jeans';

/** Must match `LocalWishlist`'s own key. */
const STORAGE_KEY = 'sd.wishlist.local';

/**
 * Sends the browser's own `/api` calls to the stub.
 *
 * `route.fetch` + `route.fulfill` rather than a proxy, for the reason `bag.e2e.ts` gives: the request
 * is made with the page's cookies and the response is handed back as though it came from the page's
 * own origin, `Set-Cookie` and all — which the "move to bag" tests need, because the bag really is
 * cookie-identified.
 */
async function useStubApi(page: Page): Promise<void> {
  await page.route('**/api/**', async (route) => {
    const url = new URL(route.request().url());
    const response = await route.fetch({ url: `${apiURL}${url.pathname}${url.search}` });
    await route.fulfill({ response });
  });
}

/**
 * Puts a wishlist in the browser **before any page script runs**.
 *
 * `addInitScript` rather than `evaluate` after `goto`: the page reads the list in
 * `afterNextRender`, so a list written after the first paint is a list the page has already decided
 * was empty, and the test would be measuring the wrong frame.
 */
async function savedInBrowser(
  page: Page,
  entries: readonly { slug: string; name?: string | null }[],
): Promise<void> {
  const payload = JSON.stringify({
    v: 2,
    items: entries.map((entry, index) => ({
      slug: entry.slug,
      name: entry.name ?? null,
      colourId: null,
      colourName: null,
      addedAt: 1_000_000 - index,
    })),
  });

  await page.addInitScript(
    ([key, value]) => {
      try {
        window.localStorage.setItem(key, value);
      } catch {
        // A browser that refuses storage is a case this suite tests deliberately; it is not a failure here.
      }
    },
    [STORAGE_KEY, payload] as const,
  );
}

/**
 * Opens `/wishlist` and waits until the page has **taken the browser's list up**.
 *
 * **Not for a row, and not for `networkidle`.** The heading, the shell and the "Loading your
 * wishlist…" line are all server-rendered, so their presence proves only that the server answered.
 * The signal is that line going away: it is rendered while `taken` is false, and `taken` flips inside
 * `afterNextRender` — so it disappearing is hydration having run *and* the local list having been
 * read, observed rather than assumed. That is the distinction two red `main` branches were built on.
 */
async function openWishlist(page: Page): Promise<void> {
  await page.goto('/wishlist');
  await expect(page.locator('.wish__muted', { hasText: 'Loading your wishlist' })).toHaveCount(0, {
    timeout: 15_000,
  });
}

/** What is really in this browser's wishlist, as the browser holds it. */
async function storedSlugs(page: Page): Promise<string[]> {
  return page.evaluate((key) => {
    const raw = window.localStorage.getItem(key);
    if (!raw) {
      return [];
    }

    const parsed: unknown = JSON.parse(raw);
    const items = Array.isArray(parsed) ? parsed : (parsed as { items?: unknown }).items;

    return Array.isArray(items)
      ? items.map((item) => (typeof item === 'string' ? item : String((item as { slug: string }).slug)))
      : [];
  }, STORAGE_KEY);
}

test.describe('the wishlist (WISH-01)', () => {
  test.beforeEach(async ({ page }) => {
    await useStubApi(page);
  });

  test.describe('what the server puts on the wire', () => {
    /**
     * **The server does not know, and must not guess.** A guest's wishlist is in their browser
     * (doc 02's WISH-01 line, doc 08 §1.7), so the document the SSR process emits is the same for a
     * shopper with twenty saved products and one with none. Read with `page.request`, which runs no
     * JavaScript at all — the only way to see what the server actually said.
     */
    test('the document carries no saved products, and says it is loading rather than empty', async ({
      page,
    }) => {
      const response = await page.request.get('/wishlist');
      const html = await response.text();

      expect(response.status()).toBe(200);
      expect(html).toContain('Loading your wishlist');
      expect(html).not.toContain('Your wishlist is empty');
      expect(html).not.toContain('Test Product');
    });

    /** A wishlist is personal: not in a shared cache, not in a search result. */
    test('the response is no-store and noindex', async ({ page }) => {
      const response = await page.request.get('/wishlist');
      const html = await response.text();

      expect(response.headers()['cache-control']).toContain('no-store');
      expect(html).toContain('name="robots"');
      expect(html).toContain('noindex');
    });

    test('the page is titled for the shop the customer is in', async ({ page }) => {
      const html = await (await page.request.get('/wishlist')).text();

      expect(html).toContain('<title>Wishlist | Colombo Wardrobe</title>');
    });

    /** The endpoint's own headers, off the wire rather than off the configuration that set them. */
    test('the wishlist read is no-store and does not claim to vary on the cookie', async ({
      page,
    }) => {
      const response = await page.request.get(`${apiURL}/api/wishlist?slug=test-product-1`);

      expect(response.status()).toBe(200);
      expect(response.headers()['cache-control']).toContain('no-store');
      expect(response.headers()['vary']).toBeUndefined();
    });
  });

  test.describe('the heart, the count and the page', () => {
    /**
     * The whole journey a shopper takes, in a real browser with real storage: tap the heart on a
     * listing card, watch the header count, open the wishlist, find the product.
     */
    test('a heart on a listing card saves the product, counts it and lists it', async ({ page }) => {
      await page.goto(LISTING);

      const heart = page.locator('.card__wish').first();
      await expect(heart).toBeVisible();

      // Hydration: the header's wishlist badge is taken up in `afterNextRender`, so a card whose
      // heart has a working click handler is a card on a hydrated page. `aria-pressed` is
      // server-rendered as "false", so waiting for it to be "true" is waiting for state, not markup.
      await heart.click();
      await expect(heart).toHaveAttribute('aria-pressed', 'true');

      await expect(page.getByRole('link', { name: /Wishlist, 1 item/ })).toBeVisible();
      expect(await storedSlugs(page)).toEqual(['test-product-1']);

      await openWishlist(page);

      await expect(page.locator('.wish__item')).toHaveCount(1);
      await expect(page.locator('.wish__count')).toContainText('1 item saved in this browser');
      await expect(page.getByRole('link', { name: 'Test Product 1' })).toBeVisible();
    });

    test('the wishlist survives a reload, because it is in this browser', async ({ page }) => {
      await savedInBrowser(page, [{ slug: 'test-product-2' }, { slug: 'test-product-3' }]);
      await openWishlist(page);
      await expect(page.locator('.wish__item')).toHaveCount(2);

      await page.reload();
      await expect(page.locator('.wish__muted', { hasText: 'Loading your wishlist' })).toHaveCount(0);

      await expect(page.locator('.wish__item')).toHaveCount(2);
    });

    /**
     * **The version-1 stored shape is read, not discarded.** Shoppers have been tapping that heart
     * since W2-04 and it stored a bare array of slugs; emptying every existing wishlist on the day
     * the page shipped would have been a silent data loss.
     */
    test('a wishlist saved by the old build still opens', async ({ page }) => {
      await page.addInitScript(
        ([key, value]) => window.localStorage.setItem(key, value),
        [STORAGE_KEY, JSON.stringify(['test-product-4', 'test-product-5'])] as const,
      );

      await openWishlist(page);

      await expect(page.locator('.wish__item')).toHaveCount(2);
      await expect(page.getByRole('link', { name: 'Test Product 4' })).toBeVisible();
    });

    test('an empty wishlist says so and offers a way into the shop', async ({ page }) => {
      await openWishlist(page);

      await expect(page.locator('.wish__empty')).toContainText('Your wishlist is empty');
      await expect(page.getByRole('link', { name: 'Start shopping' })).toBeVisible();
    });

    /** Doc 11 §575: a wishlist item removed from sale is shown as unavailable and can be removed. */
    test('a product the shop no longer sells is named and can be removed', async ({ page }) => {
      await savedInBrowser(page, [
        { slug: 'test-product-6' },
        { slug: 'withdrawn-last-season', name: 'Last Season Jacket' },
      ]);
      await openWishlist(page);

      await expect(page.locator('.wish__gone')).toHaveCount(1);
      await expect(page.locator('.wish__gone')).toContainText('Last Season Jacket');
      await expect(page.locator('.wish__gone')).toContainText('no longer available');
      await expect(page.locator('.wish__count')).toContainText('1 is no longer available');

      await page.locator('.wish__gone [data-testid="wishlist-remove"]').click();

      await expect(page.locator('.wish__gone')).toHaveCount(0);
      expect(await storedSlugs(page)).toEqual(['test-product-6']);
    });

    test('removing takes the product out of the browser and out of the count', async ({ page }) => {
      await savedInBrowser(page, [{ slug: 'test-product-7' }, { slug: 'test-product-8' }]);
      await openWishlist(page);

      await page.locator('[data-testid="wishlist-remove"]').first().click();

      await expect(page.locator('.wish__item')).toHaveCount(1);
      expect(await storedSlugs(page)).toEqual(['test-product-8']);
      await expect(page.getByRole('link', { name: /Wishlist, 1 item/ })).toBeVisible();
    });

    test('clearing asks first, and then empties the whole list', async ({ page }) => {
      await savedInBrowser(page, [{ slug: 'test-product-9' }, { slug: 'test-product-10' }]);
      await openWishlist(page);

      await page.getByRole('button', { name: 'Clear wishlist' }).click();
      await expect(page.locator('.wish__confirm')).toContainText(
        'Remove all 2 items in your wishlist?',
      );

      await page.getByRole('button', { name: 'Cancel' }).click();
      await expect(page.locator('.wish__item')).toHaveCount(2);

      await page.getByRole('button', { name: 'Clear wishlist' }).click();
      await page.getByRole('button', { name: 'Yes, clear it' }).click();

      await expect(page.locator('.wish__empty')).toContainText('Your wishlist is empty');
      expect(await storedSlugs(page)).toEqual([]);
    });

    /**
     * **A failed read does not empty the page.** Membership is in the browser and is still correct;
     * what failed is the prices and the photographs. A wishlist that read as empty here would be
     * indistinguishable from the shop having lost it.
     */
    test('a failed read keeps the saved products on the page and offers Try again', async ({
      page,
    }) => {
      await savedInBrowser(page, [{ slug: 'test-product-12', name: 'Saved Jacket' }]);

      let fail = true;
      await page.route('**/api/wishlist**', async (route) => {
        if (fail) {
          await route.fulfill({ status: 500, body: '{"title":"nope","status":500}' });
          return;
        }

        const url = new URL(route.request().url());
        await route.fulfill({ response: await route.fetch({ url: `${apiURL}${url.pathname}${url.search}` }) });
      });

      await openWishlist(page);

      await expect(page.locator('.wish__error')).toContainText('could not load your wishlist');
      await expect(page.locator('.wish__item')).toHaveCount(1);
      await expect(page.locator('.wish__placeholder')).toContainText('Saved Jacket');
      await expect(page.locator('.wish__empty')).toHaveCount(0);

      fail = false;
      await page.getByRole('button', { name: 'Try again' }).click();

      await expect(page.locator('sd-product-card')).toHaveCount(1);
      await expect(page.locator('.wish__error')).toHaveCount(0);
    });
  });

  test.describe('move to bag (WISH-01)', () => {
    /**
     * A saved entry names a product and a bag line needs a variant, so the size is chosen first
     * (doc 11 §539) — in Quick View, the only thing in the storefront that has the variants.
     */
    test('the size is chosen first, and then the product leaves the wishlist for the bag', async ({
      page,
    }) => {
      await savedInBrowser(page, [{ slug: 'test-product-13' }, { slug: 'test-product-14' }]);
      await openWishlist(page);

      await page.locator('[data-testid="wishlist-move-to-bag"]').first().click();

      const dialog = page.locator('#quick-view');
      await expect(dialog).toBeVisible();

      await dialog.locator('.sd-size').first().click();
      await dialog.locator('[data-testid="quick-view-add"]').click();

      // The dialog closing is Quick View's own confirmation that the bag took it.
      await expect(dialog).toHaveCount(0);

      await expect(page.locator('.wish__item')).toHaveCount(1);
      expect(await storedSlugs(page)).toEqual(['test-product-14']);
      await expect(page.getByRole('link', { name: /Bag, 1 item/ })).toBeVisible();
    });

    /**
     * **The one that matters: a failed add must not lose the item.**
     *
     * W2-09 shipped the mirror of this defect in the other direction — "move to wishlist" destroyed
     * the bag line over a wishlist save that had silently failed, and the item was then in neither
     * place. This is the same exchange from the other side, with the bag made unavailable, and the
     * assertion is on the shopper's own storage rather than on the screen.
     */
    test('a bag that refuses the add keeps the product in the wishlist', async ({ page }) => {
      await savedInBrowser(page, [{ slug: 'test-product-15' }]);

      await page.route('**/api/cart/items', (route) =>
        route.fulfill({ status: 500, body: '{"title":"nope","status":500}' }),
      );

      await openWishlist(page);
      await page.locator('[data-testid="wishlist-move-to-bag"]').first().click();

      const dialog = page.locator('#quick-view');
      await dialog.locator('.sd-size').first().click();
      await dialog.locator('[data-testid="quick-view-add"]').click();

      // The dialog stays, with the reason on it — the error and the control that caused it belong on
      // the same screen.
      await expect(dialog.locator('.qv__error')).toBeVisible();
      await expect(dialog).toBeVisible();

      // And the item is exactly where the shopper left it.
      expect(await storedSlugs(page)).toEqual(['test-product-15']);
      await expect(page.locator('.wish__item')).toHaveCount(1);
    });

    test('dismissing the size chooser leaves the product saved', async ({ page }) => {
      await savedInBrowser(page, [{ slug: 'test-product-16' }]);
      await openWishlist(page);

      await page.locator('[data-testid="wishlist-move-to-bag"]').first().click();
      await expect(page.locator('#quick-view')).toBeVisible();

      await page.keyboard.press('Escape');

      await expect(page.locator('#quick-view')).toHaveCount(0);
      expect(await storedSlugs(page)).toEqual(['test-product-16']);
    });
  });

  test.describe('a browser that refuses to store anything', () => {
    /**
     * Private browsing, blocked site data and a full quota all refuse `setItem`. A heart that filled
     * over a refused write would be lying, and `LocalWishlist` has reported that honestly since
     * W2-09 — this is the browser-level proof that it still does, with `localStorage.setItem` really
     * throwing rather than a stubbed service returning false.
     */
    test('the heart stays empty and says why, and the wishlist is honestly empty', async ({
      page,
    }) => {
      await page.addInitScript(() => {
        const refuse = () => {
          throw new DOMException('refused', 'QuotaExceededError');
        };

        Object.defineProperty(window.localStorage, 'setItem', { value: refuse, configurable: true });
      });

      await page.goto(LISTING);

      const heart = page.locator('.card__wish').first();
      await expect(heart).toBeVisible();
      await heart.click();

      // Nothing claims to have saved: not the heart, not its label, not the header count.
      await expect(heart).toHaveAttribute('aria-pressed', 'false');
      await expect(heart).toHaveAttribute('aria-label', /blocking site data/);
      await expect(page.getByRole('link', { name: /Wishlist, 0 items/ })).toBeVisible();

      await openWishlist(page);
      await expect(page.locator('.wish__empty')).toContainText('Your wishlist is empty');
    });
  });

  test.describe('issue #111: the WhatsApp float and the heart', () => {
    /**
     * **Nothing covers a card's wishlist heart.**
     *
     * The float (`.wa-float`, `position: fixed`, `z-index: 70`, GLB-08) owns the viewport's
     * bottom-right corner by an owner decision dated 2026-10-06. The heart used to sit at each card's
     * *top*-right, so the two met as a card entered the viewport from below: the float covered the
     * heart's centre at 5 of 116 sampled positions at 320x720 and 5 of 134 at 390x844, and a tap in
     * one of those positions opened WhatsApp instead of saving the item. W2-08's fix swaps the heart
     * with the badge, so the only thing left in the float's column is a label nobody taps.
     *
     * **This asks the question a thumb asks.** `document.elementFromPoint` at the centre of each
     * heart, at every scroll offset, because the only thing that matters is what receives the tap — a
     * CSS assertion would have passed happily throughout the bug. The assertion is that the element
     * hit *is* the heart, not merely that it is not the float: "some other thing is on top" is the
     * same dead button.
     *
     * Everything else about the shape of this scan — the sticky-header band, requiring the whole box
     * to be on screen, one `page.evaluate`, blocked images — is `quick-view.e2e.ts`'s, which says at
     * length why each is needed. Copying it is deliberate: the two controls have to be measured the
     * same way or the comparison between them means nothing.
     */
    for (const [width, height] of [
      [320, 720],
      [390, 844],
    ] as const) {
      test(`${width}x${height}: the WhatsApp float never covers a card's heart`, async ({ page }) => {
        await page.setViewportSize({ width, height });
        await page.route('**/media/**', (route) => route.abort());

        await page.goto(LISTING);
        await expect(page.locator('.product-grid > li').first()).toBeVisible();
        await expect(page.locator('.wa-float')).toBeVisible();
        expect(
          await page
            .locator('.card__media')
            .first()
            .evaluate((box: HTMLElement) => Math.round(box.getBoundingClientRect().height)),
          'a card photo has no height without its image, so blocking images changed the layout',
        ).toBeGreaterThan(100);

        const { covered, sampled, offsets } = await page.evaluate(() => {
          const problems: { scrollY: number; nth: number; hit: string; probe: string }[] = [];
          const root = document.documentElement;
          let counted = 0;
          let steps = 0;

          for (let offset = 0; offset <= 40 * 40; offset += 40) {
            window.scrollTo(0, offset);
            steps += 1;

            const header = document.querySelector<HTMLElement>('.site-header');
            const headerBottom = header ? header.getBoundingClientRect().bottom : 0;

            [...document.querySelectorAll<HTMLElement>('.card__wish')].forEach((button, nth) => {
              const box = button.getBoundingClientRect();

              if (
                box.top < headerBottom ||
                box.bottom > root.clientHeight ||
                box.left < 0 ||
                box.right > root.clientWidth
              ) {
                return;
              }

              counted += 1;

              const x = box.x + box.width / 2;
              const y = box.y + box.height / 2;
              const hit = document.elementFromPoint(x, y);
              if (hit !== button && !button.contains(hit)) {
                problems.push({
                  scrollY: Math.round(window.scrollY),
                  nth,
                  hit: `${hit?.tagName ?? 'nothing'}.${hit?.className ?? ''}`.slice(0, 70),
                  probe: `x=${Math.round(x)} y=${Math.round(y)} vw=${root.clientWidth} vh=${root.clientHeight} hb=${Math.round(headerBottom)}`,
                });
              }
            });
          }

          window.scrollTo(0, 0);

          return { covered: problems, sampled: counted, offsets: steps };
        });

        expect(offsets, 'no scroll offset was sampled, so this could not fail').toBeGreaterThan(5);
        expect(sampled, 'no heart centre was ever tested, so this could not fail').toBeGreaterThan(20);
        expect(covered, JSON.stringify(covered.slice(0, 8), null, 2)).toEqual([]);
      });
    }

    /**
     * The badge took the heart's old corner. It is not interactive, so being occluded costs a glance
     * rather than a tap — but it must still be *drawn* where the swap says, and it must not run under
     * the heart, which is what the mirrored `max-width` is for.
     */
    test('320px: the heart is top-left, the badge top-right, and they do not overlap', async ({
      page,
    }) => {
      await page.setViewportSize({ width: 320, height: 720 });
      await page.goto(LISTING);
      await expect(page.locator('.product-grid > li').first()).toBeVisible();

      const geometry = await page.evaluate(() => {
        // The first card that has a badge at all, so the overlap question is a real one.
        const card = [...document.querySelectorAll<HTMLElement>('.card')].find((candidate) =>
          candidate.querySelector('.badge'),
        );

        if (!card) {
          return null;
        }

        const media = card.querySelector<HTMLElement>('.card__media')!.getBoundingClientRect();
        const wish = card.querySelector<HTMLElement>('.card__wish')!.getBoundingClientRect();
        const badge = card.querySelector<HTMLElement>('.badge')!.getBoundingClientRect();

        return {
          wishFromLeft: Math.round(wish.left - media.left),
          badgeFromRight: Math.round(media.right - badge.right),
          overlap: !(wish.right <= badge.left || wish.left >= badge.right),
        };
      });

      expect(geometry, 'no card on this listing has a badge, so the overlap could not fail').not.toBeNull();
      expect(geometry!.wishFromLeft).toBe(0);
      expect(geometry!.badgeFromRight).toBeGreaterThanOrEqual(0);
      expect(geometry!.badgeFromRight).toBeLessThanOrEqual(8);
      expect(geometry!.overlap).toBe(false);
    });
  });

  test.describe('layout, targets and axe', () => {
    for (const width of BREAKPOINTS) {
      test(`${width}px: the wishlist page does not scroll sideways`, async ({ page }) => {
        await savedInBrowser(page, [
          { slug: 'test-product-17' },
          { slug: 'test-product-18' },
          { slug: 'gone-for-good', name: 'A Very Long Discontinued Product Name Indeed' },
        ]);
        await page.setViewportSize({ width, height: 900 });
        await openWishlist(page);

        await expect(page.locator('.wish__item')).toHaveCount(3);
        expect(await horizontalOverflow(page)).toBe(0);
      });
    }

    /**
     * **44 px, measured.** axe does not check tap size at this project's gate — W1-03 shipped 36–41 px
     * controls past a clean axe run — so it is measured here, and `unreachable` is the second half:
     * a 48 px control nobody can bring onto the screen passes a size check with nothing to say about
     * the fact that nobody can press it.
     */
    for (const width of [320, 390] as const) {
      test(`${width}px: every wishlist control is a reachable 44 px target`, async ({ page }) => {
        await savedInBrowser(page, [
          { slug: 'test-product-19' },
          { slug: 'also-withdrawn', name: 'Withdrawn Thing' },
        ]);
        await page.setViewportSize({ width, height: 720 });
        await openWishlist(page);

        const controls =
          '.wish__move, .wish__remove, .wish__clear, .wish__continue, .wish__item .card__wish';

        expect(await undersized(page, controls)).toEqual([]);
        expect(await unreachable(page, controls)).toEqual([]);
      });
    }

    test('the wishlist page is axe-clean with products, and when empty', async ({ page }) => {
      await page.setViewportSize({ width: 390, height: 844 });

      await openWishlist(page);
      const empty = await new AxeBuilder({ page })
        .withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa'])
        .analyze();
      expect(empty.violations.map((violation) => violation.id)).toEqual([]);

      await savedInBrowser(page, [
        { slug: 'test-product-20' },
        { slug: 'never-sold-here', name: 'Discontinued Item' },
      ]);
      await openWishlist(page);
      await expect(page.locator('.wish__item')).toHaveCount(2);

      const filled = await new AxeBuilder({ page })
        .withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa'])
        .analyze();
      expect(filled.violations.map((violation) => violation.id)).toEqual([]);
    });
  });
});
