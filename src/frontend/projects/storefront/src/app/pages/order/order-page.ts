import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  RESPONSE_INIT,
  computed,
  inject,
  signal,
} from '@angular/core';
import { HttpErrorResponse } from '@angular/common/http';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { Meta, Title } from '@angular/platform-browser';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { BRAND, brandPageTitle } from '@spencer-denim/shared';

import { formatLkr } from '../../core/catalog/money';
import { CheckoutApi, checkoutErrorMessage, checkoutFailure } from '../../core/checkout/checkout-api';
import {
  OrderConfirmationDto,
  OrderPaymentFieldDto,
  OrderPaymentProofDto,
  formatColomboDateTime,
} from '../../core/checkout/checkout.model';
import { RobotsMeta } from '../../core/platform/robots-meta';
import { ServerHandover } from '../../core/platform/server-handover';

/** Where the server leaves the order it rendered, for the browser that hydrates the page. */
const HANDOVER_KEY = 'order';

/** A token that names no order gets a real 404, the same as a mistyped product slug. */
const NOT_FOUND_STATUS = 404;

/**
 * The largest slip this page will send.
 *
 * Doc 08 §3.3's own figure, and the server enforces it three times over (the endpoint's body limit,
 * `MediaOptions.MaxBytesFor` and `CK_MediaAssets_SizeBytes`). Checking it here as well saves a shopper on
 * a mobile connection from uploading five megabytes to be told no.
 */
const MAX_SLIP_BYTES = 5 * 1024 * 1024;

/**
 * The order success page, and a guest's order link (CHK-04, doc 11 P6 steps 10–11).
 *
 * **One page for both, deliberately.** The shopper who has just pressed Place order and the shopper who
 * opens the link three days later want the same thing: the order number, what was bought, what it costs
 * and where it is going. Two pages would be two places for the figures to drift apart, and the success
 * page would be the one that could not survive a reload.
 *
 * **The token in the URL is the whole credential**, which is why it is a GUID. Doc 08 §3.5 keeps
 * `Orders.PublicId` under a unique index precisely so a guest's link can carry it: a guest has no
 * account, and a sequential id would let anyone read the next person's order. A token that names no order
 * gets a flat 404 — not "that order is not yours", which would confirm it exists.
 *
 * **`noindex` and `no-store`**, for the obvious reason: the page holds a name, an address and a phone
 * number, and the URL is a bearer token. A crawler that indexed one of these would publish it.
 *
 * ## What is not here, and why
 *
 * **No "this order was already placed" notice.** <code>OrderConfirmationDto.isDuplicateSubmission</code>
 * is a fact about <i>a submission</i>, not about the order, so <code>GET /api/orders/{token}</code>
 * answers <code>false</code> for every order including a replayed one — correctly. This page reads the
 * order back by its token (that is the whole reason it survives a reload), so a notice driven by that
 * flag could never render: the first version of this page had one and <code>checkout.e2e.ts</code> found
 * it unreachable. The signal that matters is the one the API gives a client — <b>200 rather than
 * 201</b> — and it is asserted there. A shopper whose request was retried sees one success page for one
 * order, which is the truth and needs no explaining.
 *
 * **The bank-transfer panel is doc 11 P7 steps 1 and 3**, and it only appears when the server has
 * something true to put in it. The account to pay into, the exact amount, the reference to quote and the
 * deadline all come from the payload — the shop's details are read live, which is correct for a shopper
 * who has not paid yet. If the shop has not filled its bank details in, the panel says the details are
 * being confirmed rather than printing an empty account number somebody might transfer money to.
 *
 * **It still does not claim an email has been sent**, because nothing sends one: `ops.EmailOutbox` and
 * ACC-03's templates are W3-06. The order's own link is what a shopper is told to keep, and the panel
 * repeats that beside the deadline.
 *
 * **No countdown that ticks.** The deadline is a date and time in Colombo, not "47 hours left": a ticking
 * figure would be rendered from the browser's clock, and a device with the wrong date would quietly tell a
 * shopper the wrong deadline for their own money. The staff queue is where a live figure belongs, and
 * there the server computes it.
 */
