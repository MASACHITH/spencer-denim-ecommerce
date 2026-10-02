import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  TestRequest,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { TransferState, makeStateKey } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import { CartApi } from './cart-api';
import { CartDto, CartLineDto, CartTotalsDto } from './cart.model';
import { CartStore } from './cart.store';
import { PriceDto } from '../catalog/catalog.model';
import { SafeLocalStorage } from '@spencer-denim/shared';

import { LocalWishlist } from '../wishlist/local-wishlist.service';

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
    imageUrl: null,
    imageAltText: null,
    quantity: 2,
    maxQuantity: 10,
    availableQuantity: 12,
    price: price(4392),
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
    itemsOriginalTotal: 8784,
    itemsSubtotal: 8784,
    savedTotal: 0,
    currency: 'LKR',
    ...overrides,
  };
}

function cart(lines: CartLineDto[] = [line()], over: Partial<CartTotalsDto> = {}): CartDto {
  return { lines, totals: totals(over) };
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

describe('CartStore (CART-01, CART-02, GLB-04)', () => {
  let store: CartStore;
  let http: HttpTestingController;

  function setUp(): void {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });

    http = TestBed.inject(HttpTestingController);
    store = TestBed.inject(CartStore);
  }

  function pending(method: string, url = CartApi.url): TestRequest {
    return http.expectOne((request) => request.url === url && request.method === method);
  }

  beforeEach(() => {
    localStorage.clear();
    setUp();
  });

  afterEach(() => http.verify());

  describe('reading the bag', () => {
    it('asks once and holds what came back', () => {
      store.load();

      expect(store.isLoading()).toBe(true);
      pending('GET').flush(cart());

      expect(store.isLoading()).toBe(false);
      expect(store.lines()).toHaveLength(1);
      expect(store.itemCount()).toBe(2);
      expect(store.isEmpty()).toBe(false);
    });

    it('ensureLoaded reads once however many times it is called', () => {
      store.ensureLoaded();
      pending('GET').flush(EMPTY);

      store.ensureLoaded();
      store.ensureLoaded();

      // `verify()` in afterEach is the assertion: a second GET nobody flushed would fail it.
      expect(store.isEmpty()).toBe(true);
    });

    /**
     * **A failed read must not empty the bag.** Telling a shopper whose connection dropped that their
     * bag is empty is indistinguishable from the shop having lost it, and it is the more damaging of
     * the two possible lies.
     */
    it('keeps what the bag last held when a later read fails', () => {
      store.load();
      pending('GET').flush(cart());

      store.load();
      pending('GET').flush('nope', { status: 500, statusText: 'Server Error' });

      expect(store.lines()).toHaveLength(1);
      expect(store.itemCount()).toBe(2);
      expect(store.error()).toContain('could not update your bag');
      expect(store.status()).toBe('ready');
    });

    it('reports an error state when the very first read fails and there is nothing to keep', () => {
      store.load();
      pending('GET').flush('nope', { status: 503, statusText: 'Unavailable' });

      expect(store.status()).toBe('error');
      expect(store.isEmpty()).toBe(true);
      expect(store.error()).not.toBeNull();
    });

    it('a second read cannot be overtaken by the first one answering late', () => {
      store.load();
      const first = pending('GET');

      store.load();
      // `expectOne` above already took the first request out of the open list, so this match sees
      // only the second.
      const [second] = http.match((request) => request.method === 'GET');

      second.flush(cart([line({ quantity: 5 })], { itemCount: 5 }));
      first.flush(EMPTY);

      expect(store.itemCount()).toBe(5);
    });

    it('offers a retry that clears the message and asks again', () => {
      store.load();
      pending('GET').flush('nope', { status: 0, statusText: 'Unknown Error' });
      expect(store.error()).toContain('could not reach the store');

      store.retry();
      expect(store.error()).toBeNull();
      pending('GET').flush(cart());

      expect(store.itemCount()).toBe(2);
    });
  });

  describe('adding (PDP-03)', () => {
    it('posts the variant and the quantity, and takes the answer as the bag', () => {
      store.add(7, 3, 'Linen shirt, Beige, size M');

      expect(store.isAdding()).toBe(true);
      const request = pending('POST', `${CartApi.url}/items`);
      expect(request.request.body).toEqual({ variantId: 7, quantity: 3 });

      request.flush(cart([line({ variantId: 7, quantity: 3 })], { itemCount: 3 }));

      expect(store.isAdding()).toBe(false);
      expect(store.itemCount()).toBe(3);
    });

    it('refuses a second add while one is in flight, so a double tap adds once', () => {
      store.add(7, 1, 'Linen shirt');
      store.add(7, 1, 'Linen shirt');

      pending('POST', `${CartApi.url}/items`).flush(cart());
      expect(store.itemCount()).toBe(2);
    });

    /** The mini bag opens on this signal (CART-02), so it has to move even for a repeat add. */
    it('reports the variant that was added, and reports it again when the same one is added twice', () => {
      store.add(7, 1, 'Linen shirt');
      pending('POST', `${CartApi.url}/items`).flush(cart([line({ variantId: 7 })]));
      expect(store.lastAdded()).toBe(7);

      store.add(7, 1, 'Linen shirt');
      pending('POST', `${CartApi.url}/items`).flush(cart([line({ variantId: 7, quantity: 2 })]));
      expect(store.lastAdded()).toBe(7);
    });

    it('says a sold-out add is sold out, and re-reads the bag because the stock has moved', () => {
      store.add(7, 1, 'Linen shirt');
      pending('POST', `${CartApi.url}/items`).flush(
        { title: 'Out of stock', status: 409 },
        { status: 409, statusText: 'Conflict' },
      );

      expect(store.error()).toContain('sold out');
      expect(store.lastAdded()).toBeNull();

      // The re-read: without it the page would keep showing a stock figure the server has just
      // contradicted.
      pending('GET').flush(cart([line({ status: 'OutOfStock', availableQuantity: 0 })]));
      expect(store.lines()[0].status).toBe('OutOfStock');

      // **And the message survives it.** A successful read says nothing about whether the add
      // succeeded, so it must not clear the add's message — it used to, and the shopper was told
      // "that size has just sold out" for a few milliseconds and then told nothing at all. Found by
      // `bag.e2e.ts`, which could not find `.pdp__add-error` on the screen at all.
      expect(store.error()).toContain('sold out');
    });

    it('shows the API message when the API refuses the request', () => {
      store.add(7, 99, 'Linen shirt');
      pending('POST', `${CartApi.url}/items`).flush(
        { errors: { quantity: ['Choose a quantity between 1 and 10.'] } },
        { status: 400, statusText: 'Bad Request' },
      );

      expect(store.error()).toBe('Choose a quantity between 1 and 10.');
    });
  });

  describe('changing and removing (CART-01)', () => {
    it('patches one line and takes the answer as the bag', () => {
      store.setQuantity(1, 4, 'Linen shirt');

      const request = pending('PATCH', `${CartApi.url}/items/1`);
      expect(request.request.body).toEqual({ quantity: 4 });

      request.flush(cart([line({ quantity: 4 })], { itemCount: 4 }));
      expect(store.itemCount()).toBe(4);
    });

    it('marks only the line being changed as busy, not the whole bag', () => {
      store.setQuantity(1, 4, 'Linen shirt');

      expect(store.isBusy(1)).toBe(true);
      expect(store.isBusy(2)).toBe(false);

      pending('PATCH', `${CartApi.url}/items/1`).flush(cart());
      expect(store.isBusy(1)).toBe(false);
    });

    it('refuses a second change to the same line while one is in flight', () => {
      store.setQuantity(1, 4, 'Linen shirt');
      store.setQuantity(1, 5, 'Linen shirt');

      pending('PATCH', `${CartApi.url}/items/1`).flush(cart());
      expect(store.isBusy(1)).toBe(false);
    });

    it('deletes a line and empties the bag when it was the last one', () => {
      store.remove(1, 'Linen shirt');
      pending('DELETE', `${CartApi.url}/items/1`).flush(EMPTY);

      expect(store.isEmpty()).toBe(true);
      expect(store.itemCount()).toBe(0);
    });

    it('frees the line again when a change fails, so the controls do not stay disabled', () => {
      store.remove(1, 'Linen shirt');
      pending('DELETE', `${CartApi.url}/items/1`).flush('nope', {
        status: 500,
        statusText: 'Server Error',
      });

      expect(store.isBusy(1)).toBe(false);
      expect(store.error()).not.toBeNull();
    });
  });

  describe('move to wishlist (CART-01)', () => {
    /**
     * It saves **before** it removes. A storage failure then costs the shopper nothing — the item is
     * still in the bag — whereas removing first and failing to save would lose it altogether, which
     * is the bug this ordering exists to prevent.
     */
    it('saves the product to this browser and only then takes the line out', () => {
      const wishlist = TestBed.inject(LocalWishlist);

      store.moveToWishlist(1, 'relaxed-linen-blend-shirt', 'Linen shirt');

      expect(wishlist.has('relaxed-linen-blend-shirt')).toBe(true);
      pending('DELETE', `${CartApi.url}/items/1`).flush(EMPTY);
      expect(store.isEmpty()).toBe(true);
    });

    /**
     * **When the write fails, the line stays in the bag.**
     *
     * `SafeLocalStorage.write` cannot throw — it catches everything and returns `false` — so the only
     * failure this path can have is a silent one, and ordering the save before the removal protects
     * against nothing on its own. Before this test, a private window, blocked site data or a full
     * quota meant: the badge incremented, every visible signal said "saved", nothing was persisted,
     * and **the bag line was removed anyway**. On the next reload the item was in neither place.
     * Safari private browsing and iOS quota make that common rather than exotic.
     */
    it('keeps the line in the bag when the wishlist write fails, and says so', () => {
      TestBed.resetTestingModule();
      TestBed.configureTestingModule({
        providers: [
          provideHttpClient(),
          provideHttpClientTesting(),
          // Storage that refuses every write, exactly as a private window does — silently.
          {
            provide: SafeLocalStorage,
            useValue: { read: () => null, write: () => false, remove: () => false },
          },
        ],
      });

      http = TestBed.inject(HttpTestingController);
      const blocked = TestBed.inject(CartStore);
      const wishlist = TestBed.inject(LocalWishlist);

      blocked.moveToWishlist(1, 'relaxed-linen-blend-shirt', 'Linen shirt');

      // No DELETE at all — `verify()` in afterEach is the other half of this assertion.
      expect(http.match(() => true)).toEqual([]);
      expect(blocked.error()).toContain('still in your bag');

      // And nothing claims to have saved it. A badge that incremented over a write that failed is the
      // same lie one step earlier.
      expect(wishlist.has('relaxed-linen-blend-shirt')).toBe(false);
      expect(wishlist.count()).toBe(0);
    });

    /** `save`, not `toggle`: moving something already on the wishlist must not take it off. */
    it('leaves a product that is already on the wishlist on it', () => {
      const wishlist = TestBed.inject(LocalWishlist);
      wishlist.toggle('relaxed-linen-blend-shirt');

      store.moveToWishlist(1, 'relaxed-linen-blend-shirt', 'Linen shirt');

      expect(wishlist.has('relaxed-linen-blend-shirt')).toBe(true);
      pending('DELETE', `${CartApi.url}/items/1`).flush(EMPTY);
    });
  });

  describe('what a screen reader hears (CART-02)', () => {
    it('names the item, what happened to it, and what the bag now holds', () => {
      store.add(7, 1, 'Linen shirt, Beige, size M');
      pending('POST', `${CartApi.url}/items`).flush(cart([line({ variantId: 7 })]));

      expect(store.lastChange()).toContain('Linen shirt, Beige, size M added to your bag');
      expect(store.lastChange()).toContain('2 items');
    });

    it('says the bag is empty when the last line goes', () => {
      store.remove(1, 'Linen shirt');
      pending('DELETE', `${CartApi.url}/items/1`).flush(EMPTY);

      expect(store.lastChange()).toContain('Your bag is now empty');
    });

    it('mentions the lines that need attention, which colour alone would be the only sign of', () => {
      store.setQuantity(1, 4, 'Linen shirt');
      pending('PATCH', `${CartApi.url}/items/1`).flush(
        cart([line({ quantity: 4, status: 'OutOfStock', availableQuantity: 0 })], {
          itemCount: 4,
          buyableItemCount: 0,
        }),
      );

      expect(store.lastChange()).toContain('1 item needs attention');
    });

    /**
     * A live region only speaks when its text changes, so two identical changes in a row would be
     * announced once. The trailing space is invisible and makes the second one audible.
     */
    it('changes the announcement even when the same thing happens twice', () => {
      store.remove(1, 'Linen shirt');
      pending('DELETE', `${CartApi.url}/items/1`).flush(EMPTY);
      const first = store.lastChange();

      store.remove(1, 'Linen shirt');
      pending('DELETE', `${CartApi.url}/items/1`).flush(EMPTY);

      expect(store.lastChange()).not.toBe(first);
      expect(store.lastChange().trim()).toBe(first.trim());
    });
  });

  describe('what the server handed over', () => {
    /**
     * The handover is taken in the store's **constructor**, and the timing is the point: the header is
     * created before the routed page, so taking it any later would mean the browser's first render
     * showed a bag count of 0 against a server-rendered badge of N — a hydration mismatch.
     */
    it('starts from the bag the server rendered, before anything has been fetched', () => {
      TestBed.resetTestingModule();
      TestBed.configureTestingModule({
        providers: [provideHttpClient(), provideHttpClientTesting()],
      });
      TestBed.inject(TransferState).set(makeStateKey<CartDto | null>('cart'), cart());

      http = TestBed.inject(HttpTestingController);
      const hydrated = TestBed.inject(CartStore);

      expect(hydrated.itemCount()).toBe(2);
      expect(hydrated.status()).toBe('ready');
    });

    /** And it still reads again: stock and prices move, and a bag is where that costs money. */
    it('reads again even when it was handed a bag, and does not flash a loading state', () => {
      TestBed.resetTestingModule();
      TestBed.configureTestingModule({
        providers: [provideHttpClient(), provideHttpClientTesting()],
      });
      TestBed.inject(TransferState).set(makeStateKey<CartDto | null>('cart'), cart());

      http = TestBed.inject(HttpTestingController);
      const hydrated = TestBed.inject(CartStore);

      hydrated.load();
      expect(hydrated.isLoading()).toBe(false);
      expect(hydrated.itemCount()).toBe(2);

      http
        .expectOne((request) => request.url === CartApi.url && request.method === 'GET')
        .flush(cart([line({ quantity: 1 })], { itemCount: 1 }));

      expect(hydrated.itemCount()).toBe(1);
    });
  });
});
