import AxeBuilder from '@axe-core/playwright';
import { Locator, Page, expect, test } from '@playwright/test';

import { apiURL } from '../playwright.config';
import { edgesWithoutRing, horizontalOverflow, undersized } from './support/measure';

/**
 * Browser checks for Quick View and the card's Add to Bag (QV-01, CARD-01, W2-04).
 *
 * `ui/quick-view/quick-view.spec.ts` and `ui/product-card/product-card.spec.ts` already pin what the
 * markup is, which variant the dialog settles on, what the bag is asked for and every failure path,
 * in jsdom. What needs a real browser is everything jsdom has no opinion about:
 *
 * - **the layout**, at eight real viewports, including the one thing docs/04 §4 decision 5 is
 *   actually about — small icon buttons on the photo on a phone, a labelled pair on hover on a
 *   desktop, which is two media queries and a `hover` capability query jsdom cannot evaluate;
 * - **the focus model**, which is the whole reason a dialog is hard: focus in, Tab trapped, Escape
 *   out, focus back on the trigger that opened it;
 * - **the hand-off to the mini bag**, which is the one genuinely new interaction in this task: a
 *   successful add closes this dialog *and* opens a drawer, so two focus traps and two claims on the
 *   `sd-no-scroll` class have to settle in the right order. jsdom can see the signals move; only a
 *   browser can say where focus ended up and whether the page behind can still scroll;
 * - **axe**, over a dialog that does not exist until something opens it.
 *
 * The catalogue and the cart come from `support/catalog-api-stub.mjs`. Worth knowing before reading
 * the assertions: **every product the stub serves has three sizes**, so the single-variant shortcut
 * the card's Add to Bag can take is unreachable here by construction and is pinned in the unit suite
 * instead. What this file proves about that trigger is the case a shopper will actually meet — that
 * it opens the dialog on the size list and adds nothing behind their back.
 */

/** docs/04 §4 and the project's non-negotiable list. */
const BREAKPOINTS = [320, 375, 390, 768, 1024, 1280, 1440, 1920] as const;

/** Below this the card shows icon buttons; at it and above, the labelled pair (docs/04 §4). */
const DESKTOP_ACTIONS = 1024;

const LISTING = '/women/jeans';

/**
 * The browser's own `/api` calls — the variant fetch and the bag — go to the stub, fetched from the
 * test process so the bag cookie round-trips to the page as same-origin.
 */
async function useStubApi(page: Page): Promise<void> {
  await page.route('**/api/**', async (route) => {
    const url = new URL(route.request().url());
    const response = await route.fetch({ url: `${apiURL}${url.pathname}${url.search}` });
    await route.fulfill({ response });
  });
}

type Trigger = 'card-quick-view' | 'card-add-to-bag';

/**
 * Opens the dialog from a card and waits until it is really there.
 *
 * **Why the click is in the poll.** `page.goto` resolves on `load`, which is before Angular has
 * claimed the server's DOM — and both of these triggers are *server-rendered*, so an actionability
 * check passes on a button whose handler does not exist yet. `withEventReplay()` is on and usually
 * replays such a click, but "usually" is not a thing to hang a focus test on, and the honest gate is
 * the one the project learned twice over: wait for **the page's own state to have been applied**,
 * never for markup the server already sent. The dialog existing is that state. Re-clicking is safe
 * because the trigger sets a signal to a value rather than toggling it.
 */
async function openQuickView(
  page: Page,
  options: { nth?: number; from?: Trigger } = {},
): Promise<{ trigger: Locator; dialog: Locator }> {
  const trigger = page.locator(`[data-testid="${options.from ?? 'card-quick-view'}"]`).nth(
    options.nth ?? 0,
  );
  const dialog = page.locator('#quick-view');

  await expect
    .poll(
      async () => {
        if (!(await dialog.isVisible())) {
          await trigger.click();
        }

        return dialog.isVisible();
      },
      { timeout: 15_000 },
    )
    .toBe(true);

  // The variants arrive over the network, so nothing about the choice is on screen until they do.
  await expect(dialog.locator('[data-testid="quick-view-add"]')).toBeAttached();

  return { trigger, dialog };
}

