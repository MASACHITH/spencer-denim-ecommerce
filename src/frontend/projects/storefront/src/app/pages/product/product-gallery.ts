import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  computed,
  effect,
  input,
  linkedSignal,
  signal,
  untracked,
  viewChildren,
} from '@angular/core';
import { Icon } from '@spencer-denim/shared';

import { Modal } from '../../ui/modal/modal';
import { ProductImageDto } from '../../core/catalog/catalog.model';

/** How far the full-screen view magnifies, in steps. 1 is "fits the screen". */
const ZOOM_STEPS = [1, 2, 3] as const;

/**
 * The product image gallery (PDP-01): thumbnails, a main stage, zoom, a full-screen view and the
 * product video when there is one.
 *
 * **Nothing here assumes an image will load.** Media is not served yet (W1-04): the catalogue
 * seeds storage keys under `public/originals/demo/`, nothing answers for them, and every single
 * `<img>` on this page 404s today. So every frame — stage and thumbnail alike — is a fixed 3:4
 * box with the ivory ground behind it, and a failed or absent image swaps the `<img>` for a
 * placeholder *inside that same box*. The layout is identical whether the photographs arrive or
 * not, and nothing on the page moves when one fails.
 *
 * **Keyboard.** The thumbnails are a tab list over one stage: arrows move between them, Home and
 * End jump to the ends, and only the selected thumbnail is in the tab order, so a product with
 * twelve photographs costs one Tab stop rather than twelve.
 */
