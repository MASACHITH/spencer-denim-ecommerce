import { PAYMENT_NOTICE_RECEIVED, paymentNoticeMessage } from './payment-notice';

describe('paymentNoticeMessage', () => {
  it('uses the code the server sends', () => {
    expect(PAYMENT_NOTICE_RECEIVED).toBe('received');
  });

  it('says what doc 11 P8 step 6 says, for an order that is waiting, expired or cancelled', () => {
    for (const status of ['PendingPayment', 'Expired', 'Cancelled']) {
      const message = paymentNoticeMessage('received', status);

      expect(message).toContain('We received your payment, our team will contact you');
      expect(message).toContain('do not need to pay again');
    }
  });

  it('says a second payment arrived when the order is already paid', () => {
    for (const status of ['Paid', 'Processing', 'Shipped', 'Delivered']) {
      const message = paymentNoticeMessage('received', status);

      expect(message).toContain('second payment');
      expect(message).not.toContain('We received your payment');
    }
  });

  it('shows nothing when there is no notice, or a code it does not know', () => {
    for (const notice of [null, undefined, '', 'RECEIVED', 'refunded', '<b>received</b>']) {
      expect(paymentNoticeMessage(notice, 'PendingPayment')).toBeNull();
    }
  });
});
