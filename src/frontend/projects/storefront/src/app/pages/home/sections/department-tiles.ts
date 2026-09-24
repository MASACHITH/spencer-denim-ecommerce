import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { RouterLink } from '@angular/router';

import { HomeSectionDto, HomeSectionItemDto } from '../../../core/content/content.model';
import { destinationOf, isUsableTile, labelOf, toLink } from '../../../core/content/home-sections';

/** One tile, resolved once so the template does no work. */
interface Tile {
  readonly id: number;
  readonly label: string;
  readonly path: string;
  readonly queryParams: Readonly<Record<string, string | string[]>> | null;
  readonly imageUrl: string | null;
  readonly imageAltText: string;
  readonly buttonLabel: string;
}

/**
 * HOME-02: Shop by Department — Women, Men, Kids.
 *
 * Each tile is one link over the whole card, so a phone has a target the size of the tile and a
 * screen reader hears one stop rather than three ("photo", "Women", "Shop Women"). The photograph
 * is decorative for that reason.
 *
 * <b>A tile with no photograph is still a tile.</b> There is no media library yet (W1-04), so the
 * seeded tiles have none, and the block would be missing from the homepage for weeks if a picture
 * were the price of entry. A tile without one is a named panel on the section background — which
 * is a thing a shopper can use, unlike the empty frame a banner without artwork would be.
 */
@Component({
  selector: 'sd-department-tiles',
  imports: [RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="home-section container" [attr.aria-labelledby]="headingId()">
      @if (section().title; as heading) {
        <div class="home-section__head">
          <h2 class="home-section__title" [id]="headingId()">{{ heading }}</h2>
          @if (section().subtitle; as standfirst) {
            <p class="home-section__subtitle">{{ standfirst }}</p>
          }
        </div>
      }

      <ul class="tiles">
        @for (tile of tiles(); track tile.id) {
          <li>
            <a
              class="tile"
              [routerLink]="tile.path"
              [queryParams]="tile.queryParams"
              data-testid="department-tile"
            >
              <span class="tile__media">
                @if (tile.imageUrl; as image) {
                  <img [src]="image" [alt]="tile.imageAltText" loading="lazy" decoding="async" />
                }
              </span>
              <span class="tile__body">
                <span class="tile__name">{{ tile.label }}</span>
                <span class="tile__cta">{{ tile.buttonLabel }}</span>
              </span>
            </a>
          </li>
        }
      </ul>
    </section>
  `,
  styles: `
    :host {
      display: block;
    }
    .tiles {
      display: grid;
      grid-template-columns: 1fr;
      gap: var(--space-2);
      list-style: none;
    }
    @media (min-width: 768px) {
      .tiles {
        grid-template-columns: repeat(3, minmax(0, 1fr));
      }
    }
    .tile {
      display: grid;
      height: 100%;
      text-decoration: none;
      background: var(--color-bg-alt);
      border: 1px solid var(--color-border);
    }
    .tile__media {
      display: block;
      /* The same 4:5 frame whether or not there is a photograph, so nothing moves the day the
         client uploads one (W1-04). */
      aspect-ratio: 4 / 5;
      max-height: 420px;
      overflow: hidden;
      background: var(--color-border);
    }
    @media (min-width: 768px) {
      .tile__media {
        max-height: none;
      }
    }
    .tile__media img {
      width: 100%;
      height: 100%;
      object-fit: cover;
    }
    .tile__body {
      display: flex;
      flex-wrap: wrap;
      align-items: baseline;
      justify-content: space-between;
      gap: var(--space-1) var(--space-2);
      /* Tall enough for a 44 px target on its own, which is what makes the tile usable even if a
         future layout stops the whole card being the link. */
      min-height: var(--tap);
      padding: var(--space-2);
    }
    .tile__name {
      font-size: var(--text-h3);
    }
    .tile__cta {
      font-size: var(--text-label);
      font-weight: var(--weight-medium);
      letter-spacing: var(--tracking-label);
      text-transform: uppercase;
      text-decoration: underline;
      text-underline-offset: 4px;
    }
    .tile:hover .tile__cta {
      text-decoration-thickness: 2px;
    }
  `,
})
export class DepartmentTiles {
  readonly section = input.required<HomeSectionDto>();

  protected readonly headingId = computed(() => `home-section-${this.section().id}`);

  protected readonly tiles = computed<readonly Tile[]>(() =>
    this.section()
      .items.filter(isUsableTile)
      .map((item) => this.toTile(item))
      .filter((tile): tile is Tile => tile !== null),
  );

  private toTile(item: HomeSectionItemDto): Tile | null {
    const link = toLink(destinationOf(item));
    const label = labelOf(item);

    // `isUsableTile` has already established both — it requires an internal link, which is the
    // only kind either tile component can draw. This narrows them for the template; it is not a
    // second opinion, and a tile that reached here without them would be a bug in that filter.
    if (link?.kind !== 'internal' || !label) {
      return null;
    }

    return {
      id: item.id,
      label,
      path: link.path,
      queryParams: link.queryParams,
      imageUrl: item.imageUrl ?? item.mobileImageUrl,
      // Decorative: the tile's own name is right beside it and the link already says where it goes.
      imageAltText: '',
      buttonLabel: item.buttonLabel?.trim() || `Shop ${label}`,
    };
  }
}
