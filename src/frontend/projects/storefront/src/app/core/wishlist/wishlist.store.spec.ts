import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { SafeLocalStorage } from '@spencer-denim/shared';

import { LocalWishlist } from './local-wishlist.service';
import { WishlistApi, WishlistResponse } from './wishlist-api';
import { WishlistStore } from './wishlist.store';
import { ProductCardDto } from '../catalog/catalog.model';

function product(overrides: Partial<ProductCardDto> = {}): ProductCardDto {
  return {
    id: 1,
    productCode: 'SD-0001',
    name: 'Relaxed Linen Blend Shirt',
    slug: 'relaxed-linen-blend-shirt',
    departmentName: 'Women',
    departmentSlug: 'women',
    categoryName: 'Shirts',
    categorySlug: 'shirts',
    imageUrl: null,
    imageSrcSet: null,
    hoverImageUrl: null,
    hoverImageSrcSet: null,
    imageAltText: null,
    price: {
      current: 4392,
      original: 4392,
      discountPercent: 0,
      hasDiscount: false,
      currency: 'LKR',
    },
    rating: { average: null, count: 0 },
    badge: null,
    stockStatus: 'InStock',
    isInStock: true,
    colours: [],
    ...overrides,
  };
}

/**
 * The wishlist page's state (W2-08, WISH-01).
 *
 * The questions worth asking of it are all about **who is authoritative for what**: the browser owns
 * membership and order, the shop owns prices and whether a product is still sold, and a failure in
 * either must not be allowed to look like the other.
 */
