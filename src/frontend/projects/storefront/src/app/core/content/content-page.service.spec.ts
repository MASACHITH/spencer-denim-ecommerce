import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { UrlSegment } from '@angular/router';

import { UNKNOWN_CONTENT_PAGES } from './content-page.model';
import { contentPageVerdict } from './content-page.guards';
import {
  ContentPageService,
  contactErrorMessage,
  contactFieldErrors,
  toIndex,
} from './content-page.service';
import { HttpErrorResponse } from '@angular/common/http';

/**
 * The published-page index (GLB-10) and the two failure classifiers beside it.
 *
 * **The question this file exists for is #30's**: a slug missing from a *live* index is a 404, and a slug
 * missing from an index that was never read is a 503. Collapsing the two is how a route guard comes to
 * say "that page is gone" about a page it had simply never been told about, and the only thing standing
 * between those two answers is `toIndex`'s `source` label — so that is what is pinned here, hard.
 */
describe('toIndex (GLB-10, #30)', () => {
  /**
   * An empty array from a successful call is **live**, and the distinction is the whole file.
   *
   * A shop whose client has published nothing is the normal state of a fresh deployment, and it is a
   * *known* state: `/returns` really is not a page yet, so a 404 is the honest answer. Reading it as
   * "unknown" would make every mistyped address answer 503 for the life of every new shop.
   */
  it('reads an empty list from a successful call as live, not as unknown', () => {
    const index = toIndex([]);

    expect(index.source).toBe('live');
    expect(index.pages).toEqual([]);
  });

  /**
   * And anything that is not a list is **unknown**.
   *
   * `200 {"oops":true}` is a successful response, so `catchError` never sees it — the lesson `toTree`
   * and `toMessages` both record. A body of the wrong shape must not be read as "nothing is published".
   */
  it.each([null, undefined, {}, 'pages', 42, { pages: 'nope' }])(
    'reads %p as unknown rather than as an empty shop',
    (payload) => {
      expect(toIndex(payload).source).toBe('unknown');
    },
  );

  it('keeps a well-formed page, lower-casing its slug', () => {
    const index = toIndex([{ slug: 'Returns', title: 'Returns and exchanges', systemKey: 'returns' }]);

    expect(index.pages).toEqual([
      { slug: 'returns', title: 'Returns and exchanges', systemKey: 'returns' },
    ]);
  });

  /**
   * A slug that is not a slug is dropped rather than put in an `href`.
   *
   * `CK_ContentPages_Slug` means no row can hold one, so this is belt-and-braces — and a bad slug in the
   * footer is a broken link on every page of the shop, which is the one place belt-and-braces earns its
   * keep.
   */
  it.each([
    'Returns Policy',
    '../../etc/passwd',
    'returns/',
    '-returns',
    'returns-',
    'returns--policy',
    '',
    'javascript:alert(1)',
  ])('drops %p rather than linking it', (slug) => {
    expect(toIndex([{ slug, title: 'A page', systemKey: null }]).pages).toEqual([]);
  });

  it('drops a page with no title, which would render as an empty link', () => {
    expect(toIndex([{ slug: 'returns', title: '   ', systemKey: null }]).pages).toEqual([]);
  });

  it('keeps the first of two rows with the same slug', () => {
    const index = toIndex([
      { slug: 'returns', title: 'First', systemKey: 'returns' },
      { slug: 'returns', title: 'Second', systemKey: null },
    ]);

    expect(index.pages).toHaveLength(1);
    expect(index.pages[0].title).toBe('First');
  });

  it('reads a missing systemKey as null rather than as an empty string', () => {
    expect(toIndex([{ slug: 'size-guide', title: 'Size guide' }]).pages[0].systemKey).toBeNull();
  });
});

describe('the content-page route verdict (GLB-10, #30)', () => {
  function segments(...paths: string[]): UrlSegment[] {
    return paths.map((path) => new UrlSegment(path, {}));
  }

  const live = toIndex([{ slug: 'returns', title: 'Returns', systemKey: 'returns' }]);

  it('is known for a slug the live index holds', () => {
    expect(contentPageVerdict(live, segments('returns'))).toBe('known');
  });

  /** Case-insensitively, so /Returns finds the page rather than 404ing on a capital letter. */
  it('is known whatever case the address was typed in', () => {
    expect(contentPageVerdict(live, segments('RETURNS'))).toBe('known');
  });

  /**
   * The live index has settled the question, so a slug it does not hold is genuinely not a page. This is
   * the answer a 404 is built on.
   */
  it('is missing for a slug the live index does not hold', () => {
    expect(contentPageVerdict(live, segments('not-a-page'))).toBe('missing');
  });

  /**
   * And an index that was never read has not been asked. **This is the one that must not be a 404**: the
   * address may be a published policy page, and a 404 with `noindex` would drop it out of the index for
   * the length of an API wobble.
   */
  it('is unconfirmed when the index could not be read', () => {
    expect(contentPageVerdict(UNKNOWN_CONTENT_PAGES, segments('returns'))).toBe('unconfirmed');
  });

  /**
   * **Only a single segment, and the bound is the safety property.** `isUnconfirmedCatalogue` records
   * having measured the alternative: a 503 route with a loose matcher answered 503 for every address on
   * the origin during an outage, `/api/...` and `/.well-known/...` included.
   */
  it.each([
    [[] as string[]],
    [['women', 'jeans']],
    [['api', 'catalog', 'products']],
    [['.well-known', 'security.txt']],
  ])('is missing rather than unconfirmed for %p, whatever the index says', (paths) => {
    expect(contentPageVerdict(UNKNOWN_CONTENT_PAGES, segments(...paths))).toBe('missing');
  });

  it('is missing for a blank segment', () => {
    expect(contentPageVerdict(UNKNOWN_CONTENT_PAGES, segments('   '))).toBe('missing');
  });
});