/** True once focus is somewhere inside the element the selector names. */
async function focusIsInside(page: Page, selector: string): Promise<boolean> {
  return page.evaluate((target) => {
    const element = document.querySelector(target);
    return !!element && !!document.activeElement && element.contains(document.activeElement);
  }, selector);
}

/**
 * Horizontal overflow **with a dialog open**, which `horizontalOverflow` alone cannot answer.
 *
 * An open dialog puts `sd-no-scroll` on the body, and `overflow: hidden` hides exactly the symptom
 * that function measures. So this looks in the two places the overflow can actually be: the panel
 * sticking out past the viewport, and the panel's own scrolling body having content wider than
 * itself. The document is measured too, so a regression that does reach it is still reported.
 */
async function dialogOverflow(page: Page): Promise<Record<string, number>> {
  return page.evaluate(() => {
    const panel = document.querySelector<HTMLElement>('#quick-view');
    const body = panel?.querySelector<HTMLElement>('.sd-modal__body');
    const box = panel?.getBoundingClientRect();
    const root = document.documentElement;

    return {
      document: root.scrollWidth - root.clientWidth,
      pastLeftEdge: box ? Math.round(Math.max(0, -box.x)) : -1,
      pastRightEdge: box ? Math.round(Math.max(0, box.right - root.clientWidth)) : -1,
      insidePanel: body ? body.scrollWidth - body.clientWidth : -1,
    };
  });
}

/** Serious and critical only, which is the gate `docs/06` §3 states (and `accessibility.e2e.ts`). */
async function violations(page: Page, within?: string) {
  let builder = new AxeBuilder({ page }).withTags([
    'wcag2a',
    'wcag2aa',
    'wcag21a',
    'wcag21aa',
    'wcag22aa',
  ]);
  if (within) {
    builder = builder.include(within);
  }

  const results = await builder.analyze();

  return results.violations
    .filter((violation) => violation.impact === 'serious' || violation.impact === 'critical')
    .map((violation) => ({
      id: violation.id,
      impact: violation.impact,
      help: violation.help,
      nodes: violation.nodes.map((node) => node.target.join(' ')),
    }));
}

test.beforeEach(async ({ page }) => {
  await useStubApi(page);
});

