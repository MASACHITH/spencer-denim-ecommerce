import { DOCUMENT, isPlatformBrowser } from '@angular/common';
import { HttpClient } from '@angular/common/http';
import { DestroyRef, Injectable, PLATFORM_ID, TransferState, inject } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRouteSnapshot, NavigationEnd, Router } from '@angular/router';
import { filter } from 'rxjs';

import { SERVER_PAGE_VIEW } from './traffic-render-context';

/** The API route the beacon posts to. */
export const PAGE_VIEW_ROUTE = '/api/traffic/page-views';

/**
 * Route data that marks a page as not a page view: the 404 and 503 pages. A shopper who lands on one has
 * not viewed a page of the shop, and the server does not record those either (it records 200s only).
 */
export const NOT_A_PAGE_VIEW = { pageView: false } as const;

/** The beacon's body: the API's `PageViewBeacon`. */
export interface PageViewBeacon {
  readonly path: string;
  readonly referrer: string | null;
  readonly automated: boolean;
}

/**
 * The browser half of traffic recording (W4-03, ADM-06, ADM-20). `src/server/page-view.ts` has the whole
 * account; in short:
 *
 * - **The first navigation** is recorded here only when the server did not record it — the page carries no
 *   {@link SERVER_PAGE_VIEW} flag (the engine served the client-side shell, or the request was a prefetch).
 * - **Every later navigation** is a page the server never saw, and is recorded here — but only when the
 *   *path* changed. Choosing a filter, a sort or "Load more" rewrites the query string of the same listing,
 *   and that is the shopper using one page, not viewing a new one.
 * - **A 404 or 503 page** ({@link NOT_A_PAGE_VIEW}) is never recorded.
 *
 * The beacon is fire-and-forget: nothing waits for it and a failure is swallowed, so recording can never
 * break a page. Only another site's referrer is sent, and only for a first page; the API reduces the
 * address to what it may store. `automated` passes on `navigator.webdriver`, which is how a browser driven
 * by Playwright, Selenium or Puppeteer is counted as a bot.
 *
 * A page being prerendered by the browser (speculation rules) is recorded when it is shown, not before:
 * a prerender that is never shown is not a view.
 */
@Injectable({ providedIn: 'root' })
export class PageViewTracker {
  private readonly router = inject(Router);
  private readonly http = inject(HttpClient);
  private readonly transferState = inject(TransferState);
  private readonly document = inject(DOCUMENT);
  private readonly destroyRef = inject(DestroyRef);
  private readonly isBrowser = isPlatformBrowser(inject(PLATFORM_ID));

  private started = false;
  private seenFirst = false;
  private serverRecordedFirst = false;
  private lastPath: string | null = null;

  /** Subscribes to navigation. Called once, from an app initializer; does nothing on the server. */
  start(): void {
    if (!this.isBrowser || this.started) {
      return;
    }

    this.started = true;
    this.serverRecordedFirst = this.transferState.get(SERVER_PAGE_VIEW, false);
    this.transferState.remove(SERVER_PAGE_VIEW);

    this.router.events
      .pipe(
        filter((event): event is NavigationEnd => event instanceof NavigationEnd),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((event) => this.onNavigated(event.urlAfterRedirects));
  }

  private onNavigated(url: string): void {
    const path = pathOf(url);
    const first = !this.seenFirst;
    this.seenFirst = true;

    if (!first && path === this.lastPath) {
      return;
    }

    this.lastPath = path;

    if (first && this.serverRecordedFirst) {
      return;
    }

    if (!isPageView(this.router.routerState.snapshot.root)) {
      return;
    }

    this.whenShown(() =>
      this.send({
        path: url,
        referrer: first ? this.externalReferrer() : null,
        automated: this.document.defaultView?.navigator.webdriver === true,
      }),
    );
  }

  private send(beacon: PageViewBeacon): void {
    this.http.post(PAGE_VIEW_ROUTE, beacon).subscribe({ error: () => undefined });
  }

  /** `document.referrer` when it is another site's; a referrer on this origin is a click inside the shop. */
  private externalReferrer(): string | null {
    const referrer = this.document.referrer;
    if (!referrer) {
      return null;
    }

    try {
      return new URL(referrer).origin === this.document.location.origin ? null : referrer;
    } catch {
      return null;
    }
  }

  /** Runs now, or when a page the browser is prerendering is actually shown. */
  private whenShown(record: () => void): void {
    const document = this.document as Document & { prerendering?: boolean };
    if (document.prerendering === true) {
      document.addEventListener('prerenderingchange', () => record(), { once: true });
      return;
    }

    record();
  }
}

/** The path of a router URL: no query string, no fragment. */
export function pathOf(url: string): string {
  const end = url.search(/[?#]/);
  return end < 0 ? url : url.slice(0, end);
}

/** False when the page that is showing is marked {@link NOT_A_PAGE_VIEW}. */
export function isPageView(root: ActivatedRouteSnapshot): boolean {
  let route: ActivatedRouteSnapshot | null = root;
  while (route) {
    if (route.data?.['pageView'] === false) {
      return false;
    }

    route = route.firstChild;
  }

  return true;
}
