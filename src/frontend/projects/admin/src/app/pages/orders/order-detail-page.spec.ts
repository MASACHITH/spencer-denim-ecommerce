import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { of } from 'rxjs';

import { OrderDetailPage } from './order-detail-page';
import { AdminOrderApi } from '../../core/orders/admin-order-api';
import {
  AdminCourier,
  AdminOrderDetail,
  AdminOrderPayment,
} from '../../core/orders/admin-order.model';

const ORDER = '44444444-4444-4444-8444-444444444444';

function payment(overrides: Partial<AdminOrderPayment> = {}): AdminOrderPayment {
  return {
    providerCode: 'BankTransfer',
    status: 'Succeeded',
    isOrderSettlement: true,
    amount: 15_596.6,
    currency: 'LKR',
    providerAmount: null,
    providerCurrency: null,
    payerReference: 'CBC-778812',
    slipPublicId: '66666666-6666-4666-8666-666666666666',
    slipContentType: 'image/jpeg',
    slipFileName: 'slip.jpg',
    failureCode: null,
    failureMessage: null,
    approvedByUserEmail: 'staff@example.com',
    approvedAtUtc: '2026-10-05T09:00:00Z',
    approvalNote: 'Credit on the statement at 14:02.',
    createdAtUtc: '2026-10-05T04:31:00Z',
    completedAtUtc: '2026-10-05T09:00:00Z',
    ...overrides,
  };
}

/**
 * Every field carries a real value and every collection has something in it.
 *
 * W1-12's blocker survived 1,571 passing tests because every fixture set the columns it cleared to
 * `null`; a fixture whose lines, payments and history are real is the only kind that can fail when a
 * panel stops rendering them.
 */
function detail(overrides: Partial<AdminOrderDetail> = {}): AdminOrderDetail {
  return {
    orderPublicId: ORDER,
    orderNumber: 'SD-2026-000123',
    status: 'Shipped',
    statusLabel: 'Shipped',
    stockState: 'Committed',
    customer: {
      name: 'Nimali Perera',
      email: 'nimali@example.com',
      phone: '0771234567',
      hasAccount: false,
      isAnonymised: false,
    },
    delivery: {
      methodCode: 'STANDARD',
      methodName: 'Standard delivery',
      isPickup: false,
      pickupLocationName: null,
      districtName: 'Colombo',
      zone: 'Colombo',
      estimatedDaysMin: 2,
      estimatedDaysMax: 4,
      recipientName: 'Nimali Perera',
      recipientPhone: '0771234567',
      line1: '14 Galle Road',
      line2: 'Flat 3',
      city: 'Kollupitiya',
      postalCode: '00300',
      landmark: 'Opposite the temple',
    },
    money: {
      currency: 'LKR',
      itemsOriginalTotal: 17_990,
      itemsListTotal: 16_490,
      itemsSubtotal: 15_096.6,
      markdownDiscountTotal: 1_500,
      campaignDiscountTotal: 1_393.4,
      promoCodeText: null,
      promoDiscountAmount: 0,
      deliveryFeeAmount: 500,
      deliveryDiscountAmount: 0,
      grandTotal: 15_596.6,
      refundedAmount: 0,
    },
    lines: [
      {
        lineNumber: 1,
        sku: 'SKU-470',
        productName: 'Slim jeans',
        productCode: 'PRD-470',
        colourName: 'Indigo',
        sizeLabel: '32',
        quantity: 1,
        unitOriginalPrice: 11_990,
        unitListPrice: 10_990,
        unitPrice: 9_990,
        lineSubtotal: 9_990,
        promoDiscountAllocated: 0,
        lineTotal: 9_990,
        refundedQuantity: 0,
      },
    ],
    payments: [payment()],
    refunds: [],
    history: [
      {
        fromStatus: null,
        toStatus: 'PendingPayment',
        fromStatusLabel: null,
        toStatusLabel: 'Awaiting payment',
        source: 'Customer',
        changedByUserEmail: null,
        note: 'Order placed',
        occurredAtUtc: '2026-10-05T04:30:00Z',
      },
      {
        fromStatus: 'Shipped',
        toStatus: 'Delivered',
        fromStatusLabel: 'Shipped',
        toStatusLabel: 'Delivered',
        source: 'SystemJob',
        changedByUserEmail: null,
        note: 'Marked delivered automatically, because nobody confirmed delivery',
        occurredAtUtc: '2026-10-13T06:00:00Z',
      },
    ],
    dispatch: {
      courierId: 4,
      courierName: 'A carrier',
      trackingNumber: 'TRACK-0001',
      trackingUrl: 'https://track.example.test/TRACK-0001',
      shippedAtUtc: '2026-10-06T06:00:00Z',
    },
    actions: {
      canStartProcessing: false,
      canMarkShipped: false,
      canMarkReadyForCollection: false,
      canMarkDelivered: true,
      canEditDispatch: true,
      canAcceptLatePayment: false,
      couriersConfigured: true,
      blockedReason: null,
    },
    customerNote: 'Please leave it with the security desk.',
    internalNote: 'Customer rang on the 8th.',
    cancelReason: null,
    invoiceNumber: 'INV-2026-000045',
    invoiceIssuedAtUtc: '2026-10-05T09:00:00Z',
    placedAtUtc: '2026-10-05T04:30:00Z',
    paymentDueAtUtc: '2026-10-07T04:30:00Z',
    secondsUntilExpiry: null,
    paidAtUtc: '2026-10-05T09:00:00Z',
    shippedAtUtc: '2026-10-06T06:00:00Z',
    deliveredAtUtc: null,
    closedAtUtc: null,
    ...overrides,
  };
}

