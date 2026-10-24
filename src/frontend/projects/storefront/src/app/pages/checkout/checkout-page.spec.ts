import { provideTestDeliveryCountries } from '../../core/delivery/delivery-country.testing';
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
import { DELIVERY_DISTRICTS } from '../../core/delivery/district.data';
import { DELIVERY_DISTRICT_STORAGE_KEY } from '../../core/delivery/delivery-district.service';
import { DELIVERY_COUNTRY_STORAGE_KEY } from '../../core/delivery/delivery-country.service';

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
    buyableItemCount: 2,
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
    appliedPromoCode: null,
    promoRefusal: null,
    promoMinimumSpend: null,
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
    buyableItemCount: 0,
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
    statusLabel: 'Awaiting payment',
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

    // The checkout page redirects on success and never renders either of these; they are here because
    // the contract has them. `OrderPage` is where the pay block is exercised, with a fixture that holds
    // real values in every field.
    payment: null,
    proof: null,

    // Doc 11 P10 flow A (W3-04b). A freshly placed order satisfies all four of step 2's guards, which is
    // what the server answers for the order this page has just created.
    canCancel: true,
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
        provideHttpClientTesting(), provideTestDeliveryCountries(),
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

  /**
   * Whether Place order is unavailable.
   *
   * **`aria-disabled`, not `disabled`, and the assertions were changed on purpose.** A `disabled` button
   * is out of the tab order, so a keyboard or screen-reader user could neither reach it nor read why it
   * would not work, and disabling a focused control throws focus to BODY (ADM-22). The press is still a
   * no-op; what changed is that the page now answers it.
   */
  function placeBlocked(): boolean {
    return placeButton().getAttribute('aria-disabled') === 'true';
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

  /**
   * **The header district is persisted in `localStorage`, which jsdom keeps between tests in one file.**
   * A test that chooses Jaffna therefore left the *next* test's page pre-selecting Jaffna and issuing a
   * second `GET /api/checkout?districtId=10` that nobody flushed. Cleared here rather than worked
   * around: the write-back is the behaviour under test in `the header district` below.
   */
  beforeEach(() => {
    localStorage.removeItem(DELIVERY_DISTRICT_STORAGE_KEY);
  });

  afterEach(() => {
    TestBed.inject(HttpTestingController).verify();
    localStorage.removeItem(DELIVERY_DISTRICT_STORAGE_KEY);
  });

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

  /**
   * **One bag, two pages, one count.** The bag's Subtotal label counts the priced lines
   * (`buyableItemCount`); checkout counted every line, so a bag of "470 x2 + 471 x1" whose 471 had been
   * deactivated printed "Subtotal (2 items) Rs. 7,980" on the bag and "Subtotal (3 items) Rs. 7,980"
   * here. The money was right both times; the label counted an item its own figure excludes.
   */
  /**
   * **"Choose a district" when a district has been chosen.** The Delivery and Total rows said it
   * whatever the reason, which was true only while a district could never be pre-selected. The header
   * district made "district chosen, method not" reachable, and in it the summary asked for something the
   * form above already showed as answered.
   */
  describe('the pending delivery figure', () => {
    it('asks for a district when none is chosen', async () => {
      await openWith(unquoted());

      expect(element.querySelector('[data-sum="delivery-pending"]')?.textContent?.trim()).toBe(
        'Choose a district',
      );
      expect(element.querySelector('[data-sum="total-pending"]')?.textContent?.trim()).toBe(
        'Choose a district',
      );
    });

    it('asks for a delivery option once a district is chosen', async () => {
      await openWith(
        session({
          selectedDistrictId: COLOMBO.id,
          selectedDeliveryMethodId: null,
          totals: totals({ deliveryFeeAmount: null, grandTotal: null }),
        }),
      );

      expect(element.querySelector('[data-sum="delivery-pending"]')?.textContent?.trim()).toBe(
        'Choose a delivery option',
      );
      expect(element.querySelector('[data-sum="total-pending"]')?.textContent?.trim()).toBe(
        'Choose a delivery option',
      );
    });

    /**
     * **This pins an unreachable guard, and says so rather than pretending to be a scenario.** In the
     * running app a method whose zone rate is unavailable renders as a `disabled` radio, a forced click
     * leaves the group unchecked, and the server echoes `selectedDeliveryMethodId` only for an available
     * rate — the round-3 tester could not reach this state by any route. The session fixture below
     * constructs it directly, so the guard is held to the one thing that matters if a future change ever
     * does open it: it must not ask for the district again.
     */
    it('pins the guard for a chosen option the zone does not offer', async () => {
      await openWith(
        session({
          selectedDistrictId: COLOMBO.id,
          selectedDeliveryMethodId: 1,
          deliveryOptions: [option({ isAvailable: false, fee: null, feeAfterDiscount: null })],
          totals: totals({ deliveryFeeAmount: null, grandTotal: null }),
        }),
      );

      expect(element.querySelector('[data-sum="delivery-pending"]')?.textContent?.trim()).toBe(
        'Choose an available delivery option',
      );
    });
  });

  describe('the counts and the line prices', () => {
    it('counts only the items the subtotal sums', async () => {
      await openWith(
        session({
          lines: [line(), line({ variantId: 471, status: 'Unavailable', quantity: 1 })],
          totals: totals({ itemCount: 3, buyableItemCount: 2, lineCount: 2 }),
        }),
      );

      const label = element.querySelector('.checkout__sums dt')?.textContent ?? '';

      expect(label).toContain('2 items');
      expect(label).not.toContain('3 items');
    });

    it('prints no price for a line that is not in the total', async () => {
      await openWith(
        session({
          lines: [line({ variantId: 471, status: 'Unavailable', lineSubtotal: 3_990 })],
          totals: totals({ itemCount: 2, buyableItemCount: 0 }),
        }),
      );

      const price = element.querySelector('.checkout__line-price')?.textContent ?? '';

      expect(price).not.toContain('3,990');
      expect(price).toContain('\u2014');
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

      expect(placeBlocked()).toBe(true);
    });
  });

  /**
   * **GLB-06's header selector is the shopper's own answer to the question this form asks**, and ignoring
   * it meant the shop asked twice: the header said Colombo while the form said "Choose a district" in
   * three places. `DeliveryDistrictService`'s docblock has named CHK-01 as its consumer since it was
   * written.
   */
  /**
   * **The page promised an email that nothing can send.** `ops.EmailOutbox` does not exist and ACC-03's
   * `OrderPlaced` and `BankTransferInstructions` messages are W3-06, so "We send your order number and
   * its link here" and "We will email your order number and a link to it" were two sentences nothing
   * could honour — and they contradicted the success page, which correctly says to keep the page's
   * address. A guest's only route back to their order is that link, so a shopper told to expect an email
   * closes the tab and loses the order.
   */
  describe('what it promises', () => {
    it('does not say an email is coming', async () => {
      await openWith(session());

      const copy = text();

      expect(copy).not.toContain('We send your order number');
      expect(copy).not.toContain('We will email');
      expect(copy).not.toMatch(/email (you|your order)/i);
    });

    it('tells the shopper to keep the link instead, as the success page does', async () => {
      await openWith(session());

      expect(text()).toContain('keep that address');
    });

    /** The field stays: the address is stored on the order and W3-06 will send to it. */
    it('still asks for the email address', async () => {
      await openWith(session());

      expect(field('co-email')).not.toBeNull();
      expect(field('co-email').getAttribute('required')).not.toBeNull();
    });
  });

  describe('the header district', () => {
    const jaffnaCode = DELIVERY_DISTRICTS.find((district) => district.name === 'Jaffna')!.code;
    const gampahaCode = DELIVERY_DISTRICTS.find((district) => district.name === 'Gampaha')!.code;

    it('prices the quote for the district the shopper chose in the header', async () => {
      localStorage.setItem(DELIVERY_DISTRICT_STORAGE_KEY, jaffnaCode);

      create();
      await settle();

      const request = checkoutRequest();
      expect(request.request.params.get('districtId')).toBe(String(JAFFNA.id));

      request.flush(session({ selectedDistrictId: JAFFNA.id }));
      await settle();

      expect(field('co-district').value).toBe(String(JAFFNA.id));
    });

    /**
     * **A default is not a statement.** The header is never blank, so it falls back to Colombo for a
     * visitor who has said nothing — and printing a Colombo delivery fee to somebody in Jaffna is
     * exactly what `CheckoutSessionDto.SelectedDistrictId` has no default in order to avoid.
     */
    it('does not pre-select the header default for a visitor who has chosen nothing', async () => {
      create();
      await settle();

      const request = checkoutRequest();
      expect(request.request.params.has('districtId')).toBe(false);

      request.flush(unquoted());
      await settle();

      expect(field('co-district').value).toBe('');
    });

    /** One question, one answer: the header follows the form as well as the other way round. */
    it('writes the district chosen here back to the header', async () => {
      await openWith(unquoted());

      const select = element.querySelector<HTMLSelectElement>('#co-district')!;
      select.value = String(JAFFNA.id);
      select.dispatchEvent(new Event('change', { bubbles: true }));
      await settle();

      checkoutRequest().flush(session({ selectedDistrictId: JAFFNA.id }));
      await settle();

      expect(localStorage.getItem(DELIVERY_DISTRICT_STORAGE_KEY)).toBe(jaffnaCode);
    });

    /**
     * **The two lists are allowed to diverge, so the id is only a hint.** `district.data.ts` is frozen
     * client-side data (TODO(W1-03)) and `ref.Districts` is the server's; all 25 names agree today and
     * nothing enforces it. When the server answers that it priced nothing, the name is tried against the
     * list the server did send.
     */
    it('recovers by name when the server does not recognise the header id', async () => {
      localStorage.setItem(DELIVERY_DISTRICT_STORAGE_KEY, gampahaCode);

      create();
      await settle();

      const serverGampaha: DistrictDto = { id: 902, name: 'Gampaha', deliveryZone: 2 };

      // The server did not price anything, because the id it was handed is not one of its own.
      checkoutRequest().flush(
        session({ districts: [COLOMBO, serverGampaha], selectedDistrictId: null }),
      );
      await settle();

      const second = checkoutRequest();
      expect(second.request.params.get('districtId')).toBe(String(serverGampaha.id));

      second.flush(
        session({ districts: [COLOMBO, serverGampaha], selectedDistrictId: serverGampaha.id }),
      );
      await settle();

      expect(field('co-district').value).toBe(String(serverGampaha.id));
    });

    it('gives up and asks when the name is not in the server list either', async () => {
      localStorage.setItem(DELIVERY_DISTRICT_STORAGE_KEY, gampahaCode);

      create();
      await settle();

      // Colombo and Jaffna only: an admin has deactivated Gampaha.
      checkoutRequest().flush(unquoted());
      await settle();

      expect(field('co-district').value).toBe('');
      expect(text()).toContain('Choose a district');
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
      expect(placeBlocked()).toBe(true);
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
      expect(placeBlocked()).toBe(true);
    });
  });

  /**
   * **The refused submission, which used to be announced to nobody.** Pressing Place order with nothing
   * filled in drew three red field messages, said nothing, and left focus on BODY — WCAG 2.2 4.1.3, and
   * invisible to axe, which reported zero violations on this page.
   */
  describe('a refused submission', () => {
    it('announces it, names every blocking field and takes focus', async () => {
      await openWith(session());
      await choosePayment('BankTransfer');

      placeButton().click();
      await settle();

      const refused = element.querySelector('#checkout-refused');
      expect(refused).not.toBeNull();
      expect(refused!.getAttribute('role')).toBe('alert');

      const named = Array.from(refused!.querySelectorAll('li')).map((item) =>
        (item.textContent ?? '').trim(),
      );

      expect(named).toContain('Enter your first name.');
      expect(named).toContain('Enter your email address.');
      expect(named).toContain('Enter a phone number for the delivery.');
      expect(named).toContain('Enter the street address.');

      // The focus move is deferred by a tick, because the summary does not exist until the signal that
      // raises it has been rendered.
      await new Promise((resolve) => setTimeout(resolve));
      expect(document.activeElement).toBe(refused);
    });

    /**
     * **The district was the one field never reported.** `missingFields()` set `deliveryMethodId` and
     * returned, and there is no delivery-method control to attach an error to until a district is
     * chosen — so the shopper saw three red messages, a District select with no error and a grey hint.
     */
    it('names the district when no delivery option has been chosen', async () => {
      await openWith(unquoted());

      // The fields that are on screen before a district is chosen, filled in, so the only thing left
      // outstanding is the one the form never used to name.
      await type('co-firstName', 'Nimali');
      await type('co-email', 'nimali@example.com');
      await type('co-phone', '0771234567');
      await choosePayment('BankTransfer');

      placeButton().click();
      await settle();

      expect(field('co-district').getAttribute('aria-invalid')).toBe('true');
      expect(element.querySelector('#co-district-error')?.textContent).toContain(
        'Choose the district your order is going to',
      );
    });

    /**
     * **GLB-09's 44 px floor, which these entries breached at every breakpoint.** They shipped at 22 px
     * tall — under WCAG 2.2 SC 2.5.8's 24 px as well — and axe does not enforce 2.5.8, so nothing saw it.
     * Measured here on the style rule and in `checkout.e2e.ts` on the rendered box.
     */
    it('gives each entry a 44 px tap target', async () => {
      await openWith(session());

      placeButton().click();
      await settle();

      const link = element.querySelector<HTMLElement>('[data-testid="refused-field"]');

      expect(link).not.toBeNull();

      // **`class="link"` is the assertion.** `base.css` gives `button.link` `min-height: var(--tap)`,
      // which is the 44 px floor, and writing a second copy of that here is what took the component's
      // style budget over. jsdom applies no stylesheet so the box cannot be measured; `checkout.e2e.ts`
      // measures the rendered box in a real browser at 320, 768 and 1280 px with the form refused.
      expect(link!.classList.contains('link')).toBe(true);
      expect(link!.tagName).toBe('BUTTON');
    });

    /** The list is live, so it empties itself as the shopper fixes things rather than going stale. */
    it('clears itself as the fields are filled in', async () => {
      await openWith(session());

      placeButton().click();
      await settle();

      expect(element.querySelector('#checkout-refused')).not.toBeNull();

      await fillValidForm();

      expect(element.querySelector('#checkout-refused')).toBeNull();
    });

    /**
     * **`aria-disabled` and a reason, not `disabled` and silence.** A disabled button is out of the tab
     * order, carried no `aria-describedby`, no `aria-disabled` and no title, so there was no way to
     * learn why it would not work. The bag page already does this (`bag-checkout-blocked`).
     */
    it('says why Place order is unavailable, and keeps it reachable', async () => {
      await openWith(unquoted());

      const button = placeButton();

      expect(button.disabled).toBe(false);
      expect(button.getAttribute('aria-disabled')).toBe('true');
      expect(button.getAttribute('aria-describedby')).toBe('checkout-place-blocked');
      expect(element.querySelector('#checkout-place-blocked')?.textContent).toContain(
        'Choose the district your order is going to',
      );
    });
  });

  /**
   * **`phone: "not-a-phone"` placed an order (201).** The server now refuses it, and the page refuses it
   * first with the same rule, so a shopper is told before the round trip rather than after.
   */
  describe('the phone number', () => {
    it('refuses one the courier could not call', async () => {
      await openWith(session());
      await fillValidForm();
      await type('co-phone', 'not-a-phone');

      expect(placeBlocked()).toBe(true);
      expect(text()).toContain('phone number the courier can call');
    });

    it('refuses one on the delivery address too, which is the one the driver dials', async () => {
      await openWith(session());
      await fillValidForm();
      await type('co-address-phone', 'ring the bell');

      expect(placeBlocked()).toBe(true);
    });

    it.each(['077 123 4567', '+94 77 123 4567', '011-2345678'])(
      'accepts %s, because somebody really typed it that way',
      async (candidate) => {
        await openWith(session());
        await fillValidForm();
        await type('co-phone', candidate);

        expect(placeBlocked()).toBe(false);
      },
    );
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

      expect(placeBlocked()).toBe(false);
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

      expect(placeBlocked()).toBe(true);
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

      expect(placeBlocked()).toBe(true);

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

      expect(placeBlocked()).toBe(true);
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

    /**
     * Issue #107. The placement limiter answers **429** when one address has placed its share of orders.
     * The shopper must be told what happened and what to do, not "something went wrong" — and the page
     * must not do anything clever: nothing was written, so nothing is re-read, nothing is cleared and the
     * pinned checkout key is still unused. Pressing the button again sends the same order under the same
     * key, which is what makes the advice safe to follow.
     */
    it('tells the shopper to wait, keeps the form, and sends the same key when they press again', async () => {
      const isPlaceRequest = (candidate: { url: string; method: string }): boolean =>
        candidate.url === `${CheckoutApi.url}/orders` && candidate.method === 'POST';

      await openWith(session());
      await fillValidForm();
      placeButton().click();
      await settle();

      const refused = http.expectOne(isPlaceRequest);
      const firstKey = refused.request.body.checkoutKey as string;
      refused.flush(
        { title: 'Too many orders', status: 429, code: 'rate_limited' },
        { status: 429, statusText: 'Too Many Requests', headers: { 'Retry-After': '600' } },
      );
      await settle();

      // A human sentence: what happened, that nothing was lost, and how long to wait.
      expect(text()).toContain('Too many orders have been placed from your connection');
      expect(text()).toContain('Nothing has been charged');
      expect(text()).toContain('wait about 10 minutes');
      expect(text()).not.toContain('could not place your order just now');
      const alert = element.querySelector('[role="alert"]');
      expect(alert?.textContent).toContain('Nothing has been charged');
      expect(navigated).toHaveLength(0);

      // And the message is brought to the shopper rather than left at the top of a page they have
      // scrolled away from: the press was at the bottom, and a button that merely comes back to life
      // invites the same press again.
      await new Promise((resolve) => setTimeout(resolve));
      expect(document.activeElement).toBe(alert);

      // Nothing was re-read and nothing was cleared: the shopper's details are where they left them.
      http.expectNone((candidate) => candidate.url === CheckoutApi.url && candidate.method === 'GET');
      expect(field('co-email').value).toBe('nimali@example.com');
      expect(field('co-line1').value).toBe('14 Galle Road');
      expect(placeBlocked()).toBe(false);

      // Pressing again, after the wait, retries the same order under the same key.
      placeButton().click();
      await settle();

      const retry = http.expectOne(isPlaceRequest);
      expect(retry.request.body.checkoutKey).toBe(firstKey);

      retry.flush(confirmation());
      await settle();

      expect(navigated).toEqual([['/order', '22222222-2222-4222-8222-222222222222']]);
      drainCartRead();
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

    /**
     * **The form's native submit does nothing (#123).** The page is server rendered, so the form is usable
     * before Angular has attached `(submit)`; with no `method` the browser then submits it itself, as a GET
     * to this page carrying the name, email, phone and address in the query string. `method="dialog"` on a
     * form outside a `<dialog>` is a submit that goes nowhere. jsdom has no opinion about navigation, so
     * this pins the attribute and `pre-hydration-submit.e2e.ts` holds the scripts back to prove the
     * behaviour in a real browser.
     */
    it('cannot submit natively, so nothing typed before hydration can reach the URL', async () => {
      await openWith(session());

      const form = element.querySelector('form')!;

      expect(form.getAttribute('method')).toBe('dialog');
      expect(form.hasAttribute('action')).toBe(false);
    });
  });

  /**
   * The promo-code box (ADM-13, CART-01, doc 11 P6 step 5).
   *
   * **Nothing here computes a discount.** Applying a code is a re-quote, and every figure comes back from
   * the server — so these tests assert the request that goes out, the sentence that comes back, and that
   * neither the box nor its live region can be destroyed by the update that fills them.
   */
  describe('the promo code (ADM-13)', () => {
    function promoSaid(): string {
      return element.querySelector('[data-testid="promo-said"]')?.textContent?.trim() ?? '';
    }

    function promoButton(testId: string): HTMLButtonElement {
      const node = element.querySelector<HTMLButtonElement>(`[data-testid="${testId}"]`);
      if (!node) {
        throw new Error(`No button [data-testid="${testId}"] is rendered.`);
      }
      return node;
    }

    async function apply(code: string): Promise<void> {
      await type('co-promo', code);
      promoButton('promo-apply').click();
      await settle();
    }

    it('sends the code as a query parameter and shows the discount the server priced', async () => {
      await openWith(session());

      await apply('welcome10');

      const request = checkoutRequest();
      expect(request.request.params.get('promoCode')).toBe('welcome10');

      request.flush(
        session({
          appliedPromoCode: 'WELCOME10',
          totals: totals({ promoDiscountAmount: 878.4, grandTotal: 8_305.6 }),
        }),
      );
      await settle();

      expect(sums().promo).toBe(878.4);
      expect(promoSaid()).toContain('WELCOME10 applied');

      // The box shows the shop's spelling, not the shopper's: that is the code the order will record.
      expect(field('co-promo').value).toBe('WELCOME10');

      // And the ladder still adds up with the Discount row in it, which is the identity the bag prints.
      const { subtotal, savings, promo, delivery, deliveryDiscount, total } = sums();
      expect(subtotal - savings - promo + delivery - deliveryDiscount).toBe(total);
    });

    it('sends no promoCode parameter when the box is empty', async () => {
      await openWith(session());

      const select = element.querySelector<HTMLSelectElement>('#co-district')!;
      select.value = String(JAFFNA.id);
      select.dispatchEvent(new Event('change', { bubbles: true }));
      await settle();

      expect(checkoutRequest().request.params.has('promoCode')).toBe(false);
    });

    /**
     * The refusal has to be visible, and the refused code has to stay in the box so one mistyped
     * character can be fixed rather than the whole thing retyped.
     */
    it('shows the refusal and keeps the typed code in the box', async () => {
      await openWith(session());

      await apply('NOPE');

      checkoutRequest().flush(session({ appliedPromoCode: null, promoRefusal: 'NotRecognised' }));
      await settle();

      expect(promoSaid()).toContain('do not recognise');
      expect(field('co-promo').value).toBe('NOPE');
      expect(sums().promo).toBe(0);
    });

    it('prints the threshold when a code needs a bigger order', async () => {
      await openWith(session());

      await apply('BIGSPEND');

      checkoutRequest().flush(
        session({ appliedPromoCode: null, promoRefusal: 'MinimumSpend', promoMinimumSpend: 5_000 }),
      );
      await settle();

      // The figure, not "you have not spent enough": it is the one refusal a shopper can act on.
      expect(promoSaid()).toContain('Rs. 5,000');
    });

    /**
     * **The sentence and the box survive the update that produces them.**
     *
     * This is the defect this project has shipped twice — a refusal written into the panel its own
     * refusal destroyed, and a live region inside a block that success removed, which meant a blind
     * customer pressed a button and heard nothing. The assertion is on node identity: the same DOM node
     * that existed before the re-quote is the one carrying the sentence afterwards, so there was no
     * moment at which a screen reader had nothing to watch.
     */
    it('keeps the same live-region node across the re-quote that fills it', async () => {
      await openWith(session());

      const before = element.querySelector('[data-testid="promo-said"]');
      const inputBefore = field('co-promo');

      await apply('welcome10');
      checkoutRequest().flush(session({ appliedPromoCode: 'WELCOME10' }));
      await settle();

      expect(element.querySelector('[data-testid="promo-said"]')).toBe(before);
      expect(field('co-promo')).toBe(inputBefore);
      expect(before?.isConnected).toBe(true);
    });

    /**
     * `role="status"` announces a *change*, so the second press of Apply on a code that is still refused
     * must write the text again rather than leave it (ADM-22's defect).
     *
     * Asserted with a real `MutationObserver` on the text node, because a before/after read of
     * `textContent` would be identical either way — a test that could not fail.
     */
    it('re-announces the same refusal, so a second press is not met with silence', async () => {
      await openWith(session());

      const said = element.querySelector('[data-testid="promo-said"]')!;

      await apply('NOPE');
      checkoutRequest().flush(session({ appliedPromoCode: null, promoRefusal: 'NotRecognised' }));
      await settle();

      const observed: (string | null)[] = [];
      const observer = new MutationObserver((records) => {
        for (const record of records) {
          // **`oldValue`, not `target.textContent`.** The callback runs as a microtask, by which time the
          // text node already holds the *final* value — so reading the target would record the sentence
          // twice and never the blank between them, which is a test that cannot fail. The admin
          // couriers page's own re-announce test makes the same point.
          observed.push(record.oldValue);
        }
      });

      observer.observe(said, {
        childList: true,
        characterData: true,
        characterDataOldValue: true,
        subtree: true,
      });

      promoButton('promo-apply').click();
      await settle();
      checkoutRequest().flush(session({ appliedPromoCode: null, promoRefusal: 'NotRecognised' }));
      await settle();

      // `afterNextRender` runs after the view has caught up, so the blanking is a real render.
      await settle();

      observer.disconnect();

      expect(observed.some((value) => (value ?? '').trim() === '')).toBe(true);
      expect(promoSaid()).toContain('do not recognise');
    });

    /**
     * #109: the buttons carry `aria-disabled` and refuse the press in the handler, because disabling the
     * control that has focus throws focus to `<body>`.
     */
    it('refuses Apply in the handler rather than disabling the button', async () => {
      await openWith(session());

      const button = promoButton('promo-apply');

      expect(button.hasAttribute('disabled')).toBe(false);
      expect(button.getAttribute('aria-disabled')).toBe('true');

      button.click();
      await settle();

      // Nothing went out: an empty box is nothing to apply.
      http.expectNone((request) => request.url === CheckoutApi.url);
    });

    it('refuses Remove in the handler when there is nothing applied', async () => {
      await openWith(session());

      const button = promoButton('promo-remove');

      expect(button.hasAttribute('disabled')).toBe(false);
      expect(button.getAttribute('aria-disabled')).toBe('true');

      button.click();
      await settle();

      http.expectNone((request) => request.url === CheckoutApi.url);
    });

    it('removes an applied code and re-quotes without it', async () => {
      await openWith(session({ appliedPromoCode: 'WELCOME10' }));

      const button = promoButton('promo-remove');
      expect(button.getAttribute('aria-disabled')).toBeNull();

      button.click();
      await settle();

      expect(checkoutRequest().request.params.has('promoCode')).toBe(false);
    });

    /**
     * Doc 08 §6 step 11 asks for "discount limited to the order total" to be shown, and
     * `promoDiscountClamped` reached no screen at all before this.
     */
    it('says when the discount was limited to the order total', async () => {
      await openWith(session());

      await apply('BIGFIXED');

      checkoutRequest().flush(
        session({
          appliedPromoCode: 'BIGFIXED',
          totals: totals({ promoDiscountAmount: 9_183, grandTotal: 1, promoDiscountClamped: true }),
        }),
      );
      await settle();

      expect(promoSaid()).toContain('limited to the order total');
    });

    /**
     * Doc 11 P3's exception, through the real 409: "the other is told the code is fully used and can
     * place the order without it". The order is not retried without the code — the shopper presses the
     * button again, having seen the new total.
     */
    it('refuses the order when the code stopped qualifying, and re-quotes without it', async () => {
      await openWith(session({ appliedPromoCode: 'WELCOME10' }));
      await fillValidForm();

      placeButton().click();
      await settle();

      const place = http.expectOne(
        (request) => request.url === `${CheckoutApi.url}/orders` && request.method === 'POST',
      );

      expect((place.request.body as { promoCode: string | null }).promoCode).toBe('WELCOME10');

      place.flush(
        {
          title: 'Promo code not applied',
          detail: 'That promo code could not be used for this order.',
          promoRefusal: 'FullyUsed',
          promoMinimumSpend: null,
        },
        { status: 409, statusText: 'Conflict' },
      );
      await settle();

      // The sentence is in the page-level alert, which sits above every block the re-quote rebuilds.
      expect(text()).toContain('fully used');
      expect(text()).toContain('Nothing has been placed');
      expect(navigated.length).toBe(0);

      // And the re-quote asks for no code, so the total on screen becomes the one the shop would charge.
      expect(checkoutRequest().request.params.has('promoCode')).toBe(false);
    });
  });

  /**
   * Owner decision 2026-10-21: Sri Lanka plus worldwide delivery. The country comes first; an overseas
   * country has no district, one option (International delivery) and its own address rules, which mirror
   * the API's `DestinationRules` and are never stricter.
   */
  describe('overseas delivery', () => {
    const INTERNATIONAL = option({
      id: 9,
      code: 'INTERNATIONAL',
      name: 'International delivery',
      description: 'Sent overseas by an international service.',
      fee: 9_500,
      freeAboveAmount: null,
      feeAfterDiscount: 9_500,
      estimatedDaysMin: 7,
      estimatedDaysMax: 14,
    });

    const PAYPAL: PaymentMethodDto = {
      code: 'PayPal',
      displayName: 'PayPal',
      checkoutDescription: 'Pay by card or PayPal balance.',
    };

    function overseas(overrides: Partial<CheckoutSessionDto> = {}): CheckoutSessionDto {
      return session({
        selectedDistrictId: null,
        selectedDeliveryMethodId: 9,
        deliveryOptions: [INTERNATIONAL],
        paymentMethods: [PAYPAL],
        selectedCountryCode: 'GB',
        totals: totals({ deliveryFeeAmount: 9_500, grandTotal: 18_284 }),
        ...overrides,
      });
    }

    async function openOverseas(code: string, payload: CheckoutSessionDto): Promise<TestRequest> {
      localStorage.setItem(DELIVERY_COUNTRY_STORAGE_KEY, code);
      create();
      await settle();
      const request = checkoutRequest();
      request.flush(payload);
      await settle();
      return request;
    }

    async function fillOverseasForm(): Promise<void> {
      await type('co-firstName', 'Amelia');
      await type('co-email', 'amelia@example.co.uk');
      await type('co-phone', '+44 20 7946 0958');
      await type('co-recipient', 'Amelia Jones');
      await type('co-address-phone', '+44 20 7946 0958');
      await type('co-line1', '10 Downing Street');
      await type('co-city', 'London');
      await type('co-postcode', 'SW1A 2AA');
      await choosePayment('PayPal');
    }

    afterEach(() => localStorage.removeItem(DELIVERY_COUNTRY_STORAGE_KEY));

    it('asks for the header country, without a district', async () => {
      const request = await openOverseas('GB', overseas());

      expect(request.request.params.get('countryCode')).toBe('GB');
      expect(request.request.params.has('districtId')).toBe(false);
      expect(element.querySelector('#co-district')).toBeNull();
      expect((field('co-country') as unknown as HTMLSelectElement).value).toBe('GB');
    });

    it('chooses the one International option for the shopper and prices it', async () => {
      await openOverseas('GB', overseas({ selectedDeliveryMethodId: null }));

      const priced = checkoutRequest();
      expect(priced.request.params.get('deliveryMethodId')).toBe('9');
      priced.flush(overseas());
      await settle();

      expect(text()).toContain('International delivery');
      expect(sums().delivery).toBe(9_500);
    });

    it('swaps the district for a state or region, and drops the landmark', async () => {
      await openOverseas('US', overseas({ selectedCountryCode: 'US' }));

      expect(element.querySelector('#co-region')).not.toBeNull();
      expect(element.querySelector('#co-landmark')).toBeNull();
      expect(field('co-region').getAttribute('required')).not.toBeNull();
      expect(field('co-postcode').getAttribute('required')).not.toBeNull();
    });

    it('leaves the region and the postcode optional where the country does not use them', async () => {
      await openOverseas('AE', overseas({ selectedCountryCode: 'AE' }));

      expect(field('co-region').getAttribute('required')).toBeNull();
      expect(field('co-postcode').getAttribute('required')).toBeNull();
    });

    it('places the order with the country and the region, and no district or landmark', async () => {
      await openOverseas('US', overseas({ selectedCountryCode: 'US' }));
      await fillOverseasForm();
      await type('co-address-phone', '+1 202 555 0143');
      await type('co-region', 'District of Columbia');
      await type('co-postcode', '20500');

      expect(placeBlocked()).toBe(false);
      placeButton().click();
      await settle();

      const request = http.expectOne(
        (candidate) =>
          candidate.url === `${CheckoutApi.url}/orders` && candidate.method === 'POST',
      );

      expect(request.request.body).toMatchObject({
        countryCode: 'US',
        districtId: null,
        deliveryMethodId: 9,
        paymentMethodCode: 'PayPal',
        expectedGrandTotal: 18_284,
        address: {
          region: 'District of Columbia',
          postalCode: '20500',
          landmark: null,
          city: 'London',
        },
      });

      request.flush(confirmation());
      await settle();
      drainCartRead();
    });

    it('refuses a missing UK postcode and a delivery phone without its country code', async () => {
      await openOverseas('GB', overseas());
      await fillOverseasForm();
      await type('co-postcode', '');
      await type('co-address-phone', '020 7946 0958');

      placeButton().click();
      await settle();

      expect(text()).toContain('Enter the postcode for United Kingdom.');
      expect(text()).toContain('Include the country code');
    });

    it('refuses a postcode with characters the server does not take', async () => {
      await openOverseas('GB', overseas());
      await fillOverseasForm();
      await type('co-postcode', 'SW1A#2AA');

      placeButton().click();
      await settle();

      expect(text()).toContain('A postcode can use only letters, numbers, spaces and hyphens.');
    });

    it('accepts a 00-prefixed phone, the other international form the server takes', async () => {
      await openOverseas('GB', overseas());
      await fillOverseasForm();
      await type('co-address-phone', '0044 20 7946 0958');

      expect(placeBlocked()).toBe(false);
    });

    it('names the country it cannot deliver to', async () => {
      await openOverseas(
        'AQ',
        overseas({
          selectedCountryCode: 'AQ',
          selectedDeliveryMethodId: null,
          deliveryOptions: [{ ...INTERNATIONAL, isAvailable: false, fee: null, feeAfterDiscount: null }],
          blockers: ['CountryNotServed'],
          totals: totals({ deliveryFeeAmount: null, grandTotal: null }),
        }),
      );

      expect(text()).toContain('We cannot deliver to Antarctica at the moment.');
      expect(placeBlocked()).toBe(true);
    });

    it('says why a free-delivery code does not cover an overseas parcel', async () => {
      await openOverseas('GB', overseas({ promoFreeDeliveryIgnored: true }));

      expect(
        element.querySelector('[data-testid="promo-free-delivery-ignored"]')?.textContent,
      ).toContain('Free-delivery codes cover Sri Lankan delivery only.');
    });

    it('re-quotes for a country chosen on the page, and tells the header', async () => {
      await openWith(session());

      const select = element.querySelector<HTMLSelectElement>('#co-country')!;
      select.value = 'AE';
      select.dispatchEvent(new Event('change', { bubbles: true }));
      await settle();

      const request = checkoutRequest();
      expect(request.request.params.get('countryCode')).toBe('AE');
      expect(request.request.params.has('districtId')).toBe(false);
      request.flush(overseas({ selectedCountryCode: 'AE' }));
      await settle();

      expect(localStorage.getItem(DELIVERY_COUNTRY_STORAGE_KEY)).toBe('AE');
      expect(element.querySelector('#co-district')).toBeNull();
    });

    it('falls back to Sri Lanka once when the server does not know the country', async () => {
      await openOverseas('ZZ', overseas({ selectedCountryCode: null }));

      const again = checkoutRequest();
      expect(again.request.params.has('countryCode')).toBe(false);
      again.flush(session());
      await settle();

      expect(element.querySelector('#co-district')).not.toBeNull();
    });

    /**
     * Fix 1 (#239): express-only country selected before the shopper reaches the checkout page.
     *
     * JP has `fee: null` / `expressFee: 15_000`, so Standard is not configured. The first read()
     * goes out without `overseasTier` (= Standard default). The server answers with the International
     * option flagged as unavailable. `apply()` detects this, calls `autoSelectTier(JP)` which sets
     * `chosenOverseasTier = 'Express'`, and fires a second read() with `overseasTier=Express`.
     * The second answer carries the option as available, so the shopper can proceed.
     */
    it('auto-selects Express and shows it as available when the country has only Express configured', async () => {
      localStorage.setItem(DELIVERY_COUNTRY_STORAGE_KEY, 'JP');
      create();
      await settle();

      // First request: no overseasTier sent (Standard). JP has no Standard → option not available.
      const firstRequest = checkoutRequest();
      expect(firstRequest.request.params.get('countryCode')).toBe('JP');
      expect(firstRequest.request.params.has('overseasTier')).toBe(false);

      firstRequest.flush(
        session({
          selectedCountryCode: 'JP',
          selectedDistrictId: null,
          selectedDeliveryMethodId: null,
          deliveryOptions: [
            { ...INTERNATIONAL, isAvailable: false, fee: null, feeAfterDiscount: null },
          ],
          totals: totals({ deliveryFeeAmount: null, grandTotal: null }),
        }),
      );
      await settle();

      // apply() detects no available option, calls autoSelectTier(), tier changes null → 'Express',
      // and a second read() is fired with overseasTier=Express.
      const secondRequest = checkoutRequest();
      expect(secondRequest.request.params.get('overseasTier')).toBe('Express');

      secondRequest.flush(
        session({
          selectedCountryCode: 'JP',
          selectedDistrictId: null,
          selectedDeliveryMethodId: INTERNATIONAL.id,
          deliveryOptions: [INTERNATIONAL],
          paymentMethods: [PAYPAL],
          totals: totals({ deliveryFeeAmount: INTERNATIONAL.feeAfterDiscount, grandTotal: 18_284 }),
        }),
      );
      await settle();

      // The International delivery option is rendered as available.
      const intlRadio = element.querySelector<HTMLInputElement>(
        `input[name="deliveryMethodId"][value="${INTERNATIONAL.id}"]`,
      );
      expect(intlRadio).not.toBeNull();
      expect(intlRadio!.disabled).toBe(false);
      expect(intlRadio!.closest('label')?.classList.contains('is-unavailable')).toBe(false);
    });

    /**
     * Owner decision 2026-10-23: a country with both Standard and Express configured shows the tier
     * picker so the shopper can choose. CA has `fee: 9_000` and `expressFee: 16_000`, so
     * `hasBothTiers()` is true and the picker is rendered.
     */
    it('shows Standard and Express tier radios when the country has both tiers configured', async () => {
      await openOverseas(
        'CA',
        session({
          selectedCountryCode: 'CA',
          selectedDistrictId: null,
          selectedDeliveryMethodId: INTERNATIONAL.id,
          deliveryOptions: [INTERNATIONAL],
          paymentMethods: [PAYPAL],
          totals: totals({ deliveryFeeAmount: 9_000, grandTotal: 17_784 }),
        }),
      );

      // Both tier radios are rendered (hasBothTiers() is true for CA).
      expect(element.querySelector('input[name="overseasTier"][value="Standard"]')).not.toBeNull();
      expect(element.querySelector('input[name="overseasTier"][value="Express"]')).not.toBeNull();
    });

    /**
     * Gap 1 (#239): the shopper picks the Express tier by interacting with the radio, and the
     * re-quote must carry `overseasTier=Express` so the server prices the right tier.
     */
    it('sends overseasTier=Express on GET when the shopper picks the Express radio', async () => {
      await openOverseas(
        'CA',
        session({
          selectedCountryCode: 'CA',
          selectedDistrictId: null,
          selectedDeliveryMethodId: INTERNATIONAL.id,
          deliveryOptions: [INTERNATIONAL],
          paymentMethods: [PAYPAL],
          totals: totals({ deliveryFeeAmount: 9_000, grandTotal: 17_784 }),
        }),
      );

      // Both tier radios must be present before the shopper can pick one.
      expect(element.querySelector('input[name="overseasTier"][value="Standard"]')).not.toBeNull();
      expect(element.querySelector('input[name="overseasTier"][value="Express"]')).not.toBeNull();

      // Shopper selects the Express radio — the template binds (change)="chooseTier('Express')".
      const expressRadio = element.querySelector<HTMLInputElement>(
        'input[name="overseasTier"][value="Express"]',
      )!;
      expressRadio.dispatchEvent(new Event('change', { bubbles: true }));
      await settle();

      // chooseTier() sets chosenOverseasTier to 'Express' and calls read(); the GET must carry it.
      const request = checkoutRequest();
      expect(request.request.params.get('overseasTier')).toBe('Express');

      request.flush(
        session({
          selectedCountryCode: 'CA',
          selectedDistrictId: null,
          selectedDeliveryMethodId: INTERNATIONAL.id,
          deliveryOptions: [{ ...INTERNATIONAL, fee: 16_000, feeAfterDiscount: 16_000 }],
          paymentMethods: [PAYPAL],
          totals: totals({ deliveryFeeAmount: 16_000, grandTotal: 24_784 }),
        }),
      );
      await settle();
    });

    /**
     * Gap 2 (#239): an express-only country (JP: fee=null, expressFee set) auto-selects Express
     * and must not render the tier picker — `hasBothTiers()` is false when only one tier is
     * configured, so there is nothing for the shopper to choose.
     */
    it('does not show the tier picker for an express-only country', async () => {
      localStorage.setItem(DELIVERY_COUNTRY_STORAGE_KEY, 'JP');
      create();
      await settle();

      // First read: no overseasTier sent (Standard default). JP has no Standard → not available.
      const firstRequest = checkoutRequest();
      firstRequest.flush(
        session({
          selectedCountryCode: 'JP',
          selectedDistrictId: null,
          selectedDeliveryMethodId: null,
          deliveryOptions: [
            { ...INTERNATIONAL, isAvailable: false, fee: null, feeAfterDiscount: null },
          ],
          totals: totals({ deliveryFeeAmount: null, grandTotal: null }),
        }),
      );
      await settle();

      // apply() detects no available option and autoSelectTier sets chosenOverseasTier to 'Express',
      // triggering a second read() with overseasTier=Express.
      const secondRequest = checkoutRequest();
      secondRequest.flush(
        session({
          selectedCountryCode: 'JP',
          selectedDistrictId: null,
          selectedDeliveryMethodId: INTERNATIONAL.id,
          deliveryOptions: [INTERNATIONAL],
          paymentMethods: [PAYPAL],
          totals: totals({ deliveryFeeAmount: INTERNATIONAL.feeAfterDiscount, grandTotal: 18_284 }),
        }),
      );
      await settle();

      // hasBothTiers() is false for JP (only Express configured), so no tier-picker radios.
      expect(element.querySelector('input[name="overseasTier"]')).toBeNull();
    });
  });

  describe('when no payment method is switched on', () => {
    it('says so plainly and keeps Place order unavailable', async () => {
      await openWith(session({ paymentMethods: [], blockers: ['NoPaymentMethod'] }));

      expect(text()).toContain('No payment method is available at the moment');
      expect(placeBlocked()).toBe(true);
    });
  });
});
