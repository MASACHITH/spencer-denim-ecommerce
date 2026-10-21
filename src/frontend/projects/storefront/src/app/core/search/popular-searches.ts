import { DOCUMENT, isPlatformBrowser } from '@angular/common';
import { HttpClient } from '@angular/common/http';
import { Injectable, PLATFORM_ID, Signal, inject, signal } from '@angular/core';
import { catchError, of } from 'rxjs';

import { isPrivate } from './recent-searches.service';
import { isSearchable, normaliseTerm } from './search-term';

/** The API route of the public list (issue #78). */
export const POPULAR_SEARCHES_ROUTE = '/api/search/popular';

/** The API route of the submitted-search beacon (issue #78, doc 08 DB25). */
export const SEARCH_SUBMISSIONS_ROUTE = '/api/search/submissions';

/** Doc 08 §7.6: "TOP 8". The API's own cap, repeated so a misbehaving response cannot fill the overlay. */
export const POPULAR_SEARCHES_LIMIT = 8;

/** `PopularSearchRules.MaxShownLength`: the API shows nothing longer, and neither does this. */
export const POPULAR_TERM_MAX_LENGTH = 40;

/**
 * What other shoppers search for most (SRCH-01 "popular searches", issue #78), from
 * `GET /api/search/popular` — which reads `analytics.SearchQueriesDaily` (doc 08 §1.7, §7.6).
 *
 * **Fetched once per page load, in the browser, and only when it is wanted** — the first time an overlay
 * opens on an empty box, or the empty search page asks. Never on the server: the list is not part of any
 * page's HTML, and an outstanding request would hold the render "unstable" for something nobody can see
 * there. A failure is an empty list, which the overlay already knows how to draw ("Browse the shop").
 *
 * **Vetted on the way in** (lesson L1): the API applies the privacy rule, the threshold, the length cap and
 * the staff hide list before a term is ever sent, and this applies the shape checks and the storefront's own
 * privacy rule again — a response is data from another process, not a promise.
 */
@Injectable({ providedIn: 'root' })
export class PopularSearches {
  private readonly http = inject(HttpClient);
  private readonly isBrowser = isPlatformBrowser(inject(PLATFORM_ID));

  private readonly list = signal<readonly string[]>([]);
  private requested = false;

  /** The terms, most searched first. Empty on the server, before {@link load} answers, and in a new shop. */
  readonly terms: Signal<readonly string[]> = this.list.asReadonly();

  /** Asks for the list if it has not been asked for in this page load. Safe to call on every keystroke. */
  load(): void {
    if (!this.isBrowser || this.requested) {
      return;
    }

    this.requested = true;
    this.http
      .get<unknown>(POPULAR_SEARCHES_ROUTE)
      .pipe(catchError(() => of(null)))
      .subscribe((response) => this.list.set(vetPopular(response)));
  }
}

/** The usable terms in a response, in order, or none. Pure, for the unit test. */
export function vetPopular(response: unknown): readonly string[] {
  const terms = (response as { terms?: unknown } | null)?.terms;
  if (!Array.isArray(terms)) {
    return [];
  }

  const kept: string[] = [];
  for (const candidate of terms) {
    if (typeof candidate !== 'string' || candidate.length > POPULAR_TERM_MAX_LENGTH * 2) {
      continue;
    }

    const term = normaliseTerm(candidate);
    if (
      term.length > POPULAR_TERM_MAX_LENGTH ||
      !isSearchable(term) ||
      isPrivate(term) ||
      kept.some((entry) => entry.toLowerCase() === term.toLowerCase())
    ) {
      continue;
    }

    kept.push(term);
    if (kept.length === POPULAR_SEARCHES_LIMIT) {
      break;
    }
  }

  return kept;
}

/**
 * Tells the API a search was submitted, for the popular list (issue #78; doc 08 DB25: "Records submitted
 * searches only (results-page requests), never suggestion keystrokes").
 *
 * **Called by the results page, once for each term it shows** — never by the box as the shopper types, and
 * not again when a filter, the sort or "Load More" changes the page under the same term. Fire and forget: the
 * API answers 204 whatever it did, nothing waits for it and a failure is swallowed, so counting can never
 * break a search.
 *
 * **Nothing personal is sent at all.** A term the storefront would not remember (an at-sign, seven digits) is
 * never put on the wire, so the server's identical rule is a second line rather than the only one. The body
 * carries the term and `navigator.webdriver` — how the API, like the traffic recorder, tells a browser driven
 * by Playwright or Selenium from a person — and nothing else: no session, no user, no address.
 */
@Injectable({ providedIn: 'root' })
export class SearchSubmissions {
  private readonly http = inject(HttpClient);
  private readonly document = inject(DOCUMENT);
  private readonly isBrowser = isPlatformBrowser(inject(PLATFORM_ID));

  record(raw: string): void {
    if (!this.isBrowser) {
      return;
    }

    const term = normaliseTerm(raw);
    if (!isSearchable(term) || isPrivate(term)) {
      return;
    }

    this.http
      .post(SEARCH_SUBMISSIONS_ROUTE, {
        term,
        automated: this.document.defaultView?.navigator.webdriver === true,
      })
      .subscribe({ error: () => undefined });
  }
}
