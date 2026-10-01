import { BRAND } from '@spencer-denim/shared';

/**
 * The two origins the SSR process has to know about, and where each one comes from (GLB-11).
 *
 * Nothing in this file reads a hard-coded host: doc 03 §5 is explicit that no code may depend on the
 * host, and both values are configuration. It lives under `src/server/` because `process.env` only
 * exists there — the browser bundle must never import it.
 */

/** Default for `ng serve` with the API running under `dotnet watch`, as `app.config.server.ts` uses. */
export const DEFAULT_API_INTERNAL_URL = 'http://localhost:5080';

/**
 * Where this process reaches the API: the container address in production, the stub in the browser
 * tests. One constant, imported by `app.config.server.ts` as well, so the render and the server's own
 * `sitemap.xml` can never end up asking two different catalogues.
 */
export function apiInternalUrl(): string {
  return process.env['API_INTERNAL_URL'] || DEFAULT_API_INTERNAL_URL;
}

/** A plausible `host` value: a name or address, optionally with a port. Nothing else. */
const HOST_PATTERN = /^[a-z0-9.\-[\]:]+$/i;

/** Hosts a development machine and the browser tests reach the server by. */
const LOCAL_HOSTS = new Set(['localhost', '127.0.0.1', '[::1]', '::1']);

/** Just the headers this needs, so a caller can pass an Express request or a plain object. */
export interface RequestHeaders {
  readonly host?: string | string[];
  readonly 'x-forwarded-host'?: string | string[];
  readonly 'x-forwarded-proto'?: string | string[];
}

/**
 * The absolute origin to publish in an answer that cannot use a relative address: the `Sitemap:` line
 * in `robots.txt`, every `<loc>` in `sitemap.xml`, and `og:url`.
 *
 * **Why this is not simply the `Host` header.** `Host` and `X-Forwarded-Host` are sent by the client.
 * This project has already shipped one defect of exactly that shape — W1-01 round 3, where an
 * unauthenticated request forged `X-Forwarded-For` and the value was written into `ops.AuditLogs` — and
 * the fix there was to stop trusting the header rather than to sanitise it. The same reasoning applies
 * here: a forged `Host` would put someone else's domain into the `Sitemap:` line and into every
 * `og:url`, which is a shop asking a crawler to go and index a stranger's site in its name.
 *
 * So the order is:
 *
 * 1. **`PUBLIC_SITE_ORIGIN`**, the deployment's own answer. Behind Caddy this is the only thing that
 *    knows the public name, so production sets it and nothing else is consulted.
 * 2. the request's host, **only when it is a host we recognise** — a loopback name, or one listed in
 *    `NG_ALLOWED_HOSTS`, which is the same allow-list the Angular engine already validates renders
 *    against. That covers `ng serve`, the browser tests and a staging box configured properly.
 * 3. `https://` plus the brand's own domain, so a misconfigured deployment publishes the right shop's
 *    address rather than a half-formed one.
 *
 * It never returns a relative or empty string: a `<loc>` without a scheme is an invalid sitemap, and
 * an `og:url` without one is ignored.
 */
export function publicOrigin(headers: RequestHeaders): string {
  const configured = normaliseOrigin(process.env['PUBLIC_SITE_ORIGIN']);
  if (configured) {
    return configured;
  }

  const host = first(headers['x-forwarded-host']) ?? first(headers.host);
  if (host && HOST_PATTERN.test(host) && isAllowedHost(host)) {
    return `${protocolFrom(headers)}://${host}`;
  }

  return `https://${BRAND.domain}`;
}

/**
 * {@link publicOrigin} for Angular's `REQUEST` token, whose headers are a Fetch-API `Headers` rather
 * than Node's plain object. Same decision, one shape conversion, so the render and the server's own
 * `robots.txt` cannot disagree about what this site is called.
 */
export function publicOriginFor(request: { readonly headers: Headers } | null): string {
  const headers = request?.headers;

  return publicOrigin({
    host: headers?.get('host') ?? undefined,
    'x-forwarded-host': headers?.get('x-forwarded-host') ?? undefined,
    'x-forwarded-proto': headers?.get('x-forwarded-proto') ?? undefined,
  });
}

/** `https` only when a proxy said so in so many words. Anything else is read as plain HTTP. */
function protocolFrom(headers: RequestHeaders): 'http' | 'https' {
  return first(headers['x-forwarded-proto'])?.toLowerCase() === 'https' ? 'https' : 'http';
}

/**
 * Whether we are willing to repeat this host back to a crawler.
 *
 * `NG_ALLOWED_HOSTS` is read per call rather than once at module load so a test can set it; it is a
 * handful of string comparisons on a request that is answered a few times an hour.
 */
function isAllowedHost(host: string): boolean {
  const name = withoutPort(host).toLowerCase();
  if (LOCAL_HOSTS.has(name)) {
    return true;
  }

  return (process.env['NG_ALLOWED_HOSTS'] ?? '')
    .split(',')
    .map((entry) => withoutPort(entry.trim()).toLowerCase())
    .filter((entry) => entry.length > 0)
    .includes(name);
}

/** `example.com:4000` → `example.com`, leaving a bracketed IPv6 literal alone. */
function withoutPort(host: string): string {
  if (host.startsWith('[')) {
    return host.slice(0, host.indexOf(']') + 1) || host;
  }

  const colon = host.lastIndexOf(':');
  return colon > 0 ? host.slice(0, colon) : host;
}

/**
 * A configured origin, or `null` when it is not usable as one.
 *
 * Rejected rather than repaired when it carries a path, a query or credentials: `PUBLIC_SITE_ORIGIN`
 * with a trailing path silently doubles a segment into every URL in the sitemap, and a deployment is
 * better served by the brand fallback and a look at its configuration than by a plausible-looking
 * wrong answer.
 */
function normaliseOrigin(value: string | undefined): string | null {
  const trimmed = value?.trim();
  if (!trimmed) {
    return null;
  }

  let url: URL;
  try {
    url = new URL(trimmed);
  } catch {
    return null;
  }

  if (url.protocol !== 'http:' && url.protocol !== 'https:') {
    return null;
  }
  if (url.username || url.password || url.search || url.hash) {
    return null;
  }
  if (url.pathname !== '/' && url.pathname !== '') {
    return null;
  }

  return url.origin;
}

/** Node gives a repeated header as an array; only the first value of these is ever meaningful. */
function first(value: string | string[] | undefined): string | undefined {
  const raw = Array.isArray(value) ? value[0] : value;
  const trimmed = raw?.split(',')[0]?.trim();
  return trimmed ? trimmed : undefined;
}
