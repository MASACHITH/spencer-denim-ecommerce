import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import {
  FULL_WIDTH_GRID_CARD_SIZES,
  LISTING_GRID_CARD_SIZES,
  ProductCard,
} from './product-card';
import { ProductCardDto } from '../../core/catalog/catalog.model';
import { SafeLocalStorage } from '@spencer-denim/shared';

import { LocalWishlist } from '../../core/wishlist/local-wishlist.service';

function product(overrides: Partial<ProductCardDto> = {}): ProductCardDto {
  return {
    id: 1,
    productCode: 'SD-0001',
    name: 'Linen Wrap Midi Dress',
    slug: 'linen-wrap-midi-dress',
    departmentName: 'Women',
    departmentSlug: 'women',
    categoryName: 'Dresses',
    categorySlug: 'dresses',
    imageUrl: '/media/products/dress-1.jpg',
    imageSrcSet: null,
    hoverImageUrl: '/media/products/dress-2.jpg',
    hoverImageSrcSet: null,
    imageAltText: 'Linen wrap midi dress',
    price: { current: 7490, original: 7490, discountPercent: 0, hasDiscount: false, currency: 'LKR' },
    rating: { average: 4.6, count: 38 },
    badge: null,
    stockStatus: 'InStock',
    isInStock: true,
    colours: [],
    ...overrides,
  };
}

@Component({
  imports: [ProductCard],
  template: `<sd-product-card [product]="item()" />`,
})
class CardHost {
  readonly item = signal<ProductCardDto>(product());
}

// The wishlist is per-browser, and jsdom keeps one `localStorage` for the whole file: a test
// that saved a product would otherwise decide what the next one starts with.
beforeEach(() => localStorage.clear());

async function render(overrides: Partial<ProductCardDto> = {}) {
  TestBed.configureTestingModule({ imports: [CardHost], providers: [provideRouter([])] });

  const fixture = TestBed.createComponent(CardHost);
  fixture.componentInstance.item.set(product(overrides));
  fixture.detectChanges();
  await fixture.whenStable();

  return {
    fixture,
    element: fixture.nativeElement as HTMLElement,
    settle: async () => {
      fixture.detectChanges();
      await fixture.whenStable();
    },
  };
}

describe('ProductCard price (CARD-01)', () => {
  it('shows one price and no strike-through when nothing is discounted', async () => {
    const { element } = await render();

    expect(element.querySelector('.price__now')?.textContent?.trim()).toBe('Rs. 7,490');
    expect(element.querySelector('.price__was')).toBeNull();
    expect(element.querySelector('.price__off')).toBeNull();
    expect(element.querySelector('.price')?.classList.contains('price--sale')).toBe(false);
  });

  it('shows the price, the original price and the discount when there is one', async () => {
    const { element } = await render({
      price: {
        current: 5990,
        original: 7990,
        discountPercent: 25,
        hasDiscount: true,
        currency: 'LKR',
      },
    });

    expect(element.querySelector('.price__now')?.textContent?.trim()).toBe('Rs. 5,990');
    expect(element.querySelector('.price__was')?.textContent).toContain('Rs. 7,990');
    expect(element.querySelector('.price__off')?.textContent?.trim()).toBe('-25%');
    expect(element.querySelector('.price')?.classList.contains('price--sale')).toBe(true);
  });

  it('tells a screen reader which price is which', async () => {
    const { element } = await render({
      price: { current: 5990, original: 7990, discountPercent: 25, hasDiscount: true, currency: 'LKR' },
    });

    expect(element.querySelector('.price')?.textContent).toContain('Sale price');
    expect(element.querySelector('.price__was')?.textContent).toContain('Original price');
  });

  it('shows a whole discount percent, as the API floors it', async () => {
    const { element } = await render({
      price: { current: 5990, original: 7990, discountPercent: 25.03, hasDiscount: true, currency: 'LKR' },
    });

    expect(element.querySelector('.price__off')?.textContent?.trim()).toBe('-25%');
  });
});

