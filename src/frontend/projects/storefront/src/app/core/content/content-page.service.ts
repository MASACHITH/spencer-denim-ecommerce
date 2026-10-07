import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, Signal, computed, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { Observable, catchError, map, of, shareReplay, tap, timeout } from 'rxjs';

import {
  ContentPageDto,
  ContentPageIndex,
  ContentPageLink,
  ContentPageLinksDto,
  UNKNOWN_CONTENT_PAGES,
} from './content-page.model';
import { ServerHandover } from '../platform/server-handover';
import { ValidationProblemDetails } from '../catalog/catalog.model';

/**
 * How long the published-page index waits before it gives up.
 *
 * Two seconds, matching the departments feed and the site settings, and for the same reason those
 * record: the footer reads this on every page, so a slow content feed would be a slow site — and an
 * answer that never comes is not a failure, so `catchError` alone cannot help (the W2-03 lesson).
 */
export const CONTENT_PAGES_TIMEOUT_MS = 2_000;

/** Where the server leaves the published-page index for the browser. */
const CONTENT_PAGES_HANDOVER = 'content:pages';

/**
 * Where the server leaves one page's body for the browser, keyed by slug.
 *
 * **This exists because the first version did not have it, and the cost was not a wasted request.**
 * The server render fetched the page and the browser fetched it again on hydration — which is the
 * defect `ServerHandover`'s own documentation records having found across the catalogue ("every
 * catalogue call was therefore made twice, and the second one replaced server-rendered content with a
 * loading state"). Here the second call is worse than wasteful: if it fails, `ContentPage` renders its
 * missing state, so **a transient blip turns a perfectly good server-rendered policy page into a 404 in
 * the shopper's browser**, with the real page already on screen and then taken away. Measured in
 * `content-pages.e2e.ts`, where the browser's own `/api/content/pages/about` answered 404 and the
 * heading changed from "Our story" to "We could not find that page" a moment after load.
 *
 * Keyed by slug so a client-side navigation to a second content page is not handed the first one's
 * body, and taken once, so a shopper who comes back later gets a fresh read rather than a replay.
 */
const contentPageHandover = (slug: string): string => `content:page:${slug}`;

/**
 * The published content pages (GLB-10): what the footer links to, and what the router uses to decide
 * whether a single-segment address is a page, is genuinely not a page, or cannot be decided at all.
 *
 * The index comes from `GET /api/content/pages`, which carries no page bodies — a page's body is
 * fetched only when somebody opens it, by {@link page}.
 *
 * **The `source` label is the point of this service**, and it is `NavigationService`'s lesson applied to
 * a second feed. A slug missing from a **live** index is genuinely not a page, so 404. A slug missing
 * from an index that was never read may be a published policy page, so 503 with `Retry-After`. Reading
 * the two as one answer is how a route guard comes to say "that page is gone" about a page it had simply
 * never been told about.
 */
@Injectable({ providedIn: 'root' })
export class ContentPageService {
  private readonly http = inject(HttpClient);
  private readonly handover = inject(ServerHandover);

  /**
   * The index, resolved once and replayed, so the footer and the route guards share one request and
   * cannot disagree about what is published.
   *
   * The guards subscribe to this rather than reading {@link index}, because a signal cannot tell "still
   * loading" from "could not be read" — and those two must not route the same way.
   */
  readonly index$: Observable<ContentPageIndex> = this.load().pipe(
    shareReplay({ bufferSize: 1, refCount: false }),
  );

  /** The index in hand and where it came from. "Unknown" until the first answer arrives. */
  readonly index: Signal<ContentPageIndex> = toSignal(this.index$, {
    initialValue: UNKNOWN_CONTENT_PAGES,
  });

  /**
   * The published pages, for the footer. Empty while the feed has not answered, which renders as no
   * policy links rather than as six links to 404s.
   */
  readonly pages: Signal<readonly ContentPageLink[]> = computed(() => this.index().pages);

  /**
   * One published page by slug. A 404 travels to the caller, which turns it into the missing state.
   *
   * The server's read is handed to the browser with the page (see {@link contentPageHandover}), so
   * hydration does not ask again — and cannot replace a rendered page with a 404 because the second
   * ask failed. Only a **successful** read is handed over: a failure hands over nothing, so the browser
   * retries it, which is the arrangement `NavigationService` arrived at after the same mistake.
   */
  page(slug: string): Observable<ContentPageDto> {
    const key = contentPageHandover(slug);
    const handedOver = this.handover.take<ContentPageDto>(key);

    if (handedOver && typeof handedOver.slug === 'string' && typeof handedOver.bodyHtml === 'string') {
      return of(handedOver);
    }

    return this.http
      .get<ContentPageDto>(`/api/content/pages/${encodeURIComponent(slug)}`)
      .pipe(tap((page) => this.handover.give(key, page)));
  }

  /** The published page with this system key, or null — so nothing matches on an editable slug. */
  bySystemKey(systemKey: string): ContentPageLink | null {
    return this.pages().find((page) => page.systemKey === systemKey) ?? null;
  }

