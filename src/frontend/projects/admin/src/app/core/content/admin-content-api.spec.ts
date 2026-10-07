import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { AdminContentApi } from './admin-content-api';
import { AdminAuthApi } from '../auth/admin-auth-api';

/**
 * The admin content client (ADM-17, GLB-10).
 *
 * **What this file is really about is the two meanings of a missing field.** `PUT` with an omitted field
 * means "leave it alone" and with `null` means "clear it", and collapsing them is how W1-12 wiped every
 * category image. `JSON.stringify` is what makes the distinction travel — it drops an `undefined` and
 * keeps a `null` — so the assertions below are on the **bytes on the wire**, not on the object passed in.
 */
describe('AdminContentApi (ADM-17, GLB-10)', () => {
  let api: AdminContentApi;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });

    api = TestBed.inject(AdminContentApi);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  /** Answers the antiforgery fetch every mutating call makes first (doc 03 §3a). */
  function grantToken(): void {
    http.expectOne(AdminAuthApi.antiforgeryUrl).flush({ token: 'xsrf-token' });
  }

  it('reads the pages with no token, because a read is not a write', () => {
    api.pages().subscribe();

    const request = http.expectOne(`${AdminContentApi.pagesUrl}/`);
    expect(request.request.method).toBe('GET');
    expect(request.request.headers.has('X-XSRF-TOKEN')).toBe(false);

    request.flush([]);
  });

  /**
   * **A field left `undefined` does not reach the wire at all.**
   *
   * This is the assertion that matters in this file. A caller sending only the body must not clear the
   * search-engine title and description the client wrote last month, and the only thing that makes that
   * true is `JSON.stringify` dropping the key.
   */
  it('omits a field the caller did not set, and keeps one set to null', () => {
    api.updatePage(7, { bodyHtml: '<p>New.</p>', seoTitle: null }, 'row-version').subscribe();

    grantToken();

    const request = http.expectOne(`${AdminContentApi.pagesUrl}/7`);
    const sent = JSON.stringify(request.request.body);

    expect(sent).toContain('"bodyHtml"');

    // Explicitly cleared.
    expect(sent).toContain('"seoTitle":null');

    // Not mentioned, so the API leaves them exactly as they are.
    expect(sent).not.toContain('"title"');
    expect(sent).not.toContain('"slug"');
    expect(sent).not.toContain('"seoDescription"');

    request.flush({ id: 7, removedFromBody: [] });
  });

  it('carries the row version with an edit, and a fresh antiforgery token', () => {
    api.updatePage(7, { title: 'About us' }, 'the-token').subscribe();

    grantToken();

    const request = http.expectOne(`${AdminContentApi.pagesUrl}/7`);
    expect(request.request.method).toBe('PUT');
    expect(request.request.headers.get('X-XSRF-TOKEN')).toBe('xsrf-token');
    expect(request.request.body.rowVersion).toBe('the-token');

    request.flush({ id: 7, removedFromBody: [] });
  });

  /**
   * Publishing is its own call with its own route, so it has its own audit row and cannot be a side
   * effect of saving a form whose checkbox was stale.
   */
  it('publishes through its own route rather than a field on the edit', () => {
    api.setPublished(7, true, 'the-token').subscribe();

    grantToken();

    const request = http.expectOne(`${AdminContentApi.pagesUrl}/7/published`);
    expect(request.request.method).toBe('PUT');
    expect(request.request.body).toEqual({ isPublished: true, rowVersion: 'the-token' });

    request.flush(null);
  });

  /** The row version travels in `If-Match`, because a DELETE has no body to put it in. */
  it('sends the row version as If-Match on a delete', () => {
    api.deletePage(7, 'the-token').subscribe();

    grantToken();

    const request = http.expectOne(`${AdminContentApi.pagesUrl}/7`);
    expect(request.request.method).toBe('DELETE');
    expect(request.request.headers.get('If-Match')).toBe('the-token');

    request.flush(null);
  });

  it('asks for a page of the inbox with the status, the skip and the take', () => {
    api.enquiries('Spam', 25, 25).subscribe();

    const request = http.expectOne(
      (candidate) => candidate.url.startsWith(`${AdminContentApi.enquiriesUrl}/`),
    );

    expect(request.request.urlWithParams).toContain('status=Spam');
    expect(request.request.urlWithParams).toContain('skip=25');
    expect(request.request.urlWithParams).toContain('take=25');

    request.flush({ items: [], total: 0, newCount: 0 });
  });

  /** No status at all means every status, so the parameter is absent rather than empty. */
  it('leaves the status out when the filter is off', () => {
    api.enquiries(null, 0, 25).subscribe();

    const request = http.expectOne(
      (candidate) => candidate.url.startsWith(`${AdminContentApi.enquiriesUrl}/`),
    );

    expect(request.request.urlWithParams).not.toContain('status=');

    request.flush({ items: [], total: 0, newCount: 0 });
  });

  /**
   * Doc 11 P15 step 7 makes reading the enquiry what moves it to Read — but as a **POST**, because a
   * GET that changes a row is a row a link checker, a prefetch or a browser's back button can change.
   */
  it('marks an enquiry read with a POST rather than riding on the GET', () => {
    api.markRead(42).subscribe();

    grantToken();

    const request = http.expectOne(`${AdminContentApi.enquiriesUrl}/42/read`);
    expect(request.request.method).toBe('POST');

    request.flush(null);
  });

  /**
   * The deletion carries the reason doc 08 §3.7 requires the audit row to hold, as a POST because the
   * reason travels in a body and a DELETE with a body is not reliably forwarded by proxies.
   */
  it('deletes an enquiry with its reason, through a POST', () => {
    api.deleteEnquiry(42, 'Contains card details').subscribe();

    grantToken();

    const request = http.expectOne(`${AdminContentApi.enquiriesUrl}/42/delete`);
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({ reason: 'Contains card details' });

    request.flush(null);
  });

  it('creates a custom page and never names a system key', () => {
    api.createPage({ title: 'Size guide', slug: 'size-guide' }).subscribe();

    grantToken();

    const request = http.expectOne(`${AdminContentApi.pagesUrl}/`);
    expect(request.request.method).toBe('POST');

    // There is no `systemKey` in `ContentPageValues` at all, which is the point: a create that could
    // name one could make a second `privacy`, or claim the protection against deletion.
    expect(JSON.stringify(request.request.body)).not.toContain('systemKey');

    request.flush({ id: 9, removedFromBody: [] });
  });
});
