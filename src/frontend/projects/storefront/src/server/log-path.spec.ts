import vectors from '../../../../../../tests/Shared/log-path-vectors.json';

import { CREDENTIAL_ROUTES, isSafeLogSegment, logPath } from './log-path';

/**
 * Issue #201: what the storefront's server log may print for a request address.
 *
 * The rows are `tests/Shared/log-path-vectors.json`, the table the API's `RequestPathRedactionTests` runs
 * through the C# implementation and that was checked against the gateway's `$sd_log_path` map. The three
 * implementations of one rule are kept in step by this being the same data: a row added there fails here if the
 * port disagrees.
 */
describe('logPath', () => {
  const rows = vectors.cases as { name: string; path: string; expected: string }[];

  it('has the table, and it is not a stub', () => {
    expect(rows.length).toBeGreaterThanOrEqual(100);
    expect(rows.some((row) => row.path.startsWith('/order/'))).toBe(true);
    expect(rows.some((row) => row.path.includes('?token='))).toBe(true);
  });

  it.each(rows.map((row) => [row.name, row.path, row.expected]))(
    '%s: %j is logged as the table says',
    (_name, path, expected) => {
      expect(logPath(path)).toBe(expected);
    },
  );

  it('logs redacting twice as it logs redacting once', () => {
    for (const row of rows) {
      expect(logPath(row.expected), row.name).toBe(row.expected);
    }
  });

  it('answers the root for no address', () => {
    expect(logPath(undefined)).toBe('/');
    expect(logPath(null)).toBe('/');
    expect(logPath('')).toBe('/');
    expect(logPath('?token=abc')).toBe('/');
  });

  /**
   * The property that matters, over spellings nobody wrote down: whatever comes in, every segment that comes
   * out is a safe slug, `*`, or the PayPal action a credential route keeps, and no control character survives.
   * A fixed seed, so a failure reproduces.
   */
  it('prints only safe segments, whatever it is given', () => {
    let state = 201;
    const next = (bound: number): number => {
      state = (state * 1103515245 + 12345) & 0x7fffffff;

      return state % bound;
    };
    const alphabet = 'abcdefghijklmnopqrstuvwxyz0123456789-ABCDEF/;%._~@: \n\r\t?#é';
    const chunks = [
      '7d1e5c0a-4b3f-4a8e-9c2d-1f6b8a3e5d90',
      'api',
      'orders',
      'order',
      'admin',
      'bank-transfers',
      'payments',
      'paypal',
      'start',
      'RETURN',
      'cancel',
      '12345678',
      'ab-cd',
      'women',
    ];

    for (let attempt = 0; attempt < 6000; attempt += 1) {
      let input = '';
      const parts = next(9);

      for (let part = 0; part < parts; part += 1) {
        input += next(6) === 0 ? '//' : '/';
        if (next(3) === 0) {
          input += chunks[next(chunks.length)];
        } else {
          for (let index = next(25); index > 0; index -= 1) {
            input += alphabet[next(alphabet.length)];
          }
        }
      }

      const logged = logPath(input);

      expect(logged.startsWith('/'), `${input} -> ${logged}`).toBe(true);
      // charCodeAt rather than /[\u0000-\u001f]/, which `no-control-regex` refuses (and `ng lint` is a CI step).
      const hasControlCharacter = [...logged].some((character) => character.charCodeAt(0) <= 0x1f);

      expect(hasControlCharacter, `control character: ${JSON.stringify(logged)}`).toBe(false);
      expect(logged).not.toContain('7d1e5c0a');

      for (const segment of logged.split('/').filter((part) => part.length > 0)) {
        const allowed =
          segment === '*' ||
          isSafeLogSegment(segment) ||
          ['start', 'return', 'cancel'].includes(segment.toLowerCase());

        expect(allowed, `${JSON.stringify(input)} -> ${logged}: ${segment}`).toBe(true);
      }
    }
  });

  it('names the same five credential routes, in the same order, as the API and the gateway', () => {
    expect(CREDENTIAL_ROUTES.map((route) => route.redacted)).toEqual([
      '/order/*',
      '/api/orders/*',
      '/api/payments/paypal/$<sd_a>/*',
      '/api/admin/orders/bank-transfers/*',
      '/api/admin/orders/*',
    ]);
  });
});
