import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';

import { AdminAuthApi } from '../../core/auth/admin-auth-api';
import { AdminSiteContentApi } from '../../core/content/admin-site-content-api';
import {
  AdminSizeGuide,
  AdminSizeGuideListItem,
  DEFAULT_SIZE_GUIDE_LIMITS,
} from '../../core/content/admin-site-content.model';
import { SizeGuideFormPage } from './size-guide-form-page';
import { SizeGuidesPage } from './size-guides-page';

function q<T extends HTMLElement>(fixture: ComponentFixture<unknown>, testId: string): T | null {
  return (fixture.nativeElement as HTMLElement).querySelector<T>(`[data-testid="${testId}"]`);
}

function type(input: HTMLInputElement | HTMLTextAreaElement, value: string): void {
  input.value = value;
  input.dispatchEvent(new Event('input'));
}

describe('SizeGuidesPage (#192, PDP-04)', () => {
  let controller: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [SizeGuidesPage],
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });
    controller = TestBed.inject(HttpTestingController);
  });

  afterEach(() => controller.verify());

  function guide(overrides: Partial<AdminSizeGuideListItem> = {}): AdminSizeGuideListItem {
    return {
      id: 1,
      name: "Women's tops",
      categoryCount: 2,
      productCount: 5,
      hasImage: false,
      updatedAtUtc: null,
      rowVersion: 'r1',
      ...overrides,
    };
  }

  function create(rows: AdminSizeGuideListItem[]): ComponentFixture<SizeGuidesPage> {
    const fixture = TestBed.createComponent(SizeGuidesPage);
    fixture.detectChanges();
    controller.expectOne(AdminSiteContentApi.sizeGuidesUrl).flush(rows);
    fixture.detectChanges();
    return fixture;
  }

  it('lists the guides with their usage counts', () => {
    const fixture = create([guide(), guide({ id: 2, name: 'Unused', categoryCount: 0, productCount: 0 })]);

    expect(q(fixture, 'sg-1')?.textContent).toContain("Women's tops");
    expect(q(fixture, 'sg-categories-1')?.textContent?.trim()).toBe('2');
    expect(q(fixture, 'sg-products-1')?.textContent?.trim()).toBe('5');
  });

  it('turns size_guide_in_use into a readable message', () => {
    const fixture = create([guide()]);

    q<HTMLButtonElement>(fixture, 'sg-remove-1')!.click();
    fixture.detectChanges();
    q<HTMLButtonElement>(fixture, 'sg-remove-1')!.click();
    controller.expectOne(AdminAuthApi.antiforgeryUrl).flush({ token: 'xsrf' });

    const request = controller.expectOne('/api/admin/content/size-guides/1');
    expect(request.request.method).toBe('DELETE');
    expect(request.request.headers.get('If-Match')).toBe('"r1"');
    request.flush(
      { code: 'size_guide_in_use', detail: 'In use.', categoryCount: 3, productCount: 1 },
      { status: 409, statusText: 'Conflict' },
    );
    fixture.detectChanges();

    expect(q(fixture, 'sg-problem')?.textContent).toContain('still used by 3 categories and 1 product');
  });

  it('shows the refusal panel to a role without Content.View', () => {
    const fixture = TestBed.createComponent(SizeGuidesPage);
    fixture.detectChanges();
    controller.expectOne(AdminSiteContentApi.sizeGuidesUrl).flush(null, { status: 403, statusText: 'Forbidden' });
    fixture.detectChanges();

    expect(q(fixture, 'sg-problem')?.textContent).toContain('does not include size guides');
    expect(q(fixture, 'sg-add')).toBeNull();
  });
});