@Component({
  selector: 'sd-product-gallery',
  imports: [Icon, Modal],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="gallery">
      @if (hasRail()) {
        <div
          class="gallery__rail"
          role="tablist"
          [attr.aria-label]="'Images of ' + productName()"
        >
          @for (slide of slides(); track slide.id; let index = $index) {
            <button
              class="gallery__thumb"
              type="button"
              role="tab"
              #thumb
              [id]="tabId(index)"
              [attr.aria-selected]="index === selected()"
              [attr.aria-controls]="stageId"
              [tabindex]="index === selected() ? 0 : -1"
              [class.is-selected]="index === selected()"
              (click)="select(index)"
              (keydown)="onRailKeydown($event)"
            >
              @if (thumbSource(slide); as source) {
                <img
                  [src]="source"
                  alt=""
                  loading="lazy"
                  decoding="async"
                  (error)="onThumbError(slide.id)"
                />
              } @else {
                <span class="gallery__placeholder" aria-hidden="true"></span>
              }
              @if (slide.role === 'Video') {
                <span class="gallery__play" aria-hidden="true"><sd-icon name="play" size="xs" /></span>
              }
              <span class="sr-only">{{ slideLabel(slide, index) }}</span>
            </button>
          }
        </div>
      }

      <!-- A tabpanel with no tab list is a half a pattern: the role only makes sense while the
           rail exists, which is from two slides up. One slide or none is just a picture. -->
      <div
        class="gallery__stage"
        [id]="stageId"
        [attr.role]="hasRail() ? 'tabpanel' : null"
        [attr.aria-labelledby]="hasRail() ? tabId(selected()) : null"
        [attr.aria-label]="hasRail() ? null : productName()"
      >
        @if (current(); as slide) {
          @if (slide.role === 'Video') {
            <video
              class="gallery__video"
              controls
              preload="none"
              playsinline
              [attr.poster]="slide.posterUrl"
              [attr.aria-label]="productName() + ' video'"
            >
              <source [src]="slide.url" />
            </video>
          } @else if (stageSource(slide); as source) {
            <button
              class="gallery__open"
              type="button"
              [attr.aria-label]="'View ' + productName() + ' full screen'"
              (click)="openFullScreen()"
            >
              <img
                class="gallery__img"
                [src]="source"
                [alt]="altFor(slide)"
                fetchpriority="high"
                decoding="async"
                (error)="onStageError(slide.id)"
              />
              <span class="gallery__zoom-hint" aria-hidden="true">
                <sd-icon name="search" size="sm" />
              </span>
            </button>
          } @else {
            <span class="gallery__placeholder gallery__placeholder--stage">
              <span class="sr-only">No photograph of {{ productName() }} yet</span>
            </span>
          }
        } @else {
          <span class="gallery__placeholder gallery__placeholder--stage">
            <span class="sr-only">No photograph of {{ productName() }} yet</span>
          </span>
        }
      </div>
    </div>

    <sd-modal
      [open]="fullScreen()"
      [heading]="productName()"
      size="full"
      closeLabel="Close full screen view"
      (closed)="closeFullScreen()"
    >
      <div class="viewer">
        <div class="viewer__frame" tabindex="0" role="group" aria-label="Zoomed image, scroll to pan">
          @if (fullScreenSource(); as source) {
            <img
              class="viewer__img"
              [class.viewer__img--fit]="zoom() === minZoom"
              [src]="source"
              [alt]="altFor(current())"
              [style.width.%]="zoom() > minZoom ? zoom() * 100 : null"
              decoding="async"
            />
          } @else {
            <span class="gallery__placeholder gallery__placeholder--stage"></span>
          }
        </div>
        <div class="viewer__bar">
          <button
            class="btn btn--secondary"
            type="button"
            [disabled]="zoom() === minZoom"
            (click)="zoomOut()"
          >
            Zoom out
          </button>
          <p class="viewer__level" role="status">{{ zoom() }}&times;</p>
          <button
            class="btn btn--secondary"
            type="button"
            [disabled]="zoom() === maxZoom"
            (click)="zoomIn()"
          >
            Zoom in
          </button>
        </div>
      </div>
    </sd-modal>
  `,
  styles: `
    :host {
      display: block;
      min-width: 0;
    }
    .gallery {
      display: grid;
      gap: var(--space-1);
    }
    /* The thumbnails sit under the stage on phones and beside it from 768 px, which is the
       vertical rail docs/04 §2 names as the PDP reference. */
    /* The rail scrolls, so its padding box is the clip edge — and a focus ring is drawn *outside*
       the control's border box, two pixels out and two and a half wide. With the thumbnails flush
       against that edge, three of the ring's four sides were cropped away and only a sliver along
       the top survived (lesson L4, WCAG 2.2 AA 2.4.11). The gutter is the ring's own width plus
       the offset, taken from the token so a thicker ring widens it, and scroll-padding keeps it
       when a thumbnail is scrolled to by arrow key. An inset outline would have been the other
       way out, and the wrong one: it reads as a border, not as focus. */
    .gallery__rail {
      --ring-gap: calc(var(--focus-width) + 3px);

      display: flex;
      gap: var(--space-1);
      order: 2;
      overflow-x: auto;
      overscroll-behavior-x: contain;
      padding: var(--ring-gap);
      scroll-padding: var(--ring-gap);
      margin: calc(var(--ring-gap) * -1);
    }
    .gallery__thumb {
      position: relative;
      flex: 0 0 auto;
      width: 56px;
      padding: 0;
      border: 1px solid var(--color-border);
      background: var(--color-bg-alt);
      cursor: pointer;
    }
    .gallery__thumb.is-selected {
      border-color: var(--color-primary);
    }
    .gallery__thumb img,
    .gallery__placeholder {
      display: block;
      width: 100%;
      aspect-ratio: 3 / 4;
      object-fit: cover;
      background: var(--color-bg-alt);
    }
    .gallery__play {
      position: absolute;
      inset-block-end: 2px;
      inset-inline-end: 2px;
      display: grid;
      place-items: center;
      width: 20px;
      height: 20px;
      background: var(--color-primary);
      color: var(--color-on-primary);
    }
    .gallery__stage {
      order: 1;
      position: relative;
      min-width: 0;
      background: var(--color-bg-alt);
    }
    .gallery__open {
      display: block;
      width: 100%;
      padding: 0;
      border: 0;
      background: none;
      overflow: hidden;
      cursor: zoom-in;
    }
    .gallery__img,
    .gallery__video,
    .gallery__placeholder--stage {
      display: block;
      width: 100%;
      aspect-ratio: 3 / 4;
      object-fit: cover;
      background: var(--color-bg-alt);
    }
    /* Zoom in place, for a pointer. The precise zoom lives in the full-screen view, which a
       keyboard and a touch screen can reach just as well. */
    @media (hover: hover) {
      .gallery__img {
        transition: transform var(--duration) var(--ease);
      }
      .gallery__open:hover .gallery__img {
        transform: scale(1.6);
      }
    }
    .gallery__zoom-hint {
      position: absolute;
      inset-block-end: var(--space-1);
      inset-inline-end: var(--space-1);
      display: grid;
      place-items: center;
      width: var(--tap);
      height: var(--tap);
      background: var(--color-surface);
      color: var(--color-text);
      border: 1px solid var(--color-border);
    }
    @media (min-width: 768px) {
      .gallery {
        grid-template-columns: 72px minmax(0, 1fr);
        gap: var(--space-1-5);
      }
      .gallery__rail {
        order: 0;
        flex-direction: column;
        max-height: 720px;
        /* One axis, not two: overflow-x visible with overflow-y auto computes to auto on both,
           so writing the pair only hid that this clips horizontally as well. */
        overflow: auto;
      }
      .gallery__thumb {
        width: 100%;
      }
    }

    .viewer {
      display: flex;
      flex-direction: column;
      height: 100%;
      min-height: 0;
    }
    .viewer__frame {
      flex: 1 1 auto;
      min-height: 0;
      overflow: auto;
      background: var(--color-bg-alt);
      text-align: center;
    }
    .viewer__img {
      display: inline-block;
      max-width: none;
      height: auto;
    }
    /* At 1x the photograph fits the frame; every step above it overflows and the frame pans. */
    .viewer__img--fit {
      width: auto;
      max-width: 100%;
      max-height: 100%;
    }
    .viewer__bar {
      display: flex;
      align-items: center;
      justify-content: center;
      gap: var(--space-2);
      padding: var(--space-1-5) var(--space-2);
      border-top: 1px solid var(--color-border);
    }
    .viewer__level {
      min-width: 3ch;
      font-variant-numeric: tabular-nums;
      text-align: center;
    }
  `,
})
export class ProductGallery {
  /** Stills first, then any video. The page assembles this from the payload. */
  readonly media = input.required<readonly ProductImageDto[]>();
  readonly productName = input.required<string>();

  protected readonly minZoom = ZOOM_STEPS[0];
  protected readonly maxZoom = ZOOM_STEPS[ZOOM_STEPS.length - 1];
  protected readonly stageId = `gallery-stage-${nextGalleryId++}`;

  private readonly thumbs = viewChildren<ElementRef<HTMLButtonElement>>('thumb');

  /** Media whose file the browser could not fetch, so it is never asked for twice. */
  private readonly broken = signal<ReadonlySet<number>>(new Set());

  /** Slides in display order. A product with no media at all has none, and the stage says so. */
  protected readonly slides = computed(() => this.media());

  /** Back to the first slide whenever the media changes — a new colourway, or a new product. */
  protected readonly selected = linkedSignal<readonly ProductImageDto[], number>({
    source: this.slides,
    computation: () => 0,
  });

  protected readonly current = computed(() => this.slides()[this.selected()] ?? null);

  /** Whether there is a thumbnail rail at all, which is what makes the stage a tab panel. */
  protected readonly hasRail = computed(() => this.slides().length > 1);

  private readonly viewerOpen = signal(false);
  protected readonly fullScreen = this.viewerOpen.asReadonly();

  private readonly zoomLevel = signal<number>(ZOOM_STEPS[0]);
  protected readonly zoom = this.zoomLevel.asReadonly();

  /** True only while the shopper is driving the rail from the keyboard. See the effect below. */
  private keyboardDriven = false;

  constructor() {
    // A tab list moves focus with the selection, but only once the shopper is using it: doing it
    // on the first render would steal focus from the top of the page on arrival.
    effect(() => {
      const index = this.selected();
      untracked(() => {
        if (this.keyboardDriven) {
          this.keyboardDriven = false;
          this.thumbs()[index]?.nativeElement.focus();
        }
      });
    });
  }

  protected tabId(index: number): string {
    return `${this.stageId}-tab-${index}`;
  }

  protected select(index: number): void {
    this.selected.set(index);
  }

  /** What a screen reader hears on a thumbnail: which one it is, and whether it is the film. */
  protected slideLabel(slide: ProductImageDto, index: number): string {
    const position = `${index + 1} of ${this.slides().length}`;
    return slide.role === 'Video' ? `Video, ${position}` : `Image ${position}`;
  }

  protected altFor(slide: ProductImageDto | null): string {
    return slide?.altText?.trim() || this.productName();
  }

  protected stageSource(slide: ProductImageDto): string | null {
    return this.sourceOf(slide, slide.url);
  }

  /** A video's thumbnail is its poster; without one it falls back to the placeholder box. */
  protected thumbSource(slide: ProductImageDto): string | null {
    return this.sourceOf(slide, slide.role === 'Video' ? slide.posterUrl : slide.url);
  }

  protected fullScreenSource(): string | null {
    const slide = this.current();
    return slide && slide.role !== 'Video' ? this.stageSource(slide) : null;
  }

  protected onStageError(id: number): void {
    this.markBroken(id);
  }

  protected onThumbError(id: number): void {
    this.markBroken(id);
  }

  protected openFullScreen(): void {
    this.zoomLevel.set(ZOOM_STEPS[0]);
    this.viewerOpen.set(true);
  }

  protected closeFullScreen(): void {
    this.viewerOpen.set(false);
  }

  protected zoomIn(): void {
    this.stepZoom(1);
  }

  protected zoomOut(): void {
    this.stepZoom(-1);
  }

  /**
   * Arrow keys move through the thumbnails; Home and End jump to the ends. Both axes are
   * accepted because the rail is a row on a phone and a column from 768 px, and a shopper should
   * not have to know which.
   */
  protected onRailKeydown(event: KeyboardEvent): void {
    const count = this.slides().length;
    if (count === 0) {
      return;
    }

    const step = ARROW_STEP[event.key];
    let next: number | null = null;

    if (step !== undefined) {
      next = (this.selected() + step + count) % count;
    } else if (event.key === 'Home') {
      next = 0;
    } else if (event.key === 'End') {
      next = count - 1;
    }

    if (next === null) {
      return;
    }

    event.preventDefault();
    this.keyboardDriven = true;
    this.selected.set(next);
  }

  private stepZoom(direction: number): void {
    const index = ZOOM_STEPS.indexOf(this.zoomLevel() as (typeof ZOOM_STEPS)[number]);
    const next = ZOOM_STEPS[Math.min(Math.max(index + direction, 0), ZOOM_STEPS.length - 1)];
    this.zoomLevel.set(next);
  }

  private sourceOf(slide: ProductImageDto, url: string | null): string | null {
    if (this.broken().has(slide.id)) {
      return null;
    }

    const trimmed = url?.trim();
    return trimmed ? trimmed : null;
  }

  private markBroken(id: number): void {
    this.broken.update((broken) => new Set(broken).add(id));
  }
}

/** Which way each arrow moves the selection. Both axes, for the two rail orientations. */
const ARROW_STEP: Readonly<Record<string, number | undefined>> = {
  ArrowRight: 1,
  ArrowDown: 1,
  ArrowLeft: -1,
  ArrowUp: -1,
};

let nextGalleryId = 0;
