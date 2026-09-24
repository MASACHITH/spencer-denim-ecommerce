import { Locator, Page, expect, test } from '@playwright/test';

import { apiURL, faultyCatalogueURL } from '../playwright.config';

/**
 * Browser checks for the product detail page (PDP-01..07).
 *
 * These are the checks jsdom cannot make. `product-page.spec.ts` already pins the URL ↔ selection
 * mapping, the price and discount, the stock lines and the four API failures; what needs a real
 * browser is layout at a real viewport, a real focus model, `prefers-reduced-motion`, and the
 * HTML the server actually puts on the wire before any script has run.
 *
 * The catalogue comes from `support/catalog-api-stub.mjs`, which answers
 * `/api/catalog/products/{slug}` the way the API does — 400 for a slug that is not a slug, 404
 * for one nothing is published under — and builds the payload from the slug it was asked for.
 * Product 1 has two colourways, product 5 has three and a video, product 7 has no media at all.
 */

const PRODUCT = '/product/test-product-1';

/** docs/04 §4 and the project's non-negotiable list. */
const BREAKPOINTS = [320, 375, 390, 768, 1024, 1280, 1440, 1920] as const;

/**
 * The browser's own `/api` calls go to the storefront's origin, which in production is a reverse
 * proxy in front of the API. Here it is the stub, fetched from the test process.
 */
async function useStubApi(page: Page): Promise<void> {
  await page.route('**/api/catalog/**', async (route) => {
    const url = new URL(route.request().url());
    const response = await route.fetch({ url: `${apiURL}${url.pathname}${url.search}` });
    await route.fulfill({ response });
  });
}

/** `scrollWidth` against `clientWidth`: `innerWidth` includes the scrollbar and hides overflow. */
async function horizontalOverflow(page: Page): Promise<number> {
  return page.evaluate(() => {
    const root = document.documentElement;
    return root.scrollWidth - root.clientWidth;
  });
}

/** Every visible element matching `selector` whose box is under the 44 px touch minimum. */
async function undersized(page: Page, selector: string) {
  return page.evaluate(
    (target) =>
      [...document.querySelectorAll<HTMLElement>(target)]
        .filter((element) => element.getClientRects().length > 0)
        .map((element) => ({
          label: `${element.tagName}.${element.className}`.slice(0, 60),
          width: Math.round(element.getBoundingClientRect().width),
          height: Math.round(element.getBoundingClientRect().height),
        }))
        .filter((box) => box.width < 44 || box.height < 44),
    selector,
  );
}

/**
 * Waits until the page has hydrated far enough to have recorded itself (PDP-07).
 *
 * `h1` being visible is not enough: the page is server-rendered, so the heading is on screen
 * before a line of JavaScript has run, and navigating away at that moment left the first product
 * unrecorded about one run in five. Recording is the last thing the page does, so waiting for it
 * is waiting for hydration.
 */
async function recorded(page: Page, slug: string): Promise<void> {
  await expect
    .poll(() => page.evaluate(() => localStorage.getItem('sd.recently-viewed') ?? ''))
    .toContain(slug);
}

/**
 * How far outside a control's border box the focus ring is drawn: `outline-offset: 2px` plus
 * `--focus-width: 2.5px`, rounded up.
 */
const RING_BAND = 6;

/**
 * The edges of a focused control where the focus ring does not actually reach the screen.
 *
 * **Why this takes pictures.** The obvious assertion — `getComputedStyle(element).outlineWidth` —
 * reads the outline the element *would* paint, and is structurally blind to an ancestor clipping
 * it away. It passed happily on a thumbnail rail that was cropping three of the four edges off
 * the ring: lesson L4 again, behind a test that could not fail. This screenshots the strip just
 * outside each edge with the control focused and again with it blurred, and reports the edges
 * whose pixels come back byte for byte identical. If the ring painted there, they cannot be.
 *
 * The control must be focused on the way in; it is left blurred on the way out.
 */
