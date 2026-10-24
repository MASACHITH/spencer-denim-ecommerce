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
import { BRAND, Icon, brandPageTitle } from '@spencer-denim/shared';

import { ProductDeliveryReturns } from './product-delivery-returns';
import { ProductDetails } from './product-details';
import { ProductGallery } from './product-gallery';
import { ProductReviews } from './product-reviews';
import { SizeGuideModal } from './size-guide-modal';
import { NotFoundPage } from '../not-found/not-found-page';
import { FULL_WIDTH_GRID_CARD_SIZES, ProductCard } from '../../ui/product-card/product-card';
import {
  ProductBadge,
  ProductDetailDto,
  StockStatus,
  hasSizeGuideContent,
} from '../../core/catalog/catalog.model';
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
import { PageMeta } from '../../core/platform/page-metadata';
import { readableInk } from '../../core/platform/contrast';
import { RobotsMeta } from '../../core/platform/robots-meta';
import { serviceUnavailable } from '../../core/platform/service-unavailable';
import { CartStore } from '../../core/cart/cart.store';
import { WishlistStore } from '../../core/wishlist/wishlist.store';

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

/**
 * What a shopper is told when the wishlist cannot be written to.
 *
 * It says what happened and what to do about it, and it does not call it an error: in a private window
 * this is the normal, permanent answer, not a fault to retry.
 */
const WISHLIST_SAVE_UNAVAILABLE =
  'We could not save that to your wishlist. Your browser is blocking site data — private browsing ' +
  'usually does.';

/**
 * The same cause, the opposite operation. A refusal leaves the signal untouched, so the heart stays
 * filled and correctly still reads "Saved to wishlist" — which made one shared "could not save"
 * sentence contradict the control it sat beside.
 */
