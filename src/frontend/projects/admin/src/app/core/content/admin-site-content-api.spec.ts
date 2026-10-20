import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { AdminAuthApi } from '../auth/admin-auth-api';
import { AdminSiteContentApi, toContentFailure } from './admin-site-content-api';
import { HomeItemRequest, HomeSectionRequest, liveLabel } from './admin-site-content.model';

/**
 * The homepage, announcement and size-guide client (#192).
 *
 * The assertions are on the wire: the URL, the verb, the antiforgery header, **every key of the body**
 * (the endpoints refuse a body missing one), and the quoted `If-Match` on a delete.
 */
describe('AdminSiteContentApi (#192)', () => {
  let api: AdminSiteContentApi;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });

    api = TestBed.inject(AdminSiteContentApi);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  function grantToken(): void {
    http.expectOne(AdminAuthApi.antiforgeryUrl).flush({ token: 'xsrf-token' });
  }

  const section: HomeSectionRequest = {
    title: 'New in',
    subtitle: null,
    isEnabled: true,
    sortOrder: 2,
    startsAt: null,
    endsAt: null,
    mode: null,
    count: 8,
    listingUrl: '/new',
    linkLabel: null,
    overlay: null,
    rowVersion: 'rv-section',
  };

  const item: HomeItemRequest = {
    title: null,
    subtitle: null,
    body: null,
    buttonLabel: null,
    linkUrl: null,
    imagePublicId: null,
    mobileImagePublicId: null,
    videoPublicId: null,
    departmentId: null,
    productSlug: null,
    iconKey: 'delivery',
    sortOrder: 0,
    isEnabled: true,
    startsAt: null,
    endsAt: null,
    rowVersion: 'rv-section',
  };

  it('reads the homepage with a plain GET and no token', () => {
    api.home().subscribe();

    const request = http.expectOne(AdminSiteContentApi.homeUrl);
    expect(request.request.method).toBe('GET');
    expect(request.request.headers.has('X-XSRF-TOKEN')).toBe(false);
    request.flush({ sections: [], departments: [], benefitIcons: [] });
  });

  it('saves a section with PUT, a token, and every key including the nulls', () => {
    api.updateSection(3, section).subscribe();
    grantToken();

    const request = http.expectOne('/api/admin/content/home/sections/3');
    expect(request.request.method).toBe('PUT');
    expect(request.request.headers.get('X-XSRF-TOKEN')).toBe('xsrf-token');

    const sent = JSON.parse(JSON.stringify(request.request.body)) as Record<string, unknown>;
    expect(Object.keys(sent).sort()).toEqual(
      [
        'title', 'subtitle', 'isEnabled', 'sortOrder', 'startsAt', 'endsAt',
        'mode', 'count', 'listingUrl', 'linkLabel', 'overlay', 'rowVersion',
      ].sort(),
    );
    expect(sent['overlay']).toBeNull();
    request.flush({ id: 3, rowVersion: 'next' });
  });

  it('creates an item under its section with POST and the section row version', () => {
    api.createItem(9, item).subscribe();
    grantToken();

    const request = http.expectOne('/api/admin/content/home/sections/9/items');
    expect(request.request.method).toBe('POST');

    const sent = JSON.parse(JSON.stringify(request.request.body)) as Record<string, unknown>;
    expect(Object.keys(sent)).toHaveLength(16);
    expect(sent['rowVersion']).toBe('rv-section');
    expect(sent['videoPublicId']).toBeNull();
    request.flush({ id: 100, rowVersion: 'next' });
  });

  it('updates an item with PUT on the items route', () => {
    api.updateItem(12, item).subscribe();
    grantToken();

    const request = http.expectOne('/api/admin/content/home/items/12');
    expect(request.request.method).toBe('PUT');
    request.flush({ id: 12, rowVersion: 'next' });
  });

  it('deletes an item with the section row version quoted in If-Match', () => {
    api.deleteItem(12, 'AAAA+/==').subscribe();
    grantToken();

    const request = http.expectOne('/api/admin/content/home/items/12');
    expect(request.request.method).toBe('DELETE');
    expect(request.request.headers.get('If-Match')).toBe('"AAAA+/=="');
    expect(request.request.headers.get('X-XSRF-TOKEN')).toBe('xsrf-token');
    request.flush(null, { status: 204, statusText: 'No Content' });
  });

  it('adds an announcement with POST and no version, and edits one with the version in the body', () => {
    const body = { message: 'Hello', linkUrl: null, sortOrder: 0, isActive: true, startsAt: null, endsAt: null };

    api.saveAnnouncement(null, body, null).subscribe();
    grantToken();
    const created = http.expectOne(AdminSiteContentApi.announcementsUrl);
    expect(created.request.method).toBe('POST');
    expect(Object.keys(created.request.body as object).sort()).toEqual(
      ['message', 'linkUrl', 'sortOrder', 'isActive', 'startsAt', 'endsAt'].sort(),
    );
    created.flush({ id: 1, version: 'v1' });

    api.saveAnnouncement(1, body, 'v1').subscribe();
    grantToken();
    const edited = http.expectOne('/api/admin/content/announcements/1');
    expect(edited.request.method).toBe('PUT');
    expect((edited.request.body as Record<string, unknown>)['version']).toBe('v1');
    expect(JSON.stringify(edited.request.body)).toContain('"linkUrl":null');
    edited.flush({ id: 1, version: 'v2' });
  });

  it('deletes an announcement with its version quoted in If-Match', () => {
    api.deleteAnnouncement(4, 'v4').subscribe();
    grantToken();

    const request = http.expectOne('/api/admin/content/announcements/4');
    expect(request.request.method).toBe('DELETE');
    expect(request.request.headers.get('If-Match')).toBe('"v4"');
    request.flush(null, { status: 204, statusText: 'No Content' });
  });

  it('reads the size guides and one guide', () => {
    api.sizeGuides().subscribe();
    http.expectOne(AdminSiteContentApi.sizeGuidesUrl).flush([]);

    api.sizeGuide(5).subscribe();
    const one = http.expectOne('/api/admin/content/size-guides/5');
    expect(one.request.method).toBe('GET');
    one.flush({});
  });

  it('creates a size guide without a row version and replaces one with it, tableJson as a string', () => {
    const body = {
      name: 'Tops',
      introHtml: null,
      tableJson: '{"columns":["Size"],"rows":[["S"]]}',
      measuringTipsHtml: null,
      imagePublicId: null,
    };

    api.saveSizeGuide(null, body, null).subscribe();
    grantToken();
    const created = http.expectOne(AdminSiteContentApi.sizeGuidesUrl);
    expect(created.request.method).toBe('POST');
    expect(Object.keys(created.request.body as object)).not.toContain('rowVersion');
    expect(typeof (created.request.body as Record<string, unknown>)['tableJson']).toBe('string');
    created.flush({ id: 7, rowVersion: 'r', removedFromIntro: [], removedFromTips: [] });

    api.saveSizeGuide(7, body, 'r').subscribe();
    grantToken();
    const edited = http.expectOne('/api/admin/content/size-guides/7');
    expect(edited.request.method).toBe('PUT');
    expect(Object.keys(edited.request.body as object).sort()).toEqual(
      ['name', 'introHtml', 'tableJson', 'measuringTipsHtml', 'imagePublicId', 'rowVersion'].sort(),
    );
    edited.flush({ id: 7, rowVersion: 'r2', removedFromIntro: [], removedFromTips: [] });
  });

  it('deletes a size guide with its row version quoted', () => {
    api.deleteSizeGuide(7, 'r2').subscribe();
    grantToken();

    const request = http.expectOne('/api/admin/content/size-guides/7');
    expect(request.request.headers.get('If-Match')).toBe('"r2"');
    request.flush(null, { status: 204, statusText: 'No Content' });
  });

  it('uploads an image with its alt text, and a video with its poster', () => {
    const file = new File(['x'], 'hero.webp', { type: 'image/webp' });
    const poster = new File(['p'], 'poster.webp', { type: 'image/webp' });

    api.uploadImage(file, 'A model in linen').subscribe();
    grantToken();
    const image = http.expectOne(AdminSiteContentApi.imagesUrl);
    const form = image.request.body as FormData;
    expect(form.get('altText')).toBe('A model in linen');
    expect(form.get('file')).toBeInstanceOf(File);
    image.flush({ publicId: 'p1', url: '/x' });

    api.uploadVideo(new File(['v'], 'clip.mp4', { type: 'video/mp4' }), poster, 'Clip').subscribe();
    grantToken();
    const video = http.expectOne(AdminSiteContentApi.videosUrl);
    const videoForm = video.request.body as FormData;
    expect(videoForm.get('poster')).toBeInstanceOf(File);
    expect(videoForm.get('altText')).toBe('Clip');
    video.flush({ publicId: 'v1', url: '/v', posterUrl: '/p' });
  });
});

