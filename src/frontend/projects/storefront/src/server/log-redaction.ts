import { formatWithOptions, inspect } from 'node:util';

import { CREDENTIAL_ROUTES } from './log-path';

/**
 * Keeps a credential out of what the storefront's server prints (issue #201, follow-up to #179).
 *
 * **What leaked.** `render-failure.ts` logged `` `${method} ${originalUrl} failed` ``, and `originalUrl` carries
 * the query string, so a failed server render of `/account/reset-password?token=…` or
 * `/account/confirm-email?token=…` wrote a live credential to the container's log. Worse than that one line is
 * the rest of what the process prints about a request, none of which this code writes: `@angular/ssr`
 * itself prints the request URL, query string included, in `An error occurred while writing the response body
 * for: ${req.url}`, `An error occurred while inlining critical CSS for: ${url}` and, for every request whose
 * `Host` it will not trust, `ERROR: Bad Request ("${request.url}")`. A Node `TypeError [ERR_INVALID_URL]` carries
 * the offending URL in an `input` property that `console.error(error)` prints in full.
 *
 * **Three layers, because no one of them is enough.**
 * 1. The line this server writes about a failed request uses {@link logPath}, the path under the same redaction
 *    rule as the API's and the gateway's logs, and never the query string (`render-failure.ts`).
 * 2. The error object that goes with it is not handed to the console as it is: {@link describeError} prints
 *    its name, message, stack and cause chain, and the properties Node adds that carry the input (`input`) are
 *    not among them.
 * 3. What libraries print on their own is filtered at the console: {@link installConsoleRedaction} wraps
 *    `console.log/info/warn/error/debug` once, in `server.ts`, so every line passes {@link redactLogText}
 *    whoever wrote it.
 *
 * **What {@link redactLogText} can and cannot do.** It removes a query string wherever one appears in text
 * (a `?` that follows an address character and is followed by something that is not whitespace) and stars the
 * path of every route in {@link CREDENTIAL_ROUTES}. It cannot know a secret that is neither: a token inside a
 * path that is not a named route, printed by a library outside a query string, is not recognised, which is
 * why layer 1 exists and why the allow-list in {@link logPath} is the primary defence. It errs towards
 * removing too much: a `?` in a message that happens to follow a word without a space loses what follows it.
 */

/** The character that ends a token in running text: whitespace, quotes, brackets, a backtick, a backslash. */
const END_OF_TOKEN = String.raw`\s"'` + '`' + String.raw`<>)\]}\\`;

/** A query string in running text: the `?` after an address character, and everything up to the end of the token. */
const QUERY_IN_TEXT = new RegExp(String.raw`(?<=[A-Za-z0-9_\-./~%@:+,;=!$&*])\?[^${END_OF_TOKEN}]+`, 'g');

/** What a removed query string is replaced by. */
const QUERY_REMOVED = '?*';

/**
 * One regular expression per credential route, for running text: the route's own pattern (anchor removed), found
 * anywhere (so after a host name too: `ERROR: Bad Request ("http://host/order/<token>")`), and the rest of the
 * token swallowed. `/order/<token>` in "for: /order/<token>." becomes `/order/*`. Unanchored on purpose: it
 * stars a little too much in a stack frame that happens to contain `/order/`, which costs nothing, and it
 * cannot miss a URL for want of a lookbehind.
 */
const ROUTES_IN_TEXT = CREDENTIAL_ROUTES.map((route) => ({
  pattern: new RegExp(`${route.pattern.source.replace(/^\^/, '')}[^${END_OF_TOKEN}?#]*`, 'gi'),
  redacted: route.redacted,
}));

/** `text` with every query string removed and every credential route's path starred. */
export function redactLogText(text: string): string {
  let redacted = text;

  for (const route of ROUTES_IN_TEXT) {
    redacted = redacted.replace(route.pattern, (...match: unknown[]) => {
      const groups = match[match.length - 1];

      return route.redacted.replace(
        '$<sd_a>',
        typeof groups === 'object' && groups !== null ? ((groups as Record<string, string>)['sd_a'] ?? '') : '',
      );
    });
  }

  return redacted.replace(QUERY_IN_TEXT, QUERY_REMOVED);
}

/** How deep a `cause` chain is followed. */
const MAX_CAUSE_DEPTH = 4;

/**
 * An error as a log may print it: its stack (which starts with its name and message), its `code`, and its
 * cause chain, each passed through {@link redactLogText}. What it deliberately does **not** print is the rest of
 * the error's own properties, which is where Node puts the offending value (`input` on an invalid-URL error).
 */
export function describeError(error: unknown, depth = 0): string {
  if (error instanceof Error) {
    const lines = [error.stack ?? `${error.name}: ${error.message}`];
    const code = (error as { code?: unknown }).code;

    if ((typeof code === 'string' || typeof code === 'number') && !lines[0].includes(String(code))) {
      lines.push(`code: ${String(code)}`);
    }

    if (error.cause !== undefined && depth < MAX_CAUSE_DEPTH) {
      lines.push(`Caused by: ${describeError(error.cause, depth + 1)}`);
    }

    if (error instanceof AggregateError && depth < MAX_CAUSE_DEPTH) {
      for (const inner of error.errors as unknown[]) {
        lines.push(`Contains: ${describeError(inner, depth + 1)}`);
      }
    }

    return redactLogText(lines.join('\n'));
  }

  return redactLogText(typeof error === 'string' ? error : inspect(error, { depth: 1, breakLength: Infinity }));
}

type LogMethod = 'log' | 'info' | 'warn' | 'error' | 'debug';

const METHODS: readonly LogMethod[] = ['log', 'info', 'warn', 'error', 'debug'];

/**
 * Marks a console that has been wrapped. A registry symbol rather than a module-level set, because this file is
 * bundled into more than one entry point (a running storefront holds two instances of every module here), and a
 * second instance must see that the first has already wrapped the one global console.
 */
const INSTALLED = Symbol.for('spencer-denim.console-redaction');

/**
 * Replaces `console.log/info/warn/error/debug` with versions that format their arguments the way the console
 * would and pass the text through {@link redactLogText}. Idempotent. Returns the function that puts the originals
 * back, which only a test needs.
 *
 * Arguments are formatted with `util.formatWithOptions` before they reach the original method, so an `Error`
 * argument is printed as Node prints it (stack, then its properties) and then filtered: a property Node added
 * that carries a URL is scrubbed by the same filter.
 */
export function installConsoleRedaction(target: Pick<Console, LogMethod> = console): () => void {
  if ((target as Record<symbol, unknown>)[INSTALLED] === true) {
    return () => undefined;
  }

  (target as Record<symbol, unknown>)[INSTALLED] = true;

  const originals = new Map<LogMethod, Console[LogMethod]>();

  for (const method of METHODS) {
    const original = target[method];
    originals.set(method, original);

    target[method] = (...args: unknown[]) => {
      original.call(target, redactLogText(formatWithOptions({}, ...args)));
    };
  }

  return () => {
    for (const [method, original] of originals) {
      target[method] = original;
    }

    delete (target as Record<symbol, unknown>)[INSTALLED];
  };
}
