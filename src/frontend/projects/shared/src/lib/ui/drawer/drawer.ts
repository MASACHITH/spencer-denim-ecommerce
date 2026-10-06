import { DOCUMENT } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  afterRenderEffect,
  inject,
  input,
  output,
  untracked,
  viewChild,
} from '@angular/core';

import { Icon } from '../icon/icon';

/** Which edge the panel slides in from. `start` = left, `end` = right in a left-to-right page. */
export type DrawerSide = 'start' | 'end';

/** Everything a browser can put in the tab order. */
const FOCUSABLE_SELECTOR = [
  'a[href]',
  'button:not([disabled]):not([tabindex="-1"])',
  'input:not([disabled])',
  'select:not([disabled])',
  'textarea:not([disabled])',
  '[tabindex]:not([tabindex="-1"])',
].join(',');

/** How many extra frames to spend trying to put focus in the panel before giving up. */
const FOCUS_ATTEMPTS = 3;

let nextDrawerId = 0;

/**
 * A modal slide-out panel: the mobile navigation now, the mini bag later (GLB-05, BAG-04).
 *
 * It is a dialog, so it behaves like one: focus moves into the panel when it opens, Tab cycles
 * inside it, Escape closes it, and focus returns to whatever opened it. The panel stays in the
 * DOM when closed (hidden with `visibility`, which takes it out of the tab order and the
 * accessibility tree) so opening it never waits for a render.
 *
 * The component reports closing through `closed`; the parent owns the open state.
 */
@Component({
  selector: 'sd-drawer',
  imports: [Icon],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class: 'sd-drawer',
    '[class.sd-drawer--end]': "side() === 'end'",
    '[class.is-open]': 'open()',
  },
  template: `
    <div
      class="sd-drawer__scrim"
      tabindex="-1"
      aria-hidden="true"
      (click)="requestClose()"
      (keydown)="onScrimKeydown($event)"
    ></div>
    <div
      class="sd-drawer__panel"
      role="dialog"
      aria-modal="true"
      [attr.id]="panelId()"
      [attr.aria-labelledby]="headingId"
      #panel
    >
      <div class="sd-drawer__head">
        <!-- A paragraph, not a heading, and the reason is #76. The drawer's markup sits ahead of
             the page's own h1 in the DOM, so an h2 here meant every storefront page announced
             "Menu" before it said what the page was, to anyone navigating by headings. The dialog
             already takes its name from this text through aria-labelledby above, and a name is what
             this text is for, so leaving the heading outline costs nothing and fixes the order. -->
        <p class="sd-drawer__title" [id]="headingId">{{ heading() }}</p>
        <button
          class="icon-btn"
          type="button"
          [attr.aria-label]="closeLabel()"
          (click)="requestClose()"
        >
          <sd-icon name="close" />
        </button>
      </div>
      <div class="sd-drawer__body">
        <ng-content />
      </div>
    </div>
  `,
  styles: `
    :host {
      position: fixed;
      inset: 0;
      z-index: 100;
      visibility: hidden;
      transition: visibility 0s linear var(--duration);
    }
    /* Opening is instant so focus can move in straight away, including when reduced motion
       shortens every transition. */
    :host(.is-open) {
      visibility: visible;
      transition: none;
    }
    .sd-drawer__scrim {
      position: absolute;
      inset: 0;
      background: var(--color-scrim);
      opacity: 0;
      transition: opacity var(--duration) var(--ease);
    }
    :host(.is-open) .sd-drawer__scrim {
      opacity: 1;
    }
    .sd-drawer__panel {
      position: absolute;
      inset-block: 0;
      inset-inline-start: 0;
      display: flex;
      flex-direction: column;
      width: min(88vw, 380px);
      max-width: 100%;
      background: var(--color-surface);
      transform: translateX(-100%);
      transition: transform var(--duration) var(--ease);
    }
    :host(.sd-drawer--end) .sd-drawer__panel {
      inset-inline-start: auto;
      inset-inline-end: 0;
      width: min(92vw, 420px);
      transform: translateX(100%);
    }
    :host(.is-open) .sd-drawer__panel {
      transform: none;
    }
    .sd-drawer__head {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: var(--space-2);
      min-height: 64px;
      padding: var(--space-1) var(--space-1) var(--space-1) var(--space-3);
      border-bottom: 1px solid var(--color-border);
    }
    .sd-drawer__title {
      font-size: var(--text-h4);
      font-weight: var(--weight-semibold);
      /* The reset gives h1-h4 a tighter line-height than body text; this keeps the look it had as
         an h2 now that it is a paragraph. */
      line-height: 1.2;
    }
    /* scroll-padding keeps focus rings clear of the scroll edge when focus scrolls an item in */
    .sd-drawer__body {
      flex: 1 1 auto;
      overflow-y: auto;
      overscroll-behavior: contain;
      padding: var(--space-1) var(--space-3) var(--space-3);
      scroll-padding-block: var(--space-2);
    }
  `,
})
export class Drawer {
  /** Whether the drawer is showing. The parent owns this state. */
  readonly open = input.required<boolean>();
  /** Visible dialog heading, which also names the dialog for screen readers. */
  readonly heading = input.required<string>();
  readonly side = input<DrawerSide>('start');
  readonly closeLabel = input('Close');
  /** Optional DOM id for the dialog, so the control that opens it can use `aria-controls`. */
  readonly panelId = input<string | null>(null);

