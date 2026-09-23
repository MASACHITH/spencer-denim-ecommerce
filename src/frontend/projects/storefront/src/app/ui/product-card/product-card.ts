import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Icon } from '@spencer-denim/shared';

import { ColourSwatchDto, ProductBadge, ProductCardDto } from '../../core/catalog/catalog.model';
import { formatLkr } from '../../core/catalog/money';
import { LocalWishlist } from '../../core/wishlist/local-wishlist.service';

/** How many colour dots fit under a card at 320 px before the rest become "+3". */
const MAX_COLOUR_DOTS = 4;

/** Badge text. The API sends the enum member name, and only these five exist. */
const BADGE_LABEL: Readonly<Record<ProductBadge, string>> = {
  New: 'New',
  Sale: 'Sale',
  OnlineExclusive: 'Online exclusive',
  BestSeller: 'Best seller',
  LowStock: 'Low stock',
};

/** One colour dot, with whether it needs a darker edge to be visible on white. */
interface ColourDot {
  readonly id: number;
  readonly name: string;
  readonly hex: string;
  readonly light: boolean;
}

/**
 * A product card in a listing grid (CARD-01).
 *
 * Deliberately not on this card yet:
 * - **Quick View** (QV-01) is its own task.
 * - **Add to Bag** needs the bag API (W2-09). A button that looked like it worked and did
 *   nothing would be worse than no button, so the card has none; the product page will take the
 *   first Add to Bag.
 *
 * The wishlist heart is a real control, but it saves to this browser only until the wishlist API
 * arrives (W2-08) — see {@link LocalWishlist}.
 */
