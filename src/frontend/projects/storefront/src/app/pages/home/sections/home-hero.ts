import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  computed,
  inject,
  input,
  signal,
  viewChild,
} from '@angular/core';
import { RouterLink } from '@angular/router';
import { Icon, ReducedMotion } from '@spencer-denim/shared';

import { HomeSectionDto } from '../../../core/content/content.model';
import { toLink } from '../../../core/content/home-sections';
import { Viewport } from '../../../core/platform/viewport';

/**
 * HOME-01: the hero banner.
 *
 * <b>It is an image far more often than it is a video.</b> docs/04 §4 makes the hero an image on a
 * phone and a video from 768 px, and `prefers-reduced-motion` switches the video off at every
 * width. On the server it is always the image: `ReducedMotion` answers `true` there, because
 * server-rendered HTML must never depend on an animation having run, and a crawler or a phone on a
 * slow connection then gets the still without ever fetching the video.
 *
 * <b>It carries a pause control.</b> WCAG 2.2.2 (Level A) asks for a way to stop anything that
 * moves on its own for more than five seconds, and a looping hero video is exactly that. The
 * announcement bar has the same control for the same reason.
 *
 * The video is decorative: the words are in the markup beside it, and the still carries the alt
 * text the client wrote in the media library, so the video element is `aria-hidden`.
 */
