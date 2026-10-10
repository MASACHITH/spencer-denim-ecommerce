import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  TestRequest,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { RESPONSE_INIT } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';

import { of } from 'rxjs';

import { OrderPage } from './order-page';
import { CheckoutApi } from '../../core/checkout/checkout-api';
import {
  OrderConfirmationDto,
  OrderLineDto,
  OrderPaymentInstructionsDto,
  OrderPaymentRedirectFormDto,
} from '../../core/checkout/checkout.model';

const TOKEN = '22222222-2222-4222-8222-222222222222';

function orderLine(overrides: Partial<OrderLineDto> = {}): OrderLineDto {
  return {
    lineNumber: 1,
    productName: 'Relaxed Linen Blend Shirt',
    productCode: 'CW-W-SHB-0001',
    sku: 'CW-W-SHB-0001-BEI-M',
    colourName: 'Beige',
    sizeLabel: 'M',
    imageUrl: '/media/public/originals/demo/a.webp',
    quantity: 2,
    unitOriginalPrice: 6990,
    unitPrice: 4392,
    lineSubtotal: 8784,
    promoDiscountAllocated: 0,
    lineTotal: 8784,
    ...overrides,
  };
}

function order(overrides: Partial<OrderConfirmationDto> = {}): OrderConfirmationDto {
  return {
    orderNumber: 'SD-2026-000123',
    publicId: TOKEN,
    status: 'PendingPayment',
    statusLabel: 'Awaiting payment',
    email: 'nimali@example.com',
    firstName: 'Nimali',
    lastName: 'Perera',
    phone: '0771234567',
    lines: [orderLine()],
    delivery: {
      methodCode: 'STANDARD',
      methodName: 'Standard delivery',
      pickupLocationName: null,
      recipientName: 'Nimali Perera',
      phone: '0771234567',
      line1: '14 Galle Road',
      line2: null,
      city: 'Kollupitiya',
      districtName: 'Colombo',
      postalCode: '00300',
      landmark: null,
      estimatedDaysMin: 1,
      estimatedDaysMax: 2,
    },
    itemsOriginalTotal: 13_980,
    itemsSubtotal: 8_784,
    savedTotal: 5_196,
    promoDiscountAmount: 0,
    deliveryFeeAmount: 400,
    deliveryDiscountAmount: 0,
    grandTotal: 9_184,
    currency: 'LKR',
    paymentMethodCode: 'BankTransfer',
    paymentMethodName: 'Bank or online transfer',
    customerNote: null,
    placedAtUtc: '2026-10-05T04:30:00Z',
    paymentDueAtUtc: '2026-10-07T04:30:00Z',
    isDuplicateSubmission: false,

    // **Every field of the pay block carries a real value, and that is deliberate.** W1-12's blocker
    // survived 1,571 passing tests because every fixture set the columns it cleared to null, so the
    // round trip compared null with null and agreed. A fixture whose account number is a real string is
    // the only kind that can fail when the page stops rendering it.
    payment: bankInstructions(),
    proof: null,

    // Doc 11 P10 flow A. The server decides this from four guards, two of which this page cannot see;
    // every fixture here is a fresh unpaid order, which is the one state in which it is true.
    canCancel: true,
    ...overrides,
  };
}

/** The bank details as the server describes them (doc 11 P7 step 1). */
export function bankInstructions(
  overrides: Partial<OrderPaymentInstructionsDto> = {},
): OrderPaymentInstructionsDto {
  return {
    providerCode: 'BankTransfer',
    reference: 'SD-2026-000123',
    amountLkr: 9_184,
    dueAtUtc: '2026-10-07T04:30:00Z',
    fields: [
      { label: 'Bank', value: 'Example Bank PLC', copyable: false },
      // An obviously-fake branch, like every other bank value in this fixture. The *city* in the address
      // above is a real Colombo suburb on purpose — that is an address, not the shop's bank details.
      { label: 'Branch', value: 'Example Branch', copyable: false },
      { label: 'Account name', value: 'Example Shop (Pvt) Ltd', copyable: false },
      { label: 'Account number', value: '0000 1111 2222', copyable: true },
    ],
    notes: 'Cash-machine deposits can take a working day to appear.',
    redirectUrl: null,
    acceptsProof: true,
    ...overrides,
  };
}

