import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, provideRouter } from '@angular/router';

import { AdminProductApi } from '../../core/products/admin-product-api';
import { AdminProductDetail, ProductFormOptions } from '../../core/products/admin-product.model';
import { ProductFormPage } from './product-form-page';

const ANTIFORGERY = '/api/admin/auth/antiforgery-token';

const OPTIONS: ProductFormOptions = {
  departments: [
    { id: 1, name: 'Women' },
    { id: 3, name: 'Kids' },
  ],
  categories: [
    { id: 10, departmentId: 1, parentCategoryId: null, depth: 1, name: 'Clothing', displayName: 'Clothing', isActive: true },
    { id: 11, departmentId: 1, parentCategoryId: 10, depth: 2, name: 'Jeans', displayName: 'Clothing › Jeans', isActive: true },
    { id: 12, departmentId: 1, parentCategoryId: null, depth: 1, name: 'Sale', displayName: 'Sale', isActive: true },
    { id: 30, departmentId: 3, parentCategoryId: null, depth: 1, name: 'Girls', displayName: 'Girls', isActive: true },
  ],
  ageGroups: [{ id: 2, name: 'Toddler' }],
  materials: [{ id: 41, name: 'Denim' }],
  fits: [{ id: 42, name: 'Slim' }],
  occasions: [{ id: 43, name: 'Everyday' }],
  colourFamilies: [
    { id: 51, name: 'Blue', hexCode: '#28415b' },
    { id: 52, name: 'Black', hexCode: '#111111' },
  ],
  sizes: [
    { id: 61, scaleName: 'Alpha', label: 'S', sortOrder: 1 },
    { id: 62, scaleName: 'Alpha', label: 'M', sortOrder: 2 },
    { id: 69, scaleName: 'Kids age', label: '4–5Y', sortOrder: 1 },
  ],
  sizeGuides: [{ id: 70, name: 'Women tops' }],
};

/**
 * A saved product **with real media attached** — two gallery photographs, a hover photograph and a video.
 *
 * That is deliberate and it is the fixture W1-12 did not have: every one of its fixtures set the media
 * columns to null, so a save that cleared them round-tripped null to null and 1,571 backend tests could not
 * see it. The tests that caught it in the end were frontend tests asserting the outgoing body, which is
 * what this file is.
 */