describe('SizeGuideFormPage (#192, PDP-04)', () => {
  let controller: HttpTestingController;

  function configure(id: string | null): void {
    TestBed.configureTestingModule({
      imports: [SizeGuideFormPage],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: convertToParamMap(id ? { id } : {}) } } },
      ],
    });
    controller = TestBed.inject(HttpTestingController);
  }

  afterEach(() => controller.verify());

  function loaded(overrides: Partial<AdminSizeGuide> = {}): AdminSizeGuide {
    return {
      id: 4,
      name: 'Tops',
      introHtml: '<p>True to size.</p>',
      table: { columns: ['Size', 'Chest (cm)'], rows: [['S', '86-91']] },
      measuringTipsHtml: null,
      image: null,
      categoryCount: 1,
      productCount: 0,
      rowVersion: 'r4',
      limits: DEFAULT_SIZE_GUIDE_LIMITS,
      ...overrides,
    };
  }

  function open(guide: AdminSizeGuide = loaded()): ComponentFixture<SizeGuideFormPage> {
    configure(String(guide.id));
    const fixture = TestBed.createComponent(SizeGuideFormPage);
    fixture.detectChanges();
    controller.expectOne(`/api/admin/content/size-guides/${guide.id}`).flush(guide);
    fixture.detectChanges();
    return fixture;
  }

  function grantToken(): void {
    controller.expectOne(AdminAuthApi.antiforgeryUrl).flush({ token: 'xsrf' });
  }

  it('renders the guide, its table and its usage', () => {
    const fixture = open();

    expect(q<HTMLInputElement>(fixture, 'sg-name')?.value).toBe('Tops');
    expect(q<HTMLInputElement>(fixture, 'sg-col-1')?.value).toBe('Chest (cm)');
    expect(q<HTMLInputElement>(fixture, 'sg-cell-0-1')?.value).toBe('86-91');
    expect(q(fixture, 'sg-usage')?.textContent).toContain('1 category');
  });

  it('adds and removes columns and rows, keeping every row as wide as the headings', () => {
    const fixture = open();

    q<HTMLButtonElement>(fixture, 'sg-add-col')!.click();
    q<HTMLButtonElement>(fixture, 'sg-add-row')!.click();
    fixture.detectChanges();
    expect(q(fixture, 'sg-col-2')).not.toBeNull();
    expect(q(fixture, 'sg-cell-1-2')).not.toBeNull();

    q<HTMLButtonElement>(fixture, 'sg-remove-col-0')!.click();
    fixture.detectChanges();
    expect(q<HTMLInputElement>(fixture, 'sg-col-0')?.value).toBe('Chest (cm)');
    expect(q(fixture, 'sg-cell-0-2')).toBeNull();
  });

  it('refuses to grow past the column limit', () => {
    const fixture = open(loaded({ limits: { ...DEFAULT_SIZE_GUIDE_LIMITS, maxColumns: 2 } }));

    q<HTMLButtonElement>(fixture, 'sg-add-col')!.click();
    fixture.detectChanges();
    expect(q(fixture, 'sg-grid-message')?.textContent).toContain('up to 2 columns');
    expect(q(fixture, 'sg-col-2')).toBeNull();
  });

  it('saves every key, with the table as a JSON string, and shows what the sanitiser removed', () => {
    const fixture = open();

    type(q<HTMLTextAreaElement>(fixture, 'sg-tips')!, '<p onclick="x()">Measure</p>');
    q<HTMLButtonElement>(fixture, 'sg-save')!.click();
    grantToken();

    const request = controller.expectOne('/api/admin/content/size-guides/4');
    expect(request.request.method).toBe('PUT');
    expect(request.request.body).toEqual({
      name: 'Tops',
      introHtml: '<p>True to size.</p>',
      tableJson: '{"columns":["Size","Chest (cm)"],"rows":[["S","86-91"]]}',
      measuringTipsHtml: '<p onclick="x()">Measure</p>',
      imagePublicId: null,
      rowVersion: 'r4',
    });
    request.flush({ id: 4, rowVersion: 'r5', removedFromIntro: [], removedFromTips: ['onclick'] });
    controller.expectOne('/api/admin/content/size-guides/4').flush(loaded({ rowVersion: 'r5' }));
    fixture.detectChanges();

    expect(q(fixture, 'sg-removed-tips')?.textContent).toContain('onclick');
    expect(q(fixture, 'sg-removed-intro')).toBeNull();
  });

  it('puts a tableJson error under the grid and a name error under the name', () => {
    const fixture = open();

    q<HTMLButtonElement>(fixture, 'sg-save')!.click();
    grantToken();
    controller.expectOne('/api/admin/content/size-guides/4').flush(
      { title: 'Invalid', status: 400, errors: { tableJson: ['Every column needs a heading.'], name: ['Required.'] } },
      { status: 400, statusText: 'Bad Request' },
    );
    fixture.detectChanges();

    expect(q(fixture, 'sg-error-tableJson')?.textContent).toContain('Every column needs a heading.');
    expect(q(fixture, 'sg-error-name')?.textContent).toContain('Required.');
  });

  it('says a stale guide was changed by someone else', () => {
    const fixture = open();

    q<HTMLButtonElement>(fixture, 'sg-save')!.click();
    grantToken();
    controller
      .expectOne('/api/admin/content/size-guides/4')
      .flush({ code: 'stale_row_version' }, { status: 409, statusText: 'Conflict' });
    fixture.detectChanges();

    expect(q(fixture, 'sg-problem')?.textContent).toContain('Someone else changed this size guide');
  });

  it('creates a new guide with POST and goes to it', async () => {
    configure(null);
    const fixture = TestBed.createComponent(SizeGuideFormPage);
    const router = TestBed.inject(Router);
    const navigate = vi.spyOn(router, 'navigate').mockResolvedValue(true);
    fixture.detectChanges();

    type(q<HTMLInputElement>(fixture, 'sg-name')!, 'Jeans');
    type(q<HTMLInputElement>(fixture, 'sg-col-0')!, 'Waist');
    type(q<HTMLInputElement>(fixture, 'sg-cell-0-0')!, '28');
    q<HTMLButtonElement>(fixture, 'sg-save')!.click();
    grantToken();

    const request = controller.expectOne(AdminSiteContentApi.sizeGuidesUrl);
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({
      name: 'Jeans',
      introHtml: null,
      tableJson: '{"columns":["Waist"],"rows":[["28"]]}',
      measuringTipsHtml: null,
      imagePublicId: null,
    });
    request.flush({ id: 9, rowVersion: 'r', removedFromIntro: ['<script>'], removedFromTips: [] });

    expect(navigate).toHaveBeenCalledWith(['/content/size-guides', 9], {
      state: { sizeGuideSaved: { removedFromIntro: ['<script>'], removedFromTips: [] } },
    });
  });

  it('shows the refusal panel to a role without Content.View', () => {
    configure('4');
    const fixture = TestBed.createComponent(SizeGuideFormPage);
    fixture.detectChanges();
    controller.expectOne('/api/admin/content/size-guides/4').flush(null, { status: 403, statusText: 'Forbidden' });
    fixture.detectChanges();

    expect(q(fixture, 'sg-problem')?.textContent).toContain('does not include size guides');
    expect(q(fixture, 'sg-save')).toBeNull();
  });
});