describe('OrderPage (CHK-04)', () => {
  let fixture: ComponentFixture<OrderPage>;
  let element: HTMLElement;
  let http: HttpTestingController;
  let response: ResponseInit;

  async function settle(): Promise<void> {
    fixture.detectChanges();
    await fixture.whenStable();
  }

  function create(query: Record<string, string> = {}): void {
    response = {};
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: RESPONSE_INIT, useValue: response },
        {
          // The route, not a signal input: the page reads the token from the route's params because it
          // starts its request from the constructor, before a required input would be populated.
          provide: ActivatedRoute,
          useValue: {
            params: of({ token: TOKEN }),
            snapshot: {
              params: { token: TOKEN },
              paramMap: convertToParamMap({ token: TOKEN }),
              queryParamMap: convertToParamMap(query),
            },
          },
        },
      ],
    });

    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(OrderPage);
    element = fixture.nativeElement as HTMLElement;
  }

  function orderRequest(): TestRequest {
    return http.expectOne(
      (request) =>
        request.url === `${CheckoutApi.orderUrl}/${TOKEN}` && request.method === 'GET',
    );
  }

  async function open(payload: OrderConfirmationDto, query: Record<string, string> = {}): Promise<void> {
    create(query);
    await settle();
    orderRequest().flush(payload);
    await settle();
  }

  function text(): string {
    return element.textContent ?? '';
  }

  function sums(): {
    subtotal: number;
    savings: number;
    delivery: number;
    deliveryDiscount: number;
    total: number;
  } {
    const read = (name: string): number => {
      const node = element.querySelector(`[data-sum="${name}"]`);
      if (!node) {
        return 0;
      }
      const matched = /[\d,]+(?:\.\d+)?/.exec((node.textContent ?? '').replace('Rs.', ''));
      return matched ? Number(matched[0].replace(/,/g, '')) : 0;
    };

    return {
      subtotal: read('subtotal'),
      savings: read('savings'),
      delivery: read('delivery'),
      deliveryDiscount: read('delivery-discount'),
      total: read('total'),
    };
  }

  afterEach(() => TestBed.inject(HttpTestingController).verify());

  /** Doc 11 P10 flow A: the shopper's own Cancel order, confirmed before it fires. */
  function cancelRequest(): TestRequest {
    return http.expectOne(
      (request) =>
        request.url === `${CheckoutApi.orderUrl}/${TOKEN}/cancel` && request.method === 'POST',
    );
  }

  it('offers Cancel this order only when the server says it can be cancelled', async () => {
    await open(order());

    expect(element.querySelector('[data-testid="cancel-panel"]')).not.toBeNull();
    expect(element.querySelector('[data-testid="cancel-order"]')).not.toBeNull();

    // Doc 11 P10's table: a paid order is refunded, never cancelled — and there is no refund path in this
    // build, so the page must offer nothing rather than a control that can only fail.
    await open(order({ status: 'Paid', statusLabel: 'Paid', payment: null, canCancel: false }));

    expect(element.querySelector('[data-testid="cancel-panel"]')).toBeNull();
  });

  /**
   * **Two presses, because this is irreversible for them.**
   *
   * The first reveals the question and sends nothing — `http.verify()` in `afterEach` is what proves the
   * "sends nothing" half. A single press that fired would mean one stray tap on a phone closed somebody's
   * order and put their items back on sale.
   */
  it('asks before it cancels, and the first press sends nothing', async () => {
    await open(order());

    element.querySelector<HTMLElement>('[data-testid="cancel-order"]')!.click();
    await settle();

    expect(element.querySelector('[data-testid="cancel-confirm-question"]')?.textContent).toContain(
      'SD-2026-000123',
    );
    expect(element.querySelector('[data-testid="cancel-confirm"]')).not.toBeNull();
    expect(element.querySelector('[data-testid="cancel-keep"]')).not.toBeNull();

    // And "Keep my order" really keeps it: the question goes away and still nothing is sent.
    element.querySelector<HTMLElement>('[data-testid="cancel-keep"]')!.click();
    await settle();

    expect(element.querySelector('[data-testid="cancel-confirm-question"]')).toBeNull();
    expect(element.querySelector('[data-testid="cancel-order"]')).not.toBeNull();
  });

  it('cancels on the second press and re-reads the order from the server', async () => {
    await open(order());

    element.querySelector<HTMLElement>('[data-testid="cancel-order"]')!.click();
    await settle();

    element.querySelector<HTMLElement>('[data-testid="cancel-confirm"]')!.click();
    await settle();

    const request = cancelRequest();

    // No body: doc 11 P10 asks the staff member for a reason and the customer only to press the button.
    expect(request.request.body).toBeNull();

    request.flush({ orderNumber: 'SD-2026-000123' });
    await settle();

    // **It re-reads rather than redrawing from what it sent**, which is the only way the page can show
    // the order's real state — and `statusLabel` is the server's wording, which the page prints instead of
    // `status.toLowerCase()`.
    orderRequest().flush(
      order({
        status: 'Cancelled',
        statusLabel: 'Cancelled',
        payment: null,
        proof: null,
        canCancel: false,
      }),
    );
    await settle();

    expect(element.querySelector('[data-testid="cancel-panel"]')).toBeNull();
    expect(element.querySelector('[data-testid="pay-panel"]')).toBeNull();
    expect(text()).toContain('This order is cancelled.');
  });

  /**
   * A 404 is the ordinary refusal on this route, and it means "this order can no longer be cancelled".
   *
   * The page cannot be told which of the reasons applies — the token never expires and is a guest's only
   * credential, so "no such order" and "that order is already paid" are deliberately the same answer — so
   * the sentence has to cover them all, and the page re-reads to show where the order actually is.
   */
  it('explains a refused cancel and reloads the order rather than guessing', async () => {
    await open(order());

    element.querySelector<HTMLElement>('[data-testid="cancel-order"]')!.click();
    await settle();

    element.querySelector<HTMLElement>('[data-testid="cancel-confirm"]')!.click();
    await settle();

    cancelRequest().flush({ title: 'Not found', status: 404 }, { status: 404, statusText: 'Not Found' });
    await settle();

    orderRequest().flush(order({ status: 'Paid', statusLabel: 'Paid', payment: null, canCancel: false }));
    await settle();

    // **The panel is gone and the message is not**, which is the point: the re-read turned `canCancel`
    // false, and the explanation used to live inside the panel — so a shopper whose press lost the race
    // to the expiry sweep was told nothing at all.
    expect(element.querySelector('[data-testid="cancel-panel"]')).toBeNull();

    const message = element.querySelector('[data-testid="cancel-error"]')?.textContent ?? '';

    expect(message).toContain('can no longer be cancelled');

    // And the question is not left half-asked over an order that has moved.
    expect(element.querySelector('[data-testid="cancel-confirm-question"]')).toBeNull();
  });

  /**
   * **A second press while the first is in flight sends nothing** (#109's other half).
   *
   * The button carries `aria-disabled` rather than `disabled`, because disabling a control under the
   * shopper's own finger throws focus to `<body>` — so the guard that `disabled` would have provided has
   * to be in the handler, and this is the test that proves it is. Without it, a shopper on a slow Sri
   * Lankan mobile connection who taps twice sends two cancels for one order.
   *
   * `http.verify()` in `afterEach` is what fails if a second request was made, and the `aria-disabled`
   * attribute is asserted too: the look and the refusal are two different promises and a screen reader
   * only hears the first.
   */
  it('refuses a second press while the cancel is in flight', async () => {
    await open(order());

    element.querySelector<HTMLElement>('[data-testid="cancel-order"]')!.click();
    await settle();

    const confirm = element.querySelector<HTMLElement>('[data-testid="cancel-confirm"]')!;

    confirm.click();
    await settle();

    expect(confirm.getAttribute('aria-disabled')).toBe('true');
    expect(confirm.hasAttribute('disabled')).toBe(false);

    confirm.click();
    confirm.click();
    await settle();

    // Exactly one, and it is still unanswered.
    const pending = http.match(
      (request) =>
        request.url === `${CheckoutApi.orderUrl}/${TOKEN}/cancel` && request.method === 'POST',
    );

    expect(pending).toHaveLength(1);

    pending[0].flush({ orderNumber: 'SD-2026-000123' });
    await settle();

    orderRequest().flush(
      order({ status: 'Cancelled', statusLabel: 'Cancelled', payment: null, canCancel: false }),
    );
    await settle();
  });

  /**
   * Every step of the cancel flow is announced, and **the ADM-22 re-fire guard is not reachable from
   * here**, which is said rather than faked.
   *
   * The first version of this test asserted the trailing space the guard appends, and it failed: pressing
   * Cancel, keeping the order and pressing again puts "Your order is still with us." between the two
   * identical questions, so the second one *is* a change and the guard is never entered. That is the
   * same resolution ADM-22 itself reached, and the same one W3-04 reached on the admin orders list — a
   * test for an unreachable path is a test that cannot fail. The guard's own coverage on this page is
   * "re-announces an identical sentence when the same thing is copied twice", further down, where the
   * identical sentence really does arrive twice in a row.
   */
  it('announces each step of the cancel flow, and keeping the order says so', async () => {
    await open(order());

    const region = element.querySelector('[data-testid="pay-announcement"]')!;

    element.querySelector<HTMLElement>('[data-testid="cancel-order"]')!.click();
    await settle();

    const asked = region.textContent ?? '';
    expect(asked).toContain('Cancel this order?');

    element.querySelector<HTMLElement>('[data-testid="cancel-keep"]')!.click();
    await settle();

    const kept = region.textContent ?? '';
    expect(kept).toContain('still with us');
    expect(kept).not.toBe(asked);

    element.querySelector<HTMLElement>('[data-testid="cancel-order"]')!.click();
    await settle();

    expect(region.textContent).toContain('Cancel this order?');
  });

  /**
   * #109, arriving by node removal rather than by `[disabled]`.
   *
   * **Both presses in the cancel flow destroy the control that was pressed.** The first swaps the whole
   * branch of the template, so "Cancel this order" is removed; the applied cancel turns `canCancel` false
   * and removes the panel, so "Yes, cancel it" is removed. A destroyed focused node sends focus to
   * `<body>`, which is what a Chromium probe measured on this page before the re-homing existed — twice,
   * once after each press — and which the admin order detail already solves with the same method.
   *
   * **And the live region has to survive**, which it did not: it lived inside the pay panel, so a
   * successful cancel moved the order off `PendingPayment` and took the region away with it. The
   * announcement was written into a node the re-read then removed, so the one thing this page can tell a
   * screen-reader user about the most destructive action it offers went nowhere. That is the same defect
   * the refusal message had, one block further down, in the same screen.
   */
  it('keeps the live region and re-homes focus when a cancel destroys the control pressed', async () => {
    await open(order());

    const ask = element.querySelector<HTMLElement>('[data-testid="cancel-order"]')!;
    ask.focus();
    expect(document.activeElement).toBe(ask);

    ask.click();
    await settle();
    await fixture.whenStable();

    // The button is gone, so focus cannot still be on it — and it is on the sentence rather than on
    // `<body>`.
    expect(ask.isConnected).toBe(false);
    expect(document.activeElement).toBe(element.querySelector('[data-testid="pay-announcement"]'));

    const confirm = element.querySelector<HTMLElement>('[data-testid="cancel-confirm"]')!;
    confirm.focus();
    confirm.click();
    await settle();

    cancelRequest().flush({ orderNumber: 'SD-2026-000123' });
    await settle();

    orderRequest().flush(
      order({ status: 'Cancelled', statusLabel: 'Cancelled', payment: null, canCancel: false }),
    );
    await settle();
    await fixture.whenStable();

    // **The region is still on the page**, although the pay panel and the cancel panel are not — which
    // is the assertion that would have failed before it was moved out of the pay panel.
    const region = element.querySelector('[data-testid="pay-announcement"]');

    expect(element.querySelector('[data-testid="pay-panel"]')).toBeNull();
    expect(element.querySelector('[data-testid="cancel-panel"]')).toBeNull();
    expect(region).not.toBeNull();
    expect(region!.textContent).toContain('is cancelled');

    expect(confirm.isConnected).toBe(false);
    expect(document.activeElement).toBe(region);
  });

  /**
   * **"Keep my order" is refused while the cancel is on the wire**, which it was not.
   *
   * The request cannot be recalled, so the press had no truthful effect — it put the panel back to
   * "Cancel this order" and announced "Your order is still with us." about an order that was being
   * cancelled as it said so. A screen-reader user heard the false sentence and then the true one.
   */
  it('refuses Keep my order while the cancel is in flight, and says nothing untrue', async () => {
    await open(order());

    element.querySelector<HTMLElement>('[data-testid="cancel-order"]')!.click();
    await settle();

    element.querySelector<HTMLElement>('[data-testid="cancel-confirm"]')!.click();
    await settle();

    const keep = element.querySelector<HTMLElement>('[data-testid="cancel-keep"]')!;

    expect(keep.getAttribute('aria-disabled')).toBe('true');
    expect(keep.hasAttribute('disabled')).toBe(false);

    keep.click();
    await settle();

    // The question is still being asked, and nothing claims the order was kept.
    expect(element.querySelector('[data-testid="cancel-confirm-question"]')).not.toBeNull();
    expect(element.querySelector('[data-testid="pay-announcement"]')?.textContent).not.toContain(
      'still with us',
    );

    cancelRequest().flush({ orderNumber: 'SD-2026-000123' });
    await settle();

    orderRequest().flush(
      order({ status: 'Cancelled', statusLabel: 'Cancelled', payment: null, canCancel: false }),
    );
    await settle();

    expect(element.querySelector('[data-testid="pay-announcement"]')?.textContent).toContain(
      'is cancelled',
    );
  });

  /** Keeping the order removes "Keep my order", which is the same removal in the other direction. */
  it('re-homes focus when Keep my order destroys the button pressed', async () => {
    await open(order());

    element.querySelector<HTMLElement>('[data-testid="cancel-order"]')!.click();
    await settle();

    const keep = element.querySelector<HTMLElement>('[data-testid="cancel-keep"]')!;
    keep.focus();
    keep.click();
    await settle();
    await fixture.whenStable();

    expect(keep.isConnected).toBe(false);
    expect(document.activeElement).toBe(element.querySelector('[data-testid="pay-announcement"]'));
  });

  /**
   * **Nothing is re-homed when nothing was lost**, which is the half that makes the rule safe.
   *
   * The reference box survives every re-read, and yanking the caret out of it mid-sentence would be a
   * worse defect than the one being fixed — so the node is checked for `isConnected` first.
   */
  it('leaves focus alone when the control pressed is still there', async () => {
    await open(order());

    const box = element.querySelector<HTMLElement>('[data-testid="pay-reference-input"]')
      ?? element.querySelector<HTMLElement>('input[type="text"]')!;

    box.focus();

    element.querySelector<HTMLElement>('[data-testid="cancel-order"]')!.click();
    await settle();
    await fixture.whenStable();

    expect(box.isConnected).toBe(true);
    expect(document.activeElement).toBe(box);
  });

  it('asks for the order before the first change detection', () => {
    create();
    orderRequest().flush(order());
  });

  /** CHK-04 in one line: the order number is on the page, and it is the number the order has. */
  it('shows the order number', async () => {
    await open(order());

    expect(element.querySelector('[data-order-number]')?.textContent?.trim()).toBe(
      'SD-2026-000123',
    );
  });

  /**
   * **The same ladder as the bag and the checkout summary, read off the screen.** Read back from the
   * order row rather than recomputed, so a drift between what was quoted and what was stored shows up
   * here as arithmetic that does not close.
   */
  it('adds up: Subtotal − Savings + Delivery − Free delivery = Total', async () => {
    await open(order());

    const { subtotal, savings, delivery, deliveryDiscount, total } = sums();

    expect(subtotal - savings + delivery - deliveryDiscount).toBe(total);
    expect(total).toBe(9_184);
  });

  it('says the order is waiting for payment, and when the hold lapses', async () => {
    await open(order());

    expect(text()).toContain('Awaiting payment');
    // Colombo is UTC+05:30, so 04:30 UTC on 7 October is 10.00 am local.
    expect(text()).toContain('7 October 2026 at 10.00 am');
  });

  /**
   * **This replaces a test W3-02 left behind, and the replacement is the point of W3-03.**
   *
   * The old one was called "promises no bank details and claims no email has been sent" and asserted that
   * the words "account number", "upload" and "reference to quote" were absent — a boundary marker for a
   * page that genuinely had nothing true to say about money. Doc 11 P7 is now built, so the first half of
   * it is wrong and asserting it would hold the feature out of the page.
   *
   * The second half still holds and is kept below: nothing sends an email (`ops.EmailOutbox` is W3-06), so
   * nothing may claim one was sent.
   */
  it('shows the bank details, the reference to quote, the amount and the deadline', async () => {
    await open(order());

    const panel = element.querySelector('[data-testid="pay-panel"]');
    expect(panel).not.toBeNull();

    const body = panel!.textContent ?? '';

    // Doc 11 P7 step 1's list. The reference is the order number, which is the only thing tying an
    // anonymous bank credit to this order.
    expect(element.querySelector('[data-testid="pay-reference"]')?.textContent?.trim()).toBe(
      'SD-2026-000123',
    );
    expect(element.querySelector('[data-testid="pay-account-number"]')?.textContent?.trim()).toBe(
      '0000 1111 2222',
    );
    expect(body).toContain('Example Bank PLC');
    expect(body).toContain('Example Branch');
    expect(body).toContain('Example Shop (Pvt) Ltd');

    // The exact amount, and the deadline in Colombo time (04:30 UTC is 10.00 am local).
    expect(element.querySelector('[data-testid="pay-amount"]')?.textContent).toContain('9,184');
    expect(element.querySelector('[data-testid="pay-due"]')?.textContent).toContain(
      '7 October 2026 at 10.00 am',
    );

    // The shop's own note, as text.
    expect(element.querySelector('[data-testid="pay-notes"]')?.textContent).toContain(
      'Cash-machine deposits',
    );
  });

  it('claims no email has been sent', async () => {
    await open(order());

    const body = text().toLowerCase();

    expect(body).not.toContain('we have emailed');
    expect(body).not.toContain('check your inbox');
    expect(body).not.toContain('sent you an email');
  });

  /**
   * The precondition doc 11 P7 states and the shop can be deployed without.
   */
  it('says the details are being confirmed when the shop has not set them', async () => {
    await open(order({ payment: null }));

    expect(element.querySelector('[data-testid="pay-pending-details"]')).not.toBeNull();

    // **Not a half-filled form.** A shopper who saw a bank name and an empty account number might
    // transfer to whatever they found, and the money would then have to be traced by hand.
    expect(element.querySelector('[data-testid="pay-account-number"]')).toBeNull();
    expect(element.querySelector('[data-testid="pay-reference"]')).toBeNull();
    expect(element.querySelector('[data-testid="slip-input"]')).toBeNull();
  });

  /**
   * The panel is driven by the order's status, not by whether instructions came back — which are two
   * different questions, and conflating them is how a paid order would keep asking for money.
   */
  it('shows no pay panel once the order is paid', async () => {
    // `canCancel: false`, as the server sends it for a paid order (doc 11 P10's table: refunded,
    // never cancelled). Without it this fixture drew the Cancel panel, so the page under test said
    // "You have not paid for this order" on an order the fixture called Paid - a contradiction no
    // assertion here looked at, in a state the reader cannot produce.
    await open(order({ status: 'Paid', payment: null, proof: null, canCancel: false }));

    expect(element.querySelector('[data-testid="pay-panel"]')).toBeNull();
    expect(text()).toContain('Paid.');
    expect(text()).not.toContain('Awaiting payment');
  });

  /**
   * **Doc 11 P9's pickup variant, and the defect it caught.**
   *
   * The page printed `status.toLowerCase()`, so a store-pickup customer whose parcel was sitting on the
   * counter read **"This order is shipped."** Doc 11 P9 is explicit that `Shipped` is shown to staff *and
   * customer* as "Ready for collection". Nothing could reach this until ADM-09 made the transition
   * pressable; W3-04's review is what found it.
   *
   * The wording comes from the server (`statusLabel`), rendered by the same `OrderStatusLabels.For` the
   * two admin screens use — so this fixture supplies a label that differs from the status, which is what
   * makes the test able to fail.
   */
  it('tells a pickup customer their order is ready for collection, not shipped', async () => {
    await open(
      order({
        status: 'Shipped',
        statusLabel: 'Ready for collection',
        payment: null,
        proof: null,
        canCancel: false,
      }),
    );

    expect(text()).toContain('This order is ready for collection.');
    expect(text()).not.toContain('shipped');
  });

  it('tells a delivery customer their order is shipped', async () => {
    await open(
      order({ status: 'Shipped', statusLabel: 'Shipped', payment: null, proof: null, canCancel: false }),
    );

    expect(text()).toContain('This order is shipped.');
  });

  /** And "Collected" rather than "delivered" once a pickup order has been handed over. */
  it('tells a pickup customer their order was collected, not delivered', async () => {
    await open(
      order({
        status: 'Delivered',
        statusLabel: 'Collected',
        payment: null,
        proof: null,
        canCancel: false,
      }),
    );

    expect(text()).toContain('This order is collected.');
    expect(text()).not.toContain('delivered');
  });

  it('sends the typed reference, and sends null for an emptied box', async () => {
    await open(order());

    const input = element.querySelector<HTMLInputElement>('[data-testid="reference-input"]')!;
    input.value = '  CBC-778812  ';
    input.dispatchEvent(new Event('input'));
    await settle();

    element.querySelector<HTMLButtonElement>('[data-testid="save-reference"]')!.click();
    await settle();

    const saved = http.expectOne(`${CheckoutApi.orderUrl}/${TOKEN}/payment-reference`);
    expect(saved.request.method).toBe('PUT');

    // Trimmed, because a reference with spaces round it will not match a bank statement.
    expect(saved.request.body).toEqual({ reference: 'CBC-778812' });

    saved.flush({
      reference: 'CBC-778812',
      slipPublicId: null,
      slipFileName: null,
      slipContentType: null,
      slipUploadedAtUtc: null,
    });

    // The save is followed by a re-read, so the page shows what the server kept.
    http.expectOne(`${CheckoutApi.orderUrl}/${TOKEN}`).flush(
      order({ proof: { reference: 'CBC-778812', slipPublicId: null, slipFileName: null, slipContentType: null, slipUploadedAtUtc: null } }),
    );
    await settle();

    input.value = '   ';
    input.dispatchEvent(new Event('input'));
    await settle();

    element.querySelector<HTMLButtonElement>('[data-testid="save-reference"]')!.click();
    await settle();

    const cleared = http.expectOne(`${CheckoutApi.orderUrl}/${TOKEN}/payment-reference`);

    // **Null, not an empty string.** Clearing it is a real action — a customer who mistyped it has to be
    // able to take it back rather than leave a wrong reference for staff to match against.
    expect(cleared.request.body).toEqual({ reference: null });
  });

  it('shows the slip already on the order, and says another one replaces it', async () => {
    await open(
      order({
        proof: {
          reference: 'CBC-778812',
          slipPublicId: '33333333-3333-4333-8333-333333333333',
          slipFileName: 'slip.jpg',
          slipContentType: 'image/jpeg',
          slipUploadedAtUtc: '2026-10-05T05:00:00Z',
        },
      }),
    );

    const current = element.querySelector('[data-testid="slip-current"]');
    expect(current?.textContent).toContain('slip.jpg');
    expect(current?.textContent).toContain('replaces it');

    // The download link is keyed on the *order*, never on the asset: a private file has no public URL and
    // the only parties who may see it name an order (doc 08 §3.3).
    expect(current?.querySelector('a')?.getAttribute('href')).toBe(
      `${CheckoutApi.orderUrl}/${TOKEN}/payment-slip`,
    );
  });

  /**
   * The write controls are never `disabled`.
   */
  it('marks the save and the file box unavailable with aria-disabled, not disabled', async () => {
    await open(order());

    const input = element.querySelector<HTMLInputElement>('[data-testid="reference-input"]')!;
    input.value = 'CBC-1';
    input.dispatchEvent(new Event('input'));
    await settle();

    const save = element.querySelector<HTMLButtonElement>('[data-testid="save-reference"]')!;
    save.click();
    await settle();

    // In flight: unavailable to assistive technology and still focusable, so pressing it did not throw
    // the shopper's focus to <body> (issue #109).
    expect(save.getAttribute('aria-disabled')).toBe('true');
    expect(save.hasAttribute('disabled')).toBe(false);
    expect(element.querySelector('[data-testid="slip-input"]')?.hasAttribute('disabled')).toBe(false);

    // And the handler refuses a second write while one is in flight, which is what the attribute is only
    // advertising. One request, not two.
    save.click();
    await settle();

    http.expectOne(`${CheckoutApi.orderUrl}/${TOKEN}/payment-reference`).flush({
      reference: 'CBC-1',
      slipPublicId: null,
      slipFileName: null,
      slipContentType: null,
      slipUploadedAtUtc: null,
    });

    http.expectOne(`${CheckoutApi.orderUrl}/${TOKEN}`).flush(order());
    await settle();
  });

  /**
   * ADM-22's defect: `role="status"` announces a *change*, so a repeated identical sentence is read out
   * once and then never again.
   *
   * **Copying is the case on this page that needs the fix, and finding that out corrected this test.** The
   * first version saved the reference twice and asserted the two "Saving your reference…" sentences were
   * different strings — and it failed, correctly: `beforeWrite`-style announcements alternate
   * ("Saving…", "saved.", "Saving…", "saved."), so every consecutive pair is already a change and the
   * trailing space never fires. ADM-22's own fix for the admin screens is exactly that alternation.
   *
   * Copy has no counterpart sentence. A shopper who copies the account number, mistypes it, and copies it
   * again produces the identical sentence twice in a row with nothing between — so this is where a
   * trailing space is the only thing that makes the second one audible.
   */
  it('re-announces an identical sentence when the same thing is copied twice', async () => {
    const copied: string[] = [];

    // jsdom has no clipboard, and the component's own fallback would then announce "could not copy" —
    // which would exercise the re-fire but not the path a real shopper takes. Stubbed so the assertion is
    // about the success sentence.
    Object.defineProperty(globalThis.navigator, 'clipboard', {
      configurable: true,
      value: {
        writeText: (value: string) => {
          copied.push(value);
          return Promise.resolve();
        },
      },
    });

    try {
      await open(order());

      const region = element.querySelector('[data-testid="pay-announcement"]')!;
      const copy = element.querySelector<HTMLButtonElement>('[data-testid="copy-pay-account-number"]')!;

      // `writeText` resolves on a microtask, and the announcement is set in its `then`. `settle()` alone
      // can return before that callback has run, which is how this read came back empty the first time —
      // a flake waiting to happen rather than a real failure. One explicit drain makes it deterministic.
      copy.click();
      await Promise.resolve();
      await settle();
      const first = region.textContent ?? '';

      copy.click();
      await Promise.resolve();
      await settle();
      const second = region.textContent ?? '';

      expect(copied).toEqual(['0000 1111 2222', '0000 1111 2222']);

      // The same sentence both times, and **not the same string** — the trailing space is invisible and is
      // what makes the second one a change a screen reader will read.
      expect(first.trim()).toBe('Copied account number.');
      expect(second.trim()).toBe(first.trim());
      expect(second).not.toBe(first);
    } finally {
      Reflect.deleteProperty(globalThis.navigator, 'clipboard');
    }
  });

  it('shows the delivery address for a courier order', async () => {
    await open(order());

    expect(text()).toContain('Nimali Perera');
    expect(text()).toContain('14 Galle Road');
    expect(text()).toContain('Kollupitiya, Colombo');
    expect(text()).toContain('Estimated 1–2 days');
  });

  /**
   * Doc 08 §3.5 R7: a pickup order has no shipping address at all and its district is the location's.
   * The branch is keyed on the method **code**, because `DeliveryMethods.Name` is admin-editable and
   * `CK_Orders_Pickup` keys on the code.
   */
  it('shows the store instead of an address for a pickup order', async () => {
    await open(
      order({
        delivery: {
          methodCode: 'PICKUP',
          methodName: 'Store pickup',
          pickupLocationName: 'Colombo Wardrobe, Kollupitiya',
          recipientName: null,
          phone: null,
          line1: null,
          line2: null,
          city: null,
          districtName: 'Colombo',
          postalCode: null,
          landmark: null,
          estimatedDaysMin: 1,
          estimatedDaysMax: 2,
        },
      }),
    );

    expect(text()).toContain('Collect from');
    expect(text()).toContain('Colombo Wardrobe, Kollupitiya');
    expect(text()).not.toContain('14 Galle Road');
  });

  /**
   * **The page says nothing about duplicate submissions, and that is deliberate.**
   *
   * `isDuplicateSubmission` is a fact about a submission rather than about the order, so
   * `GET /api/orders/{token}` answers `false` for every order — including one that was replayed. This
   * page reads the order back by its token, which is what lets it survive a reload, so a notice driven
   * by that flag can never render. The first version of this page had one; `checkout.e2e.ts` found it
   * unreachable. This test pins the decision so the branch is not added back.
   */
  it('says nothing about a replayed submission, whichever way the flag arrives', async () => {
    await open(order({ isDuplicateSubmission: true }));

    expect(text()).not.toContain('already placed');
    expect(text()).not.toContain('second one');

    // And the order itself still reads correctly: dropping the notice dropped nothing else.
    expect(element.querySelector('[data-order-number]')?.textContent?.trim()).toBe(
      'SD-2026-000123',
    );
  });

  /**
   * A real 404, not a 200 saying "not found": the same treatment a mistyped product slug gets, because
   * a soft 404 tells a crawler the address is fine. And a flat answer — no "that order is not yours",
   * which would confirm the order exists.
   */
  it('answers a real 404 for a token that names no order', async () => {
    create();
    await settle();
    orderRequest().flush(
      { title: 'Not Found', status: 404 },
      { status: 404, statusText: 'Not Found' },
    );
    await settle();

    expect(response.status).toBe(404);
    expect(text()).toContain('could not find that order');
    expect(text()).not.toContain('not yours');
  });

  /**
   * The URL is a bearer token and the page holds a name, an address and a phone number. Nothing may
   * store it and nothing may index it.
   */
  it('answers no-store, private and Vary: Cookie, and asks not to be indexed', async () => {
    await open(order());

    const headers = new Headers(response.headers);
    expect(headers.get('Cache-Control')).toContain('no-store');
    expect(headers.get('Cache-Control')).toContain('private');
    expect(headers.get('Vary')).toBe('Cookie');
    expect(document.querySelector('meta[name="robots"]')?.getAttribute('content')).toBe('noindex');

    fixture.destroy();
    expect(document.querySelector('meta[name="robots"]')).toBeNull();
  });

  it('offers a retry when the order could not be loaded', async () => {
    create();
    await settle();
    orderRequest().flush({}, { status: 500, statusText: 'Server Error' });
    await settle();

    expect(text()).toContain('could not load your order');

    element.querySelector<HTMLButtonElement>('button')!.click();
    await settle();

    orderRequest().flush(order());
    await settle();

    expect(element.querySelector('[data-order-number]')?.textContent?.trim()).toBe(
      'SD-2026-000123',
    );
  });

  // ---------------------------------------------------------------------------------------------------
  // W4-01: the pay panel is provider-neutral.
  //
  // The server says how to reach an online provider's own page - a form to POST, or a link - and the page
  // does it without knowing whose page it is. Nothing below names a gateway, and nothing in the page
  // should: a provider added later must need no change here.
  // ---------------------------------------------------------------------------------------------------

  const REDIRECT_ACTION = 'https://pay.example.test/checkout';

  /** What an online provider sends: no bank content at all, and a way to reach its page. */
  function onlineInstructions(
    overrides: Partial<OrderPaymentInstructionsDto> = {},
  ): OrderPaymentInstructionsDto {
    return bankInstructions({
      providerCode: 'OnlineProvider',
      reference: null,
      fields: [],
      notes: null,
      acceptsProof: false,
      redirectUrl: null,
      redirectForm: null,
      ...overrides,
    });
  }

  /**
   * A redirect form. `method` is a plain string here so a test can send the one thing the contract says
   * never arrives, and see the page refuse it.
   */
  function redirectForm(
    overrides: { method?: string; action?: string; fields?: OrderPaymentRedirectFormDto['fields'] } = {},
  ): OrderPaymentRedirectFormDto {
    return {
      method: 'POST',
      action: REDIRECT_ACTION,
      fields: [
        { name: 'order_ref', value: 'SD-2026-000123' },
        { name: 'amount', value: '9184.00' },
        // Markup characters, to show a value is carried as a value and never read as HTML.
        { name: 'signature', value: 'a"b&c<d>e' },
        // The same name twice: a provider may legitimately repeat one, and both must survive in order.
        { name: 'item', value: 'first' },
        { name: 'item', value: 'second' },
      ],
      ...overrides,
    } as OrderPaymentRedirectFormDto;
  }

  function payPanel(): HTMLElement {
    return element.querySelector<HTMLElement>('[data-testid="pay-panel"]')!;
  }

  /** Every part of the panel that only makes sense for a bank transfer. */
  const BANK_ONLY_TEST_IDS = [
    'pay-reference',
    'copy-reference',
    'pay-bank',
    'pay-account-number',
    'copy-pay-account-number',
    'pay-notes',
    'reference-input',
    'save-reference',
    'slip-input',
  ];

  function expectNoBankParts(): void {
    const panel = payPanel();

    for (const id of BANK_ONLY_TEST_IDS) {
      expect(panel.querySelector(`[data-testid="${id}"]`), id).toBeNull();
    }

    expect(panel.querySelector('dl')).toBeNull();
    expect(panel.querySelector('.pay__proof')).toBeNull();
    expect(panel.querySelector('input[type="text"], input[type="file"]')).toBeNull();
    expect(panel.textContent ?? '').not.toMatch(/transfer|account|bank|reference|slip|receipt/i);
  }

  /** Nothing a shopper could press, follow or submit - the point of refusing an address. */
  function expectNothingToFollow(): void {
    const panel = payPanel();

    expect(panel.querySelector('form')).toBeNull();
    expect(panel.querySelector('a')).toBeNull();
    expect(panel.querySelector('button')).toBeNull();
    expect(panel.querySelector('input')).toBeNull();
    expect(panel.textContent).not.toContain('Pay now');
  }

  it('renders a real POST form for a redirect form, with the fields in order', async () => {
    await open(order({ payment: onlineInstructions({ redirectForm: redirectForm() }) }));

    const panel = payPanel();

    expect(panel.querySelector('#pay-title')?.textContent?.trim()).toBe('Pay online');

    const form = panel.querySelector<HTMLFormElement>('form[data-testid="pay-redirect-form"]')!;

    expect(form).not.toBeNull();
    expect(form.getAttribute('method')).toBe('post');
    expect(form.getAttribute('action')).toBe(REDIRECT_ACTION);

    // Same tab: a payment is a hand-over, not a second window to lose behind the first.
    expect(form.hasAttribute('target')).toBe(false);

    const inputs = Array.from(form.querySelectorAll<HTMLInputElement>('input'));

    expect(inputs.map((input) => input.getAttribute('type'))).toEqual([
      'hidden',
      'hidden',
      'hidden',
      'hidden',
      'hidden',
    ]);
    expect(inputs.map((input) => [input.getAttribute('name'), input.getAttribute('value')])).toEqual([
      ['order_ref', 'SD-2026-000123'],
      ['amount', '9184.00'],
      ['signature', 'a"b&c<d>e'],
      ['item', 'first'],
      ['item', 'second'],
    ]);

    // What the browser will actually send, which is not the same thing as what the attributes say.
    expect(new FormData(form).getAll('signature')).toEqual(['a"b&c<d>e']);
    expect(new FormData(form).getAll('item')).toEqual(['first', 'second']);
  });

  it('gives the redirect form a visible, keyboard-reachable Pay now that submits natively', async () => {
    await open(order({ payment: onlineInstructions({ redirectForm: redirectForm() }) }));

    const form = payPanel().querySelector<HTMLFormElement>('form')!;
    const buttons = form.querySelectorAll('button');

    expect(buttons).toHaveLength(1);

    const button = buttons[0];

    expect(button.textContent?.trim()).toBe('Pay now');
    expect(button.type).toBe('submit');
    expect(button.form).toBe(form);
    expect(button.classList.contains('btn--primary')).toBe(true);

    // A native button: focusable, activated by Enter and Space, with nothing to switch it off or take it
    // out of the tab order.
    expect(button.hasAttribute('disabled')).toBe(false);
    expect(button.getAttribute('aria-disabled')).toBeNull();
    expect(button.tabIndex).toBe(0);

    // And nothing of ours stands between the press and the browser: a submit that a handler cancelled
    // would be a fetch in disguise, and the provider's page would never open.
    const submit = new Event('submit', { bubbles: true, cancelable: true });

    form.dispatchEvent(submit);

    expect(submit.defaultPrevented).toBe(false);
    http.expectNone(() => true);
  });

  it('shows the amount and the due time, in Colombo time, beside a redirect form', async () => {
    await open(order({ payment: onlineInstructions({ redirectForm: redirectForm() }) }));

    const panel = payPanel();

    expect(panel.querySelector('[data-testid="pay-amount"]')?.textContent).toContain('9,184');
    expect(panel.querySelector('[data-testid="pay-due"]')?.textContent).toContain(
      '7 October 2026 at 10.00 am',
    );

    const copy = panel.querySelector('.pay__lead')?.textContent?.replace(/\s+/g, ' ') ?? '';

    expect(copy).toContain('You will be taken to our secure payment page');
    expect(copy).toContain('Your order is held until 7 October 2026 at 10.00 am');
  });

  it('shows none of the bank-only parts for a redirect form', async () => {
    await open(order({ payment: onlineInstructions({ redirectForm: redirectForm() }) }));

    expectNoBankParts();
  });

  it('renders a real Pay now link for a redirect url, in the same tab', async () => {
    await open(
      order({ payment: onlineInstructions({ redirectUrl: 'https://pay.example.test/hosted?session=abc' }) }),
    );

    const panel = payPanel();

    expect(panel.querySelector('#pay-title')?.textContent?.trim()).toBe('Pay online');

    const links = panel.querySelectorAll<HTMLAnchorElement>('a');

    expect(links).toHaveLength(1);

    const link = links[0];

    expect(link.getAttribute('href')).toBe('https://pay.example.test/hosted?session=abc');
    expect(link.textContent?.trim()).toBe('Pay now');
    expect(link.getAttribute('rel')).toBe('noopener');
    expect(link.hasAttribute('target')).toBe(false);
    expect(link.classList.contains('btn')).toBe(true);
    expect(link.classList.contains('btn--primary')).toBe(true);

    expect(panel.querySelector('form')).toBeNull();
    expect(panel.querySelector('[data-testid="pay-due"]')?.textContent).toContain(
      '7 October 2026 at 10.00 am',
    );
    expectNoBankParts();
  });

  it('lets a local build reach a local provider stub over plain http, and nothing else', async () => {
    for (const url of ['http://localhost:5100/hosted', 'http://127.0.0.1:5100/hosted']) {
      await open(order({ payment: onlineInstructions({ redirectUrl: url }) }));

      expect(payPanel().querySelector('a')?.getAttribute('href'), url).toBe(url);
    }
  });

  it('prefers the form when the server sends both a form and a url', async () => {
    await open(
      order({
        payment: onlineInstructions({
          redirectUrl: 'https://pay.example.test/hosted',
          redirectForm: redirectForm(),
        }),
      }),
    );

    const panel = payPanel();

    expect(panel.querySelector('form')?.getAttribute('action')).toBe(REDIRECT_ACTION);
    expect(panel.querySelector('a')).toBeNull();
    expect(panel.querySelectorAll('button')).toHaveLength(1);
  });

  // ---- what an online provider says beside (or instead of) its button -----------------------------

  it('prints the provider\'s fields and notes beside the Pay now button, as text', async () => {
    await open(
      order({
        payment: onlineInstructions({
          redirectUrl: 'https://pay.example.test/hosted?session=abc',
          fields: [{ label: 'Amount charged', value: 'USD 53.55', copyable: false }],
          notes: 'The total is converted to dollars.\nNothing is charged until you approve it.',
        }),
      }),
    );

    const panel = payPanel();

    expect(panel.querySelector('[data-testid="pay-redirect-fields"]')?.textContent).toContain('Amount charged');
    expect(panel.querySelector('[data-testid="pay-amount-charged"]')?.textContent).toContain('USD 53.55');
    expect(panel.querySelector('[data-testid="pay-redirect-notes"]')?.textContent).toContain('Nothing is charged');
    expect(panel.querySelector('[data-testid="pay-now"]')).not.toBeNull();

    // No copy buttons: those belong to the bank's details.
    expect(panel.querySelector('button.pay__copy')).toBeNull();
  });

  it('renders the provider\'s notes as text and never as markup', async () => {
    await open(
      order({
        payment: onlineInstructions({
          redirectUrl: 'https://pay.example.test/hosted',
          notes: '<img src=x onerror=alert(1)> <b>bold</b>',
        }),
      }),
    );

    const notes = payPanel().querySelector('[data-testid="pay-redirect-notes"]')!;

    expect(notes.querySelector('img')).toBeNull();
    expect(notes.querySelector('b')).toBeNull();
    expect(notes.textContent).toContain('<img src=x onerror=alert(1)>');
  });

  it('draws a notice with no button when the provider has only something to say', async () => {
    await open(
      order({
        payment: onlineInstructions({
          notes: 'Your payment is being processed. Please wait a moment and reload this page.',
        }),
      }),
    );

    const panel = payPanel();

    expect(panel.querySelector('[data-testid="pay-redirect-notes"]')?.textContent).toContain('being processed');

    // Nothing to press, no "you will be taken to" promise, and none of the bank-only parts.
    expect(panel.querySelector('[data-testid="pay-now"]')).toBeNull();
    expect(panel.querySelector('form')).toBeNull();
    expect(panel.querySelector('.pay__lead')).toBeNull();
    expect(panel.querySelector('[data-testid="pay-redirect-unavailable"]')).toBeNull();
    expectNoBankParts();
  });

  it('draws a notice for a payment pending with the provider, and still offers the cancel panel', async () => {
    await open(
      order({
        payment: onlineInstructions({ notes: 'Your payment is pending. We hold your items until Friday.' }),
      }),
    );

    expect(payPanel().textContent).toContain('pending');
    expect(element.querySelector('[data-testid="cancel-panel"]')).not.toBeNull();
  });

  // ---- why the shopper is back: the hint on the address (payment-hint.ts) --------------------------------

  it('says why, once, when the payment page sent the shopper back', async () => {
    await open(order({ payment: onlineInstructions({ redirectUrl: 'https://pay.example.test/h' }) }), {
      pay: 'notapproved',
    });

    const hint = element.querySelector('[data-testid="pay-hint"]')!;

    expect(hint.textContent).toContain('nothing was charged');
    expect(hint.getAttribute('role')).toBe('status');
  });

  it('tells a shopper whose payment could not be matched that the team will contact them', async () => {
    await open(order({ status: 'Expired', statusLabel: 'Expired', payment: null, canCancel: false }), {
      pay: 'received',
    });

    expect(element.querySelector('[data-testid="pay-hint"]')?.textContent).toContain(
      'Our team will contact you',
    );
  });

  it('shows no hint without one, with an unknown one, or on a paid order', async () => {
    await open(order());
    expect(element.querySelector('[data-testid="pay-hint"]')).toBeNull();

    await open(order(), { pay: '<script>alert(1)</script>' });
    expect(element.querySelector('[data-testid="pay-hint"]')).toBeNull();
    expect(element.querySelector('script')).toBeNull();

    await open(order({ status: 'Paid', statusLabel: 'Paid', payment: null, canCancel: false }), {
      pay: 'closed',
    });
    expect(element.querySelector('[data-testid="pay-hint"]')).toBeNull();
  });

  // ---- money that arrived and could not settle the order (doc 11 P8 step 6, P8b step 4) ---------------------

  describe('a payment that arrived and could not settle the order', () => {
    const noticePanel = () => element.querySelector<HTMLElement>('[data-testid="pay-notice"]');

    /** Nothing that could take, or ask for, a second payment. */
    function expectNoWayToPay(): void {
      expect(element.querySelector('[data-testid="pay-panel"]')).toBeNull();
      expect(element.querySelector('[data-testid="pay-now"]')).toBeNull();
      expect(element.querySelector('[data-testid="pay-pending-details"]')).toBeNull();
      expect(element.querySelector('[data-testid="reference-input"]')).toBeNull();
      expect(element.querySelector('[data-testid="slip-input"]')).toBeNull();
      expect(element.querySelector('form')).toBeNull();
      expect(text()).not.toContain('Pay now');
      expect(text()).not.toContain('Transfer Rs.');
    }

    it('says the team will be in touch on an order that expired first, with no ?pay= on the address', async () => {
      await open(
        order({
          status: 'Expired',
          statusLabel: 'Expired',
          payment: null,
          canCancel: false,
          paymentNotice: 'received',
        }),
      );

      expect(noticePanel()?.querySelector('[data-testid="pay-notice-text"]')?.textContent?.trim()).toBe(
        'We received your payment, our team will contact you. You do not need to pay again.',
      );
      expect(noticePanel()?.querySelector('#pay-notice-title')?.textContent?.trim()).toBe('Your payment');
      expectNoWayToPay();
    });

    it('says it on a cancelled order too', async () => {
      await open(
        order({
          status: 'Cancelled',
          statusLabel: 'Cancelled',
          payment: null,
          canCancel: false,
          paymentNotice: 'received',
        }),
      );

      expect(noticePanel()?.textContent).toContain('We received your payment, our team will contact you');
      expectNoWayToPay();
    });

    it('replaces "still confirming the details" on an order that is still waiting (an amount that differed)', async () => {
      await open(order({ payment: null, paymentNotice: 'received' }));

      expect(noticePanel()?.textContent).toContain('We received your payment, our team will contact you');
      expect(text()).not.toContain('We are still confirming the payment details');
      expect(text()).not.toContain('nothing to transfer');

      // And the "Payment" line no longer invites a payment either.
      expect(text()).not.toContain('Awaiting payment.');
      expect(text()).toContain('We have received a payment for this order. Our team will contact you.');
      expectNoWayToPay();
    });

    it('never shows a Pay now button or the bank details, even if instructions arrive beside the notice', async () => {
      await open(
        order({
          payment: onlineInstructions({ redirectUrl: 'https://pay.example.test/h' }),
          paymentNotice: 'received',
        }),
      );

      expect(noticePanel()).not.toBeNull();
      expectNoWayToPay();
      expect(element.querySelector('a[href^="https://pay.example.test"]')).toBeNull();

      await open(order({ payment: bankInstructions(), paymentNotice: 'received' }));

      expect(noticePanel()).not.toBeNull();
      expectNoWayToPay();
      expect(text()).not.toContain('8001234567890');
    });

    it('says a second payment arrived when the order is already paid', async () => {
      await open(
        order({
          status: 'Paid',
          statusLabel: 'Paid',
          payment: null,
          canCancel: false,
          paymentNotice: 'received',
        }),
      );

      const said = noticePanel()?.textContent ?? '';

      expect(said).toContain('We received a second payment for this order');
      expect(said).not.toContain('We received your payment, our team');
      expect(text()).toContain('Paid.');
    });

    it('shows it once when the shopper also came back with ?pay=received', async () => {
      await open(
        order({
          status: 'Expired',
          statusLabel: 'Expired',
          payment: null,
          canCancel: false,
          paymentNotice: 'received',
        }),
        { pay: 'received' },
      );

      expect(noticePanel()).not.toBeNull();
      expect(element.querySelector('[data-testid="pay-hint"]')).toBeNull();
      expect(text().match(/our team will contact you/gi)).toHaveLength(1);
    });

    it('still explains other hints beside it', async () => {
      await open(
        order({
          status: 'Expired',
          statusLabel: 'Expired',
          payment: null,
          canCancel: false,
          paymentNotice: 'received',
        }),
        { pay: 'closed' },
      );

      expect(noticePanel()).not.toBeNull();
      expect(element.querySelector('[data-testid="pay-hint"]')?.textContent).toBe(
        'This order can no longer be paid.',
      );
    });

    it('shows nothing for a code it does not know, and leaves the bank panel as it was', async () => {
      await open(order({ paymentNotice: 'something-newer' }));

      expect(noticePanel()).toBeNull();
      expect(payPanel().querySelector('#pay-title')?.textContent?.trim()).toBe('How to pay');
      expect(payPanel().querySelector('[data-testid="reference-input"]')).not.toBeNull();
    });

    it('leaves the bank page and a provider-less order exactly as they were without a notice', async () => {
      await open(order({ paymentNotice: null }));

      expect(noticePanel()).toBeNull();
      expect(payPanel().querySelector('#pay-title')?.textContent?.trim()).toBe('How to pay');
      expect(text()).toContain('Awaiting payment.');

      await open(order({ payment: null }));

      expect(noticePanel()).toBeNull();
      expect(element.querySelector('[data-testid="pay-pending-details"]')).not.toBeNull();
    });

    it('prints the notice as text, never the code or any markup', async () => {
      await open(
        order({
          status: 'Expired',
          statusLabel: 'Expired',
          payment: null,
          canCancel: false,
          paymentNotice: 'received',
        }),
      );

      expect(noticePanel()?.innerHTML).not.toContain('<script');
      expect(noticePanel()?.textContent).not.toContain('received"');
      expect(noticePanel()?.querySelectorAll('a, button, input, form')).toHaveLength(0);
    });
  });

  describe('after a "being processed" return', () => {
    beforeEach(() => {
      vi.useFakeTimers();
    });

    afterEach(() => {
      vi.useRealTimers();
    });

    const processing = () =>
      order({ payment: onlineInstructions({ notes: 'Your payment is being processed.' }) });

    it('asks for the order again every few seconds until it is paid', async () => {
      await open(processing(), { pay: 'processing' });

      // Nothing yet: the first look is a few seconds after the page.
      http.expectNone((request) => request.url === `${CheckoutApi.orderUrl}/${TOKEN}`);

      await vi.advanceTimersByTimeAsync(5_000);
      orderRequest().flush(processing());
      await settle();

      await vi.advanceTimersByTimeAsync(5_000);
      orderRequest().flush(order({ status: 'Paid', statusLabel: 'Paid', payment: null, canCancel: false }));
      await settle();

      expect(text()).toContain('Paid.');

      // And it has stopped: the order no longer waits for anything.
      await vi.advanceTimersByTimeAsync(60_000);
      http.expectNone((request) => request.url === `${CheckoutApi.orderUrl}/${TOKEN}`);
    });

    it('gives up after a minute rather than asking for ever', async () => {
      await open(processing(), { pay: 'processing' });

      for (let attempt = 0; attempt < 12; attempt++) {
        await vi.advanceTimersByTimeAsync(5_000);
        orderRequest().flush(processing());
        await settle();
      }

      await vi.advanceTimersByTimeAsync(60_000);
      http.expectNone((request) => request.url === `${CheckoutApi.orderUrl}/${TOKEN}`);
    });

    it('does not poll for any other hint, or none', async () => {
      await open(processing(), { pay: 'pending' });
      await vi.advanceTimersByTimeAsync(30_000);
      http.expectNone((request) => request.url === `${CheckoutApi.orderUrl}/${TOKEN}`);

      await open(processing());
      await vi.advanceTimersByTimeAsync(30_000);
      http.expectNone((request) => request.url === `${CheckoutApi.orderUrl}/${TOKEN}`);
    });
  });

  it('keeps the live region and the cancel panel beside a redirect panel', async () => {
    await open(order({ payment: onlineInstructions({ redirectForm: redirectForm() }) }));

    expect(element.querySelector('[data-testid="pay-announcement"]')).not.toBeNull();
    expect(element.querySelector('[data-testid="cancel-panel"]')).not.toBeNull();
  });

  it('draws no redirect panel once the order is no longer waiting for payment', async () => {
    await open(
      order({
        status: 'Paid',
        statusLabel: 'Paid',
        payment: onlineInstructions({ redirectForm: redirectForm() }),
        canCancel: false,
      }),
    );

    expect(element.querySelector('[data-testid="pay-panel"]')).toBeNull();
    expect(element.querySelector('form')).toBeNull();
  });

  /**
   * **Addresses the page will not follow.** Angular's sanitiser already rewrites a `javascript:` action
   * into `unsafe:...`, but it still renders the control - a payment button that goes nowhere. The page
   * refuses first, so the shopper is told the page is unavailable instead, and there is nothing to press.
   */
  const REFUSED: readonly (readonly [string, OrderPaymentInstructionsDto])[] = [
    [
      'a javascript: form action',
      onlineInstructions({ redirectForm: redirectForm({ action: 'javascript:alert(1)' }) }),
    ],
    [
      'a data: form action',
      onlineInstructions({
        redirectForm: redirectForm({ action: 'data:text/html,<script>alert(1)</script>' }),
      }),
    ],
    [
      'a relative form action',
      onlineInstructions({ redirectForm: redirectForm({ action: '/pay/checkout' }) }),
    ],
    [
      'a protocol-relative form action',
      onlineInstructions({ redirectForm: redirectForm({ action: '//evil.example/pay' }) }),
    ],
    [
      'a plain-http form action on a public host',
      onlineInstructions({ redirectForm: redirectForm({ action: 'http://evil.example/pay' }) }),
    ],
    [
      'a form action carrying credentials',
      onlineInstructions({
        redirectForm: redirectForm({ action: 'https://pay.example.test@evil.example/pay' }),
      }),
    ],
    [
      'a form action that is not a url',
      onlineInstructions({ redirectForm: redirectForm({ action: 'not a url' }) }),
    ],
    [
      'a form that is not a POST',
      onlineInstructions({ redirectForm: redirectForm({ method: 'GET' }) }),
    ],
    [
      'a javascript: redirect url',
      onlineInstructions({ redirectUrl: 'javascript:alert(document.cookie)' }),
    ],
    ['a relative redirect url', onlineInstructions({ redirectUrl: '/pay/checkout' })],
    [
      'a protocol-relative redirect url',
      onlineInstructions({ redirectUrl: '//evil.example/pay' }),
    ],
    [
      'a plain-http redirect url on a public host',
      onlineInstructions({ redirectUrl: 'http://evil.example/pay' }),
    ],
    [
      'a plain-http redirect url on a look-alike of localhost',
      onlineInstructions({ redirectUrl: 'http://localhost.evil.example/pay' }),
    ],
    ['a redirect url that is not a url', onlineInstructions({ redirectUrl: 'pay me' })],
    [
      'a bad form action even though a good redirect url came with it',
      // The form wins, and a GET to the bare address would open the provider without its fields.
      onlineInstructions({
        redirectUrl: 'https://pay.example.test/hosted',
        redirectForm: redirectForm({ action: 'javascript:alert(1)' }),
      }),
    ],
  ];

  for (const [label, payment] of REFUSED) {
    it(`renders nothing to follow, and says so, for ${label}`, async () => {
      await open(order({ payment }));

      const panel = payPanel();

      expect(panel.querySelector('[data-testid="pay-redirect-unavailable"]')).not.toBeNull();
      expect(panel.querySelector('#pay-title')?.textContent?.trim()).toBe('Pay online');
      expect(panel.querySelector('[data-testid="pay-due"]')?.textContent).toContain(
        '7 October 2026 at 10.00 am',
      );

      expectNothingToFollow();
      expectNoBankParts();

      // Not in the markup in any form - including the `unsafe:` prefix Angular's sanitiser would add.
      const markup = element.innerHTML;

      expect(markup).not.toContain('javascript:');
      expect(markup).not.toContain('unsafe:');
      expect(markup).not.toContain('evil.example');
    });
  }

  /**
   * A refused address does not take real bank details away. Instructions that carry bank content are still
   * telling the truth about it, so the bank panel is drawn - with nothing to follow.
   */
  it('falls back to the bank panel when a refused address comes with real bank details', async () => {
    await open(order({ payment: bankInstructions({ redirectUrl: 'javascript:alert(1)' }) }));

    const panel = payPanel();

    expect(panel.querySelector('#pay-title')?.textContent?.trim()).toBe('How to pay');
    expect(panel.querySelector('[data-testid="pay-account-number"]')).not.toBeNull();
    expect(panel.querySelector('form')).toBeNull();
    expect(panel.querySelector('a')).toBeNull();
    expect(element.innerHTML).not.toContain('unsafe:');
  });

  /**
   * **The bank rendering is unchanged when neither redirect field is set.** Absent, null, empty and
   * blank all mean "no redirect", and all four must produce the same panel, byte for byte.
   */
  it('keeps the bank rendering exactly as it was when no redirect is sent', async () => {
    await open(order());

    const panel = payPanel();
    const baseline = panel.innerHTML;

    expect(panel.querySelector('#pay-title')?.textContent?.trim()).toBe('How to pay');
    expect(panel.querySelector('.pay__lead')?.textContent?.replace(/\s+/g, ' ').trim()).toBe(
      'Transfer Rs. 9,184 to the account below by 7 October 2026 at 10.00 am, quoting your order '
        + 'number as the reference. We hold your items until then.',
    );
    expect(panel.querySelectorAll('dl.pay__details > .pay__detail')).toHaveLength(5);
    expect(panel.querySelector('[data-testid="copy-reference"]')).not.toBeNull();
    expect(panel.querySelector('[data-testid="copy-pay-account-number"]')).not.toBeNull();
    expect(panel.querySelector('.pay__sub')?.textContent).toContain('Tell us about your transfer');
    expect(panel.querySelector('[data-testid="reference-input"]')).not.toBeNull();
    expect(panel.querySelector('[data-testid="save-reference"]')).not.toBeNull();
    expect(panel.querySelector('[data-testid="slip-input"]')).not.toBeNull();
    expect(panel.querySelector('form')).toBeNull();
    expect(panel.querySelector('a')).toBeNull();
    expect(panel.textContent).not.toContain('Pay now');
    expect(panel.textContent).not.toContain('Pay online');

    for (const none of [
      { redirectForm: null },
      { redirectUrl: '' },
      { redirectUrl: '   ' },
      { redirectForm: undefined },
    ] satisfies Partial<OrderPaymentInstructionsDto>[]) {
      await open(order({ payment: bankInstructions(none) }));

      expect(payPanel().innerHTML, JSON.stringify(none)).toBe(baseline);
    }
  });

  it('never calls a bank transfer an online payment', async () => {
    await open(order());

    expect(text()).not.toContain('secure payment page');
    expect(text()).not.toContain('Pay online');
  });

  /**
   * Everything on the page outside the pay panel is already neutral - "Awaiting payment", "your order is
   * held" - so a redirect provider needs no wording change there, and none was made. This pins it: the
   * only words that name a bank transfer are inside the bank panel.
   */
  it('has no bank-transfer wording anywhere on the page for a redirect provider', async () => {
    // The method's own name comes from the server (`PaymentMethods.Name`), so the fixture gives it the name
    // an online provider would have rather than the bank method's.
    await open(
      order({
        paymentMethodCode: 'OnlineProvider',
        paymentMethodName: 'Pay online',
        payment: onlineInstructions({ redirectForm: redirectForm() }),
      }),
    );

    expect(text()).toContain('Awaiting payment. We hold your items until 7 October 2026 at 10.00 am.');
    expect(text()).not.toMatch(/transfer|bank statement|account number|slip/i);
  });
});
