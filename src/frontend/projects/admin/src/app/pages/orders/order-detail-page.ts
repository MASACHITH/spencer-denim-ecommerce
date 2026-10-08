import {
  ChangeDetectionStrategy,
  Component,
  Injector,
  afterNextRender,
  computed,
  inject,
  signal,
} from '@angular/core';
import { DOCUMENT } from '@angular/common';
import { ActivatedRoute, RouterLink } from '@angular/router';

import { AdminOrderApi, toOrderTransitionRefusal } from '../../core/orders/admin-order-api';
import {
  AdminCourier,
  AdminOrderDetail,
  OrderTransitionRefusal,
  OrderTransitionSegment,
} from '../../core/orders/admin-order.model';
import { colomboTime, money, timeLeft } from '../../core/orders/order-formatting';
import { CatalogFailure, toCatalogFailure } from '../../core/catalog/admin-catalog-api';

/**
 * One order in full, with the fulfilment workflow on it (ADM-09, doc 11 P9).
 *
 * **What it shows:** what was bought, with SKU, colour, size, quantity and line money so a picker can work
 * from it; the customer's contact details and the delivery address, or the pickup point; the money
 * breakdown; every payment attempt with its slip; every refund; and the status history as a timeline with
 * who did what and when.
 *
 * **What it does:** the four transitions doc 11 P9 and doc 08 §4.1 allow, one step at a time and never
 * backwards — Start processing, Mark as shipped (courier *and* tracking number), Mark as delivered, plus
 * the pickup variant's Ready for collection and Mark as collected. A mistake is corrected with the
 * dispatch form or the internal note, because an order that has moved cannot move back.
 *
 * **And it is issue #124's answer.** The bank-transfer queue lists `Pending` payments, and expiry closes
 * the payment in the same transaction as the order — so an expired order is not in that queue at all, and
 * a staff member who got an `order_expired` refusal there and pressed Refresh lost the only route to the
 * transaction permanently. This screen is reached by searching the order number the customer quotes, which
 * is exactly what they have when they ring up about a transfer that arrived late, and a reload cannot
 * destroy it. Accept late payment posts to the route W3-03 already built and tested, and the short lines
 * it returns when the stock re-check refuses are rendered with product, colour, size and SKU.
 *
 * **Doc 11 §6.6 step 2 is why the refund shows two dates.** It tells staff to read a refund's amount, the
 * date it was sent and its reference out to a customer on the telephone — and `completedAtUtc` is the date
 * it was sent, which is not the same as `createdAtUtc`: a `Pending` refund has been recorded and the money
 * has not moved. Showing only one would have a staff member reading out the wrong date, confidently.
 *
 * **Nothing here promises an email, a notification or an acknowledgement.** `ops.EmailOutbox` is specified
 * (doc 08 §3.8) and unbuilt; W3-06 owns it. Doc 11 P9 step 5 queues `OrderShipped` and nothing can send
 * it, so the dispatch panel says in words that the customer is not told — which is also doc 11 P9's own
 * instruction about a corrected tracking number. **There is no honest route to the customer today:** the
 * shopper's own order page shows one sentence about the order's state — not a timeline, which an earlier
 * version of this comment claimed — and no tracking number, because the public contract has no such
 * field. That is ACC-02's, and the screen says nothing that implies otherwise.
 */
