import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  TestRequest,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { PLATFORM_ID, RESPONSE_INIT } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Meta, Title } from '@angular/platform-browser';
import { provideRouter } from '@angular/router';

import { WishlistPage } from './wishlist-page';
import { ProductCardDto } from '../../core/catalog/catalog.model';
import { WishlistApi, WishlistResponse } from '../../core/wishlist/wishlist-api';
import { WishlistStore } from '../../core/wishlist/wishlist.store';

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
 * The wishlist page (W2-08, WISH-01).
 *
 * `localStorage` is real here — jsdom has one — and it is the shopper's wishlist, so each test writes
 * the list it wants before the page is created, exactly as a browser would already hold one.
 */
describe('WishlistPage (WISH-01)', () => {
  let fixture: ComponentFixture<WishlistPage>;
  let element: HTMLElement;
  let http: HttpTestingController;
  let response: ResponseInit;

  const STORAGE_KEY = 'sd.wishlist.local';

  function saved(...slugs: readonly { slug: string; name?: string }[]): void {
    localStorage.setItem(
      STORAGE_KEY,
      JSON.stringify({
        v: 2,
        items: slugs.map((entry, index) => ({
          slug: entry.slug,
          name: entry.name ?? null,
          colourId: null,
          colourName: null,
          addedAt: 1000 - index,
        })),
      }),
    );
  }

  async function settle(): Promise<void> {
    fixture.detectChanges();
    await fixture.whenStable();
  }

  function create(platform: 'browser' | 'server' = 'browser'): void {
    response = {};
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: RESPONSE_INIT, useValue: response },
        ...(platform === 'server' ? [{ provide: PLATFORM_ID, useValue: 'server' }] : []),
      ],
    });

    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(WishlistPage);
    element = fixture.nativeElement as HTMLElement;
  }

  /**
   * The page as the SSR process renders it. `afterNextRender` does not run there and
   * `SafeLocalStorage` answers `null` for everything, which together are the whole of "the server
   * cannot know what this browser has saved".
   */
  function createOnServer(): void {
    create('server');
  }

  /**
   * A **`POST`**: the slugs used to ride in the query string and did not fit in a request line, so a
   * full wishlist answered 414 and the page could never load. See `WishlistTransportTests`.
   */
  function readRequest(): TestRequest {
    return http.expectOne((request) => request.url === WishlistApi.url && request.method === 'POST');
  }

  async function open(items: WishlistResponse['items']): Promise<void> {
    create();
    await settle();
    readRequest().flush({ items } satisfies WishlistResponse);
    await settle();
  }

  function text(): string {
    return element.textContent ?? '';
  }

  function button(testId: string): HTMLButtonElement | null {
    return element.querySelector(`[data-testid="${testId}"]`);
  }

  function link(label: string): HTMLButtonElement {
    const match = Array.from(element.querySelectorAll('button')).find(
      (node) => node.textContent?.trim() === label,
    );

    if (!match) {
      throw new Error(`No button labelled "${label}". The page says: ${text()}`);
    }

    return match;
  }

  beforeEach(() => localStorage.clear());

  afterEach(() => http?.verify());

  describe('what the server renders', () => {
    /**
     * **The page gates on having looked, not on the list being empty.** The server has no storage, so
     * its markup is the same for a shopper with twenty saved products and one with none — and
     * "Your wishlist is empty" would be a claim rather than a wait. Two red `main` branches have been
     * built on exactly this distinction: a missing hydration step that leaves an element absent reads
     * as a timeout, one that leaves *state* unwritten reads as a confident wrong value.
     *
     * Rendered on the server platform, which is the only way to see it: in a browser `afterNextRender`
     * runs inside the first `detectChanges`, so the pre-taken frame is not observable from a unit test.
     * `wishlist.e2e.ts` reads the same thing off the wire.
     */
    it('says it is loading rather than that the wishlist is empty', async () => {
      saved({ slug: 'relaxed-linen-blend-shirt' });
      createOnServer();
      await settle();

      expect(text()).toContain('Loading your wishlist');
      expect(text()).not.toContain('Your wishlist is empty');
      expect(text()).not.toContain('Relaxed Linen Blend Shirt');

      // And it asks the shop nothing during a render: there is no list to ask about.
      expect(http.match(() => true)).toEqual([]);
    });

    /** The live region is in the markup from the first render: one added later has not been watched. */
    it('renders the live region before there is anything to announce', () => {
      create();
      fixture.detectChanges();

      const region = element.querySelector('[role="status"]');
      expect(region).not.toBeNull();
    });
  });

  describe('the empty state (WISH-01)', () => {
    it('says the wishlist is empty, says where saves go, and offers a way back into the shop', async () => {
      create();
      await settle();

      expect(text()).toContain('Your wishlist is empty');
      expect(text()).toContain('this browser');
      expect(element.querySelector('a.btn')?.textContent).toContain('Start shopping');
      // Nothing is asked of the shop for an empty wishlist: the answer is already in the browser.
      expect(http.match(() => true)).toEqual([]);
    });
  });

  describe('the saved products', () => {
    it('draws a card per saved product and counts them', async () => {
      saved({ slug: 'relaxed-linen-blend-shirt' }, { slug: 'denim-jacket' });
      await open([
        { slug: 'relaxed-linen-blend-shirt', product: product() },
        { slug: 'denim-jacket', product: product({ id: 2, slug: 'denim-jacket', name: 'Denim Jacket' }) },
      ]);

      expect(element.querySelectorAll('sd-product-card')).toHaveLength(2);
      expect(text()).toContain('2 items saved in this browser');
      expect(text()).toContain('Denim Jacket');
    });

    /**
     * Doc 11 §575. There is no card to draw — the API has nothing to send for a product that is not for
     * sale — so the name comes from what was stored when it was saved, and Remove is still offered.
     */
    it('names a withdrawn product from what was stored and offers Remove', async () => {
      saved({ slug: 'withdrawn', name: 'Discontinued Shirt' });
      await open([{ slug: 'withdrawn', product: null }]);

      expect(text()).toContain('Discontinued Shirt');
      expect(text()).toContain('no longer available');
      expect(text()).toContain('1 is no longer available');
      expect(button('wishlist-remove')).not.toBeNull();
      expect(element.querySelectorAll('sd-product-card')).toHaveLength(0);
    });

    it('offers no Move to bag for something out of stock, and says why', async () => {
      saved({ slug: 'relaxed-linen-blend-shirt' });
      await open([
        {
          slug: 'relaxed-linen-blend-shirt',
          product: product({ isInStock: false, stockStatus: 'OutOfStock' }),
        },
      ]);

      expect(button('wishlist-move-to-bag')).toBeNull();
      expect(text()).toContain('Out of stock');
      expect(button('wishlist-remove')).not.toBeNull();
    });

    it('keeps the rows and offers Try again when the read fails', async () => {
      saved({ slug: 'relaxed-linen-blend-shirt', name: 'Saved shirt' });
      create();
      await settle();
      readRequest().error(new ProgressEvent('offline'), { status: 0 });
      await settle();

      expect(text()).toContain('could not reach the store');
      expect(text()).toContain('Saved shirt');
      expect(text()).not.toContain('Your wishlist is empty');

      link('Try again').click();
      await settle();
      readRequest().flush({ items: [] } satisfies WishlistResponse);
    });
  });

  describe('remove and clear (WISH-01)', () => {
    it('removes one product and announces it', async () => {
      saved({ slug: 'relaxed-linen-blend-shirt' }, { slug: 'denim-jacket' });
      await open([
        { slug: 'relaxed-linen-blend-shirt', product: product() },
        { slug: 'denim-jacket', product: product({ id: 2, slug: 'denim-jacket' }) },
      ]);

      button('wishlist-remove')!.click();
      await settle();

      expect(element.querySelectorAll('sd-product-card')).toHaveLength(1);
      expect(element.querySelector('[role="status"]')?.textContent).toContain(
        'removed from your wishlist',
      );
    });

    /**
     * Clearing a wishlist is destructive and irreversible, so it asks first — inline, because
     * `window.confirm` cannot be styled, is announced badly, and does not exist during a server render.
     */
    it('asks before clearing, and does nothing if the question is dismissed', async () => {
      saved({ slug: 'relaxed-linen-blend-shirt' });
      await open([{ slug: 'relaxed-linen-blend-shirt', product: product() }]);

      link('Clear wishlist').click();
      await settle();

      expect(text()).toContain('Remove the 1 item in your wishlist?');

      link('Cancel').click();
      await settle();

      expect(element.querySelectorAll('sd-product-card')).toHaveLength(1);
    });

    it('clears everything when the question is answered', async () => {
      saved({ slug: 'a' }, { slug: 'b' });
      await open([
        { slug: 'a', product: product({ slug: 'a' }) },
        { slug: 'b', product: product({ id: 2, slug: 'b' }) },
      ]);

      link('Clear wishlist').click();
      await settle();
      link('Yes, clear it').click();
      await settle();

      expect(text()).toContain('Your wishlist is empty');
      expect(localStorage.getItem(STORAGE_KEY)).toContain('"items":[]');
    });
  });

  describe('move to bag (WISH-01)', () => {
    /**
     * A wishlist entry names a product and a bag line needs a variant, so the size is chosen first
     * (doc 11 §539) — in Quick View, which is the only thing in the storefront that has the variants.
     */
    it('opens the size chooser and removes nothing by itself', async () => {
      saved({ slug: 'relaxed-linen-blend-shirt' });
      await open([{ slug: 'relaxed-linen-blend-shirt', product: product() }]);

      const move = button('wishlist-move-to-bag')!;
      expect(move.getAttribute('aria-haspopup')).toBe('dialog');
      expect(move.getAttribute('aria-expanded')).toBe('false');

      move.click();
      await settle();

      expect(element.querySelector('sd-quick-view')).not.toBeNull();
      expect(button('wishlist-move-to-bag')!.getAttribute('aria-expanded')).toBe('true');
      // Still saved: nothing has been added to a bag yet.
      expect(localStorage.getItem(STORAGE_KEY)).toContain('relaxed-linen-blend-shirt');

      // The dialog fetches the product detail it needs for the sizes.
      http.match(() => true).forEach((request) => request.flush(null, { status: 404, statusText: 'x' }));
    });

    /**
     * **Every control on this page is at least 44 px, which axe does not check.** jsdom has no layout,
     * so the figure itself is measured in `wishlist.e2e.ts`; what is assertable here is that each
     * control carries the class that sets the minimum, which is the thing a refactor deletes.
     */
    it('gives every action the tap-target class', async () => {
      saved({ slug: 'relaxed-linen-blend-shirt' });
      await open([{ slug: 'relaxed-linen-blend-shirt', product: product() }]);

      expect(button('wishlist-move-to-bag')!.className).toContain('wish__move');
      expect(button('wishlist-remove')!.className).toContain('wish__remove');
      expect(link('Clear wishlist').className).toContain('wish__clear');
    });

    /**
     * **No `[disabled]` anywhere on this page.** A `disabled` attribute applied to the button that
     * currently has focus throws focus to `<body>`; this project has shipped that three times (ADM-22's
     * save, Quick View's Add to Bag, and #109 on the bag's Checkout button). Nothing here needs a
     * disabled state at all — every action is synchronous — and this is the test that notices if one
     * arrives.
     */
    it('disables no control with the disabled attribute', async () => {
      saved({ slug: 'relaxed-linen-blend-shirt' });
      await open([{ slug: 'relaxed-linen-blend-shirt', product: product() }]);

      const disabled = Array.from(element.querySelectorAll('button[disabled], a[disabled]'));
      expect(disabled).toEqual([]);
    });
  });

  describe('focus, after a removal has destroyed the control that was pressed (MAJOR 1)', () => {
    /**
     * **This is the `[disabled]` defect arriving by another route.** Round 1 kept the attribute off this
     * page - and a spec asserts it stays off - and then removed the focused button from the DOM
     * instead, which throws focus to `<body>` just the same. A keyboard or screen-reader shopper
     * clearing a wishlist lost their place on every single item. Banning the attribute was necessary
     * and not sufficient.
     */
    it('moves focus to the row that took the removed one\u2019s place', async () => {
      saved({ slug: 'a' }, { slug: 'b' }, { slug: 'c' });
      await open([
        { slug: 'a', product: product({ slug: 'a' }) },
        { slug: 'b', product: product({ id: 2, slug: 'b' }) },
        { slug: 'c', product: product({ id: 3, slug: 'c' }) },
      ]);

      const buttons = Array.from(
        element.querySelectorAll<HTMLButtonElement>('[data-testid="wishlist-remove"]'),
      );
      buttons[1].focus();
      buttons[1].click();
      await settle();

      // Two rows left, and focus is on the Remove of the row now in position 1 - not on `<body>`.
      expect(element.querySelectorAll('.wish__item')).toHaveLength(2);
      expect(document.activeElement).toBe(
        element.querySelectorAll<HTMLButtonElement>('[data-testid="wishlist-remove"]')[1],
      );
    });

    /** Removing the last row has no successor, so focus falls back to the row before it. */
    it('moves focus to the last row when the removed one was last', async () => {
      saved({ slug: 'a' }, { slug: 'b' });
      await open([
        { slug: 'a', product: product({ slug: 'a' }) },
        { slug: 'b', product: product({ id: 2, slug: 'b' }) },
      ]);

      const buttons = Array.from(
        element.querySelectorAll<HTMLButtonElement>('[data-testid="wishlist-remove"]'),
      );
      buttons[1].focus();
      buttons[1].click();
      await settle();

      expect(document.activeElement).toBe(
        element.querySelector<HTMLButtonElement>('[data-testid="wishlist-remove"]'),
      );
    });

    it('moves focus to Start shopping when the last item goes', async () => {
      saved({ slug: 'a' });
      await open([{ slug: 'a', product: product({ slug: 'a' }) }]);

      const button = element.querySelector<HTMLButtonElement>('[data-testid="wishlist-remove"]')!;
      button.focus();
      button.click();
      await settle();

      expect(document.activeElement).toBe(
        element.querySelector('[data-testid="wishlist-start-shopping"]'),
      );
    });

    it('moves focus to Start shopping after a clear', async () => {
      saved({ slug: 'a' }, { slug: 'b' });
      await open([
        { slug: 'a', product: product({ slug: 'a' }) },
        { slug: 'b', product: product({ id: 2, slug: 'b' }) },
      ]);

      link('Clear wishlist').click();
      await settle();

      const yes = link('Yes, clear it');
      yes.focus();
      yes.click();
      await settle();

      expect(document.activeElement).toBe(
        element.querySelector('[data-testid="wishlist-start-shopping"]'),
      );
    });

    /**
     * **The card's heart is the third removal control**, and it was the worst of them: the row vanished,
     * nothing was announced and focus went to `<body>`. It goes through `WishlistStore` now, so it
     * announces and re-homes focus exactly as the Remove button does.
     */
    it('announces and re-homes focus when the card heart removes a row', async () => {
      saved({ slug: 'a' }, { slug: 'b' });
      await open([
        { slug: 'a', product: product({ slug: 'a', name: 'Product A' }) },
        { slug: 'b', product: product({ id: 2, slug: 'b' }) },
      ]);

      const heart = element.querySelector<HTMLButtonElement>('.wish__item .card__wish')!;
      heart.focus();
      heart.click();
      await settle();

      expect(element.querySelectorAll('.wish__item')).toHaveLength(1);
      expect(element.querySelector('[role="status"]')?.textContent).toContain(
        'Product A removed from your wishlist',
      );
      expect(document.activeElement).toBe(
        element.querySelector('[data-testid="wishlist-remove"]'),
      );
    });

    /**
     * **It will not take focus from somewhere else.** A removal can happen while the shopper is in the
     * header - a heart on another surface, or a future undo - and moving focus then would be a worse
     * surprise than the one being fixed.
     */
    it('leaves focus alone when it was not on this page', async () => {
      saved({ slug: 'a' }, { slug: 'b' });
      await open([
        { slug: 'a', product: product({ slug: 'a' }) },
        { slug: 'b', product: product({ id: 2, slug: 'b' }) },
      ]);

      const outside = document.createElement('button');
      document.body.append(outside);
      outside.focus();

      // Made through the store, as a heart on another page would make it.
      TestBed.inject(WishlistStore).remove('a', 'A');
      await settle();

      expect(document.activeElement).toBe(outside);
      outside.remove();
    });

    /** A refused write removed nothing, so focus must not move either. */
    it('leaves focus on the button when storage refuses the removal', async () => {
      saved({ slug: 'a' }, { slug: 'b' });
      await open([
        { slug: 'a', product: product({ slug: 'a' }) },
        { slug: 'b', product: product({ id: 2, slug: 'b' }) },
      ]);

      const button = element.querySelector<HTMLButtonElement>('[data-testid="wishlist-remove"]')!;
      button.focus();

      // Storage stops answering, exactly as a full quota does mid-session.
      const refuse = vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
        throw new DOMException('refused', 'QuotaExceededError');
      });

      button.click();
      await settle();
      refuse.mockRestore();

      expect(element.querySelectorAll('.wish__item')).toHaveLength(2);
      expect(document.activeElement).toBe(button);
    });
  });

  describe('a page nobody should find in a search result', () => {
    it('asks not to be indexed and not to be stored', async () => {
      create();
      await settle();

      expect(TestBed.inject(Meta).getTag('name="robots"')?.content).toBe('noindex');
      expect(new Headers(response.headers ?? {}).get('Cache-Control')).toContain('no-store');
    });

    /** A client-side navigation would otherwise carry `noindex` onto the next page. */
    it('clears the robots tag on the way out', async () => {
      create();
      await settle();
      fixture.destroy();

      expect(TestBed.inject(Meta).getTag('name="robots"')).toBeNull();
    });

    it('titles itself for the shop the customer is in', async () => {
      create();
      await settle();

      expect(TestBed.inject(Title).getTitle()).toBe('Wishlist | Colombo Wardrobe');
    });
  });
});