describe('ProductCard content (CARD-01)', () => {
  it('names the department and the category above the product name', async () => {
    const { element } = await render();

    expect(element.querySelector('.card__cat')?.textContent?.trim()).toBe('Women · Dresses');
    expect(element.querySelector('.card__name')?.textContent?.trim()).toBe(
      'Linen Wrap Midi Dress',
    );
  });

  it('carries the main image and the hover image', async () => {
    const { element } = await render();
    const images = element.querySelectorAll('img');

    expect(images).toHaveLength(2);
    expect(images[1].classList.contains('card__alt')).toBe(true);
  });

  it('falls back to a plain panel rather than a broken image', async () => {
    const { element } = await render({ imageUrl: null, hoverImageUrl: null });

    expect(element.querySelector('img')).toBeNull();
    expect(element.querySelector('.card__placeholder')).not.toBeNull();
  });

  it('drops an image the browser could not fetch', async () => {
    const { element, settle } = await render();

    element.querySelector('img')?.dispatchEvent(new Event('error'));
    await settle();

    expect(element.querySelector('img')).toBeNull();
    expect(element.querySelector('.card__placeholder')).not.toBeNull();
  });

  it('shows the badge the API computed, in words', async () => {
    const { element } = await render({ badge: 'LowStock' });
    const badge = element.querySelector('.badge');

    expect(badge?.textContent?.trim()).toBe('Low stock');
    expect(badge?.classList.contains('badge--low')).toBe(true);
  });

  it('shows no badge when the product has none', async () => {
    const { element } = await render();

    expect(element.querySelector('.badge')).toBeNull();
  });

  it('shows the rating, and says it in words for a screen reader', async () => {
    const { element } = await render();

    expect(element.querySelector('.rating')?.textContent).toContain(
      'Rated 4.6 out of 5 from 38 reviews',
    );
  });

  it('shows no rating row until a product has an approved review', async () => {
    const { element } = await render({ rating: { average: null, count: 0 } });

    expect(element.querySelector('.rating')).toBeNull();
  });

  it('shows a colour dot per colour, and names them all for a screen reader', async () => {
    const { element } = await render({
      colours: [
        { id: 1, name: 'Ecru', familyName: 'Cream', familySlug: 'cream', hexCode: '#F3EADA', swatchImageUrl: null },
        { id: 2, name: 'Black', familyName: 'Black', familySlug: 'black', hexCode: '#101010', swatchImageUrl: null },
      ],
    });

    expect(element.querySelectorAll('.dot')).toHaveLength(2);
    // The family is named alongside the colour when they differ, so a card a Red filter returned
    // does not read as though the filter had gone wrong.
    expect(element.querySelector('.dots')?.textContent).toContain('Colours: Ecru (Cream), Black');
  });

  it('counts the colours it had no room for', async () => {
    const colours = Array.from({ length: 7 }, (_, index) => ({
      id: index,
      name: `Colour ${index}`,
      familyName: 'Blue',
      familySlug: 'blue',
      hexCode: '#2E6DB4',
      swatchImageUrl: null,
    }));

    const { element } = await render({ colours });

    expect(element.querySelectorAll('.dot')).toHaveLength(4);
    expect(element.querySelector('.dots__more')?.textContent?.trim()).toBe('+3');
  });

  it('gives a light swatch a stronger edge so it is visible on white', async () => {
    const { element } = await render({
      colours: [
        { id: 1, name: 'White', familyName: 'White', familySlug: 'white', hexCode: '#FFFFFF', swatchImageUrl: null },
        { id: 2, name: 'Black', familyName: 'Black', familySlug: 'black', hexCode: '#101010', swatchImageUrl: null },
      ],
    });

    const dots = element.querySelectorAll('.dot');
    expect(dots[0].classList.contains('dot--light')).toBe(true);
    expect(dots[1].classList.contains('dot--light')).toBe(false);
  });

  /**
   * Both triggers (QV-01, W2-04). They used to be absent and this test used to say so.
   *
   * The names carry the product, which is the point: a listing holds twenty-four cards, and
   * twenty-four buttons all called "Quick view" tell a screen-reader user nothing about which one
   * they are on. `aria-haspopup` is on both because both open the dialog - a card cannot know
   * whether a size has to be chosen, so Add to Bag cannot skip it (see the component docblock).
   */
  it('offers Quick View and Add to Bag, each named after this product', async () => {
    const { element } = await render();

    const view = element.querySelector<HTMLButtonElement>('[data-testid="card-quick-view"]')!;
    const add = element.querySelector<HTMLButtonElement>('[data-testid="card-add-to-bag"]')!;

    expect(view.textContent?.replace(/\s+/g, ' ').trim()).toBe(
      'Quick view Linen Wrap Midi Dress',
    );
    expect(add.textContent?.replace(/\s+/g, ' ').trim()).toBe(
      'Add to bag Linen Wrap Midi Dress',
    );

    for (const trigger of [view, add]) {
      expect(trigger.getAttribute('aria-haspopup')).toBe('dialog');
      expect(trigger.getAttribute('aria-expanded')).toBe('false');
      expect(trigger.type).toBe('button');
    }

    // On the photograph, with the heart, rather than under the card: docs/04 §4 decision 5.
    expect(element.querySelector('.card__media .card__actions')).not.toBeNull();
    expect(element.querySelector('.card__media .card__wish')).not.toBeNull();
  });

  it('opens no dialog until a trigger is pressed, so a grid of cards costs nothing', async () => {
    const { element } = await render();

    expect(element.querySelector('sd-quick-view')).toBeNull();
  });
});

