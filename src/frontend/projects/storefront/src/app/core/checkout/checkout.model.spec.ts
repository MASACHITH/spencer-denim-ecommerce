import {
  CheckoutLineDto,
  DeliveryOptionDto,
  checkoutBlockerMessage,
  deliveryEstimate,
  formatColomboDateTime,
  isPricedLine,
  looksLikeEmail,
  looksLikePhone,
} from './checkout.model';

function option(overrides: Partial<DeliveryOptionDto> = {}): DeliveryOptionDto {
  return {
    id: 1,
    code: 'STANDARD',
    name: 'Standard delivery',
    description: null,
    requiresAddress: true,
    isAvailable: true,
    fee: 400,
    freeAboveAmount: null,
    feeAfterDiscount: 400,
    estimatedDaysMin: 1,
    estimatedDaysMax: 2,
    ...overrides,
  };
}

describe('checkoutBlockerMessage', () => {
  it('has a sentence for every blocker the API can send', () => {
    // A union exhaustiveness check that a compiler cannot do for a runtime switch: every member listed,
    // and every answer a real sentence rather than the default.
    for (const blocker of [
      'EmptyBag',
      'LineNotPurchasable',
      'NoDeliveryMethod',
      'NoPaymentMethod',
    ] as const) {
      const message = checkoutBlockerMessage(blocker);

      expect(message.length).toBeGreaterThan(10);
      expect(message).not.toBe('Checkout is not available at the moment.');
    }
  });
});

describe('deliveryEstimate', () => {
  it('reads a range as a range', () => {
    expect(deliveryEstimate(option())).toBe('1–2 days');
  });

  it('reads a single day as one day, not a range of one', () => {
    expect(deliveryEstimate(option({ estimatedDaysMin: 1, estimatedDaysMax: 1 }))).toBe('1 day');
  });

  it('pluralises a single bound above one', () => {
    expect(deliveryEstimate(option({ estimatedDaysMin: 3, estimatedDaysMax: 3 }))).toBe('3 days');
  });

  it('has nothing to say about an option with no rate', () => {
    expect(
      deliveryEstimate(option({ estimatedDaysMin: null, estimatedDaysMax: null })),
    ).toBeNull();
  });
});

describe('looksLikeEmail', () => {
  /**
   * **Deliberately permissive, and the same rule as the server's `PlaceOrderValidator.LooksLikeEmail`.**
   * The expensive direction of being wrong here is refusing an address the shop would have accepted,
   * because that stops a shopper buying. These are the cases that would fail a hand-rolled RFC attempt.
   */
  it.each([
    'nimali@example.com',
    'nimali.perera+orders@example.co.uk',
    "o'brien@example.lk",
    'a@b.c',
    'NIMALI@EXAMPLE.COM',
  ])('accepts %s', (candidate) => {
    expect(looksLikeEmail(candidate)).toBe(true);
  });

  it.each([
    'nimali',
    '0771234567',
    '@example.com',
    'nimali@',
    'nimali@example',
    'nimali@example.',
    'nimali@@example.com',
    'nimali perera@example.com',
    '',
  ])('refuses %s', (candidate) => {
    expect(looksLikeEmail(candidate)).toBe(false);
  });
});

/**
 * **`phone: "not-a-phone"` placed an order.** The field is required because a courier in Sri Lanka needs
 * one, which an unreachable number defeats, and the rule here has to match the server's
 * `PlaceOrderValidator.LooksLikePhone` exactly — a client stricter than the server stops a shopper
 * buying for no reason.
 */
describe('looksLikePhone', () => {
  it.each([
    '0771234567',
    '077 123 4567',
    '077-123-4567',
    '(077) 1234567',
    '011 2345678',
    '+94771234567',
    '+94 77 123 4567',
    '0094771234567',
    '771234567',
    '+44 20 7123 4567',
  ])('accepts %s, because somebody really typed it that way', (candidate) => {
    expect(looksLikePhone(candidate)).toBe(true);
  });

  it.each([
    'not-a-phone',
    'call me',
    '077ABCDEFG',
    '12345',
    '0',
    '-',
    '',
    '077123456+7',
    '0771234567890123456',
  ])('refuses %s, because a courier could not call it', (candidate) => {
    expect(looksLikePhone(candidate)).toBe(false);
  });
});

/**
 * The summary prints a price for a line only when that price is in the figures underneath it. An
 * `Unavailable` line printed `Rs. 3,990` beside a total it contributed nothing to.
 */
describe('isPricedLine', () => {
  const line = (status: CheckoutLineDto['status']): CheckoutLineDto => ({
    variantId: 470,
    productId: 1,
    productName: 'Slim Fit Jean',
    slug: 'slim-fit-jean',
    colourName: 'Indigo',
    sizeLabel: '32',
    imageUrl: null,
    imageAltText: null,
    quantity: 1,
    availableQuantity: 5,
    unitOriginalPrice: 6_990,
    unitPrice: 4_392,
    lineSubtotal: 4_392,
    status,
  });

  it('matches the bag, and the server, exactly', () => {
    expect(isPricedLine(line('Buyable'))).toBe(true);
    expect(isPricedLine(line('NotEnoughStock'))).toBe(true);
    expect(isPricedLine(line('OutOfStock'))).toBe(false);
    expect(isPricedLine(line('Unavailable'))).toBe(false);
  });
});

describe('formatColomboDateTime', () => {
  /**
   * Colombo is UTC+05:30 with no daylight saving. The conversion is hand-rolled rather than left to
   * `Intl`, because this page renders on the server and hydrates in the browser and the two runtimes do
   * not have to agree on what a time zone database looks like.
   */
  it('renders a UTC instant in Colombo time', () => {
    expect(formatColomboDateTime('2026-10-07T04:30:00Z')).toBe('7 October 2026 at 10.00 am');
  });

  it('crosses midnight into the next day', () => {
    // 20:00 UTC on 31 December is 01:30 on 1 January in Colombo — the same boundary the server's order
    // number series turns on.
    expect(formatColomboDateTime('2026-12-31T20:00:00Z')).toBe('1 January 2027 at 1.30 am');
  });

  it('renders noon and midnight without a zero hour', () => {
    expect(formatColomboDateTime('2026-10-07T06:30:00Z')).toBe('7 October 2026 at 12.00 pm');
    expect(formatColomboDateTime('2026-10-07T18:30:00Z')).toBe('8 October 2026 at 12.00 am');
  });

  it('treats a bare timestamp as UTC rather than as local time', () => {
    // The API serialises `datetime2` without a zone when the value is read back as `Unspecified`. Taking
    // that as the *browser's* local time would move a payment deadline by hours, differently for every
    // shopper, and differently again between the server render and the hydration.
    expect(formatColomboDateTime('2026-10-07T04:30:00')).toBe('7 October 2026 at 10.00 am');
  });

  it('says nothing rather than Invalid Date', () => {
    expect(formatColomboDateTime('not a date')).toBe('');
  });
});
