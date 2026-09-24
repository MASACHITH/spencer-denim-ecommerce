/**
 * LKR money formatting for the storefront (`Rs. 7,490`).
 *
 * Grouping is done by hand rather than with `Intl.NumberFormat` on purpose: the page is rendered
 * on the server and hydrated in the browser, and those two runtimes do not have to agree on what
 * a locale looks like. A formatter that disagrees by one separator rewrites the price during
 * hydration, which is a visible flicker and, worse, a number that differs between what a crawler
 * indexed and what a shopper sees.
 *
 * Rupees are whole in practice; a price that carries cents keeps them rather than rounding money
 * silently in the view layer.
 */

const CURRENCY_PREFIX = 'Rs.';

export function formatLkr(amount: number): string {
  if (!Number.isFinite(amount)) {
    return '';
  }

  const negative = amount < 0;
  const total = Math.round(Math.abs(amount) * 100);
  const cents = total % 100;
  const decimals = cents > 0 ? `.${String(cents).padStart(2, '0')}` : '';

  return `${negative ? '-' : ''}${CURRENCY_PREFIX} ${group((total - cents) / 100)}${decimals}`;
}

/** Thousands separators, western grouping (the catalogue quotes prices as `Rs. 12,500`). */
function group(value: number): string {
  return String(value).replace(/\B(?=(\d{3})+(?!\d))/g, ',');
}
