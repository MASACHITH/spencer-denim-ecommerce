import { Page, expect, test } from '@playwright/test';

import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';

import { adminURL } from '../playwright.config';
import { ownState, trustOnlyThisWorktree } from './support/admin-content-e2e';

/**
 * Issue #248: the e2e stub's display-currency save must be no more permissive than the API's rule
 * (`CurrencyRateRules`, `UpdateCurrencyRatesCommandHandler`), or the screen can pass here with a rate the real API
 * refuses. The API's half is `CurrencyRateRulesParityTests`; the screen's own pre-check is held to the same table by
 * `currency-rate-rules.spec.ts`. One file, `support/currency-rate-cases.json`, answers to all three.
 */
trustOnlyThisWorktree();

interface Case {
  readonly value: string;
  readonly problem: 'none' | 'notANumber' | 'tooManyDecimals' | 'outOfRange';
  readonly canonical?: string;
}

interface Currency {
  readonly field: string;
  readonly code: string;
  readonly name: string;
}

const table = JSON.parse(readFileSync(resolve(__dirname, 'support', 'currency-rate-cases.json'), 'utf8')) as {
  cases: Case[];
  currencies: Currency[];
  messages: Record<'tooManyDecimals' | 'outOfRange' | 'notANumber', string>;
};
const cases = table.cases;

/** The exact sentence the real handler gives (CurrencyRateRulesParityTests holds the C# to this table). */
const sentence = (kind: keyof typeof table.messages, currency: Currency): string =>
  table.messages[kind].replace('{name}', currency.name).replace('{code}', currency.code);
const gbp = table.currencies.find((currency) => currency.field === 'gbp')!;

/** The settings read model's row for a key, as the stub serves it. */
interface Row {
  readonly key: string;
  readonly value: string | null;
  readonly updatedAtUtc: string | null;
}

async function open(page: Page) {
  await ownState(page, 'currency-parity');
  const cookies = await page.context().cookies(adminURL);
  const state = cookies.find((cookie) => cookie.name === 'e2e_state')!.value;
  const headers = { Cookie: `e2e_state=${state}`, 'X-XSRF-TOKEN': 'e2e-antiforgery-token' };

  async function read(): Promise<{ rowVersion: string; rows: Row[] }> {
    const response = await page.request.get(`${adminURL}/api/admin/settings`, { headers });
    const body = (await response.json()) as {
      groups: { groupKey: string; rowVersion: string; settings: Row[] }[];
    };
    const group = body.groups.find((candidate) => candidate.groupKey === 'Currency.Display')!;

    return { rowVersion: group.rowVersion, rows: group.settings };
  }

  async function put(body: Record<string, unknown>, rowVersion?: string) {
    const version = rowVersion ?? (await read()).rowVersion;
    const response = await page.request.put(`${adminURL}/api/admin/settings/currency-rates`, {
      headers,
      data: { ...body, rowVersion: version },
    });
    const text = await response.text();
    const parsed = text ? (JSON.parse(text) as { errors?: Record<string, string[]>; code?: string }) : {};

    return { status: response.status(), errors: parsed.errors ?? {}, code: parsed.code };
  }

  const value = async (key: string) => (await read()).rows.find((row) => row.key === key)?.value ?? null;

  return { read, put, value };
}

const GBP = 'Currency.Display.GbpLkrPerUnit';

