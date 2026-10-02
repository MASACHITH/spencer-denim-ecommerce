/**
 * The one request shape that must never reach anything else: a path that **ends up** with a leading run
 * of slashes.
 *
 * **What went wrong, and why it is here rather than in a route.** `@angular/platform-server` throws
 * `NG05702 "Protocol relative URLs are not allowed in this context"` from a provider factory while the
 * injector is hydrating — so only once a component is about to render. While the catch-all route was
 * `redirectTo: ''` the throw was unreachable, because a redirect route short-circuits before any
 * component is built. Pointing the catch-all at a real page (#28) made every such address throw instead,
 * and the render handler's `catch(next)` turned that into **HTTP 500**:
 *
 * ```
 * GET /robots.txt       -> 200 text/plain
 * GET //robots.txt      -> 500   (F1, fixed first)
 * GET /.//robots.txt    -> 500   (F9: the same defect, reached by normalisation)
 * GET //about           -> 302   (a redirect route, so still short-circuited: the control)
 * ```
 *
 * A 5xx on `robots.txt` tells a crawler the **whole site** is disallowed, which is the single outcome
 * this storefront's SEO design exists to prevent — and `sitemap.xml`'s "never a 500, ever" had the
 * identical hole.
 *
 * **Why the first fix was not enough, which is the lesson.** It tested the **raw** request target for a
 * leading run. But the URL parser removes dot segments first, so a path can *arrive* with one leading
 * slash and *end up* with two: `new URL('/.//robots.txt', 'http://h').pathname` is `//robots.txt`. Nine
 * shapes still answered 500 — `/.//`, `/..//x`, `/a/..//x`, `/%2e//x` and so on — and the same control
 * proved the cause, because `/.//about` and `/..//bag` kept answering 302. So the test has to be made
 * against the **effective** path, which is what {@link effectivePath} computes.
 *
 * **Backslashes are in scope and that is not pedantry.** `/\robots.txt` threw the same way, and `/\host`
 * is read as protocol-relative by some user agents, so a redirect built by stripping only forward
 * slashes could have produced an off-site `Location`. {@link canonicalTarget} always emits exactly one
 * `/` followed by a character that is neither — checked again at the end of the function, against its
 * own regular expressions, because an open redirect would be a worse defect than the one being fixed.
 *
 * **What is deliberately *not* canonicalised.** A dot segment that does not produce a leading run —
 * `/./robots.txt`, `/a/../robots.txt` — is left alone. It is safe (it answers 404, which for
 * `robots.txt` means "crawl freely", never 5xx), and redirecting it would be a crawl-canonicalisation
 * change rather than a fix: a wider rule than the defect, on a shape nothing asks for. Interior runs
 * (`/women//jeans`) are left alone for the same reason; only a run that ends up **leading** matters.
 */

/** Two or more leading slashes or backslashes, in any mixture, in an already-normalised path. */
const LEADING_SLASH_RUN = /^[\\/]{2,}/;

/** The whole leading run, so the canonical form keeps exactly one separator. */
const LEADING_SEPARATORS = /^[\\/]+/;

/** Exactly one leading separator, followed by something that is not another. The safety property. */
const SAME_ORIGIN_PATH = /^\/(?![\\/])/;

/** A segment the URL parser treats as "this directory". The percent forms are the WHATWG spec's. */
const SINGLE_DOT = new Set(['.', '%2e']);

/** A segment the URL parser treats as "the directory above". Every mixed-encoding spelling. */
const DOUBLE_DOT = new Set(['..', '.%2e', '%2e.', '%2e%2e']);

/**
 * The path a URL parser will produce from this request target: backslashes rewritten and dot segments
 * resolved, exactly as the WHATWG path state machine does it.
 *
 * **Why not `new URL(target, base).pathname`.** Because `new URL('//evil.example.com/x', base)` resolves
 * the leading `//` as an **authority** and hands back a URL on `evil.example.com` — so using the parser
 * on the raw target to decide what the parser will do would be reading the answer off the attack. The
 * path is normalised here without ever letting it be read as a host.
 *
 * The empty-segment rules are the ones that matter and are easy to get wrong: a single-dot segment is
 * dropped, *except* as the last segment, where it leaves a trailing empty one — which is why `/./`
 * resolves to `/` and `/.//x` resolves to `//x` rather than both collapsing the same way.
 */
export function effectivePath(target: string): string {
  // A special-scheme URL's path treats a backslash as a separator, so the parser has already done this
  // by the time anything downstream sees the path.
  const path = target.replace(/\\/g, '/');
  const segments = path.startsWith('/') ? path.slice(1).split('/') : path.split('/');

  const resolved: string[] = [];
  segments.forEach((segment, index) => {
    const isLast = index === segments.length - 1;
    const lower = segment.toLowerCase();

    if (DOUBLE_DOT.has(lower)) {
      resolved.pop();
      if (isLast) {
        resolved.push('');
      }

      return;
    }

    if (SINGLE_DOT.has(lower)) {
      if (isLast) {
        resolved.push('');
      }

      return;
    }

    resolved.push(segment);
  });

  return `/${resolved.join('/')}`;
}

/**
 * The single-slash form of a request target, or `null` when its effective path is already canonical.
 *
 * Takes the raw target — path and query together — because the query has to survive the redirect:
 * `//women?colour=navy` is a filtered listing, not the department.
 */
export function canonicalTarget(url: string): string | null {
  const split = url.search(/[?#]/);
  const rawPath = split === -1 ? url : url.slice(0, split);
  const rest = split === -1 ? '' : url.slice(split);

  const path = effectivePath(rawPath);
  if (!LEADING_SLASH_RUN.test(path)) {
    return null;
  }

  const canonical = `/${path.replace(LEADING_SEPARATORS, '')}${rest}`;

  // Checked rather than trusted. An open redirect would be a worse defect than the 500 this exists to
  // fix, and the cost of being sure is one regular expression on a request answered a few times an hour.
  if (canonical !== '/' && !SAME_ORIGIN_PATH.test(canonical)) {
    throw new Error(`refusing to redirect to a target that may leave this origin: ${canonical}`);
  }

  return canonical;
}

/**
 * Which redirect to answer with.
 *
 * 301 for a GET or a HEAD: it is cacheable, and it is the answer that tells a crawler `/women` is the
 * address rather than leaving two of them indexed. 308 for anything else, because a 301 permits a
 * client to re-issue the request as a GET and silently dropping a request body is a worse bug than the
 * one being fixed — nothing posts to this server today, and this is here so that staying true is not a
 * thing anyone has to remember.
 */
export function redirectStatusFor(method: string): 301 | 308 {
  return method === 'GET' || method === 'HEAD' ? 301 : 308;
}