test.describe('the triggers on the card (CARD-01, docs/04 §4 decision 5)', () => {
  test('the HTML a crawler receives has both triggers and no dialog at all', async ({ request }) => {
    const html = await (await request.get(LISTING)).text();

    expect(html).toContain('data-testid="card-quick-view"');
    expect(html).toContain('data-testid="card-add-to-bag"');
    // The dialog is created by a press, so a server render must not carry twenty-four of them.
    expect(html).not.toContain('id="quick-view"');
    expect(html).not.toContain('Close quick view');
  });

  for (const width of BREAKPOINTS) {
    test(`${width}px: both triggers are 44px targets on the photograph`, async ({ page }) => {
      await page.setViewportSize({ width, height: 900 });
      await page.goto(LISTING);
      await expect(page.locator('.product-grid > li').first()).toBeVisible();

      // `undersized` filters on having a client box, which an opacity-0 control still has - so the
      // desktop pair is measured whether or not it is revealed, which is what we want.
      expect(
        await undersized(page, '[data-testid="card-quick-view"], [data-testid="card-add-to-bag"]'),
      ).toEqual([]);

      // From 1024 px the pair is revealed on hover, and *while hidden it is translated down 6 px* —
      // so measuring its geometry without hovering measures the animation's start frame and reports
      // it hanging below the photograph. The hovered position is the one a shopper sees.
      if (width >= DESKTOP_ACTIONS) {
        await page.locator('sd-product-card .card').first().hover();
        await expect
          .poll(() =>
            page.evaluate(
              () => getComputedStyle(document.querySelector('.card__actions')!).transform,
            ),
          )
          .toBe('none');
      }

      const layout = await page.evaluate(() => {
        const card = document.querySelector('sd-product-card .card')!;
        const media = card.querySelector('.card__media')!.getBoundingClientRect();
        const actions = card.querySelector('.card__actions')!.getBoundingClientRect();

        return {
          // Inside the photograph, which is the whole of decision 5.
          insideMedia:
            actions.bottom <= Math.ceil(media.bottom) && actions.right <= Math.ceil(media.right),
          actionsHeight: Math.round(actions.height),
          // The heart and the pair must not land on top of each other.
          clearOfHeart:
            actions.top >= Math.floor(card.querySelector('.card__wish')!.getBoundingClientRect().bottom),
        };
      });

      expect(layout.insideMedia).toBe(true);
      expect(layout.clearOfHeart).toBe(true);
      // Stacked on a phone (two 44 px buttons), a single row on a desktop.
      expect(layout.actionsHeight).toBe(width < DESKTOP_ACTIONS ? 88 : 44);
      expect(await horizontalOverflow(page)).toBe(0);
    });
  }

  /**
   * The **accessible name** as the browser computes it, which is not the same thing as
   * `textContent`: the name is built from two sibling spans, and the unit suite can only read the
   * concatenation. A listing holds twenty-four cards, so a screen-reader user who hears twenty-four
   * buttons called "Quick view" has been told nothing about which one they are on.
   */
  test('each trigger’s accessible name carries its own product', async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 844 });
    await page.goto(LISTING);
    await expect(page.locator('.product-grid > li').first()).toBeVisible();

    await expect(
      page.getByRole('button', { name: 'Quick view Test Product 1', exact: true }),
    ).toHaveCount(1);
    await expect(
      page.getByRole('button', { name: 'Add to bag Test Product 1', exact: true }),
    ).toHaveCount(1);
    await expect(
      page.getByRole('button', { name: 'Quick view Test Product 24', exact: true }),
    ).toHaveCount(1);
  });

  test('on a phone the pair is simply there, with nothing to hover', async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 844 });
    await page.goto(LISTING);
    await expect(page.locator('.product-grid > li').first()).toBeVisible();

    const opacity = await page.evaluate(
      () => getComputedStyle(document.querySelector('.card__actions')!).opacity,
    );

    expect(opacity).toBe('1');
    await expect(page.locator('[data-testid="card-quick-view"]').first()).toBeVisible();
  });

  /**
   * On a desktop the pair is revealed on hover, which is the style tile's design — and a keyboard
   * has no pointer, so `:focus-within` has to reveal it too or the controls are unreachable.
   */
  test('on a desktop the pair appears on hover, and on focus for a keyboard', async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await page.goto(LISTING);
    await expect(page.locator('.product-grid > li').first()).toBeVisible();

    const actions = page.locator('.card__actions').first();
    const opacity = () => actions.evaluate((element) => getComputedStyle(element).opacity);

    expect(await opacity()).toBe('0');

    await page.locator('sd-product-card .card').first().hover();
    await expect.poll(opacity).toBe('1');

    // Away from the card, then in by keyboard.
    await page.locator('h1').hover();
    await expect.poll(opacity).toBe('0');

    await page.locator('[data-testid="card-quick-view"]').first().focus();
    await expect.poll(opacity).toBe('1');
  });

  /**
   * #38's lesson, applied to the two controls this task adds: a ring is only there if it reaches
   * the screen, and `edgesWithoutRing` reads that off pictures rather than off a declaration.
   */
  test('both triggers keep a visible focus ring on every edge', async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 844 });
    await page.goto(LISTING);
    await expect(page.locator('.product-grid > li').first()).toBeVisible();

    for (const testId of ['card-quick-view', 'card-add-to-bag'] as const) {
      const control = page.locator(`[data-testid="${testId}"]`).first();
      await control.evaluate((element: HTMLElement) => element.focus({ preventScroll: true }));
      await page.keyboard.press('Shift+Tab');
      await page.keyboard.press('Tab');
      await expect(control).toBeFocused();

      expect(await edgesWithoutRing(page, control), testId).toEqual([]);
    }
  });
});

