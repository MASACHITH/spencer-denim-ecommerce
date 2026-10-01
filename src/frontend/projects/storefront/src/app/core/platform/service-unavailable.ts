/**
 * How long a crawler is asked to wait before trying a URL the storefront could not serve.
 *
 * Nothing in the storefront caches a failure: `NavigationService` re-asks `GET
 * /api/catalog/departments` on the very next render, `ProductDetailStore` re-asks for the product,
 * and each attempt is bounded by its own timeout. So this figure cannot be read off a backoff or a
 * TTL — there is none — and it is not meant to say "we will be ready then". It says how long it is
 * worth *not* asking: long enough for an API container to finish restarting or a loaded VPS to stop
 * shedding 2-second timeouts, and short enough that a URL is not left alone for a working day.
 *
 * Two minutes, and one figure for the whole site: a crawler that meets two different `Retry-After`
 * values on two pages of the same outage learns nothing from either.
 */
export const RETRY_AFTER_SECONDS = 120;

/**
 * Answers **503 with `Retry-After`** for the render in progress: the page may well exist, and we
 * could not find out.
 *
 * A crawler retries a 503 and keeps the URL where it drops a 404, which is the whole difference
 * between an outage and a deletion (#30). It deliberately does **not** set `robots: noindex`, and a
 * caller must not either — a 503 is not a page, so asking for it to be deindexed is asking for the
 * one outcome this exists to prevent, on any crawler that reads the body of an error.
 *
 * `response` is Angular's `RESPONSE_INIT`, which the server hands to the render and builds the
 * `Response` from afterwards, so setting the status here is what reaches the wire. It is absent in
 * the browser — a client-side navigation has no response to set a status on — and this is then a
 * no-op by design: what the shopper sees is the component's job, and the status is nobody's.
 */
export function serviceUnavailable(response: ResponseInit | null): void {
  if (!response) {
    return;
  }

  response.status = 503;
  // `headers` may already be a Headers, an array of pairs or a plain object; the constructor
  // normalises all three, so nothing already set is dropped by adding one more.
  const headers = new Headers(response.headers ?? {});
  headers.set('Retry-After', String(RETRY_AFTER_SECONDS));
  response.headers = headers;
}