@Component({
  selector: 'sda-order-detail-page',
  imports: [RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (problem(); as failure) {
      <div class="adm-state adm-state--error" role="alert">
        <p>{{ failure.message }}</p>
        <div class="adm-actions">
          <a class="adm-link" routerLink="/orders">Back to orders</a>
          <button class="adm-btn" type="button" (click)="reload()">Try again</button>
        </div>
      </div>
    }

    @if (order(); as detail) {
      <section class="adm-panel">
        <a class="adm-link" routerLink="/orders">← All orders</a>
        <h1 class="adm-panel__title" data-testid="order-number">{{ detail.orderNumber }}</h1>
        <p>
          <span class="adm-pill" data-testid="order-status">{{ detail.statusLabel }}</span>
          @if (detail.secondsUntilExpiry !== null) {
            <span class="adm-muted">&nbsp;{{ left(detail.secondsUntilExpiry) }}</span>
          }
        </p>
        <dl class="order-facts">
          <dt>Placed</dt>
          <dd>{{ when(detail.placedAtUtc) }}</dd>
          @if (detail.paidAtUtc) {
            <dt>Paid</dt>
            <dd>{{ when(detail.paidAtUtc) }}</dd>
          }
          @if (detail.shippedAtUtc) {
            <dt>{{ detail.delivery.isPickup ? 'Ready since' : 'Shipped' }}</dt>
            <dd>{{ when(detail.shippedAtUtc) }}</dd>
          }
          @if (detail.deliveredAtUtc) {
            <dt>{{ detail.delivery.isPickup ? 'Collected' : 'Delivered' }}</dt>
            <dd>{{ when(detail.deliveredAtUtc) }}</dd>
          }
          @if (detail.invoiceNumber) {
            <dt>Invoice</dt>
            <dd>{{ detail.invoiceNumber }}</dd>
          }
          <dt>Stock</dt>
          <dd>{{ detail.stockState }}</dd>
          @if (detail.cancelReason) {
            <!-- Doc 02 ADM-09's "cancel… status". It is null on every order today because nothing
                 cancels one yet (that is W3-05's), and a field nothing reads is a field nobody notices
                 breaking — so it is rendered now, beside the status it explains, rather than the day the
                 writer lands and somebody wonders why the reason never shows. -->
            <dt>Why it was cancelled</dt>
            <dd data-testid="cancel-reason">{{ detail.cancelReason }}</dd>
          }
          @if (detail.closedAtUtc) {
            <dt>Closed</dt>
            <dd>{{ when(detail.closedAtUtc) }}</dd>
          }
        </dl>

        @if (detail.dispatch; as dispatch) {
          <!-- The dispatch as it stands, which the list also shows. The *name* is the order's snapshot
               (doc 11 P9: the courier's name is copied into the order), so a rename does not rewrite a
               past dispatch note; the *link* is rendered from the courier's current template (doc 08
               §3.5), so editing a template changes the link on older orders too. Both facts are on the
               screen, because a staff member on the telephone reads one and clicks the other. -->
          <dl class="order-facts" data-testid="order-dispatch">
            <dt>Courier</dt>
            <dd>{{ dispatch.courierName }}</dd>
            <dt>Tracking number</dt>
            <dd>
              @if (dispatch.trackingUrl) {
                <a
                  class="adm-link"
                  [href]="dispatch.trackingUrl"
                  target="_blank"
                  rel="noopener noreferrer"
                  data-testid="tracking-link"
                >
                  {{ dispatch.trackingNumber }}
                </a>
              } @else {
                {{ dispatch.trackingNumber }}
                <span class="adm-muted">(this courier has no online tracking)</span>
              }
            </dd>
          </dl>
        }

        <div class="adm-actions">
          @if (detail.invoiceNumber) {
            <a
              class="adm-btn"
              [routerLink]="['/orders', detail.orderPublicId, 'invoice']"
              data-testid="print-invoice"
            >
              Invoice / packing copy
            </a>
          } @else {
            <p class="adm-panel__hint" data-testid="no-invoice">
              There is no invoice yet. One is issued, with its number, the moment the order is paid.
            </p>
          }
        </div>
      </section>

      <!-- tabindex="-1" so focus can land here when an action destroys the control that was pressed:
           focus would otherwise fall to <body> (#109). -->
      <p class="adm-muted" role="status" tabindex="-1" data-testid="order-said">{{ said() }}</p>

      @if (refusal(); as refused) {
        <div class="adm-state adm-state--error" role="alert" data-testid="order-refusal">
          <p>{{ refused.message }}</p>
          @if (refused.orderStatusLabel) {
            <p class="adm-muted">This order is now: {{ refused.orderStatusLabel }}.</p>
          }
          <div class="adm-actions">
            <button class="adm-btn" type="button" data-testid="refusal-reload" (click)="reload()">
              Reload the order
            </button>
          </div>
        </div>
      }

      @if (shortLines().length > 0) {
        <div class="adm-state adm-state--error" role="alert" data-testid="short-stock">
          <p>
            This order cannot be paid now, because some of its items have been sold since it expired.
            Refund the money received instead.
          </p>
          <ul class="order-short">
            @for (line of shortLines(); track line.sku) {
              <li [attr.data-testid]="'short-' + line.sku">
                {{ line.productName }} · {{ line.colourName }} · {{ line.sizeLabel }} ({{ line.sku }}) —
                need {{ line.needed }}, {{ line.available }} left
              </li>
            }
          </ul>
          <p class="adm-muted">
            Refunding from the system is not built yet: it needs the Orders.Refund screen. Record what you
            did in the internal note below so the next person reading this order knows.
          </p>
        </div>
      }

      <section class="adm-panel">
        <h2 class="adm-panel__title">What to do next</h2>

        @if (detail.actions.blockedReason; as blocked) {
          <div class="adm-state adm-state--warning" data-testid="order-blocked">
            <p>{{ blocked }}</p>
          </div>
        }

        <div class="adm-actions">
          @if (detail.actions.canStartProcessing) {
            <button
              class="adm-btn--primary"
              type="button"
              [attr.aria-disabled]="isBusy() ? 'true' : null"
              data-testid="start-processing"
              (click)="act('start-processing', {})"
            >
              Start processing
            </button>
          }
          @if (detail.actions.canMarkReadyForCollection) {
            <button
              class="adm-btn--primary"
              type="button"
              [attr.aria-disabled]="isBusy() ? 'true' : null"
              data-testid="ready-for-collection"
              (click)="act('ready-for-collection', {})"
            >
              Ready for collection
            </button>
          }
          @if (detail.actions.canMarkDelivered) {
            <button
              class="adm-btn--primary"
              type="button"
              [attr.aria-disabled]="isBusy() ? 'true' : null"
              data-testid="mark-delivered"
              (click)="act('mark-delivered', {})"
            >
              {{ detail.delivery.isPickup ? 'Mark as collected' : 'Mark as delivered' }}
            </button>
          }
          @if (detail.actions.canAcceptLatePayment) {
            <button
              class="adm-btn--primary"
              type="button"
              [attr.aria-disabled]="isBusy() ? 'true' : null"
              data-testid="accept-late"
              (click)="acceptLate()"
            >
              Accept late payment
            </button>
          }
        </div>

        @if (awaitingReview()) {
          <!-- The decision itself lives on the bank-transfer queue behind Orders.Approve (doc 11 P7
               step 4), and is not duplicated here: one screen owns approving money. What this screen
               owes a staff member who reached the order by its number is the way there. -->
          <p class="adm-panel__hint" data-testid="awaiting-review">
            A bank transfer on this order is waiting to be reviewed. Approving or rejecting it happens on
            <a class="adm-link" routerLink="/orders/bank-transfers">Bank transfers awaiting approval</a>,
            which needs the Orders.Approve permission.
          </p>
        }

        @if (detail.actions.canAcceptLatePayment) {
          <p class="adm-panel__hint" data-testid="accept-late-hint">
            The money arrived after the deadline. Accepting re-checks stock for every item: if everything
            is still available the order is paid with today's date and a new invoice number; if anything
            has sold, nothing changes and you are told which items.
          </p>

          <!-- **Its own box, and that was a defect.** This used to send whatever was in the internal-note
               textarea below as the reason — which is kept with the payment, audited, and read back to a
               customer who asks why their money was taken a week late. A staff member writing a note to
               themselves about a telephone call had no way to know it was about to become the reason a
               payment was accepted. Two different pieces of writing; two boxes. -->
          <label class="adm-field adm-field--wide">
            <span class="adm-label">Why you are accepting it (optional)</span>
            <textarea
              class="adm-input"
              rows="2"
              maxlength="500"
              [value]="acceptLateNote()"
              data-testid="accept-late-note"
              (input)="typeAcceptLateNote($event)"
            ></textarea>
            <span class="adm-panel__hint">
              Kept with the payment and in the order's history, and read back if the customer asks. The
              internal note further down is separate and is not sent.
            </span>
          </label>
        }

        @if (detail.actions.canCancel) {
          <!-- Doc 11 P10 flow A. **The reason is required and the form asks for it**, rather than a bare
               Cancel button with an optional box beside it: cancelling is irreversible for the customer,
               the reason is kept on the order and read back to them if they ask, and a confirmation step
               is the obvious place for a mistake. There is no second "are you sure", because typing a
               sentence *is* the deliberate act — a dialog on top of it would be a reflex to dismiss.

               It is a form element so Enter in the box submits; the control carries aria-disabled and
               never the disabled attribute, because disabling a button under the user's finger throws
               focus to <body> (#109), and the handler refuses the activation instead.

               No backticks in this comment: it is inside a template literal and one would end it, which
               is a mistake this task made twice in an hour, on two screens. -->
          <form class="order-cancel" (submit)="submitCancel($event)">
            <h3 class="adm-label">Cancel this order</h3>
            <p class="adm-panel__hint">
              Only an unpaid order can be cancelled. The items go back on sale straight away and the
              order's open payment is closed. <strong>The customer is not emailed</strong> — tell them
              yourself.
            </p>
            <label class="adm-field adm-field--wide">
              <span class="adm-label">Why (required)</span>
              <textarea
                class="adm-input"
                rows="2"
                maxlength="500"
                [value]="cancelReason()"
                data-testid="cancel-reason-input"
                (input)="typeCancelReason($event)"
              ></textarea>
              <span class="adm-panel__hint">
                Kept on the order and read back to the customer if they ask. The internal note further
                down is separate.
              </span>
            </label>
            @if (cancelError(); as message) {
              <p class="adm-state adm-state--error" role="alert" data-testid="cancel-error">
                {{ message }}
              </p>
            }
            <div class="adm-actions">
              <button
                class="adm-btn"
                type="submit"
                [attr.aria-disabled]="isBusy() ? 'true' : null"
                data-testid="cancel-order"
              >
                Cancel the order
              </button>
            </div>
          </form>
        }

        @if (detail.actions.canMarkShipped || detail.actions.canEditDispatch) {
          <form class="order-dispatch" (submit)="submitDispatch($event, detail)">
            <h3 class="adm-label">
              {{ detail.actions.canEditDispatch ? 'Correct the dispatch' : 'Dispatch' }}
            </h3>
            <div class="adm-fields">
              <label class="adm-field">
                <span class="adm-label">Courier</span>
                <!-- The selection is on each option rather than a value binding on the select, and that
                     is a defect this screen's own spec found.

                     What was measured: with a value binding on the select, the DOM value came back empty
                     on the first render even though the signal held a matching id, and moving the binding
                     on to each option fixed it. The explanation that fits is that an element's own
                     property bindings are applied before its children are refreshed, so the value was
                     written while the option list was still empty and a select whose value names no
                     option resets to the empty one — but the measurement is the part to trust, and the
                     spec is what holds it.

                     The consequence either way: the form came up unselected on every order that already
                     had a courier, which is the one case the correction form exists for. -->
                <select
                  class="adm-input"
                  name="courierId"
                  data-testid="courier"
                  (change)="pickCourier($event)"
                >
                  <option value="" [selected]="courierId() === ''">Choose a courier…</option>
                  @for (courier of activeCouriers(); track courier.id) {
                    <option [value]="courier.id" [selected]="courierId() === asText(courier.id)">
                      {{ courier.name }}
                    </option>
                  }
                </select>
              </label>
              <label class="adm-field">
                <span class="adm-label">Tracking number</span>
                <input
                  class="adm-input"
                  type="text"
                  name="trackingNumber"
                  maxlength="60"
                  autocomplete="off"
                  [value]="trackingNumber()"
                  data-testid="tracking-number"
                  (input)="typeTracking($event)"
                />
              </label>
            </div>
            @if (dispatchError(); as message) {
              <p class="adm-field-error" role="alert" data-testid="dispatch-error">{{ message }}</p>
            }
            <p class="adm-panel__hint">
              @if (detail.actions.canEditDispatch) {
                No new notice is sent to the customer, so they are still holding the old tracking link.
                Telephone or WhatsApp them today and record it in the internal note.
              } @else {
                One dispatch per order: there is no split shipment, so this sends all of it.
              }
            </p>
            <div class="adm-actions">
              <button
                class="adm-btn--primary"
                type="submit"
                [attr.aria-disabled]="isBusy() ? 'true' : null"
                data-testid="submit-dispatch"
              >
                {{ detail.actions.canEditDispatch ? 'Save the correction' : 'Mark as shipped' }}
              </button>
            </div>
          </form>
        }
      </section>

      <section class="adm-panel">
        <h2 class="adm-panel__title">What was bought</h2>
        <ul class="order-lines" data-testid="order-lines">
          @for (line of detail.lines; track line.lineNumber) {
            <li class="order-line" [attr.data-testid]="'line-' + line.sku">
              <p class="order-line__name">{{ line.productName }}</p>
              <dl class="order-facts">
                <dt>SKU</dt>
                <dd>{{ line.sku }}</dd>
                <dt>Colour</dt>
                <dd>{{ line.colourName }}</dd>
                <dt>Size</dt>
                <dd>{{ line.sizeLabel }}</dd>
                <dt>Quantity</dt>
                <dd>{{ line.quantity }}</dd>
                <dt>Unit price</dt>
                <dd>{{ cash(line.unitPrice, detail.money.currency) }}</dd>
                <dt>Line total</dt>
                <dd>{{ cash(line.lineTotal, detail.money.currency) }}</dd>
                @if (line.refundedQuantity > 0) {
                  <dt>Refunded</dt>
                  <dd>{{ line.refundedQuantity }} of {{ line.quantity }}</dd>
                }
              </dl>
            </li>
          }
        </ul>

        <dl class="order-money" data-testid="order-money">
          <dt>Items</dt>
          <dd>{{ cash(detail.money.itemsSubtotal, detail.money.currency) }}</dd>
          @if (detail.money.markdownDiscountTotal > 0) {
            <dt>Markdown</dt>
            <dd>−{{ cash(detail.money.markdownDiscountTotal, detail.money.currency) }}</dd>
          }
          @if (detail.money.campaignDiscountTotal > 0) {
            <dt>Sale campaign</dt>
            <dd>−{{ cash(detail.money.campaignDiscountTotal, detail.money.currency) }}</dd>
          }
          @if (detail.money.promoCodeText) {
            <dt>Promo code {{ detail.money.promoCodeText }}</dt>
            <dd>−{{ cash(detail.money.promoDiscountAmount, detail.money.currency) }}</dd>
          }
          <dt>Delivery</dt>
          <dd>{{ cash(detail.money.deliveryFeeAmount, detail.money.currency) }}</dd>
          @if (detail.money.deliveryDiscountAmount > 0) {
            <dt>Delivery discount</dt>
            <dd>−{{ cash(detail.money.deliveryDiscountAmount, detail.money.currency) }}</dd>
          }
          <dt class="order-money__total">Total</dt>
          <dd class="order-money__total" data-testid="order-total">
            {{ cash(detail.money.grandTotal, detail.money.currency) }}
          </dd>
          @if (detail.money.refundedAmount > 0) {
            <dt>Refunded</dt>
            <dd>{{ cash(detail.money.refundedAmount, detail.money.currency) }}</dd>
          }
        </dl>
      </section>

      <section class="adm-panel">
        <h2 class="adm-panel__title">Customer</h2>
        @if (detail.customer.isAnonymised) {
          <p class="adm-panel__hint" data-testid="anonymised">
            This customer asked for their details to be removed, so the name, address and telephone number
            below have been overwritten. The order itself is kept.
          </p>
        }
        <dl class="order-facts">
          <dt>Name</dt>
          <dd>{{ detail.customer.name }}</dd>
          <dt>Email</dt>
          <dd><a class="adm-link" [href]="'mailto:' + detail.customer.email">{{ detail.customer.email }}</a></dd>
          <dt>Telephone</dt>
          <dd>
            @if (detail.customer.phone) {
              <a class="adm-link" [href]="'tel:' + detail.customer.phone">{{ detail.customer.phone }}</a>
            } @else {
              <span class="adm-muted">not recorded</span>
            }
          </dd>
          <dt>Account</dt>
          <dd>{{ detail.customer.hasAccount ? 'Signed in' : 'Guest' }}</dd>
        </dl>

        <h3 class="adm-label">{{ detail.delivery.isPickup ? 'Collection' : 'Delivery address' }}</h3>
        <dl class="order-facts" data-testid="order-delivery">
          <dt>Method</dt>
          <dd>{{ detail.delivery.methodName }}</dd>
          @if (detail.delivery.isPickup) {
            <dt>Collect from</dt>
            <dd>{{ detail.delivery.pickupLocationName ?? 'not recorded' }}</dd>
          } @else {
            <dt>Recipient</dt>
            <dd>{{ detail.delivery.recipientName }}</dd>
            <dt>Telephone</dt>
            <dd>{{ detail.delivery.recipientPhone }}</dd>
            <dt>Address</dt>
            <dd>
              {{ detail.delivery.line1 }}@if (detail.delivery.line2) {, {{ detail.delivery.line2 }}}<br />
              {{ detail.delivery.city }}@if (detail.delivery.postalCode) {, {{ detail.delivery.postalCode }}}
              @if (detail.delivery.landmark) {<br />Near {{ detail.delivery.landmark }}}
            </dd>
          }
          <dt>District</dt>
          <dd>{{ detail.delivery.districtName }}</dd>
          <dt>Estimate given</dt>
          <dd>{{ detail.delivery.estimatedDaysMin }}–{{ detail.delivery.estimatedDaysMax }} days</dd>
        </dl>

        @if (detail.customerNote) {
          <h3 class="adm-label">What the customer asked for</h3>
          <p class="order-note" data-testid="customer-note">{{ detail.customerNote }}</p>
        }
      </section>

      <section class="adm-panel">
        <h2 class="adm-panel__title">Payment</h2>
        @if (detail.payments.length === 0) {
          <p class="adm-muted">No payment attempt has been recorded.</p>
        }
        <ul class="order-lines" data-testid="order-payments">
          @for (payment of detail.payments; track payment.createdAtUtc + payment.status) {
            <li class="order-line">
              <p class="order-line__name">
                {{ methodLabel(payment.providerCode) }} — {{ payment.status }}
                @if (payment.isOrderSettlement) {
                  <span class="adm-pill">Settled this order</span>
                }
              </p>
              <dl class="order-facts">
                <dt>Amount</dt>
                <dd>{{ cash(payment.amount, payment.currency) }}</dd>
                @if (payment.providerAmount !== null && payment.providerCurrency) {
                  <!-- The foreign figure, which is the one allowed to differ from LKR (doc 08 §3.5 D1)
                       and the one doc 11 P10 step 6 has staff read out. Gated on its presence, so it is
                       absent for every payment this build can create and appears the day PayPal ships. -->
                  <dt>Charged by the provider</dt>
                  <dd [attr.data-testid]="'provider-amount-' + payment.createdAtUtc">
                    {{ cash(payment.providerAmount, payment.providerCurrency) }}
                  </dd>
                }
                <dt>Opened</dt>
                <dd>{{ when(payment.createdAtUtc) }}</dd>
                @if (payment.payerReference) {
                  <dt>Their reference</dt>
                  <dd>{{ payment.payerReference }}</dd>
                }
                @if (payment.slipPublicId) {
                  <dt>Slip</dt>
                  <dd>
                    <a
                      class="adm-link"
                      [href]="slipUrl(detail.orderPublicId)"
                      target="_blank"
                      rel="noopener"
                      [attr.data-testid]="'slip-' + payment.status"
                    >
                      {{ payment.slipFileName ?? 'Open the slip' }}
                    </a>
                  </dd>
                }
                @if (payment.approvedByUserEmail) {
                  <dt>Reviewed by</dt>
                  <dd>{{ payment.approvedByUserEmail }}, {{ when(payment.approvedAtUtc) }}</dd>
                }
                @if (payment.approvalNote) {
                  <dt>Their note</dt>
                  <dd>{{ payment.approvalNote }}</dd>
                }
                @if (payment.failureCode) {
                  <dt>Outcome</dt>
                  <dd>{{ payment.failureMessage ?? payment.failureCode }}</dd>
                }
              </dl>
            </li>
          }
        </ul>

        @if (detail.refunds.length > 0) {
          <h3 class="adm-label">Refunds</h3>
          <ul class="order-lines" data-testid="order-refunds">
            @for (refund of detail.refunds; track refund.createdAtUtc) {
              <li class="order-line">
                <p class="order-line__name">{{ cash(refund.amount, refund.currency) }} — {{ refund.status }}</p>
                <dl class="order-facts">
                  @if (refund.providerAmount !== null) {
                    <dt>Sent by the provider</dt>
                    <dd>{{ cash(refund.providerAmount, 'USD') }}</dd>
                  }
                  <dt>How</dt>
                  <dd>{{ refund.method }}</dd>
                  <dt>Why</dt>
                  <dd>{{ refund.reason }}</dd>
                  <dt>Stock put back</dt>
                  <dd>{{ refund.restockItems ? 'Yes' : 'No' }}</dd>
                  <dt>By</dt>
                  <dd>{{ refund.createdByUserEmail ?? 'the payment provider' }}</dd>
                  <dt>Recorded</dt>
                  <dd>{{ when(refund.createdAtUtc) }}</dd>
                  <dt>Money sent</dt>
                  <dd data-testid="refund-completed">
                    @if (refund.completedAtUtc) {
                      {{ when(refund.completedAtUtc) }}
                    } @else {
                      <span class="adm-muted">not yet — this refund is still {{ refund.status }}</span>
                    }
                  </dd>
                </dl>
              </li>
            }
          </ul>
        }
      </section>

      <section class="adm-panel">
        <h2 class="adm-panel__title">History</h2>
        <ol class="order-history" data-testid="order-history">
          @for (entry of detail.history; track entry.occurredAtUtc + entry.toStatus) {
            <li class="order-history__entry">
              <p class="order-history__what">{{ step(entry.fromStatusLabel, entry.toStatusLabel) }}</p>
              <p class="adm-muted">
                {{ when(entry.occurredAtUtc) }} · {{ who(entry.source, entry.changedByUserEmail) }}
              </p>
              @if (entry.note) {
                <p class="order-note">{{ entry.note }}</p>
              }
            </li>
          }
        </ol>
      </section>

      <section class="adm-panel">
        <h2 class="adm-panel__title">Internal note</h2>
        <p class="adm-panel__hint">
          Staff only — the customer never sees it. Orders never move backwards, so a mistake is recorded
          here rather than undone.
        </p>
        <label class="adm-field adm-field--wide">
          <span class="adm-label">Note</span>
          <textarea
            class="adm-input"
            rows="4"
            maxlength="2000"
            [value]="internalNote()"
            data-testid="internal-note"
            (input)="typeNote($event)"
          ></textarea>
        </label>
        <div class="adm-actions">
          <button
            class="adm-btn"
            type="button"
            [attr.aria-disabled]="isBusy() ? 'true' : null"
            data-testid="save-note"
            (click)="saveNote()"
          >
            Save the note
          </button>
        </div>
      </section>
    } @else if (isLoading()) {
      <p class="adm-muted">Loading the order…</p>
    }
  `,
  styles: `
    :host {
      display: grid;
      gap: var(--space-3);
    }
    .order-facts {
      display: grid;
      grid-template-columns: auto 1fr;
      gap: 2px var(--space-2);
      font-size: var(--text-small);
      margin: 0;
      min-width: 0;
    }
    .order-facts dt {
      color: var(--color-text-muted);
    }
    .order-facts dd {
      margin: 0;
      /* A long SKU, email address or tracking number must not widen the panel; at 320 px that is the
         difference between a readable screen and one that scrolls sideways. */
      overflow-wrap: anywhere;
    }
    .order-lines,
    .order-history,
    .order-short {
      display: grid;
      gap: var(--space-2);
      list-style: none;
      padding: 0;
      margin: 0;
    }
    .order-short {
      gap: var(--space-0-5);
    }
    .order-line {
      display: grid;
      gap: var(--space-0-5);
      padding-block: var(--space-1);
      border-top: 1px solid var(--color-border);
      min-width: 0;
    }
    .order-line__name {
      font-weight: var(--weight-semibold);
      overflow-wrap: anywhere;
    }
    .order-money {
      display: grid;
      grid-template-columns: 1fr auto;
      gap: 2px var(--space-2);
      margin: 0;
      padding-top: var(--space-1);
      border-top: 1px solid var(--color-border-strong);
    }
    .order-money dd {
      margin: 0;
      text-align: right;
    }
    .order-money__total {
      font-weight: var(--weight-semibold);
    }
    .order-history__entry {
      display: grid;
      gap: 2px;
      padding-left: var(--space-2);
      border-left: 2px solid var(--color-border-strong);
      min-width: 0;
    }
    .order-history__what {
      font-weight: var(--weight-semibold);
    }
    .order-note {
      /* Staff and customer free text: it keeps its own line breaks and cannot widen the panel. Interpolated
         and never bound as HTML — it is text a shopper typed, on an authenticated screen. */
      white-space: pre-wrap;
      overflow-wrap: anywhere;
      padding: var(--space-1);
      background: var(--color-bg-alt);
      border: 1px solid var(--color-border);
      font-size: var(--text-small);
    }
    .order-dispatch,
    .order-cancel {
      display: grid;
      gap: var(--space-1);
      padding-top: var(--space-2);
      border-top: 1px solid var(--color-border);
    }
  `,
})
export class OrderDetailPage {
  private readonly api = inject(AdminOrderApi);
  private readonly route = inject(ActivatedRoute);
  private readonly document = inject(DOCUMENT);
  private readonly injector = inject(Injector);

  protected readonly order = signal<AdminOrderDetail | null>(null);
  protected readonly couriers = signal<readonly AdminCourier[]>([]);
  protected readonly isLoading = signal(true);
  protected readonly isBusy = signal(false);
  protected readonly problem = signal<CatalogFailure | null>(null);
  protected readonly refusal = signal<OrderTransitionRefusal | null>(null);
  protected readonly shortLines = signal<readonly ShortStockLine[]>([]);
  protected readonly said = signal('');

  protected readonly courierId = signal('');
  protected readonly trackingNumber = signal('');
  protected readonly internalNote = signal('');

  /**
   * The reason for accepting a late payment, which is <b>not</b> the internal note.
   *
   * It reaches `Payments.ApprovalNote` and the order's history row, is audited, and is what staff read
   * back to a customer who asks why their money was taken a week after the deadline. Sending the internal
   * note instead — which is what this screen did — meant a note somebody wrote to themselves about a
   * telephone call silently became a statement about money.
   */
  protected readonly acceptLateNote = signal('');
  protected readonly dispatchError = signal<string | null>(null);

  /**
   * Why this order is being cancelled (doc 11 P10 flow A, step 1: "clicks Cancel and enters a reason").
   *
   * **Its own box, not the internal note and not the accept-late note.** All three are staff writing and
   * all three go somewhere different: this one reaches `Orders.CancelReason`, is rendered under "Why it
   * was cancelled" and is read back to the customer; the internal note is private; the accept-late note
   * is about a payment. W3-04 shipped the accept-late form sending the internal note's text as its
   * audited reason, and that is the defect this separation exists to not repeat.
   */
  protected readonly cancelReason = signal('');
  protected readonly cancelError = signal<string | null>(null);

  /**
   * Whether a bank transfer on this order is still waiting for a staff decision.
   *
   * **It is derived from the payments rather than given its own flag**, because it is not an action this
   * screen performs: approving money is the bank-transfer queue's, behind `Orders.Approve` (doc 11 P7
   * step 4), and duplicating the decision would mean two screens that have to agree about it. What this
   * screen owes is the route — a staff member who found the order by the number the customer quoted
   * should not have to know the queue exists.
   */
  protected readonly awaitingReview = computed(() => {
    const detail = this.order();

    return (
      detail !== null
      && detail.status === 'PendingPayment'
      && detail.payments.some(
        (payment) => payment.providerCode === 'BankTransfer' && payment.status === 'Pending',
      )
    );
  });

  /**
   * Only the couriers a new dispatch may choose, **plus the one this order already has**.
   *
   * The second half matters when correcting a tracking number on an order shipped with a carrier that has
   * since been deactivated: without it the select would silently drop the current value, the form would
   * come up empty, and saving would move the order to a different courier by accident. Keyed on the
   * dispatch's **id** rather than its name, for the reason `courierIdFor` records.
   */
  protected readonly activeCouriers = computed(() => {
    const current = this.order()?.dispatch?.courierId ?? null;

    return this.couriers().filter((courier) => courier.isActive || courier.id === current);
  });

  private loaded = '';

  constructor() {
    // **`ActivatedRoute`, not a signal `input`, and that is the project's convention rather than
    // preference.** The three existing form pages (`product-form-page`, `user-form-page`,
    // `content-page-form-page`) all read `route.snapshot.paramMap`, because `withComponentInputBinding()`
    // is not in this application's router configuration — a route input would simply never be populated,
    // which is what the browser suite found. Turning it on globally for one screen is a change to every
    // route's behaviour for no gain.
    //
    // Subscribed rather than read from the snapshot once, because the router reuses this component when a
    // staff member moves from one order to another: a one-off read would leave the second order showing
    // the first one's data. `ensureLoaded` is idempotent per token.
    this.route.paramMap.subscribe((params) =>
      this.ensureLoaded(params.get('orderPublicId') ?? ''),
    );
  }

  /** The order's public token, from the route. */
  protected orderPublicId(): string {
    return this.route.snapshot.paramMap.get('orderPublicId') ?? '';
  }

  /**
   * An amount in **the currency the row carries**, never an assumed one.
   *
   * Every money contract on this screen has a `currency`, and the payment's and the refund's are their
   * own rather than the order's — doc 08 §3.5 gives them one because a PayPal capture is taken in USD
   * (D1). They are `LKR` by check constraint today; the figure that actually differs is
   * `providerAmount` / `providerCurrency`, rendered beside them where it exists.
   */
  protected cash(amount: number, currency: string): string {
    return money(amount, currency);
  }

  protected left(seconds: number): string {
    return timeLeft(seconds);
  }

  protected when(utc: string | null): string {
    return colomboTime(utc);
  }

  protected slipUrl(orderPublicId: string): string {
    return this.api.slipUrl(orderPublicId);
  }

  /** How the order was paid, in words rather than a provider code. */
  protected methodLabel(providerCode: string): string {
    switch (providerCode) {
      case 'BankTransfer':
        return 'Bank transfer';
      case 'PayPal':
        return 'PayPal';
      default:
        // The raw code rather than a guess. A screen that said "Bank transfer" about a provider it did not
        // recognise would be a statement about money that nobody checked.
        return providerCode;
    }
  }

  /**
   * One line of the timeline.
   *
   * **Equal statuses mean an event that did not move the order** — a rejected transfer, a dispatch
   * correction, a note — and the convention is documented on `OrderStatusHistoryEntry`. Rendering
   * "Awaiting payment → Awaiting payment" would look like a bug in the order rather than a fact about it.
   */
  protected step(from: string | null, to: string): string {
    if (from === null) {
      return 'Order placed';
    }

    return from === to ? to : `${from} → ${to}`;
  }

  /**
   * Who did it.
   *
   * **A null user is not a failed lookup**, which is why it gets a sentence rather than a blank: doc 08 §4
   * writes a history row for every System transition precisely so the timeline is complete, and the expiry
   * job and the auto-deliver job are both in it.
   */
  protected who(source: string, email: string | null): string {
    if (email !== null) {
      return email;
    }

    switch (source) {
      case 'SystemJob':
        return 'by the system';
      case 'PaymentProvider':
        return 'by the payment provider';
      case 'Customer':
        return 'by the customer';
      default:
        return `by ${source}`;
    }
  }

  /**
   * A courier id as the select's own value spells it.
   *
   * The signal holds the value as text, because that is what a `<select>` reports from the DOM; the
   * comparison has to be made in the same vocabulary or every option is unselected.
   */
  protected asText(courierId: number): string {
    return String(courierId);
  }

  protected pickCourier(event: Event): void {
    this.courierId.set((event.target as HTMLSelectElement | null)?.value ?? '');
  }

  protected typeTracking(event: Event): void {
    this.trackingNumber.set((event.target as HTMLInputElement | null)?.value ?? '');
  }

  protected typeAcceptLateNote(event: Event): void {
    this.acceptLateNote.set((event.target as HTMLTextAreaElement | null)?.value ?? '');
  }

  protected typeCancelReason(event: Event): void {
    this.cancelReason.set((event.target as HTMLTextAreaElement | null)?.value ?? '');
  }

  protected typeNote(event: Event): void {
    this.internalNote.set((event.target as HTMLTextAreaElement | null)?.value ?? '');
  }

  protected reload(): void {
    this.loaded = '';
    this.ensureLoaded(this.orderPublicId());
  }

  /**
   * Applies one transition.
   *
   * **The busy guard is here and not on a `disabled` attribute**, which is the whole point: `[disabled]`
   * on a button whose state changes under the user throws focus to `<body>` (#109, six instances), so
   * every control carries `aria-disabled` and the handler refuses the activation.
   */
  protected act(segment: OrderTransitionSegment, body: Record<string, unknown>): void {
    if (this.isBusy()) {
      return;
    }

    this.begin();

    this.api.transition(this.orderPublicId(), segment, body).subscribe({
      next: (result) => {
        this.announce(`${result.orderNumber} is now ${result.statusLabel.toLowerCase()}.`);
        this.refresh();
      },
      error: (error: unknown) => this.fail(error),
    });
  }

  protected submitDispatch(event: Event, detail: AdminOrderDetail): void {
    event.preventDefault();

    if (this.isBusy()) {
      return;
    }

    const courier = this.courierId().trim();
    const tracking = this.trackingNumber().trim();

    // Checked here as well as on the server, because a round trip to be told the box is empty is a round
    // trip. The server's check is the one that matters: doc 08 §4.1 makes both mandatory inside the
    // transaction, so a client that skipped this is refused rather than obeyed.
    if (courier.length === 0 || tracking.length === 0) {
      this.dispatchError.set(
        courier.length === 0
          ? 'Choose the courier carrying this order.'
          : 'Enter the tracking number the courier gave you. The customer has no way to follow the parcel '
            + 'without it.',
      );

      // The message is in an element with role="alert", so it is announced — but the live region is also
      // updated, because a staff member who pressed the button twice needs to hear it both times.
      this.announce(this.dispatchError()!);
      return;
    }

    this.dispatchError.set(null);

    this.act(detail.actions.canEditDispatch ? 'dispatch' : 'mark-shipped', {
      courierId: Number(courier),
      trackingNumber: tracking,
    });
  }

  /**
   * Saves the internal note.
   *
   * **`internalNote` is always sent, even when it is empty**, and empty is sent as `null` to clear it. The
   * endpoint refuses a body that leaves the key out rather than reading its absence as a clear, which is
   * the distinction four defects in this project came from collapsing.
   */
  protected saveNote(): void {
    if (this.isBusy()) {
      return;
    }

    const note = this.internalNote().trim();

    this.begin();

    this.api
      .transition(this.orderPublicId(), 'internal-note', {
        internalNote: note.length === 0 ? null : note,
      })
      .subscribe({
        next: () => {
          this.announce(note.length === 0 ? 'The internal note was cleared.' : 'The internal note is saved.');
          this.refresh();
        },
        error: (error: unknown) => this.fail(error),
      });
  }

  /**
   * Cancels an unpaid order (doc 11 P10 flow A).
   *
   * **The reason is checked here as well as on the server, because a round trip to be told the box is
   * empty is a round trip.** The server's check is the one that matters: the use case refuses a blank
   * `cancelReason` with its own sentence, so a client that skipped this is refused rather than obeyed.
   *
   * It goes through `act`, so the busy guard, the refusal classifier, the re-read and the focus re-homing
   * are the same ones every other transition on this screen uses — and the re-homing matters here more
   * than anywhere: a successful cancel destroys this very form, which is the node that had focus.
   */
  protected submitCancel(event: Event): void {
    event.preventDefault();

    if (this.isBusy()) {
      return;
    }

    const reason = this.cancelReason().trim();

    if (reason.length === 0) {
      this.cancelError.set(
        'Say why this order is being cancelled. It is kept on the order and read back to the customer if '
        + 'they ask.',
      );

      // The message is in an element with role="alert", so it is announced — but the live region is
      // updated too, because a staff member who pressed the button twice needs to hear it both times
      // (ADM-22).
      this.announce(this.cancelError()!);
      return;
    }

    this.cancelError.set(null);
    this.act('cancel', { cancelReason: reason });
  }

  /**
   * Accepts a bank transfer that arrived after the order expired (doc 11 P7 step 8, issue #124).
   *
   * It posts to the bank-transfer route W3-03 already built, tested and verified. What was missing was a
   * screen that could reach it — and when the stock re-check refuses, the short lines it answers with are
   * rendered above with the product, colour, size and SKU the customer bought under, because a staff
   * member deciding whether to refund somebody cannot act on a variant id.
   */
  protected acceptLate(): void {
    if (this.isBusy()) {
      return;
    }

    // **The accept-late box, not the internal note.** See `acceptLateNote` for what sending the wrong one
    // cost.
    const note = this.acceptLateNote().trim();

    this.begin();

    this.api.acceptLatePayment(this.orderPublicId(), note.length === 0 ? null : note).subscribe({
      next: () => {
        this.announce('The late payment is accepted. The order is paid and a new invoice number is issued.');
        this.refresh();
      },
      error: (error: unknown) => this.fail(error),
    });
  }

  private ensureLoaded(token: string): void {
    if (this.loaded === token) {
      return;
    }

    this.loaded = token;
    this.isLoading.set(true);
    this.problem.set(null);

    this.api.couriers().subscribe({
      next: (list) => {
        this.couriers.set(list);

        // **Seeded again here, because the two reads race.** The courier list and the order are separate
        // requests; whichever lands first, the select's value has to end up matching the order's own
        // dispatch. Only an *empty* box is filled in, so a staff member who has already chosen a different
        // courier while this was in flight does not have their choice overwritten.
        if (this.courierId().length === 0 && this.order() !== null) {
          this.courierId.set(this.courierIdFor(this.order()!));
        }
      },

      // A failed courier list is not worth interrupting the staff member for: every other panel on the
      // screen still works, and the dispatch form's own validation refuses an empty choice. The server
      // refuses a dispatch with no courier regardless, so nothing can be shipped blind because of this.
      error: () => undefined,
    });

    this.read();
  }

  private read(then?: () => void): void {
    this.api.order(this.orderPublicId()).subscribe({
      next: (detail) => {
        this.order.set(detail);
        this.isLoading.set(false);
        this.isBusy.set(false);

        // The form is seeded from the server's answer rather than left as the staff member typed it: after
        // a correction the boxes have to show what is now stored, and after a failed attempt they have to
        // show what is still stored. The one case this deliberately overwrites is a half-typed tracking
        // number interrupted by someone else's transition, and the alternative — keeping it — would mean
        // the boxes disagreed with the panel above them.
        this.courierId.set(this.courierIdFor(detail));
        this.trackingNumber.set(detail.dispatch?.trackingNumber ?? '');
        this.internalNote.set(detail.internalNote ?? '');

        // Cleared on a re-read: it is a reason for one decision, not a field of the order, and leaving it
        // filled in after a successful acceptance would invite it being sent again with the next one.
        this.acceptLateNote.set('');

        // Same rule, and the same reason: a cancel reason is about the one decision. After a *refused*
        // cancel the box is cleared too, which is deliberate — the refusal means the order has moved, so
        // whatever the sentence was about is no longer the situation.
        this.cancelReason.set('');

        then?.();
      },
      error: (error: unknown) => {
        this.isLoading.set(false);
        this.isBusy.set(false);
        const failure = toCatalogFailure(error);
        this.problem.set(failure);
        this.announce(failure.message);
      },
    });
  }

  /**
   * The id of the courier this order shipped with, from the dispatch itself.
   *
   * **Not matched on the snapshotted name, which is what the first version did.** The name is a
   * placement-time snapshot by design — a past dispatch note has to keep reading the same after a rename
   * — so matching it against the live list came up empty the moment somebody renamed a carrier, leaving
   * the correction form unselected on an order that plainly had a courier. A null id means the row has
   * actually been deleted, and the form is then unselected for a reason the staff member can act on.
   */
  private courierIdFor(detail: AdminOrderDetail): string {
    const courierId = detail.dispatch?.courierId;

    return courierId === undefined || courierId === null ? '' : String(courierId);
  }

  private refresh(): void {
    const focused = this.document.activeElement as HTMLElement | null;

    this.read(() => this.rehomeFocusIfLost(focused));
  }

  private begin(): void {
    this.isBusy.set(true);
    this.problem.set(null);
    this.refusal.set(null);
    this.shortLines.set([]);
    this.dispatchError.set(null);
    this.cancelError.set(null);
    this.announce('Working…');
  }

  /**
   * Turns a failure into something the staff member can act on.
   *
   * **A refused transition is classified separately from a broken request**, because the fields that
   * matter — the code and the order's *current* status — are the ones a generic classifier throws away.
   * W3-03's `ShortStock` defect was exactly this: a screen refused with no state went back to offering an
   * action that could never succeed.
   */
  private fail(error: unknown): void {
    this.isBusy.set(false);

    const shortStock = toShortStock(error);

    if (shortStock !== null) {
      this.shortLines.set(shortStock);
      this.announce(
        'Some items have been sold since this order expired, so the late payment was not accepted.',
      );

      // Re-read, because the refusal rolled the transaction back and the order's state on screen is the
      // one thing that must not be guessed from a failure.
      this.refresh();
      return;
    }

    const refused = toOrderTransitionRefusal(error);

    if (refused !== null) {
      this.refusal.set(refused);
      this.announce(refused.message);
      this.refresh();
      return;
    }

    const failure = toCatalogFailure(error);
    this.problem.set(failure);
    this.announce(failure.message);
  }

  /** @see OrdersListPage.rehomeFocusIfLost — the same rule, and the same reason. */
  private rehomeFocusIfLost(focused: HTMLElement | null): void {
    afterNextRender(
      () => {
        if (focused !== null && focused.isConnected) {
          return;
        }

        this.document
          .querySelector<HTMLElement>('[data-testid="order-said"]')
          ?.focus({ preventScroll: true });
      },
      { injector: this.injector },
    );
  }

  /** @see OrdersListPage.announce — a repeated identical announcement has to re-fire (ADM-22). */
  private announce(message: string): void {
    if (this.said() !== message) {
      this.said.set(message);
      return;
    }

    this.said.set('');
    afterNextRender(() => this.said.set(message), { injector: this.injector });
  }
}

/** One line a late acceptance could not re-acquire, as W3-03's refusal already names them. */
interface ShortStockLine {
  readonly sku: string;
  readonly productName: string;
  readonly colourName: string;
  readonly sizeLabel: string;
  readonly needed: number;
  readonly available: number;
}

/**
 * Reads the `short_stock` refusal's lines, or null when this is not one.
 *
 * **Checked rather than cast through**: a problem document is the payload most likely to arrive in an
 * unexpected shape, because every layer in front of it can produce one.
 */
function toShortStock(error: unknown): readonly ShortStockLine[] | null {
  const refusal = toOrderTransitionRefusal(error);

  if (refusal === null || refusal.code !== 'short_stock') {
    return null;
  }

  const body = ((error as { error?: unknown }).error ?? null) as Record<string, unknown> | null;
  const lines = body === null ? null : body['shortLines'];

  if (!Array.isArray(lines)) {
    // The code said short stock and the lines did not arrive. Reported as a refusal rather than silently
    // dropped — the caller's generic branch then shows the server's own sentence, which still says what
    // happened.
    return null;
  }

  return lines.filter(
    (line): line is ShortStockLine => line !== null && typeof line === 'object' && 'sku' in line,
  );
}
