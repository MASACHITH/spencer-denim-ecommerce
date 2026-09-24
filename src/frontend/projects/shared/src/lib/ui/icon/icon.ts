import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

/**
 * The icon set of the style tile (design/style-tile/index.html §13): one 24px grid,
 * 1.5px outline stroke, so every icon reads as one family.
 */
export type IconName =
  | 'search'
  | 'user'
  | 'heart'
  | 'bag'
  | 'menu'
  | 'close'
  | 'chevron-down'
  | 'chevron-left'
  | 'chevron-right'
  | 'filter'
  | 'star'
  | 'pause'
  | 'play'
  | 'whatsapp'
  | 'truck'
  | 'mail'
  | 'phone'
  | 'pin'
  | 'facebook'
  | 'instagram'
  | 'tiktok';

export type IconSize = 'xs' | 'sm' | 'md';

/**
 * Decorative inline SVG icon. It is always `aria-hidden`: every icon in this code base is
 * paired with a text label or an `aria-label` on its control, never used as the label itself.
 *
 * The paths are inlined rather than referenced from a sprite so the icon survives server-side
 * rendering and never depends on `<base href>` resolution of `<use href="#id">`.
 */
@Component({
  selector: 'sd-icon',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '[class]': 'hostClasses()' },
  template: `
    <svg viewBox="0 0 24 24" aria-hidden="true" focusable="false">
      @switch (name()) {
        @case ('search') {
          <circle cx="11" cy="11" r="6.5" />
          <path d="M16 16l4.5 4.5" />
        }
        @case ('user') {
          <circle cx="12" cy="8" r="4" />
          <path d="M4.5 20.5c1.2-3.8 4-5.5 7.5-5.5s6.3 1.7 7.5 5.5" />
        }
        @case ('heart') {
          <path d="M12 20s-7.5-4.6-7.5-10.1A4.4 4.4 0 0 1 12 7.3a4.4 4.4 0 0 1 7.5 2.6C19.5 15.4 12 20 12 20z" />
        }
        @case ('bag') {
          <path d="M5 8h14l-1 12.5H6L5 8z" />
          <path d="M9 10V7a3 3 0 0 1 6 0v3" />
        }
        @case ('menu') {
          <path d="M3.5 7h17M3.5 12h17M3.5 17h17" />
        }
        @case ('close') {
          <path d="M6 6l12 12M18 6L6 18" />
        }
        @case ('chevron-down') {
          <path d="M6 9l6 6 6-6" />
        }
        @case ('chevron-left') {
          <path d="M15 6l-6 6 6 6" />
        }
        @case ('chevron-right') {
          <path d="M9 6l6 6-6 6" />
        }
        @case ('filter') {
          <path d="M3.5 6.5h17M6.5 12h11M10 17.5h4" />
        }
        @case ('star') {
          <path d="M12 3.8l2.5 5.1 5.6.8-4 3.9 1 5.6-5.1-2.7-5.1 2.7 1-5.6-4-3.9 5.6-.8z" />
        }
        @case ('pause') {
          <path d="M9.5 5.5v13M14.5 5.5v13" />
        }
        @case ('play') {
          <path d="M7.5 5.2l11 6.8-11 6.8z" />
        }
        @case ('whatsapp') {
          <path d="M20.5 12a8.5 8.5 0 0 1-12.6 7.44L3.5 20.5l1.1-4.2A8.5 8.5 0 1 1 20.5 12z" />
          <path
            d="M9.2 8.2c-.5.4-.7 1-.5 1.8.7 2.3 2.4 4 4.7 4.8.8.2 1.4 0 1.8-.5l.4-.6-1.9-1.1-.8.7c-1-.4-1.8-1.2-2.2-2.2l.7-.8-1.1-1.9z"
          />
        }
        @case ('truck') {
          <path d="M2.5 6.5h11v10h-11zM13.5 10h4l3 3v3.5h-7" />
          <circle cx="7" cy="17.5" r="1.8" />
          <circle cx="17" cy="17.5" r="1.8" />
        }
        @case ('mail') {
          <path d="M3 6h18v12H3z" />
          <path d="M3.5 7l8.5 6 8.5-6" />
        }
        @case ('phone') {
          <path
            d="M6.5 3.5h3l1.2 3.6-1.9 1.3a11 11 0 0 0 5.8 5.8l1.3-1.9 3.6 1.2v3a2 2 0 0 1-2.2 2A16.5 16.5 0 0 1 4.5 5.7a2 2 0 0 1 2-2.2z"
          />
        }
        @case ('pin') {
          <path d="M12 21s6.5-6 6.5-11a6.5 6.5 0 1 0-13 0C5.5 15 12 21 12 21z" />
          <circle cx="12" cy="10" r="2.5" />
        }
        @case ('facebook') {
          <rect x="3.5" y="3.5" width="17" height="17" rx="3" />
          <path d="M15 8.2h-1.3a1.9 1.9 0 0 0-1.9 1.9v10.4" />
          <path d="M9.8 12.6h5" />
        }
        @case ('instagram') {
          <rect x="3.5" y="3.5" width="17" height="17" rx="4.5" />
          <circle cx="12" cy="12" r="4" />
          <path d="M17 7v.01" />
        }
        @case ('tiktok') {
          <path d="M14 3.5v11.1a3.4 3.4 0 1 1-2.8-3.35" />
          <path d="M14 3.5a5 5 0 0 0 5 5" />
        }
      }
    </svg>
  `,
  styles: `
    :host {
      display: inline-flex;
      flex: none;
    }
    svg {
      width: 24px;
      height: 24px;
      fill: none;
      stroke: currentColor;
      stroke-width: 1.5;
      stroke-linecap: round;
      stroke-linejoin: round;
    }
    :host(.sd-icon--sm) svg {
      width: 20px;
      height: 20px;
    }
    :host(.sd-icon--xs) svg {
      width: 16px;
      height: 16px;
    }
    /* A rating star and a saved wishlist heart read as "on" only when they are solid. */
    :host(.sd-icon--filled) svg {
      fill: currentColor;
    }
  `,
})
export class Icon {
  readonly name = input.required<IconName>();
  readonly size = input<IconSize>('md');
  /** Paints the glyph solid: a selected wishlist heart, a filled rating star. */
  readonly filled = input(false);

  protected readonly hostClasses = computed(
    () => `sd-icon sd-icon--${this.size()}${this.filled() ? ' sd-icon--filled' : ''}`,
  );
}