function courier(overrides: Partial<AdminCourier> = {}): AdminCourier {
  return {
    id: 4,
    name: 'A carrier',
    trackingUrlTemplate: 'https://track.example.test/{trackingNumber}',
    phone: null,
    isActive: true,
    sortOrder: 0,
    ordersShipped: 3,
    ...overrides,
  };
}

/**
 * The order detail (ADM-09, doc 11 P9).
 *
 * **What the client sends and what it then shows is the heart of this file.** The transitions themselves
 * are proved against real SQL Server in `OrderFulfilmentTests` and the payloads in
 * `AdminOrderEndpointTests`; issue #126 is why both layers exist — Playwright never starts the .NET API.
 */
describe('OrderDetailPage (ADM-09)', () => {
  let fixture: ComponentFixture<OrderDetailPage>;
  let element: HTMLElement;
  let http: HttpTestingController;

  async function settle(): Promise<void> {
    fixture.detectChanges();
    await fixture.whenStable();
  }

  async function open(
    payload: AdminOrderDetail = detail(),
    couriers: readonly AdminCourier[] = [courier()],
  ): Promise<void> {
    fixture = TestBed.createComponent(OrderDetailPage);
    element = fixture.nativeElement as HTMLElement;

    await settle();

    http.expectOne(AdminOrderApi.couriersUrl).flush(couriers);
    http.expectOne(`${AdminOrderApi.ordersUrl}/${ORDER}`).flush(payload);

    await settle();
  }

  function click(testId: string): void {
    element.querySelector<HTMLElement>(`[data-testid="${testId}"]`)!.click();
  }

  function answerAntiforgery(): void {
    http
      .match((request) => request.url.includes('antiforgery'))
      .forEach((request) => request.flush({ token: 'token' }));
  }

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        {
          // The route token, the way the three existing form pages read it. `withComponentInputBinding()`
          // is not in this application's router configuration, so a signal `input` would never be
          // populated — which the browser suite found the hard way.
          provide: ActivatedRoute,
          useValue: {
            paramMap: of(convertToParamMap({ orderPublicId: ORDER })),
            snapshot: { paramMap: convertToParamMap({ orderPublicId: ORDER }) },
          },
        },
      ],
    });

    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    http.verify();
  });

  it('shows the lines, the address, the money, the payment and the timeline', async () => {
    await open();

    const text = element.textContent ?? '';

    expect(text).toContain('SD-2026-000123');
    expect(text).toContain('SKU-470');
    expect(text).toContain('Indigo');
    expect(text).toContain('14 Galle Road');
    expect(text).toContain('Opposite the temple');
    expect(element.querySelector('[data-testid="order-total"]')?.textContent).toContain('15,596.60');
    expect(element.querySelector('[data-testid="order-payments"]')?.textContent).toContain(
      'staff@example.com',
    );
    expect(element.querySelector('[data-testid="customer-note"]')?.textContent).toContain(
      'security desk',
    );
  });

  it('renders a System transition as “by the system” rather than a blank', async () => {
    await open();

    // Doc 08 §4 writes a history row for every transition no user made, precisely so the timeline is
    // complete — so a null actor is a fact to render, not a failed lookup to hide.
    expect(element.querySelector('[data-testid="order-history"]')?.textContent).toContain(
      'by the system',
    );
  });

  it('does not render “Awaiting payment → Awaiting payment” for an event that moved nothing', async () => {
    await open(
      detail({
        history: [
          {
            fromStatus: 'PendingPayment',
            toStatus: 'PendingPayment',
            fromStatusLabel: 'Awaiting payment',
            toStatusLabel: 'Awaiting payment',
            source: 'Staff',
            changedByUserEmail: 'staff@example.com',
            note: 'Bank transfer rejected: the amount is short.',
            occurredAtUtc: '2026-10-06T06:00:00Z',
          },
        ],
      }),
    );

    const history = element.querySelector('[data-testid="order-history"]')?.textContent ?? '';

    expect(history).toContain('Awaiting payment');
    expect(history).not.toContain('→');
    expect(history).toContain('the amount is short');
  });

  it('selects the courier by the dispatch’s id, not by its snapshotted name', async () => {
    // The order shipped with "A carrier" and the courier has since been renamed. The snapshot is what the
    // order shows; the id is what the form selects by — matching on the name came up blank here, on an
    // order that plainly had a courier.
    await open(detail(), [courier({ id: 4, name: 'A renamed carrier' })]);

    expect(element.querySelector<HTMLSelectElement>('[data-testid="courier"]')!.value).toBe('4');
    expect(element.textContent).toContain('A carrier');
  });

  it('keeps a deactivated courier in the picker when the order shipped with it', async () => {
    await open(detail(), [courier({ isActive: false })]);

    const options = [
      ...element.querySelectorAll<HTMLOptionElement>('[data-testid="courier"] option'),
    ].map((option) => option.value);

    // Without this the select would silently drop the current value and saving the correction would move
    // the order to a different courier by accident.
    expect(options).toContain('4');
  });

  it('refuses a dispatch with no tracking number, without a request', async () => {
    await open();

    element.querySelector<HTMLInputElement>('[data-testid="tracking-number"]')!.value = '';
    element
      .querySelector<HTMLInputElement>('[data-testid="tracking-number"]')!
      .dispatchEvent(new Event('input'));

    click('submit-dispatch');
    await settle();

    expect(element.querySelector('[data-testid="dispatch-error"]')?.textContent).toContain(
      'tracking number',
    );

    // A round trip to be told a box is empty is a round trip. The server's check is the one that matters.
    http.expectNone(() => true);
  });

  it('always sends internalNote, and sends null to clear it', async () => {
    await open();

    const box = element.querySelector<HTMLTextAreaElement>('[data-testid="internal-note"]')!;
    box.value = '';
    box.dispatchEvent(new Event('input'));

    click('save-note');
    await settle();

    answerAntiforgery();
    await settle();

    const request = http.expectOne(`${AdminOrderApi.ordersUrl}/${ORDER}/internal-note`);

    // **The key is present and its value is null.** The endpoint refuses a body that leaves it out rather
    // than reading its absence as a clear — the distinction four defects in this project came from
    // collapsing — so sending it every time is the client's half of that guarantee.
    expect(request.request.body).toEqual({ internalNote: null });

    request.flush({ orderNumber: 'SD-2026-000123', status: 'Shipped', statusLabel: 'Shipped' });
    await settle();

    http.expectOne(`${AdminOrderApi.ordersUrl}/${ORDER}`).flush(detail({ internalNote: null }));
    await settle();
  });

  it('shows the refusal’s own sentence and the order’s current status', async () => {
    await open(detail({ status: 'Paid', statusLabel: 'Paid', actions: { ...detail().actions, canStartProcessing: true, canMarkDelivered: false, canEditDispatch: false } }));

    click('start-processing');
    await settle();

    answerAntiforgery();
    await settle();

    http.expectOne(`${AdminOrderApi.ordersUrl}/${ORDER}/start-processing`).flush(
      {
        title: 'This order could not be moved',
        detail: 'Somebody has already started processing this order.',
        code: 'order_wrong_status',
        orderStatus: 'Processing',
        orderStatusLabel: 'Processing',
      },
      { status: 409, statusText: 'Conflict' },
    );

    await settle();

    // The refusal re-reads the order, because the state on screen is the one thing that must not be
    // guessed from a failure.
    http.expectOne(`${AdminOrderApi.ordersUrl}/${ORDER}`).flush(
      detail({ status: 'Processing', statusLabel: 'Processing' }),
    );
    await settle();

    const refusal = element.querySelector('[data-testid="order-refusal"]')?.textContent ?? '';

    expect(refusal).toContain('already started processing');
    expect(refusal).toContain('This order is now: Processing.');
  });

  it('names the short lines when a late acceptance is refused by the stock re-check', async () => {
    await open(
      detail({
        status: 'Expired',
        statusLabel: 'Expired',
        payments: [payment({ status: 'Expired', isOrderSettlement: false })],
        actions: {
          ...detail().actions,
          canMarkDelivered: false,
          canEditDispatch: false,
          canAcceptLatePayment: true,
        },
      }),
    );

    click('accept-late');
    await settle();

    answerAntiforgery();
    await settle();

    http.expectOne(`/api/admin/orders/bank-transfers/${ORDER}/accept-late`).flush(
      {
        title: 'Some items are no longer in stock',
        detail: 'This order cannot be paid now.',
        code: 'short_stock',
        orderStatus: 'Expired',
        shortLines: [
          {
            variantId: 470,
            sku: 'SKU-470',
            productName: 'Slim jeans',
            colourName: 'Indigo',
            sizeLabel: '32',
            needed: 1,
            available: 0,
          },
        ],
      },
      { status: 409, statusText: 'Conflict' },
    );

    await settle();

    http.expectOne(`${AdminOrderApi.ordersUrl}/${ORDER}`).flush(detail({ status: 'Expired', statusLabel: 'Expired' }));
    await settle();

    // **Named, not numbered.** A staff member deciding whether to refund somebody cannot act on a variant
    // id — which is what the API used to send and what W3-03's review had changed before this screen
    // existed.
    const short = element.querySelector('[data-testid="short-SKU-470"]')?.textContent ?? '';

    expect(short).toContain('Slim jeans');
    expect(short).toContain('Indigo');
    expect(short).toContain('32');
    expect(short).toContain('need 1');
    expect(short).toContain('0 left');
  });

  /**
   * **The way to the approval queue, from an order found by its number.**
   *
   * Approving money is the bank-transfer queue's, behind `Orders.Approve` (doc 11 P7 step 4), and the
   * decision is deliberately not duplicated here — but a staff member who reached this order by the
   * number the customer quoted should not have to know that screen exists.
   */
  it('points at the approval queue when a transfer is still waiting', async () => {
    await open(
      detail({
        status: 'PendingPayment',
        statusLabel: 'Awaiting payment',
        secondsUntilExpiry: 5 * 60 * 60,
        invoiceNumber: null,
        payments: [payment({ status: 'Pending', isOrderSettlement: false })],
        actions: {
          ...detail().actions,
          canMarkDelivered: false,
          canEditDispatch: false,
        },
      }),
    );

    const hint = element.querySelector('[data-testid="awaiting-review"]');

    expect(hint?.textContent).toContain('waiting to be reviewed');
    expect(hint?.querySelector('a')?.getAttribute('href')).toBe('/orders/bank-transfers');

    // And no invoice is offered, because the number is issued at payment.
    expect(element.querySelector('[data-testid="print-invoice"]')).toBeNull();
    expect(element.querySelector('[data-testid="no-invoice"]')?.textContent).toContain(
      'no invoice yet',
    );
  });

  it('does not point at the approval queue once the order is paid', async () => {
    await open();

    expect(element.querySelector('[data-testid="awaiting-review"]')).toBeNull();
  });

  /**
   * <b>The currency the row carries, not an assumed symbol.</b>
   *
   * `money()` prefixed `Rs.` unconditionally while every money contract on this screen supplies a
   * `currency` — doc 08 §3.5 gives them one because a PayPal capture is taken in USD (D1). The symbol is
   * LKR-only by check constraint today, so this test drives the path with a widened value: that is what
   * makes it able to fail, since a fixture using LKR everywhere would agree with a hard-coded `Rs.`.
   */
  it('formats an amount in the currency the row carries', async () => {
    await open(
      detail({
        money: { ...detail().money, currency: 'USD', grandTotal: 53.55 },
        payments: [payment({ amount: 53.55, currency: 'USD' })],
      }),
    );

    // Doc 11 P10 step 6's own wording — "USD 53.55" — which is what a staff member reads out.
    expect(element.querySelector('[data-testid="order-total"]')?.textContent).toContain('USD 53.55');
    expect(element.querySelector('[data-testid="order-payments"]')?.textContent).toContain('USD 53.55');
    expect(element.querySelector('[data-testid="order-total"]')?.textContent).not.toContain('Rs.');
  });

  /**
   * <b>The foreign figure is the one that genuinely differs, and it reached no screen.</b>
   *
   * `Payments.Currency` is LKR by check constraint; doc 08 §3.5 puts the amount the provider was actually
   * asked for in `ProviderAmount` / `ProviderCurrency`, and doc 11 P10 step 6 tells staff to read it out.
   * It is null for every payment this build can create, so the panel shows it only when it is there.
   */
  it('shows what the provider was charged, and only when there is one', async () => {
    await open(detail());
    expect(element.textContent).not.toContain('Charged by the provider');

    await open(
      detail({
        payments: [payment({ providerAmount: 53.55, providerCurrency: 'USD' })],
      }),
    );

    const payments = element.querySelector('[data-testid="order-payments"]')?.textContent ?? '';

    expect(payments).toContain('Charged by the provider');
    expect(payments).toContain('USD 53.55');

    // The LKR amount is still there beside it: both are facts about the same payment and a staff member
    // reconciling a statement needs the rupee figure as well.
    expect(payments).toContain('Rs. 15,596.60');
  });

  it('shows what a refund actually sent back when the provider sent it', async () => {
    await open(
      detail({
        refunds: [
          {
            amount: 15_596.6,
            currency: 'LKR',
            providerAmount: 53.55,
            method: 'ProviderApi',
            status: 'Completed',
            reason: 'Customer changed their mind.',
            restockItems: true,
            createdByUserEmail: 'staff@example.com',
            createdAtUtc: '2026-10-09T09:00:00Z',
            completedAtUtc: '2026-10-09T09:05:00Z',
          },
        ],
      }),
    );

    const refunds = element.querySelector('[data-testid="order-refunds"]')?.textContent ?? '';

    expect(refunds).toContain('Sent by the provider');
    expect(refunds).toContain('USD 53.55');
  });

  it('says the customer’s details were removed when they have been', async () => {
    await open(
      detail({
        customer: {
          name: 'Removed',
          email: 'removed@example.invalid',
          phone: null,
          hasAccount: false,
          isAnonymised: true,
        },
      }),
    );

    // A PDPA removal is a real state, not missing data, so the screen says so rather than drawing blanks.
    expect(element.querySelector('[data-testid="anonymised"]')?.textContent).toContain(
      'asked for their details to be removed',
    );
    expect(element.textContent).toContain('not recorded');
  });
});
