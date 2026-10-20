import { HttpErrorResponse, HttpHeaders } from '@angular/common/http';

import { checkoutErrorMessage, checkoutFailure } from './checkout-api';

/**
 * How a failed checkout request is classified and worded. The 429 is issue #107's: the placement limiter
 * refusing an address that has placed its share of orders in the window.
 */
describe('checkoutFailure and checkoutErrorMessage', () => {
  function refusal(status: number, retryAfter?: string): HttpErrorResponse {
    return new HttpErrorResponse({
      status,
      statusText: 'refused',
      headers: retryAfter === undefined ? new HttpHeaders() : new HttpHeaders({ 'Retry-After': retryAfter }),
      error: { status, title: 'Too many orders', code: 'rate_limited' },
    });
  }

  describe('a 429 from the placement limiter', () => {
    it('is its own kind of failure, not a generic one', () => {
      expect(checkoutFailure(refusal(429, '600'))).toBe('rateLimited');
      expect(checkoutFailure(refusal(500))).toBe('failed');
    });

    it('says nothing was placed or charged, and does not tell the shopper to retry at once', () => {
      const message = checkoutErrorMessage(refusal(429, '600'));

      expect(message).toContain('has not been placed');
      expect(message).toContain('Nothing has been charged');
      expect(message).toContain('your bag is unchanged');
      expect(message).toContain('press Place order again');
      expect(message).not.toContain('try again in a moment');
    });

    it('reads Retry-After and rounds up to whole minutes, so nobody is sent back early', () => {
      expect(checkoutErrorMessage(refusal(429, '600'))).toContain('wait about 10 minutes');
      expect(checkoutErrorMessage(refusal(429, '601'))).toContain('wait about 11 minutes');
      expect(checkoutErrorMessage(refusal(429, '60'))).toContain('wait about 1 minute and');
      expect(checkoutErrorMessage(refusal(429, '5'))).toContain('wait about 1 minute and');
    });

    it('speaks in hours once the wait is an hour or more', () => {
      expect(checkoutErrorMessage(refusal(429, '3600'))).toContain('wait about 1 hour and');
      expect(checkoutErrorMessage(refusal(429, '7201'))).toContain('wait about 3 hours and');
    });

    it('says "a few minutes" rather than guess when Retry-After is missing or unreadable', () => {
      expect(checkoutErrorMessage(refusal(429))).toContain('wait a few minutes and');
      expect(checkoutErrorMessage(refusal(429, 'Wed, 21 Oct 2026 07:28:00 GMT'))).toContain(
        'wait a few minutes and',
      );
      expect(checkoutErrorMessage(refusal(429, 'soon'))).toContain('wait a few minutes and');
    });

    it('never prints a status code or a server stack at a shopper', () => {
      const message = checkoutErrorMessage(refusal(429, '600'));

      expect(message).not.toContain('429');
      expect(message).not.toContain('rate_limited');
    });
  });

  describe('the failures it must not disturb', () => {
    it('still sends a server error to the generic sentence', () => {
      expect(checkoutErrorMessage(refusal(500))).toBe(
        'We could not place your order just now. Please try again in a moment.',
      );
    });

    it('still reads a 409 without a recognised extension as blocked', () => {
      expect(checkoutFailure(refusal(409))).toBe('blocked');
    });

    it('still treats status 0 as offline', () => {
      expect(checkoutFailure(refusal(0))).toBe('offline');
    });

    it('still treats something that is not an HTTP error as a plain failure', () => {
      expect(checkoutFailure(new Error('boom'))).toBe('failed');
    });
  });
});