test.describe('the dialog (QV-01)', () => {
  test('opens named after the product, with focus inside it', async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await page.goto(LISTING);

    const { dialog } = await openQuickView(page);

    await expect(dialog).toHaveAttribute('aria-modal', 'true');
    await expect(dialog.locator('.sd-modal__title')).toHaveText('Test Product 1');
    await expect(page.locator('[data-testid="card-quick-view"]').first()).toHaveAttribute(
      'aria-expanded',
      'true',
    );

    await expect.poll(() => focusIsInside(page, '#quick-view')).toBe(true);

    // The page behind an open dialog does not scroll.
    expect(
      await page.evaluate(() => document.body.classList.contains('sd-no-scroll')),
    ).toBe(true);
  });

  test('keeps Tab inside the panel', async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await page.goto(LISTING);
    await openQuickView(page);

    for (let press = 0; press < 40; press++) {
      await page.keyboard.press('Tab');
      expect(await focusIsInside(page, '#quick-view'), `focus left after ${press + 1} tabs`).toBe(
        true,
      );
    }
  });

  test('Escape closes it and puts focus back on the trigger that opened it', async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await page.goto(LISTING);

    const { trigger, dialog } = await openQuickView(page, { nth: 2 });
    await expect.poll(() => focusIsInside(page, '#quick-view')).toBe(true);

    await page.keyboard.press('Escape');

    await expect(dialog).toBeHidden();
    await expect(trigger).toBeFocused();
    await expect(trigger).toHaveAttribute('aria-expanded', 'false');
    expect(await page.evaluate(() => document.body.classList.contains('sd-no-scroll'))).toBe(false);
  });

  test('the scrim closes it too', async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await page.goto(LISTING);

    const { trigger, dialog } = await openQuickView(page);
    await page.locator('.sd-modal__scrim').click({ position: { x: 5, y: 5 } });

    await expect(dialog).toBeHidden();
    await expect(trigger).toBeFocused();
  });

  /**
   * Lesson L3: reduced motion puts every transition on a 0.01 ms clock, and a browser refuses to
   * focus an element that is still hidden in that instant. The modal is created when it opens rather
   * than revealed, so this should be free — which is precisely why it is measured.
   */
  test('it still takes focus under reduced motion', async ({ page }) => {
    await page.emulateMedia({ reducedMotion: 'reduce' });
    await page.setViewportSize({ width: 390, height: 844 });
    await page.goto(LISTING);

    await openQuickView(page);
    await expect.poll(() => focusIsInside(page, '#quick-view')).toBe(true);
  });

  test('the price follows the size, because a size can be priced apart', async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await page.goto(LISTING);

    const { dialog } = await openQuickView(page);

    // The stub prices the third size 500 higher, as `ProductVariants.PriceOverride` does.
    await expect(dialog.locator('.qv__from')).toBeVisible();
    const band = await dialog.locator('.price__now').textContent();

    await dialog.locator('[data-testid="quick-view-size"]').nth(2).click();

    await expect(dialog.locator('.qv__from')).toBeHidden();
    await expect(dialog.locator('.price__now')).not.toHaveText(band ?? '');
    await expect(dialog.locator('.price__now')).toContainText('Rs.');
  });

  test('changing the colourway changes the photographs', async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await page.goto(LISTING);

    const { dialog } = await openQuickView(page);
    const photo = dialog.locator('.qv__photo');
    const first = await photo.getAttribute('src');

    const swatches = dialog.locator('.sd-swatch');
    await expect(swatches).not.toHaveCount(0);
    await swatches.nth(1).click();

    await expect.poll(() => photo.getAttribute('src')).not.toBe(first);
  });

  test('"View full details" carries the choice into the product page', async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await page.goto(LISTING);

    const { dialog } = await openQuickView(page);
    await dialog.locator('[data-testid="quick-view-size"]').nth(0).click();

    await dialog.locator('.qv__full').click();

    await expect(page).toHaveURL(/\/product\/test-product-1\?colour=\d+&size=\d+/);
    // The product page opens on the same choice, not on a blank one.
    await expect(page.locator('.pdp__sizes .sd-size.is-selected')).toHaveCount(1);
    // And the dialog went with the page, scroll lock and all.
    await expect(page.locator('#quick-view')).toBeHidden();
    expect(await page.evaluate(() => document.body.classList.contains('sd-no-scroll'))).toBe(false);
  });
});