async function edgesWithoutRing(page: Page, control: Locator): Promise<string[]> {
  // Centred, not merely "scrolled into view": a control resting against the bottom of the
  // viewport has no room below it for the strip, and the guard below would rightly refuse to
  // measure rather than quietly report the ring intact.
  await control.evaluate((element: HTMLElement) =>
    element.scrollIntoView({ block: 'center', inline: 'center', behavior: 'instant' }),
  );

  const box = await control.boundingBox();
  const viewport = page.viewportSize();
  if (!box || !viewport) {
    throw new Error('the control has no box to look around');
  }

  const strips: Record<string, Clip> = {
    top: {
      x: box.x - RING_BAND,
      y: box.y - RING_BAND,
      width: box.width + RING_BAND * 2,
      height: RING_BAND,
    },
    bottom: {
      x: box.x - RING_BAND,
      y: box.y + box.height,
      width: box.width + RING_BAND * 2,
      height: RING_BAND,
    },
    left: { x: box.x - RING_BAND, y: box.y, width: RING_BAND, height: box.height },
    right: { x: box.x + box.width, y: box.y, width: RING_BAND, height: box.height },
  };

  for (const [edge, clip] of Object.entries(strips)) {
    // A strip off the edge of the viewport would be unreadable, and a silent pass is the one
    // thing this helper exists to prevent.
    if (
      clip.x < 0 ||
      clip.y < 0 ||
      clip.x + clip.width > viewport.width ||
      clip.y + clip.height > viewport.height
    ) {
      throw new Error(`the ${edge} strip falls outside the viewport, so the ring cannot be seen`);
    }
  }

  const lit: Record<string, Buffer> = {};
  for (const [edge, clip] of Object.entries(strips)) {
    lit[edge] = await page.screenshot({ clip: round(clip) });
  }

  await control.evaluate((element: HTMLElement) => element.blur());

  const missing: string[] = [];
  for (const [edge, clip] of Object.entries(strips)) {
    if ((await page.screenshot({ clip: round(clip) })).equals(lit[edge])) {
      missing.push(edge);
    }
  }

  return missing;
}

interface Clip {
  x: number;
  y: number;
  width: number;
  height: number;
}

function round(clip: Clip): Clip {
  return {
    x: Math.round(clip.x),
    y: Math.round(clip.y),
    width: Math.max(1, Math.round(clip.width)),
    height: Math.max(1, Math.round(clip.height)),
  };
}

/**
 * Whether focus is currently inside the open modal.
 *
 * `.sd-modal__panel`, not `[role="dialog"]`: the shell keeps the mobile navigation drawer mounted
 * and hidden at all times, and it carries `role="dialog"` too, so a raw `querySelector` for the
 * role finds the drawer and answers about the wrong element entirely. That is what this helper
 * did at first, and it reported the trap broken while it was working perfectly.
 */
async function focusIsInsideModal(page: Page): Promise<boolean> {
  return page.evaluate(() => {
    const panel = document.querySelector('.sd-modal__panel');
    return !!panel && !!document.activeElement && panel.contains(document.activeElement);
  });
}

test.beforeEach(async ({ page }) => {
  await useStubApi(page);
});

