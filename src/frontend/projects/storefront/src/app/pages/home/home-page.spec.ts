import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, TestRequest, provideHttpClientTesting } from '@angular/common/http/testing';
import { ApplicationRef } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';

import { routes } from '../../app.routes';
import { HomePageDto, HomeSectionDto, HomeSectionItemDto, HomeSectionType } from '../../core/content/content.model';
import { HOME_SECTION_TYPES } from '../../core/content/home-sections';
import { ProductCardDto } from '../../core/catalog/catalog.model';

function item(overrides: Partial<HomeSectionItemDto> = {}): HomeSectionItemDto {
  return {
    id: 1,
    title: null,
    subtitle: null,
    body: null,
    buttonLabel: null,
    linkUrl: null,
    imageUrl: null,
    imageSrcSet: null,
    imageAltText: null,
    mobileImageUrl: null,
    mobileImageSrcSet: null,
    mobileImageAltText: null,
    videoUrl: null,
    videoPosterUrl: null,
    iconKey: null,
    departmentSlug: null,
    departmentName: null,
    productSlug: null,
    sortOrder: 1,
    ...overrides,
  };
}

function section(type: HomeSectionType, overrides: Partial<HomeSectionDto> = {}): HomeSectionDto {
  const order = HOME_SECTION_TYPES.indexOf(type) + 1;

  return {
    id: order,
    type,
    title: type,
    subtitle: null,
    sortOrder: order,
    linkUrl: null,
    linkLabel: null,
    overlay: null,
    items: [],
    products: [],
    ...overrides,
  };
}

function card(id: number): ProductCardDto {
  return {
    id,
    productCode: `SD-${id}`,
    name: `Product ${id}`,
    slug: `product-${id}`,
    departmentName: 'Women',
    departmentSlug: 'women',
    categoryName: 'Jeans',
    categorySlug: 'jeans',
    imageUrl: null,
    imageSrcSet: null,
    hoverImageUrl: null,
    hoverImageSrcSet: null,
    imageAltText: null,
    price: { current: 5990, original: 7990, discountPercent: 25, hasDiscount: true, currency: 'LKR' },
    rating: { average: 4.5, count: 8 },
    badge: 'New',
    stockStatus: 'InStock',
    isInStock: true,
    colours: [],
  };
}

/** The three department tiles the seeder writes, in the shape the API sends them. */
const DEPARTMENT_TILES = section('ShopByDepartment', {
  title: 'Shop by department',
  items: [
    item({ id: 21, departmentSlug: 'women', departmentName: 'Women', linkUrl: '/women', buttonLabel: 'Shop Women', sortOrder: 1 }),
    item({ id: 22, departmentSlug: 'men', departmentName: 'Men', linkUrl: '/men', buttonLabel: 'Shop Men', sortOrder: 2 }),
    item({ id: 23, departmentSlug: 'kids', departmentName: 'Kids', linkUrl: '/kids', buttonLabel: 'Shop Kids', sortOrder: 3 }),
  ],
});

const BENEFITS = section('Benefits', {
  title: null,
  items: [
    item({ id: 91, iconKey: 'delivery', title: 'Island-wide delivery', body: 'We deliver across Sri Lanka.', sortOrder: 1 }),
    item({ id: 92, iconKey: 'returns', title: 'Returns and exchanges', body: 'Get in touch and we will sort it out.', sortOrder: 2 }),
    item({ id: 93, iconKey: 'quality', title: 'Made to last', body: 'Fabrics chosen for the weather here.', sortOrder: 3 }),
  ],
});

