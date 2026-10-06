import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  TestRequest,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { ChangeDetectionStrategy, Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { QuickView, QuickViewIntent } from './quick-view';
import { EMPTY_CART } from '../../core/cart/cart.model';
import { CartStore } from '../../core/cart/cart.store';
import {
  ColourSwatchDto,
  PriceDto,
  ProductCardDto,
  ProductDetailDto,
  ProductImageDto,
  ProductSizeDto,
  ProductVariantDto,
} from '../../core/catalog/catalog.model';

const INDIGO = 1;
const ECRU = 2;
const SMALL = 10;
const MEDIUM = 11;
const LARGE = 12;

/**
 * Whether Add to Bag is refusing.
 *
 * It carries `aria-disabled` rather than the `disabled` attribute (W2-04 round 2: `disabled` on a
 * focused button blurs it to `<body>` with no `focusin`, which ejected the shopper from the open
 * dialog by their own press). So `.disabled` is `false` on it **by design**, and an assertion
 * reading `.disabled` would be one that cannot fail.
 */
function unavailable(button: HTMLButtonElement): boolean {
  return button.getAttribute('aria-disabled') === 'true';
}

function price(current: number, original = current): PriceDto {
  const discount = original > current ? Math.floor(((original - current) / original) * 100) : 0;
  return {
    current,
    original,
    discountPercent: discount,
    hasDiscount: discount > 0,
    currency: 'LKR',
  };
}

function colour(id: number, name: string, hex: string | null): ColourSwatchDto {
  return {
    id,
    name,
    familyName: name === 'Indigo' ? 'Blue' : name,
    familySlug: name.toLowerCase(),
    hexCode: hex,
    swatchImageUrl: null,
  };
}

function size(id: number, label: string, inStock = true): ProductSizeDto {
  return { id, label, scaleName: 'Apparel alpha', sortOrder: id, isInStock: inStock };
}

function variant(
  id: number,
  colourId: number,
  sizeId: number,
  available: number,
  amount = 5990,
): ProductVariantDto {
  return {
    id,
    sku: `SD-${colourId}-${sizeId}`,
    colourId,
    colourName: colourId === INDIGO ? 'Indigo' : 'Ecru',
    sizeId,
    sizeLabel: sizeId === SMALL ? 'S' : sizeId === MEDIUM ? 'M' : 'L',
    scaleName: 'Apparel alpha',
    price: price(amount, 7990),
    stockStatus: available === 0 ? 'OutOfStock' : available <= 3 ? 'LowStock' : 'InStock',
    isInStock: available > 0,
    availableQuantity: available,
  };
}

function image(id: number, colourId: number | null, sortOrder: number): ProductImageDto {
  return {
    id,
    url: `/media/${id}.jpg`,
    srcSet: null,
    thumbnailUrl: `/media/${id}-thumb.jpg`,
    zoomUrl: null,
    altText: `Photo ${id}`,
    role: 'Gallery',
    colourId,
    width: 1200,
    height: 1600,
    posterUrl: null,
    sortOrder,
  };
}

/** The card the dialog is opened from: everything CARD-01 renders and no variants at all. */
function card(overrides: Partial<ProductCardDto> = {}): ProductCardDto {
  return {
    id: 7,
    productCode: 'SD-0007',
    name: 'High-Rise Straight Jean',
    slug: 'high-rise-straight-jean',
    departmentName: 'Women',
    departmentSlug: 'women',
    categoryName: 'Jeans',
    categorySlug: 'jeans',
    imageUrl: '/media/card.jpg',
    imageSrcSet: '/media/card-600.webp 600w',
    hoverImageUrl: '/media/hover.jpg',
    hoverImageSrcSet: null,
    imageAltText: 'The jean, front',
    price: price(5990, 7990),
    rating: { average: 4.6, count: 38 },
    badge: 'Sale',
    stockStatus: 'InStock',
    isInStock: true,
    colours: [colour(INDIGO, 'Indigo', '#2b3a55'), colour(ECRU, 'Ecru', '#f2ece1')],
    ...overrides,
  };
}

/**
 * The product payload. Two colourways and three sizes by default, with the large priced apart —
 * which is what `ProductVariants.PriceOverride` does in the real catalogue, and the reason the
 * dialog has to re-price when a size is chosen.
 */
function detail(slug: string, overrides: Partial<ProductDetailDto> = {}): ProductDetailDto {
  const variants = [
    variant(101, INDIGO, SMALL, 9),
    variant(102, INDIGO, MEDIUM, 0),
    variant(103, INDIGO, LARGE, 4, 6490),
    variant(104, ECRU, SMALL, 2),
    variant(105, ECRU, LARGE, 5, 6490),
  ];

  return {
    id: 7,
    productCode: 'SD-0007',
    name: 'High-Rise Straight Jean',
    slug,
    department: { id: 1, name: 'Women', slug: 'women' },
    breadcrumb: [{ id: 12, name: 'Jeans', slug: 'jeans' }],
    shortDescription: 'A clean high-rise straight leg.',
    description: '<p>Rigid cotton denim.</p>',
    careInstructions: 'Machine wash cold.',
    attributes: {
      material: 'Denim',
      materialSlug: 'denim',
      fit: 'Straight',
      fitSlug: 'straight',
      occasion: null,
      occasionSlug: null,
      ageGroup: null,
      ageGroupSlug: null,
    },
    price: price(5990, 7990),
    minPrice: 5990,
    maxPrice: 6490,
    rating: { average: 4.6, count: 38 },
    badge: 'Sale',
    stockStatus: 'InStock',
    isInStock: true,
    seo: { title: 'High-Rise Straight Jean', description: null },
    sizeGuide: null,
    newUntilUtc: null,
    images: [image(1, INDIGO, 1), image(2, INDIGO, 2), image(3, ECRU, 1), image(4, null, 9)],
    colours: [colour(INDIGO, 'Indigo', '#2b3a55'), colour(ECRU, 'Ecru', '#f2ece1')],
    sizes: [size(SMALL, 'S'), size(MEDIUM, 'M'), size(LARGE, 'L')],
    variants,
    relatedProducts: [],
    ...overrides,
  };
}

@Component({
  imports: [QuickView],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <button id="trigger" type="button" (click)="open.set(true)">Quick view</button>
    @if (open()) {
      <sd-quick-view
        [product]="item()"
        [open]="true"
        [intent]="intent()"
        (closed)="closes = closes + 1; open.set(false)"
      />
    }
  `,
})
class Host {
  readonly item = signal<ProductCardDto>(card());
  readonly open = signal(true);
  readonly intent = signal<QuickViewIntent>('view');
  closes = 0;
}

/**
 * Quick View (QV-01).
 *
 * **What is pinned here and what is not.** The dialog's *behaviour as a dialog* — focus moving in,
 * Tab cycling, Escape, the scroll lock, focus returning to the trigger — belongs to the shared
 * `Modal` and is already pinned in `ui/modal/modal.spec.ts`; re-asserting it here would test that
 * component twice and this one not at all. What this file pins is the part that is Quick View's
 * own: which variant the dialog settles on, what the money says while it is unsettled, what the
 * bag is asked for, when the dialog takes itself away, and the three states the fetch can be in.
 * Layout at 320 px and a real axe pass are in `e2e/quick-view.e2e.ts`.
 */
describe('QuickView (QV-01)', () => {
  let http: HttpTestingController;

  function setUp(options: { product?: ProductCardDto; intent?: QuickViewIntent } = {}) {
    localStorage.clear();
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      imports: [Host],
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });

    http = TestBed.inject(HttpTestingController);

    const fixture = TestBed.createComponent(Host);
    if (options.product) {
      fixture.componentInstance.item.set(options.product);
    }
    if (options.intent) {
      fixture.componentInstance.intent.set(options.intent);
    }

    fixture.detectChanges();

    const element = fixture.nativeElement as HTMLElement;

    return {
      fixture,
      host: fixture.componentInstance,
      element,
      query: <T extends Element>(selector: string) => element.querySelector<T>(selector),
      all: <T extends Element>(selector: string) => [...element.querySelectorAll<T>(selector)],
      text: () => element.textContent ?? '',
      settle: async () => {
        fixture.detectChanges();
        await fixture.whenStable();
        fixture.detectChanges();
      },
    };
  }

  /** The one outstanding product request, whatever slug it carries. */
  function productRequest(): TestRequest {
    return http.expectOne((request) => request.url.startsWith('/api/catalog/products/'));
  }

  function addRequest(): TestRequest {
    return http.expectOne('/api/cart/items');
  }

  async function ready(
    options: { product?: ProductCardDto; intent?: QuickViewIntent } = {},
    overrides: Partial<ProductDetailDto> = {},
  ) {
    const harness = setUp(options);
    await harness.settle();

    const request = productRequest();
    const slug = decodeURIComponent(request.request.url.replace('/api/catalog/products/', ''));
    request.flush(detail(slug, overrides));
    await harness.settle();

    return harness;
  }

  afterEach(() => {
    document.body.classList.remove('sd-no-scroll');
  });

  describe('the fetch', () => {
    it('asks the catalogue for the card’s slug, once', async () => {
      const harness = setUp();
      await harness.settle();

      const request = productRequest();
      expect(request.request.url).toBe('/api/catalog/products/high-rise-straight-jean');
      expect(request.request.method).toBe('GET');

      request.flush(detail('high-rise-straight-jean'));
      await harness.settle();

      // Re-rendering must not re-ask: the guard is a slug, not a render count.
      await harness.settle();
      http.verify();
    });

    /**
     * The card already carries the name, the photograph and the price band, and they are on the
     * screen behind the dialog. Replacing them with a spinner would throw away an answer we have.
     */
    it('draws the card’s own name, photograph and price while the variants are in flight', async () => {
      const harness = setUp();
      await harness.settle();
      // Captured rather than re-asked for: `expectOne` consumes the match.
      const request = productRequest();

      expect(harness.query('.sd-modal__title')?.textContent).toBe('High-Rise Straight Jean');
      expect(harness.query<HTMLImageElement>('.qv__photo')?.getAttribute('src')).toBe(
        '/media/card.jpg',
      );
      expect(harness.query('.price__now')?.textContent).toContain('Rs. 5,990');
      expect(harness.query('.price__off')?.textContent).toContain('-25%');

      // And nothing that needs the variants is pretending to be there yet.
      expect(harness.query('[data-testid="quick-view-add"]')).toBeNull();
      expect(harness.query('.swatch')).toBeNull();
      expect(harness.text()).toContain('Loading sizes and colours');

      request.flush(detail('high-rise-straight-jean'));
      await harness.settle();
    });

    it('says the product is gone, and offers no dead link, when the catalogue 404s', async () => {
      const harness = setUp();
      await harness.settle();

      productRequest().flush({ title: 'Not found' }, { status: 404, statusText: 'Not Found' });
      await harness.settle();

      expect(harness.text()).toContain('no longer available');
      expect(harness.query('.qv__full')).toBeNull();
      expect(harness.query('[data-testid="quick-view-add"]')).toBeNull();
    });

    it('offers Try again — and asks again — when the store answers badly', async () => {
      const harness = setUp();
      await harness.settle();

      productRequest().flush('boom', { status: 500, statusText: 'Server Error' });
      await harness.settle();

      expect(harness.query('[role="alert"]')?.textContent).toContain('could not load this product');

      const retry = harness
        .all<HTMLButtonElement>('button')
        .find((button) => button.textContent?.includes('Try again'));
      expect(retry).toBeDefined();

      retry!.click();
      await harness.settle();

      productRequest().flush(detail('high-rise-straight-jean'));
      await harness.settle();

      expect(harness.query('[data-testid="quick-view-add"]')).not.toBeNull();
      expect(harness.query('[role="alert"]')).toBeNull();
    });
  });

  describe('the variant choice', () => {
    it('opens on the first colourway with stock and on no size at all', async () => {
      const harness = await ready();

      expect(harness.query('#qv-colour-label')?.textContent).toContain('Indigo');
      expect(harness.query('#qv-size-label')?.textContent).toContain('Choose a size');

      const pressed = harness
        .all<HTMLButtonElement>('.swatch')
        .filter((button) => button.getAttribute('aria-pressed') === 'true');
      expect(pressed.length).toBe(1);

      expect(
        harness.all('.size').filter((button) => button.getAttribute('aria-pressed') === 'true'),
      ).toEqual([]);
    });

    /** PDP-03's rule, and the reason for it: a guessed size would misprice the dialog. */
    it('says "From" until a size is chosen, then quotes that size’s own price', async () => {
      const harness = await ready();

      expect(harness.query('.qv__from')).not.toBeNull();
      expect(harness.query('.price__now')?.textContent).toContain('Rs. 5,990');

      harness.all<HTMLButtonElement>('.size')[2].click();
      await harness.settle();

      expect(harness.query('.qv__from')).toBeNull();
      expect(harness.query('.price__now')?.textContent).toContain('Rs. 6,490');
      expect(harness.query('#qv-size-label')?.textContent).toContain('L');
    });

    it('marks a size this colourway is not made in as such, and refuses it', async () => {
      const harness = await ready();

      // Ecru is made in S and L only.
      harness.all<HTMLButtonElement>('.swatch')[1].click();
      await harness.settle();

      const medium = harness.all<HTMLButtonElement>('.size')[1];
      expect(medium.disabled).toBe(true);
      expect(medium.getAttribute('aria-label')).toBe('Size M, not made in this colour');

      // And a size that is made but sold out stays selectable, because a shopper may look at it.
      harness.all<HTMLButtonElement>('.swatch')[0].click();
      await harness.settle();

      const soldOut = harness.all<HTMLButtonElement>('.size')[1];
      expect(soldOut.disabled).toBe(false);
      expect(soldOut.getAttribute('aria-label')).toBe('Size M, out of stock');
      expect(soldOut.classList.contains('is-sold-out')).toBe(true);
    });

    it('clears a size the new colourway does not come in rather than leaving a dead button lit', async () => {
      const harness = await ready();

      harness.all<HTMLButtonElement>('.size')[1].click();
      await harness.settle();
      expect(harness.query('#qv-size-label')?.textContent).toContain('M');

      harness.all<HTMLButtonElement>('.swatch')[1].click();
      await harness.settle();

      expect(harness.query('#qv-size-label')?.textContent).toContain('Choose a size');
    });

    it('un-picks a size when the chosen one is tapped again', async () => {
      const harness = await ready();

      harness.all<HTMLButtonElement>('.size')[0].click();
      await harness.settle();
      expect(harness.query('#qv-size-label')?.textContent).toContain('S');

      harness.all<HTMLButtonElement>('.size')[0].click();
      await harness.settle();
      expect(harness.query('#qv-size-label')?.textContent).toContain('Choose a size');
    });

    it('names a colour with its family when the two differ, and says when it is sold out', async () => {
      const harness = await ready({}, { variants: [variant(101, INDIGO, SMALL, 0)] });

      expect(harness.all('.swatch')[0].getAttribute('aria-label')).toBe(
        'Indigo (Blue), out of stock',
      );
    });
  });

  describe('the photographs', () => {
    it('shows the chosen colourway’s stills, and changes them with the colour', async () => {
      const harness = await ready();

      // Indigo has two of its own plus the shared shot with no colour.
      expect(harness.all('.qv__thumb').length).toBe(3);
      expect(harness.query<HTMLImageElement>('.qv__photo')?.getAttribute('src')).toBe(
        '/media/1.jpg',
      );

      harness.all<HTMLButtonElement>('.qv__thumb')[1].click();
      await harness.settle();
      expect(harness.query<HTMLImageElement>('.qv__photo')?.getAttribute('src')).toBe(
        '/media/2.jpg',
      );

      // Changing the colour re-stocks the rail and goes back to its first picture, rather than
      // leaving an index pointing into the colourway that has just gone.
      harness.all<HTMLButtonElement>('.swatch')[1].click();
      await harness.settle();

      expect(harness.query<HTMLImageElement>('.qv__photo')?.getAttribute('src')).toBe(
        '/media/3.jpg',
      );
      expect(
        harness.all('.qv__thumb').filter((b) => b.getAttribute('aria-pressed') === 'true').length,
      ).toBe(1);
    });

    it('falls back to the card’s photograph for a product with no media at all', async () => {
      const harness = await ready({}, { images: [] });

      expect(harness.query<HTMLImageElement>('.qv__photo')?.getAttribute('src')).toBe(
        '/media/card.jpg',
      );
      expect(harness.query('.qv__rail')).toBeNull();
    });

    it('draws a plain panel, not a broken image, when there is no photograph anywhere', async () => {
      const harness = await ready({ product: card({ imageUrl: null }) }, { images: [] });

      expect(harness.query('img.qv__photo')).toBeNull();
      expect(harness.query('.qv__photo--empty')).not.toBeNull();
    });

    it('offers no rail for a colourway with a single photograph', async () => {
      const harness = await ready({}, { images: [image(1, INDIGO, 1)] });

      expect(harness.query('.qv__rail')).toBeNull();
    });
  });

  describe('Add to Bag', () => {
    /**
     * **The attribute itself, pinned, because the whole fix is which attribute is used.**
     *
     * `disabled` on the element that currently holds focus makes the browser blur it to `<body>`
     * and fire no `focusin`, so `Modal.onFocusIn` never re-trapped and pressing Add to Bag threw
     * the shopper out of an open dialog. (ADM-22's settings form was fixed for the same reason.)
     * Where focus actually lands is a browser question and is measured in `quick-view.e2e.ts`;
     * what jsdom can hold is the contract that makes it possible — unavailable is *said*, never
     * *enforced by the platform*, and the element stays focusable and clickable either way.
     */
    it('says it is unavailable without ever carrying the disabled attribute', async () => {
      const harness = await ready();

      const add = harness.query<HTMLButtonElement>('[data-testid="quick-view-add"]')!;
      expect(add.getAttribute('aria-disabled')).toBe('true');
      expect(add.hasAttribute('disabled')).toBe(false);
      // Still in the tab order, which is what keeps the focus trap able to see it at all.
      expect(add.matches('button:not([disabled]):not([tabindex="-1"])')).toBe(true);

      harness.all<HTMLButtonElement>('.size')[0].click();
      await harness.settle();

      // And once it can add, the attribute is gone rather than set to 'false'.
      expect(
        harness
          .query<HTMLButtonElement>('[data-testid="quick-view-add"]')!
          .hasAttribute('aria-disabled'),
      ).toBe(false);
    });

    /** Clickable means the handler is what refuses, so it has to actually refuse. */
    it('adds nothing when a click reaches it while it is unavailable', async () => {
      const harness = await ready();

      harness.all<HTMLButtonElement>('.size')[1].click(); // the sold-out size
      await harness.settle();

      harness.query<HTMLButtonElement>('[data-testid="quick-view-add"]')!.click();
      await harness.settle();

      http.verify();
      expect(harness.host.closes).toBe(0);
    });

    it('is disabled, and says why, until a size is settled', async () => {
      const harness = await ready();

      const add = harness.query<HTMLButtonElement>('[data-testid="quick-view-add"]')!;
      expect(unavailable(add)).toBe(true);

      const reason = harness.query(`#${add.getAttribute('aria-describedby')}`);
      expect(reason?.textContent?.trim()).toBe('Choose a size to add this to your bag.');

      harness.all<HTMLButtonElement>('.size')[0].click();
      await harness.settle();

      expect(unavailable(harness.query<HTMLButtonElement>('[data-testid="quick-view-add"]')!)).toBe(
        false,
      );
      expect(
        harness
          .query<HTMLButtonElement>('[data-testid="quick-view-add"]')!
          .getAttribute('aria-describedby'),
      ).toBeNull();
    });

    it('stays disabled on a sold-out size and names that as the reason', async () => {
      const harness = await ready();

      harness.all<HTMLButtonElement>('.size')[1].click();
      await harness.settle();

      const add = harness.query<HTMLButtonElement>('[data-testid="quick-view-add"]')!;
      expect(unavailable(add)).toBe(true);
      expect(harness.query(`#${add.getAttribute('aria-describedby')}`)?.textContent?.trim()).toBe(
        'That size is out of stock. Choose another size.',
      );
    });

    it('sends the variant, the quantity and a label the bag can read out', async () => {
      const harness = await ready();

      harness.all<HTMLButtonElement>('.size')[2].click();
      await harness.settle();
      harness.all<HTMLButtonElement>('.qty__step')[1].click();
      await harness.settle();

      harness.query<HTMLButtonElement>('[data-testid="quick-view-add"]')!.click();
      await harness.settle();

      const request = addRequest();
      expect(request.request.body).toEqual({ variantId: 103, quantity: 2 });
    });

    /** The mini bag opens itself when an add lands, and two focus traps is a keyboard dead end. */
    it('takes itself away once its own add has landed', async () => {
      const harness = await ready();

      harness.all<HTMLButtonElement>('.size')[0].click();
      await harness.settle();
      harness.query<HTMLButtonElement>('[data-testid="quick-view-add"]')!.click();
      await harness.settle();

      expect(harness.host.closes).toBe(0);

      addRequest().flush(EMPTY_CART);
      await harness.settle();

      expect(harness.host.closes).toBe(1);
      expect(harness.query('[role="dialog"]')).toBeNull();
    });

    /**
     * A refused add is the one case where the dialog has to stay: the message and the control that
     * caused it belong on the same screen, and a shopper whose size has just sold out needs the
     * size list in front of them.
     */
    it('stays open, showing what the shop said, when the add is refused', async () => {
      const harness = await ready();

      harness.all<HTMLButtonElement>('.size')[0].click();
      await harness.settle();
      harness.query<HTMLButtonElement>('[data-testid="quick-view-add"]')!.click();
      await harness.settle();

      addRequest().flush({ title: 'Sold out' }, { status: 409, statusText: 'Conflict' });
      await harness.settle();

      expect(harness.host.closes).toBe(0);
      expect(harness.query('.qv__error')?.textContent).toBeTruthy();

      // A sold-out add makes the store re-read the bag, which is not this dialog's business but is
      // an outstanding request the verifier would otherwise report.
      http.expectOne('/api/cart').flush(EMPTY_CART);
      await harness.settle();
      expect(harness.query('[role="dialog"]')).not.toBeNull();
    });

    it('clamps the quantity to the chosen variant’s stock and says it has', async () => {
      const harness = await ready();

      // The large Indigo has four.
      harness.all<HTMLButtonElement>('.size')[2].click();
      await harness.settle();

      const box = harness.query<HTMLInputElement>('.qty__input')!;
      box.value = '9';
      box.dispatchEvent(new Event('input'));
      await harness.settle();

      expect(box.value).toBe('4');
      expect(harness.query('.qv__note')?.textContent).toContain('Only 4 available');
    });

    /**
     * With no size chosen the ceiling is **one**, not the colourway's total: offering 13 of
     * something nobody can buy 13 of is the defect the product page records.
     */
    it('offers one at a time while the size is still open', async () => {
      const harness = await ready();

      expect(harness.query<HTMLInputElement>('.qty__input')?.getAttribute('max')).toBe('1');
      expect(harness.query('.qv__note')?.textContent).toContain('Choose a size to see the total');
    });
  });

  /**
   * **The card's Add to Bag, finished here.** A card has no variants, so it cannot know whether a
   * choice is required; the dialog can, and this is where the brief's "a single-variant product may
   * add straight to the bag" actually happens.
   */
  describe('intent="add", which is the card’s Add to Bag', () => {
    const singleVariant: Partial<ProductDetailDto> = {
      colours: [colour(INDIGO, 'Indigo', '#2b3a55')],
      sizes: [size(SMALL, 'One size')],
      variants: [variant(101, INDIGO, SMALL, 6)],
      minPrice: 5990,
      maxPrice: 5990,
    };

    it('adds a product with one buyable variant without a second press', async () => {
      const harness = await ready({ intent: 'add' }, singleVariant);

      const request = addRequest();
      expect(request.request.body).toEqual({ variantId: 101, quantity: 1 });

      request.flush(EMPTY_CART);
      await harness.settle();

      expect(harness.host.closes).toBe(1);
    });

    it('does not add anything when a size has to be chosen', async () => {
      const harness = await ready({ intent: 'add' });

      http.verify();
      expect(harness.host.closes).toBe(0);
      expect(unavailable(harness.query<HTMLButtonElement>('[data-testid="quick-view-add"]')!)).toBe(
        true,
      );
    });

    it('does not add a single variant that is sold out', async () => {
      const harness = await ready(
        { intent: 'add' },
        { ...singleVariant, variants: [variant(101, INDIGO, SMALL, 0)] },
      );

      http.verify();
      expect(harness.host.closes).toBe(0);
    });

    /**
     * The Quick View trigger means "let me look", so it never adds anything by itself.
     *
     * The button is **still disabled** on the same one-size product, and that is the product page's
     * rule rather than an accident: `resolveSelection` gives a colourway a default and never gives
     * a size one, so even "One size" has to be tapped. Pinned so the difference between the two
     * triggers is a measured fact rather than a hope.
     */
    it('adds nothing for intent="view", even on a single-variant product', async () => {
      const harness = await ready({ intent: 'view' }, singleVariant);

      http.verify();
      expect(harness.host.closes).toBe(0);
      expect(unavailable(harness.query<HTMLButtonElement>('[data-testid="quick-view-add"]')!)).toBe(
        true,
      );

      harness.all<HTMLButtonElement>('.size')[0].click();
      await harness.settle();

      expect(unavailable(harness.query<HTMLButtonElement>('[data-testid="quick-view-add"]')!)).toBe(
        false,
      );
      http.verify();
    });
  });

  describe('the way out to the product page', () => {
    it('carries the chosen colourway and size into the URL so the choice is not made twice', async () => {
      const harness = await ready();

      expect(harness.query<HTMLAnchorElement>('.qv__full')?.getAttribute('href')).toBe(
        '/product/high-rise-straight-jean?colour=1',
      );

      harness.all<HTMLButtonElement>('.size')[2].click();
      await harness.settle();

      expect(harness.query<HTMLAnchorElement>('.qv__full')?.getAttribute('href')).toBe(
        '/product/high-rise-straight-jean?colour=1&size=12',
      );
    });

    /**
     * **The catalogue's slug, not the card's.** The two agree today; if they ever stop, the link
     * has to point at the product that was actually fetched. (`ProductCardDto.slug` is what the
     * fetch was keyed on, so this is the same string — pinned so a future change to either one
     * does not quietly send a shopper somewhere else.)
     */
    it('links to the slug the dialog fetched', async () => {
      const harness = await ready();

      expect(harness.query<HTMLAnchorElement>('.qv__full')?.getAttribute('href')).toContain(
        '/product/high-rise-straight-jean',
      );
    });
  });

  describe('what it says about stock', () => {
    it('counts down for a low-stock variant rather than only labelling it', async () => {
      const harness = await ready();

      harness.all<HTMLButtonElement>('.swatch')[1].click();
      await harness.settle();
      harness.all<HTMLButtonElement>('.size')[0].click();
      await harness.settle();

      expect(harness.query('.qv__stock')?.textContent?.trim()).toBe('Only 2 left');
    });

    it('says out of stock, and nothing can be added, for a product with none left', async () => {
      const harness = await ready(
        {},
        {
          stockStatus: 'OutOfStock',
          isInStock: false,
          variants: [variant(101, INDIGO, SMALL, 0), variant(102, INDIGO, MEDIUM, 0)],
        },
      );

      expect(harness.query('.qv__stock')?.textContent?.trim()).toBe('Out of stock');
      const add = harness.query<HTMLButtonElement>('[data-testid="quick-view-add"]')!;
      expect(unavailable(add)).toBe(true);
      expect(harness.query(`#${add.getAttribute('aria-describedby')}`)?.textContent?.trim()).toBe(
        'This product is out of stock.',
      );
    });
  });

  it('names the dialog after the product and names its own close button', async () => {
    const harness = await ready();

    expect(harness.query('[role="dialog"]')?.getAttribute('aria-labelledby')).toBe(
      harness.query('.sd-modal__title')?.id,
    );
    expect(harness.query('.sd-modal__close')?.getAttribute('aria-label')).toBe(
      'Close quick view',
    );
  });

  it('shows the category and the rating the card already had', async () => {
    const harness = await ready();

    expect(harness.query('.qv__cat')?.textContent).toContain('Women · Jeans');
    expect(harness.query('.rating')?.textContent).toContain('Rated 4.6 out of 5 from 38 reviews');
  });

  /**
   * **`CartStore.error` is a root signal**, holding the last thing that went wrong anywhere — the
   * header's own `GET /api/cart` on page load is enough. Bound straight to the template it opened a
   * brand-new dialog already showing a red message about something else entirely.
   */
  it('shows no bag error until this dialog has asked the bag for something', async () => {
    localStorage.clear();
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      imports: [Host],
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });

    http = TestBed.inject(HttpTestingController);

    // Something else broke the bag before this dialog existed, exactly as the header's own read can.
    const cart = TestBed.inject(CartStore);
    cart.load();
    http.expectOne('/api/cart').flush('boom', { status: 500, statusText: 'Server Error' });
    expect(cart.error()).not.toBeNull();

    const fixture = TestBed.createComponent(Host);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    const request = productRequest();
    request.flush(detail('high-rise-straight-jean'));
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    const element = fixture.nativeElement as HTMLElement;
    expect(element.querySelector('.qv__error')).toBeNull();

    // And it does appear for this dialog's own failure, so the gate is not simply "never".
    element.querySelectorAll<HTMLButtonElement>('.size')[0].click();
    fixture.detectChanges();
    await fixture.whenStable();
    element.querySelector<HTMLButtonElement>('[data-testid="quick-view-add"]')!.click();
    fixture.detectChanges();
    await fixture.whenStable();

    http
      .expectOne('/api/cart/items')
      .flush('boom', { status: 500, statusText: 'Server Error' });
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(element.querySelector('.qv__error')?.textContent).toBeTruthy();
  });

  it('shows no rating row for a product with no approved review', async () => {
    const harness = await ready({ product: card({ rating: { average: null, count: 0 } }) });

    expect(harness.query('.rating')).toBeNull();
  });
});
