import { isPlatformBrowser } from '@angular/common';
import {
  Injectable,
  PLATFORM_ID,
  Signal,
  afterNextRender,
  inject,
  signal,
} from '@angular/core';
import { SafeLocalStorage } from '@spencer-denim/shared';

import { ProductCardDto, ProductDetailDto } from './catalog.model';

/** Where this browser's recently viewed products live. */
export const RECENTLY_VIEWED_KEY = 'sd.recently-viewed';

/** How many are kept. Six are shown, and one of them is usually the page you are on. */
export const RECENTLY_VIEWED_LIMIT = 8;

/**
 * The products this browser has looked at (PDP-07).
 *
 * Per browser and nothing else: there is no account yet (ACC-01) and no server-side history, so
 * this is `localStorage` through {@link SafeLocalStorage}, which never throws.
 *
 * **It is never rendered on the server.** The server has no storage and would render an empty
 * row; a browser that then filled it during hydration would be adding nodes Angular never sent,
 * which hydration refuses. The list is therefore read in `afterNextRender` — the same shape
 * `Viewport` uses — so the row appears as an ordinary update one frame after the page is live.
 *
 * **Lesson L1.** Everything here is data a browser hands back, not data we wrote this run:
 * another tab, an older build of the site or a curious visitor may have left anything at all
 * under that key. Every entry is checked field by field and anything unusable is dropped, so a
 * junk value costs a shopper an empty row rather than a broken product page.
 *
 * The stored copy is a snapshot: a price that changed since the visit is shown as it was until
 * the product is visited again. TODO(W2-07): once the API can answer for several slugs at once
 * (the search endpoint is the natural home), keep only the slugs here and re-read the live cards.
 */
@Injectable({ providedIn: 'root' })
export class RecentlyViewed {
  private readonly storage = inject(SafeLocalStorage);
  private readonly isBrowser = isPlatformBrowser(inject(PLATFORM_ID));

  private readonly viewed = signal<readonly ProductCardDto[]>([]);

  /** Most recent first. Always empty on the server and until the first frame after hydration. */
  readonly products: Signal<readonly ProductCardDto[]> = this.viewed.asReadonly();

  constructor() {
    if (this.isBrowser) {
      afterNextRender(() => this.viewed.set(this.read()));
    }
  }

  /**
   * Records a product as just visited, moving it to the front if it was already there.
   *
   * Safe to call during a render: on the server it does nothing at all, so a server-rendered
   * page never pretends to know what this browser has seen.
   */
  remember(product: ProductCardDto): void {
    if (!this.isBrowser || typeof product.slug !== 'string' || product.slug === '') {
      return;
    }

    const next = [
      product,
      ...this.read().filter((entry) => entry.slug !== product.slug),
    ].slice(0, RECENTLY_VIEWED_LIMIT);

    this.viewed.set(next);
    this.storage.write(RECENTLY_VIEWED_KEY, JSON.stringify(next));
  }

  /** Everything except one slug, which is how the product page leaves itself out (PDP-07). */
  except(slug: string): readonly ProductCardDto[] {
    return this.products().filter((product) => product.slug !== slug);
  }

  /** Reads and vets the stored list. Anything that is not a usable card is dropped. */
  private read(): readonly ProductCardDto[] {
    const raw = this.storage.read(RECENTLY_VIEWED_KEY);
    if (!raw) {
      return [];
    }

    try {
      const parsed: unknown = JSON.parse(raw);
      if (!Array.isArray(parsed)) {
        return [];
      }

      return parsed.filter(isCard).slice(0, RECENTLY_VIEWED_LIMIT);
    } catch {
      return [];
    }
  }
}

/**
 * Whether a stored entry is enough of a product card for {@link ProductCard} to render.
 *
 * The card reads a name, a slug, the two category labels, a price and a rating without guarding
 * any of them, so each is checked here rather than in the template: one defensive read where
 * every reader passes, not a `?? ''` scattered through a component (the listing's round-2
 * lesson). The nullable fields are allowed to be missing, and are filled with null.
 */
function isCard(value: unknown): value is ProductCardDto {
  if (typeof value !== 'object' || value === null) {
    return false;
  }

  const card = value as Partial<ProductCardDto>;

  return (
    typeof card.id === 'number' &&
    text(card.slug) &&
    text(card.name) &&
    typeof card.departmentName === 'string' &&
    typeof card.categoryName === 'string' &&
    isPrice(card.price) &&
    isRating(card.rating) &&
    Array.isArray(card.colours)
  );
}

function isPrice(value: unknown): boolean {
  if (typeof value !== 'object' || value === null) {
    return false;
  }

  const price = value as Record<string, unknown>;
  return (
    Number.isFinite(price['current']) &&
    Number.isFinite(price['original']) &&
    Number.isFinite(price['discountPercent'])
  );
}

function isRating(value: unknown): boolean {
  if (typeof value !== 'object' || value === null) {
    return false;
  }

  const rating = value as Record<string, unknown>;
  return (
    Number.isFinite(rating['count']) &&
    (rating['average'] === null || Number.isFinite(rating['average']))
  );
}

function text(value: unknown): boolean {
  return typeof value === 'string' && value.trim().length > 0;
}

/**
 * The card the product page stores for itself (PDP-07).
 *
 * The detail payload carries everything a card renders except the hover image, which only the
 * listing projection computes; a stored card therefore simply has none, and the card component
 * already treats that as "this product has one photo".
 */
export function toRecentCard(product: ProductDetailDto): ProductCardDto {
  const category = product.breadcrumb.at(-1) ?? product.department;
  const image = product.images.find((media) => media.role !== 'Video') ?? null;

  return {
    id: product.id,
    productCode: product.productCode,
    name: product.name,
    slug: product.slug,
    departmentName: product.department.name,
    departmentSlug: product.department.slug,
    categoryName: category.name,
    categorySlug: category.slug,
    imageUrl: image?.url ?? null,
    hoverImageUrl: null,
    imageAltText: image?.altText ?? null,
    price: product.price,
    rating: product.rating,
    badge: product.badge,
    stockStatus: product.stockStatus,
    isInStock: product.isInStock,
    colours: product.colours,
  };
}
