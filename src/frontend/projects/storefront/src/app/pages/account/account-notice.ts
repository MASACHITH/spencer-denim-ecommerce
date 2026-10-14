import { ChangeDetectionStrategy, Component, ElementRef, input, viewChild } from '@angular/core';

/**
 * The two live regions every account page renders, and the one rule they exist to keep.
 *
 * **A message written into a node the same update created is invisible to a screen reader.** That has
 * shipped twice on this project; the second time, a blind customer pressed Cancel and heard nothing. An
 * `aria-live` region is announced because assistive technology was already watching it — so a region that
 * arrives with its text has nothing watching it yet, and `@if (error()) { <p role="alert">…` is the exact
 * shape of the defect. `checkout-page.ts` still has it on its page-level error.
 *
 * So both regions here are **in the markup from the server's first render, for every visitor, always**:
 * no `@if`, no `[hidden]`, no `display: none`. Only the text changes, and `.is-on` adds the border and
 * the colour. An empty region is a block holding an empty text node, which has no height, so an account
 * page with nothing to say looks exactly as it would without them.
 *
 * **This matters most on the three pages the brief singles out** — sign in, register and reset — because
 * all three *re-render on submit*: the form is still there, the values are still there, and the only new
 * thing on the page is the sentence. If that sentence arrived in a new node, the one update a shopper
 * most needs to hear would be the one they never hear.
 *
 * ## Why two regions and not one
 *
 * `role="alert"` is an assertive live region: it interrupts. `role="status"` is polite: it waits. A
 * refused sign-in is an interruption — the shopper cannot carry on until they act — and "We have queued a
 * link to your email address" is not. One region switching roles would be worse than either: `role` is
 * read when the region is created, so changing it afterwards is not reliably honoured.
 *
 * ## Both are focusable, and only the page decides when
 *
 * `tabindex="-1"` makes {@link focusAlert} possible without putting either region in the tab order. The
 * pages call it after a refused submit, which is WCAG 2.2 4.1.3 and the defect checkout recorded: pressing
 * Place order with nothing filled in drew three red field messages, announced nothing, and left focus on
 * `<body>`, so a keyboard user had no way to learn the form had been refused at all — and axe reported
 * zero violations.
 *
 * The focus call itself must happen in `afterNextRender`, **never `queueMicrotask`**: a signal write
 * schedules change detection as a *task*, so a microtask runs before the node exists. That passes Vitest,
 * where the fixture's `detectChanges` has already run, and fails in a real browser. Measured twice on this
 * project. This component only offers the handle; the pages own the timing, and each one says so.
 */
@Component({
  selector: 'sd-account-notice',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <p
      class="acc-notice acc-notice--alert"
      role="alert"
      tabindex="-1"
      [class.is-on]="alert() !== null"
      [attr.data-testid]="testId() + '-alert'"
      #alertRegion
    >{{ alert() ?? '' }}</p>

    <p
      class="acc-notice acc-notice--ok"
      role="status"
      tabindex="-1"
      [class.is-on]="status() !== null"
      [attr.data-testid]="testId() + '-status'"
      #statusRegion
    >{{ status() ?? '' }}</p>
  `,
  styles: `
    :host {
      display: block;
    }
  `,
})
export class AccountNotice {
  /**
   * What went wrong, or null. Announced assertively.
   *
   * Null rather than `''` for "nothing", because the two have to be told apart: `''` is also what the
   * region holds *after* a message has been cleared, and `.is-on` has to be off for both.
   */
  readonly alert = input<string | null>(null);

  /** What happened, or null. Announced politely. */
  readonly status = input<string | null>(null);

  /**
   * The prefix for both regions' `data-testid`.
   *
   * It is a prefix and not a fixed name because the shell already renders five live regions before a page
   * adds its own — the announcement bar, the header search, the mini bag twice and the footer newsletter
   * — so `role="status"` is not a unique address on any page of this site. `wishlist-page.ts` records the
   * same count for the same reason. Each account page passes its own, so a test names the region it
   * means.
   */
  readonly testId = input.required<string>();

  private readonly alertRegion = viewChild.required<ElementRef<HTMLElement>>('alertRegion');
  private readonly statusRegion = viewChild.required<ElementRef<HTMLElement>>('statusRegion');

  /** Puts focus on the refusal. The caller is responsible for calling it after a render. */
  focusAlert(): void {
    this.alertRegion().nativeElement.focus();
  }

  /**
   * Puts focus on the confirmation.
   *
   * Used by the pages whose successful submit leaves nothing else worth focusing — registration, forgot
   * password, reset password — where the sentence the server sent *is* the outcome and the next thing the
   * shopper does is read it.
   */
  focusStatus(): void {
    this.statusRegion().nativeElement.focus();
  }
}
