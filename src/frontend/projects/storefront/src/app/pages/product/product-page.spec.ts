import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  TestRequest,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { ApplicationRef } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';

import { routes } from '../../app.routes';
import {
  ColourSwatchDto,
  PriceDto,
  ProductCardDto,
  ProductDetailDto,
  ProductImageDto,
  ProductVariantDto,
} from '../../core/catalog/catalog.model';
import { RECENTLY_VIEWED_KEY } from '../../core/catalog/recently-viewed.service';
import { DepartmentDto } from '../../core/navigation/catalog-departments.model';

const INDIGO = 1;
const ECRU = 2;
const SMALL = 10;
const MEDIUM = 11;
const LARGE = 12;

function price(current: number, original = current): PriceDto {
  const discount = original > current ? Math.floor(((original - current) / original) * 100) : 0;
  return { current, original, discountPercent: discount, hasDiscount: discount > 0, currency: 'LKR' };
}

function colour(id: number, name: string, hex: string): ColourSwatchDto {
  return { id, name, familyName: name, familySlug: name.toLowerCase(), hexCode: hex, swatchImageUrl: null };
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
    sizeLabel: String(sizeId),
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
    thumbnailUrl: null,
    zoomUrl: null,
    altText: null,
    role: 'Gallery',
    colourId,
    width: 1200,
    height: 1600,
    posterUrl: null,
    sortOrder,
  };
}

function relatedCard(id: number): ProductCardDto {
  return {
    id,
    productCode: `SD-R${id}`,
    name: `Related ${id}`,
    slug: `related-${id}`,
    departmentName: 'Women',
    departmentSlug: 'women',
    categoryName: 'Jeans',
    categorySlug: 'jeans',
    imageUrl: null,
    imageSrcSet: null,
    hoverImageUrl: null,
    hoverImageSrcSet: null,
    imageAltText: null,
    price: price(4990),
    rating: { average: null, count: 0 },
    badge: null,
    stockStatus: 'InStock',
    isInStock: true,
    colours: [],
  };
}

/**
 * The payload, built for whatever slug was asked for.
 *
 * It is built from the request rather than returned as a constant for the reason the listing's
 * round-3 fix records: a stub that answers what the caller expected can only confirm the caller
 * against itself. Here that matters because the page renders the API's slug, not the URL's.
 */
function detail(slug: string, overrides: Partial<ProductDetailDto> = {}): ProductDetailDto {
  return {
    id: 7,
    productCode: 'SD-0007',
    name: 'High-Rise Straight Jean',
    slug,
    department: { id: 1, name: 'Women', slug: 'women' },
    breadcrumb: [{ id: 12, name: 'Jeans', slug: 'jeans' }],
    shortDescription: 'A clean high-rise straight leg.',
    // HTML, because `Products.Description` is sanitised HTML (doc 08 §3.2) and the API never
    // sends anything else. A plain sentence here is what let the page print its own tags.
    description: '<p>Cut from rigid cotton denim with a touch of stretch.</p>',
    careInstructions: 'Machine wash cold, inside out.',
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
    rating: { average: 4.4, count: 12 },
    badge: 'Sale',
    stockStatus: 'InStock',
    isInStock: true,
    seo: { title: 'High-Rise Straight Jean', description: 'The straight leg, done properly.' },
    sizeGuide: { id: 1, name: "Women's apparel" },
    newUntilUtc: null,
    images: [image(101, INDIGO, 1), image(102, ECRU, 2), image(103, null, 3)],
    colours: [colour(INDIGO, 'Indigo', '#2E6DB4'), colour(ECRU, 'Ecru', '#F3EADA')],
    sizes: [
      { id: SMALL, label: 'S', scaleName: 'Apparel alpha', sortOrder: 1, isInStock: true },
      { id: MEDIUM, label: 'M', scaleName: 'Apparel alpha', sortOrder: 2, isInStock: true },
      { id: LARGE, label: 'L', scaleName: 'Apparel alpha', sortOrder: 3, isInStock: true },
    ],
    variants: [
      variant(1, INDIGO, SMALL, 5),
      variant(2, INDIGO, MEDIUM, 0),
      variant(3, INDIGO, LARGE, 2, 6490),
      variant(4, ECRU, SMALL, 4),
      variant(5, ECRU, MEDIUM, 9),
      // No Ecru in L.
    ],
    relatedProducts: [relatedCard(91), relatedCard(92)],
    ...overrides,
  };
}

