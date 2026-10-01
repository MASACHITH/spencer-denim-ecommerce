import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { RouterLink } from '@angular/router';

import { HomeSectionDto } from '../../../core/content/content.model';
import { toLink } from '../../../core/content/home-sections';

/**
 * HOME-05 the full-width campaign banner, and HOME-06 the activewear feature.
 *
 * They are the same thing with different copy and a different crop: one picture, a few words over
 * it and one way in. Both are seeded switched off, because a banner <i>is</i> its artwork and
 * there is no media library yet (W1-04) — and `hasContent` keeps them off the page even if the
 * client switches one on before uploading the picture.
 *
 * The picture is decorative: everything it says is in the markup next to it.
 */
@Component({
  selector: 'sd-feature-banner',
  imports: [RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="banner" [attr.aria-labelledby]="headingId()">
      <div class="banner__media" [class.banner__media--scrim]="overlay() !== 'none'">
        <picture>
          @if (desktopImage(); as wide) {
            <source
              media="(min-width: 768px)"
              [srcset]="desktopImageSrcSet() ?? wide"
              [attr.sizes]="desktopImageSrcSet() ? '100vw' : null"
            />
          }
          @if (imageUrl(); as image) {
            <img
              [src]="image"
              [attr.srcset]="imageSrcSet()"
              [attr.sizes]="imageSrcSet() ? '100vw' : null"
              alt=""
              loading="lazy"
              decoding="async"
            />
          }
        </picture>
      </div>

      <div class="container banner__inner">
        <div class="banner__copy" [class.banner__copy--light]="overlay() === 'dark'">
          <h2 class="banner__title" [id]="headingId()">{{ heading() }}</h2>
          @if (subtitle(); as standfirst) {
            <p class="banner__lead">{{ standfirst }}</p>
          }
          @if (body(); as copy) {
            <p class="banner__body">{{ copy }}</p>
          }
          @if (link(); as target) {
            @if (target.kind === 'internal') {
              <a
                class="btn btn--primary banner__cta"
                [routerLink]="target.path"
                [queryParams]="target.queryParams"
              >{{ buttonLabel() }}</a>
            } @else {
              <a class="btn btn--primary banner__cta" [href]="target.href" rel="noopener noreferrer">
                {{ buttonLabel() }}
              </a>
            }
          }
        </div>
      </div>
    </section>
  `,
  styles: `
    :host {
      display: block;
    }
    .banner {
      position: relative;
      /* An explicit width, for the reason the hero carries one: with an auto width a definite
         max-height transfers through aspect-ratio into a max-width (720 x 21/9 = 1680 px), and
         both banners stopped 217 px short of the right-hand edge at 1920. */
      width: 100%;
      aspect-ratio: 4 / 5;
      max-height: 720px;
      overflow: hidden;
      background: var(--color-bg-alt);
    }
    @media (min-width: 768px) {
      .banner {
        aspect-ratio: 21 / 9;
      }
    }
    .banner__media {
      position: absolute;
      inset: 0;
    }
    .banner__media img {
      width: 100%;
      height: 100%;
      object-fit: cover;
    }
    /* On the media, never over the copy, so it cannot paint across a focus ring (lesson L4). */
    .banner__media--scrim::after {
      content: '';
      position: absolute;
      inset: 0;
      background: linear-gradient(to top, rgb(26 26 26 / 0.6) 0%, rgb(26 26 26 / 0.15) 60%, transparent 100%);
    }
    .banner__inner {
      position: relative;
      height: 100%;
      display: flex;
      align-items: flex-end;
      padding-block: var(--space-4);
    }
    .banner__copy {
      display: grid;
      gap: var(--space-1-5);
      justify-items: start;
      max-width: 42ch;
    }
    .banner__copy--light {
      color: var(--color-on-primary);
    }
    .banner__title {
      font-size: var(--text-h1);
      line-height: 1.1;
    }
    .banner__lead {
      font-size: var(--text-h4);
    }
    .banner__body {
      font-size: var(--text-small);
      line-height: 1.5;
    }
    .banner__cta {
      margin-top: var(--space-1);
      background: var(--color-bg);
      border-color: var(--color-bg);
      color: var(--color-text);
    }
    .banner__cta:hover {
      background: var(--color-bg-alt);
      border-color: var(--color-bg-alt);
    }
  `,
})
export class FeatureBanner {
  readonly section = input.required<HomeSectionDto>();

  protected readonly headingId = computed(() => `home-section-${this.section().id}`);

  private readonly item = computed(() => this.section().items[0] ?? null);

  protected readonly heading = computed(
    () => this.item()?.title?.trim() || this.section().title?.trim() || 'Featured',
  );

  protected readonly subtitle = computed(
    () => this.item()?.subtitle?.trim() || this.section().subtitle?.trim() || null,
  );

  protected readonly body = computed(() => this.item()?.body?.trim() || null);

  protected readonly buttonLabel = computed(() => this.item()?.buttonLabel?.trim() || 'Shop now');

  protected readonly link = computed(() => toLink(this.item()?.linkUrl));

  protected readonly overlay = computed(() => this.section().overlay ?? 'dark');

  /** The phone crop, falling back to the wide one when the client uploaded only one picture. */
  protected readonly imageUrl = computed(
    () => this.item()?.mobileImageUrl ?? this.item()?.imageUrl ?? null,
  );

  /**
   * The candidate list belonging to the crop above, never the other one: the two are separate
   * assets. A banner is full-bleed at every width, so both describe themselves as `100vw`.
   */
  protected readonly imageSrcSet = computed(() => {
    const item = this.item();
    return (item?.mobileImageUrl ? item.mobileImageSrcSet : item?.imageSrcSet) ?? null;
  });

  protected readonly desktopImage = computed(() => {
    const item = this.item();
    return item?.imageUrl && item.mobileImageUrl ? item.imageUrl : null;
  });

  protected readonly desktopImageSrcSet = computed(() =>
    this.desktopImage() ? (this.item()?.imageSrcSet ?? null) : null,
  );
}
