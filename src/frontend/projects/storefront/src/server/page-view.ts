import { randomUUID } from 'node:crypto';

import type { TrafficRenderContext } from '../app/core/traffic/traffic-render-context';

/**
 * The server half of traffic recording (W4-03, ADM-06, ADM-20; doc 08 §3.8).
 *
 * ## Who records a page view, and why exactly one of them does
 *
 * The storefront is server-rendered on the first request and navigates in the browser after that, so
 * neither side can record alone:
 *
 * - **The server only** misses every page a shopper reaches by clicking — a client-side navigation never
 *   reaches this process.
 * - **The browser only** misses every visitor without JavaScript, and every crawler (doc 08 counts bot
 *   views: `TrafficDaily.BotPageViews`) — and it would double-count against the server if both recorded.
 *
 * So the server records **the page it rendered**, and the browser records **every page after it**:
 *
 * 1. Before the render, this process decides whether the request is one it will record
 *    ({@link isRecordableRequest}: a GET that is not a prefetch) and passes that to the render in a
 *    {@link TrafficRenderContext}.
 * 2. The render marks the context as rendered and, when it is recordable, leaves a flag in the transfer
 *    state (`markServerPageView` in `app.config.server.ts`). That flag travels inside the HTML.
 * 3. After the render, this process records the view **if** the render ran, the request was recordable,
 *    and the answer was a 200 HTML page ({@link shouldRecordServerView}). A 404 or a 503 is not a page view.
 * 4. In the browser, `PageViewTracker` skips the first navigation when the flag is there, and records every
 *    later navigation whose path changed. With no flag — the engine served the client-side shell, or the
 *    request was a prefetch — the browser records the first page itself.
 *
 * Steps 2 and 4 make "the server recorded it" and "the browser skips it" the same fact, carried in the
 * page, rather than two guesses that could disagree.
 *
 * ## It never holds a page up
 *
 * The recording call is made after the page has been handed to the response, is not awaited, has a
 * two-second timeout, and swallows every failure. The API itself only queues the view.
 *
 * ## What is sent
 *
 * The address, the referrer only when it is another site's, and the visitor's user agent — in the body,
 * because this process's own `User-Agent` header is Node's. **Never the visitor's IP address**: nothing
 * here reads `X-Forwarded-For` or the socket address. The API strips the address down to what it may
 * store (`PagePath` in the API), so a secret in a URL is removed in one place, not two.
 */

/** The visit-session cookie, the same name and rules as the API's `VisitSessionCookie`. */
export const VISIT_COOKIE = 'sd_vs';

/** Thirty idle minutes end a visit; every recorded view restarts the thirty minutes. */
export const VISIT_IDLE_SECONDS = 30 * 60;

/** The API route the view is posted to. */
export const PAGE_VIEW_ROUTE = '/api/traffic/page-views';

/** How long the recording call may take before it is abandoned. */
export const PAGE_VIEW_TIMEOUT_MS = 2000;

const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

const EMPTY_GUID = '00000000-0000-0000-0000-000000000000';

/** Just the request headers this module reads, so a test can pass a plain object. */
export type Headers = Readonly<Record<string, string | string[] | undefined>>;

function header(headers: Headers, name: string): string | undefined {
  const value = headers[name];
  return Array.isArray(value) ? value[0] : value;
}

/** A fresh context for one render. */
export function createTrafficContext(method: string, headers: Headers): TrafficRenderContext {
  return { recordable: isRecordableRequest(method, headers), rendered: false };
}

/**
 * Whether a request is one a person (or a crawler) is looking at. A HEAD is not a view; neither is a
 * browser's speculative prefetch or prerender, which may never be shown — when one is shown, the page has
 * no server flag and the browser records it then.
 */
export function isRecordableRequest(method: string, headers: Headers): boolean {
  if (method !== 'GET') {
    return false;
  }

  const purpose = `${header(headers, 'sec-purpose') ?? ''} ${header(headers, 'purpose') ?? ''} ${
    header(headers, 'x-moz') ?? ''
  } ${header(headers, 'x-purpose') ?? ''}`.toLowerCase();

  return !purpose.includes('prefetch') && !purpose.includes('prerender') && !purpose.includes('preview');
}

