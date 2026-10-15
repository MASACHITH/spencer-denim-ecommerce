import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  TestRequest,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { RESPONSE_INIT } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { BagPage } from './bag-page';
import { CartApi } from '../../core/cart/cart-api';
import { CartDto, CartLineDto, CartTotalsDto } from '../../core/cart/cart.model';
import { PriceDto } from '../../core/catalog/catalog.model';

function price(current: number, original = current): PriceDto {
  const discount = original > current ? Math.floor(((original - current) / original) * 100) : 0;
  return { current, original, discountPercent: discount, hasDiscount: discount > 0, currency: 'LKR' };
}

function line(overrides: Partial<CartLineDto> = {}): CartLineDto {
  return {
    variantId: 1,
    productId: 10,
    productName: 'Relaxed Linen Blend Shirt',
    slug: 'relaxed-linen-blend-shirt',
    sku: 'SD-1-M',
    colourId: 2,
    colourName: 'Beige',
    sizeId: 3,
    sizeLabel: 'M',
    scaleName: 'Apparel alpha',
    imageUrl: '/media/public/renditions/a/thumb.webp',
    imageAltText: 'A beige linen shirt',
    quantity: 2,
    maxQuantity: 10,
    availableQuantity: 12,
    price: price(4392, 6990),
    lineSubtotal: 8784,
    unitPriceAtAdd: 4392,
    priceChanged: false,
    status: 'Buyable',
    ...overrides,
  };
}

function totals(overrides: Partial<CartTotalsDto> = {}): CartTotalsDto {
  return {
    itemCount: 2,
    buyableItemCount: 2,
    lineCount: 1,
    itemsOriginalTotal: 13980,
    itemsSubtotal: 8784,
    savedTotal: 5196,
    currency: 'LKR',
    ...overrides,
  };
}

const EMPTY: CartDto = {
  lines: [],
  totals: {
    itemCount: 0,
    buyableItemCount: 0,
    lineCount: 0,
    itemsOriginalTotal: 0,
    itemsSubtotal: 0,
    savedTotal: 0,
    currency: 'LKR',
  },
};

