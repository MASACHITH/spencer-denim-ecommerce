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
        <h2 class="sd-drawer__title" [id]="headingId">{{ heading() }}</h2>
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
      untracked(() => (isOpen ? this.activate() : this.deactivate()));
    });

    inject(DestroyRef).onDestroy(() => this.deactivate());
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
    this.returnFocusTo = active instanceof HTMLElement ? active : null;

    this.document.body.classList.add('sd-no-scroll');
    this.document.addEventListener('keydown', this.onKeydown, true);
    this.document.addEventListener('focusin', this.onFocusIn, true);
    this.focusFirst();
  }

  private deactivate(): void {
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

  private focusFirst(): void {
    this.focusable()[0]?.focus();
  }

  private focusable(): HTMLElement[] {
    return Array.from(
      this.panel().nativeElement.querySelectorAll<HTMLElement>(FOCUSABLE_SELECTOR),
    ).filter((element) => element.closest('[hidden]') === null);
  }
}