describe('ProductCard wishlist', () => {
  it('starts unpressed and says what the heart does', async () => {
    const { element } = await render();
    const heart = element.querySelector('.card__wish') as HTMLButtonElement;

    expect(heart.getAttribute('aria-pressed')).toBe('false');
    expect(heart.getAttribute('aria-label')).toBe(
      'Save Linen Wrap Midi Dress to wishlist',
    );
  });

  it('toggles, and says so', async () => {
    const { element, settle } = await render();
    const heart = element.querySelector('.card__wish') as HTMLButtonElement;

    heart.click();
    await settle();

    expect(heart.getAttribute('aria-pressed')).toBe('true');
    expect(heart.getAttribute('aria-label')).toBe(
      'Remove Linen Wrap Midi Dress from wishlist',
    );
    expect(TestBed.inject(LocalWishlist).has('linen-wrap-midi-dress')).toBe(true);

    heart.click();
    await settle();

    expect(heart.getAttribute('aria-pressed')).toBe('false');
  });

  /**
   * **What a shopper sees when the heart cannot save.**
   *
   * `localStorage` is refused outright by a private window, a blocked-site-data setting and a full
   * quota, and `SafeLocalStorage.write` reports that by returning `false` rather than throwing. The
   * heart used to fill anyway and the badge used to increment, over nothing, with nothing to tell
   * them — a real defect on this card with no bag involved, and the state that then let
   * `CartStore.moveToWishlist` destroy a bag line.
   *
   * A card has no room for a sentence and a listing can hold twenty-four of them, so **the control says
   * it itself**: the heart stays unfilled, which is the truth, and the accessible name and the tooltip
   * explain why. The product page has room and shows a sentence instead.
   */
  it('leaves the heart unpressed and says why when storage refuses the write', async () => {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      imports: [CardHost],
      providers: [
        provideRouter([]),
        {
          provide: SafeLocalStorage,
          useValue: { read: () => null, write: () => false, remove: () => false },
        },
      ],
    });

    const fixture = TestBed.createComponent(CardHost);
    fixture.detectChanges();
    await fixture.whenStable();

    const element = fixture.nativeElement as HTMLElement;
    const heart = element.querySelector('.card__wish') as HTMLButtonElement;

    heart.click();
    fixture.detectChanges();
    await fixture.whenStable();

    expect(heart.getAttribute('aria-pressed')).toBe('false');
    expect(heart.getAttribute('aria-label')).toBe(
      'Could not save Linen Wrap Midi Dress to wishlist — your browser is blocking site data',
    );
    expect(heart.getAttribute('title')).toContain('blocking site data');

    // And nothing anywhere claims it is saved — the header badge reads this count.
    expect(TestBed.inject(LocalWishlist).has('linen-wrap-midi-dress')).toBe(false);
    expect(TestBed.inject(LocalWishlist).count()).toBe(0);
  });
});

/**
 * The renditions the media pipeline builds are only worth building if the card offers them, and
 * a `srcset` is only worth offering with a `sizes` beside it: without one a browser assumes the
 * picture fills the viewport and fetches the widest candidate anyway, which is how a listing
 * ended up weighing 1,780 KB of full-size originals.
 *
 * jsdom has no layout, so which file a browser would then choose is not a thing this suite can
 * see — `product-listing.e2e.ts` measures that. What is pinned here is what reaches the DOM.
 */
const CARD_SRCSET =
  '/media/public/renditions/aaaa/thumb.webp 160w,' +
  ' /media/public/renditions/aaaa/card.webp 600w,' +
  ' /media/public/renditions/aaaa/large.webp 1200w';

const HOVER_SRCSET = CARD_SRCSET.replaceAll('/aaaa/', '/bbbb/');

/** A row with no filter sidebar beside it, which is the one thing {@link CardHost} cannot show. */
@Component({
  imports: [ProductCard],
  template: `<sd-product-card [product]="item()" [sizes]="width" />`,
})
class WideCardHost {
  readonly item = signal<ProductCardDto>(
    product({ imageSrcSet: CARD_SRCSET, hoverImageSrcSet: HOVER_SRCSET }),
  );
  readonly width = FULL_WIDTH_GRID_CARD_SIZES;
}

