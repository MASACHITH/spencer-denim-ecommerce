import { createServer } from 'node:http';
import type { AddressInfo } from 'node:net';
import express from 'express';

import { answerRenderFailure } from './render-failure';
import { describeError, installConsoleRedaction, redactLogText } from './log-redaction';

/**
 * Issue #201: nothing the storefront's server prints may carry a credential from a request address.
 *
 * The lines under test are the ones `@angular/ssr` writes itself, copied from its source (`fesm2022/node.mjs` and
 * `ssr.mjs`, version 21.2), because those are the ones this project's own code cannot fix at the source.
 */
describe('redactLogText', () => {
  const resetToken = 'CfDJ8Abc-reset-Zk93TmXp41';
  const orderToken = '7d1e5c0a-4b3f-4a8e-9c2d-1f6b8a3e5d90';

  it.each([
    // node.mjs: `An error occurred while writing the response body for: ${destination.req.url}.`
    [
      `An error occurred while writing the response body for: /account/reset-password?token=${resetToken}&email=jane%40example.com.`,
      'An error occurred while writing the response body for: /account/reset-password?*',
    ],
    // ssr.mjs: `An error occurred while inlining critical CSS for: ${url}.`
    [
      `An error occurred while inlining critical CSS for: http://localhost:4000/account/confirm-email?token=${resetToken}.`,
      'An error occurred while inlining critical CSS for: http://localhost:4000/account/confirm-email?*',
    ],
    // ssr.mjs: `ERROR: Bad Request ("${request.url}").` for a Host it will not trust
    [
      `ERROR: Bad Request ("http://evil.example/account/reset-password?token=${resetToken}").\nThe Host header is not allowed.`,
      'ERROR: Bad Request ("http://evil.example/account/reset-password?*").\nThe Host header is not allowed.',
    ],
    // The guest order link is a path credential, not a query one.
    [
      `An error occurred while writing the response body for: /order/${orderToken}.`,
      'An error occurred while writing the response body for: /order/*',
    ],
    [
      `ERROR: Bad Request ("https://evil.example/order/${orderToken}?paypal=return&token=EC-8AB12345CD678901E").`,
      'ERROR: Bad Request ("https://evil.example/order/*?*").',
    ],
    [`GET /api/orders/${orderToken}/payment-slip failed`, 'GET /api/orders/* failed'],
    [`GET /API/PAYMENTS/PAYPAL/Return/${orderToken} failed`, 'GET /api/payments/paypal/Return/* failed'],
  ])('%j becomes %j', (text, expected) => {
    expect(redactLogText(text)).toBe(expected);
  });

  it.each([
    'listening on http://localhost:4000',
    'SIGTERM received, closing HTTP server',
    'sitemap.xml could not be rebuilt: the catalogue answered 503',
    'Are you sure? Yes',
    'at Object.<anonymous> (file:///app/dist/storefront/server/server.mjs:12:3)',
    'GET /women/dresses failed',
  ])('leaves %j alone', (text) => {
    expect(redactLogText(text)).toBe(text);
  });
});

describe('describeError', () => {
  const secret = 'CfDJ8Abc-reset-Zk93TmXp41';

  it('prints the name, the message, the stack and the code, but not the URL Node attaches as `input`', () => {
    const error = Object.assign(new TypeError('Invalid URL'), {
      code: 'ERR_INVALID_URL',
      input: `http://localhost:4000/account/reset-password?token=${secret}`,
    });

    const text = describeError(error);

    expect(text).toContain('TypeError: Invalid URL');
    expect(text).toContain('ERR_INVALID_URL');
    expect(text).not.toContain(secret);
    expect(text).not.toContain('input');
  });

  it('removes the query string from a message that carries the address', () => {
    const text = describeError(new Error(`Cannot render http://localhost:4000/search?q=${secret}`));

    expect(text).toContain('Cannot render http://localhost:4000/search?*');
    expect(text).not.toContain(secret);
  });

  it('follows the cause chain and an aggregate, and filters each', () => {
    const inner = new Error(`inner: /account/confirm-email?token=${secret}`);
    const text = describeError(
      new Error('outer', { cause: new AggregateError([inner], `aggregate /x?token=${secret}`) }),
    );

    expect(text).toContain('Caused by: ');
    expect(text).toContain('Contains: ');
    expect(text).toContain('inner: /account/confirm-email?*');
    expect(text).not.toContain(secret);
  });

  it('describes a value that is not an error', () => {
    expect(describeError(`failed: /search?q=${secret}`)).toBe('failed: /search?*');
    expect(describeError({ url: `/search?q=${secret}` })).not.toContain(secret);
  });
});