describe('BagPage (CART-01)', () => {
  let fixture: ComponentFixture<BagPage>;
  let element: HTMLElement;
  let http: HttpTestingController;
  let response: ResponseInit;

  async function settle(): Promise<void> {
    fixture.detectChanges();
    await fixture.whenStable();
  }

  function create(): void {
    response = {};
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: RESPONSE_INIT, useValue: response },
      ],
    });

    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(BagPage);
    element = fixture.nativeElement as HTMLElement;
  }

  function cartRequest(): TestRequest {
    return http.expectOne((request) => request.url === CartApi.url && request.method === 'GET');
  }

  async function open(cart: CartDto): Promise<void> {
    create();
    await settle();
    cartRequest().flush(cart);
    await settle();
  }

  function text(): string {
    return element.textContent ?? '';
  }

  /** The `<dt>` of every row in the summary, so a label can be asserted present *and* absent. */
  function rowLabels(): string[] {
    return Array.from(element.querySelectorAll('.bag__sums dt')).map(
      (node) => node.textContent?.trim().replace(/\s*\(.*\)$/, '') ?? '',
    );
  }

  /**
   * The three money figures in the summary, **read back off the screen** rather than taken from the
   * fixture, so the test is about what a shopper sees. A row that is not rendered reads as 0, which is
   * what makes the no-savings case assertable with the same helper.
   */
  function sums(): { subtotal: number; savings: number; total: number } {
    const amount = (label: string): number => {
      const row = Array.from(element.querySelectorAll('.bag__sums > div')).find((node) =>
        node.querySelector('dt')?.textContent?.trim().startsWith(label),
      );
      // `Rs. 8,784` — the currency prefix carries a full stop, so stripping "everything but digits and
      // a dot" turns it into 0.8784. Match the number instead, and drop the group separators.
      const raw = (row?.querySelector('dd')?.textContent ?? '').replace('Rs.', '');
      const matched = /[\d,]+(?:\.\d+)?/.exec(raw);
      // The magnitude, not the sign: the deduction row renders a U+2212 and this is subtracted below.
      return matched ? Number(matched[0].replace(/,/g, '')) : 0;
    };

    return { subtotal: amount('Subtotal'), savings: amount('Savings'), total: amount('Total') };
  }

  beforeEach(() => localStorage.clear());

  afterEach(() => TestBed.inject(HttpTestingController).verify());

  describe('the request and the response it belongs to', () => {
    /**
     * From the constructor, not from an effect: the server render waits on this request, so the HTML a
     * shopper (and a crawler, were it allowed to look) receives carries the lines rather than a spinner.
     */
    it('asks for the bag before the first change detection', () => {
      create();
      cartRequest().flush(EMPTY);
    });

    /**
     * **The one page in the storefront whose markup is personal.** A shared cache holding it would hand
     * one shopper another shopper's bag, so the response forbids storing and varies on the cookie.
     */
    it('answers no-store, private and Vary: Cookie', async () => {
      await open(EMPTY);

      const headers = new Headers(response.headers);
      expect(headers.get('Cache-Control')).toContain('no-store');
      expect(headers.get('Cache-Control')).toContain('private');
      expect(headers.get('Vary')).toBe('Cookie');
    });

    it('asks not to be indexed, and clears the tag on the way out', async () => {
      await open(EMPTY);

      expect(document.querySelector('meta[name="robots"]')?.getAttribute('content')).toBe('noindex');

      fixture.destroy();
      expect(document.querySelector('meta[name="robots"]')).toBeNull();
    });

    it('titles the page for the shop', async () => {
      await open(EMPTY);
      expect(document.title).toContain('Your bag');
      expect(document.title).toContain('Colombo Wardrobe');
    });
  });

  describe('an empty bag', () => {
    it('says so and offers a way into the shop', async () => {
      await open(EMPTY);

      expect(text()).toContain('Your bag is empty');
      expect(element.querySelector<HTMLAnchorElement>('.bag__empty a')?.textContent).toContain(
        'Start shopping',
      );
      expect(element.querySelectorAll('.bag__line')).toHaveLength(0);
    });
  });

  describe('a bag with things in it', () => {
    it('shows each line with its colour, size, unit price and line total', async () => {
      await open({ lines: [line()], totals: totals() });

      const row = element.querySelector('.bag__line') as HTMLElement;
      expect(row.textContent).toContain('Relaxed Linen Blend Shirt');
      expect(row.textContent).toContain('Beige');
      expect(row.textContent).toContain('M');
      expect(row.textContent).toContain('Rs. 4,392 each');
      expect(row.querySelector('.bag__total')?.textContent?.trim()).toBe('Rs. 8,784');
    });

    it('links the name and the photograph to the product, and does not make the photograph a second stop', async () => {
      await open({ lines: [line()], totals: totals() });

      expect(element.querySelector<HTMLAnchorElement>('.bag__name')?.getAttribute('href')).toBe(
        '/product/relaxed-linen-blend-shirt',
      );

      // The thumbnail repeats the name's link, so it is hidden from the tab order and the
      // accessibility tree rather than being a duplicate stop (the product card does the same).
      const thumb = element.querySelector('.bag__thumb') as HTMLElement;
      expect(thumb.getAttribute('tabindex')).toBe('-1');
      expect(thumb.getAttribute('aria-hidden')).toBe('true');
    });

    it('shows the struck-through original price only when there is a discount', async () => {
      await open({ lines: [line()], totals: totals() });
      expect(element.querySelector('.bag__was')?.textContent).toContain('Rs. 6,990');

      await open({ lines: [line({ price: price(4392) })], totals: totals({ savedTotal: 0 }) });
      expect(element.querySelector('.bag__was')).toBeNull();
    });

    /**
     * **The ladder has to add up, and three `toContain`s cannot say that.**
     *
     * This test used to assert that the three figures appeared *somewhere* in the summary, which is
     * exactly the too-weak shape that has missed shipped bugs on this project before — and it missed
     * one here. The rendered rows said `Subtotal Rs. 8,784`, `Discount −Rs. 5,196`,
     * `Total Rs. 8,784`: a deduction row between two identical numbers, because `itemsSubtotal` is
     * already net of the markdown while `savedTotal` is the markdown. Every string the old assertions
     * looked for was present.
     *
     * So the numbers are read back off the screen and checked **as a sum**, with the exact figures pinned
     * beside the identity.
     *
     * **Of the two, the figures are the stronger guard and the identity is the weakest.** Under the old ladder
     * this helper reads Subtotal as 8,784 and Savings as 0 — because the row was labelled `Discount` — so
     * `8,784 - 0 === 8,784` holds and the identity alone would have sailed straight past the bug, while the
     * pinned `subtotal: 13_980` catches it on the very first figure. The identity is here because it is the
     * property a future change can break *without* changing any of these three numbers; it is not the thing
     * standing between us and this bug. The label test below is a third independent guard, and it is valuable
     * as the third, not as the first.
     */
    it('adds up: Subtotal − Savings = Total', async () => {
      await open({ lines: [line()], totals: totals() });

      const { subtotal, savings, total } = sums();

      expect(subtotal - savings).toBe(total);
      expect({ subtotal, savings, total }).toEqual({
        subtotal: 13_980,
        savings: 5_196,
        total: 8_784,
      });
    });

    /**
     * The row is **Savings**, not Discount, and the distinction is not cosmetic: nothing here was
     * applied by a code. ADM-13's promo will add a `Discount` row of its own below this one, and two
     * rows both called Discount would be unreadable.
     */
    it('labels the deduction Savings, because no code was applied to earn it', async () => {
      await open({ lines: [line()], totals: totals() });

      const labels = rowLabels();
      expect(labels).toContain('Savings');
      expect(labels).not.toContain('Discount');
    });

    /** With nothing marked down there is nothing to deduct, so the simple case stays simple. */
    it('hides the Savings row when there is nothing saved, and Subtotal equals Total', async () => {
      await open({
        lines: [line({ price: price(4392) })],
        totals: totals({ itemsOriginalTotal: 8784, savedTotal: 0 }),
      });

      const { subtotal, savings, total } = sums();

      expect(rowLabels()).not.toContain('Savings');
      expect(savings).toBe(0);
      expect(subtotal).toBe(total);
    });

    /**
     * The summary is the part that must not invent anything. There is no promo field (ADM-13 is not
     * built), no delivery figure (#33, and `sales.DeliveryMethodZoneRates` is #53's), and so the total
     * row says what it covers.
     */
    it('shows a delivery row that does not pretend to know the fee', async () => {
      await open({ lines: [line()], totals: totals() });

      const summary = element.querySelector('.bag__summary') as HTMLElement;
      expect(summary.textContent).toContain('Subtotal (2 items)');
      expect(summary.textContent).toContain('Calculated at checkout');
      expect(summary.textContent).toContain('Total (items only)');
    });

    it('has no promo code field, because there is nothing behind one', async () => {
      await open({ lines: [line()], totals: totals() });

      expect(element.querySelectorAll('input[type="text"]')).toHaveLength(0);
      expect(text().toLowerCase()).not.toContain('promo');
    });

    it('hides the discount row when there is no discount to report', async () => {
      await open({ lines: [line({ price: price(4392) })], totals: totals({ savedTotal: 0 }) });

      expect(element.querySelector('.bag__summary')?.textContent).not.toContain('Discount');
    });

    it('offers Remove and Move to wishlist on every line, each naming the item it acts on', async () => {
      await open({ lines: [line()], totals: totals() });

      const labels = Array.from(
        element.querySelectorAll<HTMLButtonElement>('.bag__actions button'),
      ).map((button) => button.getAttribute('aria-label'));

      expect(labels).toEqual([
        'Remove Relaxed Linen Blend Shirt, Beige, size M from your bag',
        'Move Relaxed Linen Blend Shirt, Beige, size M to your wishlist',
      ]);
    });
  });

  describe('the quantity control', () => {
    function input(): HTMLInputElement {
      return element.querySelector('.qty__input') as HTMLInputElement;
    }

    function steps(): HTMLButtonElement[] {
      return Array.from(element.querySelectorAll<HTMLButtonElement>('.qty__step'));
    }

    it('is labelled for the line it belongs to, not just "Quantity"', async () => {
      await open({ lines: [line()], totals: totals() });

      const label = element.querySelector(`label[for="bag-qty-1"]`);
      expect(label?.textContent).toContain('Relaxed Linen Blend Shirt, Beige, size M');
    });

    it('sends the new quantity when the shopper steps it up', async () => {
      await open({ lines: [line()], totals: totals() });

      steps()[1].click();
      await settle();

      const request = http.expectOne(
        (candidate) => candidate.url === `${CartApi.url}/items/1` && candidate.method === 'PATCH',
      );
      expect(request.request.body).toEqual({ quantity: 3 });
      request.flush({ lines: [line({ quantity: 3 })], totals: totals({ itemCount: 3 }) });
      await settle();
    });

    it('cannot be stepped below one or above the line ceiling', async () => {
      await open({ lines: [line({ quantity: 1, maxQuantity: 2 })], totals: totals({ itemCount: 1 }) });
      expect(steps()[0].disabled).toBe(true);
      expect(steps()[1].disabled).toBe(false);

      await open({ lines: [line({ quantity: 2, maxQuantity: 2 })], totals: totals({ itemCount: 2 }) });
      expect(steps()[1].disabled).toBe(true);
    });

    /**
     * Doc 04 §4: clamp to the maximum rather than accept a number and fail. A typed 99 against a
     * ceiling of 4 is applied as 4 and the box is rewritten, so it can never be left showing a value
     * the bag does not hold.
     */
    it('clamps a typed quantity to the ceiling and rewrites the box', async () => {
      await open({ lines: [line({ quantity: 2, maxQuantity: 4 })], totals: totals() });

      input().value = '99';
      input().dispatchEvent(new Event('change'));
      await settle();

      expect(input().value).toBe('4');
      const request = http.expectOne(
        (candidate) => candidate.url === `${CartApi.url}/items/1` && candidate.method === 'PATCH',
      );
      expect(request.request.body).toEqual({ quantity: 4 });
      request.flush({ lines: [line({ quantity: 4 })], totals: totals({ itemCount: 4 }) });
      await settle();
    });

    /**
     * Unparseable input is the shape that has already cost this project a 500 on a public endpoint. On
     * the page it must fall back to the quantity the bag already holds — the one value guaranteed valid
     * — and send nothing at all.
     */
    it.each([[''], ['abc'], ['-'], ['-3'], ['1e9'], ['0']])(
      'falls back to the quantity already in the bag for %p and sends nothing',
      async (typed) => {
        await open({ lines: [line({ quantity: 2, maxQuantity: 10 })], totals: totals() });

        input().value = typed;
        input().dispatchEvent(new Event('change'));
        await settle();

        expect(input().value).toBe('2');
        // `verify()` in afterEach is the assertion: a PATCH here would be one nobody flushed.
      },
    );

    /**
     * **A rejected change must not leave the box showing a number the bag does not hold.**
     *
     * The handler writes `input.value` by hand, which bypasses the `[value]` binding — so when the PATCH
     * fails, `line.quantity` never changes, Angular sees no change to the bound expression and never
     * rewrites the element. The box kept `4` against a bag holding `2`, and the next `+` computed from
     * the real quantity and jumped to `3`.
     */
    it('puts the box back to what the bag holds when the change is refused', async () => {
      await open({ lines: [line({ quantity: 2, maxQuantity: 10 })], totals: totals() });

      input().value = '4';
      input().dispatchEvent(new Event('change'));
      await settle();
      expect(input().value).toBe('4');

      http
        .expectOne((candidate) => candidate.method === 'PATCH')
        .flush('nope', { status: 500, statusText: 'Server Error' });
      await settle();

      expect(input().value).toBe('2');
      expect(element.querySelector('[role="alert"]')).not.toBeNull();
    });

    /**
     * Same keystroke intent, same outcome. An all-digit value past `MAX_SAFE_INTEGER` used to be *reset*
     * while `99` *clamped* — two answers to "give me as many as you have", decided by how long the
     * number was.
     */
    /**
     * A 400-digit value is deliberately **not** in this list, and the reason is worth keeping: an
     * `<input type="number">` runs the HTML value-sanitisation algorithm, which turns a number outside
     * the float range into the empty string *before* any handler sees it. The box then reads `''`, which
     * is the "no instruction" path above and resets — correctly. Asserting the clamp on it would be
     * asserting what the element does, not what this component does. Twenty digits survives sanitisation
     * and is the real boundary case.
     */
    it.each([['99'], ['1000'], ['99999999999999999999']])(
      'clamps the all-digit value %p to the ceiling rather than resetting it',
      async (typed) => {
        await open({ lines: [line({ quantity: 2, maxQuantity: 4 })], totals: totals() });

        input().value = typed;
        input().dispatchEvent(new Event('change'));
        await settle();

        expect(input().value, `typed ${typed}`).toBe('4');
        http
          .expectOne((candidate) => candidate.method === 'PATCH')
          .flush({ lines: [line({ quantity: 4, maxQuantity: 4 })], totals: totals({ itemCount: 4 }) });
        await settle();
      },
    );

    it('disables the whole control for a line that is being changed', async () => {
      await open({ lines: [line()], totals: totals() });

      steps()[1].click();
      await settle();

      expect(input().disabled).toBe(true);
      expect(steps().every((step) => step.disabled)).toBe(true);

      http
        .expectOne((candidate) => candidate.method === 'PATCH')
        .flush({ lines: [line({ quantity: 3 })], totals: totals({ itemCount: 3 }) });
      await settle();

      expect(input().disabled).toBe(false);
    });
  });

  describe('lines the shop can no longer sell', () => {
    /**
     * The line stays. A bag that silently drops an item is worse than one that explains itself — and the
     * only thing a shopper can do with it is take it out, so that is what the row offers.
     */
    it('shows a withdrawn line with no price, a reason, and no quantity control', async () => {
      await open({
        lines: [line({ status: 'Unavailable', price: null, lineSubtotal: null })],
        totals: totals({ itemCount: 2, buyableItemCount: 0, itemsSubtotal: 0, savedTotal: 0 }),
      });

      const row = element.querySelector('.bag__line') as HTMLElement;
      expect(row.classList).toContain('is-unavailable');
      expect(row.textContent).toContain('Relaxed Linen Blend Shirt');
      expect(row.textContent).toContain('no longer available');
      expect(row.textContent).not.toContain('Rs.');
      expect(row.querySelector('.qty')).toBeNull();
      expect(row.querySelector('.bag__qty-fixed')?.textContent).toContain('Qty 2');

      // Still removable, which is the whole point of keeping it on the page.
      expect(row.querySelectorAll('.bag__actions button')).toHaveLength(2);
    });

    it('says how many lines are left out of the total, and does not add them to it', async () => {
      await open({
        lines: [line(), line({ variantId: 2, status: 'OutOfStock', availableQuantity: 0 })],
        totals: totals({ itemCount: 4, buyableItemCount: 2 }),
      });

      const summary = element.querySelector('.bag__summary') as HTMLElement;
      expect(summary.textContent).toContain('1 item in your bag cannot be bought');
      expect(summary.textContent).toContain('Subtotal (2 items)');
    });

    it('reports the price-changed notice on a line that still has a price', async () => {
      await open({
        lines: [line({ priceChanged: true, unitPriceAtAdd: 3990 })],
        totals: totals(),
      });

      expect(element.querySelector('.bag__notice')?.textContent).toContain('price');
    });
  });

  describe('when the read fails', () => {
    it('says so and offers a retry on an empty bag', async () => {
      create();
      await settle();
      cartRequest().flush('nope', { status: 500, statusText: 'Server Error' });
      await settle();

      expect(element.querySelector('[role="alert"]')?.textContent).toContain('could not update');
      const retry = element.querySelector<HTMLButtonElement>('[role="alert"] button');
      expect(retry?.textContent).toContain('Try again');

      retry?.click();
      await settle();
      cartRequest().flush(EMPTY);
      await settle();
    });

    /** A bag that is already on screen keeps its lines: the alternative is telling the shopper it emptied. */
    it('keeps the lines on screen when a later read fails', async () => {
      await open({ lines: [line()], totals: totals() });

      // A quantity change that fails is the ordinary way a shopper meets this.
      element.querySelectorAll<HTMLButtonElement>('.qty__step')[1].click();
      await settle();
      http
        .expectOne((candidate) => candidate.method === 'PATCH')
        .flush('nope', { status: 500, statusText: 'Server Error' });
      await settle();

      expect(element.querySelectorAll('.bag__line')).toHaveLength(1);
      expect(element.querySelector('[role="alert"]')).not.toBeNull();
    });
  });
  /**
   * The button W3-02 added, and the rule it enforces (doc 11 §7 G-08: warn and block, never silently
   * reduce a quantity or drop a line).
   */
  describe('Proceed to checkout (W3-02)', () => {
    function checkoutLink(): HTMLAnchorElement | null {
      return element.querySelector<HTMLAnchorElement>('a.bag__checkout');
    }

    function blockedButton(): HTMLButtonElement | null {
      return element.querySelector<HTMLButtonElement>('button.bag__checkout');
    }

    it('links to /checkout when every line can be bought', async () => {
      await open({ lines: [line()], totals: totals() });

      expect(checkoutLink()?.getAttribute('href')).toBe('/checkout');
      expect(blockedButton()).toBeNull();
    });

    /**
     * **A blocked `<button>`, not a styled `<a>`.** A disabled anchor is still followable by keyboard
     * and by a crawler, and `pointer-events: none` is not a disabled control to anyone navigating with a
     * keyboard — so the two states are two elements. It is `aria-disabled`, not `disabled` (#109): see the
     * next test for why that matters.
     */
    it('is a blocked button, not a link, while a line cannot be bought', async () => {
      await open({
        lines: [line({ status: 'OutOfStock', availableQuantity: 0, price: null, lineSubtotal: null })],
        totals: totals({ buyableItemCount: 0, itemsOriginalTotal: 0, itemsSubtotal: 0, savedTotal: 0 }),
      });

      expect(checkoutLink()).toBeNull();
      expect(blockedButton()?.getAttribute('aria-disabled')).toBe('true');
      expect(text()).toContain('before you check out');
    });

    /**
     * **#109. The keyboard has to be able to reach it.** A `disabled` button is out of the tab order, so a
     * keyboard or screen-reader user could never land on it to hear the reason `aria-describedby` points
     * at, and it is also why disabling a *focused* control throws focus to `<body>`. `focus()` is the
     * observable difference: it does nothing to a `disabled` button, in jsdom and in a browser, so this
     * fails against the old markup. (Pressing it still does nothing: it has no handler and no
     * `routerLink`, which `e2e/bag.e2e.ts` checks in a real browser.)
     */
    it('can be reached by keyboard: it is not `disabled`, so it can take focus', async () => {
      await open({
        lines: [line({ status: 'Unavailable', price: null, lineSubtotal: null })],
        totals: totals({ buyableItemCount: 0, itemsOriginalTotal: 0, itemsSubtotal: 0, savedTotal: 0 }),
      });

      const button = blockedButton();
      expect(button).not.toBeNull();
      expect(button?.disabled).toBe(false);
      expect(button?.getAttribute('aria-disabled')).toBe('true');

      button?.focus();
      expect(document.activeElement).toBe(button);
    });

    /**
     * **This is the case the two counts are not allowed to agree on.** `isBuyable` counts a short-stock
     * line as buyable — it is in the subtotal, because the price is real — but it cannot be *ordered*,
     * because the API reserves the full quantity or refuses the order. So checkout is blocked while the
     * "not in this total" warning stays silent, and conflating the two would have offered a button the
     * server refuses.
     */
    it('blocks a short-stock line although it is still in the subtotal', async () => {
      await open({
        lines: [line({ status: 'NotEnoughStock', availableQuantity: 1 })],
        totals: totals(),
      });

      expect(blockedButton()?.getAttribute('aria-disabled')).toBe('true');
      expect(text()).not.toContain('cannot be bought at the moment and is not in this total');
      expect(sums().total).toBe(8784);
    });

    it('names the reason through aria-describedby, not only by being grey', async () => {
      await open({
        lines: [line({ status: 'Unavailable', price: null, lineSubtotal: null })],
        totals: totals({ buyableItemCount: 0, itemsOriginalTotal: 0, itemsSubtotal: 0, savedTotal: 0 }),
      });

      const describedBy = blockedButton()?.getAttribute('aria-describedby');
      expect(describedBy).toBe('bag-checkout-blocked');
      expect(element.querySelector(`#${describedBy}`)?.textContent).toContain('before you check out');
    });

    it('is absent altogether when the bag is empty', async () => {
      await open(EMPTY);

      expect(checkoutLink()).toBeNull();
      expect(blockedButton()).toBeNull();
    });
  });
});
