import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { RouterLink } from '@angular/router';

import { HomeSectionDto, HomeSectionItemDto } from '../../../core/content/content.model';
import { SectionLink, toLink } from '../../../core/content/home-sections';

/** One gallery photograph, resolved once. */
interface Shot {
  readonly id: number;
  readonly imageUrl: string;
  readonly imageSrcSet: string | null;
  readonly altText: string;
  readonly link: SectionLink;
  readonly label: string;
}

/**
 * The `.gallery` grid below: two columns, three from 768 px, six from 1024 px, an 8 px gap,
 * inside a container that caps at 1440 px with 16 / 32 / 48 px gutters. Six across is the
 * smallest picture on the homepage — about 218 px — so this is where a srcset without a `sizes`
 * would waste the most.
 */
const SHOT_SIZES =
  '(min-width: 1440px) 218px,' +
  ' (min-width: 1280px) calc((100vw - 136px) / 6),' +
  ' (min-width: 1024px) calc((100vw - 104px) / 6),' +
  ' (min-width: 768px) calc((100vw - 80px) / 3),' +
  ' calc((100vw - 40px) / 2)';

/**
 * HOME-08: the social gallery — admin-uploaded images that link to social profiles.
 *
 * <b>This is the one block whose links leave the site</b>, so it is the one place `toLink` is
 * allowed to return an external destination. Those are ordinary anchors with
 * `rel="noopener noreferrer"` and `target="_blank"`, and the accessible name says both where the
 * link goes and that it opens in a new tab — a link that silently replaces the page a shopper was
 * browsing is a small betrayal, and one that silently opens a tab is a confusing one.
 *
 * A photograph with no destination is still shown: it is a picture of the client's clothes, which
 * is the point of the block. It is simply not a link.
 */
@Component({
  selector: 'sd-social-gallery',
  imports: [RouterLink],
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
      </div>

      <ul class="gallery">
        @for (shot of shots(); track shot.id) {
          <li class="gallery__cell">
            @if (shot.link; as target) {
              @if (target.kind === 'external') {
                <a
                  class="gallery__link"
                  [href]="target.href"
                  target="_blank"
                  rel="noopener noreferrer"
                  data-testid="social-link"
                >
                  <img
                    [src]="shot.imageUrl"
                    [attr.srcset]="shot.imageSrcSet"
                    [attr.sizes]="shot.imageSrcSet ? shotSizes : null"
                    [alt]="shot.altText"
                    loading="lazy"
                    decoding="async"
                  />
                  <span class="sr-only">{{ shot.label }} (opens in a new tab)</span>
                </a>
              } @else {
                <a
                  class="gallery__link"
                  [routerLink]="target.path"
                  [queryParams]="target.queryParams"
                  data-testid="social-link"
                >
                  <img
                    [src]="shot.imageUrl"
                    [attr.srcset]="shot.imageSrcSet"
                    [attr.sizes]="shot.imageSrcSet ? shotSizes : null"
                    [alt]="shot.altText"
                    loading="lazy"
                    decoding="async"
                  />
                  <span class="sr-only">{{ shot.label }}</span>
                </a>
              }
            } @else {
              <img
                [src]="shot.imageUrl"
                [attr.srcset]="shot.imageSrcSet"
                [attr.sizes]="shot.imageSrcSet ? shotSizes : null"
                [alt]="shot.altText"
                loading="lazy"
                decoding="async"
              />
            }
          </li>
        }
      </ul>
    </section>
  `,
  styles: `
    :host {
      display: block;
    }
    .gallery {
      display: grid;
      grid-template-columns: repeat(2, minmax(0, 1fr));
      gap: var(--space-1);
      list-style: none;
    }
    @media (min-width: 768px) {
      .gallery {
        grid-template-columns: repeat(3, minmax(0, 1fr));
      }
    }
    @media (min-width: 1024px) {
      .gallery {
        grid-template-columns: repeat(6, minmax(0, 1fr));
      }
    }
    /* No overflow clipping here: object-fit cover on the image below already crops it to the
       square, and a clip would crop the focus ring off the link with it. */
    .gallery__cell {
      aspect-ratio: 1;
      background: var(--color-bg-alt);
    }
    .gallery__cell img {
      width: 100%;
      height: 100%;
      object-fit: cover;
    }
    .gallery__link {
      display: block;
      width: 100%;
      height: 100%;
    }
    /* An ordinary outward ring, measured in home-page.e2e.ts rather than assumed: the first
       version of this was inset, which the pixel check correctly reported as no ring at all from
       outside the photograph. The 8 px grid gap gives it room. */
    .gallery__link:focus-visible {
      outline-offset: 2px;
    }
  `,
})
export class SocialGallery {
  readonly section = input.required<HomeSectionDto>();

  protected readonly shotSizes = SHOT_SIZES;

  protected readonly headingId = computed(() => `home-section-${this.section().id}`);

  protected readonly heading = computed(() => this.section().title?.trim() || 'Follow us');

  protected readonly shots = computed<readonly Shot[]>(() =>
    this.section()
      .items.map((item) => this.toShot(item))
      .filter((shot): shot is Shot => shot !== null),
  );

  private toShot(item: HomeSectionItemDto): Shot | null {
    const imageUrl = item.imageUrl ?? item.mobileImageUrl;
    if (!imageUrl) {
      return null;
    }

    return {
      id: item.id,
      imageUrl,
      // Whichever crop was chosen above, with its own renditions and not the other asset's.
      imageSrcSet: item.imageUrl ? item.imageSrcSet : item.mobileImageSrcSet,
      // The alt text the client wrote in the media library, or none: a photograph with a link is
      // named by the visually hidden label below it, and one without is decoration.
      altText: item.imageAltText?.trim() ?? '',
      link: toLink(item.linkUrl),
      label: item.title?.trim() || 'See this on social media',
    };
  }
}
