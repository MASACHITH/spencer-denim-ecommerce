import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { RouterLink } from '@angular/router';

import { HomeSectionDto, HomeSectionItemDto } from '../../../core/content/content.model';
import { destinationOf, isUsableTile, labelOf, toLink } from '../../../core/content/home-sections';
import { FULL_WIDTH_GRID_CARD_SIZES, ProductCard } from '../../../ui/product-card/product-card';

/** One trending tile, resolved once (HOME-04, manual mode). */
interface Tile {
  readonly id: number;
  readonly label: string;
  readonly subtitle: string | null;
  readonly path: string;
  readonly queryParams: Readonly<Record<string, string | string[]>> | null;
  readonly imageUrl: string | null;
  readonly imageSrcSet: string | null;
}

/**
 * The `.trending` grid below: two columns, four from 1024 px, a 16 px gap, inside a container
 * that caps at 1440 px with 16 / 32 / 48 px gutters. Same reasoning as the card sizes — a srcset
 * with no `sizes` makes the browser fetch the widest candidate whatever the tile measures.
 */
const TILE_SIZES =
  '(min-width: 1440px) 324px,' +
  ' (min-width: 1280px) calc((100vw - 144px) / 4),' +
  ' (min-width: 1024px) calc((100vw - 112px) / 4),' +
  ' (min-width: 768px) calc((100vw - 80px) / 2),' +
  ' calc((100vw - 48px) / 2)';

/**
 * HOME-03 New Arrivals, HOME-04 Trending Items and HOME-07 Sale: a row under a heading.
 *
 * <b>What is in the row depends on how the client set the section up.</b> In <i>auto</i> mode the
 * API has already fetched the head of the listing named in the section's own settings, so the row
 * is the first page of that listing and the "view all" beside the heading opens the rest of it —
 * the two cannot drift apart, because they are the same query. In <i>manual</i> mode the client
 * has arranged tiles by hand and each one opens its own filtered listing, which is HOME-04's
 * wording: "each card opens the matching filtered listing".
 *
 * The cards are the listing's own `ProductCard` (CARD-01), not a homepage copy of it, so a
 * discount, a badge or a sold-out state reads the same here as it does on a listing page.
 */
@Component({
  selector: 'sd-product-row',
  imports: [ProductCard, RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="home-section container" [attr.aria-labelledby]="headingId()">
      <div class="home-section__head">
        <div class="home-section__heading">
          <h2 class="home-section__title" [id]="headingId()">{{ heading() }}</h2>
          @if (section().subtitle; as standfirst) {
            <p class="home-section__subtitle">{{ standfirst }}</p>
          }
        </div>

        <!-- Only when the client gave the section both a destination and a label for it. A link
             that reads "View all" and goes nowhere in particular is worse than no link. -->
        @if (sectionLink(); as target) {
          <a
            class="home-section__link"
            [routerLink]="target.path"
            [queryParams]="target.queryParams"
            data-testid="section-link"
          >
            {{ section().linkLabel }}
          </a>
        }
      </div>

      @if (products().length > 0) {
        <ul class="product-grid">
          @for (product of products(); track product.id) {
            <li>
              <sd-product-card [product]="product" [sizes]="cardSizes" />
            </li>
          }
        </ul>
      } @else {
        <ul class="trending">
          @for (tile of tiles(); track tile.id) {
            <li>
              <a
                class="trending__tile"
                [routerLink]="tile.path"
                [queryParams]="tile.queryParams"
                data-testid="trending-tile"
              >
                <span class="trending__media">
                  @if (tile.imageUrl; as image) {
                    <img
                      [src]="image"
                      [attr.srcset]="tile.imageSrcSet"
                      [attr.sizes]="tile.imageSrcSet ? tileSizes : null"
                      alt=""
                      loading="lazy"
                      decoding="async"
                    />
                  }
                </span>
                <span class="trending__body">
                  <span class="trending__name">{{ tile.label }}</span>
                  @if (tile.subtitle; as standfirst) {
                    <span class="trending__sub">{{ standfirst }}</span>
                  }
                </span>
              </a>
            </li>
          }
        </ul>
      }
    </section>
  `,
  styles: `
    :host {
      display: block;
    }
    .trending {
      display: grid;
      grid-template-columns: repeat(2, minmax(0, 1fr));
      gap: var(--space-2);
      list-style: none;
    }
    @media (min-width: 1024px) {
      .trending {
        grid-template-columns: repeat(4, minmax(0, 1fr));
      }
    }
    .trending__tile {
      display: grid;
      height: 100%;
      text-decoration: none;
      border: 1px solid var(--color-border);
      background: var(--color-bg-alt);
    }
    .trending__media {
      display: block;
      aspect-ratio: 3 / 4;
      overflow: hidden;
      background: var(--color-border);
    }
    .trending__media img {
      width: 100%;
      height: 100%;
      object-fit: cover;
    }
    .trending__body {
      display: grid;
      gap: var(--space-0-5);
      align-content: center;
      min-height: var(--tap);
      padding: var(--space-1-5) var(--space-2);
    }
    .trending__name {
      font-weight: var(--weight-medium);
    }
    .trending__sub {
      font-size: var(--text-small);
      color: var(--color-text-muted);
    }
    .trending__tile:hover .trending__name {
      text-decoration: underline;
      text-underline-offset: 4px;
    }
  `,
})
export class ProductRow {
  readonly section = input.required<HomeSectionDto>();

  /** A homepage row gives up no filter sidebar, so its cards are wider than a listing's. */
  protected readonly cardSizes = FULL_WIDTH_GRID_CARD_SIZES;
  protected readonly tileSizes = TILE_SIZES;

  protected readonly headingId = computed(() => `home-section-${this.section().id}`);

  protected readonly products = computed(() => this.section().products);

  /** A heading is always rendered: the row is a landmark and needs a name (WCAG 1.3.1). */
  protected readonly heading = computed(() => this.section().title?.trim() || 'Featured products');

  /** The section's own "view all", only when it is a path on this site and has a label. */
  protected readonly sectionLink = computed(() => {
    const target = toLink(this.section().linkUrl);

    return target?.kind === 'internal' && this.section().linkLabel?.trim() ? target : null;
  });

  protected readonly tiles = computed<readonly Tile[]>(() =>
    this.section()
      .items.filter(isUsableTile)
      .map((item) => this.toTile(item))
      .filter((tile): tile is Tile => tile !== null),
  );

  private toTile(item: HomeSectionItemDto): Tile | null {
    const link = toLink(destinationOf(item));
    const label = labelOf(item);

    if (link?.kind !== 'internal' || !label) {
      return null;
    }

    return {
      id: item.id,
      label,
      subtitle: item.subtitle?.trim() || null,
      path: link.path,
      queryParams: link.queryParams,
      imageUrl: item.imageUrl ?? item.mobileImageUrl,
      // The candidate list has to belong to the picture that was chosen, not to whichever one
      // the client happened to upload: the two crops are different assets with different ids.
      imageSrcSet: item.imageUrl ? item.imageSrcSet : item.mobileImageSrcSet,
    };
  }
}