test.describe('what the server puts on the wire (GLB-11)', () => {
  /**
   * Fetched, not rendered: this is the document a crawler receives, before a single line of
   * JavaScript has run. If the page fetched its product in the browser instead, everything below
   * would be missing from it.
   */
  test('the name, the price and the description are already in the HTML', async ({ request }) => {
    const response = await request.get(PRODUCT);

    expect(response.status()).toBe(200);
    const html = await response.text();

    expect(html).toContain('Test Product 1');
    expect(html).toContain('SD-0001');
    expect(html).toContain('Rs. 2,600');
    expect(html).toContain('rigid cotton denim');
    // The size buttons are rendered too, so the page is usable the moment it paints.
    expect(html).toContain('Choose a size');
  });

  /**
   * `catalog.Products.Description` is sanitised HTML (doc 08 §3.2) and the seeder writes real
   * markup into it. Interpolating it printed `<p>` at the shopper on the live API — found by
   * clicking through, not by a test, which is why this one exists.
   */
  test('the description is rendered as rich text, not as its own tags', async ({ page }) => {
    await page.goto(PRODUCT);

    const body = page.locator('.rich');
    await expect(body.locator('p')).toHaveCount(2);
    await expect(body).not.toContainText('<p>');
  });

  test('a shared colourway link renders that colourway on the server', async ({ request }) => {
    const html = await (await request.get(`${PRODUCT}?colour=2`)).text();

    // On the `alt` attributes, not anywhere in the document: the server hands its fetched payload
    // to the browser in a transfer-state script, and every colourway's alt text is in there.
    // Asserting on the whole document would only prove the payload arrived, not that the page
    // rendered the right half of it.
    expect(html).toContain('alt="Test Product 1 in Black"');
    expect(html).not.toContain('alt="Test Product 1 in Indigo"');
  });

  test('a shared size link is priced on the server', async ({ request }) => {
    // Size L carries a price override in the stub, as ProductVariants.PriceOverride does.
    const html = await (await request.get(`${PRODUCT}?colour=1&size=12`)).text();

    expect(html).toContain('Rs. 3,100');
  });

  test('the canonical address is the product, not the colourway', async ({ request }) => {
    const html = await (await request.get(`${PRODUCT}?colour=2&size=11`)).text();

    expect(html).toContain('href="/product/test-product-1"');
    expect(html).not.toContain('href="/product/test-product-1?colour=2"');
  });

  test('an unknown slug is a real 404, not a 200 that says "not found"', async ({ request }) => {
    const response = await request.get('/product/no-such-product');

    expect(response.status()).toBe(404);
    expect(await response.text()).toContain('We could not find that page');
  });

  /**
   * The server already knows there is no such product, so the browser must not ask again. It did,
   * and every not-found page carried a second, guaranteed 404 in the console.
   */
  test('the browser does not re-ask for a product the server already said is missing', async ({
    page,
  }) => {
    const asked: string[] = [];
    page.on('request', (request) => {
      if (request.url().includes('/api/catalog/products/')) {
        asked.push(request.url());
      }
    });

    await page.goto('/product/no-such-product');
    await expect(page.getByRole('heading', { level: 1 })).toHaveText(
      'We could not find that page',
    );

    expect(asked).toEqual([]);
  });

  test('a slug the API will not even accept is the same 404', async ({ request }) => {
    const response = await request.get('/product/NOT%20a%20slug');

    expect(response.status()).toBe(404);
    expect(await response.text()).toContain('We could not find that page');
  });
});

test.describe('the gallery from the keyboard (PDP-01)', () => {
  test('arrows move through the thumbnails and change the stage', async ({ page }) => {
    await page.goto('/product/test-product-5');
    // Hydration first. A click made before it lands is replayed by `withEventReplay`, but a
    // keystroke is not: an arrow pressed at a server-rendered thumbnail simply does nothing, and
    // the test failed about one run in ten on a loaded machine with the rail never moving.
    await recorded(page, 'test-product-5');

    // Product 5 has three colourways, one shot that belongs to all of them and one video, so
    // one colourway's stage holds three slides: its own photograph, the shared one, the film.
    const thumbs = page.getByRole('tab');
    await expect(thumbs).toHaveCount(3);

    await thumbs.first().focus();
    await expect(thumbs.first()).toHaveAttribute('aria-selected', 'true');

    await page.keyboard.press('ArrowRight');
    await expect(thumbs.nth(1)).toBeFocused();
    await expect(thumbs.nth(1)).toHaveAttribute('aria-selected', 'true');
    await expect(thumbs.first()).toHaveAttribute('aria-selected', 'false');

    await page.keyboard.press('End');
    await expect(thumbs.nth(2)).toBeFocused();
    // The last slide of product 5 is the video, so the stage swaps the image for a player.
    await expect(page.locator('video')).toBeVisible();

    await page.keyboard.press('Home');
    await expect(thumbs.first()).toBeFocused();
    await expect(page.locator('.gallery__img')).toBeVisible();
  });

  test('the rail costs one Tab stop, not one per photograph', async ({ page }) => {
    await page.goto('/product/test-product-5');

    const tabbable = await page.evaluate(
      () =>
        [...document.querySelectorAll('[role="tab"]')].filter(
          (tab) => tab.getAttribute('tabindex') !== '-1',
        ).length,
    );

    expect(tabbable).toBe(1);
  });

  test('a product with no photographs shows a placeholder and no rail', async ({ page }) => {
    await page.goto('/product/test-product-7');

    await expect(page.getByRole('tab')).toHaveCount(0);
    await expect(page.locator('.gallery__placeholder--stage')).toBeVisible();
    await expect(page.locator('.gallery__img')).toHaveCount(0);
  });

  test('the stage keeps its box whether or not a photograph arrives', async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 844 });
    await page.goto(PRODUCT);
    const withImage = await page.locator('.gallery__stage').boundingBox();

    await page.goto('/product/test-product-7');
    const withNone = await page.locator('.gallery__stage').boundingBox();

    expect(withImage).not.toBeNull();
    expect(withNone).not.toBeNull();
    expect(Math.round(withNone!.height)).toBe(Math.round(withImage!.height));
  });
});

