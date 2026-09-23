import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { ProductCard } from './product-card';
import { ProductCardDto } from '../../core/catalog/catalog.model';
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
    hoverImageUrl: '/media/products/dress-2.jpg',
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
    expect(element.querySelector('.dots')?.textContent).toContain('Colours: Ecru, Black');
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

  it('offers no Add to Bag and no Quick View: neither is built yet', async () => {
    const { element } = await render();
    const labels = [...element.querySelectorAll('button')].map(
      (button) => `${button.textContent} ${button.getAttribute('aria-label') ?? ''}`,
    );

    expect(labels.some((label) => /add to bag/i.test(label))).toBe(false);
    expect(labels.some((label) => /quick view/i.test(label))).toBe(false);
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
});
