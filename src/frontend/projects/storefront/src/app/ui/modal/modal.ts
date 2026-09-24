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
import { Icon } from '@spencer-denim/shared';

/** How much room the dialog takes: a panel of text, or the whole viewport for a photograph. */
export type ModalSize = 'panel' | 'full';

/** Everything a browser can put in the tab order. Same list the shared drawer traps on. */
const FOCUSABLE_SELECTOR = [
  'a[href]',
  'button:not([disabled]):not([tabindex="-1"])',
  'input:not([disabled])',
  'select:not([disabled])',
  'textarea:not([disabled])',
  'video[controls]',
  '[tabindex]:not([tabindex="-1"])',
].join(',');

/** How many extra frames to spend getting focus into the panel before giving up. */
const FOCUS_ATTEMPTS = 3;

let nextModalId = 0;

/**
 * A centred modal dialog: the size guide (PDP-04) and the gallery's full-screen view (PDP-01).
 *
 * It behaves as a dialog must. Focus moves into the panel when it opens, Tab cycles inside it,
 * focus cannot escape to the page behind, Escape closes it, and focus returns to the control that
 * opened it. The page behind does not scroll.
 *
 * **Why this is not `sd-drawer`.** The drawer is a slide-out that stays mounted and is revealed
 * with `visibility`, which is what forces its retry-on-next-frame dance (lesson L3: a browser
 * will not focus an element that is still hidden in that instant). A dialog has no reason to stay
 * mounted, so this one is created when it opens: by the time focus moves, the panel exists and is
 * painted, and the whole class of bug is designed out. The two therefore do not share an
 * implementation, only the shape of the trap.
 *
 * TODO(QV-01): Quick View wants exactly this. When that task lands, this moves to
 * `@spencer-denim/shared` alongside the drawer and the trap becomes one utility both use. It is
 * deliberately storefront-local today, next to `ui/product-card`, so a shared-library change does
 * not land in three other branches at once.
 */
@Component({
  selector: 'sd-modal',
  imports: [Icon],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class: 'sd-modal',
    '[class.sd-modal--full]': "size() === 'full'",
    '[hidden]': '!open()',
  },
  template: `
    @if (open()) {
      <div class="sd-modal__scrim" aria-hidden="true" (click)="requestClose()"></div>
      <div
        class="sd-modal__panel"
        role="dialog"
        aria-modal="true"
        [attr.id]="panelId()"
        [attr.aria-labelledby]="headingId"
        #panel
      >
        <div class="sd-modal__head">
          <h2 class="sd-modal__title" [id]="headingId">{{ heading() }}</h2>
          <button
            class="icon-btn sd-modal__close"
            type="button"
            [attr.aria-label]="closeLabel()"
            (click)="requestClose()"
          >
            <sd-icon name="close" />
          </button>
        </div>
        <div class="sd-modal__body">
          <ng-content />
        </div>
      </div>
    }
  `,
  styles: `
    :host {
      position: fixed;
      inset: 0;
      z-index: 120;
      display: grid;
      place-items: center;
      padding: var(--space-2);
    }
    :host(.sd-modal--full) {
      padding: 0;
    }
    .sd-modal__scrim {
      position: absolute;
      inset: 0;
      background: var(--color-scrim);
    }
    .sd-modal__panel {
      position: relative;
      display: flex;
      flex-direction: column;
      width: min(100%, 720px);
      max-height: min(100%, 92vh);
      background: var(--color-surface);
      border: 1px solid var(--color-border);
    }
    :host(.sd-modal--full) .sd-modal__panel {
      width: 100%;
      max-width: none;
      height: 100%;
      max-height: none;
      border: 0;
      background: var(--color-bg);
    }
    .sd-modal__head {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: var(--space-2);
      min-height: 64px;
      padding: var(--space-1) var(--space-1) var(--space-1) var(--space-3);
      border-bottom: 1px solid var(--color-border);
    }
    .sd-modal__title {
      font-size: var(--text-h4);
      font-weight: var(--weight-semibold);
    }
    .sd-modal__body {
      flex: 1 1 auto;
      min-height: 0;
      overflow-y: auto;
      overscroll-behavior: contain;
      padding: var(--space-2) var(--space-3) var(--space-3);
      scroll-padding-block: var(--space-2);
    }
    :host(.sd-modal--full) .sd-modal__body {
      display: flex;
      flex-direction: column;
      padding: 0;
    }
  `,
})
export class Modal {
  /** Whether the dialog is showing. The parent owns this state. */
  readonly open = input.required<boolean>();
  /** The visible heading, which also names the dialog for a screen reader. */
  readonly heading = input.required<string>();
  readonly size = input<ModalSize>('panel');
  readonly closeLabel = input('Close');
  /** Optional DOM id, so the control that opens it can carry `aria-controls`. */
  readonly panelId = input<string | null>(null);

  /** Raised when the scrim, the close button or Escape asks for the dialog to be dismissed. */
  readonly closed = output<void>();

  protected readonly headingId = `sd-modal-title-${nextModalId++}`;

  private readonly panel = viewChild<ElementRef<HTMLElement>>('panel');
  private readonly document = inject(DOCUMENT);
  private isActive = false;
  private returnFocusTo: HTMLElement | null = null;

  constructor() {
    // afterRenderEffect only runs in the browser, and only once the panel is on the page, so
    // there is never a hidden element to focus.
    afterRenderEffect(() => {
      const isOpen = this.open();
      untracked(() => (isOpen ? this.activate() : this.deactivate()));
    });

    inject(DestroyRef).onDestroy(() => this.deactivate());
  }

  protected requestClose(): void {
    this.closed.emit();
  }

  private activate(): void {
    if (this.isActive || !this.panel()) {
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

    // Focus goes back to whatever opened the dialog. `isConnected` because the control may have
    // gone with the dialog — a colour swatch the shopper changed, for instance.
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
    const panel = this.panel()?.nativeElement;
    if (panel && event.target instanceof Node && !panel.contains(event.target)) {
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
   * Moves focus to the first control in the panel, retrying for a few frames.
   *
   * The panel is created with the dialog, so the first attempt normally lands. The retry is kept
   * because a dialog that fails to take focus strands a keyboard user behind a scrim, and one
   * `focus()` call is a single point of failure for that.
   */
  private focusFirst(attempt = 0): void {
    const panel = this.panel()?.nativeElement;
    const target = this.focusable()[0];
    if (!panel || !target) {
      return;
    }

    target.focus();

    if (panel.contains(this.document.activeElement) || !this.isActive || attempt >= FOCUS_ATTEMPTS) {
      return;
    }

    this.retryFocus(attempt + 1);
  }

  private retryFocus(attempt: number): void {
    const retry = () => {
      const panel = this.panel()?.nativeElement;
      if (this.isActive && panel && !panel.contains(this.document.activeElement)) {
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
    const panel = this.panel()?.nativeElement;
    if (!panel) {
      return [];
    }

    return Array.from(panel.querySelectorAll<HTMLElement>(FOCUSABLE_SELECTOR)).filter(
      (element) => element.closest('[hidden]') === null,
    );
  }
}