  private load(): Observable<ContentPageIndex> {
    const handedOver = this.handover.take<ContentPageIndex>(CONTENT_PAGES_HANDOVER);
    if (handedOver) {
      // Vetted again rather than trusted: the handover crosses a process boundary, and this value
      // decides whether an address answers 404.
      return of(toIndex(handedOver.pages));
    }

    return this.http.get<ContentPageLinksDto>('/api/content/pages').pipe(
      timeout(CONTENT_PAGES_TIMEOUT_MS),
      map((payload) => toIndex(payload?.pages)),

      // **Only a live index is handed over, and the operator order is load-bearing.** `tap` above
      // `timeout` would hand over whatever arrived before anything vetted it, and the browser would then
      // find a non-null value in `TransferState` and never ask again — stranding the session on an index
      // that was never read. That is `NavigationService`'s own measured failure, one feed over.
      tap((index) => {
        if (index.source === 'live') {
          this.handover.give(CONTENT_PAGES_HANDOVER, index);
        }
      }),
      catchError(() => of(UNKNOWN_CONTENT_PAGES)),
    );
  }
}

/**
 * Pairs an answer with where it came from, without ever calling an unread index live.
 *
 * **An empty array from a successful call is `live`, and that distinction is the whole file.** A shop
 * whose client has published nothing is the normal state of a fresh deployment, and it is a *known*
 * state: `/returns` really is not a page yet, so 404 is the honest answer. What must not be called live
 * is an answer that never arrived or that was not the shape we asked for — `200 {"oops":true}` is a
 * successful response, and `catchError` never sees it (the lesson `toTree` and `toMessages` both record).
 */
export function toIndex(pages: unknown): ContentPageIndex {
  if (!Array.isArray(pages)) {
    return UNKNOWN_CONTENT_PAGES;
  }

  const links: ContentPageLink[] = [];
  const seen = new Set<string>();

  for (const row of pages) {
    const slug = typeof row?.slug === 'string' ? row.slug.trim().toLowerCase() : '';
    const title = typeof row?.title === 'string' ? row.title.trim() : '';

    // A slug that is not a slug is dropped rather than put in an href: `CK_ContentPages_Slug` means no
    // row can hold one, so this is the same belt-and-braces the sitemap applies — and a bad slug in the
    // footer is a link on every page of the shop.
    if (!/^[a-z0-9]+(?:-[a-z0-9]+)*$/.test(slug) || title.length === 0 || seen.has(slug)) {
      continue;
    }

    seen.add(slug);
    links.push({
      slug,
      title,
      systemKey: typeof row?.systemKey === 'string' && row.systemKey.length > 0 ? row.systemKey : null,
    });
  }

  return { pages: links, source: 'live' };
}

/**
 * Turns a failed enquiry submission into something a visitor can act on (GLB-10).
 *
 * A 400 carries an RFC 9457 problem keyed by field, and the API's messages are written for a shopper, so
 * they are shown as written. A 429 is the rate limiter, which is a different thing to say: wait, do not
 * retype. The shape and the reasoning are `newsletterErrorMessage`'s; the strings differ because what the
 * visitor has just lost differs — a mistyped email costs nothing, and a four-paragraph enquiry costs the
 * whole message if the form clears itself.
 */
export function contactErrorMessage(error: unknown): string {
  if (!(error instanceof HttpErrorResponse)) {
    return CONTACT_GENERIC_ERROR;
  }

  if (error.status === 400) {
    return firstValidationMessage(error.error) ?? 'Check the highlighted fields and try again.';
  }

  if (error.status === 429) {
    return 'That is a few messages in a short time. Please wait a minute and try again, or message us on WhatsApp.';
  }

  if (error.status === 0) {
    return 'We could not reach the store. Your message is still here — check your connection and try again.';
  }

  return CONTACT_GENERIC_ERROR;
}

/**
 * The generic failure.
 *
 * It says the message is still in the form, which is true — the component does not clear the fields on a
 * failure — because the one thing a visitor fears on a long form is having to retype it.
 */
const CONTACT_GENERIC_ERROR =
  'We could not send your message just now. It is still here, so please try again in a moment.';

/** The per-field messages a 400 carries, so the form can mark each control. */
export function contactFieldErrors(error: unknown): Readonly<Record<string, string>> {
  if (!(error instanceof HttpErrorResponse) || error.status !== 400) {
    return {};
  }

  const body = error.error as ValidationProblemDetails | null;
  if (typeof body !== 'object' || body === null || !body.errors) {
    return {};
  }

  const fields: Record<string, string> = {};

  for (const [field, messages] of Object.entries(body.errors)) {
    const message = messages?.find((candidate) => typeof candidate === 'string' && candidate.trim());
    if (message) {
      fields[field] = message;
    }
  }

  return fields;
}

function firstValidationMessage(body: unknown): string | null {
  if (typeof body !== 'object' || body === null) {
    return null;
  }

  const errors = (body as ValidationProblemDetails).errors;
  if (!errors) {
    return null;
  }

  for (const messages of Object.values(errors)) {
    const message = messages?.find((candidate) => typeof candidate === 'string' && candidate.trim());
    if (message) {
      return message;
    }
  }

  return null;
}