@Component({
  selector: 'sd-home-hero',
  imports: [Icon, RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="hero" [attr.aria-label]="label()">
      <div class="hero__media" [class.hero__media--scrim]="overlay() !== 'none'">
        @if (showVideo()) {
          <!-- A bound muted, not a bare muted attribute. This element is never in the server's
               HTML — the server always renders the still — so it is always created by Angular,
               and on that path the attribute does not set the muted *property*:
               HTMLMediaElement.muted stayed false, which is the one thing that stops Chrome
               autoplaying, and would have played sound if it ever had. -->
          <video
            #video
            class="hero__video"
            data-testid="hero-video"
            [src]="videoUrl()"
            [poster]="posterUrl()"
            [muted]="true"
            autoplay
            loop
            playsinline
            preload="metadata"
            aria-hidden="true"
            tabindex="-1"
            (play)="playing.set(true)"
            (playing)="playing.set(true)"
            (pause)="playing.set(false)"
          ></video>
        } @else if (stillUrl(); as still) {
          <picture>
            @if (desktopStill(); as wide) {
              <source
                media="(min-width: 768px)"
                [srcset]="desktopStillSrcSet() ?? wide"
                [attr.sizes]="desktopStillSrcSet() ? '100vw' : null"
              />
            }
            <img
              class="hero__image"
              data-testid="hero-image"
              [src]="still"
              [attr.srcset]="stillSrcSet()"
              [attr.sizes]="stillSrcSet() ? '100vw' : null"
              [alt]="stillAlt()"
              fetchpriority="high"
              decoding="async"
            />
          </picture>
        }
      </div>

      <div class="container hero__inner">
        <div class="hero__copy" [class.hero__copy--light]="overlay() === 'dark'">
          @if (title(); as heading) {
            <p class="hero__title">{{ heading }}</p>
          }
          @if (subtitle(); as standfirst) {
            <p class="hero__lead">{{ standfirst }}</p>
          }
          @if (link(); as target) {
            @if (target.kind === 'internal') {
              <a
                class="btn btn--primary hero__cta"
                [routerLink]="target.path"
                [queryParams]="target.queryParams"
              >
                {{ buttonLabel() }}
              </a>
            } @else {
              <a class="btn btn--primary hero__cta" [href]="target.href" rel="noopener noreferrer">
                {{ buttonLabel() }}
              </a>
            }
          }
        </div>

        @if (showVideo()) {
          <button
            class="hero__toggle"
            type="button"
            data-testid="hero-toggle"
            [attr.aria-pressed]="!playing()"
            [attr.aria-label]="playing() ? 'Pause the background video' : 'Play the background video'"
            (click)="toggle()"
          >
            <sd-icon [name]="playing() ? 'pause' : 'play'" size="sm" />
          </button>
        }
      </div>
    </section>
  `,
  styles: `
    :host {
      display: block;
    }
    .hero {
      position: relative;
      /* docs/04 §4: 4:5 on phones, 16:9 from 768 px, capped at 880 px on very wide screens. The
         box is fixed either way, so swapping the still for the video after hydration moves
         nothing.

         The explicit width below is load-bearing, not decoration. With an *auto* width, a
         definite max-height transfers through aspect-ratio and becomes a max-width — 880 x 16/9 =
         1564.44 px — so a full-bleed hero stopped 333 px short of the right-hand edge at 1920 and
         left a white band down the side of the page. An explicit width makes the width definite,
         and max-height then does only the job it was written for: capping the height. */
      width: 100%;
      aspect-ratio: 4 / 5;
      max-height: 880px;
      background: var(--color-bg-alt);
      overflow: hidden;
    }
    @media (min-width: 768px) {
      .hero {
        aspect-ratio: 16 / 9;
      }
    }
    .hero__media {
      position: absolute;
      inset: 0;
    }
    .hero__media img,
    .hero__media video {
      width: 100%;
      height: 100%;
      object-fit: cover;
    }
    /* W1-14: the focal point of the video, measured rather than guessed.

       The delivered hero.mp4 is 360 x 640 — portrait — and this frame is 16:9 from 768 px, so
       object-fit: cover scales it on the width and the overflow is entirely vertical: between 68%
       and 74% of the source height is thrown away, depending on whether max-height has clamped the
       box. A centred object-position discards that equally top and bottom, which lands the visible
       band on the model's chest and cuts her face off above the mouth. That is why HOME-01 was held
       back in W1-13.

       Moving the focal point up distributes the overflow instead of hiding it. Her face occupies
       roughly 25%-42% of the source height and barely moves across the ten seconds, so the band has
       to start above 25% and end below 42% at EVERY width. It is widest where the box is a true
       16:9 (768-1564 px: 31.64% of the source) and narrowest at 1920, where max-height 880px makes
       the box 2.18:1 and only 25.78% of the source survives — so 1920 is what pins the value.
       Solving both gives 21.8%-33.7%; 30% sits inside it with headroom at 1920. Do not round the
       upper bound up: 34% fails the e2e test. Verified by screenshot at 768/1024/1280/1440/1920,
       and the e2e suite photographs the hero at each of them and asserts the band that must stay in
       frame is whole inside the picture.

       AND IT RUNS OUT. The clamped box keeps growing wider than 16:9 past 1564 px, so the surviving
       band keeps shrinking. Measured: at 2560 px of hero the band is 19.3% of the source and 30%
       still holds, with about one point to spare; by 2890 px it is 17.1% and 30% has failed; past
       about 2900 px the band is narrower than her face (17.0%) and NO value of object-position fits
       it at all — 3440 x 1440 is an ordinary desktop. 30% is right for the widths this site is
       built and tested for, not universally.

       THIS IS MAKING THE BEST OF A FILE THAT IS WRONG FOR THE SLOT, NOT A FIX. 1920 px of frame
       from 360 px of source is a 5.3x upscale and the result is soft. At 768-1440 the crop is
       identical in source terms and her collar and shoulders are in frame; at 1920 the clamp takes
       those too and the hero is a face with no garment in it. A 16:9 export at least 1920 px wide
       is still the right answer, and the 3440 px case above is a second reason for it.

       On .hero__video alone and not on the shared rule above: the still (.hero__image) is a
       separate, correctly-shaped photograph and stays centred. The video element paints its own
       poster attribute, so that shifts too — it is 16:9, so below 1565 px there is no overflow for
       it to shift. */
    .hero__video {
      object-position: center 30%;
    }
    /* docs/04 decision 7: light text over the photograph with a soft dark fade under it. The fade
       is on the media, never over the copy, so it can never paint across a focus ring (lesson L4). */
    .hero__media--scrim::after {
      content: '';
      position: absolute;
      inset: 0;
      background: linear-gradient(to top, rgb(26 26 26 / 0.66) 0%, rgb(26 26 26 / 0.15) 55%, transparent 100%);
    }
    .hero__inner {
      position: relative;
      height: 100%;
      display: flex;
      align-items: flex-end;
      justify-content: space-between;
      gap: var(--space-2);
      padding-block: var(--space-4);
    }
    .hero__copy {
      display: grid;
      gap: var(--space-2);
      justify-items: start;
      max-width: 34ch;
    }
    .hero__copy--light {
      color: var(--color-on-primary);
    }
    .hero__title {
      font-size: var(--text-display);
      line-height: 1.05;
      letter-spacing: -0.01em;
    }
    .hero__lead {
      font-size: var(--text-body);
      line-height: 1.5;
    }
    .hero__cta {
      background: var(--color-bg);
      border-color: var(--color-bg);
      color: var(--color-text);
    }
    .hero__cta:hover {
      background: var(--color-bg-alt);
      border-color: var(--color-bg-alt);
    }
    .hero__toggle {
      display: inline-grid;
      place-items: center;
      flex: none;
      width: var(--tap);
      height: var(--tap);
      padding: 0;
      border: 1px solid var(--color-bg);
      background: rgb(26 26 26 / 0.55);
      color: var(--color-on-primary);
      cursor: pointer;
    }
    .hero__toggle:hover {
      background: rgb(26 26 26 / 0.8);
    }
    /* The ring is drawn outside the border box and the hero clips its overflow, so a control
       flush against the edge would lose three of its four sides (lesson L4, and the thumbnail
       rail of W2-05). Both controls sit inside the container's gutter, which is at least 16 px. */
    .hero__toggle:focus-visible,
    .hero__cta:focus-visible {
      outline-offset: 2px;
    }
  `,
})
export class HomeHero {
  private readonly reducedMotion = inject(ReducedMotion);
  private readonly viewport = inject(Viewport);

  readonly section = input.required<HomeSectionDto>();

  private readonly video = viewChild<ElementRef<HTMLVideoElement>>('video');

  /**
   * Whether the video is actually running, for the pause control's label and icon.
   *
   * It starts `false` and is moved only by the element's own events. It used to start `true`,
   * which was an assumption rather than an observation: autoplay is a request the browser may
   * refuse — a data saver, a battery saver, a site permission — and when it is refused no event
   * ever fires, so the button read "Pause" over a stopped video and pressing Pause started it.
   */
  protected readonly playing = signal(false);

  /** The hero is one item; the client arranges the rest of the page, not this. */
  private readonly item = computed(() => this.section().items[0] ?? null);

  protected readonly title = computed(() => this.item()?.title?.trim() || this.section().title);

  protected readonly subtitle = computed(
    () => this.item()?.subtitle?.trim() || this.section().subtitle,
  );

  protected readonly buttonLabel = computed(() => this.item()?.buttonLabel?.trim() || 'Shop now');

  protected readonly link = computed(() => toLink(this.item()?.linkUrl));

  protected readonly overlay = computed(() => this.section().overlay ?? 'dark');

  protected readonly videoUrl = computed(() => this.item()?.videoUrl ?? null);

  /**
   * The poster is the video's own frame when the media library made one, and the still otherwise.
   * Doc 08 §3.3 makes a poster mandatory on a video asset, so the first branch is the usual one.
   */
  protected readonly posterUrl = computed(
    () => this.item()?.videoPosterUrl ?? this.stillUrl() ?? null,
  );

  /** The phone still, falling back to the wide one when the client uploaded only one picture. */
  protected readonly stillUrl = computed(
    () => this.item()?.mobileImageUrl ?? this.item()?.imageUrl ?? null,
  );

  /**
   * The candidate list for whichever still {@link stillUrl} settled on. The phone crop and the
   * wide crop are separate assets, so their rendition sets are not interchangeable — and the
   * hero is full-bleed at every width, which is why both sources describe themselves as `100vw`.
   */
  protected readonly stillSrcSet = computed(() => {
    const item = this.item();
    return (item?.mobileImageUrl ? item.mobileImageSrcSet : item?.imageSrcSet) ?? null;
  });

  /** The wide still, used from 768 px when the client uploaded a separate one. */
  protected readonly desktopStill = computed(() => {
    const item = this.item();
    return item?.imageUrl && item.mobileImageUrl ? item.imageUrl : null;
  });

  protected readonly desktopStillSrcSet = computed(() =>
    this.desktopStill() ? (this.item()?.imageSrcSet ?? null) : null,
  );

  protected readonly stillAlt = computed(
    () => this.item()?.mobileImageAltText ?? this.item()?.imageAltText ?? '',
  );

  /**
   * The video plays only where docs/04 says it may: in a browser, wide enough for it, with motion
   * allowed, and with a video to play. Anything else renders the still.
   */
  protected readonly showVideo = computed(
    () => !!this.videoUrl() && this.viewport.isAtLeastTablet() && !this.reducedMotion.enabled(),
  );

  protected readonly label = computed(() => this.title() ?? this.section().title ?? 'Featured');

  /**
   * Plays or pauses the video.
   *
   * The signal is not set optimistically: `play()` returns a promise the browser can reject, and
   * a button that says "Pause" over a video that never started is exactly the bug this control
   * exists to avoid. `pause()` and a successful `play()` both raise the events above, which is
   * what moves the signal.
   */
  protected toggle(): void {
    const video = this.video()?.nativeElement;
    if (!video) {
      return;
    }

    if (video.paused) {
      void video.play().catch(() => this.playing.set(false));
    } else {
      video.pause();
    }
  }
}
