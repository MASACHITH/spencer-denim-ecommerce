import { effectivePath } from './canonical-path';
import { logPath } from './log-path';
import { describeError } from './log-redaction';
import { RequestHeaders } from './origins';
import { robotsTxt } from './robots-txt';

/**
 * What the storefront answers when a render fails for a reason nobody anticipated.
 *
 * **Why this is a module and not four lines in `server.ts`.** It is the second line of defence behind
 * the path canonicaliser, and a second line of defence that has never been exercised is a comment. In
 * its own file it is a pure function of (error, request, response) with a test that can provoke it,
 * which is the difference between "should be unreachable" and "is harmless when reached".
 *
 * Two things it fixes, both measured on the pre-fix build:
 *
 * 1. **Express's default handler answers `text/html` carrying the stack and the absolute build
 *    directory** — `.../sd-rename/src/frontend/dist/storefront/server/…` — whenever `NODE_ENV` is not
 *    `production`, which is this project's own development default. That is reconnaissance handed to
 *    anybody who can provoke an error. One line in the log, one sentence on the wire.
 * 2. **`robots.txt` is answered rather than failed.** Its content depends on nothing that can break —
 *    a constant plus this request's own origin — so there is no reading of a failure here in which 5xx
 *    is the honest answer, and 5xx on that file tells a crawler the **entire site** is disallowed. A
 *    404 would at least mean "crawl freely"; the file itself is better than either.
 *
 * 3. **The log line names the path, never the address (#201).** `originalUrl` carries the query string, and a
 *    failed render of `/account/reset-password?token=…` or `/account/confirm-email?token=…` used to write that
 *    live credential into the container's log, which Docker keeps. The line now prints {@link logPath} of the
 *    request (the same redaction the API's and the gateway's logs follow: a slug path, `/order/*`, no query), and
 *    the error that goes with it is {@link describeError}, not the raw object, which Node prints with its
 *    `input` property in full.
 *
 * The types are structural rather than Express's own, so a test needs no HTTP server and no fakes
 * beyond an object literal. Express's `Request` and `Response` satisfy them.
 */

/** Just enough of a request to decide what to answer. */
export interface FailedRequest {
  readonly method: string;
  readonly originalUrl: string;
  /** The path with the query string removed, as Express computes it. */
  readonly path: string;
  readonly headers: RequestHeaders;
}

/** Just enough of a response to answer. */
export interface FailingResponse {
  readonly headersSent: boolean;
  status(code: number): unknown;
  type(value: string): unknown;
  setHeader(name: string, value: string): unknown;
  send(body: string): unknown;
}

/** What {@link answerRenderFailure} did, so a caller and a test can both tell. */
export type FailureOutcome = 'robots' | 'error' | 'already-answered';

/**
 * Answers a failed request, and returns which of the three things it did.
 *
 * `log` is injected so the test can read what was logged rather than silence the console: what goes to
 * the log is half the point, and a handler that swallowed the error would be worse than the default.
 */
export function answerRenderFailure(
  error: unknown,
  request: FailedRequest,
  response: FailingResponse,
  log: (message: string, error: unknown) => void = (message, cause) => console.error(message, cause),
): FailureOutcome {
  // The whole error, including the stack, but only here. The path, not the address: see the file comment (3).
  log(`${request.method} ${logPath(request.originalUrl)} failed`, describeError(error));

  if (response.headersSent) {
    // Something already started writing. Anything more would corrupt the body.
    return 'already-answered';
  }

  // The **effective** path, not the raw one (F9). Express does not resolve dot segments either, so
  // `req.path` for `/.//robots.txt` is `/.//robots.txt` and a raw comparison missed — which meant the
  // branch that exists so `robots.txt` can never be a 5xx answered 500 for the one shape that actually
  // reached it. A last line of defence that is narrower than the request is not one.
  if (effectivePath(request.path) === '/robots.txt') {
    response.status(200);
    response.type('text/plain; charset=utf-8');
    // Not cached: this one was served from a failure path, so the next request should ask again.
    response.setHeader('Cache-Control', 'no-store');
    response.send(robotsTxt(request.headers));

    return 'robots';
  }

  response.status(500);
  response.type('text/plain; charset=utf-8');
  response.setHeader('Cache-Control', 'no-store');
  // One sentence. No status code reflected back, no stack, no path, nothing about the error.
  response.send('Something went wrong on our side. Please try again.\n');

  return 'error';
}