  /** Raised when the scrim, the close button or Escape asks for the drawer to be dismissed. */
  readonly closed = output<void>();

  /**
   * Raised when the drawer closed and **could not give focus back**, so the host can decide where it
   * should go instead.
   *
   * The drawer records whatever had focus when it opened and restores it on the way out. That element
   * can be gone by then — the control that opened the drawer may have been removed by what the drawer
   * was confirming. The drawer declines to focus a detached node, correctly, and the browser's answer
   * to "focus nothing" is `<body>`: no tab position, nothing announced, a keyboard shopper stranded.
   *
   * **Found in W2-08.** Moving a wishlist row to the bag deletes that row's Move to bag button and the
   * mini bag opens over the gap; dismissing it left focus on `<body>`. Adding from a listing card was
   * unaffected, because that opener survives — which is how the cause was identified rather than
   * guessed at.
   *
   * **Why an output and not a guess inside the drawer.** Only the host knows what a sensible
   * destination is: the shell's bag anchor, a page heading, the row that took the removed one's place.
   * A drawer that picked for itself would be wrong on some page.
   *
   * **Why a host cannot work this out for itself.** It has no moment to look: the drawer restores focus
   * in a render phase of its own, and a host that checked on the `closed` output — or in a later render
   * phase, which was tried and measured — runs while the drawing is still focused and sees nothing
   * wrong. This fires at the one instant the answer is known.
   *
   * It is not raised when the drawer is destroyed with its host: there is no page left to put focus on.
   */
  readonly focusLost = output<void>();

  protected readonly headingId = `sd-drawer-title-${nextDrawerId++}`;

  private readonly panel = viewChild.required<ElementRef<HTMLElement>>('panel');
  private readonly document = inject(DOCUMENT);
  private isActive = false;
  private returnFocusTo: HTMLElement | null = null;

  constructor() {
    // afterRenderEffect only ever runs in the browser, and only once the `is-open` class is on
    // the host, so the panel is really visible by the time focus moves into it.
    afterRenderEffect(() => {
      const isOpen = this.open();
      untracked(() => (isOpen ? this.activate() : this.deactivate({ restoring: true })));
    });

    // `restoring: false`: the host is going away, so there is nothing to hand focus back to and
    // nothing listening for the fact.
    inject(DestroyRef).onDestroy(() => this.deactivate({ restoring: false }));
  }

  protected requestClose(): void {
    this.closed.emit();
  }

  /** The scrim is a plain surface; Escape is handled globally while the drawer is open. */
  protected onScrimKeydown(event: KeyboardEvent): void {
    if (event.key === 'Enter' || event.key === ' ') {
      event.preventDefault();
      this.requestClose();
    }
  }

