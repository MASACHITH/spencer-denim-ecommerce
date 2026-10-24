/**
 * What counts as a display-currency rate (issue #248): the screen's copy of `CurrencyRateRules` in the API.
 *
 * **A pre-check, never the authority.** It exists so a typo is caught before a round trip; the server answers for
 * every save and refuses anything this lets through. The two are tied together by one table of cases,
 * `e2e/support/currency-rate-cases.json`: `CurrencyRateRulesParityTests` (C#) asserts the API agrees with every
 * case, `admin-currency-stub-parity.e2e.ts` asserts the e2e stub does, and `currency-rate-rules.spec.ts` asserts
 * this module does. A rule changed in one place and not the others fails one of them.
 *
 * **Strictly digits, an optional point and more digits.** Everything a lenient parser would forgive is refused on
 * purpose: a sign, an exponent (1e3), a thousands separator (1,000 is a thousand in one country and one in another),
 * a space inside the number, and a bare point (.5 and 5.). The ends of the text are trimmed by the caller.
 */

/** Why a typed rate is not a rate. `none` means it is one. Same four answers as the API's enum. */
export type CurrencyRateProblem = 'none' | 'notANumber' | 'tooManyDecimals' | 'outOfRange';

/** `SettingKeys.CurrencyDisplay.MaxDecimalPlaces` and `.MaxRate` on the server. */
export const CURRENCY_RATE_LIMITS = {
  maxDecimalPlaces: 4,
  maxRate: 1_000_000,
} as const;

/** Checks one non-empty, already trimmed text. An empty box is not checked: it means "stop offering it". */
export function checkCurrencyRate(text: string): CurrencyRateProblem {
  if (text.length === 0) {
    return 'notANumber';
  }

  const point = text.indexOf('.');
  const whole = point < 0 ? text : text.slice(0, point);
  const fraction = point < 0 ? '' : text.slice(point + 1);

  if (
    whole.length === 0
    || !allAsciiDigits(whole)
    || (point >= 0 && (fraction.length === 0 || !allAsciiDigits(fraction)))
  ) {
    return 'notANumber';
  }

  if (fraction.length > CURRENCY_RATE_LIMITS.maxDecimalPlaces) {
    return 'tooManyDecimals';
  }

  // Digits only by now, so the only ways this fails are zero and a figure too large to be a rate. A figure with
  // hundreds of digits parses to Infinity, which is also out of range.
  const value = Number(text);

  if (!Number.isFinite(value) || value <= 0 || value > CURRENCY_RATE_LIMITS.maxRate) {
    return 'outOfRange';
  }

  return 'none';
}

/**
 * The sentence for a problem, word for word what the API says (`UpdateCurrencyRatesCommandHandler`), so the same
 * mistake reads the same whether the screen or the server caught it. `none` has no sentence.
 */
export function currencyRateMessage(
  problem: CurrencyRateProblem,
  currencyName: string,
  currencyCode: string,
): string | null {
  const name = currencyName.toLowerCase();

  switch (problem) {
    case 'none':
      return null;

    case 'tooManyDecimals':
      return `Use at most ${CURRENCY_RATE_LIMITS.maxDecimalPlaces} decimal places.`;

    case 'outOfRange':
      return (
        `Use a rate above 0 and no more than ${CURRENCY_RATE_LIMITS.maxRate.toLocaleString('en-US')}. `
        + `It is how many rupees one ${name} is worth.`
      );

    default:
      return (
        `Enter the ${name} rate as digits with an optional decimal point, such as 399.5: no signs, commas, `
        + `spaces or letters. Leave it empty to stop offering ${currencyCode}.`
      );
  }
}

function allAsciiDigits(text: string): boolean {
  for (let index = 0; index < text.length; index += 1) {
    const code = text.charCodeAt(index);

    if (code < 48 || code > 57) {
      return false;
    }
  }

  return true;
}
