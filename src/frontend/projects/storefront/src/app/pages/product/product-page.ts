import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  RESPONSE_INIT,
  computed,
  effect,
  inject,
  signal,
  untracked,
} from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { Meta, Title } from '@angular/platform-browser';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Icon } from '@spencer-denim/shared';

import { ProductDetails } from './product-details';
import { ProductGallery } from './product-gallery';
import { SizeGuideModal } from './size-guide-modal';
import { NotFoundPage } from '../not-found/not-found-page';
import { ProductCard } from '../../ui/product-card/product-card';
import { ProductBadge, ProductDetailDto, StockStatus } from '../../core/catalog/catalog.model';
import { ProductDetailStore } from '../../core/catalog/product-detail-store';
import { productErrorMessage } from '../../core/catalog/catalog-api';
import { formatLkr } from '../../core/catalog/money';
import { RecentlyViewed, toRecentCard } from '../../core/catalog/recently-viewed.service';
import {
  ColourOption,
  SizeOption,
  colourOptions,
  galleryFor,
  hasPriceRange,
  priceFor,
  readRequestedSelection,
  resolveSelection,
  sizeOptions,
  stockFor,
  toVariantParams,
  variantFor,
  videosFor,
} from '../../core/catalog/product-variants';
import { CanonicalLink } from '../../core/platform/canonical-link';
import { LocalWishlist } from '../../core/wishlist/local-wishlist.service';

/** How long a crawler is asked to wait before retrying a product the store could not serve. */
const RETRY_AFTER_SECONDS = '120';

/** What the API answers for `StockStatus`, in the shopper's words. */
const STOCK_LABEL: Readonly<Record<StockStatus, string>> = {
  InStock: 'In stock',
  LowStock: 'Low stock',
  OutOfStock: 'Out of stock',
};

/** Badge text, as on the card (CARD-01). */
const BADGE_LABEL: Readonly<Record<ProductBadge, string>> = {
  New: 'New',
  Sale: 'Sale',
  OnlineExclusive: 'Online exclusive',
  BestSeller: 'Best seller',
  LowStock: 'Low stock',
};

/** How many recently viewed products the row shows, once this one is taken out. */
const RECENT_SHOWN = 6;

/**
 * The product detail page (PDP-01..07).
 *
 * The URL is the state, as on the listing: the slug names the product and the query string
 * carries the colourway and size, because both change what the page *shows* — the photographs
 * and the price — and a link a shopper sends has to arrive showing the same thing. The reasoning
 * is set out in `core/catalog/product-variants.ts`. The canonical address carries neither, so the
 * colourways stay one page in an index rather than one page each.
 *
 * The request is issued from the constructor, before the first change detection, so it is already
 * in flight while the server renders and the HTML a crawler receives carries the name, the price
 * and the description rather than a spinner.
 *
 * **Three failures, three answers.** A slug the catalogue does not know (404) and a slug the API
 * will not even accept (400) are the same thing to a shopper — this address is not a product — so
 * both render the site's own not-found page, which answers a real HTTP 404. A store that answers
 * badly (500, a proxy's error page) or not at all is a different thing: the product probably
 * exists, so the page says so, offers "Try again", and the server answers **503 with
 * `Retry-After`**, which a crawler retries instead of dropping the URL.
 *
 * **Deliberately not here.** Add to Bag and Buy Now need the bag API (W2-09) and the real
 * wishlist needs W2-08; a button that looked like it worked and did nothing would be worse than
 * no button, so the page has neither. The heart is the same local, per-browser wishlist the
 * product card already uses. Reviews are PDP-08 (W2-06) and Quick View is QV-01 (W2-04).
 */
