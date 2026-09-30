import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Icon } from '@spencer-denim/shared';

import { ColourSwatchDto, ProductBadge, ProductCardDto } from '../../core/catalog/catalog.model';
import { formatLkr } from '../../core/catalog/money';
import { LocalWishlist } from '../../core/wishlist/local-wishlist.service';

/** How many colour dots fit under a card at 320 px before the rest become "+3". */
const MAX_COLOUR_DOTS = 4;

/**
 * How wide a card really is, for the browser to pick a rendition with.
 *
 * `srcset` on its own buys nothing: without `sizes` a browser assumes the image fills the
 * viewport and fetches the widest candidate anyway, which is how a listing ended up weighing
 * 1,780 KB of full-size originals. These two strings are the arithmetic of the grids the card is
 * actually laid out in, taken from the rules that draw them rather than guessed:
 *
 * - `.product-grid` (storefront `styles.css`) is 2 columns, then 3 from 768 px, 4 from 1024 px
 *   and 5 from 1440 px, with column gaps of 12 / 16 / 24 px;
 * - `.container` (shared `base.css`) caps at 1440 px and has gutters of 16 / 32 / 48 px;
 * - a listing also gives up a 240 px filter sidebar plus a 48 px gap from 1024 px, and 264 px of
 *   sidebar from 1440 px (`listing-page.ts`).
 *
 * Above 1440 px the container stops growing, so both end in a fixed pixel width rather than a
 * `vw` expression that would keep over-estimating on a 1920 px screen.
 */
export const LISTING_GRID_CARD_SIZES =
  '(min-width: 1440px) 188px,' +
  ' (min-width: 1280px) calc((100vw - 456px) / 4),' +
  ' (min-width: 1024px) calc((100vw - 424px) / 4),' +
  ' (min-width: 768px) calc((100vw - 96px) / 3),' +
  ' calc((100vw - 44px) / 2)';

/** The same grid without a sidebar: the homepage rows and the product page's two rows. */
export const FULL_WIDTH_GRID_CARD_SIZES =
  '(min-width: 1440px) 250px,' +
  ' (min-width: 1280px) calc((100vw - 168px) / 4),' +
  ' (min-width: 1024px) calc((100vw - 136px) / 4),' +
  ' (min-width: 768px) calc((100vw - 96px) / 3),' +
  ' calc((100vw - 44px) / 2)';

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
          <!-- attr bindings, not property ones: a product photographed before the renditions
               existed has no candidate list, and an empty srcset is not the same as none. -->
          @if (mainImage(); as image) {
            <img
              [src]="image"
              [attr.srcset]="mainSrcSet()"
              [attr.sizes]="mainSrcSet() ? sizes() : null"
              alt=""
              loading="lazy"
              decoding="async"
              (error)="onImageError()"
            />
          } @else {
            <span class="card__placeholder"></span>
          }
          @if (hoverImage(); as image) {
            <img
              class="card__alt"
              [src]="image"
              [attr.srcset]="hoverSrcSet()"
              [attr.sizes]="hoverSrcSet() ? sizes() : null"
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
      font-size: var(--text-label);
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
      font-size: var(--text-label);
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
        font-size: var(--text-body);
      }
    }
    /* The shared rating row (storefront styles.css); a card sets it a size smaller. */
    .rating {
      font-size: var(--text-label);
    }
  `,
})
export class ProductCard {
  readonly product = input.required<ProductCardDto>();

  /**
   * How wide this card will be laid out, as a `sizes` list. It defaults to the listing grid,
   * which is where most cards are; a row that gives up no sidebar passes
   * {@link FULL_WIDTH_GRID_CARD_SIZES} instead, because the card cannot see the grid it is in.
   */
  readonly sizes = input<string>(LISTING_GRID_CARD_SIZES);

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

  /**
   * The candidate lists follow their own image: an `<img>` that has been dropped for failing to
   * load must not leave a srcset behind for the browser to try the same asset from again.
   */
  protected readonly mainSrcSet = computed(() =>
    this.mainImage() ? this.product().imageSrcSet : null,
  );

  protected readonly hoverSrcSet = computed(() =>
    this.hoverImage() ? this.product().hoverImageSrcSet : null,
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

  /**
   * The dots are decorative; this sentence is what a screen reader reads instead. The family is
   * named alongside the colour when they differ, so a card that a Red filter returned reads as
   * "Deep Maroon (Red)" rather than looking like the filter was wrong.
   */
  protected readonly colourSummary = computed(() => {
    const names = this.product().colours.map((colour: ColourSwatchDto) =>
      colour.familyName && colour.familyName !== colour.name
        ? `${colour.name} (${colour.familyName})`
        : colour.name,
    );

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
