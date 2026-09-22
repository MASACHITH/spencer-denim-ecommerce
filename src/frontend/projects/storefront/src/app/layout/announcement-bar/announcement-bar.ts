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
 * It rotates by itself, but never at the cost of control. WCAG 2.2.2 (Level A) asks for a way to
 * stop anything that moves on its own for more than five seconds, and hover and focus are not
 * that way on a phone, which is this store's main device. So the bar carries a visible
 * pause/resume toggle as well: hovering or tabbing in pauses it too, and
 * `prefers-reduced-motion` switches the rotation off altogether (GLB-12).
 *
 * The message itself is not a live region: it changes on a timer, and a screen reader reading a
 * marketing line aloud every six seconds interrupts whatever the visitor is doing. Only a
 * previous/next press announces, through the visually hidden status below.
 */
@Component({
  selector: 'sd-announcement-bar',
  imports: [Icon],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    '(mouseenter)': 'setHovered(true)',
    '(mouseleave)': 'setHovered(false)',
    '(focusin)': 'setHovered(true)',
    '(focusout)': 'setHovered(false)',
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

        <p class="announce__text" data-testid="announcement">{{ current().text }}</p>

        @if (hasMany()) {
          <button
            class="icon-btn announce__control"
            type="button"
            aria-label="Next announcement"
            (click)="next()"
          >
            <sd-icon name="chevron-right" size="sm" />
          </button>

          @if (canRotate()) {
            <button
              class="icon-btn announce__control"
              type="button"
              aria-label="Pause announcements"
              [attr.aria-pressed]="userPaused()"
              data-testid="announcement-pause"
              (click)="togglePaused()"
            >
              <sd-icon [name]="userPaused() ? 'play' : 'pause'" size="sm" />
            </button>
          }
        }

        <p class="sr-only" role="status" data-testid="announcement-status">
          {{ spokenMessage() }}
        </p>
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
    .announce__control[aria-pressed='true'] {
      background: var(--color-border);
    }
    .announce__text {
      flex: 1 1 auto;
      min-width: 0;
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
        gap: 0;
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
  private readonly hovered = signal(false);
  private readonly pausedByUser = signal(false);
  private readonly spoken = signal('');

  protected readonly hasMany = computed(() => this.messages().length > 1);

  /** Whether the bar rotates on its own at all. Reduced motion means it never does. */
  protected readonly canRotate = computed(() => this.hasMany() && !this.reducedMotion.enabled());

  /** Whether the visitor pressed pause, as opposed to merely hovering over the bar. */
  readonly userPaused = this.pausedByUser.asReadonly();

  /** Text for the visually hidden status: set only by a previous/next press, never by the timer. */
  protected readonly spokenMessage = this.spoken.asReadonly();

  /** Always a message: the index is wrapped against the current list length. */
  protected readonly current = computed(() => {
    const list = this.messages();
    return list[this.index() % list.length] ?? { id: 'empty', text: '' };
  });

  private timer: ReturnType<typeof setInterval> | null = null;

  constructor() {
    effect(() => {
      const shouldRotate =
        this.isBrowser && this.canRotate() && !this.hovered() && !this.pausedByUser();

      untracked(() => this.setTimer(shouldRotate));
    });

    inject(DestroyRef).onDestroy(() => this.setTimer(false));
  }

  /** A visitor asked for the next message, so it is worth announcing. */
  next(): void {
    this.step(1);
    this.speakCurrent();
  }

  previous(): void {
    this.step(-1);
    this.speakCurrent();
  }

  togglePaused(): void {
    this.pausedByUser.update((paused) => !paused);
  }

  /** Exposed for tests: which message is showing. */
  currentIndex(): number {
    const length = this.messages().length;
    return length === 0 ? 0 : this.index() % length;
  }

  protected setHovered(hovered: boolean): void {
    this.hovered.set(hovered);
  }

  private step(direction: number): void {
    const length = this.messages().length;
    if (length === 0) {
      return;
    }

    this.index.update((value) => (value + direction + length) % length);
  }

  private speakCurrent(): void {
    this.spoken.set(this.current().text);
  }

  private setTimer(run: boolean): void {
    if (this.timer !== null) {
      clearInterval(this.timer);
      this.timer = null;
    }

    if (run) {
      this.timer = setInterval(() => this.step(1), ANNOUNCEMENT_ROTATION_MS);
    }
  }
}