test.describe('the full-screen view (PDP-01)', () => {
  test('traps focus, and hands it back to the control that opened it', async ({ page }) => {
    await page.goto(PRODUCT);

    const opener = page.locator('.gallery__open');
    await opener.focus();
    await opener.click();

    const dialog = page.getByRole('dialog');
    await expect(dialog).toBeVisible();
    await expect.poll(() => focusIsInsideModal(page)).toBe(true);

    // Tab all the way round: focus must never land outside the dialog.
    for (let press = 0; press < 8; press++) {
      await page.keyboard.press('Tab');
      expect(await focusIsInsideModal(page)).toBe(true);
    }

    await page.keyboard.press('Escape');
    await expect(dialog).toHaveCount(0);
    await expect(opener).toBeFocused();
  });

  test('zooms in and out, and the level is announced', async ({ page }) => {
    await page.goto(PRODUCT);
    await page.locator('.gallery__open').click();

    const level = page.locator('.viewer__level');
    await expect(level).toHaveText('1×');
    await expect(page.getByRole('button', { name: 'Zoom out' })).toBeDisabled();

    await page.getByRole('button', { name: 'Zoom in' }).click();
    await expect(level).toHaveText('2×');
    await page.getByRole('button', { name: 'Zoom in' }).click();
    await expect(level).toHaveText('3×');
    await expect(page.getByRole('button', { name: 'Zoom in' })).toBeDisabled();
  });

  test('closes on the scrim too', async ({ page }) => {
    await page.goto(PRODUCT);
    await page.locator('.gallery__open').click();
    await expect(page.getByRole('dialog')).toBeVisible();

    // The full-screen panel covers the viewport, so the scrim is only reachable underneath it.
    await page.locator('.sd-modal__scrim').dispatchEvent('click');
    await expect(page.getByRole('dialog')).toHaveCount(0);
  });

  test('the page behind does not scroll while it is open', async ({ page }) => {
    await page.goto(PRODUCT);
    await page.locator('.gallery__open').click();

    expect(
      await page.evaluate(() => getComputedStyle(document.body).overflow),
    ).toBe('hidden');
  });
});

test.describe('the size guide (PDP-04)', () => {
  /**
   * The API sends a guide's id and name and none of its measurements (issue #32), so there is
   * nothing to open. A trigger that opened a dialog saying "nothing to show here" is a dead
   * control wearing a coat, and the one condition it hangs on is `hasSizeGuideContent`.
   */
  test('offers no trigger while the API carries no measurements', async ({ page }) => {
    await page.goto(PRODUCT);
    await expect(page.locator('h1')).toBeVisible();

    await expect(page.locator('.pdp__guide')).toHaveCount(0);
    await expect(page.getByRole('button', { name: /size guide/i })).toHaveCount(0);
  });

  test('says nothing about a size guide in the server-rendered HTML either', async ({
    request,
  }) => {
    expect(await (await request.get(PRODUCT)).text()).not.toContain('size guide');
  });
});

