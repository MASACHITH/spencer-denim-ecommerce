import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  RESPONSE_INIT,
  computed,
  inject,
  signal,
} from '@angular/core';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { Meta, Title } from '@angular/platform-browser';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { BRAND, brandPageTitle } from '@spencer-denim/shared';

import { formatLkr } from '../../core/catalog/money';
import { CheckoutApi, checkoutErrorMessage, checkoutFailure } from '../../core/checkout/checkout-api';
import {
  OrderConfirmationDto,
  formatColomboDateTime,
} from '../../core/checkout/checkout.model';
import { RobotsMeta } from '../../core/platform/robots-meta';
import { ServerHandover } from '../../core/platform/server-handover';

/** Where the server leaves the order it rendered, for the browser that hydrates the page. */
const HANDOVER_KEY = 'order';

/** A token that names no order gets a real 404, the same as a mistyped product slug. */
const NOT_FOUND_STATUS = 404;

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
 * **No bank details, no reference to quote, no "upload your slip", no countdown.** Doc 11 P6 step 10
 * puts all of that on this page for a bank transfer, and all of it is doc 11 P7 — PAY-01, W3-03's step 3.
 * What this page can say truthfully today is that the order exists, that it is waiting for payment, and
 * when the hold lapses. It says exactly that and promises nothing it does not have. **It also does not
 * claim an email has been sent**, because nothing sends one: `ops.EmailOutbox` and ACC-03's templates are
 * W3-06.
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
                  <!-- What is true today and nothing more: the order is held until this time. The account
                       to pay into, the reference to quote and the slip upload are doc 11 P7 (W3-03 step 3)
                       and are not promised here. -->
                  <span class="order__block order__muted">
                    Awaiting payment. We hold your items until
                    {{ dueAt(placed) }}.
                  </span>
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
}
