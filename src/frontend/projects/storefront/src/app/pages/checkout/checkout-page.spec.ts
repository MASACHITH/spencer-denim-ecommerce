import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  TestRequest,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { RESPONSE_INIT } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';

import { CheckoutPage } from './checkout-page';
import { CartApi } from '../../core/cart/cart-api';
import { CheckoutApi } from '../../core/checkout/checkout-api';
import {
  CheckoutLineDto,
  CheckoutSessionDto,
  CheckoutTotalsDto,
  DeliveryOptionDto,
  DistrictDto,
  OrderConfirmationDto,
  PaymentMethodDto,
} from '../../core/checkout/checkout.model';

const COLOMBO: DistrictDto = { id: 1, name: 'Colombo', deliveryZone: 1 };
const JAFFNA: DistrictDto = { id: 10, name: 'Jaffna', deliveryZone: 3 };

function option(overrides: Partial<DeliveryOptionDto> = {}): DeliveryOptionDto {
  return {
    id: 1,
    code: 'STANDARD',
    name: 'Standard delivery',
    description: 'Delivered by courier to your address.',
    requiresAddress: true,
    isAvailable: true,
    fee: 400,
    freeAboveAmount: 10_000,
    feeAfterDiscount: 400,
    estimatedDaysMin: 1,
    estimatedDaysMax: 2,
    ...overrides,
  };
}

function line(overrides: Partial<CheckoutLineDto> = {}): CheckoutLineDto {
  return {
    variantId: 101,
    productId: 10,
    productName: 'Relaxed Linen Blend Shirt',
    slug: 'relaxed-linen-blend-shirt',
    colourName: 'Beige',
    sizeLabel: 'M',
    imageUrl: '/media/public/renditions/a/thumb.webp',
    imageAltText: 'A beige linen shirt',
    quantity: 2,
    availableQuantity: 12,
    unitOriginalPrice: 6990,
    unitPrice: 4392,
    lineSubtotal: 8784,
    status: 'Buyable',
    ...overrides,
  };
}

/**
 * Doc 08 §6's worked example line 1, priced for Standard to Colombo: original 13,980, subtotal 8,784,
 * savings 5,196, fee 400 (below the 10,000 threshold), total 9,184.
 */
function totals(overrides: Partial<CheckoutTotalsDto> = {}): CheckoutTotalsDto {
  return {
    itemCount: 2,
    lineCount: 1,
    itemsOriginalTotal: 13_980,
    itemsSubtotal: 8_784,
    savedTotal: 5_196,
    promoDiscountAmount: 0,
    deliveryFeeAmount: 400,
    deliveryDiscountAmount: 0,
    grandTotal: 9_184,
    promoDiscountClamped: false,
    currency: 'LKR',
    ...overrides,
  };
}

const BANK: PaymentMethodDto = {
  code: 'BankTransfer',
  displayName: 'Bank or online transfer',
  checkoutDescription: 'Place your order now and transfer the total to our bank account.',
};

function session(overrides: Partial<CheckoutSessionDto> = {}): CheckoutSessionDto {
  return {
    checkoutKey: '11111111-1111-4111-8111-111111111111',
    lines: [line()],
    totals: totals(),
    districts: [COLOMBO, JAFFNA],
    deliveryOptions: [option()],
    pickupLocations: [],
    paymentMethods: [BANK],
    selectedDistrictId: COLOMBO.id,
    selectedDeliveryMethodId: 1,
    blockers: [],
    ...overrides,
  };
}

/** A checkout with nothing chosen yet: no district, so no fee and no total. */
function unquoted(): CheckoutSessionDto {
  return session({
    deliveryOptions: [],
    selectedDistrictId: null,
    selectedDeliveryMethodId: null,
    totals: totals({ deliveryFeeAmount: null, grandTotal: null }),
  });
}

const EMPTY: CheckoutSessionDto = session({
  lines: [],
  totals: totals({
    itemCount: 0,
    lineCount: 0,
    itemsOriginalTotal: 0,
    itemsSubtotal: 0,
    savedTotal: 0,
    deliveryFeeAmount: null,
    grandTotal: null,
  }),
  deliveryOptions: [],
  selectedDistrictId: null,
  selectedDeliveryMethodId: null,
  blockers: ['EmptyBag'],
});