test.describe('reduced motion', () => {
  test.beforeEach(async ({ page }) => {
    await page.emulateMedia({ reducedMotion: 'reduce' });
  });

  test('the setting really is on', async ({ page }) => {
    await page.goto(PRODUCT);

    expect(
      await page.evaluate(() => window.matchMedia('(prefers-reduced-motion: reduce)').matches),
    ).toBe(true);
  });

  // Lesson L3: reduced motion puts every transition on 0.01 ms, including the `visibility` that
  // reveals a dialog, and a browser will not focus an element that is still hidden.
  test('the full-screen view still takes focus', async ({ page }) => {
    await page.goto(PRODUCT);
    await page.locator('.gallery__open').click();

    await expect(page.getByRole('dialog')).toBeVisible();
    await expect.poll(() => focusIsInsideModal(page)).toBe(true);
  });

  test('the full-screen view still hands focus back on Escape', async ({ page }) => {
    await page.goto(PRODUCT);

    const opener = page.locator('.gallery__open');
    await opener.focus();
    await opener.click();
    await expect.poll(() => focusIsInsideModal(page)).toBe(true);

    await page.keyboard.press('Escape');
    await expect(opener).toBeFocused();
  });

  /**
   * Lesson L4: a pseudo-element with `inset: 0` painted over an inset focus ring. Every control
   * on this page is checked for a ring that is really drawn and really on top — an outline with
   * a width, or a visible `::after` ring like the card's heart uses.
   */
  const FOCUSABLE = [
    { name: 'a colour swatch', selector: '.swatch' },
    { name: 'a size button', selector: '.size:not(:disabled)' },
    { name: 'the wishlist button', selector: '.pdp__wish' },
    // With no size chosen the stepper's ceiling is one, so both of its buttons are disabled and
    // there is nothing to focus. A size is the state a shopper is in when they use it anyway.
    { name: 'the quantity stepper', selector: '.qty__step:not(:disabled)', url: `${PRODUCT}?colour=1&size=10` },
    { name: 'the zoom button', selector: '.gallery__open' },
    { name: 'a thumbnail', selector: '[role="tab"]' },
  ] as const;

  for (const control of FOCUSABLE) {
    test(`${control.name} has a focus ring that is visible`, async ({ page }) => {
      // Taller than the default 720, for two measured reasons. The zoom button is the whole 3:4
      // stage — 771 px at this width — so a shorter viewport leaves no room above or below it for
      // the strip the ring is measured in; and at 1000 px its centred top lands at y=114, under a
      // header that ends at y=118, so the top strip showed the header in both shots and the
      // helper reported a ring that was in fact drawn. Neither was a page defect; both would have
      // been a red test for the wrong reason.
      await page.setViewportSize({ width: 1280, height: 1200 });
      await page.goto('url' in control ? control.url : PRODUCT);
      // Hydration first. `goto` resolves on `load`, which is before Angular has claimed the
      // server's DOM, and focus placed in that window can be lost to the hydration that follows
      // — a failure `toBeFocused` can never recover from, because nothing focuses it again.
      // Recording the visit is the last thing the page does, so it is the signal that it is live.
      await recorded(page, 'test-product-1');

      // A press of Tab first, so the browser is in keyboard modality: Chromium only matches
      // `:focus-visible` on a button when the last interaction was a keyboard one, and a bare
      // programmatic `focus()` would test a ring the shopper never sees.
      await page.keyboard.press('Tab');

      const target = page.locator(control.selector).first();
      await target.focus();
      await expect(target).toBeFocused();
      expect(await target.evaluate((element) => element.matches(':focus-visible'))).toBe(true);

      const ring = await target.evaluate((element) => {
        const style = getComputedStyle(element);
        const after = getComputedStyle(element, '::after');

        return {
          outlineWidth: parseFloat(style.outlineWidth),
          outlineStyle: style.outlineStyle,
          afterBorder: parseFloat(after.borderTopWidth) || 0,
          // The control must not be painted over by its own overlay.
          opacity: parseFloat(style.opacity),
        };
      });

      expect(ring.opacity).toBeGreaterThan(0.5);
      expect(
        (ring.outlineStyle !== 'none' && ring.outlineWidth >= 2) || ring.afterBorder >= 2,
      ).toBe(true);

      // …and that it reaches the screen on all four sides. The check above only says what the
      // element would paint; this one says what a shopper can see (WCAG 2.2 AA 2.4.11).
      expect(await edgesWithoutRing(page, target)).toEqual([]);
    });
  }

  /**
   * The rail is a scrolling row on a phone and a scrolling column from 768 px, and the two clip on
   * different axes, so the ring has to be checked at both. It was cropped at 375 px as well.
   */
  test('a thumbnail keeps its ring where the rail is a scrolling row', async ({ page }) => {
    await page.setViewportSize({ width: 375, height: 1200 });
    await page.goto(PRODUCT);
    await recorded(page, 'test-product-1');
    await page.keyboard.press('Tab');

    const thumb = page.locator('[role="tab"]').first();
    await thumb.focus();
    expect(await thumb.evaluate((element) => element.matches(':focus-visible'))).toBe(true);

    expect(await edgesWithoutRing(page, thumb)).toEqual([]);
  });
});

