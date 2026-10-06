import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
} from '@angular/core';
import { Params, RouterLink } from '@angular/router';
import { Icon } from '@spencer-denim/shared';

import { Modal } from '../modal/modal';
import { productErrorMessage } from '../../core/catalog/catalog-api';
import {
  ProductBadge,
  ProductCardDto,
  ProductDetailDto,
  ProductVariantDto,
  StockStatus,
} from '../../core/catalog/catalog.model';
import { formatLkr } from '../../core/catalog/money';
import { ProductDetailStore } from '../../core/catalog/product-detail-store';
import {
  COLOUR_PARAM,
  ColourOption,
  SIZE_PARAM,
  SizeOption,
  VariantSelection,
  colourOptions,
  galleryFor,
  hasPriceRange,
  priceFor,
  resolveSelection,
  sizeOptions,
  stockFor,
  variantFor,
} from '../../core/catalog/product-variants';
import { CartStore } from '../../core/cart/cart.store';
import { readableInk } from '../../core/platform/contrast';

/** Why the dialog was opened, which is the only difference between the card's two triggers. */
export type QuickViewIntent = 'view' | 'add';

const STOCK_LABEL: Readonly<Record<StockStatus, string>> = {
  InStock: 'In stock',
  LowStock: 'Low stock',
  OutOfStock: 'Out of stock',
};

/** Badge text, as on the card (CARD-01) and the product page (PDP-02). */
const BADGE_LABEL: Readonly<Record<ProductBadge, string>> = {
  New: 'New',
  Sale: 'Sale',
  OnlineExclusive: 'Online exclusive',
  BestSeller: 'Best seller',
  LowStock: 'Low stock',
};

/** How many stills the rail shows before the rest are left to the product page. */
const MAX_THUMBNAILS = 4;

/** The narrow shape the dialog needs from either source of a photograph. */
interface QuickViewPhoto {
  readonly id: number;
  readonly url: string;
  readonly srcSet: string | null;
  readonly thumbnailUrl: string | null;
  readonly altText: string | null;
}

/** Ids have to be unique per dialog: a listing may hold one of these per card. */
let nextId = 0;

/**
 * Quick View (QV-01): choose a colourway and a size and put it in the bag **without leaving the
 * listing**.
 *
 * That sentence is the whole justification for the dialog. A card already links to the product
 * page, so a modal that only restated the card would be a second way to read the same four lines;
 * what it adds is the one thing the card cannot do, which is settle a variant. Everything in here
 * either serves that choice (the photographs of the chosen colourway, the swatches, the sizes, the
 * quantity, the price that moves when a size with a `PriceOverride` is picked) or is the escape
 * hatch from it ("View full details", which carries the selection into the URL so the choice is not
 * made twice).
 *
 * **What it deliberately does not do.**
 * - **It does not write the selection into the address bar**, which is the opposite of the product
 *   page's rule (see `product-variants.ts`). It cannot: a listing's query string already owns
 *   `?colour=` and `?size=` as **filters** (PLP-02), so writing a colourway id into `colour` would
 *   re-run the listing against a filter value that is not even a colour-family slug. The selection
 *   is therefore local to the dialog, and the link out is what makes it shareable.
 * - **No wishlist heart.** The card's heart is right beside the trigger and is still the local,
 *   per-browser list until W2-08; a second heart over the same `localStorage` would be two controls
 *   for one bit of state.
 * - **No reviews, no delivery estimate, no related products, no size guide.** Those are the product
 *   page, and this is the thing that exists so a shopper does not have to go there.
 *
 * **One at a time, which is what lets the ids inside be fixed.** `panelId`, `qv-colour-label` and
 * `qv-size-label` are constants rather than per-instance strings, as the product page's are. A
 * listing holds a card's worth of this component per card, so that is only safe because a second
 * one cannot be opened while the first is up: the scrim is `position: fixed; inset: 0` above
 * everything, focus is trapped inside the panel, and `aria-modal` takes the rest of the page out of
 * the accessibility tree. The quantity box's id is per-instance anyway, because a `<label for>` is
 * the one place a stale match would silently point at the wrong control rather than being caught.
 *
 * **Server-side rendering.** The dialog is created by a click, so it never exists during a server
 * render. Nothing here touches `window` or `document`; the focus trap, the scroll lock and Escape
 * all belong to {@link Modal}, which is the same dialog the size guide (PDP-04) and the gallery's
 * full-screen view (PDP-01) use — adopting it rather than copying it was the whole point of the
 * `TODO(QV-01)` note that component carried.
 *
 * **The detail fetch.** A card carries no variants (`ProductCardDto` has colours and nothing else),
 * so the variants have to be fetched, and {@link ProductDetailStore} already does exactly that
 * fetch with the ticketing and the three failure modes worked out. It is provided per dialog, so
 * closing one takes its state with it. While the fetch is outstanding the dialog shows the card's
 * own name, photograph and price rather than an empty box — they are already on the screen behind
 * it, and replacing them with a spinner would be a worse answer than the truth we already have.
 */