@Component({
  selector: 'sd-product-page',
  imports: [
    Icon,
    NotFoundPage,
    ProductCard,
    ProductDetails,
    ProductGallery,
    RouterLink,
    SizeGuideModal,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [ProductDetailStore],
  template: `
    @if (store.isMissing()) {
      <!-- The same page an unknown category gets, and the same real 404 with it. -->
      <sd-not-found-page />
    } @else if (store.isUnavailable()) {
      <div class="container pdp__state">
        <h1 class="pdp__state-title">We could not show this product</h1>
        <p>{{ errorMessage() }}</p>
        <div class="pdp__state-actions">
          <button class="btn btn--primary" type="button" (click)="retry()">Try again</button>
          <a class="btn btn--secondary" routerLink="/">Back to the home page</a>
        </div>
      </div>
    } @else if (product(); as item) {
      <div class="container pdp">
        <nav class="breadcrumb" aria-label="Breadcrumb">
          <ol>
            @for (crumb of breadcrumb(); track crumb.path; let last = $last) {
              <li>
                @if (last) {
                  <span aria-current="page">{{ crumb.label }}</span>
                } @else {
                  <a [routerLink]="crumb.path">{{ crumb.label }}</a>
                }
              </li>
            }
          </ol>
        </nav>

        <div class="pdp__layout">
          <sd-product-gallery class="pdp__gallery" [media]="media()" [productName]="item.name" />

          <div class="pdp__info">
            @if (badge(); as label) {
              <p class="badge" [class.badge--sale]="item.badge === 'Sale'">{{ label }}</p>
            }

            <h1 class="pdp__name">{{ item.name }}</h1>

            <p class="pdp__code">
              <span class="sr-only">Product code</span>{{ item.productCode }}
            </p>

            @if (ratingAverage(); as average) {
              <p class="rating">
                <span class="sr-only">
                  Rated {{ average }} out of 5 from {{ item.rating.count }} reviews
                </span>
                <sd-icon name="star" size="xs" [filled]="true" />
                <span aria-hidden="true">{{ average }} <span class="muted">({{ item.rating.count }})</span></span>
              </p>
            }

            <p class="price pdp__price" [class.price--sale]="price().hasDiscount">
              @if (showFrom()) {
                <span class="pdp__from">From</span>
              }
              @if (price().hasDiscount) {
                <span class="sr-only">Sale price</span>
              }
              <span class="price__now">{{ currentPrice() }}</span>
              @if (price().hasDiscount) {
                <s class="price__was"><span class="sr-only">Original price</span>{{ originalPrice() }}</s>
                <span class="price__off">-{{ discountPercent() }}%</span>
              }
            </p>

            <p class="pdp__stock" [class.pdp__stock--out]="stock().status === 'OutOfStock'">
              {{ stockLabel() }}
            </p>

            @if (colours().length > 0) {
              <div class="pdp__group" role="group" aria-labelledby="pdp-colour-label">
                <p class="pdp__legend" id="pdp-colour-label">
                  Colour: <span class="pdp__chosen">{{ selectedColourName() }}</span>
                </p>
                <div class="pdp__swatches">
                  @for (option of colours(); track option.colour.id) {
                    <button
                      class="swatch"
                      type="button"
                      [class.is-selected]="option.colour.id === selection().colourId"
                      [attr.aria-pressed]="option.colour.id === selection().colourId"
                      [attr.aria-label]="colourLabel(option)"
                      (click)="chooseColour(option.colour.id)"
                    >
                      <span
                        class="swatch__dot"
                        [class.swatch__dot--light]="isLight(option.colour.hexCode)"
                        [style.--swatch]="option.colour.hexCode ?? 'transparent'"
                      ></span>
                    </button>
                  }
                </div>
              </div>
            }

            @if (sizes().length > 0) {
              <div class="pdp__group" role="group" aria-labelledby="pdp-size-label">
                <p class="pdp__legend" id="pdp-size-label">
                  Size: <span class="pdp__chosen">{{ selectedSizeLabel() }}</span>
                </p>
                <div class="pdp__sizes">
                  @for (option of sizes(); track option.size.id) {
                    <button
                      class="size"
                      type="button"
                      [class.is-selected]="option.size.id === selection().sizeId"
                      [disabled]="!option.offered"
                      [attr.aria-pressed]="option.size.id === selection().sizeId"
                      [attr.aria-label]="sizeLabel(option)"
                      (click)="chooseSize(option.size.id)"
                    >
                      <span aria-hidden="true">{{ option.size.label }}</span>
                    </button>
                  }
                </div>
                @if (sizeGuide(); as guide) {
                  <button
                    class="pdp__guide"
                    type="button"
                    aria-haspopup="dialog"
                    aria-controls="size-guide"
                    [attr.aria-expanded]="sizeGuideOpen()"
                    (click)="openSizeGuide()"
                  >
                    {{ guide.name }} size guide
                  </button>
                }
              </div>
            }

            <div class="pdp__group">
              <label class="pdp__legend" for="pdp-quantity">Quantity</label>
              <div class="qty">
                <button
                  class="qty__step"
                  type="button"
                  aria-label="Decrease quantity"
                  [disabled]="quantity() <= 1"
                  (click)="stepQuantity(-1)"
                >
                  &minus;
                </button>
                <input
                  id="pdp-quantity"
                  class="qty__input"
                  type="number"
                  inputmode="numeric"
                  min="1"
                  [max]="maxQuantity()"
                  [value]="quantity()"
                  (input)="onQuantityInput($event)"
                />
                <button
                  class="qty__step"
                  type="button"
                  aria-label="Increase quantity"
                  [disabled]="quantity() >= maxQuantity()"
                  (click)="stepQuantity(1)"
                >
                  +
                </button>
              </div>
              <p class="pdp__qty-note" role="status">{{ quantityNote() }}</p>
            </div>

            <!-- The wishlist heart, exactly as the product card works it: the same per-browser
                 LocalWishlist, the same aria-pressed, the same labels. Add to Bag and Buy Now
                 wait for W2-09; nothing dead or disabled stands in for them. -->
            <button
              class="btn btn--secondary btn--block pdp__wish"
              type="button"
              [attr.aria-pressed]="saved()"
              (click)="toggleWishlist()"
            >
              <sd-icon name="heart" size="sm" [filled]="saved()" />
              {{ saved() ? 'Saved to wishlist' : 'Save to wishlist' }}
            </button>

            <sd-product-details [product]="item" />
          </div>
        </div>

        @if (item.relatedProducts.length > 0) {
          <section class="pdp__row" aria-labelledby="pdp-related">
            <h2 class="pdp__row-title" id="pdp-related">You may also like</h2>
            <ul class="product-grid">
              @for (related of item.relatedProducts; track related.id) {
                <li><sd-product-card [product]="related" /></li>
              }
            </ul>
          </section>
        }

        @if (recentlyViewed().length > 0) {
          <section class="pdp__row" aria-labelledby="pdp-recent">
            <h2 class="pdp__row-title" id="pdp-recent">Recently viewed</h2>
            <ul class="product-grid">
              @for (recent of recentlyViewed(); track recent.id) {
                <li><sd-product-card [product]="recent" /></li>
              }
            </ul>
          </section>
        }
      </div>

      @if (sizeGuide(); as guide) {
        <sd-size-guide-modal
          [guide]="guide"
          [open]="sizeGuideOpen()"
          (closed)="closeSizeGuide()"
        />
      }
    } @else {
      <p class="container pdp__state">Loading product…</p>
    }
  `,
  styles: `
    :host {
      display: block;
    }
    .pdp {
      padding-block: var(--space-3) var(--space-8);
    }
    .pdp__layout {
      display: grid;
      gap: var(--space-4);
      padding-top: var(--space-2);
    }
    .pdp__gallery,
    .pdp__info {
      min-width: 0;
    }
    .pdp__info {
      display: grid;
      align-content: start;
      gap: var(--space-1-5);
    }
    @media (min-width: 768px) {
      .pdp__layout {
        grid-template-columns: minmax(0, 1fr) minmax(0, 1fr);
        gap: var(--space-4);
      }
    }
    @media (min-width: 1280px) {
      .pdp__layout {
        grid-template-columns: minmax(0, 7fr) minmax(0, 5fr);
        gap: var(--space-6);
      }
    }

    .pdp__name {
      font-size: var(--text-h2);
      letter-spacing: -0.01em;
    }
    .pdp__code {
      font-size: var(--text-label);
      letter-spacing: var(--tracking-label);
      text-transform: uppercase;
      color: var(--color-text-muted);
    }
    .rating {
      display: flex;
      align-items: center;
      gap: var(--space-0-5);
      font-size: var(--text-small);
    }
    .pdp__price {
      font-size: var(--text-h3);
    }
    .pdp__from {
      font-size: var(--text-small);
      color: var(--color-text-muted);
    }
    .pdp__stock {
      font-size: var(--text-small);
      color: var(--color-success);
    }
    .pdp__stock--out {
      color: var(--color-text-muted);
    }
    .badge {
      justify-self: start;
      padding: 3px 6px;
      background: var(--color-primary);
      color: var(--color-on-primary);
      font-size: var(--text-label);
      font-weight: var(--weight-medium);
      letter-spacing: 0.1em;
      text-transform: uppercase;
      white-space: nowrap;
    }
    .badge--sale {
      background: var(--color-sale);
    }

    .pdp__group {
      display: grid;
      gap: var(--space-1);
      padding-top: var(--space-1);
      min-width: 0;
    }
    .pdp__legend {
      font-size: var(--text-small);
      color: var(--color-text-muted);
    }
    .pdp__chosen {
      color: var(--color-text);
    }
    .pdp__swatches,
    .pdp__sizes {
      display: flex;
      flex-wrap: wrap;
      gap: var(--space-1);
    }
    /* Round, like the card's colour dots (docs/04 style-tile decision 3), inside a 44 px target. */
    .swatch {
      display: grid;
      place-items: center;
      width: var(--tap);
      height: var(--tap);
      padding: 0;
      border: 1px solid transparent;
      border-radius: var(--radius-round);
      background: none;
      cursor: pointer;
    }
    .swatch.is-selected {
      border-color: var(--color-primary);
    }
    .swatch__dot {
      width: 26px;
      height: 26px;
      border: 1px solid var(--color-border);
      border-radius: var(--radius-round);
      background: var(--swatch);
    }
    .swatch__dot--light {
      border-color: var(--color-border-strong);
    }
    .size {
      min-width: var(--tap);
      min-height: var(--tap);
      padding-inline: var(--space-1-5);
      border: 1px solid var(--color-border-strong);
      background: var(--color-surface);
      font-size: var(--text-small);
      cursor: pointer;
    }
    .size.is-selected {
      border-color: var(--color-primary);
      background: var(--color-primary);
      color: var(--color-on-primary);
    }
    /* A size this colourway is not made in. Struck through rather than merely greyed, so it does
       not rely on colour alone (WCAG 1.4.1). */
    .size:disabled {
      border-color: var(--color-border);
      background: var(--color-bg-alt);
      color: var(--color-text-muted);
      text-decoration: line-through;
      cursor: not-allowed;
    }
    .pdp__guide {
      justify-self: start;
      min-height: var(--tap);
      padding: 0;
      border: 0;
      background: none;
      font-size: var(--text-small);
      text-decoration: underline;
      text-underline-offset: 4px;
      cursor: pointer;
    }

    .qty {
      display: flex;
      align-items: stretch;
      width: max-content;
      border: 1px solid var(--color-border-strong);
    }
    .qty__step {
      width: var(--tap);
      min-height: var(--tap);
      border: 0;
      background: var(--color-surface);
      font-size: var(--text-h4);
      cursor: pointer;
    }
    .qty__step:disabled {
      color: var(--color-text-muted);
      cursor: not-allowed;
    }
    .qty__input {
      width: 3.5rem;
      min-height: var(--tap);
      border: 0;
      border-inline: 1px solid var(--color-border-strong);
      background: var(--color-surface);
      text-align: center;
      font-variant-numeric: tabular-nums;
    }
    .pdp__qty-note {
      font-size: var(--text-label);
      color: var(--color-text-muted);
      min-height: 1.4em;
    }
    .pdp__wish {
      margin-top: var(--space-1);
    }

    .pdp__row {
      padding-top: var(--space-8);
    }
    .pdp__row-title {
      font-size: var(--text-h3);
      padding-bottom: var(--space-3);
    }

    .pdp__state {
      display: grid;
      justify-items: start;
      gap: var(--space-2);
      max-width: 62ch;
      padding-block: var(--space-8) var(--space-12);
      color: var(--color-text-muted);
    }
    .pdp__state-title {
      font-size: var(--text-h1);
      color: var(--color-text);
    }
    .pdp__state-actions {
      display: flex;
      flex-wrap: wrap;
      gap: var(--space-1-5);
    }
  `,
})
export class ProductPage {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly title = inject(Title);
  private readonly meta = inject(Meta);
  private readonly canonical = inject(CanonicalLink);
  private readonly wishlist = inject(LocalWishlist);
  private readonly recent = inject(RecentlyViewed);
  private readonly response = inject(RESPONSE_INIT, { optional: true });

  protected readonly store = inject(ProductDetailStore);

  private readonly routeParams = toSignal(this.route.params, {
    initialValue: this.route.snapshot.params,
  });

  private readonly queryParams = toSignal(this.route.queryParams, {
    initialValue: this.route.snapshot.queryParams,
  });

  /** The slug in the address bar, which may be anything at all. The API decides if it is real. */
  private readonly slug = computed(() => String(this.routeParams()['slug'] ?? ''));

  protected readonly product = this.store.product;

  protected readonly errorMessage = computed(() =>
    productErrorMessage(this.store.error() ?? 'failed'),
  );

  /** The colourway and size the URL asks for, resolved against what the product really has. */
  protected readonly selection = computed(() => {
    const item = this.product();
    if (!item) {
      return { colourId: null, sizeId: null };
    }

    return resolveSelection(item, readRequestedSelection(this.queryParams()));
  });

  protected readonly colours = computed<readonly ColourOption[]>(() => {
    const item = this.product();
    return item ? colourOptions(item) : [];
  });

  protected readonly sizes = computed<readonly SizeOption[]>(() => {
    const item = this.product();
    return item ? sizeOptions(item, this.selection().colourId) : [];
  });

  /** Stills for the chosen colourway, then any video. One list, in the order the stage shows. */
  protected readonly media = computed(() => {
    const item = this.product();
    if (!item) {
      return [];
    }

    const colourId = this.selection().colourId;
    return [...galleryFor(item, colourId), ...videosFor(item, colourId)];
  });

  protected readonly price = computed(() => {
    const item = this.product();
    return item
      ? priceFor(item, this.selection())
      : { current: 0, original: 0, discountPercent: 0, hasDiscount: false, currency: 'LKR' };
  });

  protected readonly currentPrice = computed(() => formatLkr(this.price().current));
  protected readonly originalPrice = computed(() => formatLkr(this.price().original));
  protected readonly discountPercent = computed(() => Math.round(this.price().discountPercent));

  /** "From Rs. X" only while no size is chosen and the sizes are not all one price. */
  protected readonly showFrom = computed(() => {
    const item = this.product();
    return item !== null && variantFor(item, this.selection()) === null && hasPriceRange(item);
  });

  protected readonly stock = computed(() => {
    const item = this.product();
    return item
      ? stockFor(item, this.selection())
      : { status: 'OutOfStock' as StockStatus, available: 0 };
  });

  protected readonly stockLabel = computed(() => {
    const { status, available } = this.stock();
    if (status === 'LowStock' && available > 0) {
      return available === 1 ? 'Only 1 left' : `Only ${available} left`;
    }

    return STOCK_LABEL[status];
  });

  protected readonly badge = computed(() => {
    const badge = this.product()?.badge;
    return badge ? (BADGE_LABEL[badge] ?? null) : null;
  });

  protected readonly ratingAverage = computed(() => {
    const rating = this.product()?.rating;
    return rating && rating.count > 0 && rating.average !== null ? rating.average : null;
  });

  protected readonly sizeGuide = computed(() => this.product()?.sizeGuide ?? null);

  protected readonly selectedColourName = computed(() => {
    const id = this.selection().colourId;
    return this.colours().find((option) => option.colour.id === id)?.colour.name ?? 'Not chosen';
  });

  protected readonly selectedSizeLabel = computed(() => {
    const id = this.selection().sizeId;
    return this.sizes().find((option) => option.size.id === id)?.size.label ?? 'Choose a size';
  });

  /** Breadcrumb from the API's own department and category trail, not from the URL. */
  protected readonly breadcrumb = computed(() => {
    const item = this.product();
    if (!item) {
      return [];
    }

    const crumbs = [
      { label: 'Home', path: '/' },
      { label: item.department.name, path: `/${item.department.slug}` },
    ];

    let path = `/${item.department.slug}`;
    for (const step of item.breadcrumb) {
      path = `${path}/${step.slug}`;
      crumbs.push({ label: step.name, path });
    }

    crumbs.push({ label: item.name, path: `/product/${item.slug}` });
    return crumbs;
  });

  /** PDP-07, minus the product being looked at. Empty on the server, by design. */
  /**
   * The catalogue's own slug once the product has loaded, and the URL's until then. The wishlist
   * and the recently-viewed list are keyed on what the API calls the product, so `/product/JEANS`
   * and `/product/jeans` cannot save it twice.
   */
  private readonly currentSlug = computed(() => this.product()?.slug ?? this.slug());

  protected readonly recentlyViewed = computed(() =>
    this.recent.except(this.currentSlug()).slice(0, RECENT_SHOWN),
  );

  protected readonly saved = computed(() => this.wishlist.has(this.currentSlug()));

  private readonly quantityWanted = signal(1);

  /** Never more than there is, and never fewer than one. */
  protected readonly maxQuantity = computed(() => Math.max(1, this.stock().available));

  protected readonly quantity = computed(() =>
    Math.min(Math.max(this.quantityWanted(), 1), this.maxQuantity()),
  );

  /**
   * What the quantity box says underneath. It is a live region, so a shopper who types 99 into a
   * box that tops out at 4 is told, rather than watching the number silently change (docs/04 §4).
   */
  protected readonly quantityNote = computed(() => {
    const { status, available } = this.stock();
    if (status === 'OutOfStock') {
      return '';
    }

    if (this.quantityWanted() > this.maxQuantity()) {
      return `Only ${available} available, so the quantity is ${this.quantity()}.`;
    }

    return `${this.quantity()} × ${formatLkr(this.price().current)} = ${formatLkr(
      this.quantity() * this.price().current,
    )}`;
  });

  private readonly guideOpen = signal(false);
  protected readonly sizeGuideOpen = this.guideOpen.asReadonly();

  constructor() {
    // Before the first change detection: the server render waits on this request, so the HTML a
    // crawler receives already has the product in it.
    this.store.load(this.slug());

    let requested = this.slug();
    effect(() => {
      const slug = this.slug();
      untracked(() => {
        if (slug !== requested) {
          requested = slug;
          this.quantityWanted.set(1);
          this.store.load(slug);
        }
      });
    });

    effect(() => {
      const item = this.product();
      untracked(() => {
        if (item) {
          this.title.setTitle(documentTitle(item));
          this.meta.updateTag({ name: 'description', content: metaDescription(item) });
          this.canonical.set(`/product/${item.slug}`);
          // A page that is fine must not inherit the noindex a 404 or a 503 left behind on a
          // client-side navigation.
          this.meta.removeTag('name="robots"');
          this.recent.remember(toRecentCard(item));
        }
      });
    });

    // A store that answered badly is a 503, not a 404: the product is probably there, and a
    // crawler retries a 503 and keeps the URL where it drops a 404.
    effect(() => {
      const unavailable = this.store.isUnavailable();
      untracked(() => {
        if (!unavailable) {
          return;
        }

        this.title.setTitle('Product unavailable | Spencer Denim');
        this.meta.updateTag({ name: 'robots', content: 'noindex' });

        if (this.response) {
          this.response.status = 503;
          // `headers` may already be a Headers, an array of pairs or a plain object; the
          // constructor normalises all three, so nothing is dropped by setting one more.
          const headers = new Headers(this.response.headers ?? {});
          headers.set('Retry-After', RETRY_AFTER_SECONDS);
          this.response.headers = headers;
        }
      });
    });

    // The next page owns its own canonical address, or has none.
    inject(DestroyRef).onDestroy(() => this.canonical.clear());
  }

  protected retry(): void {
    this.store.retry(this.slug());
  }

  protected chooseColour(colourId: number): void {
    this.apply({ colourId, sizeId: this.selection().sizeId });
  }

  /** Tapping the chosen size again clears it, which is how a shopper undoes a mis-tap. */
  protected chooseSize(sizeId: number): void {
    const current = this.selection();
    this.apply({
      colourId: current.colourId,
      sizeId: current.sizeId === sizeId ? null : sizeId,
    });
  }

  protected stepQuantity(step: number): void {
    this.quantityWanted.set(Math.min(Math.max(this.quantity() + step, 1), this.maxQuantity()));
  }

  protected onQuantityInput(event: Event): void {
    const raw = Number((event.target as HTMLInputElement).value);
    this.quantityWanted.set(Number.isFinite(raw) ? Math.floor(raw) : 1);
  }

  protected toggleWishlist(): void {
    this.wishlist.toggle(this.currentSlug());
  }

  protected openSizeGuide(): void {
    this.guideOpen.set(true);
  }

  protected closeSizeGuide(): void {
    this.guideOpen.set(false);
  }

  protected colourLabel(option: ColourOption): string {
    const { colour } = option;
    const name =
      colour.familyName && colour.familyName !== colour.name
        ? `${colour.name} (${colour.familyName})`
        : colour.name;

    return option.isInStock ? name : `${name}, out of stock`;
  }

  protected sizeLabel(option: SizeOption): string {
    if (!option.offered) {
      return `Size ${option.size.label}, not made in this colour`;
    }

    return option.isInStock
      ? `Size ${option.size.label}`
      : `Size ${option.size.label}, out of stock`;
  }

  /** A pale swatch needs an edge to be visible on white (docs/04 §4). Same rule as the card. */
  protected isLight(hex: string | null): boolean {
    if (!hex || !/^#[0-9a-f]{6}$/i.test(hex)) {
      return true;
    }

    const red = parseInt(hex.slice(1, 3), 16);
    const green = parseInt(hex.slice(3, 5), 16);
    const blue = parseInt(hex.slice(5, 7), 16);

    return (0.2126 * red + 0.7152 * green + 0.0722 * blue) / 255 > 0.8;
  }

  /**
   * Writes the selection into the URL. `replaceUrl` is deliberate for the colour and the size:
   * trying four sizes should not cost four presses of Back to leave the page.
   */
  private apply(selection: { colourId: number | null; sizeId: number | null }): void {
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: toVariantParams(selection),
      queryParamsHandling: 'merge',
      replaceUrl: true,
    });
  }
}

/**
 * The document title. `seo.title` is whatever an admin wrote, falling back to the product name in
 * the API, so the brand is appended only when it is not already there — the seeded departments
 * showed what happens otherwise ("Women's clothing | Spencer Denim | Spencer Denim").
 */
function documentTitle(product: ProductDetailDto): string {
  const title = product.seo.title.trim() || product.name;
  return title.includes('Spencer Denim') ? title : `${title} | Spencer Denim`;
}

function metaDescription(product: ProductDetailDto): string {
  const text =
    product.seo.description?.trim() ||
    product.shortDescription?.trim() ||
    product.description?.trim();

  return text ? text.slice(0, 300) : `${product.name} at Spencer Denim.`;
}