describe('installConsoleRedaction', () => {
  const secret = 'CfDJ8Abc-reset-Zk93TmXp41';

  function fakeConsole() {
    const written: Record<'log' | 'info' | 'warn' | 'error' | 'debug', string[]> = {
      log: [],
      info: [],
      warn: [],
      error: [],
      debug: [],
    };
    const target = {
      log: (...args: unknown[]) => written.log.push(args.join(' ')),
      info: (...args: unknown[]) => written.info.push(args.join(' ')),
      warn: (...args: unknown[]) => written.warn.push(args.join(' ')),
      error: (...args: unknown[]) => written.error.push(args.join(' ')),
      debug: (...args: unknown[]) => written.debug.push(args.join(' ')),
    };

    return { target, written };
  }

  it('redacts everything every console method writes', () => {
    const { target, written } = fakeConsole();
    installConsoleRedaction(target);

    target.log(`log /a?token=${secret}`);
    target.info(`info /a?token=${secret}`);
    target.warn(`warn /a?token=${secret}`);
    target.error(`error /a?token=${secret}`);
    target.debug(`debug /a?token=${secret}`);

    for (const method of ['log', 'info', 'warn', 'error', 'debug'] as const) {
      expect(written[method]).toEqual([`${method} /a?*`]);
    }
  });

  it('redacts an Error argument as Node would print it, including the property that holds the input', () => {
    const { target, written } = fakeConsole();
    installConsoleRedaction(target);

    target.error(
      'unhandled',
      Object.assign(new TypeError('Invalid URL'), {
        code: 'ERR_INVALID_URL',
        input: `http://localhost:4000/account/reset-password?token=${secret}`,
      }),
    );

    expect(written.error).toHaveLength(1);
    expect(written.error[0]).toContain('ERR_INVALID_URL');
    expect(written.error[0]).not.toContain(secret);
  });

  it('keeps format specifiers working', () => {
    const { target, written } = fakeConsole();
    installConsoleRedaction(target);

    target.log('%s listening on %d', 'storefront', 4000);

    expect(written.log).toEqual(['storefront listening on 4000']);
  });

  it('wraps once, and gives the originals back', () => {
    const { target, written } = fakeConsole();
    const original = target.log;

    const restore = installConsoleRedaction(target);
    const wrapped = target.log;
    installConsoleRedaction(target);

    expect(target.log).toBe(wrapped);

    restore();

    expect(target.log).toBe(original);

    target.log(`/a?token=${secret}`);
    expect(written.log).toEqual([`/a?token=${secret}`]);
  });
});

/**
 * The storefront's server, end to end as far as a test can take it without a browser: a real Express app on a
 * real socket, a route whose render fails with an error that carries the request address, the same error
 * handler `server.ts` mounts, and the wrapped console. Everything the process then prints, through any console
 * method, is searched for the credential in the path and in the query string.
 */
describe('a failed render on a real server', () => {
  const resetToken = 'CfDJ8Abc-reset-Zk93TmXp41';
  const orderToken = '7d1e5c0a-4b3f-4a8e-9c2d-1f6b8a3e5d90';

  it('prints neither the query string nor the order token, by any route into the log', async () => {
    const printed: string[] = [];
    const fake = {
      log: (...args: unknown[]) => printed.push(args.join(' ')),
      info: (...args: unknown[]) => printed.push(args.join(' ')),
      warn: (...args: unknown[]) => printed.push(args.join(' ')),
      error: (...args: unknown[]) => printed.push(args.join(' ')),
      debug: (...args: unknown[]) => printed.push(args.join(' ')),
    };
    const restore = installConsoleRedaction(fake);

    const app = express();

    // What a render does when it fails: throws an error that names the address it was asked for, with the
    // property Node adds to an invalid-URL error, and a library that prints the URL on its own.
    app.use((req, _res, next) => {
      fake.error(`An error occurred while writing the response body for: ${req.url}.`);
      next(
        Object.assign(new TypeError(`Invalid URL: http://localhost${req.originalUrl}`), {
          code: 'ERR_INVALID_URL',
          input: `http://localhost${req.originalUrl}`,
        }),
      );
    });
    app.use((error: unknown, req: express.Request, res: express.Response, next: express.NextFunction) => {
      if (
        answerRenderFailure(error, req, res, (message, cause) => fake.error(message, cause)) ===
        'already-answered'
      ) {
        next(error);
      }
    });

    const server = createServer(app);
    await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve));

    try {
      const { port } = server.address() as AddressInfo;

      for (const target of [
        `/account/reset-password?token=${resetToken}&email=jane%40example.com`,
        `/account/confirm-email?token=${resetToken}&userId=42`,
        `/order/${orderToken}?paypal=return&token=${resetToken}`,
        `/search?q=0771234567`,
      ]) {
        const response = await fetch(`http://127.0.0.1:${port}${target}`);

        expect(response.status).toBe(500);
        // The visitor is told one sentence and nothing else.
        expect(await response.text()).toBe('Something went wrong on our side. Please try again.\n');
      }
    } finally {
      await new Promise<void>((resolve) => server.close(() => resolve()));
      restore();
    }

    const everything = printed.join('\n');

    // The sweep looked at something: four failures were logged, with their paths.
    expect(printed.filter((line) => line.includes(' failed'))).toHaveLength(4);
    expect(everything).toContain('GET /account/reset-password failed');
    expect(everything).toContain('GET /order/* failed');

    for (const secret of [resetToken, orderToken, 'jane%40example.com', 'jane@example.com', '0771234567', 'token=', 'input:']) {
      expect(everything, secret).not.toContain(secret);
    }
  });
});