describe('toContentFailure (#192)', () => {
  function error(status: number, body: unknown): HttpErrorResponse {
    return new HttpErrorResponse({ status, error: body });
  }

  it('says a 403 is about the role', () => {
    const failure = toContentFailure(error(403, null));
    expect(failure.kind).toBe('forbidden');
    expect(failure.message).toContain('Content permission');
  });

  it('turns stale_row_version into a reload message naming the thing', () => {
    const failure = toContentFailure(error(409, { code: 'stale_row_version', detail: 'x' }), 'this block');
    expect(failure.stale).toBe(true);
    expect(failure.message).toContain('Someone else changed this block');
  });

  it('keeps per-field messages, normalising PascalCase keys', () => {
    const failure = toContentFailure(
      error(400, { title: 'Invalid', errors: { ListingUrl: ['Use a path.'], title: ['Too long.', 'Really.'] } }),
    );
    expect(failure.fields).toEqual({ listingUrl: 'Use a path.', title: 'Too long. Really.' });
  });
});

describe('liveLabel (#192)', () => {
  it('names the three states', () => {
    expect(liveLabel({ isEnabled: true, isLiveNow: true })).toBe('On the shop now');
    expect(liveLabel({ isEnabled: true, isLiveNow: false })).toBe('Scheduled');
    expect(liveLabel({ isEnabled: false, isLiveNow: false })).toBe('Off');
  });
});
