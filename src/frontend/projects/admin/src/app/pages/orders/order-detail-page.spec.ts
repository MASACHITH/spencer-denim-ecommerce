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
      region: null,
      countryCode: 'LK',
      countryName: 'Sri Lanka',
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
      canCancel: false,
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

  /**
   * Doc 11 P10 flow A: the panel appears only on an unpaid order, and the reason is required.
   */
  function unpaid(): AdminOrderDetail {
    return detail({
      status: 'PendingPayment',
      statusLabel: 'Awaiting payment',
      stockState: 'Reserved',
      invoiceNumber: null,
      invoiceIssuedAtUtc: null,
      paidAtUtc: null,
      shippedAtUtc: null,
      deliveredAtUtc: null,
      dispatch: null,
      secondsUntilExpiry: 90_000,
      payments: [payment({ status: 'Pending', isOrderSettlement: false })],
      actions: {
        ...detail().actions,
        canMarkDelivered: false,
        canEditDispatch: false,
        canCancel: true,
      },
    });
  }

  it('offers Cancel the order only when the server says it can be cancelled', async () => {
    // Doc 11 P10's table: a paid order is refunded, never cancelled — and there is no refund screen, so
    // the honest answer is no control at all rather than one that can only fail.
    await open();

    expect(element.querySelector('[data-testid="cancel-order"]')).toBeNull();
    expect(element.querySelector('[data-testid="cancel-reason-input"]')).toBeNull();

    await open(unpaid());

    expect(element.querySelector('[data-testid="cancel-order"]')).not.toBeNull();
    expect(element.querySelector('[data-testid="cancel-reason-input"]')).not.toBeNull();
  });

  it('refuses a blank reason without a round trip, and says so again on a second press', async () => {
    await open(unpaid());

    click('cancel-order');
    await settle();

    // No request at all: `http.verify()` in afterEach is what proves it, and the message is the thing a
    // staff member reads.
    expect(element.querySelector('[data-testid="cancel-error"]')?.textContent).toContain('Say why');

    // ADM-22: the live region has to re-fire an identical sentence, because somebody who pressed twice
    // needs to hear it both times. The region is cleared and restored inside the same change-detection
    // pass (afterNextRender), so it is watched rather than sampled — reading it afterwards always finds
    // the sentence back, and a before/after comparison would pass against a component that never cleared.
    //
    // `record.oldValue` with `characterDataOldValue`, which is `couriers-page.spec.ts`' pattern and not a
    // preference: a callback can batch both mutations, so reading `textContent` inside it sees the
    // restored sentence twice and the blank never — which is how the first version of this test failed
    // against a component that was behaving correctly.
    const said = element.querySelector('[data-testid="order-said"]')!;
    const oldValues: (string | null)[] = [];
    const observer = new MutationObserver((records) => {
      for (const record of records) {
        oldValues.push(record.oldValue);
      }
    });
    observer.observe(said, {
      childList: true,
      characterData: true,
      characterDataOldValue: true,
      subtree: true,
    });

    try {
      click('cancel-order');
      await settle();
    } finally {
      observer.disconnect();
    }

    // Without the clear-and-restore, setting a signal to the value it already holds produces **no DOM
    // mutation whatsoever** — and no mutation is precisely the silence a staff member gets on their
    // second press.
    expect(oldValues.length).toBeGreaterThan(0);
    expect(oldValues).toContain('');
    expect(said.textContent).toContain('Say why');
  });

  it('cancels with the reason, and the reason is not the internal note', async () => {
    await open(unpaid());

    // The internal note is filled in from the server's answer, and a staff member typing in it must not
    // have that text become the audited reason an order was closed — which is the defect W3-04 shipped on
    // the accept-late form.
    const reason = element.querySelector<HTMLTextAreaElement>('[data-testid="cancel-reason-input"]')!;
    reason.value = 'Customer telephoned to cancel.';
    reason.dispatchEvent(new Event('input'));
    await settle();

    click('cancel-order');
    await settle();

    answerAntiforgery();
    await settle();

    const request = http.expectOne(`${AdminOrderApi.ordersUrl}/${ORDER}/cancel`);

    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({ cancelReason: 'Customer telephoned to cancel.' });

    request.flush({
      orderNumber: 'SD-2026-000123',
      status: 'Cancelled',
      statusLabel: 'Cancelled',
    });

    await settle();

    // It re-reads the order — and **only** the order: `refresh()` calls `read()`, not `ensureLoaded`,
    // so the courier list is not fetched again. This is the only way the panel can disappear and the
    // reason can appear beside the status: the screen never redraws from what it sent.
    http.expectOne(`${AdminOrderApi.ordersUrl}/${ORDER}`).flush(
      detail({
        status: 'Cancelled',
        statusLabel: 'Cancelled',
        stockState: 'Released',
        cancelReason: 'Customer telephoned to cancel.',
        closedAtUtc: '2026-10-08T06:00:00Z',
        dispatch: null,
        actions: {
          ...detail().actions,
          canMarkDelivered: false,
          canEditDispatch: false,
          canCancel: false,
        },
      }),
    );

    await settle();

    expect(element.querySelector('[data-testid="cancel-order"]')).toBeNull();
    expect(element.querySelector('[data-testid="cancel-reason"]')?.textContent).toContain(
      'Customer telephoned to cancel.',
    );
    expect(element.querySelector('[data-testid="order-status"]')?.textContent).toContain('Cancelled');
  });

  /**
   * #109: destroying the focused node throws focus to the document body.
   *
   * A successful cancel removes the form that was pressed, so there is nothing left to focus — and the
   * screen has to put focus somewhere a screen reader will read, which is the live region.
   *
   * It re-uses `rehomeFocusIfLost`, which every other action on this screen already goes through, so what
   * this test adds is that the *cancel* path reaches it — and that is worth its own test precisely because
   * cancel is the only action here that destroys the control that was pressed rather than replacing it
   * with another. The scheduling is `afterNextRender`; I have not measured whether a microtask would be
   * too early on this screen, and the reason to use `afterNextRender` is that the node's removal happens
   * in a render rather than in a task.
   */
  it('re-homes focus when the cancel it applied destroys the control that was pressed', async () => {
    await open(unpaid());

    const reason = element.querySelector<HTMLTextAreaElement>('[data-testid="cancel-reason-input"]')!;
    reason.value = 'They changed their mind.';
    reason.dispatchEvent(new Event('input'));
    await settle();

    const button = element.querySelector<HTMLElement>('[data-testid="cancel-order"]')!;
    button.focus();
    expect(document.activeElement).toBe(button);

    click('cancel-order');
    await settle();

    answerAntiforgery();
    await settle();

    http.expectOne(`${AdminOrderApi.ordersUrl}/${ORDER}/cancel`).flush({
      orderNumber: 'SD-2026-000123',
      status: 'Cancelled',
      statusLabel: 'Cancelled',
    });

    await settle();

    http.expectOne(`${AdminOrderApi.ordersUrl}/${ORDER}`).flush(
      detail({
        status: 'Cancelled',
        statusLabel: 'Cancelled',
        stockState: 'Released',
        dispatch: null,
        actions: {
          ...detail().actions,
          canMarkDelivered: false,
          canEditDispatch: false,
          canCancel: false,
        },
      }),
    );

    await settle();
    await fixture.whenStable();

    expect(button.isConnected).toBe(false);
    expect(document.activeElement).toBe(element.querySelector('[data-testid="order-said"]'));
  });

  /**
   * **A second press while the first is in flight sends nothing** (#109's other half).
   *
   * The button carries `aria-disabled` rather than `disabled`, so the guard `disabled` would have given
   * has to be in the handler — and two cancels for one order would be two audit rows and two history
   * rows for one decision even though the second claim affects no rows.
   */
  it('refuses a second press while the cancel is in flight', async () => {
    await open(unpaid());

    const reason = element.querySelector<HTMLTextAreaElement>('[data-testid="cancel-reason-input"]')!;
    reason.value = 'Customer rang.';
    reason.dispatchEvent(new Event('input'));
    await settle();

    const button = element.querySelector<HTMLElement>('[data-testid="cancel-order"]')!;

    button.click();
    await settle();

    expect(button.getAttribute('aria-disabled')).toBe('true');
    expect(button.hasAttribute('disabled')).toBe(false);

    button.click();
    button.click();
    await settle();

    answerAntiforgery();
    await settle();

    const pending = http.match(`${AdminOrderApi.ordersUrl}/${ORDER}/cancel`);

    expect(pending).toHaveLength(1);

    pending[0].flush({
      orderNumber: 'SD-2026-000123',
      status: 'Cancelled',
      statusLabel: 'Cancelled',
    });

    await settle();

    http.expectOne(`${AdminOrderApi.ordersUrl}/${ORDER}`).flush(
      detail({
        status: 'Cancelled',
        statusLabel: 'Cancelled',
        stockState: 'Released',
        dispatch: null,
        actions: {
          ...detail().actions,
          canMarkDelivered: false,
          canEditDispatch: false,
          canCancel: false,
        },
      }),
    );

    await settle();
  });

  it('shows the server’s refusal when somebody else closed the order first', async () => {
    await open(unpaid());

    const reason = element.querySelector<HTMLTextAreaElement>('[data-testid="cancel-reason-input"]')!;
    reason.value = 'Customer rang.';
    reason.dispatchEvent(new Event('input'));
    await settle();

    click('cancel-order');
    await settle();

    answerAntiforgery();
    await settle();

    http.expectOne(`${AdminOrderApi.ordersUrl}/${ORDER}/cancel`).flush(
      {
        title: 'This order could not be moved',
        detail: 'The payment deadline for this order has passed, so it closed on its own.',
        code: 'order_not_cancellable',
        orderStatus: 'Expired',
        orderStatusLabel: 'Expired',
      },
      { status: 409, statusText: 'Conflict' },
    );

    await settle();

    http.expectOne(`${AdminOrderApi.ordersUrl}/${ORDER}`).flush(
      detail({
        status: 'Expired',
        statusLabel: 'Expired',
        stockState: 'Released',
        dispatch: null,
        actions: {
          ...detail().actions,
          canMarkDelivered: false,
          canEditDispatch: false,
          canCancel: false,
        },
      }),
    );

    await settle();

    const refusal = element.querySelector('[data-testid="order-refusal"]')?.textContent ?? '';

    expect(refusal).toContain('closed on its own');
    expect(refusal).toContain('Expired');
    expect(element.querySelector('[data-testid="cancel-order"]')).toBeNull();
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

  /**
   * <b>Accepting a late payment sends its own reason, not the internal note.</b>
   *
   * This screen used to send whatever was in the internal-note textarea as the accept-late reason — which
   * reaches `Payments.ApprovalNote`, is audited, and is read back to a customer who asks why their money
   * was taken a week late. A staff member writing a note to themselves about a telephone call had no way
   * to know it was about to become a statement about money.
   */
  it('sends the accept-late reason, and never the internal note', async () => {
    await open(
      detail({
        status: 'Expired',
        statusLabel: 'Expired',
        internalNote: 'Rang the customer; they said they paid on Tuesday.',
        payments: [payment({ status: 'Expired', isOrderSettlement: false })],
        actions: {
          ...detail().actions,
          canMarkDelivered: false,
          canEditDispatch: false,
          canAcceptLatePayment: true,
        },
      }),
    );

    // The internal note is on screen and filled in, which is what made the old coupling invisible.
    expect(
      element.querySelector<HTMLTextAreaElement>('[data-testid="internal-note"]')!.value,
    ).toContain('paid on Tuesday');

    const reason = element.querySelector<HTMLTextAreaElement>('[data-testid="accept-late-note"]')!;
    reason.value = 'Credit on the statement dated the 9th.';
    reason.dispatchEvent(new Event('input'));
    await settle();

    click('accept-late');
    await settle();

    answerAntiforgery();
    await settle();

    const request = http.expectOne(`/api/admin/orders/bank-transfers/${ORDER}/accept-late`);

    expect(request.request.body).toEqual({ note: 'Credit on the statement dated the 9th.' });

    request.flush({ orderNumber: 'SD-2026-000123', invoiceNumber: 'INV-2026-000046' });
    await settle();

    http.expectOne(`${AdminOrderApi.ordersUrl}/${ORDER}`).flush(detail());
    await settle();
  });

  it('sends null when no accept-late reason was given', async () => {
    await open(
      detail({
        status: 'Expired',
        statusLabel: 'Expired',
        internalNote: 'Rang the customer; they said they paid on Tuesday.',
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

    const request = http.expectOne(`/api/admin/orders/bank-transfers/${ORDER}/accept-late`);

    // Null, not the internal note's text — which is the whole point.
    expect(request.request.body).toEqual({ note: null });

    request.flush({ orderNumber: 'SD-2026-000123', invoiceNumber: 'INV-2026-000046' });
    await settle();

    http.expectOne(`${AdminOrderApi.ordersUrl}/${ORDER}`).flush(detail());
    await settle();
  });

  /**
   * <b>Doc 02 ADM-09's "cancel… status", rendered before anything can write it.</b>
   *
   * `cancelReason` is null on every order today, because nothing cancels one yet (W3-05's). A field
   * nothing reads is a field nobody notices breaking — my own words about the dispatch, which had exactly
   * that defect — so it is on the screen now, beside the status it explains.
   */
  it('shows why an order was cancelled, and nothing when it was not', async () => {
    await open(detail());
    expect(element.querySelector('[data-testid="cancel-reason"]')).toBeNull();

    await open(
      detail({
        status: 'Cancelled',
        statusLabel: 'Cancelled',
        cancelReason: 'Customer rang and asked us not to send it.',
        closedAtUtc: '2026-10-06T06:00:00Z',
        actions: { ...detail().actions, canMarkDelivered: false, canEditDispatch: false },
      }),
    );

    expect(element.querySelector('[data-testid="cancel-reason"]')?.textContent).toContain(
      'asked us not to send it',
    );
  });

  /**
   * <b>Doc 11 §6.6 step 2: staff read a refund's amount, the date it was sent and its reference out to a
   * customer.</b>
   *
   * `completedAtUtc` is the date it was sent and it reached no screen — and it is not `createdAtUtc`: a
   * `Pending` refund has been recorded and the money has not moved. Showing only one date would have a
   * staff member reading out the wrong one, confidently.
   */
  it('shows when a refund’s money actually went, and says so when it has not', async () => {
    const refund = {
      amount: 15_596.6,
      currency: 'LKR',
      providerAmount: null,
      method: 'ManualBankTransfer',
      status: 'Completed',
      reason: 'Out of stock after the order expired. Ref OUT-0001.',
      restockItems: false,
      createdByUserEmail: 'staff@example.com',
      createdAtUtc: '2026-10-09T09:00:00Z',
      completedAtUtc: '2026-10-10T11:30:00Z',
    };

    await open(detail({ refunds: [refund] }));

    const sent = element.querySelector('[data-testid="refund-completed"]')?.textContent ?? '';

    // The 10th in Colombo, which the 9th's `createdAtUtc` is not — so this cannot pass by accidentally
    // rendering the recorded date instead.
    expect(sent).toContain('Oct 10, 2026');
    expect(sent).not.toContain('Oct 9');

    // A refund that has been recorded and not sent says so rather than showing the recorded date twice.
    await open(detail({ refunds: [{ ...refund, status: 'Pending', completedAtUtc: null }] }));

    expect(element.querySelector('[data-testid="refund-completed"]')?.textContent).toContain(
      'not yet',
    );
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

  it('renders an overseas address with region, postcode and country, and no district', async () => {
    const base = detail();
    await open(
      detail({
        delivery: {
          ...base.delivery,
          methodCode: 'INTERNATIONAL',
          methodName: 'International delivery',
          districtName: null,
          zone: 'Overseas',
          city: 'Austin',
          region: 'TX',
          postalCode: '78701',
          landmark: null,
          countryCode: 'US',
          countryName: 'United States',
        },
      }),
    );

    const facts = element.querySelector('[data-testid="order-delivery"]')?.textContent ?? '';
    expect(facts).toContain('Austin, TX, 78701');
    expect(element.querySelector('[data-testid="order-country"]')?.textContent).toContain('United States');
    expect(element.querySelector('[data-testid="order-overseas"]')).not.toBeNull();
    expect(element.querySelector('[data-testid="order-district"]')).toBeNull();
  });

  it('keeps the district for a Sri Lankan order and does not mark it overseas', async () => {
    await open();

    expect(element.querySelector('[data-testid="order-district"]')?.textContent).toContain('Colombo');
    expect(element.querySelector('[data-testid="order-country"]')?.textContent).toContain('Sri Lanka');
    expect(element.querySelector('[data-testid="order-overseas"]')).toBeNull();
  });
});