test.describe('responsive (GLB-09)', () => {
  for (const width of BREAKPOINTS) {
    test(`${width}px: no horizontal scroll`, async ({ page }) => {
      await page.setViewportSize({ width, height: 900 });
      await page.goto(PRODUCT);
      await expect(page.locator('h1')).toBeVisible();

      expect(await horizontalOverflow(page)).toBeLessThanOrEqual(0);
    });
  }

  test('every button, input and thumbnail meets the 44px touch minimum at 320px', async ({
    page,
  }) => {
    await page.setViewportSize({ width: 320, height: 640 });
    await page.goto(PRODUCT);
    await expect(page.locator('h1')).toBeVisible();

    expect(
      await undersized(
        page,
        '.pdp__info button, .pdp__info input, .gallery__thumb, .details__summary',
      ),
    ).toEqual([]);
  });

  /**
   * The test above deliberately excludes links, and used to claim "every control" while doing so.
   * This one covers them, and says exactly which exemption each one is standing on rather than
   * waving at "inline text" — there are only two kinds on this page, and both are checkable:
   *
   * - a **stretched** link, whose `::after` covers its whole card, so the real target is the card
   *   and the anchor's own 96×17 box is not what anyone taps;
   * - a **trail** link in the breadcrumb, 44 px tall and as wide as its word, which clears the
   *   WCAG 2.2 AA minimum of 24×24 (2.5.8). The project's own 44 px rule is the AAA figure
   *   (2.5.5), and widening a word in a breadcrumb to reach it would space the trail oddly.
   *
   * Anything else under 44 px — a bare icon link, say — fails here, which is the point.
   */
  test('every link under 44px is a stretched card link or a breadcrumb crumb', async ({ page }) => {
    await page.setViewportSize({ width: 320, height: 640 });
    await page.goto(PRODUCT);
    await expect(page.locator('h1')).toBeVisible();

    const small = await page.evaluate(() =>
      [...document.querySelectorAll<HTMLAnchorElement>('main a[href]')]
        .filter((link) => link.getClientRects().length > 0)
        .map((link) => {
          const box = link.getBoundingClientRect();
          const after = getComputedStyle(link, '::after');
          const card = link.closest('li')?.getBoundingClientRect();

          return {
            text: (link.textContent ?? '').trim().slice(0, 40),
            width: Math.round(box.width),
            height: Math.round(box.height),
            // A stretched link: the whole card is the target, so measure the card.
            stretched: after.position === 'absolute' && after.inset === '0px',
            cardWidth: Math.round(card?.width ?? 0),
            cardHeight: Math.round(card?.height ?? 0),
            inBreadcrumb: !!link.closest('.breadcrumb'),
          };
        })
        .filter((link) => link.width < 44 || link.height < 44),
    );

    // Nothing under 44 px that is neither of the two known shapes.
    expect(small.filter((link) => !link.stretched && !link.inBreadcrumb)).toEqual([]);

    // A stretched link's real target is the card, and that is comfortably over 44 px.
    for (const link of small.filter((entry) => entry.stretched)) {
      expect(link.cardWidth).toBeGreaterThanOrEqual(44);
      expect(link.cardHeight).toBeGreaterThanOrEqual(44);
    }

    // A crumb is full height and clears the AA minimum on the other axis.
    for (const link of small.filter((entry) => entry.inBreadcrumb)) {
      expect(link.height).toBeGreaterThanOrEqual(44);
      expect(link.width).toBeGreaterThanOrEqual(24);
    }
  });

  test('the gallery and the details sit side by side from 768px, and stack below it', async ({
    page,
  }) => {
    await page.setViewportSize({ width: 390, height: 844 });
    await page.goto(PRODUCT);
    const stacked = await page.evaluate(
      () => getComputedStyle(document.querySelector('.pdp__layout')!).gridTemplateColumns,
    );

    await page.setViewportSize({ width: 1024, height: 900 });
    const side = await page.evaluate(
      () => getComputedStyle(document.querySelector('.pdp__layout')!).gridTemplateColumns,
    );

    expect(stacked.split(' ').filter(Boolean)).toHaveLength(1);
    expect(side.split(' ').filter(Boolean)).toHaveLength(2);
  });
});