@Component({
  selector: 'sd-product-card',
  imports: [Icon, RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="card">
      <div class="card__media">
        <!-- aria-hidden with tabindex="-1": the photo repeats the link on the product name, and
             two links to the same place is two stops for a keyboard and two reads for a screen
             reader. The whole card is still clickable through the stretched link below. -->
        <a class="card__img" [routerLink]="link()" tabindex="-1" aria-hidden="true">
          @if (mainImage(); as image) {
            <img [src]="image" alt="" loading="lazy" decoding="async" (error)="onImageError()" />
          } @else {
            <span class="card__placeholder"></span>
          }
          @if (hoverImage(); as image) {
            <img
              class="card__alt"
              [src]="image"
              alt=""
              loading="lazy"
              decoding="async"
              (error)="onHoverError()"
            />
          }
        </a>

        @if (badge(); as label) {
          <span class="badge" [class]="badgeClass()">{{ label }}</span>
        }

        <button
          class="card__wish"
          type="button"
          [attr.aria-pressed]="saved()"
          [attr.aria-label]="wishLabel()"
          (click)="toggleWishlist()"
        >
          <sd-icon name="heart" size="sm" [filled]="saved()" />
        </button>
      </div>

      <div class="card__body">
        @if (dots().length > 0) {
          <p class="dots">
            <span class="sr-only">{{ colourSummary() }}</span>
            @for (dot of dots(); track dot.id) {
              <span
                class="dot"
                [class.dot--light]="dot.light"
                [style.--dot]="dot.hex"
                aria-hidden="true"
              ></span>
            }
            @if (extraColours() > 0) {
              <span class="dots__more" aria-hidden="true">+{{ extraColours() }}</span>
            }
          </p>
        }

        <p class="card__cat">{{ product().departmentName }} · {{ product().categoryName }}</p>

        <h3 class="card__name">
          <a [routerLink]="link()">{{ product().name }}</a>
        </h3>

        @if (ratingAverage() !== null) {
          <p class="rating">
            <span class="sr-only">
              Rated {{ ratingAverage() }} out of 5 from {{ product().rating.count }} reviews
            </span>
            <sd-icon name="star" size="xs" [filled]="true" />
            <span aria-hidden="true">
              {{ ratingAverage() }} <span class="muted">({{ product().rating.count }})</span>
            </span>
          </p>
        }

        <p class="price" [class.price--sale]="product().price.hasDiscount">
          @if (product().price.hasDiscount) {
            <span class="sr-only">Sale price</span>
          }
          <span class="price__now">{{ current() }}</span>
          @if (product().price.hasDiscount) {
            <s class="price__was"><span class="sr-only">Original price</span>{{ original() }}</s>
            <span class="price__off">-{{ discount() }}%</span>
          }
        </p>
      </div>
    </div>
  `,
  styles: `
    :host {
      display: block;
      min-width: 0;
    }
    .card {
      position: relative;
      display: grid;
      gap: var(--space-1-5);
      align-content: start;
      min-width: 0;
    }
    .card__media {
      position: relative;
    }
    .card__img {
      display: block;
      position: relative;
    }
    .card__img img,
    .card__placeholder {
      display: block;
      width: 100%;
      aspect-ratio: 3 / 4;
      object-fit: cover;
      background: var(--color-bg-alt);
    }
    /* The second image sits over the first and fades in; a card with only one photo simply
       never gets the element, so nothing flickers to an empty box on hover. */
    .card__alt {
      position: absolute;
      inset: 0;
      height: 100%;
      opacity: 0;
      transition: opacity var(--duration) var(--ease);
    }
    @media (hover: hover) {
      .card:hover .card__alt {
        opacity: 1;
      }
    }
    .card:focus-within .card__alt {
      opacity: 1;
    }

    .badge {
      position: absolute;
      top: var(--space-1);
      left: var(--space-1);
      /* Keeps clear of the wishlist button whatever the label says. */
      max-width: calc(100% - var(--tap) - var(--space-2));
      padding: 3px 6px;
      font-size: 0.6875rem;
      font-weight: var(--weight-medium);
      letter-spacing: 0.1em;
      text-transform: uppercase;
      line-height: 1.3;
      /* Style tile QA r2: "LOW STOCK" wrapped at 320 px. */
      white-space: nowrap;
      overflow: hidden;
      text-overflow: ellipsis;
    }
    .badge--dark {
      background: var(--color-primary);
      color: var(--color-on-primary);
    }
    .badge--sale {
      background: var(--color-sale);
      color: var(--color-on-primary);
    }
    .badge--low {
      background: var(--color-surface);
      color: var(--color-warning);
      border: 1px solid currentColor;
    }

    .card__wish {
      position: absolute;
      top: 0;
      right: 0;
      z-index: 1;
      display: grid;
      place-items: center;
      width: var(--tap);
      height: var(--tap);
      padding: 0;
      border: 0;
      background: transparent;
      color: var(--color-text);
      cursor: pointer;
    }
    .card__wish::before {
      content: '';
      position: absolute;
      inset: 6px;
      background: var(--color-surface);
      transition: background-color var(--duration) var(--ease);
    }
    .card__wish sd-icon {
      position: relative;
    }
    .card__wish:hover::before {
      background: var(--color-bg-alt);
    }
    /* Lesson L4: the ::before fill above would paint over an inset outline, so the ring is drawn
       on ::after, on top of it, with a white inner line so it reads on a dark photo too. */
    .card__wish:focus-visible {
      outline: none;
    }
    .card__wish:focus-visible::after {
      content: '';
      position: absolute;
      inset: 2px;
      border: var(--focus-width) solid var(--color-primary);
      box-shadow: inset 0 0 0 var(--focus-width) var(--color-on-primary);
      pointer-events: none;
    }

    .card__body {
      display: grid;
      gap: var(--space-0-5);
      min-width: 0;
    }
    .dots {
      display: flex;
      align-items: center;
      gap: 6px;
      min-height: 20px;
      margin-bottom: 2px;
    }
    .dot {
      width: 14px;
      height: 14px;
      border-radius: var(--radius-round);
      background: var(--dot);
      border: 1px solid var(--color-border);
    }
    .dot--light {
      border-color: var(--color-border-strong);
    }
    .dots__more {
      font-size: var(--text-label);
      color: var(--color-text-muted);
    }
    .card__cat {
      font-size: 0.6875rem;
      letter-spacing: 0.1em;
      text-transform: uppercase;
      color: var(--color-text-muted);
      line-height: 1.4;
    }
    .card__name {
      font-size: var(--text-small);
      font-weight: var(--weight-regular);
      line-height: 1.4;
    }
    .card__name a {
      text-decoration: none;
    }
    /* Stretched link: the whole card opens the product page. */
    .card__name a::after {
      content: '';
      position: absolute;
      inset: 0;
    }
    .card__name a:hover {
      text-decoration: underline;
      text-underline-offset: 3px;
    }
    @media (min-width: 768px) {
      .card__name {
        font-size: 0.9375rem;
      }
    }
    .rating {
      display: flex;
      align-items: center;
      gap: var(--space-0-5);
      font-size: var(--text-label);
    }
    .price {
      display: flex;
      flex-wrap: wrap;
      align-items: baseline;
      gap: 2px var(--space-1);
      font-size: 0.9375rem;
      line-height: 1.4;
    }
    .price__now {
      font-weight: var(--weight-medium);
      white-space: nowrap;
    }
    .price__was {
      color: var(--color-text-muted);
      font-size: 0.8125rem;
      white-space: nowrap;
    }
    .price__off {
      color: var(--color-sale);
      font-size: 0.8125rem;
      font-weight: var(--weight-medium);
    }
    .price--sale .price__now {
      color: var(--color-sale);
    }
  `,
})
export class ProductCard {
  readonly product = input.required<ProductCardDto>();

  private readonly wishlist = inject(LocalWishlist);

  /** An image the browser could not fetch is dropped, so a card never shows a broken picture. */
  private readonly mainFailed = signal(false);
  private readonly hoverFailed = signal(false);

  protected readonly saved = computed(() => this.wishlist.has(this.product().slug));

  /** TODO(W2-04): this is the product detail page's route (PDP-01..08). */
  protected readonly link = computed(() => ['/product', this.product().slug]);

  protected readonly mainImage = computed(() =>
    this.mainFailed() ? null : this.product().imageUrl,
  );

  protected readonly hoverImage = computed(() =>
    this.hoverFailed() || this.mainImage() === null ? null : this.product().hoverImageUrl,
  );

  protected readonly badge = computed(() => {
    const badge = this.product().badge;
    return badge ? (BADGE_LABEL[badge] ?? null) : null;
  });

  protected readonly badgeClass = computed(() => {
    switch (this.product().badge) {
      case 'Sale':
        return 'badge--sale';
      case 'LowStock':
        return 'badge--low';
      default:
        return 'badge--dark';
    }
  });

  /** A product with no approved review has no rating row at all, rather than an empty one. */
  protected readonly ratingAverage = computed(() => {
    const rating = this.product().rating;
    return rating.count > 0 && rating.average !== null ? rating.average : null;
  });

  protected readonly current = computed(() => formatLkr(this.product().price.current));
  protected readonly original = computed(() => formatLkr(this.product().price.original));
  protected readonly discount = computed(() => Math.round(this.product().price.discountPercent));

  protected readonly dots = computed<readonly ColourDot[]>(() =>
    this.product()
      .colours.slice(0, MAX_COLOUR_DOTS)
      .map((colour) => ({
        id: colour.id,
        name: colour.name,
        hex: colour.hexCode ?? 'transparent',
        light: isLight(colour.hexCode),
      })),
  );

  protected readonly extraColours = computed(() =>
    Math.max(0, this.product().colours.length - MAX_COLOUR_DOTS),
  );

  /** The dots are decorative; this sentence is what a screen reader reads instead. */
  protected readonly colourSummary = computed(() => {
    const names = this.product().colours.map((colour: ColourSwatchDto) => colour.name);
    return names.length === 1 ? `Colour: ${names[0]}` : `Colours: ${names.join(', ')}`;
  });

  protected readonly wishLabel = computed(() =>
    this.saved()
      ? `Remove ${this.product().name} from wishlist`
      : `Save ${this.product().name} to wishlist`,
  );

  protected toggleWishlist(): void {
    this.wishlist.toggle(this.product().slug);
  }

  protected onImageError(): void {
    this.mainFailed.set(true);
  }

  protected onHoverError(): void {
    this.hoverFailed.set(true);
  }
}

/**
 * Whether a swatch needs the stronger border to be visible on white and on ivory. Relative
 * luminance rather than a hard-coded list, because colour families are admin-managed.
 */
function isLight(hex: string | null): boolean {
  if (!hex || !/^#[0-9a-f]{6}$/i.test(hex)) {
    return true;
  }

  const red = parseInt(hex.slice(1, 3), 16);
  const green = parseInt(hex.slice(3, 5), 16);
  const blue = parseInt(hex.slice(5, 7), 16);

  return (0.2126 * red + 0.7152 * green + 0.0722 * blue) / 255 > 0.8;
}
