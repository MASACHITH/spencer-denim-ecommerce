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
import { OrderConfirmationDto, OrderLineDto } from '../../core/checkout/checkout.model';

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
   * Doc 11 P7 — the bank details, the reference to quote and the slip upload — is W3-03's step 3, and
   * ACC-03's emails are W3-06. This page must not promise any of it.
   */
  it('promises no bank details and claims no email has been sent', async () => {
    await open(order());

    const body = text().toLowerCase();

    expect(body).not.toContain('account number');
    expect(body).not.toContain('sort code');
    expect(body).not.toContain('upload');
    expect(body).not.toContain('reference to quote');
    expect(body).not.toContain('we have emailed');
    expect(body).not.toContain('check your inbox');
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