describe('WishlistStore (WISH-01)', () => {
  let http: HttpTestingController;
  let store: WishlistStore;
  let local: LocalWishlist;

  /** A storage stub that really stores, so membership behaves as it does in a browser. */
  function configure(stored: string | null = null, writes = true): void {
    let value = stored;

    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        {
          provide: SafeLocalStorage,
          useValue: {
            read: () => value,
            write: (_key: string, next: string) => {
              if (!writes) {
                return false;
              }

              value = next;
              return true;
            },
            remove: () => writes,
          },
        },
      ],
    });

    http = TestBed.inject(HttpTestingController);
    store = TestBed.inject(WishlistStore);
    local = TestBed.inject(LocalWishlist);
  }

  function savedList(...slugs: readonly string[]): string {
    return JSON.stringify({
      v: 2,
      items: slugs.map((slug, index) => ({
        slug,
        name: `Saved ${slug}`,
        colourId: null,
        colourName: null,
        addedAt: 1000 - index,
      })),
    });
  }

  /**
   * The one outstanding request.
   *
   * A **`POST`**: the slugs used to ride in the query string and did not fit — Kestrel's request line
   * is 8,192 bytes and 200 slugs exceeded it at about 32 characters each, so a full wishlist was a 414
   * and a page that could never load. `WishlistTransportTests` holds that against a real server; what
   * this helper pins is that the slugs are in the **body**, where their length cannot reach the URL.
   */
  function pendingRead() {
    return http.expectOne(
      (request) => request.url === WishlistApi.url && request.method === 'POST',
    );
  }

  /** The slugs one request asked about, read out of its body. */
  function askedAbout(request: { request: { body: unknown } }): readonly string[] {
    return (request.request.body as { slugs: readonly string[] }).slugs;
  }

  afterEach(() => http?.verify());

  describe('reading the live cards', () => {
    it('asks about every saved slug, newest first', () => {
      configure(savedList('a', 'b'));

      store.load();

      const request = pendingRead();
      expect(askedAbout(request)).toEqual(['a', 'b']);
      // And nothing is in the URL, which is the whole of the 414 fix.
      expect(request.request.urlWithParams).toBe(WishlistApi.url);
      request.flush({ items: [] } satisfies WishlistResponse);
    });

    it('asks nothing at all when nothing is saved, and is ready immediately', () => {
      configure();

      store.load();

      expect(http.match(() => true)).toEqual([]);
      expect(store.status()).toBe('ready');
      expect(store.isEmpty()).toBe(true);
    });

    it('draws a card for a slug the shop still sells', () => {
      configure(savedList('relaxed-linen-blend-shirt'));

      store.load();
      pendingRead().flush({
        items: [{ slug: 'relaxed-linen-blend-shirt', product: product() }],
      } satisfies WishlistResponse);

      const [row] = store.rows();
      expect(row.pending).toBe(false);
      expect(row.product?.name).toBe('Relaxed Linen Blend Shirt');
      expect(store.unavailableCount()).toBe(0);
    });

    /** Doc 11 §575: a wishlist item removed from sale is shown as unavailable and can be removed. */
    it('marks a slug the shop no longer offers as unavailable rather than dropping it', () => {
      configure(savedList('withdrawn'));

      store.load();
      pendingRead().flush({ items: [{ slug: 'withdrawn', product: null }] } satisfies WishlistResponse);

      const [row] = store.rows();
      expect(row.pending).toBe(false);
      expect(row.product).toBeNull();
      expect(store.unavailableCount()).toBe(1);
      // Still in the shopper's list, and still counted: the header badge and the page must agree.
      expect(store.count()).toBe(1);
    });

    /**
     * **A slug the answer does not mention stays pending, it does not become "withdrawn".**
     *
     * The two render differently and mean different things — "we are still finding out" against "the
     * shop has stopped selling this" — and the only honest reading of a missing entry is the first.
     * The API promises one entry per slug, so this is a contract violation rather than an answer, and
     * a store that read it as "withdrawn" would be putting words in the shop's mouth.
     */
    it('leaves a slug the answer never mentions pending', () => {
      configure(savedList('answered', 'ignored'));

      store.load();
      pendingRead().flush({
        items: [{ slug: 'answered', product: product({ slug: 'answered' }) }],
      } satisfies WishlistResponse);

      expect(store.rows().map((row) => row.pending)).toEqual([false, true]);
      expect(store.unavailableCount()).toBe(0);
    });

    /**
     * **A failed read never empties the page.** The saved list is in the browser and is still correct;
     * what failed is the prices and the photographs. A wishlist that read as empty would be
     * indistinguishable from the shop having lost it.
     */
    it('keeps the rows and offers a retry when the read fails', () => {
      configure(savedList('a', 'b'));

      store.load();
      pendingRead().error(new ProgressEvent('offline'), { status: 0 });

      expect(store.status()).toBe('error');
      expect(store.error()).toContain('could not reach the store');
      expect(store.rows()).toHaveLength(2);
      expect(store.isEmpty()).toBe(false);

      store.retry();
      expect(store.error()).toBeNull();
      pendingRead().flush({ items: [] } satisfies WishlistResponse);
      expect(store.status()).toBe('ready');
    });

    /**
     * **And a failed *re-*read does not throw away the cards it already had.** This is the half of the
     * rule above that can actually be got wrong: membership comes from the browser, so the rows cannot
     * disappear, but the prices and the photographs can — and a shopper whose connection dropped would
     * watch their wishlist turn into a column of grey placeholders for no reason. What failed is the
     * refresh; the last answer the shop gave is still the best one available.
     */
    it('keeps the cards it already had when a re-read fails', () => {
      configure(savedList('a'));

      store.load();
      pendingRead().flush({
        items: [{ slug: 'a', product: product({ slug: 'a', name: 'Still here' }) }],
      } satisfies WishlistResponse);

      store.load();
      pendingRead().error(new ProgressEvent('offline'), { status: 0 });

      expect(store.status()).toBe('error');
      expect(store.rows()[0]?.product?.name).toBe('Still here');
      expect(store.rows()[0]?.pending).toBe(false);
    });

    /** An answer from a superseded read must not overwrite a newer one. */
    it('ignores the answer to a read that has been superseded', () => {
      configure(savedList('a'));

      store.load();
      const first = pendingRead();

      store.load();
      const second = pendingRead();

      second.flush({ items: [{ slug: 'a', product: product({ slug: 'a', name: 'Newer' }) }] });
      first.flush({ items: [{ slug: 'a', product: product({ slug: 'a', name: 'Older' }) }] });

      expect(store.rows()[0]?.product?.name).toBe('Newer');
    });

    it('caps the request at the server cap and says the list was truncated', () => {
      const many = Array.from({ length: 205 }, (_unused, index) => `product-${index}`);
      configure(savedList(...many));

      store.load();

      const request = pendingRead();
      expect(askedAbout(request)).toHaveLength(200);
      request.flush({ items: [] } satisfies WishlistResponse);

      expect(store.truncated()).toBe(true);
      // Nothing was removed from the shopper's own list.
      expect(store.count()).toBe(205);
    });

    /**
     * **And no row is drawn that the request never asked about** (round 2, m1). `rows()` used to map
     * every saved entry while `load()` asked about the first 200, so entries past the cap rendered as
     * rows stuck on "Loading…" for as long as the page stayed open — 205 rows under a note saying 200
     * are shown. The count and the note still read the whole list, because nothing has been deleted.
     */
    it('draws no more rows than one request can answer for', () => {
      const many = Array.from({ length: 205 }, (_unused, index) => `product-${index}`);
      configure(savedList(...many));

      store.load();
      pendingRead().flush({ items: [] } satisfies WishlistResponse);

      expect(store.rows()).toHaveLength(200);
      expect(store.count()).toBe(205);
      expect(store.truncated()).toBe(true);
    });

    /** The boundary either side of the cap, so the slice cannot be off by one. */
    it.each([
      [199, 199, false],
      [200, 200, false],
      [201, 200, true],
    ])('%i saved draws %i rows (truncated: %s)', (saved, drawn, truncated) => {
      const many = Array.from({ length: saved }, (_unused, index) => `product-${index}`);
      configure(savedList(...many));

      store.load();
      pendingRead().flush({ items: [] } satisfies WishlistResponse);

      expect(store.rows()).toHaveLength(drawn);
      expect(store.truncated()).toBe(truncated);
    });
  });

  describe('remove and clear', () => {
    it('takes one item out in the same frame, with no second request', () => {
      configure(savedList('a', 'b'));
      store.load();
      pendingRead().flush({ items: [] } satisfies WishlistResponse);

      expect(store.remove('a', 'Saved a')).toBe(true);

      expect(store.rows().map((row) => row.entry.slug)).toEqual(['b']);
      expect(store.lastChange()).toContain('Saved a removed from your wishlist');
      expect(store.lastChange()).toContain('1 item');
      expect(http.match(() => true)).toEqual([]);
    });

    it('clears everything and says how many went', () => {
      configure(savedList('a', 'b', 'c'));
      store.load();
      pendingRead().flush({ items: [] } satisfies WishlistResponse);

      expect(store.clear()).toBe(true);

      expect(store.isEmpty()).toBe(true);
      expect(store.lastChange()).toContain('3 items removed');
    });

    /**
     * **A refused write leaves the item saved, and the page must say so rather than drop the row.** The
     * mirror of a filled heart over a refused save: the page would disagree with the browser until the
     * next reload and the shopper would have no idea.
     */
    it('keeps the item and explains when storage refuses the removal', () => {
      configure(savedList('a'), false);
      store.load();
      pendingRead().flush({ items: [] } satisfies WishlistResponse);

      expect(store.remove('a', 'Saved a')).toBe(false);

      expect(store.rows()).toHaveLength(1);
      expect(store.error()).toContain('still there');
    });

    it('keeps everything and explains when storage refuses the clear', () => {
      configure(savedList('a', 'b'), false);
      store.load();
      pendingRead().flush({ items: [] } satisfies WishlistResponse);

      expect(store.clear()).toBe(false);

      expect(store.count()).toBe(2);
      expect(store.error()).toContain('still saved');
    });
  });

  describe('move to bag (WISH-01)', () => {
    it('opens the size chooser for one item and adds nothing by itself', () => {
      configure(savedList('a'));
      store.load();
      pendingRead().flush({ items: [] } satisfies WishlistResponse);

      store.beginMove('a');

      expect(store.movingSlug()).toBe('a');
      expect(store.count()).toBe(1);
      expect(http.match(() => true)).toEqual([]);
    });

    it('leaves the item saved when the chooser is dismissed', () => {
      configure(savedList('a'));
      store.beginMove('a');

      store.cancelMove();

      expect(store.movingSlug()).toBeNull();
      expect(local.has('a')).toBe(true);
    });

    /**
     * **The item leaves the wishlist only once the bag has it.** `completeMove` is called from
     * `QuickView.added`, which fires in the one branch where `CartStore` answered with a bag and no
     * error. This is the mirror of the W2-09 defect, where a bag line was destroyed over a wishlist
     * save that had silently failed and the item ended up in neither place.
     */
    it('removes the item once the bag has taken it', () => {
      configure(savedList('a', 'b'));
      store.load();
      pendingRead().flush({ items: [] } satisfies WishlistResponse);

      store.beginMove('a');
      store.completeMove('a', 'Saved a');

      expect(store.movingSlug()).toBeNull();
      expect(local.has('a')).toBe(false);
      expect(store.lastChange()).toContain('Saved a moved to your bag');
    });

    /**
     * **In the bag and still in the wishlist is a tidy-up; in neither is a loss.** If the removal fails
     * after a successful add, the shopper is told both facts.
     */
    it('says the item is in both places when the removal is refused after a successful add', () => {
      configure(savedList('a'), false);

      store.completeMove('a', 'Saved a');

      expect(local.has('a')).toBe(true);
      expect(store.error()).toContain('could not take it out of your wishlist');
      expect(store.lastChange()).toContain('still in your wishlist');
    });
  });

  describe('reporting a removal, so a page can re-home focus', () => {
    /**
     * **Removing a row destroys the control that was pressed, which throws focus to `<body>`** — the
     * same harm a `[disabled]` attribute does, arriving by a different route. Round 1 of W2-08 kept the
     * attribute off the page and lost focus on all three removal controls anyway, so the store now
     * reports *what went and where it was* and the page decides where focus goes.
     */
    it('says which visible row went', () => {
      configure(savedList('a', 'b', 'c'));
      store.load();
      pendingRead().flush({ items: [] } satisfies WishlistResponse);

      store.remove('b', 'B');

      expect(store.lastRemoval()).toMatchObject({ kind: 'row', index: 1 });
    });

    /** A clear is not a row, and the page puts focus somewhere else for it. */
    it('says the whole list went on a clear', () => {
      configure(savedList('a', 'b'));

      store.clear();

      expect(store.lastRemoval()).toMatchObject({ kind: 'all' });
    });

    /** Moving to the bag removes a row too, and destroys the button that opened the chooser. */
    it('reports a move to the bag as a removal', () => {
      configure(savedList('a', 'b'));

      store.completeMove('a', 'A');

      expect(store.lastRemoval()).toMatchObject({ kind: 'row', index: 0 });
    });

    /** A refused write removed nothing, so there is nothing to re-home focus for. */
    it('reports nothing when storage refuses the removal', () => {
      configure(savedList('a'), false);

      store.remove('a', 'A');

      expect(store.lastRemoval()).toBeNull();
    });

    /**
     * Two removals of the same row produce the same `kind` and the same `index`, so without a fresh
     * ticket the page would see no change and leave focus on `<body>` for the second one.
     */
    it('gives every removal a new ticket, even an identical one', () => {
      configure(savedList('a', 'b'));
      store.load();
      pendingRead().flush({ items: [] } satisfies WishlistResponse);

      store.remove('a', 'A');
      const first = store.lastRemoval();

      store.remove('b', 'B');
      const second = store.lastRemoval();

      expect(first).not.toBeNull();
      expect(second?.index).toBe(first?.index);
      expect(second?.ticket).not.toBe(first?.ticket);
    });

    /** An entry past the visible cap has no row, so there is no successor to focus. */
    it('reports -1 for an entry that was never on screen', () => {
      const many = Array.from({ length: 205 }, (_unused, index) => `product-${index}`);
      configure(savedList(...many));
      store.load();
      pendingRead().flush({ items: [] } satisfies WishlistResponse);

      store.remove('product-204', 'Last');

      expect(store.lastRemoval()).toMatchObject({ index: -1 });
    });
  });

  describe('the heart, from wherever it is pressed (m3)', () => {
    /**
     * **The card's heart used to be silent.** It called `LocalWishlist.toggle` directly, so a row
     * disappearing from the wishlist page announced nothing while the same row's Remove button
     * announced a full sentence — one feature, two controls, one of them mute. Going through the store
     * means both speak, and both report the removal that focus handling needs.
     */
    it('announces a save and a removal, and reports the removal', () => {
      configure(savedList('a'));
      store.load();
      pendingRead().flush({ items: [] } satisfies WishlistResponse);

      expect(store.toggle({ slug: 'b', name: 'B' }, 'B')).toBe('saved');
      expect(store.lastChange()).toContain('B saved to your wishlist');
      expect(store.lastRemoval()).toBeNull();

      expect(store.toggle({ slug: 'b' }, 'B')).toBe('removed');
      expect(store.lastChange()).toContain('B removed from your wishlist');
      expect(store.lastRemoval()).toMatchObject({ kind: 'row', index: 0 });
    });

    /**
     * A refused write changes nothing, so nothing is announced and nothing is reported — a sentence
     * about a save that did not happen is the same lie as a filled heart over one.
     */
    it('announces nothing when storage refuses the write', () => {
      configure(null, false);

      expect(store.toggle({ slug: 'a', name: 'A' }, 'A')).toBe('unavailable');
      expect(store.lastChange()).toBe('');
      expect(store.lastRemoval()).toBeNull();
      expect(store.error()).toContain('exactly as it was');
    });

    it('answers whether a product is saved, so a card needs one injection', () => {
      configure(savedList('a'));

      expect(store.isSaved('a')).toBe(true);
      expect(store.isSaved('b')).toBe(false);
    });
  });

  describe('what a screen reader hears', () => {
    /**
     * **ADM-22's defect: a repeated identical announcement was never read out a second time.** Two
     * removals of identically named items produce the same sentence, and a live region only announces
     * a value that has changed.
     */
    it('re-announces an identical sentence', () => {
      configure(savedList('a', 'b'));
      store.load();
      pendingRead().flush({ items: [] } satisfies WishlistResponse);

      store.remove('a', 'Linen shirt');
      const first = store.lastChange();

      // The same request again: removing something already gone succeeds (it is the end state the
      // caller asked for), the count is unchanged, and so the sentence is character-for-character the
      // one before it. A shopper who taps Remove twice has to hear an answer both times.
      store.remove('a', 'Linen shirt');
      const second = store.lastChange();

      expect(first).toContain('Linen shirt removed');
      expect(second).not.toBe(first);
      expect(second.trim()).toBe(first.trim());
    });

    it('says the wishlist is empty when the last item goes', () => {
      configure(savedList('a'));
      store.load();
      pendingRead().flush({ items: [] } satisfies WishlistResponse);

      store.remove('a', 'Saved a');

      expect(store.lastChange()).toContain('Your wishlist is now empty.');
    });
  });
});
