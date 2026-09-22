import { isPlatformBrowser } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  PLATFORM_ID,
  computed,
  effect,
  inject,
  signal,
  untracked,
} from '@angular/core';
import { Icon, ReducedMotion } from '@spencer-denim/shared';

import { AnnouncementService } from '../../core/announcements/announcement.service';

/** How long each message stays up before the next one takes over. */
export const ANNOUNCEMENT_ROTATION_MS = 6000;

/**
 * The rotating announcement bar above the header (GLB-03).
 *
 * It rotates by itself, but never at the cost of control: previous/next always work, hovering or
 * tabbing into the bar pauses it, and `prefers-reduced-motion` switches the rotation off
 * altogether so the bar only ever changes when the visitor asks it to (GLB-12).
 */
@Component({
  selector: 'sd-announcement-bar',
  imports: [Icon],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    '(mouseenter)': 'setPaused(true)',
    '(mouseleave)': 'setPaused(false)',
    '(focusin)': 'setPaused(true)',
    '(focusout)': 'setPaused(false)',
  },
  template: `
    @if (messages().length > 0) {
      <div class="container announce">
        @if (hasMany()) {
          <button
            class="icon-btn announce__control"
            type="button"
            aria-label="Previous announcement"
            (click)="previous()"
          >
            <sd-icon name="chevron-left" size="sm" />
          </button>
        }
        <p class="announce__text" aria-live="polite" aria-atomic="true" data-testid="announcement">
          {{ current().text }}
        </p>
        @if (hasMany()) {
          <button
            class="icon-btn announce__control"
            type="button"
            aria-label="Next announcement"
            (click)="next()"
          >
            <sd-icon name="chevron-right" size="sm" />
          </button>
        }
      </div>
    }
  `,
  styles: `
    :host {
      display: block;
      background: var(--color-bg-alt);
      border-bottom: 1px solid var(--color-border);
    }
    .announce {
      display: flex;
      align-items: center;
      justify-content: center;
      gap: var(--space-0-5);
      font-size: var(--text-small);
      text-align: center;
    }
    .announce__control:hover {
      background: var(--color-border);
    }
    .announce__text {
      flex: 1 1 auto;
      max-width: 52ch;
      min-height: var(--tap);
      display: flex;
      align-items: center;
      justify-content: center;
      line-height: 1.4;
      padding-block: var(--space-0-5);
    }
    @media (max-width: 374px) {
      .announce {
        font-size: 0.8125rem;
      }
    }
  `,
})
export class AnnouncementBar {
  private readonly announcements = inject(AnnouncementService);
  private readonly reducedMotion = inject(ReducedMotion);
  private readonly isBrowser = isPlatformBrowser(inject(PLATFORM_ID));

  readonly messages = this.announcements.messages;

  private readonly index = signal(0);
  private readonly paused = signal(false);

  protected readonly hasMany = computed(() => this.messages().length > 1);

  /** Always a message: the index is wrapped against the current list length. */
  protected readonly current = computed(() => {
    const list = this.messages();
    return list[this.index() % list.length] ?? { id: 'empty', text: '' };
  });

  private timer: ReturnType<typeof setInterval> | null = null;

  constructor() {
    effect(() => {
      const shouldRotate =
        this.isBrowser && this.hasMany() && !this.paused() && !this.reducedMotion.enabled();

      untracked(() => this.setTimer(shouldRotate));
    });

    inject(DestroyRef).onDestroy(() => this.setTimer(false));
  }

  next(): void {
    this.step(1);
  }

  previous(): void {
    this.step(-1);
  }

  /** Exposed for tests: which message is showing. */
  currentIndex(): number {
    const length = this.messages().length;
    return length === 0 ? 0 : this.index() % length;
  }

  protected setPaused(paused: boolean): void {
    this.paused.set(paused);
  }

  private step(direction: number): void {
    const length = this.messages().length;
    if (length === 0) {
      return;
    }

    this.index.update((value) => (value + direction + length) % length);
  }

  private setTimer(run: boolean): void {
    if (this.timer !== null) {
      clearInterval(this.timer);
      this.timer = null;
    }

    if (run) {
      this.timer = setInterval(() => this.next(), ANNOUNCEMENT_ROTATION_MS);
    }
  }
}
