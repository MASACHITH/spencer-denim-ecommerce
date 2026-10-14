import {
  PAY_HINT_PARAMETER,
  PAY_POLL_INTERVAL_MS,
  PAY_POLL_MAX_ATTEMPTS,
  payHintIsProcessing,
  payHintMessage,
} from './payment-hint';

describe('payHintMessage', () => {
  it('uses the parameter the server redirects with', () => {
    expect(PAY_HINT_PARAMETER).toBe('pay');
  });

  it('gives a fixed sentence for each hint that needs one', () => {
    expect(payHintMessage('received', 'PendingPayment')).toContain('We received your payment');
    expect(payHintMessage('received', 'Expired')).toContain('Our team will contact you');
    expect(payHintMessage('notapproved', 'PendingPayment')).toContain('nothing was charged');
    expect(payHintMessage('cancelled', 'PendingPayment')).toContain('You can pay again below');
    expect(payHintMessage('closed', 'PendingPayment')).toBe('This order can no longer be paid.');
    expect(payHintMessage('unavailable', 'PendingPayment')).toContain('not available right now');
  });

  it('shows nothing for the hints the provider\'s own panel already explains', () => {
    // pending, processing, declined and paid are said by the order's payment instructions or its status,
    // for as long as they are true - not once, from the address bar.
    for (const hint of ['paid', 'pending', 'processing', 'declined']) {
      expect(payHintMessage(hint, 'PendingPayment')).toBeNull();
    }
  });

  /** The address is the customer's to edit. Only a whitelisted key selects a sentence; nothing in it is shown. */
  it('never displays what the address says', () => {
    for (const hint of [
      '<script>alert(1)</script>',
      'received<img src=x onerror=alert(1)>',
      'We have refunded you Rs. 1,000,000',
      '__proto__',
      'constructor',
      'toString',
      '',
      ' received',
      'RECEIVED',
    ]) {
      expect(payHintMessage(hint, 'PendingPayment')).toBeNull();
    }

    expect(payHintMessage(null, 'PendingPayment')).toBeNull();
    expect(payHintMessage(undefined, 'PendingPayment')).toBeNull();
  });

  it('says nothing to a paid order, whatever the address says', () => {
    for (const hint of ['received', 'notapproved', 'cancelled', 'closed', 'unavailable']) {
      expect(payHintMessage(hint, 'Paid')).toBeNull();
    }
  });

  it('does not point at a "below" that has gone once the order is no longer waiting for payment', () => {
    for (const status of ['Expired', 'Cancelled']) {
      expect(payHintMessage('notapproved', status)).toBeNull();
      expect(payHintMessage('cancelled', status)).toBeNull();
      expect(payHintMessage('unavailable', status)).toBeNull();

      // These two make sense whatever the order has become.
      expect(payHintMessage('closed', status)).toBe('This order can no longer be paid.');
      expect(payHintMessage('received', status)).toContain('We received your payment');
    }
  });

  it('names no payment provider', () => {
    for (const hint of ['received', 'notapproved', 'cancelled', 'closed', 'unavailable']) {
      expect(payHintMessage(hint, 'PendingPayment') ?? '').not.toMatch(/paypal|payhere|stripe/i);
    }
  });
});

describe('payHintIsProcessing', () => {
  it('is true only for the processing hint', () => {
    expect(payHintIsProcessing('processing')).toBe(true);

    for (const hint of ['pending', 'paid', 'Processing', '', null, undefined]) {
      expect(payHintIsProcessing(hint)).toBe(false);
    }
  });

  it('polls every few seconds for about a minute, not for ever', () => {
    expect(PAY_POLL_INTERVAL_MS).toBeGreaterThanOrEqual(3_000);
    expect(PAY_POLL_INTERVAL_MS * PAY_POLL_MAX_ATTEMPTS).toBeLessThanOrEqual(120_000);
  });
});