/** What the render answered, as far as recording cares. */
export interface RenderedAnswer {
  readonly status: number;
  readonly contentType: string | null;
}

/** Step 3: record only a recordable request that the render answered with a 200 HTML page. */
export function shouldRecordServerView(
  context: TrafficRenderContext,
  answer: RenderedAnswer | null,
): boolean {
  return (
    context.recordable &&
    context.rendered &&
    answer !== null &&
    answer.status === 200 &&
    (answer.contentType ?? '').toLowerCase().includes('text/html')
  );
}

/**
 * The visit session the request carries, or a new one. A value that is not a non-empty GUID is replaced,
 * never refused — the same rule as the API's.
 */
export function visitSession(cookieHeader: string | undefined): string {
  for (const part of (cookieHeader ?? '').split(';')) {
    const separator = part.indexOf('=');
    if (separator < 0 || part.slice(0, separator).trim() !== VISIT_COOKIE) {
      continue;
    }

    const value = part.slice(separator + 1).trim();
    if (GUID.test(value) && value !== EMPTY_GUID) {
      return value.toLowerCase();
    }
  }

  return randomUUID();
}

/**
 * The `Set-Cookie` value. `Secure` always, like the API's cookies: production is HTTPS only, and browsers
 * treat `http://localhost` as a secure context, so development and the browser tests are unaffected.
 */
export function visitCookie(session: string): string {
  return `${VISIT_COOKIE}=${session}; Max-Age=${VISIT_IDLE_SECONDS}; Path=/; HttpOnly; Secure; SameSite=Lax`;
}

/**
 * The referrer, only when it is another site. A referrer on this site — a shopper who opened a link in a
 * new tab — is a click inside the shop, not a source. "This site" is the host the request came in on and
 * the deployment's `PUBLIC_SITE_ORIGIN`.
 */
export function externalReferrer(headers: Headers, publicSiteOrigin: string | undefined): string | null {
  const referrer = header(headers, 'referer');
  if (!referrer) {
    return null;
  }

  let referrerHost: string;
  try {
    referrerHost = new URL(referrer).host.toLowerCase();
  } catch {
    return null;
  }

  const ownHosts = new Set<string>();
  for (const value of [header(headers, 'x-forwarded-host'), header(headers, 'host')]) {
    if (value) {
      ownHosts.add(value.split(',')[0].trim().toLowerCase());
    }
  }

  if (publicSiteOrigin) {
    try {
      ownHosts.add(new URL(publicSiteOrigin).host.toLowerCase());
    } catch {
      // An unusable PUBLIC_SITE_ORIGIN is reported by origins.ts; here it just adds nothing.
    }
  }

  return ownHosts.has(referrerHost) ? null : referrer;
}

/** The body posted to the API for a server-rendered page. */
export interface ServerPageView {
  readonly path: string;
  readonly referrer: string | null;
  /** The visitor's user agent; an empty string when they sent none, which the API counts as a bot. */
  readonly userAgent: string;
}

export function serverPageView(url: string, headers: Headers, publicSiteOrigin: string | undefined): ServerPageView {
  return {
    path: url,
    referrer: externalReferrer(headers, publicSiteOrigin),
    userAgent: header(headers, 'user-agent') ?? '',
  };
}

/**
 * Posts the view and never fails: no await reaches the caller, a slow API is abandoned after
 * {@link PAGE_VIEW_TIMEOUT_MS}, and every error is swallowed. The session travels as the cookie the API
 * reads, so the API's code path is the same one the browser's beacon takes.
 */
export function sendServerPageView(
  apiBaseUrl: string,
  view: ServerPageView,
  session: string,
  send: typeof fetch = fetch,
): Promise<void> {
  try {
    return send(new URL(PAGE_VIEW_ROUTE, apiBaseUrl), {
      method: 'POST',
      headers: { 'content-type': 'application/json', cookie: `${VISIT_COOKIE}=${session}` },
      body: JSON.stringify(view),
      signal: AbortSignal.timeout(PAGE_VIEW_TIMEOUT_MS),
    }).then(
      () => undefined,
      () => undefined,
    );
  } catch {
    return Promise.resolve();
  }
}