function confirmation(): OrderConfirmationDto {
  return {
    orderNumber: 'SD-2026-000123',
    publicId: '22222222-2222-4222-8222-222222222222',
    status: 'PendingPayment',
    email: 'nimali@example.com',
    firstName: 'Nimali',
    lastName: 'Perera',
    phone: '0771234567',
    lines: [],
    delivery: {
      methodCode: 'STANDARD',
      methodName: 'Standard delivery',
      pickupLocationName: null,
      recipientName: 'Nimali Perera',
      phone: '0771234567',
      line1: '14 Galle Road',
      line2: null,
      city: 'Colombo',
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
  };
}

describe('CheckoutPage (CHK-01..03)', () => {
  let fixture: ComponentFixture<CheckoutPage>;
  let element: HTMLElement;
  let http: HttpTestingController;
  let response: ResponseInit;
  let navigated: unknown[][];

  async function settle(): Promise<void> {
    fixture.detectChanges();
    await fixture.whenStable();
  }

  function create(): void {
    response = {};
    navigated = [];
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: RESPONSE_INIT, useValue: response },
      ],
    });

    http = TestBed.inject(HttpTestingController);

    const router = TestBed.inject(Router);
    // Recorded rather than performed: the success page is a different component with its own test, and a
    // real navigation in a unit test would start fetching an order.
    router.navigate = (commands: unknown[]) => {
      navigated.push(commands);
      return Promise.resolve(true);
    };

    fixture = TestBed.createComponent(CheckoutPage);
    element = fixture.nativeElement as HTMLElement;
  }

  function checkoutRequest(): TestRequest {
    return http.expectOne(
      (request) => request.url === CheckoutApi.url && request.method === 'GET',
    );
  }

  async function openWith(payload: CheckoutSessionDto): Promise<void> {
    create();
    await settle();
    checkoutRequest().flush(payload);
    await settle();
  }

  function text(): string {
    return element.textContent ?? '';
  }

  function field(id: string): HTMLInputElement | HTMLTextAreaElement {
    const node = element.querySelector<HTMLInputElement | HTMLTextAreaElement>(`#${id}`);
    if (!node) {
      throw new Error(`No field #${id} is rendered.`);
    }
    return node;
  }

  function placeButton(): HTMLButtonElement {
    const node = element.querySelector<HTMLButtonElement>('.checkout__place');
    if (!node) {
      throw new Error('No Place order button is rendered.');
    }
    return node;
  }

  async function type(id: string, value: string): Promise<void> {
    const input = field(id);
    input.value = value;
    input.dispatchEvent(new Event('input', { bubbles: true }));
    await settle();
  }

  async function choosePayment(code: string): Promise<void> {
    const radio = element.querySelector<HTMLInputElement>(
      `input[name="paymentMethodCode"][value="${code}"]`,
    );
    if (!radio) {
      throw new Error(`No payment radio for ${code}.`);
    }
    radio.click();
    await settle();
  }

  /** Fills in everything a valid Colombo delivery needs. */
  async function fillValidForm(): Promise<void> {
    await type('co-firstName', 'Nimali');
    await type('co-email', 'nimali@example.com');
    await type('co-phone', '0771234567');
    await type('co-recipient', 'Nimali Perera');
    await type('co-address-phone', '0771234567');
    await type('co-line1', '14 Galle Road');
    await type('co-city', 'Colombo');
    await choosePayment('BankTransfer');
  }

  /**
   * Every money figure in the summary, **read back off the rendered DOM** rather than taken from the
   * fixture, so the assertion is about what a shopper sees. A row that is not rendered reads as 0, which
   * is what makes the no-savings and no-discount cases assertable with the same helper.
   *
   * The helper is keyed on `data-sum` rather than on the label text: the labels are prose and matching
   * them by string would couple the test to wording somebody will improve.
   */
  function sums(): {
    subtotal: number;
    savings: number;
    promo: number;
    delivery: number;
    deliveryDiscount: number;
    total: number;
  } {
    const read = (name: string): number => {
      const node = element.querySelector(`[data-sum="${name}"]`);
      if (!node) {
        return 0;
      }
      // `Rs. 8,784` — the prefix carries a full stop, so "strip everything but digits and a dot" would
      // turn it into 0.8784. Match the number instead and drop the group separators. The magnitude, not
      // the sign: a deduction renders U+2212 and is subtracted by the caller.
      const matched = /[\d,]+(?:\.\d+)?/.exec((node.textContent ?? '').replace('Rs.', ''));
      return matched ? Number(matched[0].replace(/,/g, '')) : 0;
    };

    return {
      subtotal: read('subtotal'),
      savings: read('savings'),
      promo: read('promo'),
      delivery: read('delivery'),
      deliveryDiscount: read('delivery-discount'),
      total: read('total'),
    };
  }

  /**
   * Answers the bag read that a successful placement triggers.
   *
   * The page calls `CartStore.retry()` after the order is placed, because the placement transaction
   * deleted the bag's lines and the header badge would otherwise keep counting the items that have just
   * been bought. Draining it here rather than ignoring it: `HttpTestingController.verify()` fails on an
   * outstanding request, which is what makes "the badge is told" an assertion rather than a hope.
   */
  function drainCartRead(): void {
    http.expectOne((candidate) => candidate.url === CartApi.url).flush({
      lines: [],
      totals: {
        itemCount: 0,
        buyableItemCount: 0,
        lineCount: 0,
        itemsOriginalTotal: 0,
        itemsSubtotal: 0,
        savedTotal: 0,
        currency: 'LKR',
      },
    });
  }

  afterEach(() => TestBed.inject(HttpTestingController).verify());

  describe('the request and the response it belongs to', () => {
    /**
     * From the constructor, not from an effect: the server render waits on this request, so the HTML
     * carries the bag and the form rather than a spinner.
     */
    it('asks for the checkout before the first change detection', () => {
      create();
      checkoutRequest().flush(EMPTY);
    });

    /**
     * **This page's markup is the most personal in the storefront**: a name, an email address, a phone
     * number, a street address and the token that places the order. A shared cache holding it would hand
     * one shopper another shopper's details.
     */
    it('answers no-store, private and Vary: Cookie', async () => {
      await openWith(EMPTY);

      const headers = new Headers(response.headers);
      expect(headers.get('Cache-Control')).toContain('no-store');
      expect(headers.get('Cache-Control')).toContain('private');
      expect(headers.get('Vary')).toBe('Cookie');
    });

    it('asks not to be indexed, and clears the tag on the way out', async () => {
      await openWith(EMPTY);

      expect(document.querySelector('meta[name="robots"]')?.getAttribute('content')).toBe('noindex');

      fixture.destroy();
      expect(document.querySelector('meta[name="robots"]')).toBeNull();
    });

    /**
     * Doc 08 §3.5 R7's two fallbacks are both unreachable — there is no customer sign-in (ACC-01) and no
     * `ops.Settings` (ADM-22) — so the page must not guess a district. A guessed district is a guessed
     * zone, and a guessed zone is a delivery fee the shop has not agreed to.
     */
    it('asks for the checkout with no district until one is chosen', () => {
      create();
      const request = checkoutRequest();

      expect(request.request.params.has('districtId')).toBe(false);
      expect(request.request.params.has('deliveryMethodId')).toBe(false);

      request.flush(unquoted());
    });
  });

  describe('the order summary', () => {
    /**
     * **The ladder has to add up, and three `toContain`s cannot say that.** This project has already
     * shipped a summary in which Subtotal − Discount did not equal Total, and every figure here is read
     * back off the screen and put through the identity rather than compared with the fixture.
     */
    it('adds up: Subtotal − Savings − Discount + Delivery − Free delivery = Total', async () => {
      await openWith(session());

      const { subtotal, savings, promo, delivery, deliveryDiscount, total } = sums();

      expect(subtotal - savings - promo + delivery - deliveryDiscount).toBe(total);
    });

    it('prints the figures doc 08 §6 prices for this bag', async () => {
      await openWith(session());

      expect(sums()).toEqual({
        subtotal: 13_980,
        savings: 5_196,
        promo: 0,
        delivery: 400,
        deliveryDiscount: 0,
        total: 9_184,
      });
    });

    /**
     * The same identity with a free-delivery threshold met, because that is the case where a careless
     * implementation prints the fee in the Delivery row *and* forgets the deduction row — leaving a total
     * that is lower than the arithmetic on screen.
     */
    it('still adds up when the free-delivery threshold is met', async () => {
      await openWith(
        session({
          deliveryOptions: [option({ feeAfterDiscount: 0 })],
          totals: totals({
            itemsSubtotal: 12_000,
            itemsOriginalTotal: 15_000,
            savedTotal: 3_000,
            deliveryFeeAmount: 400,
            deliveryDiscountAmount: 400,
            grandTotal: 12_000,
          }),
        }),
      );

      const { subtotal, savings, promo, delivery, deliveryDiscount, total } = sums();

      expect(delivery).toBe(400);
      expect(deliveryDiscount).toBe(400);
      expect(subtotal - savings - promo + delivery - deliveryDiscount).toBe(total);
    });

    /**
     * **Null is not zero.** Doc 08 §6 step 10 has no fee without a district and a method, and a Delivery
     * row reading `Rs. 0` would be a promise of free delivery.
     */
    it('says a district is needed rather than printing a delivery fee of zero', async () => {
      await openWith(unquoted());

      expect(element.querySelector('[data-sum="delivery"]')).toBeNull();
      expect(element.querySelector('[data-sum="total"]')).toBeNull();
      expect(element.querySelector('[data-sum="delivery-pending"]')?.textContent).toContain(
        'Choose a district',
      );
      expect(element.querySelector('[data-sum="total-pending"]')?.textContent).toContain(
        'Choose a district',
      );
      expect(text()).not.toContain('Rs. 0');
    });

    /**
     * The bag settled on these three labels after shipping a ladder whose subtraction was false, and a
     * shopper moving from the bag to checkout must not meet the same word meaning something else.
     */
    it('labels the deduction Savings, and hides it when there is nothing saved', async () => {
      await openWith(session());
      expect(text()).toContain('Savings');

      await openWith(
        session({
          totals: totals({ itemsOriginalTotal: 8_784, savedTotal: 0 }),
        }),
      );
      expect(element.querySelector('[data-sum="savings"]')).toBeNull();
    });
  });

  describe('the delivery options', () => {
    it('shows the fee, the estimate and the free-delivery threshold', async () => {
      await openWith(session());

      expect(text()).toContain('Standard delivery');
      expect(text()).toContain('Rs. 400');
      expect(text()).toContain('Estimated 1–2 days');
      expect(text()).toContain('Free above Rs. 10,000');
    });

    /**
     * Doc 08 §3.5: "a missing row means unavailable", and §9 makes Express island-wide exactly that. The
     * option is listed and disabled rather than dropped — a shopper who used it last week would read a
     * silently shorter list as a bug.
     */
    it('lists an unavailable option, disabled, and does not price it', async () => {
      await openWith(
        session({
          deliveryOptions: [
            option(),
            option({
              id: 2,
              code: 'EXPRESS',
              name: 'Express delivery',
              isAvailable: false,
              fee: null,
              freeAboveAmount: null,
              feeAfterDiscount: null,
              estimatedDaysMin: null,
              estimatedDaysMax: null,
            }),
          ],
        }),
      );

      const express = element.querySelector<HTMLInputElement>(
        'input[name="deliveryMethodId"][value="2"]',
      );

      expect(express).not.toBeNull();
      expect(express!.disabled).toBe(true);
      expect(text()).toContain('Express delivery');
      expect(text()).toContain('Not available here');
    });

    it('says Free rather than Rs. 0 once the threshold is met', async () => {
      await openWith(session({ deliveryOptions: [option({ feeAfterDiscount: 0 })] }));

      const choice = element.querySelector('.checkout__choice-fee');
      expect(choice?.textContent?.trim()).toBe('Free');
    });

    /**
     * Doc 11 P6: an option that is not offered in the new district must be chosen again, not carried
     * over — the server would refuse it, and the shopper would meet the refusal after pressing Place
     * order rather than before.
     */
    it('re-quotes and drops the chosen method when the district changes', async () => {
      await openWith(session());

      const select = element.querySelector<HTMLSelectElement>('#co-district')!;
      select.value = String(JAFFNA.id);
      select.dispatchEvent(new Event('change', { bubbles: true }));
      await settle();

      const request = checkoutRequest();
      expect(request.request.params.get('districtId')).toBe(String(JAFFNA.id));
      expect(request.request.params.has('deliveryMethodId')).toBe(false);

      request.flush(
        session({
          selectedDistrictId: JAFFNA.id,
          selectedDeliveryMethodId: null,
          deliveryOptions: [option({ fee: 600, feeAfterDiscount: 600, freeAboveAmount: 15_000 })],
          totals: totals({ deliveryFeeAmount: null, grandTotal: null }),
        }),
      );
      await settle();

      expect(placeButton().disabled).toBe(true);
    });
  });

  describe('the empty and blocked states', () => {
    it('says the bag is empty rather than showing a form', async () => {
      await openWith(EMPTY);

      expect(text()).toContain('Your bag is empty');
      expect(element.querySelector('#co-firstName')).toBeNull();
    });

    /**
     * G-08: warn and block. The server applies the same rule, and the page offering a button the server
     * will refuse is the version of this that wastes a shopper's time.
     */
    it('blocks checkout and points back to the bag when a line cannot be bought', async () => {
      await openWith(
        session({
          lines: [line({ status: 'NotEnoughStock', availableQuantity: 1 })],
          blockers: ['LineNotPurchasable'],
        }),
      );

      expect(text()).toContain('cannot be bought right now');
      expect(text()).toContain('Only 1 left');
      expect(placeButton().disabled).toBe(true);
      expect(element.querySelector('a[href="/bag"]')).not.toBeNull();
    });

    it('says so when no delivery option is available for the district', async () => {
      await openWith(
        session({
          deliveryOptions: [],
          selectedDeliveryMethodId: null,
          blockers: ['NoDeliveryMethod'],
          totals: totals({ deliveryFeeAmount: null, grandTotal: null }),
        }),
      );

      expect(text()).toContain('cannot deliver to that district');
      expect(placeButton().disabled).toBe(true);
    });
  });

  describe('placing the order', () => {
    /**
     * G-10's guard travels with the request, so the server can refuse an order at a total the shopper
     * has not seen. A request without it would be refused by the server, which is the point.
     */
    it('sends the displayed total, the pinned checkout key and the trimmed fields', async () => {
      await openWith(session());
      await fillValidForm();
      await type('co-firstName', '  Nimali  ');

      expect(placeButton().disabled).toBe(false);
      placeButton().click();
      await settle();

      const request = http.expectOne(
        (candidate) =>
          candidate.url === `${CheckoutApi.url}/orders` && candidate.method === 'POST',
      );

      expect(request.request.body).toMatchObject({
        checkoutKey: '11111111-1111-4111-8111-111111111111',
        firstName: 'Nimali',
        email: 'nimali@example.com',
        deliveryMethodId: 1,
        districtId: COLOMBO.id,
        pickupLocationId: null,
        paymentMethodCode: 'BankTransfer',
        expectedGrandTotal: 9_184,
      });
      expect(request.request.body.address).toMatchObject({ line1: '14 Galle Road', city: 'Colombo' });

      request.flush(confirmation());
      await settle();
      drainCartRead();
    });

    /**
     * **The token is pinned at the first read and must survive a re-quote.** Every GET answers with a
     * fresh one; taking the newest would mean that changing the district rotated the token, so a shopper
     * who changed district and then double-clicked would have sent two different tokens — and placed two
     * orders. This is the test that would have caught that.
     */
    it('keeps the first checkout key across a re-quote', async () => {
      await openWith(session());

      const select = element.querySelector<HTMLSelectElement>('#co-district')!;
      select.value = String(JAFFNA.id);
      select.dispatchEvent(new Event('change', { bubbles: true }));
      await settle();

      checkoutRequest().flush(
        session({
          // A different key, as a real second GET would send.
          checkoutKey: '99999999-9999-4999-8999-999999999999',
          selectedDistrictId: JAFFNA.id,
        }),
      );
      await settle();

      await fillValidForm();
      placeButton().click();
      await settle();

      const request = http.expectOne(
        (candidate) =>
          candidate.url === `${CheckoutApi.url}/orders` && candidate.method === 'POST',
      );

      expect(request.request.body.checkoutKey).toBe('11111111-1111-4111-8111-111111111111');

      request.flush(confirmation());
      await settle();
      drainCartRead();
    });

    it('goes to the order page, replacing the checkout in history', async () => {
      await openWith(session());
      await fillValidForm();
      placeButton().click();
      await settle();

      http
        .expectOne(
          (candidate) =>
            candidate.url === `${CheckoutApi.url}/orders` && candidate.method === 'POST',
        )
        .flush(confirmation());
      await settle();

      expect(navigated).toEqual([['/order', '22222222-2222-4222-8222-222222222222']]);

      // The bag has been emptied by the placement transaction, so the header badge is told.
      drainCartRead();
    });

    /** A second press while the first request is in flight must not send a second order. */
    it('refuses a second press while the first is in flight', async () => {
      await openWith(session());
      await fillValidForm();

      placeButton().click();
      await settle();

      expect(placeButton().disabled).toBe(true);
      expect(text()).toContain('Placing your order…');

      placeButton().click();
      await settle();

      http
        .expectOne(
          (candidate) =>
            candidate.url === `${CheckoutApi.url}/orders` && candidate.method === 'POST',
        )
        .flush(confirmation());
      await settle();

      expect(navigated).toHaveLength(1);
      drainCartRead();
    });

    it('does not submit, and says what is missing, when a required field is blank', async () => {
      await openWith(session());
      await choosePayment('BankTransfer');

      expect(placeButton().disabled).toBe(true);

      // Pressed anyway, as a keyboard Enter in a field would: nothing is sent and the reasons appear.
      element.querySelector('form')!.dispatchEvent(new Event('submit', { bubbles: true }));
      await settle();

      expect(text()).toContain('Enter your first name.');
      expect(text()).toContain('Enter your email address.');
      expect(field('co-firstName').getAttribute('aria-invalid')).toBe('true');
      http.expectNone(
        (candidate) =>
          candidate.url === `${CheckoutApi.url}/orders` && candidate.method === 'POST',
      );
    });

    it('refuses an email that is not an address, with the same rule as the server', async () => {
      await openWith(session());
      await fillValidForm();
      await type('co-email', '0771234567');

      expect(placeButton().disabled).toBe(true);
      expect(text()).toContain('email address we can send your order to');
    });
  });

  describe('when the server refuses it', () => {
    /**
     * **The stub refuses what the real API refuses.** A 400 from this endpoint is a `ValidationProblem`
     * with camelCase field keys, including the nested `address.line1`, and the page has to put each
     * message against its own input — a stub that answered 200, or that invented a flat message, would
     * leave this path untested in exactly the way the brief warns about.
     */
    it('puts each server field error against its own input', async () => {
      await openWith(session());
      await fillValidForm();
      placeButton().click();
      await settle();

      http
        .expectOne(
          (candidate) =>
            candidate.url === `${CheckoutApi.url}/orders` && candidate.method === 'POST',
        )
        .flush(
          {
            type: 'https://tools.ietf.org/html/rfc9110#section-15.5.1',
            title: 'One or more validation errors occurred.',
            status: 400,
            errors: {
              email: ['Enter an email address we can send your order to.'],
              'address.line1': ['Enter the street address.'],
            },
          },
          { status: 400, statusText: 'Bad Request' },
        );
      await settle();

      expect(field('co-email').getAttribute('aria-invalid')).toBe('true');
      expect(element.querySelector('#co-email-error')?.textContent).toContain(
        'email address we can send your order to',
      );
      expect(element.querySelector('#co-line1-error')?.textContent).toContain(
        'Enter the street address.',
      );
      expect(navigated).toHaveLength(0);
    });

    it('clears a server field error as the shopper corrects that field', async () => {
      await openWith(session());
      await fillValidForm();
      placeButton().click();
      await settle();

      http
        .expectOne(
          (candidate) =>
            candidate.url === `${CheckoutApi.url}/orders` && candidate.method === 'POST',
        )
        .flush(
          { status: 400, errors: { email: ['Enter an email address we can send your order to.'] } },
          { status: 400, statusText: 'Bad Request' },
        );
      await settle();

      expect(field('co-email').getAttribute('aria-invalid')).toBe('true');

      await type('co-email', 'nimali@example.lk');

      expect(field('co-email').getAttribute('aria-invalid')).toBeNull();
    });

    /**
     * Doc 08 §5.4 step 3 and doc 11 P6: the order is refused, nothing is written, and the shopper is told
     * which items are short and how many are left. The per-variant figures are matched back to the bag so
     * the message names the product rather than an id.
     */
    it('names the sold-out line and how many are left', async () => {
      await openWith(session());
      await fillValidForm();
      placeButton().click();
      await settle();

      http
        .expectOne(
          (candidate) =>
            candidate.url === `${CheckoutApi.url}/orders` && candidate.method === 'POST',
        )
        .flush(
          {
            title: 'Out of stock',
            detail: 'Some of your items sold out while you were checking out.',
            status: 409,
            shortLines: { '101': 1 },
          },
          { status: 409, statusText: 'Conflict' },
        );
      await settle();

      expect(text()).toContain('Relaxed Linen Blend Shirt');
      expect(text()).toContain('only 1 left');
      expect(text()).toContain('nothing has been charged');
      expect(navigated).toHaveLength(0);

      // It re-reads, so the lines and totals on screen are the ones that refused the order.
      checkoutRequest().flush(session());
      await settle();
    });

    /**
     * G-10: "we never place an order at a total the customer has not seen". The new total is shown and
     * the order is **not** retried — the shopper presses the button again, having seen it.
     */
    it('shows the new total and does not retry when the price has changed', async () => {
      await openWith(session());
      await fillValidForm();
      placeButton().click();
      await settle();

      http
        .expectOne(
          (candidate) =>
            candidate.url === `${CheckoutApi.url}/orders` && candidate.method === 'POST',
        )
        .flush(
          {
            title: 'Prices changed',
            detail: 'The total has changed since this page was loaded.',
            status: 409,
            totals: totals({ itemsSubtotal: 9_000, savedTotal: 4_980, grandTotal: 9_400 }),
          },
          { status: 409, statusText: 'Conflict' },
        );
      await settle();

      expect(text()).toContain('total has changed');
      expect(sums().total).toBe(9_400);
      expect(navigated).toHaveLength(0);
      http.expectNone(
        (candidate) =>
          candidate.url === `${CheckoutApi.url}/orders` && candidate.method === 'POST',
      );

      checkoutRequest().flush(session({ totals: totals({ grandTotal: 9_400 }) }));
      await settle();
    });
  });

  describe('accessibility of the form', () => {
    it('labels every input, and describes the ones that carry a hint', async () => {
      await openWith(session());

      const inputs = Array.from(
        element.querySelectorAll<HTMLInputElement | HTMLTextAreaElement | HTMLSelectElement>(
          '.checkout__input',
        ),
      );

      expect(inputs.length).toBeGreaterThan(5);

      for (const input of inputs) {
        expect(
          element.querySelector(`label[for="${input.id}"]`),
          `#${input.id} has no label`,
        ).not.toBeNull();
      }

      expect(field('co-email').getAttribute('aria-describedby')).toContain('co-email-hint');
    });

    /**
     * The whole row is the label, so the tap target is the row rather than the 16 px dot — doc 02
     * GLB-09's floor, which a bare radio does not meet.
     */
    it('wraps every radio in its label', async () => {
      await openWith(session());

      const radios = Array.from(element.querySelectorAll('input[type="radio"]'));
      expect(radios.length).toBeGreaterThan(0);

      for (const radio of radios) {
        expect(radio.closest('label')).not.toBeNull();
      }
    });

    /**
     * The submit button sits in the summary, which is a sibling of the form, so it is associated by
     * `form=` rather than by nesting. Without that, Enter in a field would not submit and the button
     * would need a click handler of its own — two ways to place an order instead of one.
     */
    it('associates the submit button with the form it submits', async () => {
      await openWith(session());

      expect(placeButton().getAttribute('form')).toBe('checkout-form');
      expect(element.querySelector('form')?.id).toBe('checkout-form');
      expect(placeButton().type).toBe('submit');
    });
  });
});
