import {
  ChangeDetectionStrategy,
  Component,
  Injector,
  afterNextRender,
  computed,
  effect,
  inject,
  input,
  signal,
} from '@angular/core';
import { DOCUMENT } from '@angular/common';
import { RouterLink } from '@angular/router';

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
 * **Nothing here promises an email, a notification or an acknowledgement.** `ops.EmailOutbox` is specified
 * (doc 08 §3.8) and unbuilt; W3-06 owns it. Doc 11 P9 step 5 queues `OrderShipped` and nothing can send
 * it, so the dispatch panel says in words that the customer is not told — which is also doc 11 P9's own
 * instruction about a corrected tracking number. The shopper's own order page already shows a status
 * timeline, which is the honest route.
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
        </dl>

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

        @if (detail.actions.canAcceptLatePayment) {
          <p class="adm-panel__hint" data-testid="accept-late-hint">
            The money arrived after the deadline. Accepting re-checks stock for every item: if everything
            is still available the order is paid with today's date and a new invoice number; if anything
            has sold, nothing changes and you are told which items.
          </p>
        }

        @if (detail.actions.canMarkShipped || detail.actions.canEditDispatch) {
          <form class="order-dispatch" (submit)="submitDispatch($event, detail)">
            <h3 class="adm-label">
              {{ detail.actions.canEditDispatch ? 'Correct the dispatch' : 'Dispatch' }}
            </h3>
            <div class="adm-fields">
              <label class="adm-field">
                <span class="adm-label">Courier</span>
                <select
                  class="adm-input"
                  name="courierId"
                  [value]="courierId()"
                  data-testid="courier"
                  (change)="pickCourier($event)"
                >
                  <option value="">Choose a courier…</option>
                  @for (courier of activeCouriers(); track courier.id) {
                    <option [value]="courier.id">{{ courier.name }}</option>
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
                <dd>{{ cash(line.unitPrice) }}</dd>
                <dt>Line total</dt>
                <dd>{{ cash(line.lineTotal) }}</dd>
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
          <dd>{{ cash(detail.money.itemsSubtotal) }}</dd>
          @if (detail.money.markdownDiscountTotal > 0) {
            <dt>Markdown</dt>
            <dd>−{{ cash(detail.money.markdownDiscountTotal) }}</dd>
          }
          @if (detail.money.campaignDiscountTotal > 0) {
            <dt>Sale campaign</dt>
            <dd>−{{ cash(detail.money.campaignDiscountTotal) }}</dd>
          }
          @if (detail.money.promoCodeText) {
            <dt>Promo code {{ detail.money.promoCodeText }}</dt>
            <dd>−{{ cash(detail.money.promoDiscountAmount) }}</dd>
          }
          <dt>Delivery</dt>
          <dd>{{ cash(detail.money.deliveryFeeAmount) }}</dd>
          @if (detail.money.deliveryDiscountAmount > 0) {
            <dt>Delivery discount</dt>
            <dd>−{{ cash(detail.money.deliveryDiscountAmount) }}</dd>
          }
          <dt class="order-money__total">Total</dt>
          <dd class="order-money__total" data-testid="order-total">
            {{ cash(detail.money.grandTotal) }}
          </dd>
          @if (detail.money.refundedAmount > 0) {
            <dt>Refunded</dt>
            <dd>{{ cash(detail.money.refundedAmount) }}</dd>
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
                <dd>{{ cash(payment.amount) }}</dd>
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
                <p class="order-line__name">{{ cash(refund.amount) }} — {{ refund.status }}</p>
                <dl class="order-facts">
                  <dt>How</dt>
                  <dd>{{ refund.method }}</dd>
                  <dt>Why</dt>
                  <dd>{{ refund.reason }}</dd>
                  <dt>Stock put back</dt>
                  <dd>{{ refund.restockItems ? 'Yes' : 'No' }}</dd>
                  <dt>By</dt>
                  <dd>{{ refund.createdByUserEmail ?? 'the payment provider' }}</dd>
                  <dt>When</dt>
                  <dd>{{ when(refund.createdAtUtc) }}</dd>
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
    .order-dispatch {
      display: grid;
      gap: var(--space-1);
      padding-top: var(--space-2);
      border-top: 1px solid var(--color-border);
    }
  `,
})
export class OrderDetailPage {
  /** The order's public token, from the route. */
  readonly orderPublicId = input.required<string>();

  private readonly api = inject(AdminOrderApi);
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
  protected readonly dispatchError = signal<string | null>(null);

  /**
   * Only the couriers a new dispatch may choose, **plus the one this order already has**.
   *
   * The second half matters when correcting a tracking number on an order shipped with a carrier that has
   * since been deactivated: without it the select would silently drop the current value, the form would
   * come up empty, and saving would move the order to a different courier by accident.
   */
  protected readonly activeCouriers = computed(() => {
    const current = this.order()?.dispatch?.courierName;

    return this.couriers().filter(
      (courier) => courier.isActive || (current !== undefined && courier.name === current),
    );
  });

  private loaded = '';

  constructor() {
    // **An effect rather than a constructor read, because the token is a signal input.**
    // `input.required` is not readable in the constructor at all — and, just as importantly, the router
    // reuses this component when a staff member moves from one order to another, so a one-off read would
    // leave the second order showing the first one's data. `ensureLoaded` is idempotent per token, so the
    // effect re-running for any other reason costs nothing.
    effect(() => this.ensureLoaded(this.orderPublicId()));
  }

  protected cash(amount: number): string {
    return money(amount);
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

  protected pickCourier(event: Event): void {
    this.courierId.set((event.target as HTMLSelectElement | null)?.value ?? '');
  }

  protected typeTracking(event: Event): void {
    this.trackingNumber.set((event.target as HTMLInputElement | null)?.value ?? '');
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

    const note = this.internalNote().trim();

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
   * The id of the courier this order shipped with, matched by the **snapshotted name**.
   *
   * The order keeps `CourierName` as a snapshot and the detail contract does not publish `CourierId` —
   * deliberately, because the name is what a past dispatch note has to keep reading. The match is by name
   * because `UX_Couriers_Name` makes it unique; a courier renamed since the dispatch therefore comes up
   * unselected, which is the honest outcome: the staff member picks again rather than having a different
   * carrier chosen for them.
   */
  private courierIdFor(detail: AdminOrderDetail): string {
    const name = detail.dispatch?.courierName;

    if (name === undefined) {
      return '';
    }

    const match = this.couriers().find((courier) => courier.name === name);

    return match === undefined ? '' : String(match.id);
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