const WISHLIST_REMOVE_UNAVAILABLE =
  'We could not remove that from your wishlist. Your browser is blocking site data — private ' +
  'browsing usually does.';

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
 * **Add to Bag arrived with W2-09** (CART-01/02): it is disabled until a colour and size are settled,
 * as PDP-03 asks, and a successful add opens the mini bag. **Buy Now is still not here**: it is Add to
 * Bag followed by checkout, and checkout is W3-02 — a button that looked like it worked and did
 * nothing would be worse than no button, which is the same judgement that kept Add to Bag off this page
 * until the bag API existed. The heart writes to the same per-browser list the product card uses, which
 * W2-08's `/wishlist` page reads; a guest's wishlist lives in the browser by design (doc 08 §1.7). Reviews (PDP-08, #189) are `ProductReviews`, below the details; Quick View is QV-01 (W2-04).
 */
@Component({
  selector: 'sd-product-page',
  imports: [
    Icon,
    NotFoundPage,
    ProductCard,
    ProductDeliveryReturns,
    ProductDetails,
    ProductGallery,
    ProductReviews,
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
                      class="sd-swatch"
                      type="button"
                      [class.is-selected]="option.colour.id === selection().colourId"
                      [attr.aria-pressed]="option.colour.id === selection().colourId"
                      [attr.aria-label]="colourLabel(option)"
                      (click)="chooseColour(option.colour.id)"
                    >
                      <span
                        class="sd-swatch__dot"
                        [class.sd-swatch__dot--light]="isLight(option.colour.hexCode)"
                        [class.is-sold-out]="!option.isInStock"
                        [style.--swatch]="option.colour.hexCode ?? 'transparent'"
                        [style.--slash]="slashInk(option.colour.hexCode)"
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
                      class="sd-size"
                      type="button"
                      [class.is-selected]="option.size.id === selection().sizeId"
                      [class.is-sold-out]="option.offered && !option.isInStock"
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
                    class="link pdp__guide"
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

            <!-- PDP-03: unavailable until there is exactly one thing to add. aria-describedby points at
                 the note below, so a screen reader is told *why* it is unavailable rather than only that
                 it is — a dead control with no reason is the accessible version of a dead button.

                 **aria-disabled, not disabled (W2-04 round 2).** This carried the same defect Quick View
                 was failed for and the same one ADM-22 was fixed for: the disabled attribute on the
                 element that holds focus makes the browser blur it to the document body with no focusin
                 event, so pressing Add to Bag threw the shopper's place away. Here there is no dialog to
                 be ejected from, but a refused add still leaves a keyboard user at the top of the
                 document next to an error they can no longer get back to, and aria-describedby on an
                 unfocusable button was never reachable at all. addToBag() refuses the activation. -->
            <button
              class="btn btn--primary btn--block pdp__add"
              type="button"
              data-testid="add-to-bag"
              [attr.aria-disabled]="canAddToBag() && !adding() ? null : 'true'"
              [attr.aria-describedby]="addToBagReason() ? 'pdp-add-reason' : null"
              (click)="addToBag()"
            >
              <sd-icon name="bag" size="sm" />
              {{ adding() ? 'Adding…' : 'Add to bag' }}
            </button>

            <!-- A live region, always in the DOM rather than inside an @if, for the reason Quick
                 View's copy of this line gives (round 2, NIT 2): quantityNote() returns '' for an
                 OutOfStock selection, so picking a sold-out size silenced the status region above
                 and changed only this line, which was not live. An emptied region announces
                 nothing, and a region that appears already holding its text is not a change. -->
            <p class="pdp__qty-note" id="pdp-add-reason" role="status">{{ addToBagReason() }}</p>

            @if (bagError(); as message) {
              <p class="pdp__add-error" role="alert">{{ message }}</p>
            }

            <!-- The wishlist heart, exactly as the product card works it: the same per-browser
                 LocalWishlist, the same aria-pressed, the same labels. Buy Now waits for W3-02;
                 nothing dead or disabled stands in for it.

                 The heart can fail, and silently: storage is localStorage, which a private window, a
                 blocked-site-data setting or a full quota all refuse without throwing. LocalWishlist
                 reports that now instead of filling the heart over nothing, and this is where a shopper
                 is told. -->
            <button
              class="btn btn--secondary btn--block pdp__wish"
              type="button"
              [attr.aria-pressed]="saved()"
              (click)="toggleWishlist()"
            >
              <sd-icon name="heart" size="sm" [filled]="saved()" />
              {{ saved() ? 'Saved to wishlist' : 'Save to wishlist' }}
            </button>

            @if (wishError(); as message) {
              <p class="pdp__add-error pdp__wish-error" role="alert">{{ message }}</p>
            }

            <sd-product-details [product]="item" />

            <!-- PDP-05 (#33): delivery fees from the zone rates, and the owner's delivery and returns texts. -->
            <sd-product-delivery-returns />
          </div>
        </div>

        <sd-product-reviews class="pdp__reviews" [productId]="item.id" [slug]="item.slug" />

        @if (item.relatedProducts.length > 0) {
          <section class="pdp__row" aria-labelledby="pdp-related">
            <h2 class="pdp__row-title" id="pdp-related">You may also like</h2>
            <ul class="product-grid">
              @for (related of item.relatedProducts; track related.id) {
                <li><sd-product-card [product]="related" [sizes]="cardSizes" /></li>
              }
            </ul>
          </section>
        }

        @if (recentlyViewed().length > 0) {
          <section class="pdp__row" aria-labelledby="pdp-recent">
            <h2 class="pdp__row-title" id="pdp-recent">Recently viewed</h2>
            <ul class="product-grid">
              @for (recent of recentlyViewed(); track recent.id) {
                <li><sd-product-card [product]="recent" [sizes]="cardSizes" /></li>
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
    /* The trigger is a shared button.link; this is the only thing it needs from the page, and it
       is here rather than deleted because the markup stays (issue #32) and a grid child would
       otherwise stretch the full width of the column. */
    .pdp__guide {
      justify-self: start;
    }
    .pdp__swatches,
    .pdp__sizes {
      display: flex;
      flex-wrap: wrap;
      gap: var(--space-1);
    }
    /* .sd-swatch, .sd-size and .qty are in the storefront stylesheet, not here: Quick View (QV-01)
       draws the same picker from the same markup, and two copies of these rules is how a sold-out
       swatch ends up slashed on one screen and plain on the other. The two picker classes carry
       the sd- prefix because they are global and "size" and "swatch" are about as generic as a
       class name gets; .qty does not, and that is pre-existing rather than considered. */
    .pdp__qty-note {
      font-size: var(--text-label);
      color: var(--color-text-muted);
      min-height: 1.4em;
    }
    /* .btn already supplies the icon gap; the reason line reuses .pdp__qty-note, which is the same
       muted label. Both to keep this component's stylesheet inside the 4 kB budget. */
    .pdp__add,
    .pdp__wish {
      margin-top: var(--space-1);
    }
    .pdp__add-error {
      padding: var(--space-1);
      border: 1px solid var(--color-sale);
      color: var(--color-sale);
      font-size: var(--text-label);
    }

    .pdp__row {
      padding-top: var(--space-8);
    }
    .pdp__reviews {
      display: block;
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
  private readonly pageMeta = inject(PageMeta);
  private readonly robots = inject(RobotsMeta);
  // **`WishlistStore`, like the listing card** (W2-08 round 3). This page wrote the stored list
  // directly while the card went through the store, so "every heart in the storefront goes through
  // `WishlistStore`" was false and was reported to the owner as fact. One feature with two code paths
  // is the m3 finding again, one layer up.
  //
  // **It still does not need a sentence, and that is not the same as not announcing.** The heart here
  // is a persistent toggle: `aria-pressed` flips and the accessible name changes from "Save to
  // wishlist" to "Saved to wishlist", which a screen reader reads out on the press. A live-region
  // sentence on top of that would say the same thing twice. What the page gains from the store is the
  // single code path, the shared refusal wording, and a `lastChange` that is correct for whoever reads
  // it — not a second announcement. There are also no rows here to re-home focus to, and the button
  // survives its own press, so the removal reporting is simply unused on this page.
  private readonly wishlist = inject(WishlistStore);
  private readonly cart = inject(CartStore);
  private readonly recent = inject(RecentlyViewed);
  private readonly response = inject(RESPONSE_INIT, { optional: true });

  protected readonly store = inject(ProductDetailStore);

  /** The two rows below run the full width of the container, with no filter sidebar beside them. */
  protected readonly cardSizes = FULL_WIDTH_GRID_CARD_SIZES;

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

  /**
   * The size guide, **only when there is one worth opening** (PDP-04).
   *
   * Null for a product with no guide at all — six of the catalogue's forty-five — and also for a
   * guide an admin has created and left empty, because a button that opened a dialog to say
   * "nothing to show here" is a dead control wearing a coat. `hasSizeGuideContent` is the one
   * condition both the trigger and the dialog hang on, which is also why the measurements have to
   * arrive with the product payload: there is nothing to click that a fetch could hang off.
   */
  protected readonly sizeGuide = computed(() => {
    const guide = this.product()?.sizeGuide ?? null;
    return hasSizeGuideContent(guide) ? guide : null;
  });

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

  protected readonly saved = computed(() => this.wishlist.isSaved(this.currentSlug()));

  private readonly wishFailure = signal<string | null>(null);

  /** What to say when the heart could not save. Null the rest of the time, which is almost always. */
  protected readonly wishError = this.wishFailure.asReadonly();

  private readonly quantityWanted = signal(1);

  /** The exact colour × size the shopper has settled on, or null while a choice is outstanding. */
  private readonly chosenVariant = computed(() => {
    const item = this.product();
    return item ? variantFor(item, this.selection()) : null;
  });

  /**
   * How many may be asked for.
   *
   * Until a variant is settled this is **1**, not the whole colourway's stock. A shopper with no
   * size chosen was being offered up to 138 of a jean, which is the sum across every size and a
   * number no single thing can be bought in. Once a size is chosen it is that variant's stock.
   * A product with no sizes at all has nothing outstanding, so its own stock is the ceiling.
   */
  protected readonly maxQuantity = computed(() => {
    const variant = this.chosenVariant();
    if (variant) {
      return Math.max(1, variant.availableQuantity);
    }

    return this.sizes().length === 0 ? Math.max(1, this.stock().available) : 1;
  });

  protected readonly quantity = computed(() =>
    Math.min(Math.max(this.quantityWanted(), 1), this.maxQuantity()),
  );

  /**
   * What the quantity box says underneath. It is a live region, so a shopper who types 99 into a
   * box that tops out at 4 is told, rather than watching the number silently change (docs/04 §4).
   *
   * **No total until a variant is settled.** While the price line still reads "From Rs. 5,990",
   * this said "3 × Rs. 5,990 = Rs. 17,970" — a definite sum at an indefinite price, for an amount
   * of nothing in particular. A shopper who has not chosen a size is told to choose one instead.
   */
  protected readonly quantityNote = computed(() => {
    const variant = this.chosenVariant();
    const outstanding = !variant && this.sizes().length > 0;

    if (this.stock().status === 'OutOfStock') {
      return '';
    }

    if (this.quantityWanted() > this.maxQuantity()) {
      // The ceiling, not the colourway's total: with no size chosen those are different numbers,
      // and quoting the colourway's would explain the clamp with a figure that did not cause it.
      return outstanding
        ? 'Choose a size before increasing the quantity.'
        : `Only ${this.maxQuantity()} available, so the quantity is ${this.quantity()}.`;
    }

    if (outstanding) {
      return 'Choose a size to see the total.';
    }

    return `${this.quantity()} × ${formatLkr(this.price().current)} = ${formatLkr(
      this.quantity() * this.price().current,
    )}`;
  });

  /** True when exactly one variant is settled and there is stock for it (PDP-03). */
  protected readonly canAddToBag = computed(() => {
    const variant = this.chosenVariant();
    if (variant) {
      return variant.availableQuantity > 0;
    }

    // A product with no sizes has nothing outstanding — but it still needs a variant to add, and a
    // product with no variants at all is not something that can be put in a bag.
    return false;
  });

  /**
   * Why Add to Bag is disabled, in a sentence, or null when it is not.
   *
   * It doubles as the button's `aria-describedby`, so the reason is announced rather than left to be
   * inferred from a greyed-out control.
   */
  protected readonly addToBagReason = computed(() => {
    if (this.canAddToBag()) {
      return null;
    }

    // **The chosen size first, and the order is a fix (QV-01, W2-04).** `stock()` narrows to the
    // selection, so picking a sold-out size makes `stock().status` `OutOfStock` for a product with
    // plenty of the other sizes. Asked in the other order, this said "This product is out of
    // stock" - false, and it made the sentence below unreachable for the one case it was written
    // for. Found while Quick View was being built from this same logic.
    const variant = this.chosenVariant();
    if (variant && variant.availableQuantity <= 0) {
      return 'That size is out of stock. Choose another size.';
    }

    if (this.stock().status === 'OutOfStock') {
      return 'This product is out of stock.';
    }

    if (this.sizes().length > 0 && !variant) {
      return 'Choose a size to add this to your bag.';
    }

    return 'This product cannot be added to a bag at the moment.';
  });

  protected readonly adding = this.cart.isAdding;
  protected readonly bagError = this.cart.error;

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
      const shareImage = item ? galleryFor(item, null)[0] : undefined;
      untracked(() => {
        if (item) {
          this.pageMeta.set({
            title: documentTitle(item),
            description: metaDescription(item),
            // The product, never the colourway: one page, not one per colour (see the class docblock).
            path: `/product/${item.slug}`,
            kind: 'product',
            // The product's own first photograph, and `null` for the colourway on purpose: the
            // canonical address above is the product rather than the colourway, so a card showing
            // whichever colour happened to be selected would illustrate a different URL.
            imageUrl: shareImage?.url ?? null,
            imageAlt: shareImage?.altText ?? item.name,
          });
          // A page that is fine must not inherit the noindex a 404 left behind on a client-side
          // navigation.
          this.robots.clear();
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

        this.title.setTitle(brandPageTitle('Product unavailable'));
        // Deliberately no `noindex`, and none inherited from a 404 the shopper arrived through:
        // the point of a 503 is that the URL keeps its place, and a crawler that reads the body of
        // an error page would take the meta tag at its word (#30).
        this.robots.clear();

        serviceUnavailable(this.response);
      });
    });

    // The next page owns its own canonical address and card, or has none.
    inject(DestroyRef).onDestroy(() => this.pageMeta.clear());
  }

  protected retry(): void {
    this.store.retry(this.slug());
  }

  /**
   * The new colourway keeps the size only if it is made in it; `resolveSelection` is the same
   * rule the URL is read with, so the address bar can never end up naming a variant that does
   * not exist (`?colour=2&size=12` when Ecru stops at M).
   */
  protected chooseColour(colourId: number): void {
    const item = this.product();
    const next = { colourId, sizeId: this.selection().sizeId };
    this.apply(item ? resolveSelection(item, next) : next);
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

  /**
   * Reads the box, clamps it, and **writes the clamped number back into the element**.
   *
   * The write-back is not belt and braces. `[value]` only touches the DOM when the bound value
   * changes, so whenever the typed text clamped to the number the binding already held — `0`, an
   * empty box, `1.7`, or `99` on a product with one left — Angular wrote nothing and the box went
   * on showing the raw text while the page priced and stepped from 1. docs/04 §4 asks for the
   * clamp to happen live rather than on blur, and this is what makes it visible.
   */
  protected onQuantityInput(event: Event): void {
    const input = event.target as HTMLInputElement;
    const raw = Number(input.value);

    this.quantityWanted.set(Number.isFinite(raw) && raw >= 1 ? Math.floor(raw) : 1);
    input.value = String(this.quantity());
  }

  /**
   * Add to Bag (PDP-03, CART-01).
   *
   * The quantity sent is `quantity()`, which is already clamped to the variant's stock — so the server
   * is never asked for more than the page is showing. It can still refuse (the stock moved, the product
   * was unpublished a second ago), and `bagError()` shows what it said.
   */
  protected addToBag(): void {
    const variant = this.chosenVariant();
    const item = this.product();
    // `adding()` joins the guard because the button is `aria-disabled` rather than `disabled` and
    // so is still clickable: what the attribute says is unavailable, this has to make inert.
    if (!variant || !item || !this.canAddToBag() || this.adding()) {
      return;
    }

    this.cart.add(
      variant.id,
      this.quantity(),
      `${item.name}, ${variant.colourName}, size ${variant.sizeLabel}`,
    );
  }

  protected toggleWishlist(): void {
    // `'unavailable'` means nothing changed, in storage or in memory — so the heart stays as it was and
    // the shopper is told why, rather than being shown a filled heart over a save that did not happen.
    // Read `saved()` **before** the toggle: on a refusal the signal does not move, but reading it
    // first says plainly that it is the attempted operation being named rather than the outcome.
    const wasSaved = this.saved();

    // **The name and the colourway are recorded, and this is the one surface that knows both** (W2-08
    // round 2, MAJOR 2). It used to send a bare slug while the listing card sent `{ slug, name }`, so the
    // page with a colour visibly chosen on it stored the least of the two — and the stored name is the
    // only thing the wishlist page can print for an item the shop has since withdrawn (doc 11 §575).
    // Two such entries read as "This item … no longer available" twice over, with two identically
    // labelled Remove buttons.
    //
    // This is **not** the absent-field rule being bent: that rule is for a caller which does not know
    // (the listing card has colour dots and no selection, so it omits the colour). Here the selection is
    // on screen, so the fields are sent — and `colourId` is sent as an explicit `null` when nothing is
    // chosen yet, because "the shopper has not picked a colour on the product page" really is "no
    // colour", not "I was not asked".
    const item = this.product();
    const colour = this.colours().find((option) => option.colour.id === this.selection().colourId);

    const outcome = this.wishlist.toggle(
      {
        slug: this.currentSlug(),
        name: item?.name ?? null,
        colourId: colour?.colour.id ?? null,
        colourName: colour?.colour.name ?? null,
      },
      item?.name ?? 'This item',
    );

    if (outcome !== 'unavailable') {
      this.wishFailure.set(null);
      return;
    }

    this.wishFailure.set(wasSaved ? WISHLIST_REMOVE_UNAVAILABLE : WISHLIST_SAVE_UNAVAILABLE);
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

  /**
   * The colour to draw the sold-out slash in, chosen for the swatch it lands on.
   *
   * It used to be `--color-text-muted` on every swatch, which is a mark that is mostly not there:
   * 1.08:1 on Olive Green, 1.16:1 on Deep Teal, under 3:1 (WCAG 2.2 AA 1.4.11) on twelve of the
   * sixteen seeded colourways. {@link readableInk} measures both inks against the swatch rather
   * than reading a lightness threshold — {@link isLight} below is a threshold, and it is the
   * wrong question here: it calls Mustard and Blush Pink dark, and white on those scores 2.17:1
   * and 2.06:1. Worst measured across the palette is now 4.75:1.
   */
  protected slashInk(hex: string | null): string {
    return readableInk(hex) === 'dark' ? 'var(--color-text)' : 'var(--color-on-primary)';
  }

  /**
   * Whether a pale swatch needs a stronger edge to be seen against white (docs/04 §4). The same
   * rule as the card, and a different question from {@link slashInk}: this is about the ring
   * around the dot, that is about what can be drawn on top of it.
   */
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
   * trying four sizes should not cost four presses of Back to leave the page. `scroll: 'manual'` for the same
   * reason: choosing a size must not carry the shopper from the buy box back up to the top of the page.
   */
  private apply(selection: { colourId: number | null; sizeId: number | null }): void {
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: toVariantParams(selection),
      queryParamsHandling: 'merge',
      replaceUrl: true,
      scroll: 'manual',
    });
  }
}

/**
 * The document title. `seo.title` is whatever an admin wrote, falling back to the product name in
 * the API, so the brand is appended only when it is not already there — the seeded departments
 * showed what happens otherwise: their SEO titles carry the brand, and appending it said it twice.
 */
function documentTitle(product: ProductDetailDto): string {
  const title = product.seo.title.trim() || product.name;
  return title.includes(BRAND.name) ? title : brandPageTitle(title);
}

/**
 * The meta description. `Products.Description` is sanitised HTML (doc 08 §3.2), so its tags are
 * taken out before it can reach a `<meta>` — the admin-written SEO description and the plain
 * short description are preferred over it precisely because they are prose.
 */
function metaDescription(product: ProductDetailDto): string {
  const text =
    product.seo.description?.trim() ||
    product.shortDescription?.trim() ||
    stripTags(product.description ?? '');

  return text ? text.slice(0, 300) : `${product.name} at ${BRAND.name}.`;
}

function stripTags(html: string): string {
  return html
    .replace(/<[^>]*>/g, ' ')
    .replace(/\s+/g, ' ')
    .trim();
}