function product(overrides: Partial<AdminProductDetail> = {}): AdminProductDetail {
  return {
    id: 12,
    productCode: 'SD-W-0001',
    name: 'Wide-Leg Rinse Jeans',
    slug: 'wide-leg-rinse-jeans',
    departmentId: 1,
    primaryCategoryId: 11,
    extraCategoryIds: [12],
    ageGroupId: null,
    shortDescription: 'Everyday denim.',
    description: '<p>Everyday denim.</p>',
    careInstructions: 'Machine wash cold.',
    materialId: 41,
    fitId: 42,
    occasionId: 43,
    basePrice: 5490,
    compareAtPrice: 6990,
    badgeOverride: null,
    newUntilUtc: null,
    recommendedRank: null,
    sizeGuideId: 70,
    seoTitle: 'Wide-Leg Rinse Jeans | Colombo Wardrobe',
    seoDescription: 'Everyday denim.',
    isPublished: false,
    firstPublishedAtUtc: null,
    cardImageUrl: '/media/one.webp',
    hoverImageUrl: '/media/hover.webp',
    colours: [
      {
        id: 101,
        name: 'Indigo Rinse',
        colourFamilyId: 51,
        colourFamilyName: 'Blue',
        swatchHex: '#28415b',
        swatchAssetPublicId: null,
        swatchAssetUrl: null,
        sortOrder: 0,
      },
      {
        id: 102,
        name: 'Jet Black',
        colourFamilyId: 52,
        colourFamilyName: 'Black',
        swatchHex: null,
        swatchAssetPublicId: null,
        swatchAssetUrl: null,
        sortOrder: 1,
      },
    ],
    variants: [
      {
        id: 201,
        colourName: 'Indigo Rinse',
        sizeId: 61,
        sizeLabel: 'S',
        sizeScaleName: 'Alpha',
        sku: 'SD-W-0001-IR-S',
        barcode: null,
        priceOverride: null,
        compareAtPriceOverride: null,
        isActive: true,
        sortOrder: 0,
        hasOrders: false,
        onHand: 4,
      },
      {
        id: 202,
        colourName: 'Jet Black',
        sizeId: 61,
        sizeLabel: 'S',
        sizeScaleName: 'Alpha',
        sku: 'SD-W-0001-JB-S',
        barcode: null,
        priceOverride: null,
        compareAtPriceOverride: null,
        isActive: true,
        sortOrder: 1,
        hasOrders: true,
        onHand: 1,
      },
    ],
    media: [
      {
        id: 301,
        assetPublicId: 'aaaaaaaa-0000-0000-0000-000000000001',
        mediaType: 'Image',
        url: '/media/one.webp',
        posterUrl: null,
        colourName: null,
        mediaRole: 'Gallery',
        sortOrder: 0,
        altTextOverride: 'Front view',
        assetAltText: 'A photograph',
      },
      {
        id: 302,
        assetPublicId: 'aaaaaaaa-0000-0000-0000-000000000002',
        mediaType: 'Image',
        url: '/media/two.webp',
        posterUrl: null,
        colourName: 'Jet Black',
        mediaRole: 'Gallery',
        sortOrder: 1,
        altTextOverride: 'Jet Black, front',
        assetAltText: 'A photograph',
      },
      {
        id: 303,
        assetPublicId: 'aaaaaaaa-0000-0000-0000-000000000003',
        mediaType: 'Image',
        url: '/media/hover.webp',
        posterUrl: null,
        colourName: null,
        mediaRole: 'Hover',
        sortOrder: 0,
        altTextOverride: 'Back view',
        assetAltText: 'A photograph',
      },
      {
        id: 304,
        assetPublicId: 'aaaaaaaa-0000-0000-0000-000000000004',
        mediaType: 'Video',
        url: '/media/clip.mp4',
        posterUrl: '/media/poster.webp',
        colourName: null,
        mediaRole: 'Video',
        sortOrder: 0,
        altTextOverride: null,
        assetAltText: null,
      },
    ],
    relatedProducts: [
      { relatedProductId: 90, name: 'Denim Jacket', productCode: 'SD-W-0090', isPublished: true, sortOrder: 0 },
      { relatedProductId: 91, name: 'Linen Shirt', productCode: 'SD-W-0091', isPublished: false, sortOrder: 1 },
    ],
    canBePublished: true,
    publishBlockers: [],
    rowVersion: 'rv-1',
    ...overrides,
  };
}