@Component({
  selector: 'sd-quick-view',
  imports: [Icon, Modal, RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [ProductDetailStore],
  host: { class: 'sd-quick-view' },
  template: `
    <sd-modal
      [open]="open()"
      [heading]="product().name"
      closeLabel="Close quick view"
      panelId="quick-view"
      (closed)="closed.emit()"
    >
      @if (store.isMissing()) {
        <!-- A card for something the catalogue no longer publishes. The listing behind is stale,
             which is worth saying plainly rather than offering a link that would 404. -->
        <div class="qv__state">
          <p>This product is no longer available.</p>
          <button class="btn btn--secondary" type="button" (click)="closed.emit()">
            Back to the listing
          </button>
        </div>
      } @else {
        <div class="qv">
          <div class="qv__media">
            <!-- An empty alt when the asset has no alt text, rather than the product name: the
                 dialog is titled with the name a line below, and a photograph that repeats its own
                 heading is a second read of the same words. ADM-11 should require alt text. -->
            @if (mainImage(); as image) {
              <img
                class="qv__photo"
                [src]="image.url"
                [attr.srcset]="image.srcSet"
                [attr.sizes]="image.srcSet ? PHOTO_SIZES : null"
                [alt]="image.altText ?? ''"
                decoding="async"
              />
            } @else {
              <span class="qv__photo qv__photo--empty"></span>
            }

            @if (thumbnails().length > 1) {
              <ul class="qv__rail">
                @for (thumb of thumbnails(); track thumb.id; let index = $index) {
                  <li>
                    <button
                      class="qv__thumb"
                      type="button"
                      [class.is-selected]="index === imageIndex()"
                      [attr.aria-pressed]="index === imageIndex()"
                      [attr.aria-label]="'Show photograph ' + (index + 1) + ' of ' + thumbnails().length"
                      (click)="showImage(index)"
                    >
                      <img [src]="thumb.thumbnailUrl ?? thumb.url" alt="" decoding="async" />
                    </button>
                  </li>
                }
              </ul>
            }
          </div>

          <div class="qv__info">
            @if (badge(); as label) {
              <p class="qv__badge" [class.qv__badge--sale]="product().badge === 'Sale'">
                {{ label }}
              </p>
            }

            <p class="qv__cat">{{ product().departmentName }} · {{ product().categoryName }}</p>

            @if (ratingAverage(); as average) {
              <p class="rating">
                <span class="sr-only">
                  Rated {{ average }} out of 5 from {{ product().rating.count }} reviews
                </span>
                <sd-icon name="star" size="xs" [filled]="true" />
                <span aria-hidden="true">
                  {{ average }} <span class="muted">({{ product().rating.count }})</span>
                </span>
              </p>
            }

            <p class="price qv__price" [class.price--sale]="price().hasDiscount">
              @if (showFrom()) {
                <span class="qv__from">From</span>
              }
              @if (price().hasDiscount) {
                <span class="sr-only">Sale price</span>
              }
              <span class="price__now">{{ currentPrice() }}</span>
              @if (price().hasDiscount) {
                <s class="price__was">
                  <span class="sr-only">Original price</span>{{ originalPrice() }}
                </s>
                <span class="price__off">-{{ discountPercent() }}%</span>
              }
            </p>

            @if (detail()) {
              <p class="qv__stock" [class.qv__stock--out]="stock().status === 'OutOfStock'">
                {{ stockLabel() }}
              </p>

              @if (colours().length > 0) {
                <div class="qv__group" role="group" aria-labelledby="qv-colour-label">
                  <p class="qv__legend" id="qv-colour-label">
                    Colour: <span class="qv__chosen">{{ selectedColourName() }}</span>
                  </p>
                  <div class="qv__options">
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
                <div class="qv__group" role="group" aria-labelledby="qv-size-label">
                  <p class="qv__legend" id="qv-size-label">
                    Size: <span class="qv__chosen">{{ selectedSizeLabel() }}</span>
                  </p>
                  <div class="qv__options">
                    @for (option of sizes(); track option.size.id) {
                      <button
                        class="sd-size"
                        type="button"
                        data-testid="quick-view-size"
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
                </div>
              }

              <div class="qv__group">
                <label class="qv__legend" [for]="quantityId">Quantity</label>
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
                    [id]="quantityId"
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
                <p class="qv__note" role="status">{{ quantityNote() }}</p>
              </div>

              <!-- Unavailable until there is exactly one thing to add, as PDP-03 asks of the
                   product page's button, and describing itself for the same reason: a greyed-out
                   control with no reason is the accessible version of a dead button.

                   **aria-disabled rather than disabled, and that is the fix (round 2).** The
                   disabled attribute on the element that *currently holds focus* makes the browser
                   blur it to the document body, and Chromium fires no focusin doing so - so
                   Modal.onFocusIn never re-trapped and the shopper was thrown out of an open
                   dialog by their own press of Add to Bag. On a successful add the mini bag takes
                   focus and hides it; on a **refused** add it does not, and a screen-reader user
                   simply lost their place inside a dialog that was still open. Identical root
                   cause to the ADM-22 settings defect, and the identical remedy: stay focusable,
                   say unavailable, and refuse the activation in addToBag(). It also makes the
                   aria-describedby below reachable, which on a disabled button it was not. -->
              <button
                class="btn btn--primary btn--block"
                type="button"
                data-testid="quick-view-add"
                [attr.aria-disabled]="canAddToBag() && !adding() ? null : 'true'"
                [attr.aria-describedby]="addToBagReason() ? quantityNoteId : null"
                (click)="addToBag()"
              >
                <sd-icon name="bag" size="sm" />
                {{ adding() ? 'Adding…' : 'Add to bag' }}
              </button>

              <!-- A live region, and always in the DOM rather than inside an @if (round 2, NIT 2).
                   Choosing a sold-out size announced **nothing**: quantityNote() returns '' for an
                   OutOfStock selection, so the status region above went silent, and the one line
                   that did change - this one - was not a live region at all. An emptied region says
                   nothing and a region that appears already holding its text is not a change, so
                   this has to exist before the sentence arrives in it. Empty when there is no
                   reason, which costs an empty paragraph and no height.
                   It is also the aria-describedby target, so the reason is both read on focus and
                   announced when it changes. -->
              <p class="qv__note" [id]="quantityNoteId" role="status">{{ addToBagReason() }}</p>
            } @else if (store.isUnavailable()) {
              <!-- The store answered badly. The product probably exists, so the dialog says so and
                   offers the two things that can still be done: ask again, or open the full page. -->
              <p class="qv__error" role="alert">{{ errorMessage() }}</p>
              <button class="btn btn--secondary btn--block" type="button" (click)="retry()">
                Try again
              </button>
            } @else {
              <p class="qv__note" role="status">Loading sizes and colours…</p>
            }

            @if (bagError(); as message) {
              <p class="qv__error" role="alert">{{ message }}</p>
            }

            <a class="link qv__full" [routerLink]="link()" [queryParams]="linkParams()">
              View full details
            </a>
          </div>
        </div>
      }
    </sd-modal>
  `,
  styles: `
    :host {
      display: contents;
    }
    .qv {
      display: grid;
      gap: var(--space-2);
      min-width: 0;
    }
    .qv__state {
      display: grid;
      justify-items: start;
      gap: var(--space-2);
    }
    @media (min-width: 768px) {
      .qv {
        grid-template-columns: minmax(0, 5fr) minmax(0, 6fr);
        gap: var(--space-3);
      }
    }
    .qv__media,
    .qv__info {
      min-width: 0;
    }
    .qv__info {
      display: grid;
      align-content: start;
      gap: var(--space-1-5);
    }
    .qv__photo {
      width: 100%;
      aspect-ratio: 3 / 4;
      object-fit: cover;
      background: var(--color-bg-alt);
    }
    .qv__photo--empty {
      display: block;
    }
    .qv__rail {
      display: flex;
      flex-wrap: wrap;
      gap: var(--space-1);
      padding-top: var(--space-1);
      list-style: none;
    }
    /* A 44 px target with the picture inside it, so a thumbnail rail is tappable on a phone
       without the pictures being 44 px of nothing. */
    .qv__thumb {
      display: grid;
      place-items: center;
      width: var(--tap);
      height: var(--tap);
      padding: 2px;
      border: 1px solid transparent;
      background: none;
      cursor: pointer;
    }
    .qv__thumb.is-selected {
      border-color: var(--color-primary);
    }
    .qv__thumb img {
      width: 100%;
      height: 100%;
      object-fit: cover;
    }
    .qv__badge {
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
    .qv__badge--sale {
      background: var(--color-sale);
    }
    .qv__cat {
      font-size: var(--text-label);
      letter-spacing: 0.1em;
      text-transform: uppercase;
      color: var(--color-text-muted);
    }
    .qv__price {
      font-size: var(--text-h3);
    }
    .qv__from {
      font-size: var(--text-small);
      color: var(--color-text-muted);
    }
    .qv__stock {
      font-size: var(--text-small);
      color: var(--color-success);
    }
    .qv__stock--out {
      color: var(--color-text-muted);
    }
    .qv__group {
      display: grid;
      gap: var(--space-1);
      min-width: 0;
    }
    .qv__legend {
      font-size: var(--text-small);
      color: var(--color-text-muted);
    }
    .qv__chosen {
      color: var(--color-text);
    }
    .qv__options {
      display: flex;
      flex-wrap: wrap;
      gap: var(--space-1);
    }
    .qv__note {
      font-size: var(--text-label);
      color: var(--color-text-muted);
      min-height: 1.4em;
    }
    .qv__error {
      padding: var(--space-1);
      border: 1px solid var(--color-sale);
      color: var(--color-sale);
      font-size: var(--text-label);
    }
    /* Full tap height, because it is the way out of the dialog on a phone. */
    .qv__full {
      display: inline-flex;
      align-items: center;
      justify-self: start;
      min-height: var(--tap);
      font-size: var(--text-small);
    }
  `,
})
export class QuickView {
  /** The card that opened this. It is what the dialog draws from until the detail arrives. */
  readonly product = input.required<ProductCardDto>();

  readonly open = input.required<boolean>();

  /**
   * Which trigger opened it. `'add'` means the shopper pressed Add to Bag on the card, so a
   * product with exactly one thing to buy is added without a second press — see {@link autoAdd}.
   */
  readonly intent = input<QuickViewIntent>('view');

  /** Raised when the dialog should go away: Escape, the scrim, the close button, or a finished add. */
  readonly closed = output<void>();

  protected readonly store = inject(ProductDetailStore);
  private readonly cart = inject(CartStore);

  /**
   * The dialog's panel is 720 px at most and the photograph takes a little under half of it from
   * 768 px up, so the 600 w card rendition covers it everywhere. Without a `sizes` the browser
   * assumes the image fills the viewport and takes the 1200 w one.
   */
  protected readonly PHOTO_SIZES = '(min-width: 768px) 320px, calc(100vw - 48px)';

  /** Unique per dialog instance, because a listing may hold one of these per card. */
  protected readonly quantityId = `qv-quantity-${nextId}`;
  protected readonly quantityNoteId = `qv-add-reason-${nextId++}`;

  private readonly chosen = signal<VariantSelection>({ colourId: null, sizeId: null });
  private readonly quantityWanted = signal(1);
  private readonly activeImage = signal(0);

  /** True from the moment this dialog asks the bag for something until the bag answers. */
  private addRequested = false;

  /**
   * Whether this dialog has asked the bag for anything yet.
   *
   * It gates the error line, and it is not belt and braces. `CartStore.error` is a **root** signal
   * holding the last thing that went wrong anywhere — a failed `GET /api/cart` from the header on
   * page load is enough — so binding it directly would open a brand-new dialog already showing a
   * red message about something that had nothing to do with it.
   */
  private readonly tried = signal(false);

  /** So the single-variant shortcut fires at most once per dialog. */
  private autoAdded = false;

  protected readonly detail = this.store.product;

  /** The slug the fetch was issued for, so a re-render cannot issue it again. */
  private requested: string | null = null;

  constructor() {
    // In an effect rather than straight from the constructor, because a required input has no
    // value yet at construction (NG8118) - and the effect's first run is still before the dialog's
    // first paint, which is all this needs. The dialog is created by a click, so there is never a
    // server render to be in flight during.
    effect(() => {
      const slug = this.product().slug;
      untracked(() => {
        if (slug !== this.requested) {
          this.requested = slug;
          this.store.load(slug);
        }
      });
    });

    // The default colourway is whatever the product has (`resolveSelection`'s rule: the first one
    // with stock), applied **once the detail is here** rather than guessed from the card. Size has
    // no default on purpose — PDP-03 wants a deliberate choice, and a variant's `PriceOverride`
    // means a guessed size would misprice the dialog.
    effect(() => {
      const item = this.detail();
      untracked(() => {
        if (item) {
          this.chosen.set(resolveSelection(item, { colourId: null, sizeId: null }));
          this.activeImage.set(0);
        }
      });
    });

    effect(() => {
      const item = this.detail();
      untracked(() => this.autoAdd(item));
    });

    // Closing on a finished add is what makes the mini bag (CART-02) the confirmation: the drawer
    // opens itself when `lastAdded` moves, and two dialogs trapping focus against each other is a
    // keyboard user with nowhere to go. A **failed** add keeps the dialog, because the error and
    // the controls that caused it belong on the same screen.
    //
    // It is keyed on `isAdding` falling rather than on `lastAdded` alone, because that signal is
    // shared: a shopper who added something from the product page earlier in the session arrives
    // here with it already non-null. `addRequested` is what makes this dialog answer only for its
    // own add.
    effect(() => {
      const adding = this.cart.isAdding();
      untracked(() => {
        if (!this.addRequested || adding) {
          return;
        }

        this.addRequested = false;
        if (this.cart.error() === null) {
          this.closed.emit();
        }
      });
    });
  }

  protected readonly selection = this.chosen.asReadonly();
  protected readonly imageIndex = this.activeImage.asReadonly();

  protected readonly adding = this.cart.isAdding;

  /** What the shop said about **this dialog's** add, or null. See {@link tried}. */
  protected readonly bagError = computed(() => (this.tried() ? this.cart.error() : null));

  protected readonly errorMessage = computed(() => {
    const failure = this.store.error();
    return failure ? productErrorMessage(failure) : '';
  });

  protected readonly colours = computed<readonly ColourOption[]>(() => {
    const item = this.detail();
    return item ? colourOptions(item) : [];
  });

  protected readonly sizes = computed<readonly SizeOption[]>(() => {
    const item = this.detail();
    return item ? sizeOptions(item, this.chosen().colourId) : [];
  });

  /**
   * The chosen colourway's stills, capped. The rail is a way to see the colour from a second
   * angle, not the gallery — a shopper who wants every photograph wants the product page, and the
   * link to it is right there.
   */
  protected readonly thumbnails = computed(() => {
    const item = this.detail();
    return item ? galleryFor(item, this.chosen().colourId).slice(0, MAX_THUMBNAILS) : [];
  });

  /**
   * What the big picture is.
   *
   * The card's own photograph until the detail arrives, so the dialog opens on the picture the
   * shopper just tapped rather than on a grey box; after that it is the colourway's, which is what
   * makes changing the colour mean something.
   */
  protected readonly mainImage = computed<QuickViewPhoto | null>(() => {
    const stills = this.thumbnails();
    if (stills.length > 0) {
      return stills[Math.min(this.activeImage(), stills.length - 1)];
    }

    const card = this.product();
    return card.imageUrl === null
      ? null
      : {
          id: -1,
          url: card.imageUrl,
          srcSet: card.imageSrcSet,
          thumbnailUrl: null,
          altText: card.imageAltText,
        };
  });

  /** The exact colour × size the shopper has settled on, or null while a choice is outstanding. */
  private readonly chosenVariant = computed<ProductVariantDto | null>(() => {
    const item = this.detail();
    return item ? variantFor(item, this.chosen()) : null;
  });

  /** The selected variant's price, the product's band until then, and the card's before that. */
  protected readonly price = computed(() => {
    const item = this.detail();
    return item ? priceFor(item, this.chosen()) : this.product().price;
  });

  protected readonly showFrom = computed(() => {
    const item = this.detail();
    return item !== null && this.chosenVariant() === null && hasPriceRange(item);
  });

  protected readonly currentPrice = computed(() => formatLkr(this.price().current));
  protected readonly originalPrice = computed(() => formatLkr(this.price().original));
  protected readonly discountPercent = computed(() => Math.round(this.price().discountPercent));

  protected readonly ratingAverage = computed(() => {
    const rating = this.product().rating;
    return rating.count > 0 && rating.average !== null ? rating.average : null;
  });

  protected readonly badge = computed(() => {
    const badge = this.product().badge;
    return badge ? (BADGE_LABEL[badge] ?? null) : null;
  });

  protected readonly stock = computed(() => {
    const item = this.detail();
    return item
      ? stockFor(item, this.chosen())
      : { status: this.product().stockStatus, available: 0 };
  });

  protected readonly stockLabel = computed(() => {
    const { status, available } = this.stock();
    if (status === 'LowStock' && available > 0) {
      return available === 1 ? 'Only 1 left' : `Only ${available} left`;
    }

    return STOCK_LABEL[status];
  });

  protected readonly selectedColourName = computed(() => {
    const id = this.chosen().colourId;
    return this.colours().find((option) => option.colour.id === id)?.colour.name ?? 'Not chosen';
  });

  protected readonly selectedSizeLabel = computed(() => {
    const id = this.chosen().sizeId;
    return this.sizes().find((option) => option.size.id === id)?.size.label ?? 'Choose a size';
  });

  /** The same ceiling the product page uses, and for the same reason (see `product-page.ts`). */
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

  protected readonly quantityNote = computed(() => {
    const variant = this.chosenVariant();
    const outstanding = !variant && this.sizes().length > 0;

    if (this.stock().status === 'OutOfStock') {
      return '';
    }

    if (this.quantityWanted() > this.maxQuantity()) {
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

  protected readonly canAddToBag = computed(() => {
    const variant = this.chosenVariant();
    return variant !== null && variant.availableQuantity > 0;
  });

  /**
   * Why Add to Bag is disabled, in a sentence, or null when it is not.
   *
   * **The chosen size is asked about first, and the order is the fix.** `stock()` narrows to the
   * selection, so a shopper who picks a sold-out size makes `stock().status` `OutOfStock` for a
   * product that has plenty of the other sizes - and asking that question first answered "This
   * product is out of stock", which is false, and made the sentence about the size unreachable.
   * The product page had the branches the other way round and the same dead branch; it is
   * corrected there too, in the same commit.
   */
  protected readonly addToBagReason = computed(() => {
    if (this.canAddToBag()) {
      return null;
    }

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

  protected readonly link = computed(() => ['/product', this.product().slug]);

  /**
   * The selection, carried into the product page's URL so the choice is not made twice. The
   * parameter names are the product page's own (`product-variants.ts`), and an unmade choice is
   * left out rather than sent as null — `?size=` naming nothing is a URL the page would have to
   * recover from.
   */
  protected readonly linkParams = computed<Params>(() => {
    const { colourId, sizeId } = this.chosen();
    const params: Params = {};
    if (colourId !== null) {
      params[COLOUR_PARAM] = colourId;
    }
    if (sizeId !== null) {
      params[SIZE_PARAM] = sizeId;
    }

    return params;
  });

  protected chooseColour(colourId: number): void {
    const item = this.detail();
    const next = { colourId, sizeId: this.chosen().sizeId };
    // The same rule the product page applies, so a colourway that is not made in the chosen size
    // clears it rather than leaving a button lit that names no variant.
    this.chosen.set(item ? resolveSelection(item, next) : next);
    this.activeImage.set(0);
  }

  /** Tapping the chosen size again clears it, which is how a shopper undoes a mis-tap. */
  protected chooseSize(sizeId: number): void {
    const current = this.chosen();
    this.chosen.set({
      colourId: current.colourId,
      sizeId: current.sizeId === sizeId ? null : sizeId,
    });
  }

  protected showImage(index: number): void {
    this.activeImage.set(index);
  }

  protected stepQuantity(step: number): void {
    this.quantityWanted.set(Math.min(Math.max(this.quantity() + step, 1), this.maxQuantity()));
  }

  /**
   * Reads the box, clamps it, and writes the clamped number back into the element — the write-back
   * for the reason the product page documents: `[value]` only touches the DOM when the bound value
   * changes, so a typed `99` that clamps to the number already held would leave the raw text on
   * screen.
   */
  protected onQuantityInput(event: Event): void {
    const input = event.target as HTMLInputElement;
    const raw = Number(input.value);

    this.quantityWanted.set(Number.isFinite(raw) && raw >= 1 ? Math.floor(raw) : 1);
    input.value = String(this.quantity());
  }

  /**
   * The button says `aria-disabled` rather than `disabled`, so it is still focusable and still
   * clickable — which makes refusing the activation *here* the thing that keeps it inert. `send`
   * already refuses a missing variant, a sold-out one and a second add over one in flight, which
   * is exactly `!canAddToBag() || adding()`; the guard is restated so the rejection is visible at
   * the handler the button actually calls rather than two frames away.
   */
  protected addToBag(): void {
    if (!this.canAddToBag() || this.adding()) {
      return;
    }

    this.send(this.chosenVariant());
  }

  protected retry(): void {
    this.store.retry(this.product().slug);
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

  /** The same measured choice of slash colour the product page makes (WCAG 1.4.11). */
  protected slashInk(hex: string | null): string {
    return readableInk(hex) === 'dark' ? 'var(--color-text)' : 'var(--color-on-primary)';
  }

  protected isLight(hex: string | null): boolean {
    return isLightSwatch(hex);
  }

  /**
   * The card's Add to Bag, finished.
   *
   * A card cannot know whether a choice is required — `ProductCardDto` carries colours and no
   * variants at all — so the trigger opens this dialog and the decision is made **here**, where
   * the variants are known. A product with exactly one buyable variant has nothing to choose, so
   * it goes straight into the bag and the dialog closes itself; anything else stays open with the
   * size list and the reason. The card never guesses a size, which is the rule this preserves.
   *
   * Once only, and never for the Quick View trigger: `'view'` means the shopper asked to look.
   */
  private autoAdd(item: ProductDetailDto | null): void {
    if (!item || this.autoAdded || this.intent() !== 'add') {
      return;
    }

    // Consumed whether or not the shortcut then fires, so a retry after a failed fetch does not
    // put something in the bag a second or two after the shopper has started reading the dialog.
    this.autoAdded = true;

    const buyable = item.variants.filter((variant) => variant.availableQuantity > 0);
    if (item.variants.length === 1 && buyable.length === 1) {
      this.chosen.set({ colourId: buyable[0].colourId, sizeId: buyable[0].sizeId });
      this.send(buyable[0]);
    }
  }

  private send(variant: ProductVariantDto | null): void {
    // `cart.add` ignores a second call while one is in flight, so asking first is what keeps
    // `addRequested` honest: a flag set over a call that was dropped would close this dialog on
    // somebody else's add.
    if (!variant || variant.availableQuantity <= 0 || this.cart.isAdding()) {
      return;
    }

    this.addRequested = true;
    this.tried.set(true);
    this.cart.add(
      variant.id,
      this.quantity(),
      `${this.product().name}, ${variant.colourName}, size ${variant.sizeLabel}`,
    );
  }
}

/**
 * Whether a pale swatch needs a stronger edge to be seen against white (docs/04 §4). The same
 * threshold the card and the product page use; it is a different question from {@link
 * QuickView.slashInk}, which is about what can be drawn *on* the swatch.
 */
function isLightSwatch(hex: string | null): boolean {
  if (!hex || !/^#[0-9a-f]{6}$/i.test(hex)) {
    return true;
  }

  const red = parseInt(hex.slice(1, 3), 16);
  const green = parseInt(hex.slice(3, 5), 16);
  const blue = parseInt(hex.slice(5, 7), 16);

  return (0.2126 * red + 0.7152 * green + 0.0722 * blue) / 255 > 0.8;
}
