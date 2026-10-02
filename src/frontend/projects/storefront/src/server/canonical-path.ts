/**
 * The one request shape that must never reach anything else: a leading run of slashes.
 *
 * **What went wrong, and why it is here rather than in a route.** `@angular/platform-server` throws
 * `NG05702 "Protocol relative URLs are not allowed in this context"` from a provider factory while the
 * injector is hydrating — so only once a component is about to render. While the catch-all route was
 * `redirectTo: ''` the throw was unreachable, because a redirect route short-circuits before any
 * component is built. Pointing the catch-all at a real page (#28) made every `//`-prefixed address the
 * route table does not otherwise claim throw instead, and the render handler's `catch(next)` turned
 * that into **HTTP 500**:
 *
 * ```
 * GET /robots.txt    -> 200 text/plain
 * GET //robots.txt   -> 500 text/html   Error: NG05702
 * GET //sitemap.xml  -> 500        GET //  //women  //not-a-dept  //healthz -> 500
 * GET //about        -> 302        (still a redirect route, so still short-circuited)
 * ```
 *
 * A 5xx on `robots.txt` tells a crawler the **whole site** is disallowed, which is the single outcome
 * this storefront's SEO design exists to prevent — and `sitemap.xml`'s "never a 500, ever" had the
 * identical hole. Both guarantees were one character away from being false.
 *
 * So the fix is not a route: a route is the thing that was unreliable. The URL is **canonicalised
 * before anything can route it** — before the health probe, before the static middleware, before
 * `robots.txt`, before the renderer — and the canonical form is redirected to rather than silently
 * rewritten, because `//women` and `/women` are two addresses for one page and a crawler should be
 * told which one is the page.
 *
 * **Backslashes are in scope and that is not pedantry.** `/\robots.txt` threw the same way, and
 * `/\host` is read as protocol-relative by some user agents, so a redirect built by stripping only
 * forward slashes could have produced an off-site `Location`. The replacement always emits exactly one
 * `/` followed by a character that is neither, so the target is same-origin by construction.
 */

/** Two or more leading slashes or backslashes, in any mixture. The condition this exists for. */
const LEADING_SLASH_RUN = /^[\\/]{2,}/;

/** The whole leading run, so the canonical form keeps exactly one separator. */
const LEADING_SEPARATORS = /^[\\/]+/;

/**
 * The single-slash form of a request target, or `null` when it is already canonical.
 *
 * Operates on the raw target — path and query together — because the query has to survive the
 * redirect: `//women?colour=navy` is a filtered listing, not the department.
 */
export function canonicalTarget(url: string): string | null {
  if (!LEADING_SLASH_RUN.test(url)) {
    return null;
  }

  return `/${url.replace(LEADING_SEPARATORS, '')}`;
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
