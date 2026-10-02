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
     * The summary is the part that must not invent anything. There is no promo field (ADM-13 is not
     * built), no delivery figure (#33, and `sales.DeliveryMethodZoneRates` is #53's), and so the total
     * row says what it covers.
     */
    it('shows subtotal, discount and a delivery row that does not pretend to know the fee', async () => {
      await open({ lines: [line()], totals: totals() });

      const summary = element.querySelector('.bag__summary') as HTMLElement;
      expect(summary.textContent).toContain('Subtotal (2 items)');
      expect(summary.textContent).toContain('Rs. 8,784');
      expect(summary.textContent).toContain('Rs. 5,196');
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
});
