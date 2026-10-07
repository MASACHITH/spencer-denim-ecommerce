import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  inject,
  signal,
  viewChild,
  viewChildren,
} from '@angular/core';
import { FormsModule } from '@angular/forms';

import { CatalogFailure, toCatalogFailure } from '../../core/catalog/admin-catalog-api';
import { BankTransferApi, toBankTransferRefusal } from '../../core/orders/bank-transfer-api';
import {
  BANK_TRANSFER_REFUSALS,
  BankTransferDecision,
  BankTransferQueueRow,
  BankTransferRefusal,
} from '../../core/orders/bank-transfer.model';

/** Doc 11 §6.1 has staff working this queue twice a day, so a page holds a sensible day's worth. */
const PAGE_SIZE = 20;

/**
 * "Orders → Bank transfers awaiting approval" (PAY-01, ADM-09, doc 11 P7 step 4).
 *
 * **This is the screen the shop earns through.** Every bank-transfer order sits here until a staff member
 * matches it against the bank statement; nothing else in the system can turn a transfer into a paid order.
 *
 * **Deliberately narrow.** It is the queue doc 11 P7 needs and nothing more: no order list, no order
 * detail, no fulfilment, no cancel, no refund. The full admin orders screen is W3-04, and the refund paths
 * — including doc 11 §7 G-13's "money received on an order that was never paid" — are its and ADM-09's.
 *
 * **Three things no control on this page does.**
 *
 * 1. **Nothing is ever `disabled`.** Approving a row removes it from the queue, so the button the staff
 *    member pressed is about to stop existing; disabling it first would throw their focus to `<body>`
 *    (issue #109, six instances) and then destroying it would do the same again. Every action carries
 *    `aria-disabled` and the handler refuses while a write is in flight.
 * 2. **The row that was acted on is not torn out from under the focus.** A decided row stays on screen
 *    with its outcome, and the next read replaces the list only when the staff member asks for it. The
 *    `aria-disabled` rule is necessary and not sufficient — destroying the focused node does the same harm
 *    as disabling it.
 * 3. **An identical announcement re-fires.** `role="status"` announces a *change*, so approving two
 *    transfers in a row would otherwise be read out once; the second gets a trailing space, which is
 *    invisible and is what makes it a change (ADM-22, and the same fix as the wishlist store's).
 *
 * The API is the security boundary (doc 03 §3a). A user without `Orders.View` reaches this page — the
 * route only asks for a session, like the rest of the shell — and is shown the refusal rather than an
 * empty screen that cannot explain itself.
 */