test.describe('the selection (PDP-03)', () => {
  test('choosing a colourway changes the photographs and the address', async ({ page }) => {
    await page.goto(PRODUCT);

    const first = page.locator('.gallery__img');
    await expect(first).toHaveAttribute('alt', 'Test Product 1 in Indigo');

    await page.locator('.swatch').nth(1).click();

    await expect(page).toHaveURL(/colour=2/);
    await expect(page.locator('.gallery__img')).toHaveAttribute('alt', 'Test Product 1 in Black');
  });

  test('a size the colourway is not made in is struck through and cannot be chosen', async ({
    page,
  }) => {
    await page.goto(`${PRODUCT}?colour=2`);

    const large = page.locator('.size').nth(2);
    await expect(large).toBeDisabled();
    expect(
      await large.evaluate((element) => getComputedStyle(element).textDecorationLine),
    ).toContain('line-through');
  });

  test('picking a size re-prices the page', async ({ page }) => {
    await page.goto(PRODUCT);
    // Scoped to the product's own price: the related product cards carry `.price__now` too.
    const now = page.locator('.pdp__price .price__now');
    await expect(now).toHaveText('Rs. 2,600');

    await page.locator('.size').nth(2).click();

    await expect(page).toHaveURL(/size=12/);
    await expect(now).toHaveText('Rs. 3,100');
  });
});

test.describe('sold out looks sold out (PDP-03)', () => {
  /**
   * The screen reader was told and the sighted shopper was not: a size that is made but has none
   * left rendered byte for byte like one in stock. The three states have to differ from each
   * other, and by something other than colour alone.
   */
  test('a sold-out size is marked, and differs from both in stock and not made', async ({
    page,
  }) => {
    await page.goto(`${PRODUCT}?colour=1`);

    const sizes = page.locator('.size');
    const inStock = sizes.nth(0);
    const soldOut = sizes.nth(1);
    const notMade = page.locator('.size:disabled');

    await expect(soldOut).toBeEnabled();
    await expect(soldOut).toHaveClass(/is-sold-out/);
    await expect(soldOut).toHaveAttribute('aria-label', /out of stock/);
    await expect(inStock).not.toHaveClass(/is-sold-out/);

    // The mark is drawn, not merely classed, and it is not a colour change on its own.
    const mark = await soldOut.evaluate((element) => {
      const after = getComputedStyle(element, '::after');
      return { content: after.content, image: after.backgroundImage };
    });
    expect(mark.content).not.toBe('none');
    expect(mark.image).toContain('gradient');

    // An in-stock size draws no mark at all, so the two really do differ on screen.
    expect(
      await inStock.evaluate((element) => getComputedStyle(element, '::after').content),
    ).toBe('none');

    // This colourway is made in every size, so the third state is not on screen to confuse it.
    await expect(notMade).toHaveCount(0);
  });

  test('a sold-out colourway is marked too', async ({ page }) => {
    // Every variant of product 11 has nothing left.
    await page.goto('/product/test-product-11');
    await expect(page.locator('h1')).toBeVisible();

    const dots = page.locator('.swatch__dot');
    await expect(dots.first()).toHaveClass(/is-sold-out/);
    expect(
      await dots.first().evaluate((element) => getComputedStyle(element, '::after').content),
    ).not.toBe('none');
    await expect(page.locator('.pdp__stock')).toHaveText('Out of stock');
  });

  test('an in-stock colourway carries no mark', async ({ page }) => {
    await page.goto(PRODUCT);

    expect(
      await page
        .locator('.swatch__dot')
        .first()
        .evaluate((element) => getComputedStyle(element, '::after').content),
    ).toBe('none');
  });
});