const DEPARTMENTS: readonly DepartmentDto[] = [
  {
    id: 1,
    name: 'Women',
    slug: 'women',
    description: null,
    heroImageUrl: null,
    heroImageSrcSet: null,
    seoTitle: null,
    seoDescription: null,
    categories: [],
  },
];

describe('ProductPage (PDP-01..07)', () => {
  let http: HttpTestingController;
  let harness: RouterTestingHarness;

  function element(): HTMLElement {
    return harness.routeNativeElement as HTMLElement;
  }

  function text(): string {
    return element().textContent ?? '';
  }

  function query<T extends Element>(selector: string): T | null {
    return element().querySelector<T>(selector);
  }

  function queryAll<T extends Element>(selector: string): T[] {
    return [...element().querySelectorAll<T>(selector)];
  }

  /** The one outstanding product request, whatever slug it carries. */
  function productRequest(): TestRequest {
    return http.expectOne((request) => request.url.startsWith('/api/catalog/products/'));
  }

  /** The slug the page actually asked the API for, decoded. */
  function requestedSlug(request: TestRequest): string {
    return decodeURIComponent(request.request.url.replace('/api/catalog/products/', ''));
  }

  async function activated(): Promise<void> {
    await TestBed.inject(ApplicationRef).whenStable();
    harness.detectChanges();

    if (!harness.routeDebugElement) {
      throw new Error('The router never activated a component.');
    }
  }

  /** Navigates, then answers the request from the slug it carried. */
  async function open(url: string, overrides: Partial<ProductDetailDto> = {}) {
    harness = await RouterTestingHarness.create(url);
    await activated();

    const request = productRequest();
    request.flush(detail(requestedSlug(request), overrides));
    harness.detectChanges();

    return request;
  }

  async function settle(): Promise<void> {
    await harness.fixture.whenStable();
    harness.detectChanges();
  }

  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({
      providers: [provideRouter(routes), provideHttpClient(), provideHttpClientTesting()],
    });
  });

  afterEach(() => TestBed.inject(HttpTestingController).verify());

  beforeEach(() => {
    http = TestBed.inject(HttpTestingController);
  });

  describe('the request it makes', () => {
    it('asks the API for the slug in the address', async () => {
      const request = await open('/product/high-rise-straight-jean');

      expect(requestedSlug(request)).toBe('high-rise-straight-jean');
      expect(request.request.method).toBe('GET');
    });

    it('encodes a slug that is not a slug, rather than changing which resource it asks for', async () => {
      harness = await RouterTestingHarness.create('/product/not%20a%20slug%3Fq%3D1');
      await activated();

      const request = productRequest();
      expect(request.request.url).toBe('/api/catalog/products/not%20a%20slug%3Fq%3D1');

      request.flush({ title: 'Bad request', status: 400 }, { status: 400, statusText: 'Bad Request' });
      harness.detectChanges();
      // The not-found page the 400 falls through to builds its links from the department feed.
      http.expectOne('/api/catalog/departments').flush(DEPARTMENTS);
      harness.detectChanges();
    });
  });

  describe('what it shows (PDP-02)', () => {
    it('renders the name, the product code and the stock line', async () => {
      await open('/product/high-rise-straight-jean');

      expect(query('h1')?.textContent).toContain('High-Rise Straight Jean');
      expect(text()).toContain('SD-0007');
      expect(query('.pdp__stock')?.textContent?.trim()).toBe('In stock');
    });

    it('renders the price, the original price and the discount', async () => {
      await open('/product/high-rise-straight-jean');

      expect(query('.price__now')?.textContent).toContain('Rs. 5,990');
      expect(query('.price__was')?.textContent).toContain('Rs. 7,990');
      expect(query('.price__off')?.textContent).toContain('-25%');
    });

    it('says "From" while the sizes are priced apart and none is chosen', async () => {
      await open('/product/high-rise-straight-jean');

      expect(query('.pdp__from')).not.toBeNull();
    });

    it('drops "From" when every size is the same price', async () => {
      await open('/product/high-rise-straight-jean', { minPrice: 5990, maxPrice: 5990 });

      expect(query('.pdp__from')).toBeNull();
    });

    it('shows no discount markup on a product that is not reduced', async () => {
      await open('/product/high-rise-straight-jean', {
        price: price(5990),
        variants: [variant(1, INDIGO, SMALL, 5, 5990)],
        minPrice: 5990,
        maxPrice: 5990,
      });

      expect(query('.price__was')).toBeNull();
      expect(query('.price__off')).toBeNull();
    });

    it('renders the rating for a product that has been reviewed', async () => {
      await open('/product/high-rise-straight-jean');

      expect(query('.rating')).not.toBeNull();
    });

    it('renders no rating row at all for a product that has not', async () => {
      await open('/product/unreviewed', { rating: { average: null, count: 0 } });

      expect(query('.rating')).toBeNull();
    });

    it('renders the description in the HTML, not behind a fetch', async () => {
      await open('/product/high-rise-straight-jean');

      expect(text()).toContain('Cut from rigid cotton denim');
    });

    /**
     * `catalog.Products.Description` is `nvarchar(max)` holding sanitised HTML (doc 08 §3.2) and
     * the demo seeder writes `<p>…</p><p>…</p>` into it. Interpolating it printed the tags.
     */
    it('renders the description as rich text, not as its own tags', async () => {
      await open('/product/rich', {
        description: '<p>First paragraph.</p><p>Second paragraph.</p>',
      });

      const body = query('.rich');
      expect(body?.querySelectorAll('p')).toHaveLength(2);
      expect(text()).not.toContain('<p>');
      expect(text()).toContain('Second paragraph.');
    });

    it('strips the markup out of anything it copies into a meta tag', async () => {
      await open('/product/rich', {
        seo: { title: 'Rich', description: null },
        shortDescription: null,
        description: '<p>First paragraph.</p><p>Second paragraph.</p>',
      });

      expect(document.querySelector('meta[name="description"]')?.getAttribute('content')).toBe(
        'First paragraph. Second paragraph.',
      );
    });

    it('falls back to the plain teaser when there is no long copy', async () => {
      await open('/product/teaser-only', { description: null });

      expect(query('.rich')).toBeNull();
      expect(text()).toContain('A clean high-rise straight leg.');
    });
  });

  describe('the selection in the URL (PDP-03)', () => {
    it('opens on the first colourway with stock', async () => {
      await open('/product/high-rise-straight-jean');

      expect(query('.pdp__chosen')?.textContent).toContain('Indigo');
    });

    it('honours the colour the URL asks for', async () => {
      await open('/product/high-rise-straight-jean?colour=2');

      expect(query('.pdp__chosen')?.textContent).toContain('Ecru');
    });

    it('shows that colourway’s photographs', async () => {
      await open('/product/high-rise-straight-jean?colour=2');

      // Ecru's own shot plus the one that belongs to every colour; never Indigo's.
      const sources = queryAll<HTMLImageElement>('.gallery__thumb img').map((img) =>
        img.getAttribute('src'),
      );
      expect(sources).toEqual(['/media/102.jpg', '/media/103.jpg']);
    });

    it('re-prices on the size the URL asks for', async () => {
      await open('/product/high-rise-straight-jean?colour=1&size=12');

      expect(query('.price__now')?.textContent).toContain('Rs. 6,490');
      expect(query('.pdp__from')).toBeNull();
      expect(query('.pdp__stock')?.textContent?.trim()).toBe('Only 2 left');
    });

    it('says a chosen size is out of stock rather than hiding it', async () => {
      await open('/product/high-rise-straight-jean?colour=1&size=11');

      expect(query('.pdp__stock')?.textContent?.trim()).toBe('Out of stock');
    });

    it('disables a size the chosen colourway is not made in', async () => {
      await open('/product/high-rise-straight-jean?colour=2');

      const sizes = queryAll<HTMLButtonElement>('.size');
      expect(sizes.map((button) => button.disabled)).toEqual([false, false, true]);
      expect(sizes[2].getAttribute('aria-label')).toContain('not made in this colour');
    });

    it('ignores a colour id the product does not have', async () => {
      await open('/product/high-rise-straight-jean?colour=999&size=12');

      expect(query('.pdp__chosen')?.textContent).toContain('Indigo');
    });

    it('writes the chosen colour into the address bar', async () => {
      await open('/product/high-rise-straight-jean');

      queryAll<HTMLButtonElement>('.swatch')[1].click();
      await settle();

      expect(TestBed.inject(Router).url).toContain('colour=2');
      expect(query('.pdp__chosen')?.textContent).toContain('Ecru');
    });

    it('clears the size when the new colourway is not made in it', async () => {
      await open('/product/high-rise-straight-jean?colour=1&size=12');

      queryAll<HTMLButtonElement>('.swatch')[1].click();
      await settle();

      expect(TestBed.inject(Router).url).not.toContain('size=');
      expect(query('#pdp-size-label')?.textContent).toContain('Choose a size');
    });
  });

  describe('quantity (PDP-03)', () => {
    it('clamps to what is left and says so', async () => {
      await open('/product/high-rise-straight-jean?colour=1&size=12');

      const input = query<HTMLInputElement>('#pdp-quantity')!;
      input.value = '9';
      input.dispatchEvent(new Event('input'));
      harness.detectChanges();

      expect(query<HTMLInputElement>('#pdp-quantity')?.value).toBe('2');
      expect(query('.pdp__qty-note')?.textContent).toContain('Only 2 available');
    });

    it('quotes no total, and offers no second unit, until a size is chosen', async () => {
      await open('/product/high-rise-straight-jean');

      // The price line still says "From", so a total here would be a definite sum at an
      // indefinite price, for an amount of nothing in particular.
      expect(query('.pdp__from')).not.toBeNull();
      expect(query('.pdp__qty-note')?.textContent).toContain('Choose a size');
      expect(query('.pdp__qty-note')?.textContent).not.toContain('=');

      // And the ceiling is one, not the whole colourway's stock.
      const up = queryAll<HTMLButtonElement>('.qty__step')[1];
      expect(up.disabled).toBe(true);
    });

    it('says why, rather than quoting a stock figure that did not cause the clamp', async () => {
      await open('/product/high-rise-straight-jean');

      const input = query<HTMLInputElement>('#pdp-quantity')!;
      input.value = '9';
      input.dispatchEvent(new Event('input'));
      harness.detectChanges();

      expect(query('.pdp__qty-note')?.textContent).toContain('Choose a size');
      // Indigo has seven across its sizes; none of them is a number that explains this clamp.
      expect(query('.pdp__qty-note')?.textContent).not.toContain('7');
    });

    /**
     * The box is bound with `[value]`, which writes nothing when the bound number has not
     * changed — so a typed value that clamped to the number already held left the raw text on
     * screen while the page priced and stepped from something else.
     */
    it('writes the clamped number back into the box', async () => {
      await open('/product/high-rise-straight-jean?colour=1&size=10');

      const input = query<HTMLInputElement>('#pdp-quantity')!;

      for (const typed of ['0', '', '1.7', '-4', 'abc']) {
        input.value = typed;
        input.dispatchEvent(new Event('input'));
        harness.detectChanges();

        expect(query<HTMLInputElement>('#pdp-quantity')?.value).toBe('1');
      }
    });

    it('shows what the chosen quantity costs', async () => {
      await open('/product/high-rise-straight-jean?colour=1&size=10');

      query<HTMLButtonElement>('.qty__step:last-of-type')?.click();
      harness.detectChanges();

      expect(query('.pdp__qty-note')?.textContent).toContain('2 × Rs. 5,990 = Rs. 11,980');
    });
  });

  describe('the buttons that are not there', () => {
    it('offers no Add to Bag or Buy Now while the bag API does not exist (W2-09)', async () => {
      await open('/product/high-rise-straight-jean');

      expect(text()).not.toContain('Add to Bag');
      expect(text()).not.toContain('Buy Now');
      expect(queryAll('button').filter((button) => button.hasAttribute('disabled') && button.textContent?.includes('Bag'))).toEqual([]);
    });

    it('saves to the same per-browser wishlist the card uses (W2-08)', async () => {
      await open('/product/high-rise-straight-jean');

      const heart = query<HTMLButtonElement>('.pdp__wish')!;
      expect(heart.getAttribute('aria-pressed')).toBe('false');

      heart.click();
      harness.detectChanges();

      expect(query('.pdp__wish')?.getAttribute('aria-pressed')).toBe('true');
      expect(localStorage.getItem('sd.wishlist.local')).toContain('high-rise-straight-jean');
    });
  });

  /**
   * PDP-04. The one condition the trigger hangs on is `hasSizeGuideContent`: a guide the API sends
   * with no measurements is still not enough to earn a button. What the dialog then *draws* is
   * pinned in `size-guide-modal.spec.ts`; this block is only about whether the trigger exists.
   */
  describe('the size guide (PDP-04)', () => {
    it('offers no trigger for a guide that carries no measurements', async () => {
      await open('/product/high-rise-straight-jean');

      // The fixture sends a guide here — an id and a name — and it is still not enough.
      expect(query('.pdp__guide')).toBeNull();
      expect(text()).not.toContain('size guide');
    });

    it('offers nothing when the product has no size guide at all', async () => {
      await open('/product/no-guide', { sizeGuide: null });

      expect(query('.pdp__guide')).toBeNull();
    });

    it('offers the trigger when the API sends measurements', async () => {
      // Issue #32, expressed as a test: content arrives and nothing else on the page moves.
      await open('/product/with-a-guide', {
        sizeGuide: {
          id: 1,
          name: "Women's apparel",
          content: {
            introHtml: '<p>Measurements are in centimetres.</p>',
            table: { columns: ['Size', 'Bust'], rows: [['S', '83-87']] },
            measuringTipsHtml: null,
            imageUrl: null,
            imageAltText: null,
          },
        },
      });

      const trigger = query<HTMLButtonElement>('.pdp__guide')!;
      expect(trigger.textContent).toContain("Women's apparel size guide");

      trigger.click();
      harness.detectChanges();

      expect(query('[role="dialog"]')).not.toBeNull();
    });
  });

  /**
   * A size that is made but sold out used to render byte for byte like one in stock: the screen
   * reader was told and the sighted shopper was not.
   */
  describe('sold out (PDP-03)', () => {
    it('marks a sold-out size without disabling it', async () => {
      await open('/product/high-rise-straight-jean?colour=1');

      const sizes = queryAll<HTMLButtonElement>('.size');
      // Indigo: S has five, M has none, L has two.
      expect(sizes[0].classList.contains('is-sold-out')).toBe(false);
      expect(sizes[1].classList.contains('is-sold-out')).toBe(true);
      expect(sizes[1].disabled).toBe(false);
      expect(sizes[1].getAttribute('aria-label')).toContain('out of stock');
    });

    it('keeps "sold out" and "not made in this colour" apart', async () => {
      await open('/product/high-rise-straight-jean?colour=2');

      const sizes = queryAll<HTMLButtonElement>('.size');
      // Ecru has no L variant at all, which is a disabled button, not a sold-out one.
      expect(sizes[2].disabled).toBe(true);
      expect(sizes[2].classList.contains('is-sold-out')).toBe(false);
      expect(sizes[2].getAttribute('aria-label')).toContain('not made in this colour');
    });

    it('marks a colourway with nothing left', async () => {
      await open('/product/sold-out', {
        variants: [variant(1, INDIGO, SMALL, 0), variant(2, ECRU, SMALL, 4)],
      });

      const dots = queryAll<HTMLElement>('.swatch__dot');
      expect(dots[0].classList.contains('is-sold-out')).toBe(true);
      expect(dots[1].classList.contains('is-sold-out')).toBe(false);
    });
  });

  describe('related and recently viewed (PDP-06, PDP-07)', () => {
    it('renders the related products on product cards', async () => {
      await open('/product/high-rise-straight-jean');

      expect(queryAll('.card__name').map((name) => name.textContent?.trim())).toEqual([
        'Related 91',
        'Related 92',
      ]);
    });

    it('renders no related row when the product has none', async () => {
      await open('/product/lonely', { relatedProducts: [] });

      expect(text()).not.toContain('You may also like');
    });

    it('shows what was looked at before, and never the product on screen', async () => {
      localStorage.setItem(
        RECENTLY_VIEWED_KEY,
        JSON.stringify([relatedCard(55), { ...relatedCard(56), slug: 'high-rise-straight-jean' }]),
      );

      await open('/product/high-rise-straight-jean', { relatedProducts: [] });
      await settle();

      expect(text()).toContain('Recently viewed');
      const names = queryAll('.card__name').map((name) => name.textContent?.trim());
      expect(names).toEqual(['Related 55']);
    });

    it('records the product it is showing', async () => {
      await open('/product/high-rise-straight-jean');
      await settle();

      expect(localStorage.getItem(RECENTLY_VIEWED_KEY)).toContain('high-rise-straight-jean');
    });
  });

  describe('when the API says no', () => {
    /** The not-found page asks for the department list to build its links. */
    function answerDepartments(): void {
      harness.detectChanges();
      http.expectOne('/api/catalog/departments').flush(DEPARTMENTS);
    }

    async function fail(status: number, body: object = { title: 'nope', status }) {
      harness = await RouterTestingHarness.create('/product/gone');
      await activated();

      productRequest().flush(body, { status, statusText: 'x' });
      harness.detectChanges();
    }

    it('answers a 404 with the site’s own not-found page', async () => {
      await fail(404);
      answerDepartments();
      harness.detectChanges();

      expect(text()).toContain('We could not find that page');
      expect(document.querySelector('meta[name="robots"]')?.getAttribute('content')).toBe('noindex');
    });

    it('answers a 400 the same way: that address is not a product', async () => {
      await fail(400, { title: 'One or more query parameters are invalid.', errors: { slug: ['slug is invalid.'] } });
      answerDepartments();
      harness.detectChanges();

      expect(text()).toContain('We could not find that page');
    });

    it('treats a 500 as the store failing, not the product missing', async () => {
      await fail(500);

      expect(text()).toContain('We could not show this product');
      expect(text()).toContain('try again in a moment');
      expect(text()).not.toContain('We could not find that page');
    });

    /**
     * The server answers this 503 with `Retry-After`, and a 503 must not carry `noindex`: the whole
     * point of the status code is that the URL keeps its place, and a crawler that reads the body of
     * an error page would take the meta tag at its word (#30). The tag used to be set here, and
     * nothing asserted either way.
     */
    it('asks no crawler to drop a product the store merely failed to serve', async () => {
      // As the not-found page would have left it, on the way in from a product that really is gone.
      // Exactly one, whatever an earlier test left behind, or `removeTag` could take the wrong one.
      document.querySelectorAll('meta[name="robots"]').forEach((tag) => tag.remove());
      document.head.insertAdjacentHTML('beforeend', '<meta name="robots" content="noindex">');

      await fail(500);

      expect(document.querySelector('meta[name="robots"]')).toBeNull();
    });

    it('says something different when the browser could not reach the store', async () => {
      harness = await RouterTestingHarness.create('/product/gone');
      await activated();

      productRequest().error(new ProgressEvent('error'), { status: 0, statusText: 'Unknown Error' });
      harness.detectChanges();

      expect(text()).toContain('could not reach the store');
    });

    it('asks again when the shopper presses Try again', async () => {
      await fail(500);

      query<HTMLButtonElement>('.pdp__state-actions .btn--primary')?.click();
      harness.detectChanges();

      const retry = productRequest();
      expect(requestedSlug(retry)).toBe('gone');

      retry.flush(detail('gone'));
      harness.detectChanges();

      expect(query('h1')?.textContent).toContain('High-Rise Straight Jean');
    });

    it('shows no raw status code or stack anywhere', async () => {
      await fail(500, { title: 'Internal Server Error', status: 500, detail: 'at Foo.Bar()' });

      expect(text()).not.toContain('500');
      expect(text()).not.toContain('Foo.Bar');
    });
  });

  describe('head tags (GLB-11)', () => {
    it('sets the title, the description and a canonical address', async () => {
      await open('/product/high-rise-straight-jean?colour=2&size=11');

      expect(document.title).toBe('High-Rise Straight Jean | Colombo Wardrobe');
      expect(document.querySelector('meta[name="description"]')?.getAttribute('content')).toBe(
        'The straight leg, done properly.',
      );
      // The canonical address is the product, not the colourway: one page, not one per colour.
      expect(document.querySelector('link[rel="canonical"]')?.getAttribute('href')).toBe(
        '/product/high-rise-straight-jean',
      );
    });

    it('does not say the brand twice when an admin already wrote it', async () => {
      await open('/product/x', {
        seo: { title: 'The Jean | Colombo Wardrobe', description: null },
      });

      expect(document.title).toBe('The Jean | Colombo Wardrobe');
    });

    it('takes the canonical slug from the API, not from the address bar', async () => {
      harness = await RouterTestingHarness.create('/product/HIGH-RISE-STRAIGHT-JEAN');
      await activated();

      const request = productRequest();
      // The API normalises the slug and answers with the one it knows.
      request.flush(detail('high-rise-straight-jean'));
      harness.detectChanges();

      expect(document.querySelector('link[rel="canonical"]')?.getAttribute('href')).toBe(
        '/product/high-rise-straight-jean',
      );
    });
  });

  describe('media that is not there yet (W1-04)', () => {
    it('renders a placeholder, not a broken picture, when a product has no images', async () => {
      await open('/product/unphotographed', { images: [] });

      expect(query('.gallery__placeholder--stage')).not.toBeNull();
      expect(queryAll('.gallery__img')).toEqual([]);
    });

    it('drops an image the browser could not fetch and keeps the frame', async () => {
      await open('/product/high-rise-straight-jean?colour=2');

      const stage = query<HTMLImageElement>('.gallery__img')!;
      stage.dispatchEvent(new Event('error'));
      harness.detectChanges();

      expect(query('.gallery__img')).toBeNull();
      expect(query('.gallery__placeholder--stage')).not.toBeNull();
    });

    it('shows no thumbnail rail for a product with one photograph', async () => {
      await open('/product/one-photo', { images: [image(101, null, 1)] });

      expect(query('[role="tablist"]')).toBeNull();
      expect(query('.gallery__img')).not.toBeNull();
    });
  });

  /**
   * The media pipeline builds four WebP renditions of every public image (doc 08 §3.3), and the
   * gallery is the one place that draws the same photograph at three sizes nowhere near each
   * other: a 56 px thumbnail, a stage of a few hundred pixels and a full-screen view magnified to
   * three times. One URL for all three is a 1200 px file behind a 56 px box at one end and a
   * soft picture at the other, so each size names its own rendition.
   */
  describe('renditions (PDP-01)', () => {
    const LARGE = '/media/public/renditions/cccc/large.webp';
    const THUMB = '/media/public/renditions/cccc/thumb.webp';
    const ZOOM = '/media/public/renditions/cccc/zoom.webp';
    const SRCSET = `${THUMB} 160w, ${LARGE} 1200w, ${ZOOM} 2000w`;

    /** One photographed product: the first slide has renditions, the second has none. */
    const PHOTOGRAPHED: Partial<ProductDetailDto> = {
      images: [
        { ...image(101, null, 1), url: LARGE, srcSet: SRCSET, thumbnailUrl: THUMB, zoomUrl: ZOOM },
        image(102, null, 2),
      ],
    };

    it('draws the rail from the thumb and the stage from the large rendition', async () => {
      await open('/product/photographed', PHOTOGRAPHED);

      expect(query<HTMLImageElement>('.gallery__thumb img')?.getAttribute('src')).toBe(THUMB);

      const stage = query<HTMLImageElement>('.gallery__img');
      expect(stage?.getAttribute('src')).toBe(LARGE);
      expect(stage?.getAttribute('srcset')).toBe(SRCSET);
      expect(stage?.getAttribute('sizes')).toContain('100vw');
    });

    it('magnifies the zoom rendition in the full-screen view', async () => {
      await open('/product/photographed', PHOTOGRAPHED);

      query<HTMLButtonElement>('.gallery__open')?.click();
      await settle();

      expect(query<HTMLImageElement>('.viewer__img')?.getAttribute('src')).toBe(ZOOM);
    });

    /** A photograph taken before the pipeline existed still shows, at its one available size. */
    it('falls back to the one URL a photograph without renditions has', async () => {
      await open('/product/one-photo', { images: [image(101, null, 1)] });

      const stage = query<HTMLImageElement>('.gallery__img');
      expect(stage?.getAttribute('src')).toBe('/media/101.jpg');
      expect(stage?.hasAttribute('srcset')).toBe(false);
      expect(stage?.hasAttribute('sizes')).toBe(false);
    });
  });
});
