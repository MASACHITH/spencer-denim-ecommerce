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

  function create(): void {
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
            snapshot: { params: { token: TOKEN }, paramMap: convertToParamMap({ token: TOKEN }) },
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

  async function open(payload: OrderConfirmationDto): Promise<void> {
    create();
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
    await open(order({ status: 'Paid', payment: null, proof: null }));

    expect(element.querySelector('[data-testid="pay-panel"]')).toBeNull();
    expect(text()).toContain('Paid.');
    expect(text()).not.toContain('Awaiting payment');
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
});