describe('ProductCard renditions (CARD-01)', () => {
  it('offers the candidates the API sent, on both photographs', async () => {
    const { element } = await render({
      imageSrcSet: CARD_SRCSET,
      hoverImageSrcSet: HOVER_SRCSET,
    });

    const [main, hover] = [...element.querySelectorAll('img')];

    expect(main.getAttribute('srcset')).toBe(CARD_SRCSET);
    expect(hover.getAttribute('srcset')).toBe(HOVER_SRCSET);
  });

  it('describes the listing column it sits in, because a card cannot see its own grid', async () => {
    const { element } = await render({ imageSrcSet: CARD_SRCSET });

    expect(element.querySelector('img')?.getAttribute('sizes')).toBe(LISTING_GRID_CARD_SIZES);
  });

  it('takes the width a row gives it instead', async () => {
    TestBed.configureTestingModule({ imports: [WideCardHost], providers: [provideRouter([])] });

    const fixture = TestBed.createComponent(WideCardHost);
    fixture.detectChanges();
    await fixture.whenStable();

    const images = (fixture.nativeElement as HTMLElement).querySelectorAll('img');
    for (const image of images) {
      expect(image.getAttribute('sizes')).toBe(FULL_WIDTH_GRID_CARD_SIZES);
    }
  });

  /**
   * A product photographed before the pipeline existed has no candidate list, and `srcset=""` is
   * not the same as no `srcset`: an empty list is a list with nothing usable in it. The attribute
   * has to be absent rather than blank, which is why the template binds it with `[attr.]`.
   */
  it('emits no srcset at all when the product has no renditions', async () => {
    const { element } = await render({ imageSrcSet: null, hoverImageSrcSet: null });

    const images = element.querySelectorAll('img');
    expect(images).toHaveLength(2);

    for (const image of images) {
      expect(image.hasAttribute('srcset')).toBe(false);
      expect(image.hasAttribute('sizes')).toBe(false);
    }
  });

  /** The candidates follow their photograph: a dropped image must not leave a list behind. */
  it('drops the candidates with the image the browser could not fetch', async () => {
    const { element, settle } = await render({
      imageSrcSet: CARD_SRCSET,
      hoverImageSrcSet: HOVER_SRCSET,
    });

    element.querySelector('img')?.dispatchEvent(new Event('error'));
    await settle();

    expect(element.querySelector('img')).toBeNull();
  });
});

/**
 * The card's half of Quick View (QV-01): which dialog the press opens, which trigger owns it, and
 * that it goes away again. What the dialog then *does* is `ui/quick-view/quick-view.spec.ts`; the
 * whole flow through a real browser is `e2e/quick-view.e2e.ts`.
 */
describe('ProductCard Quick View (QV-01)', () => {
  async function open(testId: string) {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      imports: [CardHost],
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });

    const http = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(CardHost);
    fixture.detectChanges();
    await fixture.whenStable();

    const element = fixture.nativeElement as HTMLElement;
    const trigger = element.querySelector<HTMLButtonElement>(`[data-testid="${testId}"]`)!;

    trigger.click();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    return { fixture, element, trigger, http };
  }

  afterEach(() => document.body.classList.remove('sd-no-scroll'));

  it('opens the dialog for this product, and only the pressed trigger says so', async () => {
    const { element, trigger, http } = await open('card-quick-view');

    expect(element.querySelector('sd-quick-view')).not.toBeNull();
    expect(element.querySelector('[role="dialog"]')?.id).toBe('quick-view');
    expect(trigger.getAttribute('aria-expanded')).toBe('true');
    expect(
      element.querySelector('[data-testid="card-add-to-bag"]')?.getAttribute('aria-expanded'),
    ).toBe('false');

    // It fetches the variants the card does not carry, for this card's slug.
    http
      .expectOne('/api/catalog/products/linen-wrap-midi-dress')
      .flush({}, { status: 404, statusText: 'Not Found' });
  });

  it('opens the same dialog from Add to Bag, which is the trigger that then owns it', async () => {
    const { element, trigger, http } = await open('card-add-to-bag');

    expect(element.querySelector('[role="dialog"]')?.id).toBe('quick-view');
    expect(trigger.getAttribute('aria-expanded')).toBe('true');
    expect(
      element.querySelector('[data-testid="card-quick-view"]')?.getAttribute('aria-expanded'),
    ).toBe('false');

    http
      .expectOne('/api/catalog/products/linen-wrap-midi-dress')
      .flush({}, { status: 404, statusText: 'Not Found' });
  });

  it('takes the dialog away again when it closes, leaving the card as it was', async () => {
    const { fixture, element, trigger, http } = await open('card-quick-view');

    http
      .expectOne('/api/catalog/products/linen-wrap-midi-dress')
      .flush({}, { status: 404, statusText: 'Not Found' });
    fixture.detectChanges();

    element.querySelector<HTMLButtonElement>('.sd-modal__close')!.click();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(element.querySelector('sd-quick-view')).toBeNull();
    expect(trigger.getAttribute('aria-expanded')).toBe('false');
    // The dialog owns the scroll lock, so a card that opened one must not leave the page frozen.
    expect(document.body.classList.contains('sd-no-scroll')).toBe(false);
  });
});