@Component({
  selector: 'sd-order-page',
  imports: [RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="container order">
      @if (isLoading() && order() === null) {
        <p class="order__muted" role="status">Loading your order…</p>
      } @else if (notFound()) {
        <div class="order__empty">
          <h1 class="order__title">We could not find that order</h1>
          <p class="order__muted">
            The link may be incomplete, or the order may have been placed with a different email address.
            If you have your order number, get in touch and we will look it up.
          </p>
          <a class="btn btn--primary" routerLink="/">Continue shopping</a>
        </div>
      } @else if (order(); as placed) {
        <header class="order__head">
          <p class="order__eyebrow">Thank you</p>
          <h1 class="order__title" id="order-title">Your order is placed</h1>

          <!-- The order number is the one thing a shopper will be asked for on the phone, so it is the
               largest thing on the page and is selectable text rather than part of a sentence. -->
          <p class="order__number">
            Order number
            <strong class="order__number-value" data-order-number>{{ placed.orderNumber }}</strong>
          </p>

          <p class="order__muted">
            We have your order and it is waiting for payment. Keep this page's address: it is the link to
            your order.
          </p>
        </header>

        <div class="order__layout">
          <section class="order__panel" aria-labelledby="order-items-title">
            <h2 class="order__panel-title" id="order-items-title">What you ordered</h2>

            <ul class="order__lines">
              @for (line of placed.lines; track line.lineNumber) {
                <li class="order__line">
                  @if (line.imageUrl) {
                    <img
                      class="order__thumb"
                      [src]="line.imageUrl"
                      alt=""
                      width="56"
                      height="75"
                      loading="lazy"
                    />
                  }
                  <span class="order__line-body">
                    <span class="order__line-name">{{ line.productName }}</span>
                    <span class="order__line-meta">
                      {{ line.colourName }} · {{ line.sizeLabel }} · Qty {{ line.quantity }}
                    </span>
                    <span class="order__line-meta">{{ line.productCode }}</span>
                  </span>
                  <span class="order__line-price">{{ money(line.lineSubtotal) }}</span>
                </li>
              }
            </ul>

            <!-- Same ladder, same labels, same arithmetic as the bag and the checkout summary, read back
                 off the order row rather than recomputed:
                 Subtotal − Savings − Discount + Delivery − Free delivery = Total. -->
            <dl class="order__sums">
              <div>
                <dt>Subtotal</dt>
                <dd data-sum="subtotal">{{ money(placed.itemsOriginalTotal) }}</dd>
              </div>
              @if (placed.savedTotal > 0) {
                <div>
                  <dt>Savings</dt>
                  <dd class="text-sale" data-sum="savings">&minus;{{ money(placed.savedTotal) }}</dd>
                </div>
              }
              @if (placed.promoDiscountAmount > 0) {
                <div>
                  <dt>Discount</dt>
                  <dd class="text-sale" data-sum="promo">
                    &minus;{{ money(placed.promoDiscountAmount) }}
                  </dd>
                </div>
              }
              <div>
                <dt>Delivery</dt>
                <dd data-sum="delivery">{{ money(placed.deliveryFeeAmount) }}</dd>
              </div>
              @if (placed.deliveryDiscountAmount > 0) {
                <div>
                  <dt>Free delivery</dt>
                  <dd class="text-sale" data-sum="delivery-discount">
                    &minus;{{ money(placed.deliveryDiscountAmount) }}
                  </dd>
                </div>
              }
              <div class="order__sums-total">
                <dt>Total</dt>
                <dd data-sum="total">{{ money(placed.grandTotal) }}</dd>
              </div>
            </dl>
          </section>

          <aside class="order__panel" aria-labelledby="order-details-title">
            <h2 class="order__panel-title" id="order-details-title">Your details</h2>

            <dl class="order__facts">
              <div>
                <dt>Email</dt>
                <dd>{{ placed.email }}</dd>
              </div>
              @if (placed.phone) {
                <div>
                  <dt>Phone</dt>
                  <dd>{{ placed.phone }}</dd>
                </div>
              }
              <div>
                <dt>{{ isPickup(placed) ? 'Collect from' : 'Delivery' }}</dt>
                <dd>
                  <span class="order__block">{{ placed.delivery.methodName }}</span>
                  @if (isPickup(placed)) {
                    @if (placed.delivery.pickupLocationName) {
                      <span class="order__block">{{ placed.delivery.pickupLocationName }}</span>
                    }
                    <span class="order__block">{{ placed.delivery.districtName }}</span>
                  } @else {
                    @if (placed.delivery.recipientName) {
                      <span class="order__block">{{ placed.delivery.recipientName }}</span>
                    }
                    @if (placed.delivery.line1) {
                      <span class="order__block">{{ placed.delivery.line1 }}</span>
                    }
                    @if (placed.delivery.line2) {
                      <span class="order__block">{{ placed.delivery.line2 }}</span>
                    }
                    <span class="order__block">
                      {{ placed.delivery.city }}, {{ placed.delivery.districtName }}
                      @if (placed.delivery.postalCode) {
                        {{ placed.delivery.postalCode }}
                      }
                    </span>
                    @if (placed.delivery.landmark) {
                      <span class="order__block order__muted">{{ placed.delivery.landmark }}</span>
                    }
                  }
                  <span class="order__block order__muted">Estimated {{ estimate(placed) }}</span>
                </dd>
              </div>
              <div>
                <dt>Payment</dt>
                <dd>
                  <span class="order__block">{{ placed.paymentMethodName }}</span>
                  <span class="order__block order__muted">{{ paymentState(placed) }}</span>
                </dd>
              </div>
              @if (placed.customerNote) {
                <div>
                  <dt>Your note</dt>
                  <dd>{{ placed.customerNote }}</dd>
                </div>
              }
            </dl>

            <a class="link" routerLink="/">Continue shopping</a>
          </aside>
        </div>

        @if (awaitingPayment(placed)) {
          <section class="order__panel pay" aria-labelledby="pay-title" data-testid="pay-panel">
            <h2 class="order__panel-title" id="pay-title">How to pay</h2>

            <p class="sr-only" role="status" data-testid="pay-announcement">{{ announcement() }}</p>

            @if (placed.payment; as how) {
              <p class="pay__lead">
                Transfer <strong data-testid="pay-amount">{{ money(how.amountLkr) }}</strong> to the
                account below by <strong data-testid="pay-due">{{ dueAt(placed) }}</strong>, quoting your
                order number as the reference. We hold your items until then.
              </p>

              <dl class="pay__details">
                @if (how.reference) {
                  <div class="pay__detail">
                    <dt>Reference to quote</dt>
                    <dd>
                      <span class="pay__value" data-testid="pay-reference">{{ how.reference }}</span>
                      <button
                        class="link pay__copy"
                        type="button"
                        (click)="copy(how.reference, 'the reference')"
                        data-testid="copy-reference"
                      >
                        Copy
                      </button>
                    </dd>
                  </div>
                }

                @for (field of how.fields; track field.label) {
                  <div class="pay__detail">
                    <dt>{{ field.label }}</dt>
                    <dd>
                      <span class="pay__value" [attr.data-testid]="fieldTestId(field)">
                        {{ field.value }}
                      </span>
                      @if (field.copyable) {
                        <button
                          class="link pay__copy"
                          type="button"
                          (click)="copy(field.value, field.label.toLowerCase())"
                          [attr.data-testid]="'copy-' + fieldTestId(field)"
                        >
                          Copy
                        </button>
                      }
                    </dd>
                  </div>
                }
              </dl>

              @if (how.notes) {
                <!-- Interpolated, so it is text whatever it holds. The value is a settings field a staff
                     user types and this is a public page; there is no HTML sanitiser in this build, and
                     an unsanitised value rendered as markup here would be stored XSS. -->
                <p class="pay__notes" data-testid="pay-notes">{{ how.notes }}</p>
              }

              @if (how.acceptsProof) {
                <!-- **Not a <form>, and the first version was one — a browser test caught what that costs.**
                     A native submit button inside a form with no action submits to the page's own URL, and
                     before Angular hydrates there is no (ngSubmit) handler to stop it: a shopper who types
                     a reference on a slow connection and presses Save navigates to
                     ?payerReference=...&slip=, the typed value goes into the URL instead of the order, and
                     nothing says so. Silent loss of something a shopper just typed about their own money.

                     So the controls are plain buttons with click handlers, and Enter is handled explicitly
                     on the text box: the keyboard shortcut survives without a submission that can escape
                     to the browser.

                     **The first version of this comment also claimed "there was no <form> in the storefront
                     before this one" and that checkout's Place Order is a button. Both are false, and the
                     correction is the point.** checkout-page.ts has a <form> whose submit button is
                     attached from outside by the form= attribute, which is why a grep for (ngSubmit) found
                     only this file and why I believed the claim when I wrote it. That one is worse than
                     mine was: a pre-hydration submit puts a shopper's name, phone, address and city into
                     the query string, loses the form and leaves personal data in history and access logs.
                     It is W3-02's and filed as issue #123 — not fixed here, and not to be inferred away by
                     this comment a second time. The lesson is the one this file keeps relearning: a
                     comment written from what the author intended, rather than from the behaviour in front
                     of them, is worse than no comment. -->
                <div class="pay__proof">
                  <h3 class="pay__sub">Tell us about your transfer</h3>
                  <p class="order__muted">
                    Optional, and it helps us find your payment faster. We check our bank statement either
                    way.
                  </p>

                  <label class="pay__field">
                    <span class="pay__label">Your bank's reference</span>
                    <input
                      class="pay__input"
                      id="pay-ref-input"
                      type="text"
                      maxlength="100"
                      autocomplete="off"
                      [value]="reference()"
                      (input)="setReference($event)"
                      (keydown.enter)="saveReference()"
                      data-testid="reference-input"
                    />
                  </label>

                  <div class="pay__actions">
                    <!-- aria-disabled, never disabled: disabling the control under the shopper's own
                         finger throws focus to <body> (issue #109), and saveReference refuses while a
                         write is in flight so the button is inert either way. -->
                    <button
                      class="btn btn--secondary"
                      type="button"
                      [attr.aria-disabled]="busy() ? 'true' : null"
                      (click)="saveReference()"
                      data-testid="save-reference"
                    >
                      {{ busy() ? 'Saving…' : 'Save the reference' }}
                    </button>
                  </div>

                  <label class="pay__field">
                    <span class="pay__label">
                      A photo or PDF of your slip (up to 5 MB)
                    </span>
                    <input
                      class="pay__input"
                      id="pay-slip-input"
                      type="file"
                      [accept]="slipAccept"
                      [attr.aria-disabled]="busy() ? 'true' : null"
                      (change)="onSlipChosen($event)"
                      data-testid="slip-input"
                      #slipInput
                    />
                  </label>

                  @if (placed.proof; as sent) {
                    @if (sent.slipFileName) {
                      <p class="pay__sent" data-testid="slip-current">
                        We have <strong>{{ sent.slipFileName }}</strong> for this order.
                        <a class="link" [href]="slipUrl()" target="_blank" rel="noopener">
                          Open your copy
                        </a>
                        · Uploading another one replaces it.
                      </p>
                    }
                  }

                  @if (problem(); as message) {
                    <p class="pay__error" role="alert" data-testid="pay-error">{{ message }}</p>
                  }
                </div>
              }
            } @else {
              <!-- A real state, not an error: doc 11 P7 makes "bank details set by a Super Admin" a
                   precondition, and the shop can be live before the client sends them. Half a form is
                   worse than none — a shopper who saw a bank name and an empty account number might
                   transfer to whatever they found.

                   **"We will be in touch" used to be here, and it was a promise nothing can keep.**
                   Nothing in this build contacts a customer: ops.EmailOutbox and the templates are W3-06,
                   and this branch is reached precisely when the shop has not finished setting itself up,
                   so the one thing that would make the sentence true is the thing that is missing. A
                   reviewer was right that a three-phrase blocklist in the tests is a canary and not a
                   property — the property is that this page only ever says what the system can do, and
                   that has to be held line by line.

                   The deadline is named here as well. It applies whether or not the shop has filled its
                   details in: this order is released if nothing arrives by then, and a shopper who cannot
                   see a deadline cannot act on it. -->
              <p class="pay__lead" data-testid="pay-pending-details">
                We are still confirming the payment details for this order, so there is nothing to transfer
                to yet. Nothing has been charged. Keep this page's address — it is the link to your order,
                and the details will appear here once they are set.
              </p>
              <p class="pay__lead" data-testid="pay-pending-due">
                Please check back before <strong>{{ dueAt(placed) }}</strong>: if no payment reaches us by
                then, the order is released and the items go back on sale.
              </p>
            }
          </section>
        }

        @if (placed.canCancel) {
          <!-- Doc 11 P10 flow A, the customer's half. **Two steps, because this is irreversible for
               them**: the first press reveals the confirmation, the second one fires. It is an inline
               confirmation rather than window.confirm or a <dialog> - a native confirm does not exist
               during server-side rendering, cannot be styled or announced, and is the sort of thing
               people dismiss by reflex. This way the question is on the page, in the reading order, and
               "Keep my order" is the button a shopper reaches first.

               Every control says aria-disabled rather than disabled, because disabling a button under the
               shopper's own finger throws focus to <body> (issue #109), and the handlers refuse the
               activation instead. -->
          <section class="order__panel" aria-labelledby="cancel-title" data-testid="cancel-panel">
            <h2 class="order__panel-title" id="cancel-title">Changed your mind?</h2>

            @if (!confirmingCancel()) {
              <p class="order__muted pay__lead">
                You have not paid for this order, so you can cancel it yourself. The items go back on sale
                straight away.
              </p>
              <div class="pay__actions">
                <button
                  class="btn btn--secondary"
                  type="button"
                  [attr.aria-disabled]="busy() ? 'true' : null"
                  data-testid="cancel-order"
                  (click)="askToCancel()"
                >
                  Cancel this order
                </button>
              </div>
            } @else {
              <p class="pay__lead" data-testid="cancel-confirm-question">
                Cancel order {{ placed.orderNumber }}? This cannot be undone - if you want the items
                afterwards you would have to order them again, and we cannot promise they will still be
                there.
              </p>
              <div class="pay__actions">
                <button
                  class="btn btn--secondary"
                  type="button"
                  data-testid="cancel-keep"
                  (click)="keepOrder()"
                >
                  Keep my order
                </button>
                <button
                  class="btn btn--primary"
                  type="button"
                  [attr.aria-disabled]="busy() ? 'true' : null"
                  data-testid="cancel-confirm"
                  (click)="confirmCancel()"
                >
                  Yes, cancel it
                </button>
              </div>
            }

            @if (cancelProblem(); as message) {
              <p class="order__error" role="alert" data-testid="cancel-error">{{ message }}</p>
            }

            <!-- Nothing promises an email, because nothing sends one: ops.EmailOutbox is W3-06's, and doc
                 11 P10's notification list has no email for a cancellation in any case (G-14). -->
            <p class="order__muted">
              Nothing is charged and nothing is emailed. This page will show the order as cancelled.
            </p>
          </section>
        }
      } @else if (error(); as message) {
        <div class="order__empty">
          <h1 class="order__title">We could not load your order</h1>
          <p class="order__error" role="alert">{{ message }}</p>
          <button class="btn btn--secondary" type="button" (click)="retry()">Try again</button>
        </div>
      }
    </div>
  `,
  styles: `
    :host {
      display: block;
    }
    .order {
      display: grid;
      gap: var(--space-3);
      padding-block: var(--space-4) var(--space-12);
    }
    .order__head {
      display: grid;
      gap: var(--space-1);
      max-width: 60ch;
    }
    .order__eyebrow {
      font-size: var(--text-label);
      font-weight: var(--weight-medium);
      letter-spacing: var(--tracking-label);
      text-transform: uppercase;
      color: var(--color-text-muted);
    }
    .order__title {
      font-size: var(--text-h1);
      letter-spacing: -0.01em;
    }
    .order__number {
      display: flex;
      flex-wrap: wrap;
      align-items: baseline;
      gap: var(--space-1);
      font-size: var(--text-small);
      color: var(--color-text-muted);
      text-transform: uppercase;
      letter-spacing: var(--tracking-label);
    }
    .order__number-value {
      font-size: var(--text-h3);
      letter-spacing: 0.02em;
      color: var(--color-text);
      /* Long enough to need to wrap at 320 px rather than widen the page. */
      overflow-wrap: anywhere;
    }
    .order__muted,
    .order__line-meta {
      color: var(--color-text-muted);
    }
    .order__error {
      padding: var(--space-1-5);
      border: 1px solid var(--color-sale);
      color: var(--color-sale);
    }
    /* The pay panel. Its own block rather than reusing .order__facts, because these values are copied
       rather than read: an account number has to be selectable as one run of characters, and the copy
       button has to be a full-height control at 320 px (GLB-09's 44 px floor). */
    /* min-width: 0 on every grid level of this panel, and it is the fix the measurement pointed at.
       A grid item's default min-width is auto, which means min-content — and a file input's min-content
       is the width of its button plus its filename text, which does not shrink. That forced this panel to
       362px inside a 320px viewport, and because the panel is a sibling in the page's own grid every
       other panel stretched with it: the browser test reported 74px of overflow and named
       h2.order__panel-title as an offender, which is a heading that is entirely innocent.
       W3-02's round 3 records the inverse of this lesson - dropping a min-width: 0 and having to gate it
       with a re-measure - so both directions are now written down. */
    .pay {
      display: grid;
      gap: var(--space-2);
      min-width: 0;
    }
    .pay__lead {
      max-width: 60ch;
    }
    /* minmax(min(100%, 16rem), 1fr) and not minmax(16rem, 1fr).
       The second form makes 16rem a hard floor, so at 320 px the track is wider than the column it is in
       and the page scrolls sideways — which is what a browser test measured as 74 px of overflow on this
       page, against the project's own non-negotiable. min(100%, ...) lets the single track shrink to the
       container when the container is the smaller of the two. */
    .pay__details {
      display: grid;
      gap: var(--space-1-5);
      grid-template-columns: repeat(auto-fit, minmax(min(100%, 16rem), 1fr));
      margin: 0;
      min-width: 0;
    }
    .pay__detail dt {
      font-size: var(--text-small);
      color: var(--color-text-muted);
    }
    .pay__detail dd {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: var(--space-1);
      margin: 0;
      min-width: 0;
    }
    .pay__value {
      font-size: var(--text-body);
      letter-spacing: 0.02em;
      /* An account number can be longer than a narrow column, and a shopper copying it by hand needs it
         whole rather than widening the page. */
      overflow-wrap: anywhere;
      user-select: all;
    }
    /* **A 44 px tap target in both directions, and the width half is not decoration.** The shared
       button.link rule gives these controls min-height: var(--tap) and nothing about width, which is
       right for a link-shaped button sitting inside a sentence and wrong for one standing on its own: at
       320 px the browser tests measured this button at 40x44, because "Copy" is simply narrower than a
       finger. justify-content: center keeps the label in the middle of the box the min-width creates
       rather than hard against its left edge.

       Fixed here rather than in base.css: widening every link-button in the shop would stretch the ones
       that genuinely are words in a sentence, and that is a change for whoever owns the design system. It
       is written up in the journal as a trap any short-labelled button.link will fall into. */
    .pay__copy {
      flex: none;
      justify-content: center;
      min-width: var(--tap);
    }
    /* The shop's own note, with its line breaks kept. It arrives as text and is interpolated as text. */
    .pay__notes {
      max-width: 60ch;
      white-space: pre-line;
      color: var(--color-text-muted);
    }
    .pay__proof {
      display: grid;
      gap: var(--space-1-5);
      padding-top: var(--space-2);
      border-top: 1px solid var(--color-border);
      min-width: 0;
    }
    .pay__sub {
      font-size: var(--text-h3);
    }
    .pay__field {
      display: grid;
      gap: 0.375rem;
      max-width: min(36rem, 100%);
      min-width: 0;
    }
    .pay__label {
      font-size: var(--text-small);
      color: var(--color-text-muted);
    }
    /* max-width: 100% is load-bearing for the file input specifically: a file control has a wide
       intrinsic size that does not shrink with its container, and it was the larger part of the 74 px of
       overflow a browser test found at 320 px. A text input would have shrunk on its own. */
    .pay__input {
      width: 100%;
      max-width: 100%;
      min-width: 0;
      min-height: var(--tap);
      padding: 0 var(--space-1);
      font: inherit;
      color: var(--color-text);
      background-color: var(--color-surface);
      border: 1px solid var(--color-border);
    }
    .pay__actions {
      display: flex;
      flex-wrap: wrap;
      gap: var(--space-1);
    }
    .pay__sent {
      color: var(--color-text-muted);
    }
    .pay__error {
      padding: var(--space-1-5);
      border: 1px solid var(--color-sale);
      color: var(--color-sale);
    }
    /* The write controls say aria-disabled rather than disabled, so a press does not throw the shopper's
       focus to the document body. That leaves the unavailable look to be drawn here. Opacity only: a
       control that cannot be hovered cannot show a cursor, and the press is already a no-op. Pointer
       events stay on, because removing them would stop the control being hit-tested, and
       document.elementFromPoint is how the occlusion sweep finds it.
       (No backticks in this block: it is a template literal, and one would end it.)

       **The selector is unscoped, and W3-04b made it so to stay inside a budget.** It used to be
       .pay [aria-disabled=true]; the cancel panel's controls need the same look, and this component's
       styles have a 4 kB budget that four new classes for that panel's layout blew by exactly 253 bytes
       (measured both ways: origin/main builds clean, with them it warned). So the panel reuses
       order__panel for its grid, pay__lead for its 60ch measure and pay__actions for its button row, and
       this rule dropped its prefix rather than gaining a second selector - which is also the truer rule,
       since every aria-disabled control in this component is a busy guard and every one of them should
       look unavailable. Angular scopes it to this component, so it cannot reach another screen.

       And the line above about backticks is not decoration: the first draft of this very comment quoted
       those three class names in them, which ended the literal and turned the rest of the file into
       compile errors about class attributes. The warning was already written here and I still did it. */
    [aria-disabled='true'] {
      opacity: 0.6;
      cursor: progress;
    }
    .order__empty {
      display: grid;
      justify-items: start;
      gap: var(--space-2);
      max-width: 60ch;
      padding-block: var(--space-4);
    }
    .order__layout {
      display: grid;
      gap: var(--space-3);
      align-items: start;
    }
    .order__panel {
      display: grid;
      gap: var(--space-2);
      min-width: 0;
      padding: var(--space-2);
      background: var(--color-bg-alt);
    }
    .order__panel-title {
      font-size: var(--text-h3);
      letter-spacing: -0.01em;
    }
    .order__lines {
      display: grid;
      gap: var(--space-1-5);
      margin: 0;
      padding: 0;
      list-style: none;
    }
    .order__line {
      display: flex;
      gap: var(--space-1);
      align-items: flex-start;
    }
    .order__thumb {
      flex: none;
      width: 56px;
      height: auto;
      object-fit: cover;
      background: var(--color-bg);
    }
    .order__line-body {
      display: grid;
      gap: 2px;
      min-width: 0;
      flex: 1 1 auto;
    }
    .order__line-name {
      font-size: var(--text-small);
      overflow-wrap: anywhere;
    }
    .order__line-meta {
      font-size: var(--text-small);
    }
    .order__line-price {
      flex: none;
      font-size: var(--text-small);
      font-variant-numeric: tabular-nums;
    }
    .order__sums,
    .order__facts {
      display: grid;
      gap: var(--space-1);
      margin: 0;
    }
    .order__sums {
      padding-block-start: var(--space-1-5);
      border-block-start: 1px solid var(--color-border);
    }
    .order__sums > div {
      display: flex;
      gap: var(--space-1);
      justify-content: space-between;
    }
    .order__sums dd {
      margin: 0;
      font-variant-numeric: tabular-nums;
      text-align: end;
    }
    .order__sums-total {
      padding-block-start: var(--space-1);
      border-block-start: 1px solid var(--color-border);
      font-weight: var(--weight-medium);
    }
    .order__facts > div {
      display: grid;
      gap: 2px;
    }
    .order__facts dt {
      font-size: var(--text-label);
      font-weight: var(--weight-medium);
      letter-spacing: var(--tracking-label);
      text-transform: uppercase;
      color: var(--color-text-muted);
    }
    .order__facts dd {
      margin: 0;
      overflow-wrap: anywhere;
    }
    .order__block {
      display: block;
    }

    @media (min-width: 64rem) {
      .order__layout {
        grid-template-columns: minmax(0, 1fr) minmax(18rem, 24rem);
      }
    }
  `,
})
export class OrderPage {
  private readonly api = inject(CheckoutApi);
  private readonly handover = inject(ServerHandover);
  private readonly destroyRef = inject(DestroyRef);

  private readonly current = signal<OrderConfirmationDto | null>(null);
  private readonly loading = signal(false);
  private readonly failure = signal<string | null>(null);
  private readonly missing = signal(false);

  private readonly route = inject(ActivatedRoute);

  private readonly routeParams = toSignal(this.route.params, {
    initialValue: this.route.snapshot.params,
  });

  /**
   * The unguessable token from the URL, which carries `sales.Orders.PublicId`.
   *
   * **Read from the route's own params rather than through a signal input**, which is the pattern the
   * product page already uses for its slug. The difference matters: a required signal input is not
   * populated until the first change detection, and this page starts its request from the
   * **constructor** so that the server render has the order in hand before it renders anything. Reading
   * a required input there throws NG0950.
   *
   * The route segment is named `:token` rather than `:publicId` because `app.routes.spec.ts` holds
   * GLB-11's "friendly URLs, no ids" rule over the whole table — and "token" is the more honest word
   * anyway: this value is a credential, not an identifier anyone could guess or increment.
   */
  protected readonly token = computed(() => String(this.routeParams()['token'] ?? ''));

  /**
   * The server render's response, so this page can set a 404 status and its cache headers.
   * <p>
   * A field initialiser rather than an assignment in the constructor: `inject` works in both, and a
   * field declared without an initialiser would be defined as `undefined` by the class-fields semantics
   * before the constructor body ran — which is harmless here but reads as a bug waiting to be
   * introduced. Absent in the browser, where a client-side navigation has no response.
   * </p>
   */
  private readonly response = inject(RESPONSE_INIT, { optional: true });

  private readonly writing = signal(false);
  private readonly payProblem = signal<string | null>(null);
  private readonly said = signal('');

  constructor() {
    inject(Title).setTitle(brandPageTitle('Your order'));
    inject(Meta).updateTag({
      name: 'description',
      content: `Your ${BRAND.name} order.`,
    });

    // The URL is a bearer token and the page holds an address. Nothing about it may be indexed, and
    // unlike a 404 there is no status code saying so.
    const robots = inject(RobotsMeta);
    robots.noindex();
    this.destroyRef.onDestroy(() => robots.clear());

    if (this.response) {
      const headers = new Headers(this.response.headers ?? {});
      headers.set('Cache-Control', 'no-store, no-cache, private');
      headers.set('Vary', 'Cookie');
      this.response.headers = headers;
    }

    const handedOver = this.handover.take<OrderConfirmationDto>(HANDOVER_KEY);
    if (handedOver) {
      this.current.set(handedOver);
    }

    this.read();
  }

  protected readonly order = this.current.asReadonly();
  protected readonly isLoading = this.loading.asReadonly();
  protected readonly error = this.failure.asReadonly();
  protected readonly notFound = computed(() => this.missing() && this.current() === null);

  /** What the media endpoint takes for a slip. Checked on the server by file signature, not by name. */
  protected readonly slipAccept = 'image/jpeg,image/png,image/webp,application/pdf';

  /**
   * The reference box's contents.
   *
   * Seeded from the payload and then owned by the shopper: a re-read after a save must not reset what
   * they have started typing next, which is `SettingsPage.applySettings`' lesson in a smaller form.
   */
  protected readonly reference = signal('');

  /**
   * Whether the shopper has pressed "Cancel this order" and is being asked to confirm.
   *
   * **Two steps on purpose.** Cancelling is irreversible for them and the items go back on sale, so one
   * stray press on a phone must not close somebody's order. It resets on every re-read, so a page that
   * has moved on never comes back with the question half-asked.
   */
  private readonly askingCancel = signal(false);
  private readonly cancelFailure = signal<string | null>(null);

  protected readonly confirmingCancel = this.askingCancel.asReadonly();
  protected readonly cancelProblem = this.cancelFailure.asReadonly();

  protected readonly busy = this.writing.asReadonly();
  protected readonly problem = this.payProblem.asReadonly();
  protected readonly announcement = this.said.asReadonly();

  /**
   * Whether this order is still waiting for money, and so whether the pay panel belongs on screen.
   *
   * **Read off the status, not off `payment`.** The two are different questions and conflating them is
   * how a paid order would show a "transfer this amount" panel for as long as the shop's bank details
   * were unset: `payment` is null for a paid order *and* for an order whose details are not filled in,
   * and only one of those should put the panel on screen.
   */
  protected awaitingPayment(order: OrderConfirmationDto): boolean {
    return order.status === 'PendingPayment';
  }

  /**
   * The sentence beside "Payment" in the details list.
   *
   * **The wording comes from the server, not from `status.toLowerCase()`.** Doc 11 P9's pickup variant
   * says `Shipped` is shown to staff *and customer* as "Ready for collection", so a pickup customer was
   * reading "This order is shipped." — unreachable until ADM-09 made the transition pressable, and found
   * in its review. `statusLabel` is rendered by the same `OrderStatusLabels.For` the two admin screens
   * use, so a shopper, a staff list and a staff detail cannot describe one order three ways.
   */
  protected paymentState(order: OrderConfirmationDto): string {
    if (!this.awaitingPayment(order)) {
      return order.status === 'Paid'
        ? 'Paid. Thank you — we are getting your order ready.'
        : `This order is ${order.statusLabel.toLowerCase()}.`;
    }

    return `Awaiting payment. We hold your items until ${this.dueAt(order)}.`;
  }

  /** A stable test id per detail, from its label. */
  protected fieldTestId(field: OrderPaymentFieldDto): string {
    return `pay-${field.label.toLowerCase().replace(/[^a-z0-9]+/g, '-')}`;
  }

  protected slipUrl(): string {
    return this.api.paymentSlipUrl(this.token());
  }

  /**
   * Copies a value and says so.
   *
   * **`navigator.clipboard` is not always there** — it needs a secure context, and it is absent during
   * server-side rendering and in a plain-http preview. A failure is announced as "could not copy, select
   * it instead" rather than being swallowed, because the shopper is about to retype an account number and
   * silence would have them believe it was on the clipboard.
   */
  protected copy(value: string, what: string): void {
    const clipboard = globalThis.navigator?.clipboard;

    if (!clipboard) {
      this.announce(`We could not copy ${what}. Select it and copy it yourself.`);
      return;
    }

    clipboard.writeText(value).then(
      () => this.announce(`Copied ${what}.`),
      () => this.announce(`We could not copy ${what}. Select it and copy it yourself.`),
    );
  }

  /**
   * Keeps the reference box and the signal in step.
   *
   * [value] plus (input) rather than ngModel, which is how every other input in this shop is bound: the
   * reference box used to live in a form, and taking the form away took ngModel with it (ngModel outside
   * a form needs standalone options, and reaching for them would be keeping a dependency for nothing).
   */
  protected setReference(event: Event): void {
    this.reference.set((event.target as HTMLInputElement).value);
  }

  protected saveReference(): void {
    // The guard the `disabled` attribute would have provided, which is why nothing here is disabled.
    if (this.writing()) {
      return;
    }

    const value = this.reference().trim();

    this.beginWrite(value.length === 0 ? 'Removing your reference…' : 'Saving your reference…');

    this.api
      .setPaymentReference(this.token(), value.length === 0 ? null : value)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: () => {
          this.writing.set(false);
          this.announce(
            value.length === 0
              ? 'Your reference was removed.'
              : 'Your reference is saved. We will look for it on our statement.',
          );
          this.read();
        },
        error: (error: unknown) => this.failWrite(error),
      });
  }

  /** First press: ask (doc 11 P10 flow A, step 1). */
  protected askToCancel(): void {
    // The guard the `disabled` attribute would have provided, which is why nothing here is disabled.
    if (this.writing()) {
      return;
    }

    this.cancelFailure.set(null);
    this.askingCancel.set(true);
    this.announce('Cancel this order? Choose "Yes, cancel it", or "Keep my order".');
  }

  /** Second thoughts about the second thoughts. */
  protected keepOrder(): void {
    this.askingCancel.set(false);
    this.cancelFailure.set(null);
    this.announce('Your order is still with us.');
  }

  /**
   * Second press: cancel it (doc 11 P10 flow A, step 3).
   *
   * **Nothing here decides whether it is allowed.** All four of P10 step 2's guards are evaluated inside
   * the transaction, under the order row's own lock (doc 08 §5.2) - which is what makes this press and the
   * expiry sweep taking the same order produce exactly one winner and release the stock exactly once.
   * `canCancel` on the payload decides only whether the panel is drawn.
   *
   * **A 404 is the ordinary refusal**, and it is deliberately the same answer as "no such order": the
   * token never expires and is the only credential there is. So the sentence covers every reason at once
   * and the page re-reads the order, which is where a shopper holding the link is entitled to the detail.
   */
  protected confirmCancel(): void {
    if (this.writing()) {
      return;
    }

    this.beginWrite('Cancelling your order...');

    this.api
      .cancelOrder(this.token())
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (cancelled) => {
          this.writing.set(false);
          this.askingCancel.set(false);
          this.announce(`Order ${cancelled.orderNumber} is cancelled. The items are back on sale.`);
          this.read();
        },
        error: (error: unknown) => {
          this.writing.set(false);
          this.askingCancel.set(false);
          this.said.set('');

          this.cancelFailure.set(
            error instanceof HttpErrorResponse && error.status === 404
              ? 'This order can no longer be cancelled - it may already have been paid, or the hold may '
                + 'have lapsed. We have reloaded it below so you can see where it stands.'
              : checkoutErrorMessage(error),
          );

          // Re-read either way: the refusal rolled nothing back, and the order's state on screen is the
          // one thing that must not be guessed from a failure.
          this.read();
        },
      });
  }

  protected onSlipChosen(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];

    this.payProblem.set(null);

    // The box carries `aria-disabled` rather than `disabled`, so the guard it would have provided has to
    // be here — and the box is cleared so no filename is left on screen implying a file was taken.
    if (this.writing()) {
      input.value = '';
      return;
    }

    if (!file) {
      return;
    }

    // Checked here as well as on the server, because being refused after a slow upload on a Sri Lankan
    // mobile connection is a worse experience than a sentence before it starts. The server's check is the
    // one that matters: it reads the file's signature, so a renamed file does not get through.
    if (file.size > MAX_SLIP_BYTES) {
      this.payProblem.set('That file is larger than 5 MB. A photo of the slip is usually well under it.');
      input.value = '';
      return;
    }

    this.beginWrite('Sending your slip…');

    this.api
      .uploadPaymentSlip(this.token(), file)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (uploaded) => {
          this.writing.set(false);
          input.value = '';
          this.announce(`We have your slip, ${uploaded.fileName}.`);
          this.read();
        },
        error: (error: unknown) => {
          input.value = '';
          this.failWrite(error);
        },
      });
  }

  private beginWrite(message: string): void {
    this.writing.set(true);
    this.payProblem.set(null);
    this.announce(message);
  }

  private failWrite(error: unknown): void {
    this.writing.set(false);
    this.said.set('');
    this.payProblem.set(this.payMessage(error));
  }

  /**
   * What to tell a shopper about a refused reference or slip.
   *
   * The server's own sentence wherever there is one: it says what to do next. The two cases worth
   * recognising are a 404 — which for these routes means the order can no longer be paid, not that it
   * does not exist — and a 429, which is doc 08 §3.3 N10's limit.
   */
  private payMessage(error: unknown): string {
    if (error instanceof HttpErrorResponse) {
      const body = (error.error ?? null) as Record<string, unknown> | null;
      const detail = typeof body?.['detail'] === 'string' ? (body['detail'] as string) : null;

      if (error.status === 404) {
        return 'This order can no longer be paid. If you have already transferred the money, get in '
          + 'touch and we will sort it out.';
      }

      if (detail !== null) {
        return detail;
      }
    }

    return checkoutErrorMessage(error);
  }

  /**
   * Puts a sentence in the live region, **re-firing an identical one**.
   *
   * `role="status"` announces a change, and setting a region to the text it already holds is not a change
   * — so copying the account number twice would be read out once and then never again. A trailing space
   * is invisible and makes it a change. Same fix as `CartStore.announce`, same defect (ADM-22).
   */
  private announce(sentence: string): void {
    this.said.update((previous) => (previous === sentence ? `${sentence} ` : sentence));
  }

  protected money(amount: number): string {
    return formatLkr(amount);
  }

  protected isPickup(order: OrderConfirmationDto): boolean {
    // The method's code, not its name: `DeliveryMethods.Name` is admin-editable and the code is the
    // value `CK_Orders_Pickup` and doc 08 §4.1's G-18 guard both key on.
    return order.delivery.methodCode === 'PICKUP';
  }

  protected estimate(order: OrderConfirmationDto): string {
    const { estimatedDaysMin: min, estimatedDaysMax: max } = order.delivery;

    return min === max ? `${min} ${min === 1 ? 'day' : 'days'}` : `${min}–${max} days`;
  }

  protected dueAt(order: OrderConfirmationDto): string {
    return formatColomboDateTime(order.paymentDueAtUtc);
  }

  protected retry(): void {
    this.read();
  }

  private read(): void {
    const token = this.token();

    this.loading.set(true);
    this.failure.set(null);

    this.api
      .order(token)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (order) => {
          this.loading.set(false);
          this.missing.set(false);
          this.current.set(order);
          this.handover.give(HANDOVER_KEY, order);
          this.seedReference(order.proof);

          // **A half-asked question never survives a re-read.** The order's state has just changed under
          // the panel, so "Cancel order SD-...? This cannot be undone" about an order that is now paid or
          // cancelled would be a question with no true answer.
          this.askingCancel.set(false);
        },
        error: (error: unknown) => {
          this.loading.set(false);

          if (checkoutFailure(error) === 'notFound') {
            this.missing.set(true);

            // A real 404, not a 200 that says "not found" — the same treatment a mistyped product slug
            // gets, and for the same reason: a soft 404 tells a crawler the address is fine.
            if (this.response) {
              this.response.status = NOT_FOUND_STATUS;
            }

            return;
          }

          this.failure.set(checkoutErrorMessage(error));
        },
      });
  }

  /**
   * Puts the stored reference into the box, **without overwriting what the shopper is typing**.
   *
   * The box is seeded once, from the first payload that has a value for it. A re-read follows every save,
   * and that response can land after the shopper has started correcting the value again — setting it from
   * the server then would silently wipe those keystrokes, which is the defect `SettingsPage` records at
   * length under `keepUnsavedEdits`.
   */
  private seedReference(proof: OrderPaymentProofDto | null): void {
    if (this.referenceSeeded) {
      return;
    }

    this.referenceSeeded = true;
    this.reference.set(proof?.reference ?? '');
  }

  private referenceSeeded = false;
}
