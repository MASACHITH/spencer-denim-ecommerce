import { formatLkr } from './money';

describe('formatLkr', () => {
  it('writes rupees the way the catalogue quotes them', () => {
    expect(formatLkr(7490)).toBe('Rs. 7,490');
    expect(formatLkr(990)).toBe('Rs. 990');
    expect(formatLkr(0)).toBe('Rs. 0');
  });

  it('groups thousands however large the number is', () => {
    expect(formatLkr(12500)).toBe('Rs. 12,500');
    expect(formatLkr(1234567)).toBe('Rs. 1,234,567');
  });

  it('keeps cents when a price has them, and drops them when it does not', () => {
    expect(formatLkr(5000.5)).toBe('Rs. 5,000.50');
    expect(formatLkr(1999.99)).toBe('Rs. 1,999.99');
    expect(formatLkr(1999.0)).toBe('Rs. 1,999');
  });

  it('rounds to the cent rather than showing a floating-point tail', () => {
    expect(formatLkr(0.1 + 0.2)).toBe('Rs. 0.30');
  });

  it('gives back nothing for a value that is not a number', () => {
    expect(formatLkr(Number.NaN)).toBe('');
    expect(formatLkr(Number.POSITIVE_INFINITY)).toBe('');
  });

  it('does not depend on the runtime locale, so SSR and the browser agree', () => {
    // The page is rendered on the server and hydrated in the browser. Node and a browser need not
    // agree on what a locale looks like, and a price that changes on hydration is a visible
    // flicker and a number a crawler indexed differently.
    expect(formatLkr(1234567.89)).toBe('Rs. 1,234,567.89');
  });
});
