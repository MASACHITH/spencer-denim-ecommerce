import { HttpTestingController } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { ContentPageLinkDto } from './content-page.model';
import { ContentPageService } from './content-page.service';

/**
 * Answers `GET /api/content/pages` in a spec (GLB-10).
 *
 * **Why this exists as a shared helper rather than a copy per spec.** The footer reads the
 * published-page index on every page of the site, so every spec that renders the shell — and every
 * spec that navigates, because the route guard reads the same index — has an outstanding request to
 * answer before `HttpTestingController.verify()`. That is six specs today and every future one, and
 * six private copies of the same two lines is how one of them comes to flush a shape the API does not
 * send.
 *
 * The default is an **empty live index**, which is the state of a fresh shop: the client has published
 * nothing, so the footer draws no policy links and every single-segment address is genuinely not a
 * page. A spec that cares about a published page passes it in.
 *
 * It is `match` rather than `expectOne`, deliberately: the index is shared and replayed, so a spec that
 * renders the shell twice makes one request and a spec that does not render it at all makes none.
 * `expectOne` would fail the second case, which is most of them.
 */
/**
 * Issues the published-page request **before** anything navigates, and answers it (GLB-10).
 *
 * **This is the deterministic form and the one a navigation needs**, mirroring what
 * `listing-page.spec.ts` does with the departments feed and for the identical reason: the content
 * guard cannot settle until the index has answered, and `RouterTestingHarness.create` does not resolve
 * until the guards have — so flushing after `create` is a race the spec loses. Measured: without this,
 * `/privacy` sat for the full `CONTENT_PAGES_TIMEOUT_MS` and then matched the **503** route, because an
 * index that never answered is exactly what "unconfirmed" means.
 *
 * Injecting the service is what issues the request: `index` is a `toSignal` over `index$` in a field
 * initialiser, so construction subscribes. The same trick `primeDepartments` uses on
 * `NavigationService`.
 */
export function primeContentPages(
  http: HttpTestingController,
  pages: readonly Partial<ContentPageLinkDto>[] = [],
): void {
  TestBed.inject(ContentPageService);
  flushContentPages(http, pages);
}

/** As {@link primeContentPages}, but the feed fails, so the index is "unknown" (#30). */
export function primeFailedContentPages(http: HttpTestingController): void {
  TestBed.inject(ContentPageService);
  failContentPages(http);
}

export function flushContentPages(
  http: HttpTestingController,
  pages: readonly Partial<ContentPageLinkDto>[] = [],
): void {
  const body = {
    pages: pages.map((page) => ({
      slug: page.slug ?? 'about',
      title: page.title ?? 'About us',
      systemKey: page.systemKey ?? null,
      updatedAtUtc: page.updatedAtUtc ?? null,
    })),
  };

  for (const request of http.match((request) => request.url === '/api/content/pages')) {
    // **Cancelled requests are skipped, and that is not defensiveness for its own sake.** The guard
    // reads the index through `take(1)`, so a navigation that settles — because a route declared above
    // `:slug` matched — unsubscribes and the in-flight request is cancelled. `HttpTestingController`
    // still returns it from `match`, and flushing a cancelled request throws. Measured in
    // `listing-page.spec.ts`, where `/not-a-department` cancelled it on its way to the catch-all.
    if (!request.cancelled) {
      request.flush(body);
    }
  }
}

/**
 * Fails `GET /api/content/pages`, so the index is "unknown" and a single-segment address answers 503
 * rather than 404 (#30).
 *
 * A 500 rather than a timeout, because a spec cannot wait out `CONTENT_PAGES_TIMEOUT_MS` without fake
 * timers and the service treats the two identically — `catchError` and `timeout` both end at
 * `UNKNOWN_CONTENT_PAGES`, which is the property that matters.
 */
export function failContentPages(http: HttpTestingController): void {
  for (const request of http.match((request) => request.url === '/api/content/pages')) {
    if (!request.cancelled) {
      request.flush({ error: 'nope' }, { status: 500, statusText: 'Server Error' });
    }
  }
}
