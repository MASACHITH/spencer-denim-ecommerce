/**
 * The admin e2e stub's copy of the API's display-currency rate rules (`CurrencyRateRules.cs`, issue #248).
 *
 * The stub must be no more permissive than the validator, or a screen passes here with a rate the real API refuses.
 * JavaScript cannot run the C# rules, so the two are tied together by one table of cases,
 * `currency-rate-cases.json`: `CurrencyRateRulesParityTests` (C#) asserts the API agrees with every case, and
 * `admin-currency-stub-parity.e2e.ts` asserts this module (through the stub) agrees with the same cases. A rule
 * changed on one side and not the other fails one of the two.
 */

export const MAX_DECIMAL_PLACES = 4;
export const MAX_RATE = 1_000_000;

/** `DisplayCurrencies.All` on the server: the five currencies, in order, with the lower-case body field. */
export const DISPLAY_CURRENCIES = [
  { code: 'USD', name: 'US dollar', field: 'usd', key: 'Currency.Display.UsdLkrPerUnit' },
  { code: 'GBP', name: 'pound sterling', field: 'gbp', key: 'Currency.Display.GbpLkrPerUnit' },
  { code: 'EUR', name: 'euro', field: 'eur', key: 'Currency.Display.EurLkrPerUnit' },
  { code: 'AUD', name: 'Australian dollar', field: 'aud', key: 'Currency.Display.AudLkrPerUnit' },
  { code: 'AED', name: 'UAE dirham', field: 'aed', key: 'Currency.Display.AedLkrPerUnit' },
];

function allAsciiDigits(text) {
  for (let index = 0; index < text.length; index += 1) {
    const code = text.charCodeAt(index);

    if (code < 48 || code > 57) {
      return false;
    }
  }

  return true;
}

/** `CurrencyRateRules.Check`: 'none', 'notANumber', 'tooManyDecimals' or 'outOfRange'. */
export function checkCurrencyRate(text) {
  if (typeof text !== 'string' || text.length === 0) {
    return 'notANumber';
  }

  const point = text.indexOf('.');
  const whole = point < 0 ? text : text.slice(0, point);
  const fraction = point < 0 ? '' : text.slice(point + 1);

  if (whole.length === 0 || !allAsciiDigits(whole) || (point >= 0 && (fraction.length === 0 || !allAsciiDigits(fraction)))) {
    return 'notANumber';
  }

  if (fraction.length > MAX_DECIMAL_PLACES) {
    return 'tooManyDecimals';
  }

  const value = Number(text);

  if (!Number.isFinite(value) || value <= 0 || value > MAX_RATE) {
    return 'outOfRange';
  }

  return 'none';
}

/** `CurrencyRateRules.Canonical`: no leading zeros but one, no trailing zeros after the point, no trailing point. */
export function canonicalCurrencyRate(text) {
  const [whole, fraction = ''] = text.split('.');
  const wholeDigits = whole.replace(/^0+(?=[0-9])/u, '');
  const fractionDigits = fraction.replace(/0+$/u, '');

  return fractionDigits === '' ? wholeDigits : `${wholeDigits}.${fractionDigits}`;
}

/** The handler's sentence for a problem (`UpdateCurrencyRatesCommandHandler`), word for word. */
export function currencyRateMessage(problem, currency) {
  // The name as it stands (see DisplayCurrency.Name on the server): US dollar and UAE dirham keep their capitals.
  const name = currency.name;

  if (problem === 'tooManyDecimals') {
    return `Use at most ${MAX_DECIMAL_PLACES} decimal places.`;
  }

  if (problem === 'outOfRange') {
    return `Use a rate above 0 and no more than ${MAX_RATE.toLocaleString('en-US')}. It is how many rupees one ${name} is worth.`;
  }

  return (
    `Enter the ${name} rate as digits with an optional decimal point, such as 399.5: no signs, commas, spaces or `
    + `letters. Leave it empty to stop offering ${currency.code}.`
  );
}

/** The handler's two 409 sentences (`UpdateCurrencyRatesCommandHandler`), word for word; the table holds both. */
export const STALE_ROW_VERSION_MESSAGE =
  'Somebody else changed the currency rates while you were editing them. Reload the page to see their change, then '
  + 're-apply yours.';

export const MISSING_ROW_VERSION_MESSAGE =
  "This request did not carry the settings' current version, so it cannot be applied safely. Reload the page and try "
  + 'again.';

/**
 * How the real route reads a field NAME (`AdminSettingsEndpoints.CurrencyRates`): the body is read with the shared
 * case-insensitive reader, so `Usd` and `USD` are the dollar, and the same name twice in two letter cases is a 400.
 * Returns the body's own key for a field (or undefined), and the pair of keys that collide, if any.
 */
export function findFieldKey(body, field) {
  return Object.keys(body).find((key) => key.toLowerCase() === field.toLowerCase());
}

export function repeatedNames(body) {
  const seen = new Map();

  for (const key of Object.keys(body)) {
    const lower = key.toLowerCase();

    if (seen.has(lower)) {
      return [seen.get(lower), key];
    }

    seen.set(lower, key);
  }

  return null;
}