test.describe('the display-currency stub is no more permissive than the API (#248)', () => {
  /**
   * Every case in the table the API's own test is held to. A value the API's rule refuses must be refused here with
   * the same kind of sentence, and one it takes must be stored in the same form. The ends of the text and an empty box
   * are the handler's business before the rule sees the value (it trims, and empty clears), so they are tested below.
   */
  test('agrees with every case in currency-rate-cases.json, as the API does', async ({ page }) => {
    const api = await open(page);
    const tested = cases.filter((entry) => entry.value === entry.value.trim() && entry.value !== '');

    expect(tested.length).toBeGreaterThanOrEqual(40);

    for (const entry of tested) {
      const answer = await api.put({ gbp: entry.value });

      if (entry.problem === 'none') {
        expect(answer.status, `'${entry.value}' should be accepted`).toBe(204);
        expect(await api.value(GBP), `'${entry.value}' is stored as`).toBe(entry.canonical);
        continue;
      }

      expect(answer.status, `'${entry.value}' should be refused as ${entry.problem}`).toBe(400);

      const message = answer.errors['gbp']?.[0] ?? '';
      const expected = sentence(entry.problem, gbp);

      expect(message, `'${entry.value}' (${entry.problem})`).toBe(expected);
    }
  });

  test('gives each currency the exact sentence the API gives for each kind of fault', async ({ page }) => {
    const api = await open(page);

    for (const currency of table.currencies) {
      for (const [kind, value] of [
        ['notANumber', '1e3'],
        ['tooManyDecimals', '1.00001'],
        ['outOfRange', '0'],
      ] as const) {
        const answer = await api.put({ [currency.field]: value });

        expect(answer.status, `${currency.code} ${kind}`).toBe(400);
        expect(answer.errors[currency.field], `${currency.code} ${kind}`).toEqual([sentence(kind, currency)]);
      }
    }
  });

  test('names the right field for each of the five currencies, and only that one', async ({ page }) => {
    const api = await open(page);

    for (const field of ['usd', 'gbp', 'eur', 'aud', 'aed']) {
      const good = await api.put({ [field]: '123.45' });
      expect(good.status, field).toBe(204);

      const bad = await api.put({ [field]: '1e3' });
      expect(bad.status, field).toBe(400);
      expect(Object.keys(bad.errors), field).toEqual([field]);
    }
  });

  test('trims the ends of what it is sent, and treats an empty or null field as clearing that rate', async ({ page }) => {
    const api = await open(page);

    expect((await api.put({ gbp: '  399.5  ' })).status).toBe(204);
    expect(await api.value(GBP)).toBe('399.5');

    expect((await api.put({ gbp: '' })).status).toBe(204);
    expect(await api.value(GBP)).toBeNull();

    expect((await api.put({ gbp: '400' })).status).toBe(204);
    expect((await api.put({ gbp: null })).status).toBe(204);
    expect(await api.value(GBP)).toBeNull();
  });

  test('leaves a field the body does not carry alone, and a JSON number is a named field error', async ({ page }) => {
    const api = await open(page);

    expect((await api.put({ usd: '321.5', gbp: '399.5' })).status).toBe(204);
    expect((await api.put({ gbp: '410' })).status).toBe(204);
    expect(await api.value('Currency.Display.UsdLkrPerUnit')).toBe('321.5');

    const number = await api.put({ gbp: 399.5 });
    expect(number.status).toBe(400);
    expect(Object.keys(number.errors)).toEqual(['gbp']);
    expect(await api.value(GBP)).toBe('410');
  });

  test('refuses a stale row version with the code the screen reads, and writes nothing', async ({ page }) => {
    const api = await open(page);
    const first = (await api.read()).rowVersion;

    expect((await api.put({ gbp: '399.5' }, first)).status).toBe(204);

    const stale = await api.put({ gbp: '500' }, first);
    expect(stale.status).toBe(409);
    expect(stale.code).toBe('stale_row_version');
    expect(await api.value(GBP)).toBe('399.5');
  });

  test('re-dates a rate only when its number changes, as the API does (the row UpdatedAtUtc)', async ({ page }) => {
    const api = await open(page);

    await api.put({ gbp: '399.5' });
    const stamp = (await api.read()).rows.find((row) => row.key === GBP)!.updatedAtUtc;
    expect(stamp).not.toBeNull();

    await page.waitForTimeout(25);
    await api.put({ gbp: '399.50' });
    expect((await api.read()).rows.find((row) => row.key === GBP)!.updatedAtUtc).toBe(stamp);

    await page.waitForTimeout(25);
    await api.put({ gbp: '399.6' });
    expect((await api.read()).rows.find((row) => row.key === GBP)!.updatedAtUtc).not.toBe(stamp);
  });
});
