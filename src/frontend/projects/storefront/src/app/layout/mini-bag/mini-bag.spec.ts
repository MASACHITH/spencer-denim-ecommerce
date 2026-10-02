import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { MiniBag } from './mini-bag';
import { CartApi } from '../../core/cart/cart-api';
import { CartDto, CartLineDto, CartTotalsDto } from '../../core/cart/cart.model';
import { CartStore } from '../../core/cart/cart.store';
import { PriceDto } from '../../core/catalog/catalog.model';

function price(current: number): PriceDto {
  return { current, original: current, discountPercent: 0, hasDiscount: false, currency: 'LKR' };
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

/** A parent, because `open` is an input the header owns, exactly as it does for the mobile menu. */
@Component({
  imports: [MiniBag],
  template: `<sd-mini-bag [open]="open()" (closed)="closedCount = closedCount + 1" />`,
})
class Host {
  readonly open = signal(false);
  closedCount = 0;
}

describe('MiniBag (CART-02)', () => {
  let fixture: ComponentFixture<Host>;
  let host: Host;
  let element: HTMLElement;
  let http: HttpTestingController;
  let store: CartStore;

  async function settle(): Promise<void> {
    fixture.detectChanges();
    await fixture.whenStable();
  }

  async function withBag(cart: CartDto): Promise<void> {
    store.load();
    http
      .expectOne((request) => request.url === CartApi.url && request.method === 'GET')
      .flush(cart);
    await settle();
  }

  async function openDrawer(): Promise<void> {
    host.open.set(true);
    await settle();
  }

  beforeEach(async () => {
    localStorage.clear();
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        // A stub route for /product/:slug: the links inside the drawer are real router links, and a
        // router with no routes turns the click under test into an unmatched-route error.
        provideRouter([
          { path: 'product/:slug', children: [] },
          { path: '', children: [] },
          { path: 'bag', children: [] },
        ]),
        provideHttpClient(),
        provideHttpClientTesting(),
      ],
    });

    http = TestBed.inject(HttpTestingController);
    store = TestBed.inject(CartStore);
    fixture = TestBed.createComponent(Host);
    host = fixture.componentInstance;
    element = fixture.nativeElement as HTMLElement;
    await settle();
  });

  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('reuses the shared drawer rather than building a second one', () => {
    const drawer = element.querySelector('sd-drawer');

    expect(drawer).not.toBeNull();
    expect(drawer?.querySelector('[role="dialog"]')?.getAttribute('aria-modal')).toBe('true');
    expect(drawer?.querySelector('[role="dialog"]')?.getAttribute('id')).toBe('mini-bag');
  });

  /**
   * `sd-drawer` keeps its panel in the DOM when closed, so anything in its body would be in the HTML of
   * every page — and a bag is per-visitor, which would make every page's markup personal. The drawer
   * can only be opened in a browser, so on the server this is always empty.
   */
  it('renders no bag contents at all until it has been opened', async () => {
    await withBag({ lines: [line()], totals: totals() });

    expect(element.textContent).not.toContain('Relaxed Linen Blend Shirt');
    expect(element.querySelectorAll('.mini-bag__line')).toHaveLength(0);

    await openDrawer();
    expect(element.querySelectorAll('.mini-bag__line')).toHaveLength(1);
  });

  it('shows each line with its colour, size, quantity and line total', async () => {
    await withBag({ lines: [line()], totals: totals() });
    await openDrawer();

    const row = element.querySelector('.mini-bag__line') as HTMLElement;
    expect(row.textContent).toContain('Relaxed Linen Blend Shirt');
    expect(row.textContent).toContain('Beige');
    expect(row.textContent).toContain('M');
    expect(row.textContent).toContain('Qty 2');
    expect(row.textContent).toContain('Rs. 8,784');
  });

  it('shows the subtotal, says where delivery is worked out, and links to the bag page', async () => {
    await withBag({ lines: [line()], totals: totals() });
    await openDrawer();

    const foot = element.querySelector('.mini-bag__foot') as HTMLElement;
    expect(foot.textContent).toContain('Subtotal');
    expect(foot.textContent).toContain('Rs. 8,784');
    expect(foot.textContent).toContain('Delivery is calculated at checkout');
    expect(foot.querySelector<HTMLAnchorElement>('a')?.getAttribute('href')).toBe('/bag');
  });

  it('says the bag is empty and offers a way into the shop', async () => {
    await withBag(EMPTY);
    await openDrawer();

    expect(element.textContent).toContain('Your bag is empty');
    expect(element.querySelector<HTMLAnchorElement>('.mini-bag a')?.getAttribute('href')).toBe('/');
  });

  it('removes a line, naming the item on the control', async () => {
    await withBag({ lines: [line()], totals: totals() });
    await openDrawer();

    const remove = element.querySelector<HTMLButtonElement>('.mini-bag__remove');
    expect(remove?.getAttribute('aria-label')).toBe(
      'Remove Relaxed Linen Blend Shirt, Beige, size M from your bag',
    );

    remove?.click();
    await settle();

    http
      .expectOne((request) => request.url === `${CartApi.url}/items/1` && request.method === 'DELETE')
      .flush(EMPTY);
    await settle();

    expect(element.textContent).toContain('Your bag is empty');
  });

  it('reports a line that cannot be bought rather than hiding it', async () => {
    await withBag({
      lines: [line({ status: 'OutOfStock', availableQuantity: 0 })],
      totals: totals({ buyableItemCount: 0, itemsSubtotal: 0 }),
    });
    await openDrawer();

    expect(element.querySelector('.mini-bag__notice')?.textContent).toContain('Out of stock');
    expect(element.querySelectorAll('.mini-bag__line')).toHaveLength(1);
  });

  describe('what a screen reader hears', () => {
    function regions(): HTMLElement[] {
      return Array.from(element.querySelectorAll<HTMLElement>('[role="status"][aria-live="polite"]'));
    }

    /**
     * This component owns **two** regions on purpose, and exactly one of the two ever holds text. A dialog
     * with `aria-modal="true"` can hide everything outside it from a screen reader, so a change made
     * inside the open drawer has to be announced from inside it (doc 04 §4); a change made on the bag
     * page, with the drawer shut, has to be announced from outside, because the drawer's contents are not
     * in the accessibility tree at all. Both holding the sentence would announce it twice.
     *
     * **"Two" is about this component, not about the page**, and the earlier wording said it about the
     * page and was wrong. On `/bag` with the drawer open the document has **three** `role="status"`
     * regions — these two plus the bag page's own loading status — and **two** `role="alert"` regions
     * bound to the same error, because the drawer renders every line a second time. `aria-modal` is what
     * makes that harmless, and `accessibility.e2e.ts` scans that exact state rather than assuming it.
     */
    it('announces from outside the drawer while it is shut', async () => {
      await withBag({ lines: [line()], totals: totals() });

      store.remove(1, 'Linen shirt');
      http.expectOne((request) => request.method === 'DELETE').flush(EMPTY);
      await settle();

      const spoken = regions().filter((region) => (region.textContent ?? '').trim().length > 0);
      expect(spoken).toHaveLength(1);
      expect(spoken[0].closest('[role="dialog"]')).toBeNull();
      expect(spoken[0].textContent).toContain('removed from your bag');
    });

    /**
     * **Toggling the drawer must not re-announce the last change.**
     *
     * The first version computed both regions from `open()`, and `lastChange` is app-lifetime state —
     * so opening or closing the drawer moved the sentence from one region to the other, and a text
     * change in a live region is an announcement. The commonest path hit it: add (announced inside),
     * Escape (the identical sentence announced again from outside). "Exactly one region ever holds
     * text" was true; "nothing is announced twice" was not.
     */
    it('does not move the sentence when the drawer opens or closes', async () => {
      await withBag({ lines: [line()], totals: totals() });
      await openDrawer();

      store.remove(1, 'Linen shirt');
      http.expectOne((request) => request.method === 'DELETE').flush(EMPTY);
      await settle();

      const before = regions().map((region) => region.textContent ?? '');
      expect(before.filter((text) => text.trim().length > 0)).toHaveLength(1);

      host.open.set(false);
      await settle();
      expect(regions().map((region) => region.textContent ?? '')).toEqual(before);

      host.open.set(true);
      await settle();
      expect(regions().map((region) => region.textContent ?? '')).toEqual(before);
    });

    it('announces from inside the drawer while it is open', async () => {
      await withBag({ lines: [line()], totals: totals() });
      await openDrawer();

      store.remove(1, 'Linen shirt');
      http.expectOne((request) => request.method === 'DELETE').flush(EMPTY);
      await settle();

      const spoken = regions().filter((region) => (region.textContent ?? '').trim().length > 0);
      expect(spoken).toHaveLength(1);
      expect(spoken[0].closest('[role="dialog"]')).not.toBeNull();
    });
  });

  it('asks to be closed when a link inside it is followed', async () => {
    await withBag({ lines: [line()], totals: totals() });
    await openDrawer();

    element.querySelector<HTMLAnchorElement>('.mini-bag__name')?.click();
    await settle();

    expect(host.closedCount).toBeGreaterThan(0);
  });
});
