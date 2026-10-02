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
  const raw = process.env['PUBLIC_SITE_ORIGIN'];
  const configured = normaliseOrigin(raw);
  if (configured) {
    return configured;
  }

  if (raw !== undefined && raw.trim() !== '') {
    // **Said out loud, which was F6.** The docblock below claims a deployment is better served by "a
    // look at its configuration" than by a plausible-looking wrong answer - and then rejected the value
    // in complete silence, so nothing told anyone to look. A value that was set and refused is a
    // configuration mistake, not a default, and it is the one case here worth an error.
    warnOnce(
      raw,
      `PUBLIC_SITE_ORIGIN is set to something unusable and is being ignored: ${describeRejected(raw)}. ` +
        'It must be a scheme and host only, for example https://colombowardrobecompany.com. ' +
        `Falling back to https://${BRAND.domain}, which is wrong on any host but production.`,
    );
  }

  const host = first(headers['x-forwarded-host']) ?? first(headers.host);
  if (host && HOST_PATTERN.test(host)) {
    // The **normalised** host, which was F7: `Host: LOCALHOST:5200` was echoed verbatim into the
    // `Sitemap:` line and into every `og:url`, so one deployment could publish several spellings of one
    // address. `isAllowedHost` already lower-cases and strips the port to make its decision; returning
    // what it matched means the answer cannot disagree with the check.
    const allowed = allowedHost(host);
    if (allowed) {
      return `${protocolFrom(headers)}://${allowed}`;
    }
  }

  return `https://${BRAND.domain}`;
}

/**
 * Says a thing once per process rather than once per request.
 *
 * A misconfiguration is a standing condition, and `sitemap.xml` plus every rendered page would otherwise
 * repeat it until the disk filled - which is how a real message becomes noise and then becomes filtered.
 *
 * Keyed on the **value**, not on the message: `describeRejected` describes the shape of the mistake, so
 * two different bad values produce one line of text, and keying on the text would silence the second
 * misconfiguration anybody made. The key never leaves this set, which is why keying on a value that may
 * carry a credential is safe where logging it would not be.
 */
const alreadyWarned = new Set<string>();

function warnOnce(key: string, message: string): void {
  if (alreadyWarned.has(key)) {
    return;
  }

  alreadyWarned.add(key);
  console.error(message);
}

/**
 * The rejected value, described rather than echoed.
 *
 * It comes from this deployment's own environment, so there is no attacker here - but a configured value
 * can carry a credential (`https://user:password@host` is one of the shapes this refuses), and a log line
 * is the wrong place for one.
 */
function describeRejected(raw: string): string {
  const trimmed = raw.trim();

  let url: URL;
  try {
    url = new URL(trimmed);
  } catch {
    return 'it is not a URL';
  }

  if (url.protocol !== 'http:' && url.protocol !== 'https:') {
    return `its scheme is "${url.protocol.replace(':', '')}" rather than http or https`;
  }
  if (url.username || url.password) {
    return 'it carries credentials';
  }
  if (url.search || url.hash) {
    return 'it carries a query string or a fragment';
  }

  return `it carries the path "${url.pathname}"`;
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
 * The host we are willing to repeat back to a crawler, **normalised**, or `null` when there is none.
 *
 * It returns the value rather than a boolean so that the answer cannot disagree with the check: the
 * decision is made on the lower-cased name, so that is what gets published. `Host: LOCALHOST:5200` was
 * being echoed verbatim, which meant one deployment could publish several spellings of a single address
 * in its `Sitemap:` line and its `og:url` tags (F7).
 *
 * The port is kept, because a port is part of an origin and `ng serve` and the browser tests are
 * reached on one. Only its case and the name's are settled here, since a port has no case.
 *
 * `NG_ALLOWED_HOSTS` is read per call rather than once at module load so a test can set it; it is a
 * handful of string comparisons on a request answered a few times an hour.
 */
function allowedHost(host: string): string | null {
  const name = withoutPort(host).toLowerCase();
  const port = host.slice(withoutPort(host).length);

  if (LOCAL_HOSTS.has(name)) {
    return `${name}${port}`;
  }

  const listed = (process.env['NG_ALLOWED_HOSTS'] ?? '')
    .split(',')
    .map((entry) => withoutPort(entry.trim()).toLowerCase())
    .filter((entry) => entry.length > 0)
    .includes(name);

  return listed ? `${name}${port}` : null;
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
