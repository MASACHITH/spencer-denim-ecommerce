import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';

/**
 * `server.ts` really installs the console redaction, and does it before anything else can print (issue #201).
 *
 * **Why a source check.** `installConsoleRedaction` is covered, with a fake console, by `log-redaction.spec.ts`;
 * what nothing covered is the one line that calls it. #201's tester replaced `installConsoleRedaction();` with
 * `void installConsoleRedaction;` and all 269 server specs stayed green, while the real built server then wrote
 * every reset token, order token and e-mail address that `@angular/ssr` prints for a request with an
 * untrusted `Host` (`ERROR: Bad Request ("http://…/account/reset-password?token=…")`). `server.ts` cannot be
 * imported by a spec — it builds the Angular engine from the build output and starts an Express app — so the
 * wiring is asserted on its text, with comments removed so that a mention in a docblock cannot satisfy it.
 *
 * It is a weak check by nature (it pins that the statement exists and where, not that it runs), and it is the
 * strongest one available without booting the server. The behaviour behind it is `log-redaction.spec.ts`.
 */
describe('server.ts', () => {
  const source = readFileSync(resolve(process.cwd(), 'projects/storefront/src/server.ts'), 'utf8');

  /** The file with block comments and line comments removed, so only code is searched. */
  const code = source.replace(/\/\*[\s\S]*?\*\//g, '').replace(/^\s*\/\/.*$/gm, '');

  it('imports installConsoleRedaction from the redaction module', () => {
    expect(code).toMatch(/import\s*\{[^}]*\binstallConsoleRedaction\b[^}]*\}\s*from\s*'\.\/server\/log-redaction'/);
  });

  it('calls it as a top-level statement, not merely mentions it', () => {
    expect(code).toMatch(/^installConsoleRedaction\(\);\s*$/m);
  });

  it('calls it before the Express application is created', () => {
    const call = code.search(/^installConsoleRedaction\(\);\s*$/m);
    const app = code.search(/\bexpress\(\)/);

    expect(call, 'installConsoleRedaction() is not called').toBeGreaterThanOrEqual(0);
    expect(app, 'express() is not called').toBeGreaterThanOrEqual(0);
    expect(call).toBeLessThan(app);
  });
});
