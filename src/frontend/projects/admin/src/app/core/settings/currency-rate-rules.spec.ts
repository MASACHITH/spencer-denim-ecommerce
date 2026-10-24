import { DISPLAY_CURRENCIES } from './admin-settings.model';
import { checkCurrencyRate, currencyRateMessage } from './currency-rate-rules';

/**
 * Issue #248: the screen's pre-check against the same table of cases as the API's rule
 * (`CurrencyRateRulesParityTests`) and the e2e stub (`admin-currency-stub-parity.e2e.ts`). A rule changed here and
 * not there fails one of the three; that is the point of reading one file.
 */
interface Case {
  readonly value: string;
  readonly problem: 'none' | 'notANumber' | 'tooManyDecimals' | 'outOfRange';
  readonly canonical?: string;
}

// This project's spec types do not include Node's, and the shared TypeScript configuration is not this change's to
// widen. The shared table is a file outside the project, so it is read with Node's own modules, loaded by a
// computed name that the type checker does not try to resolve. The runner is Node, so they are there at run time.
declare const process: { cwd(): string };

const nodeFs = (await import(/* @vite-ignore */ ['node', 'fs'].join(':'))) as {
  readFileSync(path: string, encoding: 'utf8'): string;
};

const table = JSON.parse(
  nodeFs.readFileSync(`${process.cwd()}/e2e/support/currency-rate-cases.json`, 'utf8'),
) as { readonly cases: readonly Case[] };

describe('checkCurrencyRate (issue #248)', () => {
  it('reads the shared table, which is not empty and has every kind of answer', () => {
    expect(table.cases.length).toBeGreaterThanOrEqual(40);
    expect(new Set(table.cases.map((entry) => entry.problem))).toEqual(
      new Set(['none', 'notANumber', 'tooManyDecimals', 'outOfRange']),
    );
  });

  it.each(table.cases.map((entry) => [entry.value, entry.problem] as const))(
    'answers %j with %s, as the API does',
    (value, problem) => {
      expect(checkCurrencyRate(value)).toBe(problem);
    },
  );
});

describe('currencyRateMessage (issue #248)', () => {
  const pound = DISPLAY_CURRENCIES[1];
  const dollar = DISPLAY_CURRENCIES[0];

  it('has no sentence for a good rate', () => {
    expect(currencyRateMessage('none', pound.name, pound.code)).toBeNull();
  });

  it("says what the API says, word for word, so a mistake reads the same wherever it was caught", () => {
    expect(currencyRateMessage('tooManyDecimals', pound.name, pound.code)).toBe(
      'Use at most 4 decimal places.',
    );
    expect(currencyRateMessage('outOfRange', pound.name, pound.code)).toBe(
      'Use a rate above 0 and no more than 1,000,000. It is how many rupees one pound sterling is worth.',
    );
    expect(currencyRateMessage('notANumber', dollar.name, dollar.code)).toBe(
      'Enter the us dollar rate as digits with an optional decimal point, such as 399.5: no signs, commas, '
        + 'spaces or letters. Leave it empty to stop offering USD.',
    );
  });

  it('is one of the three sentences for every case in the table that is not a rate', () => {
    for (const entry of table.cases.filter((candidate) => candidate.problem !== 'none')) {
      expect(
        currencyRateMessage(checkCurrencyRate(entry.value), pound.name, pound.code),
        `'${entry.value}'`,
      ).toEqual(expect.any(String));
    }
  });
});

describe('DISPLAY_CURRENCIES (issue #248)', () => {
  it('is the five currencies the API has, in its order, each with its own field and key', () => {
    expect(DISPLAY_CURRENCIES.map((currency) => currency.code)).toEqual(['USD', 'GBP', 'EUR', 'AUD', 'AED']);
    expect(DISPLAY_CURRENCIES.map((currency) => currency.field)).toEqual(['usd', 'gbp', 'eur', 'aud', 'aed']);

    for (const currency of DISPLAY_CURRENCIES) {
      expect(currency.key).toBe(`Currency.Display.${currency.code[0]}${currency.code.slice(1).toLowerCase()}LkrPerUnit`);
    }
  });
});