test.describe('what is deliberately not here', () => {
  test('there is no Add to Bag or Buy Now, dead or disabled (W2-09)', async ({ page }) => {
    await page.goto(PRODUCT);
    await expect(page.locator('h1')).toBeVisible();

    await expect(page.getByRole('button', { name: /add to bag/i })).toHaveCount(0);
    await expect(page.getByRole('button', { name: /buy now/i })).toHaveCount(0);
  });

  test('the wishlist heart works, per browser, as it does on a card (W2-08)', async ({ page }) => {
    await page.goto(PRODUCT);

    const heart = page.locator('.pdp__wish');
    await expect(heart).toHaveAttribute('aria-pressed', 'false');
    await heart.click();
    await expect(heart).toHaveAttribute('aria-pressed', 'true');

    await page.reload();
    await expect(page.locator('.pdp__wish')).toHaveAttribute('aria-pressed', 'true');
  });
});

test.describe('recently viewed (PDP-07)', () => {
  test('is absent from the server-rendered HTML, because the server has no storage', async ({
    request,
  }) => {
    expect(await (await request.get(PRODUCT)).text()).not.toContain('Recently viewed');
  });

  test('appears after a second product, and never shows the one on screen', async ({ page }) => {
    await page.goto(PRODUCT);
    await recorded(page, 'test-product-1');

    await page.goto('/product/test-product-2');
    await expect(page.locator('h1')).toHaveText('Test Product 2');

    const row = page.locator('section[aria-labelledby="pdp-recent"]');
    await expect(row).toBeVisible();
    await expect(row.locator('.card__name')).toHaveText(['Test Product 1']);
  });

  test('survives junk left under the key (lesson L1)', async ({ page }) => {
    await page.goto(PRODUCT);
    await recorded(page, 'test-product-1');
    await page.evaluate(() => localStorage.setItem('sd.recently-viewed', '{"not":"an array"}'));

    await page.goto('/product/test-product-2');

    await expect(page.locator('h1')).toHaveText('Test Product 2');
    await expect(page.locator('section[aria-labelledby="pdp-recent"]')).toHaveCount(0);
  });
});

test.describe('when the store itself is broken', () => {
  /**
   * A 500 from the catalogue is not a missing product. The page says so, offers "Try again", and
   * the server answers 503 with `Retry-After` — a crawler retries a 503 and keeps the URL, where
   * it drops a 404. This runs against the faulty server pair, whose stub 500s every product.
   */
  test('answers 503 with Retry-After, not 404', async ({ request }) => {
    const response = await request.get(`${faultyCatalogueURL}${PRODUCT}`);

    expect(response.status()).toBe(503);
    expect(response.headers()['retry-after']).toBeTruthy();

    const html = await response.text();
    expect(html).toContain('We could not show this product');
    expect(html).toContain('Try again');
    // Never a status code or a stack in front of a shopper.
    expect(html).not.toContain('Forced failure');
  });

  test('offers a way back, and no blank page', async ({ page }) => {
    // The suite's own route sends every `/api/catalog/**` call to the healthy stub, and the page
    // asks again once it hydrates, so without this the broken page would repair itself and the
    // test would pass for the wrong reason.
    await page.route('**/api/catalog/products/*', (route) =>
      route.fulfill({
        status: 500,
        contentType: 'application/problem+json',
        body: JSON.stringify({ title: 'Forced failure', status: 500 }),
      }),
    );

    await page.goto(`${faultyCatalogueURL}${PRODUCT}`);

    await expect(page.getByRole('heading', { level: 1 })).toHaveText(
      'We could not show this product',
    );
    await expect(page.getByRole('button', { name: 'Try again' })).toBeVisible();
    await expect(page.getByRole('link', { name: 'Back to the home page' })).toBeVisible();
  });
});