describe('HomePage', () => {
  let http: HttpTestingController;
  let harness: RouterTestingHarness;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideRouter(routes), provideHttpClient(), provideHttpClientTesting()],
    });
  });

  afterEach(() => TestBed.inject(HttpTestingController).verify());

  /**
   * The department tree the page's fallback links are drawn from (GLB-05). It is the shell's feed,
   * not this page's, but the page reads it for the way-into-the-shop block, so it is answered here
   * with the same three departments the seeder writes.
   */
  function flushDepartments(): void {
    http
      .match('/api/catalog/departments')
      .forEach((request) =>
        request.flush([
          { id: 1, name: 'Women', slug: 'women', description: null, heroImageUrl: null, seoTitle: null, seoDescription: null, categories: [] },
          { id: 2, name: 'Men', slug: 'men', description: null, heroImageUrl: null, seoTitle: null, seoDescription: null, categories: [] },
          { id: 3, name: 'Kids', slug: 'kids', description: null, heroImageUrl: null, seoTitle: null, seoDescription: null, categories: [] },
        ]),
      );
  }

  /** Opens `/` and answers the content call with `sections`, or fails it with `failure`. */
  async function open(
    sections: readonly HomeSectionDto[] | null,
    failure?: { status: number; statusText: string },
  ): Promise<void> {
    http = TestBed.inject(HttpTestingController);
    harness = await RouterTestingHarness.create('/');
    flushDepartments();

    const request = contentRequest();
    if (failure) {
      request.flush({ title: 'Nope' }, failure);
    } else {
      request.flush({ sections } as HomePageDto);
    }

    await TestBed.inject(ApplicationRef).whenStable();
    harness.detectChanges();
  }

  function contentRequest(): TestRequest {
    return http.expectOne((request) => request.url === '/api/content/home');
  }

  function root(): HTMLElement {
    return harness.routeNativeElement as HTMLElement;
  }

  function text(): string {
    return root().textContent ?? '';
  }

  function queryAll<T extends Element>(selector: string): T[] {
    return [...root().querySelectorAll<T>(selector)];
  }

  /** The order the blocks appear down the page, by their component's element name. */
  function rendered(): string[] {
    return [...root().children]
      .map((node) => node.tagName.toLowerCase())
      .filter((name) => name.startsWith('sd-'));
  }

  /**
   * The picture a shared link to the home page shows (GLB-11).
   *
   * The home page has no artwork of its own — everything on it is ADM-17 content — so the card's
   * picture is chosen from the sections the client manages, which makes *which* one it chooses the
   * whole of the behaviour. It is tested here rather than in the browser suite because it is a
   * selection rule over a payload, and here the payload can be shaped; `seo.e2e.ts` covers the
   * product page's picture on the wire, where the shape is the stub's.
   */
  describe('the share card’s picture (GLB-11)', () => {
    function ogImage(): string | null {
      return document.head.querySelector('meta[property="og:image"]')?.getAttribute('content') ?? null;
    }

    function ogImageAlt(): string | null {
      return document.head.querySelector('meta[property="og:image:alt"]')?.getAttribute('content') ?? null;
    }

    function twitterCard(): string | null {
      return document.head.querySelector('meta[name="twitter:card"]')?.getAttribute('content') ?? null;
    }

    it('takes the hero’s still, not its video, because a card cannot play one', async () => {
      await open([
        section('HeroVideo', {
          items: [
            item({
              videoUrl: '/media/public/originals/hero.webm',
              videoPosterUrl: '/media/public/originals/hero-poster.webp',
              imageUrl: '/media/public/renditions/a/large.webp',
              imageAltText: 'A linen shirt on a balcony',
            }),
          ],
        }),
      ]);

      // Absolute, because the Open Graph consumers do not resolve a relative URL.
      expect(ogImage()).toBe(`${location.origin}/media/public/originals/hero-poster.webp`);
      expect(ogImageAlt()).toBe('A linen shirt on a balcony');
      expect(twitterCard()).toBe('summary_large_image');
    });

    /**
     * **The defect this test exists for.** The first version took the first non-null candidate and
     * concatenated the origin onto it, so the browser-test stub's hero — whose three images are all
     * `data:` URIs — published
     * `og:image="http://localhost:4960/data:image/gif;base64,R0lGOD…"`. A `data:` URI is not a valid
     * `og:image` in any case, because the consumers fetch the URL. So a candidate a crawler could not
     * fetch is skipped, and the next real picture on the page is used instead of nothing.
     */
    it('skips a picture a crawler could not fetch and finds the next real one', async () => {
      const dataUri = 'data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7';

      await open([
        section('HeroVideo', {
          items: [item({ videoPosterUrl: dataUri, imageUrl: dataUri, mobileImageUrl: dataUri })],
        }),
        section('ShopByDepartment', {
          items: [
            item({
              id: 2,
              departmentSlug: 'women',
              departmentName: 'Women',
              linkUrl: '/women',
              imageUrl: '/media/public/renditions/b/card.webp',
            }),
          ],
        }),
      ]);

      expect(ogImage()).toBe(`${location.origin}/media/public/renditions/b/card.webp`);
      expect(ogImage()).not.toContain('data:');
    });

    it('leaves a URL the catalogue already published absolute alone', async () => {
      await open([
        section('HeroVideo', { items: [item({ imageUrl: 'https://cdn.example.net/hero.webp' })] }),
      ]);

      expect(ogImage()).toBe('https://cdn.example.net/hero.webp');
    });

    it('shrinks the card rather than publishing an empty picture when there is none', async () => {
      await open([section('Benefits', { items: [item({ iconKey: 'truck', title: 'Delivery' })] })]);

      expect(ogImage()).toBeNull();
      expect(twitterCard()).toBe('summary');
    });
  });

  describe('order and identity (HOME-01..10)', () => {
    it('renders the sections in the order the API sent them, not in type order', () => {
      // Deliberately out of the seeded order: the client may reorder the page in admin, and the
      // page must not quietly sort it back.
      const sections = [
        section('Newsletter', { sortOrder: 1 }),
        BENEFITS,
        DEPARTMENT_TILES,
      ];

      return open(sections).then(() => {
        expect(rendered()).toEqual(['sd-newsletter-signup', 'sd-benefits-row', 'sd-department-tiles']);
      });
    });

    it('draws each of the ten blocks with the component for its type', async () => {
      const sections: readonly HomeSectionDto[] = [
        section('HeroVideo', { items: [item({ id: 11, imageUrl: '/media/hero.webp', title: 'New season' })] }),
        DEPARTMENT_TILES,
        section('NewArrivals', { products: [card(1)] }),
        section('TrendingItems', { products: [card(2)] }),
        section('CampaignBanner', { items: [item({ id: 51, imageUrl: '/media/c.webp', title: 'Campaign' })] }),
        section('ActivewearFeature', { items: [item({ id: 61, imageUrl: '/media/a.webp', title: 'Activewear' })] }),
        section('SaleSection', { products: [card(3)] }),
        section('SocialGallery', { items: [item({ id: 81, imageUrl: '/media/s.webp' })] }),
        BENEFITS,
        section('Newsletter'),
      ];

      await open(sections);

      expect(rendered()).toEqual([
        'sd-home-hero',
        'sd-department-tiles',
        'sd-product-row',
        'sd-product-row',
        'sd-feature-banner',
        'sd-feature-banner',
        'sd-product-row',
        'sd-social-gallery',
        'sd-benefits-row',
        'sd-newsletter-signup',
      ]);
    });

    /** Every section is the client's to remove, so the page's own heading cannot be a section's. */
    /**
     * `id` is document-unique, and the footer carries a newsletter field on every page. A bare
     * `newsletter-email` on this one left the footer's input with no accessible name at all and
     * sent its label's click up the page. axe 4.10 retired `duplicate-id`, so nothing caught it.
     */
    it('gives the newsletter field an id of its own, scoped to its section', async () => {
      await open([section('Newsletter', { id: 10 })]);

      const input = root().querySelector<HTMLInputElement>('[data-testid="newsletter-email"]')!;
      const label = root().querySelector<HTMLLabelElement>('label')!;

      expect(input.id).toBe('newsletter-email-10');
      expect(label.getAttribute('for')).toBe('newsletter-email-10');
      expect(input.id).not.toBe('newsletter-email');
    });

    it('has exactly one h1, whatever the client has switched on', async () => {
      await open([DEPARTMENT_TILES, section('NewArrivals', { products: [card(1)] })]);

      const h1s = queryAll('h1');
      expect(h1s).toHaveLength(1);
      expect(h1s[0].textContent?.trim()).toBe('Colombo Wardrobe');
    });
  });

  describe('a section with nothing in it draws nothing at all', () => {
    it('leaves out a banner whose artwork has not been uploaded', async () => {
      await open([
        section('CampaignBanner', { title: 'Campaign', items: [item({ id: 51, title: 'Campaign' })] }),
        BENEFITS,
      ]);

      expect(rendered()).toEqual(['sd-benefits-row']);
      // Not the heading, not an empty frame, not a promise to come back later.
      expect(text()).not.toContain('Campaign');
      expect(text()).not.toContain('coming soon');
    });

    it('leaves out a product row whose listing returned nothing', async () => {
      await open([section('SaleSection', { title: 'Sale' }), BENEFITS]);

      expect(rendered()).toEqual(['sd-benefits-row']);
      expect(text()).not.toContain('Sale');
    });

    it('leaves out a department block whose tiles have all been switched off', async () => {
      await open([section('ShopByDepartment', { title: 'Shop by department', items: [] }), BENEFITS]);

      expect(rendered()).toEqual(['sd-benefits-row']);
      expect(text()).not.toContain('Shop by department');
    });

    it('keeps the tiles that are usable and drops the ones that are not', async () => {
      await open([
        section('ShopByDepartment', {
          items: [
            item({ id: 21, departmentSlug: 'women', departmentName: 'Women', linkUrl: '/women' }),
            item({ id: 24, title: 'Nowhere' }),
            item({ id: 25, title: 'Unsafe', linkUrl: 'javascript:alert(1)' }),
            item({ id: 26, title: 'Absolute', linkUrl: 'https://colombowardrobecompany.com/men' }),
          ],
        }),
      ]);

      const tiles = queryAll<HTMLAnchorElement>('[data-testid="department-tile"]');
      expect(tiles).toHaveLength(1);
      expect(tiles[0].getAttribute('href')).toBe('/women');
    });

    /**
     * The section passed the has-content gate and then drew nothing: a heading over an empty list
     * between two real blocks. Both tile components render with `routerLink` and drop anything
     * that is not a path on this site, so counting an external link as content made the gate and
     * the rendering disagree.
     */
    it.each([
      ['ShopByDepartment' as const, 'department-tile'],
      ['TrendingItems' as const, 'trending-tile'],
    ])('leaves %s out entirely when every tile carries an absolute URL', async (type, testid) => {
      await open([
        section(type, {
          title: 'Would be a bare heading',
          items: [
            item({ id: 31, title: 'Women', linkUrl: 'https://colombowardrobecompany.com/women' }),
            item({ id: 32, title: 'Men', linkUrl: 'https://colombowardrobecompany.com/men' }),
          ],
        }),
        BENEFITS,
      ]);

      expect(rendered()).toEqual(['sd-benefits-row']);
      expect(queryAll(`[data-testid="${testid}"]`)).toHaveLength(0);
      expect(text()).not.toContain('Would be a bare heading');
    });
  });

  describe('product rows (HOME-03, HOME-04, HOME-07)', () => {
    it('renders the listing cards and a link to the rest of that listing', async () => {
      await open([
        section('NewArrivals', {
          title: 'New arrivals',
          linkUrl: '/new-arrivals',
          linkLabel: 'View all new arrivals',
          products: [card(1), card(2), card(3)],
        }),
      ]);

      expect(queryAll('sd-product-card')).toHaveLength(3);

      const link = queryAll<HTMLAnchorElement>('[data-testid="section-link"]');
      expect(link).toHaveLength(1);
      expect(link[0].getAttribute('href')).toBe('/new-arrivals');
      expect(link[0].textContent?.trim()).toBe('View all new arrivals');
    });

    it('draws no "view all" when the client gave the section no label for one', async () => {
      await open([
        section('TrendingItems', { title: 'Trending now', linkUrl: '/?sort=best-selling', products: [card(1)] }),
      ]);

      expect(queryAll('[data-testid="section-link"]')).toHaveLength(0);
      expect(queryAll('sd-product-card')).toHaveLength(1);
    });

    /**
     * HOME-04's own wording: "each card opens the matching filtered listing". That is the manual
     * mode of the trending section, where the client has arranged tiles by hand and each one
     * carries the listing URL it opens.
     */
    it('a trending card opens the filtered listing its tile names', async () => {
      await open([
        section('TrendingItems', {
          title: 'Trending now',
          items: [
            item({ id: 41, title: 'Wide leg denim', linkUrl: '/women/jeans?fit=wide-leg', sortOrder: 1 }),
            item({ id: 42, title: 'Linen shirts', linkUrl: '/men/shirts?material=linen', sortOrder: 2 }),
            item({ id: 43, title: 'Half price', linkUrl: '/sale?minDiscountPercent=50', sortOrder: 3 }),
          ],
        }),
      ]);

      const tiles = queryAll<HTMLAnchorElement>('[data-testid="trending-tile"]');

      expect(tiles.map((tile) => tile.getAttribute('href'))).toEqual([
        '/women/jeans?fit=wide-leg',
        '/men/shirts?material=linen',
        '/sale?minDiscountPercent=50',
      ]);
      expect(tiles.map((tile) => tile.textContent?.trim())).toEqual([
        'Wide leg denim',
        'Linen shirts',
        'Half price',
      ]);
    });

    it('shows the products when there are products, not the tiles behind them', async () => {
      await open([
        section('TrendingItems', {
          products: [card(1)],
          items: [item({ id: 41, title: 'Wide leg denim', linkUrl: '/women/jeans?fit=wide-leg' })],
        }),
      ]);

      expect(queryAll('sd-product-card')).toHaveLength(1);
      expect(queryAll('[data-testid="trending-tile"]')).toHaveLength(0);
    });
  });

  describe('the hero (HOME-01)', () => {
    /**
     * `ReducedMotion` answers `true` outside a browser that says otherwise, and jsdom has no
     * `matchMedia`, so this is the reduced-motion case: the still, and no video element at all.
     * The browser suite is where the other branch is measured.
     */
    it('renders the still and no video under reduced motion', async () => {
      await open([
        section('HeroVideo', {
          overlay: 'dark',
          items: [
            item({
              id: 11,
              title: 'New season',
              subtitle: 'Linen for the weather here',
              buttonLabel: 'Shop new arrivals',
              linkUrl: '/new-arrivals',
              imageUrl: '/media/hero-wide.webp',
              mobileImageUrl: '/media/hero-tall.webp',
              mobileImageAltText: 'A linen shirt on a balcony',
              videoUrl: '/media/hero.mp4',
              videoPosterUrl: '/media/hero-poster.webp',
            }),
          ],
        }),
      ]);

      expect(queryAll('[data-testid="hero-video"]')).toHaveLength(0);
      expect(queryAll('[data-testid="hero-toggle"]')).toHaveLength(0);

      const still = queryAll<HTMLImageElement>('[data-testid="hero-image"]');
      expect(still).toHaveLength(1);
      expect(still[0].getAttribute('src')).toBe('/media/hero-tall.webp');
      expect(still[0].getAttribute('alt')).toBe('A linen shirt on a balcony');

      expect(text()).toContain('New season');
      const cta = queryAll<HTMLAnchorElement>('.hero__cta');
      expect(cta[0].getAttribute('href')).toBe('/new-arrivals');
    });

    it('serves the wide still from 768 px when the client uploaded two crops', async () => {
      await open([
        section('HeroVideo', {
          items: [
            item({ id: 11, imageUrl: '/media/wide.webp', mobileImageUrl: '/media/tall.webp' }),
          ],
        }),
      ]);

      const source = queryAll<HTMLSourceElement>('picture source');
      expect(source).toHaveLength(1);
      expect(source[0].getAttribute('media')).toBe('(min-width: 768px)');
      expect(source[0].getAttribute('srcset')).toBe('/media/wide.webp');
    });

    it('uses the one picture it has when only one was uploaded', async () => {
      await open([section('HeroVideo', { items: [item({ id: 11, imageUrl: '/media/only.webp' })] })]);

      expect(queryAll('picture source')).toHaveLength(0);
      expect(queryAll<HTMLImageElement>('[data-testid="hero-image"]')[0].getAttribute('src')).toBe(
        '/media/only.webp',
      );
    });
  });

  describe('the social gallery (HOME-08)', () => {
    it('opens an external profile in a new tab, and says so', async () => {
      await open([
        section('SocialGallery', {
          items: [
            item({
              id: 81,
              imageUrl: '/media/social-1.webp',
              linkUrl: 'https://instagram.com/colombowardrobe',
              title: 'Colombo Wardrobe on Instagram',
            }),
          ],
        }),
      ]);

      const link = queryAll<HTMLAnchorElement>('[data-testid="social-link"]')[0];

      expect(link.getAttribute('href')).toBe('https://instagram.com/colombowardrobe');
      expect(link.getAttribute('target')).toBe('_blank');
      expect(link.getAttribute('rel')).toBe('noopener noreferrer');
      expect(link.textContent).toContain('opens in a new tab');
    });

    it('shows a photograph with no destination as a picture rather than a dead link', async () => {
      await open([section('SocialGallery', { items: [item({ id: 82, imageUrl: '/media/social-2.webp' })] })]);

      expect(queryAll('[data-testid="social-link"]')).toHaveLength(0);
      expect(queryAll('.gallery__cell img')).toHaveLength(1);
    });
  });

  describe('the benefits row (HOME-09)', () => {
    it('renders the copy the client wrote, with an icon for each key', async () => {
      await open([BENEFITS]);

      const benefits = queryAll('[data-testid="benefit"]');
      expect(benefits).toHaveLength(3);
      expect(benefits.map((benefit) => benefit.querySelector('h3')?.textContent?.trim())).toEqual([
        'Island-wide delivery',
        'Returns and exchanges',
        'Made to last',
      ]);
      expect(queryAll('sd-icon')).toHaveLength(3);
    });

    it('draws a mark for an icon key nobody has mapped, rather than a hole in the row', async () => {
      await open([
        section('Benefits', {
          items: [item({ id: 94, iconKey: 'unmapped-key', title: 'Something new' })],
        }),
      ]);

      expect(queryAll('sd-icon')).toHaveLength(1);
      expect(text()).toContain('Something new');
    });
  });

  /**
   * The homepage's content service is not the catalogue, and the catalogue is what a shopper came
   * for. A failure must never be a blank page or a status code on screen.
   */
  describe('when the content service cannot be reached', () => {
    it('still renders the shell heading and a way into the shop', async () => {
      await open(null, { status: 500, statusText: 'Server Error' });

      expect(root().querySelector('h1')?.textContent?.trim()).toBe('Colombo Wardrobe');
      expect(text()).toContain('We could not load the rest of the home page');
      expect(text()).not.toContain('500');
      expect(text()).not.toContain('Server Error');

      const links = queryAll<HTMLAnchorElement>('.fallback__links a').map((link) =>
        link.getAttribute('href'),
      );
      expect(links).toEqual(['/women', '/men', '/kids', '/new-arrivals', '/sale']);
    });

    it('offers a way to ask again, and asks again when it is used', async () => {
      await open(null, { status: 0, statusText: 'Unknown Error' });

      const retry = root().querySelector<HTMLButtonElement>('.fallback button');
      expect(retry?.textContent?.trim()).toBe('Try again');

      retry?.click();
      harness.detectChanges();

      contentRequest().flush({ sections: [BENEFITS] });
      await TestBed.inject(ApplicationRef).whenStable();
      harness.detectChanges();

      expect(rendered()).toEqual(['sd-benefits-row']);
      expect(text()).not.toContain('We could not load');
    });

    /** A successful response carrying something else entirely. `catchError` never sees it. */
    it('treats a payload it cannot read as a failure rather than as an empty page', async () => {
      http = TestBed.inject(HttpTestingController);
      harness = await RouterTestingHarness.create('/');
      flushDepartments();
      contentRequest().flush({ oops: true });
      await TestBed.inject(ApplicationRef).whenStable();
      harness.detectChanges();

      expect(text()).toContain('We could not load the rest of the home page');
    });

    /**
     * An empty page is a state the client is allowed to choose, so the page offers the way in
     * without apologising for something nobody broke.
     */
    it('does not apologise when the client has simply switched every section off', async () => {
      await open([]);

      expect(text()).toContain('Shop Colombo Wardrobe');
      expect(text()).not.toContain('We could not load');
      expect(queryAll('.fallback__links a')).toHaveLength(5);
      expect(root().querySelector('.fallback button')).toBeNull();
    });
  });

  describe('the newsletter signup (HOME-10)', () => {
    async function openNewsletter(): Promise<void> {
      await open([section('Newsletter', { title: 'Newsletter', subtitle: 'New arrivals and sale news.' })]);
    }

    function field(): HTMLInputElement {
      return root().querySelector<HTMLInputElement>('[data-testid="newsletter-email"]')!;
    }

    function submit(): void {
      root().querySelector<HTMLFormElement>('.signup__form')!.dispatchEvent(
        new Event('submit', { cancelable: true, bubbles: true }),
      );
      harness.detectChanges();
    }

    function type(value: string): void {
      const input = field();
      input.value = value;
      input.dispatchEvent(new Event('input'));
      harness.detectChanges();
    }

    it('posts the address and shows the sentence the server sent back', async () => {
      await openNewsletter();
      type('shopper@example.com');
      submit();

      const request = http.expectOne('/api/content/newsletter');
      expect(request.request.method).toBe('POST');
      // The source travels with it, so ADM-21 can tell a homepage signup from a footer one.
      expect(request.request.body).toEqual({ email: 'shopper@example.com', source: 'homepage' });

      request.flush({ message: 'Thank you. Your email address is on the list.' });
      harness.detectChanges();

      expect(text()).toContain('Thank you. Your email address is on the list.');
      expect(root().querySelector('[data-testid="newsletter-email"]')).toBeNull();
    });

    it('never claims success on its own: a failure says so and keeps the form', async () => {
      await openNewsletter();
      type('shopper@example.com');
      submit();

      http.expectOne('/api/content/newsletter').flush(
        { title: 'Nope' },
        { status: 500, statusText: 'Server Error' },
      );
      harness.detectChanges();

      expect(text()).not.toContain('on the list');
      expect(text()).toContain('We could not save your email address');
      expect(field()).not.toBeNull();
      expect(field().getAttribute('aria-invalid')).toBe('true');
    });

    it('shows the message the API wrote for an address it refuses', async () => {
      await openNewsletter();
      type('not-an-address');
      submit();

      http.expectOne('/api/content/newsletter').flush(
        {
          title: 'That email address could not be read.',
          errors: { email: ['Enter an email address, for example name@example.com.'] },
        },
        { status: 400, statusText: 'Bad Request' },
      );
      harness.detectChanges();

      expect(text()).toContain('Enter an email address, for example name@example.com.');
    });

    it('says to wait rather than to retype when the rate limiter answers', async () => {
      await openNewsletter();
      type('shopper@example.com');
      submit();

      http.expectOne('/api/content/newsletter').flush(null, { status: 429, statusText: 'Too Many Requests' });
      harness.detectChanges();

      expect(text()).toContain('Please wait a minute');
    });

    it('does not send an empty box to the server', async () => {
      await openNewsletter();
      submit();

      http.expectNone('/api/content/newsletter');
      expect(text()).toContain('Enter your email address to sign up.');
    });

    it('stops arguing as soon as the visitor starts fixing it', async () => {
      await openNewsletter();
      submit();
      expect(text()).toContain('Enter your email address');

      type('s');

      expect(text()).not.toContain('Enter your email address');
      expect(field().getAttribute('aria-invalid')).toBeNull();
    });
  });
});
