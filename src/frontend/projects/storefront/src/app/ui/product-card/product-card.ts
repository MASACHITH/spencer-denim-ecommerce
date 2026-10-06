import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Icon } from '@spencer-denim/shared';

import { ColourSwatchDto, ProductBadge, ProductCardDto } from '../../core/catalog/catalog.model';
import { formatLkr } from '../../core/catalog/money';
import { QuickView, QuickViewIntent } from '../quick-view/quick-view';
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
 * **Quick View and Add to Bag (QV-01, W2-04).** Both sit on the photograph, as docs/04 §4 decision
 * 5 draws them: two small icon buttons on a phone, a pair of labelled bars revealed on hover from
 * 1024 px. Tapping the card itself still opens the product page, which is the same decision's
 * other half, and is why neither trigger is the card's primary action.
 *
 * **Bottom-left on a phone, by an owner decision dated 2026-10-06** that reconciles decision 5
 * with decision 6's floating WhatsApp button (GLB-08), which owns the bottom-**right** corner of
 * the viewport site-wide. They were both in that corner and the float covered a card's Add to Bag
 * outright — at 320x720 on the very first card. The side is chosen in `.card__actions`, which says
 * so at length. The heart is untouched at top-right - but it is *not* collision-free: the float
 * covers it at a few scroll positions on every phone width (issue #111, to be fixed with W2-08).
 * An earlier version of this comment claimed it never collided, which had not been measured.
 *
 * **Both triggers open the same dialog, and that is not an oversight.** `ProductCardDto` carries
 * colours and *no variants at all*, so a card cannot know whether this product needs a size
 * chosen — and a card that guessed a size would put the wrong thing in a bag. So both triggers
 * open Quick View, which does have the variants, and the choice is made there. The two buttons
 * therefore differ only in what they say and in which one reports itself expanded — and that is
 * the honest cost of the card projection not carrying a variant count.
 *
 * There *was* a third behaviour: `intent="add"` made the dialog add a single-variant product and
 * close itself. Removed in round 2 — it opened, added and vanished, which reads as a flash or a
 * glitch, and it saved no request because the card still had to fetch the detail to know. The fix
 * worth having is `variantCount` on the card projection, so the card can skip the dialog *before*
 * opening it; that follow-up is filed.
 *
 * The wishlist heart is a real control, but it saves to this browser only until the wishlist API
 * arrives (W2-08) — see {@link LocalWishlist}.
 */
@Component({
  selector: 'sd-product-card',
  imports: [Icon, QuickView, RouterLink],
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

        <!-- The heart can fail silently — localStorage is refused outright by a private window, a
             blocked-site-data setting or a full quota — and LocalWishlist reports that now rather than
             filling the heart over nothing. A card has no room for a message and there may be
             twenty-four of them on a page, so the **control says it itself**: the heart stays unfilled,
             which is the truth, and the accessible name and the tooltip explain why. The product page
             has room for a sentence and shows one. -->
        <button
          class="card__icon card__wish"
          type="button"
          [attr.aria-pressed]="saved()"
          [attr.aria-label]="wishLabel()"
          [attr.title]="wishUnavailable() ? wishLabel() : null"
          (click)="toggleWishlist()"
        >
          <sd-icon name="heart" size="sm" [filled]="saved()" />
        </button>

        <!-- QV-01. aria-haspopup and aria-expanded on both, because both of them open the dialog
             and a shopper is entitled to know that before pressing: see the class docblock for why
             Add to Bag cannot skip it. The label is the product's, not "Quick view" alone - there
             are twenty-four of these on a listing and a screen reader reading twenty-four
             identically named buttons has told the shopper nothing. -->
        <div class="card__actions">
          <button
            class="card__icon card__action"
            type="button"
            data-testid="card-quick-view"
            aria-haspopup="dialog"
            [attr.aria-expanded]="openedBy() === 'view'"
            (click)="openQuickView('view')"
          >
            <sd-icon name="eye" size="sm" />
            <span class="card__action-label">Quick view</span>
            <span class="sr-only">&ngsp;{{ product().name }}</span>
          </button>
          <button
            class="card__icon card__action card__action--bag"
            type="button"
            data-testid="card-add-to-bag"
            aria-haspopup="dialog"
            [attr.aria-expanded]="openedBy() === 'add'"
            (click)="openQuickView('add')"
          >
            <sd-icon name="bag" size="sm" />
            <span class="card__action-label">Add to bag</span>
            <span class="sr-only">&ngsp;{{ product().name }}</span>
          </button>
        </div>
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

    <!-- Created by the press and destroyed by the close, which is what makes the focus trap, the
         scroll lock and the return of focus all free: the shared modal does them on activate and
         on destroy. A listing holds twenty-four cards, so a dialog that stayed mounted would be
         twenty-four dialogs' worth of render effects for the one a shopper might open. -->
    @if (quickViewOpen()) {
      <sd-quick-view
        [product]="product()"
        [open]="true"
        (closed)="closeQuickView()"
      />
    }
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

    /* The shell all three photo controls share - the heart, Quick View and Add to Bag (docs/04 §4
       decision 5). A 44 px target with a smaller white plate inside it, so the icons line up with
       each other and the target is a thumb's rather than an icon's. */
    .card__icon {
      position: relative;
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
    .card__icon::before {
      content: '';
      position: absolute;
      inset: 6px;
      background: var(--color-surface);
      transition: background-color var(--duration) var(--ease);
    }
    .card__icon sd-icon,
    .card__icon .card__action-label {
      position: relative;
    }
    .card__icon:hover::before {
      background: var(--color-bg-alt);
    }
    .card__wish {
      position: absolute;
      top: 0;
      right: 0;
    }

    /* Bottom-**left** on a phone, stacked, always there: docs/04 §4 decision 5 asks for small icon
       buttons on the photo rather than a hover affordance a touch screen has no way to produce.

       (No backticks in this block: it is a template literal, and one would end it.)

       **Left, and that is an owner decision dated 2026-10-06 reconciling decisions 5 and 6
       (docs/04 §4).** These were bottom-right, and decision 6 puts a 56 px fixed WhatsApp button in
       the bottom-right corner of the viewport site-wide at z-index 70 (GLB-08). The two
       specifications never mentioned each other and they collided: at 390x844 a card's Add to Bag
       was *fully* covered at 4 of 41 sampled scroll offsets, and at 320x720 it happened at
       scrollY 0 - so the first card a shopper saw had a dead button that opened WhatsApp. The
       float keeps the corner it was given and the card actions take the opposite one; nothing is
       removed and neither decision is weakened. The heart stays top-right - and collides at a
       handful of scroll positions itself, which is issue #111 rather than something this decision
       fixed: the heart is at the card's *top*-right against the float's *bottom*-right, so they
       meet as a card enters from below. Milder, and left to W2-08.

       **Where the switch happens, and why there is no new number here.** The side is set once in
       each of the two states this component already has, and the boundary between them is the
       existing min-width: 1024px query below - the same one that turns the stacked icon pair into
       a labelled bar. Below it the pair is a 44 px column and has a side to choose; at and above it
       the bar spans the whole photo (left: 0 *and* right: 0), so there is no side to choose and
       nothing to move. One declaration lives in each block and no third rule repeats the figure. */
    .card__actions {
      position: absolute;
      z-index: 1;
      left: 0;
      bottom: 0;
      display: flex;
      flex-direction: column;
    }
    /* Visible from 1024 px, where the pair becomes a labelled bar; until then it is the accessible
       name and nothing more. Same geometry as .sr-only, which cannot be used directly because the
       desktop rule has to undo it. */
    .card__action-label {
      position: absolute;
      width: 1px;
      height: 1px;
      overflow: hidden;
      clip: rect(0 0 0 0);
      white-space: nowrap;
    }
    @media (min-width: 1024px) {
      /* right: 0 is the other half of the side decision above: with left: 0 already set, the pair
         stops being a column with a side and becomes a bar across the whole photo. */
      .card__actions {
        right: 0;
        flex-direction: row;
      }
      .card__action {
        flex: 1 1 50%;
        width: auto;
        padding-inline: var(--space-0-5);
        font-size: 0.6875rem;
        font-weight: var(--weight-medium);
        letter-spacing: 0.1em;
        text-transform: uppercase;
      }
      .card__action::before {
        inset: 0;
      }
      .card__action sd-icon {
        display: none;
      }
      .card__action-label {
        position: relative;
        width: auto;
        height: auto;
        overflow: visible;
        clip: auto;
      }
      .card__action--bag {
        color: var(--color-on-primary);
      }
      .card__action--bag::before {
        background: var(--color-primary);
      }
      .card__action--bag:hover::before {
        background: var(--color-primary-hover);
      }
    }
    /* Revealed on hover only where there is a pointer that can hover. A touch screen keeps the
       icon buttons, which is the whole of decision 5. :focus-within is what makes the pair
       reachable by keyboard on a desktop. */
    @media (min-width: 1024px) and (hover: hover) {
      .card__actions {
        opacity: 0;
        transform: translateY(6px);
        transition:
          opacity var(--duration) var(--ease),
          transform var(--duration) var(--ease);
      }
      .card:hover .card__actions,
      .card:focus-within .card__actions {
        opacity: 1;
        transform: none;
      }
    }
    /* #38: the ring is drawn **outside** the control.

       It used to be a ::after with inset: 2px — inside the button's own border box — because of
       lesson L4: the ::before fill above would paint over an inset outline. That reasoning was
       right about ::before and wrong about the outline, which is not inset at all: the global rule
       in base.css draws it at outline-offset: 2px, clear of the border box, where ::before (inset
       6px) cannot reach it. Drawn inside, the ring was invisible from outside the control, which is
       what #38 measured: a focused-versus-blurred comparison of the strip outside each of the four
       edges came back byte for byte identical on all four.
       So the global ring is used, as every other control on the site uses it, and the white band is
       kept — it is what makes a dark ring legible over a dark photograph — as a box-shadow filling
       the 2 px the outline offset leaves. Both are outside the border box, so neither can be
       painted over. */
    .card__icon:focus-visible {
      box-shadow: 0 0 0 2px var(--color-on-primary);
    }
    /* The white band would be invisible against the dark Add to Bag bar, so that one gets the
       inverse: a dark band inside the primary-coloured plate, with the global ring outside it. */
    .card__action--bag:focus-visible {
      box-shadow: 0 0 0 2px var(--color-primary);
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

  /**
   * Which trigger has the Quick View dialog open, or null (QV-01).
   *
   * One signal rather than a boolean and an intent, so the two can never disagree - and it is what
   * `aria-expanded` is read from, per trigger: the dialog belongs to whichever button opened it,
   * and reporting both as expanded would tell a screen reader that pressing Add to Bag had done
   * something it had not.
   */
  private readonly quickView = signal<QuickViewIntent | null>(null);

  protected readonly openedBy = this.quickView.asReadonly();

  protected readonly quickViewOpen = computed(() => this.quickView() !== null);

  protected readonly saved = computed(() => this.wishlist.has(this.product().slug));

  /** The product detail page (PDP-01..08), which tapping the card itself opens (docs/04 §4). */
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

  private readonly wishFailed = signal(false);

  /** True once this card's heart has been tapped and storage refused the write. */
  protected readonly wishUnavailable = this.wishFailed.asReadonly();

  protected readonly wishLabel = computed(() => {
    const name = this.product().name;

    if (this.wishUnavailable()) {
      // Which operation was refused is readable from `saved()`, because a refusal leaves the signal
      // exactly as it was: a heart that was filled was being emptied. Without this the control said
      // "Could not save" while still reporting `aria-pressed="true"` — a sentence that contradicted
      // the state beside it, which is the whole defect this round was about one layer down.
      return this.saved()
        ? `Could not remove ${name} from wishlist — your browser is blocking site data`
        : `Could not save ${name} to wishlist — your browser is blocking site data`;
    }

    return this.saved() ? `Remove ${name} from wishlist` : `Save ${name} to wishlist`;
  });

  protected toggleWishlist(): void {
    // `'unavailable'` means nothing changed anywhere, so the heart stays as it was and the label says
    // why. A filled heart over a refused write is the lie this replaced.
    this.wishFailed.set(this.wishlist.toggle(this.product().slug) === 'unavailable');
  }

  protected openQuickView(intent: QuickViewIntent): void {
    this.quickView.set(intent);
  }

  protected closeQuickView(): void {
    this.quickView.set(null);
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