@Component({
  selector: 'sda-bank-transfers-page',
  imports: [FormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="bt">
      <div class="bt__head">
        <h1 class="bt__title">Bank transfers awaiting approval</h1>
        <button
          class="adm-btn"
          type="button"
          [attr.aria-disabled]="busy() ? 'true' : null"
          (click)="reload()"
          data-testid="refresh"
          #refreshButton
        >
          Refresh
        </button>
      </div>

      <p class="sr-only" role="status" data-testid="announcement">{{ announcement() }}</p>

      <p class="adm-panel__hint">
        Check the shop's bank statement for a credit matching the amount <strong>and</strong> the
        reference, then approve. Approve only when the full amount has arrived — there is no part-payment
        handling. A rejected transfer leaves the order open until its deadline, so the customer can
        transfer again.
      </p>

      @if (failure(); as problem) {
        <div class="adm-state adm-state--error" role="alert" data-testid="page-error">
          <p>{{ problem.message }}</p>
          @if (problem.kind !== 'forbidden') {
            <button class="adm-btn" type="button" (click)="reload()">Try again</button>
          }
        </div>
      }

      @if (loading()) {
        <p class="adm-state">Loading the queue…</p>
      } @else if (rows().length === 0 && decided().size === 0) {
        <p class="adm-state" data-testid="queue-empty">
          Nothing is waiting for approval. Every bank transfer has been dealt with.
        </p>
      } @else {
        <p class="bt__count" data-testid="queue-count">
          {{ total() }} waiting · showing {{ rows().length }}
        </p>

        <ul class="bt__list">
          @for (row of rows(); track row.orderPublicId) {
            <li class="bt__row" [attr.data-testid]="'row-' + row.orderNumber">
              <div class="bt__row-head">
                <h2 class="bt__order">{{ row.orderNumber }}</h2>
                <span
                  class="adm-pill"
                  [class.bt__pill--late]="row.secondsUntilExpiry <= 0"
                  [class.bt__pill--soon]="row.secondsUntilExpiry > 0 && row.secondsUntilExpiry < 21600"
                  [attr.data-testid]="'time-left-' + row.orderNumber"
                >
                  {{ timeLeft(row) }}
                </span>
              </div>

              <dl class="bt__facts">
                <div><dt>Customer</dt><dd>{{ row.customerName }}</dd></div>
                <div><dt>Email</dt><dd>{{ row.customerEmail }}</dd></div>
                <div>
                  <dt>Amount</dt>
                  <dd [attr.data-testid]="'amount-' + row.orderNumber">{{ money(row.amountLkr) }}</dd>
                </div>
                <div>
                  <dt>Their reference</dt>
                  <dd [attr.data-testid]="'reference-' + row.orderNumber">
                    {{ row.payerReference ?? 'None given' }}
                  </dd>
                </div>
                <div>
                  <dt>Slip</dt>
                  <dd>
                    @if (row.slipAssetPublicId) {
                      <!-- A download, not a preview. A slip is a stranger's bank document and a PDF is a
                           programmable container; the API serves it as an attachment with nosniff, so
                           nothing renders it inside the admin's own session. -->
                      <a
                        class="adm-link"
                        [href]="slipUrl(row)"
                        target="_blank"
                        rel="noopener"
                        [attr.data-testid]="'slip-' + row.orderNumber"
                      >
                        Download the slip
                      </a>
                    } @else {
                      None uploaded
                    }
                  </dd>
                </div>
              </dl>

              @if (outcome(row); as done) {
                <!-- **The row stays, and that is necessary and not sufficient.**
                     Keeping the row on screen was the first half of issue #109's second rule, and a
                     browser test proved the first version of this still dropped focus to <body>: the
                     action bar the staff member had just pressed is replaced by this message, so the
                     focused node is destroyed whether or not its row survives. The comment that used to
                     sit here said the row keeping its place solved it, which was wrong — and a wrong
                     comment is worse than none, because it stops the next reader checking.
                     So the message itself takes the focus. tabindex="-1" makes it focusable without
                     putting it in the tab order, and it is the honest place to land: it is about what just
                     happened, and it is where the control used to be.
                     (No backticks anywhere in this template: it is a template literal and one would end
                     it. Two of this file's comments were written with them before the build said so.) -->
                <p
                  class="adm-state bt__outcome"
                  role="status"
                  tabindex="-1"
                  [attr.data-testid]="'done-' + row.orderNumber"
                  #outcomeMessage
                >
                  {{ done }}
                </p>
              } @else {
                @if (refusal(row); as problem) {
                  <div
                    class="adm-state adm-state--warning"
                    role="alert"
                    [attr.data-testid]="'refused-' + row.orderNumber"
                  >
                    <p>{{ problem.message }}</p>
                    @if (problem.shortLines; as short) {
                      <p>
                        Items no longer available:
                        @for (line of shortList(short); track line.variantId) {
                          <span>variant {{ line.variantId }} ({{ line.available }} left)</span>
                        }
                      </p>
                    }
                  </div>
                }

                <label class="adm-field adm-field--wide">
                  <span class="adm-label" [attr.for]="'note-' + row.orderNumber">
                    Note (required when rejecting)
                  </span>
                  <input
                    class="adm-input"
                    type="text"
                    maxlength="500"
                    [name]="'note-' + row.orderNumber"
                    [ngModel]="note(row)"
                    (ngModelChange)="setNote(row, $event)"
                    [attr.data-testid]="'note-' + row.orderNumber"
                  />
                </label>

                <div class="adm-actions">
                  @if (canAcceptLate(row)) {
                    <button
                      class="adm-btn--primary"
                      type="button"
                      [attr.aria-disabled]="busy() ? 'true' : null"
                      (click)="decide(row, 'accept-late')"
                      [attr.data-testid]="'accept-late-' + row.orderNumber"
                    >
                      Accept late payment
                    </button>
                  } @else {
                    <button
                      class="adm-btn--primary"
                      type="button"
                      [attr.aria-disabled]="busy() ? 'true' : null"
                      (click)="decide(row, 'approve')"
                      [attr.data-testid]="'approve-' + row.orderNumber"
                    >
                      Approve
                    </button>
                  }

                  <button
                    class="adm-btn--danger"
                    type="button"
                    [attr.aria-disabled]="busy() ? 'true' : null"
                    (click)="decide(row, 'reject')"
                    [attr.data-testid]="'reject-' + row.orderNumber"
                  >
                    Reject
                  </button>
                </div>
              }
            </li>
          }
        </ul>

        @if (total() > rows().length || page() > 1) {
          <div class="adm-actions" data-testid="paging">
            <button
              class="adm-btn"
              type="button"
              [attr.aria-disabled]="page() <= 1 || busy() ? 'true' : null"
              (click)="goTo(page() - 1)"
              data-testid="previous-page"
            >
              Previous
            </button>
            <span class="bt__count">Page {{ page() }}</span>
            <button
              class="adm-btn"
              type="button"
              [attr.aria-disabled]="!hasMore() || busy() ? 'true' : null"
              (click)="goTo(page() + 1)"
              data-testid="next-page"
            >
              Next
            </button>
          </div>
        }
      }
    </section>
  `,
  styles: `
    .bt {
      display: grid;
      gap: var(--space-3, 1.5rem);
    }
    .bt__head {
      display: flex;
      flex-wrap: wrap;
      gap: var(--space-2, 1rem);
      align-items: center;
      justify-content: space-between;
    }
    .bt__title {
      font-size: 1.5rem;
      font-weight: 500;
    }
    .bt__count {
      color: var(--adm-muted, #5b5b5b);
      font-size: 0.875rem;
    }
    .bt__list {
      display: grid;
      gap: var(--space-2, 1rem);
      list-style: none;
      margin: 0;
      padding: 0;
    }
    .bt__row {
      display: grid;
      gap: var(--space-2, 1rem);
      padding: var(--space-2, 1rem);
      border: 1px solid var(--adm-border, #d6d6d6);
    }
    .bt__row-head {
      display: flex;
      flex-wrap: wrap;
      gap: 0.75rem;
      align-items: center;
      justify-content: space-between;
    }
    .bt__order {
      font-size: 1.125rem;
      font-weight: 500;
      font-variant-numeric: tabular-nums;
    }
    .bt__pill--soon {
      background-color: #fff4d6;
    }
    .bt__pill--late {
      background-color: #ffe0e0;
    }
    /* A grid rather than a table: a row is five short facts and an action bar, and at 320 px a table of
       five columns either scrolls sideways or squeezes the amount into two characters. The project's
       non-negotiable is no horizontal scroll at 320 px. */
    .bt__facts {
      display: grid;
      gap: 0.75rem;
      /* min(100%, 14rem) rather than a bare 14rem: the bare form is a hard floor, so a track wider than
         its container scrolls the page sideways. This one happens to fit at 320 px and the order page's
         16rem version did not — same pattern, measured 74 px of overflow there. Written the safe way in
         both places rather than left to the arithmetic. */
      grid-template-columns: repeat(auto-fit, minmax(min(100%, 14rem), 1fr));
      margin: 0;
    }
    .bt__facts dt {
      color: var(--adm-muted, #5b5b5b);
      font-size: 0.8125rem;
    }
    .bt__facts dd {
      margin: 0;
      overflow-wrap: anywhere;
    }
    /* The actions say aria-disabled rather than disabled, so pressing one does not throw the admin's
       focus to the document body (issue #109). That leaves the unavailable look to be drawn here.
       Opacity only, and no pointer-events: none — a control that cannot be hovered cannot show a cursor,
       and the press is already a no-op.
       (No backticks in this block: it is a template literal, and one would end it.) */
    .bt [aria-disabled='true'] {
      opacity: 0.6;
      cursor: progress;
    }
    /* The outcome message is focusable so a decision can hand it the focus, and it needs a ring: a focus
       that lands somewhere invisible leaves a keyboard user's next Tab starting from a place they cannot
       see. It is :focus and NOT :focus-visible, which is the whole point — base.css's global rule is
       :focus-visible, and Chrome does not grant that to an element focused by script (the lesson W3-02's
       round 2 recorded about a button and a programmatic focus), so the global rule would never fire
       here. A class rather than the data-testid, which is a test hook and not a styling one.
       (No backticks in this block: it is a template literal, and one would end it. That is the mistake
       this comment was written with on the first attempt.) */
    .bt__outcome:focus {
      outline: var(--focus-width) solid var(--color-primary);
      outline-offset: 2px;
    }
  `,
})
export class BankTransfersPage {
  private readonly api = inject(BankTransferApi);
  private readonly injector = inject(Injector);
  private readonly refreshButton = viewChild<ElementRef<HTMLButtonElement>>('refreshButton');

  /**
   * The outcome messages currently on screen, one per decided row.
   *
   * <c>viewChildren</c> rather than <c>viewChild</c> because several rows can be decided before the next
   * Refresh, and the one to focus is the one that has just appeared.
   */
  private readonly outcomeMessages = viewChildren<ElementRef<HTMLElement>>('outcomeMessage');

  private readonly queueRows = signal<readonly BankTransferQueueRow[]>([]);
  private readonly totalCount = signal(0);
  private readonly currentPage = signal(1);
  private readonly isLoading = signal(true);
  private readonly isBusy = signal(false);
  private readonly problem = signal<CatalogFailure | null>(null);
  private readonly said = signal('');
  private readonly notes = signal<ReadonlyMap<string, string>>(new Map());
  private readonly outcomes = signal<ReadonlyMap<string, string>>(new Map());
  private readonly refusals = signal<ReadonlyMap<string, BankTransferRefusal>>(new Map());

  protected readonly rows = this.queueRows.asReadonly();
  protected readonly total = this.totalCount.asReadonly();
  protected readonly page = this.currentPage.asReadonly();
  protected readonly loading = this.isLoading.asReadonly();
  protected readonly busy = this.isBusy.asReadonly();
  protected readonly failure = this.problem.asReadonly();
  protected readonly announcement = this.said.asReadonly();
  protected readonly decided = this.outcomes.asReadonly();

  protected readonly hasMore = computed(
    () => this.currentPage() * PAGE_SIZE < this.totalCount(),
  );

  constructor() {
    // No focus move on the first read: nothing has been pressed, and a page that grabs focus on load
    // takes a keyboard user's place away before they have had one.
    this.read({ restoreFocus: false });
  }

  protected reload(): void {
    if (this.isBusy()) {
      return;
    }

    // Refresh lives outside the list, so it survives its own re-read and keeps the focus it already has.
    this.read({ restoreFocus: false });
  }

  protected goTo(page: number): void {
    if (this.isBusy() || page < 1) {
      return;
    }

    this.currentPage.set(page);

    // Previous and Next can both leave the screen when the page they land on is the only one, so the
    // control that was pressed may not survive this read. Focus is handed to Refresh deliberately rather
    // than left to fall to <body> (issue #109's second half: destroying the focused node does the same
    // harm as disabling it).
    this.read({ restoreFocus: true });
  }

  protected note(row: BankTransferQueueRow): string {
    return this.notes().get(row.orderPublicId) ?? '';
  }

  protected setNote(row: BankTransferQueueRow, value: string): void {
    this.notes.update((current) => new Map(current).set(row.orderPublicId, value));
  }

  protected outcome(row: BankTransferQueueRow): string | null {
    return this.outcomes().get(row.orderPublicId) ?? null;
  }

  protected refusal(row: BankTransferQueueRow): BankTransferRefusal | null {
    return this.refusals().get(row.orderPublicId) ?? null;
  }

  protected slipUrl(row: BankTransferQueueRow): string {
    return this.api.slipUrl(row.orderPublicId);
  }

  /**
   * Whether this row's button should read **Accept late payment** rather than **Approve**.
   *
   * Doc 11 P7 step 8. The row is in the queue because its payment is still Pending, so normally Approve is
   * the action — but the deadline can pass while the screen is open, and once the expiry job has run
   * Approve is refused with `order_expired`. Two things put the right button there: a deadline that has
   * passed, and a refusal that already said so.
   */
  protected canAcceptLate(row: BankTransferQueueRow): boolean {
    return (
      this.refusal(row)?.code === BANK_TRANSFER_REFUSALS.orderExpired || row.secondsUntilExpiry <= 0
    );
  }

  /**
   * The time left, in words.
   *
   * <b>Rendered from the server's own figure</b>, never from the deadline and the browser's clock: a shop
   * computer with a wrong clock would otherwise show a queue of deadlines that are all wrong, with nothing
   * on the page saying so.
   */
  protected timeLeft(row: BankTransferQueueRow): string {
    const seconds = row.secondsUntilExpiry;

    if (seconds <= 0) {
      const overdue = Math.abs(seconds);

      return overdue < 3600
        ? `Overdue by ${Math.max(1, Math.floor(overdue / 60))} min`
        : `Overdue by ${Math.floor(overdue / 3600)} h`;
    }

    const hours = Math.floor(seconds / 3600);
    const minutes = Math.floor((seconds % 3600) / 60);

    return hours === 0 ? `${Math.max(1, minutes)} min left` : `${hours} h ${minutes} min left`;
  }

  /** Rs. 15,596.60 — the shop's own currency, formatted the way the storefront formats it. */
  protected money(amount: number): string {
    return `Rs. ${amount.toLocaleString('en-LK', {
      minimumFractionDigits: 2,
      maximumFractionDigits: 2,
    })}`;
  }

  protected shortList(
    lines: Readonly<Record<string, number>>,
  ): readonly { variantId: string; available: number }[] {
    return Object.entries(lines).map(([variantId, available]) => ({ variantId, available }));
  }

  protected decide(row: BankTransferQueueRow, decision: BankTransferDecision): void {
    // The guard the `disabled` attribute would have provided, which is why nothing here is disabled.
    if (this.isBusy() || this.outcome(row) !== null) {
      return;
    }

    const note = this.note(row).trim();

    if (decision === 'reject' && note.length === 0) {
      // Refused here as well as on the server, because a round trip to be told the box is empty is a
      // round trip. The server's check is the one that matters.
      this.refusals.update((current) =>
        new Map(current).set(row.orderPublicId, {
          code: 'note_required',
          message:
            'Say why the transfer is being rejected. The reason is kept with the order and is what you '
            + 'read back if the customer asks.',
          orderStatus: null,
          paymentStatus: null,
          shortLines: null,
        }),
      );

      return;
    }

    this.isBusy.set(true);
    this.problem.set(null);
    this.clearRefusal(row);
    this.announce(this.startedMessage(row, decision));

    this.api.decide(row.orderPublicId, decision, note.length === 0 ? null : note).subscribe({
      next: (result) => {
        this.isBusy.set(false);

        const message =
          decision === 'reject'
            ? `Transfer for ${result.orderNumber} rejected. The order stays open until its deadline.`
            : `${result.orderNumber} is paid. Invoice ${result.invoiceNumber}.`;

        // The row keeps its place and gains its outcome. It is not removed, because removing the node the
        // staff member's focus is inside does the same harm as disabling it (issue #109's second half) —
        // and the count line above already tells them the queue has shrunk.
        this.outcomes.update((current) => new Map(current).set(row.orderPublicId, message));
        this.announce(message);

        // **And the focus is re-homed, because keeping the row is not enough.** The action bar that held
        // the pressed button is replaced by the outcome message, so the focused node goes either way; a
        // browser test caught the first version of this leaving focus on <body>.
        this.focusOutcome(row);
      },
      error: (error: unknown) => {
        this.isBusy.set(false);

        const refused = toBankTransferRefusal(error);

        if (refused !== null) {
          this.refusals.update((current) => new Map(current).set(row.orderPublicId, refused));

          // Announced as well as shown, because the staff member's focus is on the button they pressed
          // and the message appears above it.
          this.announce(refused.message);
          return;
        }

        this.problem.set(toCatalogFailure(error));
        this.announce('');
      },
    });
  }

  private startedMessage(row: BankTransferQueueRow, decision: BankTransferDecision): string {
    switch (decision) {
      case 'approve':
        return `Approving the transfer for ${row.orderNumber}…`;
      case 'reject':
        return `Rejecting the transfer for ${row.orderNumber}…`;
      default:
        return `Accepting the late payment for ${row.orderNumber}…`;
    }
  }

  /**
   * Puts a sentence in the live region, **re-firing an identical one**.
   *
   * `role="status"` announces a change, and setting a region to the text it already holds is not a change
   * — so a repeated identical sentence is read out once and then never again (ADM-22). A trailing space is
   * invisible and makes it a change; same mechanism as `WishlistStore.announce`.
   *
   * <b>On this screen the guarantee actually comes from the alternation</b>, and that is worth saying so
   * the next reader does not mistake the backstop for the fix: every decision announces
   * "Approving the transfer for …" on the way in and its outcome on the way out, so two identical outcomes
   * are separated by a different sentence and both are changes already. That is ADM-22's own fix for the
   * admin screens, and the spec asserts the alternation rather than the trailing space because the
   * trailing space is unreachable here. It is kept because an announcement added later — one with no
   * "…ing" counterpart, as `OrderPage`'s copy button has — would need it, and would otherwise be silent
   * the second time with nothing to say why.
   */
  private announce(sentence: string): void {
    this.said.update((previous) => (previous === sentence ? `${sentence} ` : sentence));
  }

  /**
   * Moves focus to a decided row's outcome message, once the view holds it.
   *
   * <b><c>afterNextRender</c>, and the two cheaper things both fail.</b> A straight <c>focus()</c> finds
   * nothing: the signal write a line earlier is what makes the message exist and change detection has not
   * run yet. A <c>queueMicrotask</c> was the first attempt and **a browser test caught it still leaving
   * focus on <c>&lt;body&gt;</c>** — the microtask queue is where change detection is *scheduled*, so the
   * callback can run before the render rather than after it. <c>afterNextRender</c> is the one that means
   * what it says, and <c>security-page.ts</c> uses it for the same job.
   */
  private focusOutcome(row: BankTransferQueueRow): void {
    const testId = `done-${row.orderNumber}`;

    afterNextRender(
      () => {
        const target = this.outcomeMessages()
          .map((message) => message.nativeElement)
          .find((element) => element.dataset['testid'] === testId);

        target?.focus();
      },
      { injector: this.injector },
    );
  }

  private clearRefusal(row: BankTransferQueueRow): void {
    this.refusals.update((current) => {
      const next = new Map(current);
      next.delete(row.orderPublicId);
      return next;
    });
  }

  private read({ restoreFocus }: { restoreFocus: boolean }): void {
    this.isLoading.set(true);
    this.problem.set(null);

    this.api.queue(this.currentPage(), PAGE_SIZE).subscribe({
      next: (page) => {
        this.queueRows.set(page.rows);
        this.totalCount.set(page.totalCount);
        this.isLoading.set(false);

        // A re-read replaces the list, so the decided and refused messages belong to rows that are no
        // longer on screen.
        this.outcomes.set(new Map());
        this.refusals.set(new Map());

        if (restoreFocus) {
          this.refreshButton()?.nativeElement.focus();
        }
      },
      error: (error: unknown) => {
        this.problem.set(toCatalogFailure(error));
        this.isLoading.set(false);
      },
    });
  }
}