/**
 * **The hand-off, which is the only genuinely new interaction in QV-01.**
 *
 * A successful add makes the mini bag open itself (CART-02), so for one change-detection pass there
 * are two dialogs, two focus traps and two claims on `sd-no-scroll`. The dialog takes itself away on
 * its own add precisely so that never happens — and the ordering is the kind of thing that works in
 * jsdom and does not in a browser, so it is measured here.
 */
test.describe('adding to the bag from the dialog', () => {
  test('closes the dialog, opens the mini bag and leaves focus in it', async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await page.goto(LISTING);

    // A product of its own, because the suite is `fullyParallel` and the stub's bag is per-cookie
    // but its stock switch is not (see `bag.e2e.ts`).
    const { dialog } = await openQuickView(page, { nth: 3 });
    await dialog.locator('[data-testid="quick-view-size"]').nth(0).click();

    const add = dialog.locator('[data-testid="quick-view-add"]');
    await expect(add).toBeEnabled();
    await add.click();

    await expect(page.locator('#quick-view')).toBeHidden();
    await expect(page.locator('sd-mini-bag .mini-bag__line')).toHaveCount(1);
    await expect(page.locator('[data-testid="bag-toggle"] .count')).toHaveText('1');

    // Focus ended up in the drawer rather than being handed back to a card behind a scrim.
    await expect.poll(() => focusIsInside(page, '#mini-bag')).toBe(true);
    // And the page behind is still frozen, because a dialog is still open — just a different one.
    expect(await page.evaluate(() => document.body.classList.contains('sd-no-scroll'))).toBe(true);

    // One of the mini bag's two live regions holds the sentence - which one depends on whether the
    // drawer was open when it landed, and `mini-bag.spec.ts` is where that invariant is pinned. What
    // matters here is that a screen reader was told at all.
    await expect(page.locator('sd-mini-bag')).toContainText('added to your bag');
  });

  /**
   * **The bag gets what was chosen, not the default.** The card's own `Add to Bag` cannot know a
   * variant, so the one thing that must be true of this whole feature is that the line the shop
   * records is the colourway and size the shopper settled on — so the choice here is deliberately
   * *not* the one the dialog opens with. Product 2 is the stub's three-colourway product.
   */
  test('the line in the bag is the colourway and size that were chosen, not the default', async ({
    page,
  }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await page.goto(LISTING);

    const { dialog } = await openQuickView(page, { nth: 1 });

    const swatches = dialog.locator('.sd-swatch');
    await expect(swatches).toHaveCount(3);
    await swatches.nth(2).click();
    await dialog.locator('[data-testid="quick-view-size"]').nth(0).click();

    // The dialog says what it is about to send, and "Ecru" is the third colourway rather than the
    // Indigo it opened on.
    await expect(dialog.locator('#qv-colour-label')).toHaveText(/Colour:\s*Ecru/);
    await expect(dialog.locator('#qv-size-label')).toHaveText(/Size:\s*S/);

    await dialog.locator('[data-testid="quick-view-add"]').click();
    await expect(page.locator('sd-mini-bag .mini-bag__line')).toHaveCount(1);

    await expect(page.locator('sd-mini-bag .mini-bag__meta').first()).toHaveText('Ecru · S');
  });

  /**
   * The card's Add to Bag on a product that needs a size. Every stub product has three, so this is
   * the only shape reachable here — and it is the one that matters: the card must not guess.
   */
  test('the card’s Add to Bag opens the dialog on the size list and adds nothing by itself', async ({
    page,
  }) => {
    await page.setViewportSize({ width: 390, height: 844 });
    await page.goto(LISTING);

    const posts: string[] = [];
    page.on('request', (request) => {
      if (request.method() === 'POST' && request.url().includes('/api/cart/items')) {
        posts.push(request.url());
      }
    });

    const { trigger, dialog } = await openQuickView(page, { nth: 7, from: 'card-add-to-bag' });

    await expect(trigger).toHaveAttribute('aria-expanded', 'true');
    await expect(dialog.locator('[data-testid="quick-view-size"]')).toHaveCount(3);
    await expect(dialog.locator('[data-testid="quick-view-add"]')).toBeDisabled();
    await expect(dialog).toContainText('Choose a size to add this to your bag');
    await expect(page.locator('[data-testid="bag-toggle"] .count')).toHaveCount(0);

    expect(posts).toEqual([]);
  });
});