describe('ProductFormPage (ADM-10)', () => {
  let controller: HttpTestingController;
  let fixture: ComponentFixture<ProductFormPage>;
  let element: HTMLElement;

  function configure(id: string | null): void {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      imports: [ProductFormPage],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        {
          provide: ActivatedRoute,
          useValue: { snapshot: { paramMap: { get: () => id } } },
        },
      ],
    });

    controller = TestBed.inject(HttpTestingController);
  }

  async function open(detail: AdminProductDetail | null = product()): Promise<void> {
    configure(detail === null ? null : String(detail.id));

    fixture = TestBed.createComponent(ProductFormPage);
    element = fixture.nativeElement as HTMLElement;
    fixture.detectChanges();

    controller.expectOne('/api/admin/products/form-options').flush(OPTIONS);

    if (detail !== null) {
      controller.expectOne(`/api/admin/products/${detail.id}`).flush(detail);
    }

    fixture.detectChanges();
    await settle();
  }

  async function settle(): Promise<void> {
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  function testId<T extends HTMLElement>(id: string): T {
    return element.querySelector(`[data-testid="${id}"]`) as T;
  }

  async function type(id: string, value: string): Promise<void> {
    const input = testId<HTMLInputElement>(id);
    input.value = value;
    input.dispatchEvent(new Event('input'));
    await settle();
  }

  async function click(id: string): Promise<void> {
    testId<HTMLButtonElement>(id).click();
    await settle();
  }

  async function save(): Promise<void> {
    element.querySelector('form')!.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
    await settle();
  }

  /** Answers the antiforgery fetch every mutating call makes, then returns the write request. */
  function writeTo(url: string) {
    controller.expectOne(ANTIFORGERY).flush({ token: 'xsrf-token' });

    return controller.expectOne(url);
  }

  afterEach(() => controller.verify());

  // ---- reading ---------------------------------------------------------------------------------

  it('fills every field of a saved product, including its colours, matrix, media and picks', async () => {
    await open();

    expect(testId<HTMLInputElement>('field-name').value).toBe('Wide-Leg Rinse Jeans');
    expect(testId<HTMLInputElement>('field-slug').value).toBe('wide-leg-rinse-jeans');
    expect(element.querySelectorAll('[data-testid^="colour-"][data-testid$="-0"]').length).toBeGreaterThan(0);
    expect(testId('colour-list').children).toHaveLength(2);
    expect(testId('variant-list').children).toHaveLength(2);
    expect(testId('media-list').children).toHaveLength(4);
    expect(testId('related-list').children).toHaveLength(2);
  });

  it('marks an ordered variant so the admin can see why it cannot be deleted', async () => {
    await open();

    expect(testId('variant-ordered-1')).not.toBeNull();
    expect(testId('variant-ordered-0')).toBeNull();
  });

  it('shows the discount the shop would work out from the original price', async () => {
    await open();

    expect(testId('discount-preview').textContent).toContain('21%');
  });

  // ---- the W1-12 defect ------------------------------------------------------------------------

  /**
   * **The defect W1-12 shipped, caught where it was caught the first time: in the outgoing body.** A save
   * that changes only the name must send no `media` field at all, because that is what tells the API to
   * leave the photographs alone.
   */
  it('a save that only renames the product sends no media field', async () => {
    await open();

    await type('field-name', 'Wide-Leg Rinse Jeans (2026)');
    await save();

    const request = writeTo('/api/admin/products/12');
    const body = request.request.body as Record<string, unknown>;

    expect(body['name']).toBe('Wide-Leg Rinse Jeans (2026)');
    expect(Object.hasOwn(body, 'media')).toBe(false);

    request.flush(null);
    controller.expectOne('/api/admin/products/12').flush(product({ name: 'Wide-Leg Rinse Jeans (2026)' }));
    await settle();
  });

  it('a save after removing a photograph sends the whole remaining list', async () => {
    await open();

    await click('media-remove-0');
    await save();

    const request = writeTo('/api/admin/products/12');
    const body = request.request.body as { media: { assetPublicId: string }[] };

    expect(body.media).toHaveLength(3);
    expect(body.media.map((row) => row.assetPublicId)).not.toContain(
      'aaaaaaaa-0000-0000-0000-000000000001',
    );

    request.flush(null);
    controller.expectOne('/api/admin/products/12').flush(product());
    await settle();
  });

  it('a save after reordering the gallery sends the new sort order', async () => {
    await open();

    await click('media-down-0');
    await save();

    const request = writeTo('/api/admin/products/12');
    const body = request.request.body as { media: { assetPublicId: string; sortOrder: number }[] };

    expect(body.media[0].assetPublicId).toBe('aaaaaaaa-0000-0000-0000-000000000002');
    expect(body.media.map((row) => row.sortOrder)).toEqual([0, 1, 2, 3]);

    request.flush(null);
    controller.expectOne('/api/admin/products/12').flush(product());
    await settle();
  });

  // ---- colours, variants, gallery --------------------------------------------------------------

  it('renaming a colour follows through to the variants and the gallery rows that name it', async () => {
    await open();

    await type('colour-name-1', 'Jet Black Deep');
    await save();

    const request = writeTo('/api/admin/products/12');
    const body = request.request.body as {
      colours: { name: string }[];
      variants: { colourName: string }[];
    };

    expect(body.colours.map((colour) => colour.name)).toContain('Jet Black Deep');
    expect(body.variants.map((variant) => variant.colourName)).toContain('Jet Black Deep');
    expect(body.variants.map((variant) => variant.colourName)).not.toContain('Jet Black');

    request.flush(null);
    controller.expectOne('/api/admin/products/12').flush(product());
    await settle();
  });

  it('removing a colour removes its variants and its own gallery rows', async () => {
    await open();

    await click('colour-remove-1');

    expect(testId('colour-list').children).toHaveLength(1);
    expect(testId('variant-list').children).toHaveLength(1);
    expect(testId('media-list').children).toHaveLength(3);
  });

  it('adding a colour and ticking a size makes one row with a suggested SKU', async () => {
    await open(product({ colours: [], variants: [], media: [] }));

    await click('add-colour');
    await type('colour-name-0', 'Indigo Rinse');

    const scale = testId<HTMLSelectElement>('field-size-scale');
    scale.value = scale.options[1].value;
    scale.dispatchEvent(new Event('change'));
    await settle();

    await click('size-61');

    expect(testId('variant-list').children).toHaveLength(1);
    expect(testId<HTMLInputElement>('variant-sku-0').value).toBe('SD-W-0001-INDIGO-RINSE-S');
  });

  // ---- the Kids rule ---------------------------------------------------------------------------

  it('asks for an age group only in the Kids department', async () => {
    await open();

    expect(testId('field-age-group')).toBeNull();

    const department = testId<HTMLSelectElement>('field-department');
    department.value = department.options[2].value;
    department.dispatchEvent(new Event('change'));
    await settle();

    expect(testId('field-age-group')).not.toBeNull();
  });

  // ---- the slug rules --------------------------------------------------------------------------

  it('suggests a slug from the name until the product has been published', async () => {
    await open(product({ slug: 'wide-leg-rinse-jeans' }));

    await type('field-name', 'Wide-Leg Rinse Jeans');
    await type('field-name', 'Straight-Leg Rinse Jeans');

    expect(testId<HTMLInputElement>('field-slug').value).toBe('straight-leg-rinse-jeans');
  });

  it('stops suggesting once the product has been published, because the address may be bookmarked', async () => {
    await open(product({ firstPublishedAtUtc: '2026-09-01T00:00:00Z', isPublished: true }));

    await type('field-name', 'Straight-Leg Rinse Jeans');

    expect(testId<HTMLInputElement>('field-slug').value).toBe('wide-leg-rinse-jeans');
  });

  it('offers a confirmation when the API refuses a published slug change, and resends with it', async () => {
    await open(product({ firstPublishedAtUtc: '2026-09-01T00:00:00Z', isPublished: true }));

    await type('field-slug', 'renamed-jeans');
    await save();

    writeTo('/api/admin/products/12').flush(
      {
        title: 'This change cannot be applied',
        detail: 'That address may be bookmarked.',
        code: 'slug_change_needs_confirmation',
        currentSlug: 'wide-leg-rinse-jeans',
        requestedSlug: 'renamed-jeans',
      },
      { status: 409, statusText: 'Conflict' },
    );
    await settle();

    expect(testId('confirm-slug')).not.toBeNull();

    await click('confirm-slug');

    const retry = writeTo('/api/admin/products/12');
    expect((retry.request.body as { confirmSlugChange: boolean }).confirmSlugChange).toBe(true);

    retry.flush(null);
    controller.expectOne('/api/admin/products/12').flush(product({ slug: 'renamed-jeans' }));
    await settle();
  });

  it('puts the old address back when the admin declines the confirmation', async () => {
    await open(product({ firstPublishedAtUtc: '2026-09-01T00:00:00Z', isPublished: true }));

    await type('field-slug', 'renamed-jeans');
    await save();

    writeTo('/api/admin/products/12').flush(
      {
        detail: 'That address may be bookmarked.',
        code: 'slug_change_needs_confirmation',
        currentSlug: 'wide-leg-rinse-jeans',
        requestedSlug: 'renamed-jeans',
      },
      { status: 409, statusText: 'Conflict' },
    );
    await settle();

    await click('cancel-slug');

    expect(testId<HTMLInputElement>('field-slug').value).toBe('wide-leg-rinse-jeans');
    expect(testId('page-error')).toBeNull();
  });

  // ---- validation ------------------------------------------------------------------------------

  it('attaches a field error to the control it belongs to and marks it invalid', async () => {
    await open();

    await save();

    writeTo('/api/admin/products/12').flush(
      { title: 'One or more fields are invalid.', errors: { slug: ['That slug is already in use.'] } },
      { status: 400, statusText: 'Bad Request' },
    );
    await settle();

    expect(testId('error-slug').textContent).toContain('already in use');
    expect(testId<HTMLInputElement>('field-slug').getAttribute('aria-invalid')).toBe('true');
  });

  it('attaches a variant error to the row it belongs to', async () => {
    await open();

    await save();

    writeTo('/api/admin/products/12').flush(
      {
        title: 'One or more fields are invalid.',
        errors: { 'variants[1].sku': ['SKU SD-W-0001-JB-S already belongs to another product.'] },
      },
      { status: 400, statusText: 'Bad Request' },
    );
    await settle();

    expect(testId('variant-sku-error-1').textContent).toContain('already belongs');
  });

  // ---- accessibility ---------------------------------------------------------------------------

  /**
   * The ADM-22 defect, in both halves.
   *
   * `role="status"` announces a *change*, so a save that only ever set the region to its final sentence
   * announced once and then never again — which on a screen built for repeated edits is most of the time.
   * Saying "Saving…" on the way in gives two real mutations per save.
   */
  it('announces the start and the end of every save, so a repeated save speaks twice', async () => {
    await open();

    await type('field-name', 'First edit');
    await save();

    expect(testId('announcement').textContent).toContain('Saving…');

    writeTo('/api/admin/products/12').flush(null);
    controller.expectOne('/api/admin/products/12').flush(product({ name: 'First edit' }));
    await settle();

    expect(testId('announcement').textContent).toContain('Changes saved');

    await type('field-name', 'Second edit');
    await save();

    expect(testId('announcement').textContent).toContain('Saving…');

    writeTo('/api/admin/products/12').flush(null);
    controller.expectOne('/api/admin/products/12').flush(product({ name: 'Second edit' }));
    await settle();

    expect(testId('announcement').textContent).toContain('Changes saved');
  });

  /**
   * The other half: `[disabled]` on a focused control makes the browser move focus to `<body>`, so a
   * keyboard user loses their place as a result of their own keystroke. Every control a save makes
   * unavailable therefore says `aria-disabled` and stays focusable.
   */
  it('uses aria-disabled rather than disabled while a save is in flight, and keeps the form mounted', async () => {
    await open();

    const button = testId<HTMLButtonElement>('save');
    button.focus();

    await type('field-name', 'Edited');
    await save();

    expect(button.getAttribute('aria-disabled')).toBe('true');
    expect(button.disabled).toBe(false);
    expect(document.activeElement).toBe(button);

    // The form is not replaced by a loading state, so the control the admin pressed survives the write.
    writeTo('/api/admin/products/12').flush(null);
    controller.expectOne('/api/admin/products/12').flush(product({ name: 'Edited' }));
    await settle();

    expect(testId('save')).not.toBeNull();
    expect(testId('loading')).toBeNull();
  });

  it('refuses a second save while one is in flight, since the button is still clickable', async () => {
    await open();

    await type('field-name', 'Edited');
    await save();
    await save();

    const request = writeTo('/api/admin/products/12');
    request.flush(null);
    controller.expectOne('/api/admin/products/12').flush(product({ name: 'Edited' }));
    await settle();

    // controller.verify() in afterEach is the assertion: a second save would have queued a second
    // antiforgery fetch and a second PUT, and the verify would fail on them.
    expect(testId('announcement').textContent).toContain('Changes saved');
  });

  it('announces a move and says where the row has gone', async () => {
    await open();

    await click('colour-down-0');

    expect(testId('announcement').textContent).toContain('position 2 of 2');
  });

  it('keeps focus on the button the admin pressed after a move', async () => {
    await open();

    const down = testId<HTMLButtonElement>('colour-down-0');
    down.focus();
    down.click();
    await settle();

    expect((document.activeElement as HTMLElement)?.getAttribute('data-testid')).toBe('colour-down-1');
  });

  it('marks a move button unavailable at the end of its list without disabling it', async () => {
    await open();

    const up = testId<HTMLButtonElement>('colour-up-0');

    expect(up.getAttribute('aria-disabled')).toBe('true');
    expect(up.disabled).toBe(false);
  });

  // ---- publish and remove ----------------------------------------------------------------------

  it('shows the publish checklist from the payload rather than working it out again', async () => {
    await open(
      product({
        canBePublished: false,
        publishBlockers: ['Add at least one gallery photograph: it becomes the picture on the listing card.'],
      }),
    );

    expect(testId('publish-blockers').textContent).toContain('gallery photograph');
    expect(testId<HTMLButtonElement>('toggle-published').getAttribute('aria-disabled')).toBe('true');
  });

  it('publishes through the publish route, with the row version', async () => {
    await open();

    await click('toggle-published');

    const request = writeTo('/api/admin/products/12/published');
    expect(request.request.body).toEqual({ isPublished: true, rowVersion: 'rv-1' });

    request.flush(null);
    controller.expectOne('/api/admin/products/12').flush(product({ isPublished: true }));
    await settle();

    expect(testId('announcement').textContent).toContain('on the shop');
  });

  it('asks before removing a product and names what survives', async () => {
    await open();

    await click('remove');

    expect(testId('confirm-remove').textContent).toContain('order history are kept');

    await click('confirm-remove-no');

    expect(testId('confirm-remove')).toBeNull();
  });

  it('removes with the row version in If-Match', async () => {
    await open();

    await click('remove');
    await click('confirm-remove-yes');

    const request = writeTo('/api/admin/products/12');
    expect(request.request.method).toBe('DELETE');
    expect(request.request.headers.get('If-Match')).toBe('"rv-1"');

    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);

    request.flush(null);
    await settle();

    // And back to the list, because the product this form was editing no longer exists.
    expect(navigate).toHaveBeenCalledWith(['/products']);
  });

  // ---- creating --------------------------------------------------------------------------------

  it('creates a product with a POST and no row version', async () => {
    await open(null);

    await type('field-product-code', 'SD-W-0002');
    await type('field-name', 'Linen Shirt');
    await type('field-base-price', '4490');

    const department = testId<HTMLSelectElement>('field-department');
    department.value = department.options[1].value;
    department.dispatchEvent(new Event('change'));
    await settle();

    const category = testId<HTMLSelectElement>('field-primary-category');
    category.value = category.options[2].value;
    category.dispatchEvent(new Event('change'));
    await settle();

    await save();

    const request = writeTo('/api/admin/products');
    const body = request.request.body as Record<string, unknown>;

    expect(request.request.method).toBe('POST');
    expect(body['slug']).toBe('linen-shirt');
    expect(body['rowVersion']).toBeUndefined();

    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);

    request.flush({ id: 55 });
    await settle();

    // **The half of this test that was missing.** It asserted the request body and stopped, so it never
    // checked that the admin is then taken to the product they just created — which is the only way to
    // publish it. The gap also left a real promise rejection escaping the suite: the component navigated
    // to /products/55 against `provideRouter([])`, so Angular raised NG04002 "Cannot match any routes",
    // Vitest printed `Errors 2` and still exited 0. A pass with errors beside it is not a pass.
    expect(navigate).toHaveBeenCalledWith(['/products', 55]);
  });

  it('does not offer publish or remove before the product exists', async () => {
    await open(null);

    expect(testId('toggle-published')).toBeNull();
    expect(testId('remove')).toBeNull();
  });

  // ---- media upload ----------------------------------------------------------------------------

  it('refuses a video larger than the library will store, before uploading anything', async () => {
    await open();

    const input = testId<HTMLInputElement>('upload-video');
    const big = new File([new Uint8Array(8)], 'big.mp4', { type: 'video/mp4' });
    Object.defineProperty(big, 'size', { value: 51 * 1024 * 1024 });
    Object.defineProperty(input, 'files', { value: [big] });

    input.dispatchEvent(new Event('change'));
    await settle();

    expect(testId('media-error').textContent).toContain('50 MB');
    expect(testId('upload-poster')).toBeNull();
  });

  it('asks for a poster once a video is chosen, because the library cannot store one without it', async () => {
    await open();

    const input = testId<HTMLInputElement>('upload-video');
    const clip = new File(['x'], 'clip.mp4', { type: 'video/mp4' });
    Object.defineProperty(input, 'files', { value: [clip] });

    input.dispatchEvent(new Event('change'));
    await settle();

    expect(testId('upload-poster')).not.toBeNull();
    expect(testId('announcement').textContent).toContain('poster');
  });

  it('says in plain words, in view, why a photograph was refused by the web server', async () => {
    await open(product({ media: [] }));

    const input = testId<HTMLInputElement>('upload-image');
    Object.defineProperty(input, 'files', { value: [new File(['x'], 'front.png', { type: 'image/png' })] });

    input.dispatchEvent(new Event('change'));
    await settle();

    // The web server in front of the API answers an over-size upload itself, with a page and no JSON.
    writeTo(AdminProductApi.mediaImagesUrl).flush('<html>413</html>', { status: 413, statusText: 'Payload Too Large' });
    await settle();

    expect(testId('media-error').textContent).toContain('larger than the server accepts');
    expect(element.querySelector('[data-testid="media-status"]')).toBeNull();
  });

  it('names the status number when the server breaks while a photograph is uploading', async () => {
    await open(product({ media: [] }));

    const input = testId<HTMLInputElement>('upload-image');
    Object.defineProperty(input, 'files', { value: [new File(['x'], 'front.png', { type: 'image/png' })] });

    input.dispatchEvent(new Event('change'));
    await settle();

    writeTo(AdminProductApi.mediaImagesUrl).flush(null, { status: 503, statusText: 'Service Unavailable' });
    await settle();

    expect(testId('media-error').textContent).toContain('error 503');
  });

  it('uploads a photograph through the media library and adds it to the gallery', async () => {
    await open(product({ media: [] }));

    const input = testId<HTMLInputElement>('upload-image');
    const file = new File(['x'], 'front.png', { type: 'image/png' });
    Object.defineProperty(input, 'files', { value: [file] });

    input.dispatchEvent(new Event('change'));
    await settle();

    // UAT feedback: choosing a file looked like it did nothing, because only a hidden live region said so.
    expect(testId('media-status').textContent).toContain('Uploading front.png');

    const request = writeTo(AdminProductApi.mediaImagesUrl);

    // Uploaded undescribed. Pre-filling the alt text with the product's name made doc 11 §7
    // G-01's third rule unable to ever block a publish, because the checklist accepts the
    // library asset's own alt text.
    expect((request.request.body as FormData).has('altText')).toBe(false);

    request.flush({
      publicId: 'new-asset',
      mediaType: 'Image',
      url: '/media/new.webp',
      posterUrl: null,
      altText: null,
    });
    await settle();

    expect(testId('media-list').children).toHaveLength(1);
    expect(testId('media-status').textContent).toContain('front.png is added to the gallery');

    // And the gallery is now "touched", so the next save sends the field.
    await save();

    const put = writeTo('/api/admin/products/12');
    expect(Object.hasOwn(put.request.body as object, 'media')).toBe(true);
    put.flush(null);
    controller.expectOne('/api/admin/products/12').flush(product({ media: [] }));
    await settle();
  });
});
