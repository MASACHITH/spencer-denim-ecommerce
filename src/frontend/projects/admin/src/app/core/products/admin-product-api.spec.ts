import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { AdminAuthApi } from '../auth/admin-auth-api';
import { AdminProductApi } from './admin-product-api';
import { ProductFormValues } from './admin-product.model';

function values(overrides: Partial<ProductFormValues> = {}): ProductFormValues {
  return {
    productCode: 'SD-W-0001',
    name: 'Wide-Leg Rinse Jeans',
    slug: 'wide-leg-rinse-jeans',
    departmentId: 1,
    primaryCategoryId: 11,
    extraCategoryIds: [],
    ageGroupId: null,
    shortDescription: null,
    description: null,
    careInstructions: null,
    materialId: null,
    fitId: null,
    occasionId: null,
    basePrice: 5490,
    compareAtPrice: null,
    badgeOverride: null,
    recommendedRank: null,
    sizeGuideId: null,
    seoTitle: null,
    seoDescription: null,
    colours: [],
    variants: [],
    relatedProducts: [],
    ...overrides,
  };
}

describe('AdminProductApi', () => {
  let api: AdminProductApi;
  let controller: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });

    api = TestBed.inject(AdminProductApi);
    controller = TestBed.inject(HttpTestingController);
  });

  afterEach(() => controller.verify());

  /** Answers the antiforgery fetch every mutating call makes first (doc 03 §3a). */
  function grantToken(): void {
    controller.expectOne(AdminAuthApi.antiforgeryUrl).flush({ token: 'xsrf-token' });
  }

  it('reads the admin list, not the storefront listing', () => {
    api.list().subscribe();

    const request = controller.expectOne((candidate) => candidate.url === AdminProductApi.productsUrl);
    expect(request.request.method).toBe('GET');
    expect(request.request.url).toBe('/api/admin/products');
    request.flush({ items: [] });
  });

  it('sends only the filters that were asked for', () => {
    api.list({ term: '  denim  ', published: false, page: 2 }).subscribe();

    const request = controller.expectOne((candidate) => candidate.url === AdminProductApi.productsUrl);

    expect(request.request.params.get('term')).toBe('denim');
    expect(request.request.params.get('published')).toBe('false');
    expect(request.request.params.get('page')).toBe('2');
    expect(request.request.params.has('departmentId')).toBe(false);
    request.flush({ items: [] });
  });

  it('fetches an antiforgery token before a create and sends it in the header', () => {
    api.create(values()).subscribe();
    grantToken();

    const request = controller.expectOne(AdminProductApi.productsUrl);
    expect(request.request.method).toBe('POST');
    expect(request.request.headers.get('X-XSRF-TOKEN')).toBe('xsrf-token');
    request.flush({ id: 12 });
  });

  /**
   * **The W1-12 defect, on the wire.** The API leaves a product's photographs alone when the body carries
   * no `media` field and clears them when it carries an empty one, so the client has to be able to send
   * the first. `JSON.stringify` drops an `undefined` property, which is what makes that work — and this
   * test is what stops somebody "tidying" the field into `media: []`.
   */
  it('omits the media field entirely when the form has not touched the gallery', () => {
    api.update(12, values({ media: undefined }), 'rv').subscribe();
    grantToken();

    const request = controller.expectOne('/api/admin/products/12');
    const serialised = JSON.stringify(request.request.body);

    expect(Object.hasOwn(request.request.body as object, 'media')).toBe(false);
    expect(serialised).not.toContain('"media"');
    request.flush(null);
  });

  it('sends an empty media array when the admin has removed every photograph', () => {
    api.update(12, values({ media: [] }), 'rv').subscribe();
    grantToken();

    const request = controller.expectOne('/api/admin/products/12');

    expect((request.request.body as { media: unknown[] }).media).toEqual([]);
    request.flush(null);
  });

  it('sends the row version with an edit', () => {
    api.update(12, values(), 'row-version-token').subscribe();
    grantToken();

    const request = controller.expectOne('/api/admin/products/12');
    expect((request.request.body as { rowVersion: string }).rowVersion).toBe('row-version-token');
    request.flush(null);
  });

  it('publishes through its own route rather than as a field on the form', () => {
    api.setPublished(12, true, 'rv').subscribe();
    grantToken();

    const request = controller.expectOne('/api/admin/products/12/published');
    expect(request.request.method).toBe('PUT');
    expect(request.request.body).toEqual({ isPublished: true, rowVersion: 'rv' });
    request.flush(null);
  });

  it('removes with the row version in If-Match, because a DELETE has no body', () => {
    api.remove(12, 'rv').subscribe();
    grantToken();

    const request = controller.expectOne('/api/admin/products/12');
    expect(request.request.method).toBe('DELETE');
    expect(request.request.headers.get('If-Match')).toBe('"rv"');
    request.flush(null);
  });

  it('uploads a photograph through the media library, not through a product route', () => {
    api.uploadImage(new File(['x'], 'front.png', { type: 'image/png' }), 'Front view').subscribe();
    grantToken();

    const request = controller.expectOne(AdminProductApi.mediaImagesUrl);
    expect(request.request.url).toBe('/api/admin/media/images');
    expect(request.request.body).toBeInstanceOf(FormData);
    expect((request.request.body as FormData).get('altText')).toBe('Front view');
    request.flush({ publicId: 'abc' });
  });

  it('sends no alt text when it has none, rather than an empty one', () => {
    // The publish checklist reads "has alt text", and an empty string would satisfy it. This is
    // what lets doc 11 §7 G-01's third rule block a publish until somebody has described the
    // photograph: the product form uploads with null, and nothing arrives in its place.
    api.uploadImage(new File(['x'], 'front.png', { type: 'image/png' }), null).subscribe();
    grantToken();

    const request = controller.expectOne(AdminProductApi.mediaImagesUrl);
    expect((request.request.body as FormData).has('altText')).toBe(false);
    request.flush({ publicId: 'abc' });
  });

  it('treats whitespace as no alt text at all', () => {
    api.uploadImage(new File(['x'], 'front.png', { type: 'image/png' }), '   ').subscribe();
    grantToken();

    const request = controller.expectOne(AdminProductApi.mediaImagesUrl);
    expect((request.request.body as FormData).has('altText')).toBe(false);
    request.flush({ publicId: 'abc' });
  });

  it('uploads the video and its poster in one request, because the library needs both', () => {
    api
      .uploadVideo(
        new File(['x'], 'clip.mp4', { type: 'video/mp4' }),
        new File(['y'], 'poster.png', { type: 'image/png' }),
        'A clip',
      )
      .subscribe();
    grantToken();

    const request = controller.expectOne(AdminProductApi.mediaVideosUrl);
    const form = request.request.body as FormData;

    expect((form.get('file') as File).name).toBe('clip.mp4');
    expect((form.get('poster') as File).name).toBe('poster.png');
    request.flush({ publicId: 'def' });
  });

  it('searches for related products and excludes the one being edited', () => {
    api.searchForRelated('denim', 12).subscribe();

    const request = controller.expectOne(
      (candidate) => candidate.url === '/api/admin/products/related-search',
    );

    expect(request.request.params.get('term')).toBe('denim');
    expect(request.request.params.get('excludeProductId')).toBe('12');
    request.flush([]);
  });
});