describe('ContentPageService (GLB-10)', () => {
  let service: ContentPageService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });

    service = TestBed.inject(ContentPageService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('reads the index from /api/content/pages, once, however many readers there are', () => {
    service.index$.subscribe();
    service.index$.subscribe();

    http
      .expectOne('/api/content/pages')
      .flush({ pages: [{ slug: 'returns', title: 'Returns', systemKey: 'returns' }] });

    expect(service.pages()).toHaveLength(1);
  });

  it('answers unknown when the feed fails, rather than letting the failure escape', () => {
    let seen = UNKNOWN_CONTENT_PAGES;
    service.index$.subscribe((index) => (seen = index));

    http.expectOne('/api/content/pages').error(new ProgressEvent('error'), { status: 500 });

    expect(seen.source).toBe('unknown');
    expect(seen.pages).toEqual([]);
  });

  /**
   * The slug is matched on `systemKey`, never on the slug, because the slug is editable (doc 11 P13
   * step 12) — a client who renames `contact` to `contact-us` must not lose the form on that page.
   */
  it('finds a page by its system key whatever its slug has been renamed to', () => {
    service.index$.subscribe();
    http.expectOne('/api/content/pages').flush({
      pages: [{ slug: 'reach-us', title: 'Reach us', systemKey: 'contact' }],
    });

    expect(service.bySystemKey('contact')?.slug).toBe('reach-us');
    expect(service.bySystemKey('about')).toBeNull();
  });

  it('encodes a slug on its way into the URL', () => {
    service.index$.subscribe();
    http.expectOne('/api/content/pages').flush({ pages: [] });

    service.page('a b').subscribe({ error: () => undefined });

    http.expectOne('/api/content/pages/a%20b').flush(
      { slug: 'a-b', title: 'A page', bodyHtml: '' },
      { status: 404, statusText: 'Not Found' },
    );
  });
});

describe('contactErrorMessage and contactFieldErrors (GLB-10)', () => {
  function problem(status: number, body: unknown): HttpErrorResponse {
    return new HttpErrorResponse({ status, error: body, url: '/api/content/contact' });
  }

  /**
   * The API's own sentence is shown as written. It is already written for a shopper, and replacing it
   * with an apology would lose the one thing the visitor needs — which field to fix.
   */
  it('shows the API’s own message for a refused field', () => {
    const message = contactErrorMessage(
      problem(400, { errors: { email: ['Enter an email address we can reply to.'] } }),
    );

    expect(message).toBe('Enter an email address we can reply to.');
  });

  it('keys the field errors so the form can mark each control', () => {
    const fields = contactFieldErrors(
      problem(400, { errors: { email: ['Bad email.'], message: ['Say something.'] } }),
    );

    expect(fields).toEqual({ email: 'Bad email.', message: 'Say something.' });
  });

  it('returns no field errors for anything but a 400', () => {
    expect(contactFieldErrors(problem(429, {}))).toEqual({});
    expect(contactFieldErrors(problem(500, {}))).toEqual({});
    expect(contactFieldErrors(new Error('not an http error'))).toEqual({});
  });

  /** The rate limiter is a different thing to say: wait, do not retype. */
  it('tells a rate-limited visitor to wait rather than to retype', () => {
    expect(contactErrorMessage(problem(429, {}))).toContain('wait a minute');
  });

  /**
   * **Every failure message says the enquiry is still there.** It is true — the component does not clear
   * the fields on a failure — and it is the one thing a visitor fears on a long form.
   */
  it.each([0, 500, 502])('promises the message is still there after a %i', (status) => {
    expect(contactErrorMessage(problem(status, {})).toLowerCase()).toContain('still');
  });

  it('never promises an email, because nothing sends one', () => {
    for (const status of [0, 400, 429, 500]) {
      const message = contactErrorMessage(problem(status, {})).toLowerCase();

      expect(message).not.toContain('email you');
      expect(message).not.toContain('inbox');
    }
  });
});
