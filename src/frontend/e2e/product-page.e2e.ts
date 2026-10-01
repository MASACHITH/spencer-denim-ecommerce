import { Page, expect, test } from '@playwright/test';

import { apiURL, faultyCatalogueURL } from '../playwright.config';
// Moved to support/measure.ts when the homepage (W2-01) needed the same three measurements.
// One copy, because `edgesWithoutRing` exists precisely because an earlier assertion could
// not fail, and a second copy is a second chance for one of them to drift back.
import { edgesWithoutRing, horizontalOverflow, undersized } from './support/measure';

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

/** The same product with a colourway and size settled, so a variant is resolved. */
const SIZED = `${PRODUCT}?colour=1&size=10`;

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
    // The dialog first. A click made before hydration is replayed afterwards, so reading the
    // body's style the instant the click resolves can catch the page before the dialog exists —
    // it did, about one run in thirty. Every other test here waits on a locator, which retries;
    // this one read the DOM once.
    await expect(page.getByRole('dialog')).toBeVisible();

    expect(
      await page.evaluate(() => getComputedStyle(document.body).overflow),
    ).toBe('hidden');
  });
});

test.describe('the size guide (PDP-04)', () => {
  /** The real seeded guide's last row, which is also the furthest cell from the left edge. */
  const LAST_ROW = ['XXL', '107-113', '89-95', '115-121'];

  /**
   * The trigger, by its exact name.
   *
   * `/size guide/i` matches two controls the moment the dialog is open — the trigger and the
   * dialog's own "Close size guide" button — and Playwright's strict mode rightly refuses to guess.
   */
  const TRIGGER = { name: 'Women apparel size guide', exact: true } as const;

  /** The stub serves a seven-column guide here, which no phone can fit. */
  const WIDE_PRODUCT = '/product/test-product-2';

  test('opens on the measurements the API sent', async ({ page }) => {
    await page.goto(PRODUCT);

    const trigger = page.getByRole('button', TRIGGER);
    await expect(trigger).toBeVisible();
    await expect(trigger).toHaveAttribute('aria-expanded', 'false');

    await trigger.click();

    const dialog = page.getByRole('dialog');
    await expect(dialog).toBeVisible();
    await expect(trigger).toHaveAttribute('aria-expanded', 'true');

    // The prose, which arrives as markup and must be rendered as markup: a `<p>` element, not the
    // characters `<p>` on the screen.
    await expect(dialog.locator('.guide__intro p')).toHaveText(/Measurements are in centimetres/);
    await expect(dialog.getByRole('heading', { name: 'How to measure' })).toBeVisible();

    // The table, as a table: headers that are headers, and every row the API sent.
    const table = dialog.getByRole('table');
    await expect(table).toHaveAccessibleName(/measurements/i);
    await expect(table.getByRole('columnheader')).toHaveText(['Size', 'Bust', 'Waist', 'Hip']);
    await expect(table.locator('tbody tr')).toHaveCount(6);
    await expect(table.locator('tbody tr').last().locator('th, td')).toHaveText(LAST_ROW);

    // Each size is the row's header, not an ordinary cell, so a screen reader can say which row a
    // measurement belongs to.
    await expect(table.getByRole('rowheader')).toHaveText(['XS', 'S', 'M', 'L', 'XL', 'XXL']);
  });

  /**
   * **The hard part, and the reason this is not one line.** Four columns of measurements cannot fit
   * 320 px, so something has to give: either the dialog scrolls inside itself or the page scrolls
   * sideways, and only the first is acceptable.
   *
   * **The reach assertion is the one doing the work, and that is worth saying so nobody trusts the
   * other one.** Deleting `overflow-x: auto` from the scroller was measured: the page overflow stayed
   * `0` at all eight widths and only the reach assertion went red. It cannot be otherwise here —
   * the dialog panel is `width: min(100%, 720px)` so it never exceeds the viewport, and
   * `.sd-modal__body` already has `overflow-y: auto`, which makes CSS compute its `overflow-x` to
   * `auto` as well. Content inside this dialog therefore has nowhere to push the page to.
   *
   * So the page check is kept as the cheap guard it is — it would catch a future change that let the
   * dialog escape its panel — and the real question is answered by scrolling the container to its
   * end and asking where the furthest cell actually landed. That is measured off the screen rather
   * than read back off a declaration, which is the only version of this that can fail.
   */
  for (const width of BREAKPOINTS) {
    test(`${width}px: the table scrolls inside the dialog and the page does not`, async ({
      page,
    }) => {
      await page.setViewportSize({ width, height: 900 });
      await page.goto(PRODUCT);
      await page.getByRole('button', TRIGGER).click();
      await expect(page.getByRole('dialog')).toBeVisible();

      // The cheap guard. See the note above: it cannot fail on this content alone.
      expect(await horizontalOverflow(page), `the page scrolls sideways at ${width}px`).toBe(0);

      // Scroll the container to its right-hand end, then ask where the last cell actually is.
      const reach = await page.locator('.guide__scroll').evaluate((scroller: HTMLElement) => {
        scroller.scrollLeft = scroller.scrollWidth;

        const rows = scroller.querySelectorAll('tbody tr');
        const cells = rows[rows.length - 1].querySelectorAll('th, td');
        const last = cells[cells.length - 1].getBoundingClientRect();
        const box = scroller.getBoundingClientRect();

        return {
          text: cells[cells.length - 1].textContent?.trim() ?? '',
          scrollable: scroller.scrollWidth > scroller.clientWidth,
          // Half a pixel of slack for sub-pixel layout, and no more.
          inside: last.right <= box.right + 0.5 && last.left >= box.left - 0.5,
        };
      });

      expect(reach.text).toBe('115-121');
      expect(reach.inside, `the last measurement is unreachable at ${width}px`).toBe(true);

      // Deliberately no "and it must be scrollable" here. Whether this table overflows is a fact
      // about the content, not about the breakpoint: four columns of short measurements fit from
      // 375 px up, and at 320 the real table is 246 px against about 254 px of dialog — it fits by
      // eight pixels, which moves with font loading. `the scroller carries a table no phone can
      // fit` below proves the mechanism with content that cannot fit under any metrics.
    });
  }

  /**
   * The scroller doing its actual job, on a seven-column guide the stub serves for product 2.
   *
   * Content that cannot fit any phone under any font, rather than the seeded table that fits 320 px
   * by eight pixels — see `WIDE_GUIDE` in the stub. This is the test that would fail if the table
   * were clipped, crushed into three-line headings, or allowed to push the page sideways.
   *
   * Phone widths only, and measured rather than assumed: seven columns **do** fit the 720 px dialog
   * panel from 768 px up, and an earlier version of this asserted 768 and failed there with
   * "nothing to scroll". A test that demands a scrollbar where the content fits is demanding a
   * worse layout.
   */
  for (const width of [320, 375, 390] as const) {
    test(`${width}px: the scroller carries a table no phone can fit`, async ({ page }) => {
      await page.setViewportSize({ width, height: 900 });
      await page.goto(WIDE_PRODUCT);
      await page.getByRole('button', { name: 'Wide size guide', exact: true }).click();
      await expect(page.getByRole('dialog')).toBeVisible();

      expect(await horizontalOverflow(page), `the page scrolls sideways at ${width}px`).toBe(0);

      const reach = await page.locator('.guide__scroll').evaluate((scroller: HTMLElement) => {
        const before = scroller.scrollWidth > scroller.clientWidth;
        scroller.scrollLeft = scroller.scrollWidth;

        const cells = scroller.querySelectorAll('tbody tr:last-child th, tbody tr:last-child td');
        const last = cells[cells.length - 1].getBoundingClientRect();
        const box = scroller.getBoundingClientRect();

        return {
          scrollable: before,
          moved: scroller.scrollLeft > 0,
          text: cells[cells.length - 1].textContent?.trim() ?? '',
          inside: last.right <= box.right + 0.5 && last.left >= box.left - 0.5,
        };
      });

      expect(reach.scrollable, `nothing to scroll at ${width}px, so a column was lost`).toBe(true);
      expect(reach.moved).toBe(true);
      expect(reach.text).toBe('38');
      expect(reach.inside, `the last measurement is unreachable at ${width}px`).toBe(true);
    });
  }

  /** A pointer user drags the table; a keyboard user has to be given the same reach. */
  test('the table scroller is reachable by keyboard', async ({ page }) => {
    await page.setViewportSize({ width: 320, height: 900 });
    await page.goto(WIDE_PRODUCT);
    await page.getByRole('button', { name: 'Wide size guide', exact: true }).click();

    const scroller = page.locator('.guide__scroll');
    await expect(scroller).toBeVisible();
    await expect(scroller).toHaveAttribute('tabindex', '0');

    await scroller.focus();
    await expect(scroller).toBeFocused();

    // ArrowRight, not End: End scrolls a container to its *bottom*, so it moved nothing here and
    // the assertion failed for a reason that had nothing to do with reach.
    for (let press = 0; press < 10; press++) {
      await page.keyboard.press('ArrowRight');
    }

    expect(
      await scroller.evaluate((element: HTMLElement) => element.scrollLeft),
      'the keyboard cannot reach the columns a pointer can drag to',
    ).toBeGreaterThan(0);
  });

  /**
   * Server-rendered, so the guide works before any script has run — which is the other half of why
   * the content travels with the product payload rather than being fetched when the dialog opens.
   */
  test('the measurements are in the server-rendered HTML', async ({ request }) => {
    const html = await (await request.get(PRODUCT)).text();

    expect(html).toContain('size guide');
    expect(html).toContain('107-113');
  });

  /** A category with no guide set: the trigger is not there at all (stub sends null on every 4th). */
  test('offers no trigger for a product whose category has no guide', async ({ page }) => {
    await page.goto('/product/test-product-4');
    await expect(page.locator('h1')).toBeVisible();

    await expect(page.locator('.pdp__guide')).toHaveCount(0);
    // Loose on purpose here: no dialog is open, so nothing else can match, and this would catch a
    // trigger that had been renamed as well as one that had been removed.
    await expect(page.getByRole('button', { name: /size guide/i })).toHaveCount(0);
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
  /**
   * The quantity group is three controls edge to edge with no gap, so each one's right-hand ring
   * lands underneath the next sibling. Aiming at `.qty__step:not(:disabled)` and taking
   * `.first()` was aiming at the **plus** — the last child, the only one of the three whose ring
   * could not be covered, and so the only one that could not fail. The box and the minus are
   * listed explicitly, and `quantity` puts the page in the state where the minus is live.
   */
  const FOCUSABLE = [
    { name: 'a colour swatch', selector: '.swatch' },
    { name: 'a size button', selector: '.size:not(:disabled)' },
    { name: 'the wishlist button', selector: '.pdp__wish' },
    // With no size chosen the stepper's ceiling is one and both its buttons are disabled, so
    // there is nothing to focus. A size is the state a shopper is in when they use it anyway.
    { name: 'the quantity box', selector: '#pdp-quantity', url: SIZED },
    { name: 'the quantity minus', selector: '.qty__step:first-child', url: SIZED, quantity: 2 },
    { name: 'the quantity plus', selector: '.qty__step:last-child', url: SIZED },
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

      // Some controls only come alive in a particular state — the minus is disabled at one.
      if ('quantity' in control) {
        const box = page.locator('#pdp-quantity');
        await box.fill(String(control.quantity));
        await expect(box).toHaveValue(String(control.quantity));
      }

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

  /**
   * The browser scrolls a focused element to the very top of the viewport, which on this site is
   * underneath the sticky header (GLB-04): 118 px of the zoom button sat behind it. The fix is
   * `scroll-padding-top` on `html`, which belongs to the shell rather than to this page, but it
   * is measurable here.
   *
   * **At every width, not at one.** The first version of this ran only at 1280×800 — the single
   * width where the value already worked — which is the third time in this task a check has been
   * pointed somewhere it could not fail. The header is *tallest on a phone*, because the
   * announcement bar wraps: 170 px at 320, against 118 at 1280.
   */
  for (const width of BREAKPOINTS) {
    test(`${width}px: a focused control is not scrolled under the sticky header`, async ({
      page,
    }) => {
      await page.setViewportSize({ width, height: 800 });
      await page.goto(PRODUCT);
      await recorded(page, 'test-product-1');

      const measured = await page.evaluate(() => {
        const header = document.querySelector('header');
        const target = document.querySelector<HTMLElement>('.gallery__open');
        if (!header || !target) {
          return null;
        }

        target.scrollIntoView({ block: 'start', behavior: 'instant' });
        return {
          headerHeight: Math.round(header.getBoundingClientRect().height),
          hidden: Math.round(
            header.getBoundingClientRect().bottom - target.getBoundingClientRect().top,
          ),
        };
      });

      // Reported rather than just asserted, so a failure says how tall the header actually was.
      expect(measured, 'the page did not render a header and a zoom button').not.toBeNull();
      expect(
        measured!.hidden,
        `${measured!.headerHeight}px of header at ${width}px wide`,
      ).toBeLessThanOrEqual(0);
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

  /**
   * The mark has to be *visible*, on every colour it can land on.
   *
   * The first version of this test asserted `::after { content }` was not "none" — that the mark
   * was *specified* — on a single stub colourway, and it would have passed unchanged while the
   * slash was painted in a grey that scored 1.08:1 against Olive Green. Specified is not the same
   * as visible, the same mistake the focus-ring assertion made.
   *
   * So this reads the colour the browser actually resolved for the slash (Chromium resolves
   * `currentColor` inside a computed gradient, which is what makes this a measurement rather than
   * a restatement of the stylesheet), reads the colour actually resolved for the dot under it,
   * and computes the WCAG 2.1 contrast ratio between the two — over all sixteen colourways the
   * demo catalogue seeds, not one. 3:1 is the non-text minimum (WCAG 2.2 AA 1.4.11).
   */
  test('the sold-out mark clears 3:1 on every seeded colourway', async ({ page }) => {
    await page.goto('/product/test-product-11');
    await expect(page.locator('h1')).toBeVisible();

    const measured = await page.evaluate(() => {
      const channel = (value: number) => {
        const part = value / 255;
        return part <= 0.04045 ? part / 12.92 : Math.pow((part + 0.055) / 1.055, 2.4);
      };

      const luminance = (rgb: number[]) =>
        0.2126 * channel(rgb[0]) + 0.7152 * channel(rgb[1]) + 0.0722 * channel(rgb[2]);

      const contrast = (a: number[], b: number[]) => {
        const [high, low] = [luminance(a), luminance(b)].sort((x, y) => y - x);
        return (high + 0.05) / (low + 0.05);
      };

      /** Every rgb()/rgba() in a value, dropping the fully transparent stops. */
      const opaque = (value: string): number[][] =>
        [...value.matchAll(/rgba?\(([^)]+)\)/g)]
          .map((match) => match[1].split(/[,/]/).map((part) => parseFloat(part.trim())))
          .filter((parts) => parts.length < 4 || parts[3] > 0);

      return [...document.querySelectorAll<HTMLElement>('.swatch__dot')].map((dot) => {
        const after = getComputedStyle(dot, '::after');
        const slash = opaque(after.backgroundImage)[0] ?? null;
        const behind = opaque(getComputedStyle(dot).backgroundColor)[0] ?? [255, 255, 255];

        return {
          name: dot.closest('button')?.getAttribute('aria-label') ?? '?',
          marked: after.content !== 'none',
          slash,
          ratio: slash ? Math.round(contrast(slash, behind) * 100) / 100 : 0,
        };
      });
    });

    // The palette really is all of it, and every swatch really is marked.
    expect(measured).toHaveLength(16);
    expect(measured.filter((swatch) => !swatch.marked)).toEqual([]);

    const failing = measured
      .filter((swatch) => swatch.ratio < 3)
      .map((swatch) => `${swatch.name}: ${swatch.ratio}:1`);

    expect(failing).toEqual([]);
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