  private activate(): void {
    if (this.isActive) {
      return;
    }

    this.isActive = true;
    const active = this.document.activeElement;

    // **`<body>` is not a return target, it is the absence of one** — and treating it as one is what
    // made `focusLost` unreachable in the case it was written for (W2-08 round 3). The sequence: a
    // control opens a dialog, the dialog's work removes that control, the dialog declines to restore
    // focus to a detached node, and focus is therefore on `<body>` by the time *this* drawer opens.
    // Recording that and then "restoring" it is a no-op dressed up as success: `body.isConnected` is
    // true and `body.focus()` changes nothing, so the drawer reported a restore it had not made and
    // the host was never told. Null here is the truth, and `deactivate` turns it into `focusLost`.
    this.returnFocusTo =
      active instanceof HTMLElement && active !== this.document.body ? active : null;

    this.document.body.classList.add('sd-no-scroll');
    this.document.addEventListener('keydown', this.onKeydown, true);
    this.document.addEventListener('focusin', this.onFocusIn, true);
    this.focusFirst();
  }

  private deactivate(options: { restoring: boolean } = { restoring: true }): void {
    if (!this.isActive) {
      return;
    }

    this.isActive = false;
    this.document.body.classList.remove('sd-no-scroll');
    this.document.removeEventListener('keydown', this.onKeydown, true);
    this.document.removeEventListener('focusin', this.onFocusIn, true);

    const target = this.returnFocusTo;
    this.returnFocusTo = null;

    if (target?.isConnected) {
      target.focus();
      return;
    }

    // Nothing to return focus to: the opener has been removed while the drawer was open, or there was
    // never one. See `focusLost` — the host decides, because only the host knows where.
    if (options.restoring) {
      this.focusLost.emit();
    }
  }

  private readonly onKeydown = (event: KeyboardEvent): void => {
    if (event.key === 'Escape') {
      event.preventDefault();
      this.requestClose();
      return;
    }

    if (event.key === 'Tab') {
      this.trapTab(event);
    }
  };

  /** Focus can never rest outside an open dialog. */
  private readonly onFocusIn = (event: FocusEvent): void => {
    const panel = this.panel().nativeElement;
    if (event.target instanceof Node && !panel.contains(event.target)) {
      this.focusFirst();
    }
  };

  private trapTab(event: KeyboardEvent): void {
    const items = this.focusable();
    if (items.length === 0) {
      event.preventDefault();
      return;
    }

    const index = items.indexOf(this.document.activeElement as HTMLElement);
    if (event.shiftKey && index <= 0) {
      event.preventDefault();
      items[items.length - 1].focus();
    } else if (!event.shiftKey && (index === -1 || index === items.length - 1)) {
      event.preventDefault();
      items[0].focus();
    }
  }

  /**
   * Moves focus to the first thing in the panel, and keeps trying for a few frames.
   *
   * Lesson L3: the panel is revealed by a class on the host, and a browser refuses to focus an
   * element that is still `visibility: hidden` at that instant. CSS makes the reveal immediate,
   * but a single `.focus()` call is one point of failure for a dialog that must trap focus, so
   * a miss is retried on the next frames until focus really is inside the panel.
   */
  private focusFirst(attempt = 0): void {
    const target = this.focusable()[0];
    if (!target) {
      return;
    }

    target.focus();

    const panel = this.panel().nativeElement;
    const landed = panel.contains(this.document.activeElement);
    if (landed || !this.isActive || attempt >= FOCUS_ATTEMPTS) {
      return;
    }

    this.retryFocus(attempt + 1);
  }

  private retryFocus(attempt: number): void {
    const retry = () => {
      if (this.isActive && !this.panel().nativeElement.contains(this.document.activeElement)) {
        this.focusFirst(attempt);
      }
    };

    if (typeof requestAnimationFrame === 'function') {
      requestAnimationFrame(retry);
    } else {
      setTimeout(retry, 16);
    }
  }

  private focusable(): HTMLElement[] {
    return Array.from(
      this.panel().nativeElement.querySelectorAll<HTMLElement>(FOCUSABLE_SELECTOR),
    ).filter((element) => element.closest('[hidden]') === null);
  }
}