test.describe('the dialog at every viewport (GLB-12)', () => {
  for (const width of BREAKPOINTS) {
    test(`${width}px: nothing scrolls sideways and the panel fits`, async ({ page }) => {
      await page.setViewportSize({ width, height: 900 });
      await page.goto(LISTING);

      const { dialog } = await openQuickView(page);
      // The wholly sold-out product wears all sixteen seeded colourways, which is the widest the
      // swatch row ever gets; this one has two, so both shapes are covered by the pair of tests.
      await expect(dialog.locator('.sd-swatch').first()).toBeVisible();

      expect(await dialogOverflow(page)).toEqual({
        document: 0,
        pastLeftEdge: 0,
        pastRightEdge: 0,
        insidePanel: 0,
      });
    });
  }

  test('320px: sixteen colourways still fit, and every control is a 44px target', async ({
    page,
  }) => {
    await page.setViewportSize({ width: 320, height: 720 });
    // Product 11 is the stub's wholly sold-out one and carries the whole seeded palette.
    await page.goto('/product/test-product-10');
    await expect(page.locator('.pdp__row').first()).toBeVisible();

    const { dialog } = await openQuickView(page);
    await expect(dialog.locator('.sd-swatch').first()).toBeVisible();

    expect(await dialogOverflow(page)).toEqual({
      document: 0,
      pastLeftEdge: 0,
      pastRightEdge: 0,
      insidePanel: 0,
    });

    // Every control the dialog puts on a 320 px screen. Measured per clause, because `undersized`
    // reports an empty list for a selector that matched nothing (issue #80).
    for (const clause of [
      '#quick-view .sd-modal__close',
      '#quick-view .sd-swatch',
      '#quick-view [data-testid="quick-view-size"]',
      '#quick-view .qty__step',
      '#quick-view .qty__input',
      '#quick-view [data-testid="quick-view-add"]',
      '#quick-view .qv__full',
    ]) {
      const rendered = await page.locator(clause).count();
      expect(rendered, `nothing matches "${clause}", so measuring it could not fail`).toBeGreaterThan(0);
      expect(await undersized(page, clause), clause).toEqual([]);
    }
  });
});

/**
 * axe over the dialog, which a scan of a page at rest can never see: it does not exist until
 * something opens it, exactly as the mega menu and the mini bag do not.
 */
test.describe('axe over the open dialog (GLB-12, #65)', () => {
  for (const width of [390, 1280] as const) {
    test(`${width}px: no serious or critical violation`, async ({ page }) => {
      await page.setViewportSize({ width, height: 900 });
      await page.goto(LISTING);
      await openQuickView(page);

      const found = await violations(page);
      expect(found, JSON.stringify(found, null, 2)).toEqual([]);
    });
  }

  /** The listing behind it too: forty-eight new buttons is the biggest change this task makes. */
  test('a listing carrying the new triggers has no serious or critical violation', async ({
    page,
  }) => {
    await page.setViewportSize({ width: 390, height: 844 });
    await page.goto(LISTING);
    await expect(page.locator('.product-grid > li').first()).toBeVisible();

    const found = await violations(page);
    expect(found, JSON.stringify(found, null, 2)).toEqual([]);
  });

  /**
   * With a size chosen, which is a different document: the reason line goes away, the price block
   * changes colour on a discounted variant and Add to Bag stops being a disabled grey button —
   * `color-contrast` is a serious rule and those are its three inputs here.
   */
  test('with a size chosen it still has no serious or critical violation', async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await page.goto(LISTING);

    const { dialog } = await openQuickView(page, { nth: 9 });
    await dialog.locator('[data-testid="quick-view-size"]').nth(0).click();
    await expect(dialog.locator('[data-testid="quick-view-add"]')).toBeEnabled();

    const found = await violations(page);
    expect(found, JSON.stringify(found, null, 2)).toEqual([]);
  });
});
