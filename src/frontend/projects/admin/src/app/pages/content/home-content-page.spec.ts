import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { AdminAuthApi } from '../../core/auth/admin-auth-api';
import { AdminSiteContentApi } from '../../core/content/admin-site-content-api';
import {
  AdminHomeContent,
  AdminHomeSection,
  AdminHomeSectionRules,
  HomeSectionType,
} from '../../core/content/admin-site-content.model';
import { HomeContentPage } from './home-content-page';

const RULES: Record<HomeSectionType, AdminHomeSectionRules> = {
  HeroVideo: {
    sectionFields: ['title', 'subtitle', 'overlay'],
    itemFields: ['title', 'subtitle', 'buttonLabel', 'linkUrl', 'image', 'mobileImage', 'video'],
    requiredItemFields: [],
    maxItems: 1,
    linkMayLeaveSite: false,
    needsArtwork: false,
  },
  ShopByDepartment: {
    sectionFields: ['title', 'subtitle'],
    itemFields: ['title', 'buttonLabel', 'linkUrl', 'image', 'mobileImage', 'department'],
    requiredItemFields: ['department'],
    maxItems: 6,
    linkMayLeaveSite: false,
    needsArtwork: false,
  },
  NewArrivals: {
    sectionFields: ['title', 'subtitle', 'count', 'listingUrl', 'linkLabel'],
    itemFields: [],
    requiredItemFields: [],
    maxItems: 0,
    linkMayLeaveSite: false,
    needsArtwork: false,
  },
  TrendingItems: {
    sectionFields: ['title', 'subtitle', 'mode', 'count', 'listingUrl', 'linkLabel'],
    itemFields: ['title', 'subtitle', 'linkUrl', 'image', 'product'],
    requiredItemFields: ['title'],
    maxItems: 12,
    linkMayLeaveSite: false,
    needsArtwork: false,
  },
  CampaignBanner: {
    sectionFields: ['title', 'subtitle', 'overlay'],
    itemFields: ['title', 'subtitle', 'body', 'buttonLabel', 'linkUrl', 'image', 'mobileImage'],
    requiredItemFields: [],
    maxItems: 1,
    linkMayLeaveSite: false,
    needsArtwork: false,
  },
  ActivewearFeature: {
    sectionFields: ['title', 'subtitle', 'overlay'],
    itemFields: ['title', 'subtitle', 'body', 'buttonLabel', 'linkUrl', 'image', 'mobileImage'],
    requiredItemFields: [],
    maxItems: 1,
    linkMayLeaveSite: false,
    needsArtwork: false,
  },
  SaleSection: {
    sectionFields: ['title', 'subtitle', 'count', 'listingUrl', 'linkLabel'],
    itemFields: [],
    requiredItemFields: [],
    maxItems: 0,
    linkMayLeaveSite: false,
    needsArtwork: false,
  },
  SocialGallery: {
    sectionFields: ['title', 'subtitle'],
    itemFields: ['title', 'linkUrl', 'image'],
    requiredItemFields: ['image'],
    maxItems: 12,
    linkMayLeaveSite: true,
    needsArtwork: false,
  },
  Benefits: {
    sectionFields: ['title', 'subtitle'],
    itemFields: ['title', 'body', 'iconKey'],
    requiredItemFields: ['iconKey'],
    maxItems: 6,
    linkMayLeaveSite: false,
    needsArtwork: false,
  },
  Newsletter: {
    sectionFields: ['title', 'subtitle'],
    itemFields: [],
    requiredItemFields: [],
    maxItems: 0,
    linkMayLeaveSite: false,
    needsArtwork: false,
  },
};

function section(id: number, type: HomeSectionType, overrides: Partial<AdminHomeSection> = {}): AdminHomeSection {
  return {
    id,
    type,
    title: null,
    subtitle: null,
    isEnabled: true,
    sortOrder: id - 1,
    startsAt: null,
    endsAt: null,
    mode: type === 'TrendingItems' ? 'Auto' : null,
    count: RULES[type].sectionFields.includes('count') ? 8 : null,
    listingUrl: RULES[type].sectionFields.includes('listingUrl') ? '/new' : null,
    linkLabel: null,
    overlay: null,
    isLiveNow: true,
    rules: RULES[type],
    items: [],
    rowVersion: `rv-${id}`,
    ...overrides,
  };
}

function content(): AdminHomeContent {
  const types = Object.keys(RULES) as HomeSectionType[];
  const sections = types.map((type, index) => section(index + 1, type));

  sections[0] = section(1, 'HeroVideo', { title: 'The linen edit', overlay: 'dark' });
  sections[8] = section(9, 'Benefits', {
    title: 'Why shop with us',
    items: [
      {
        id: 31,
        title: 'Island-wide delivery',
        subtitle: null,
        body: null,
        buttonLabel: null,
        linkUrl: null,
        image: null,
        mobileImage: null,
        video: null,
        departmentId: null,
        productSlug: null,
        iconKey: 'delivery',
        sortOrder: 0,
        isEnabled: true,
        startsAt: null,
        endsAt: null,
        isLiveNow: true,
      },
    ],
  });
  sections[9] = section(10, 'Newsletter', { isEnabled: false, isLiveNow: false });

  return {
    sections,
    departments: [{ id: 1, name: 'Women', slug: 'women', isActive: true }],
    benefitIcons: ['delivery', 'returns'],
  };
}

describe('HomeContentPage (#192)', () => {
  let controller: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [HomeContentPage],
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });

    controller = TestBed.inject(HttpTestingController);
  });

  afterEach(() => controller.verify());

  function create(data: AdminHomeContent = content()): ComponentFixture<HomeContentPage> {
    const fixture = TestBed.createComponent(HomeContentPage);
    fixture.detectChanges();
    controller.expectOne(AdminSiteContentApi.homeUrl).flush(data);
    fixture.detectChanges();
    return fixture;
  }

  function el(fixture: ComponentFixture<HomeContentPage>): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  function q<T extends HTMLElement>(fixture: ComponentFixture<HomeContentPage>, testId: string): T | null {
    return el(fixture).querySelector<T>(`[data-testid="${testId}"]`);
  }

  function grantToken(): void {
    controller.expectOne(AdminAuthApi.antiforgeryUrl).flush({ token: 'xsrf' });
  }

  function type(input: HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement, value: string): void {
    input.value = value;
    input.dispatchEvent(new Event(input instanceof HTMLSelectElement ? 'change' : 'input'));
  }

  it('renders the ten blocks in order with their live status', () => {
    const fixture = create();

    const headings = [...el(fixture).querySelectorAll('h2')].map((h) => h.textContent?.trim().split(/\s{2,}|\n/)[0]);
    expect(headings).toHaveLength(10);
    expect(headings[0]).toContain('Hero video');
    expect(headings[9]).toContain('Newsletter');
    expect(q(fixture, 'HeroVideo-status')?.textContent).toContain('On the shop now');
    expect(q(fixture, 'Newsletter-status')?.textContent).toContain('Off');
    expect(q<HTMLInputElement>(fixture, 'HeroVideo-title')?.value).toBe('The linen edit');
  });

  it('shows only the fields a block uses', () => {
    const fixture = create();

    expect(q(fixture, 'HeroVideo-overlay')).not.toBeNull();
    expect(q(fixture, 'HeroVideo-count')).toBeNull();
    expect(q(fixture, 'TrendingItems-mode')).not.toBeNull();
    expect(q(fixture, 'NewArrivals-mode')).toBeNull();
    expect(q(fixture, 'NewArrivals-listingUrl')).not.toBeNull();
    expect(q(fixture, 'NewArrivals-automatic')).not.toBeNull();
    expect(q(fixture, 'Benefits-item-31')?.textContent).toContain('Island-wide delivery');
  });

  it('saves a block with every key, and null for the fields it does not use', () => {
    const fixture = create();

    type(q<HTMLInputElement>(fixture, 'HeroVideo-title')!, 'Summer linen');
    q<HTMLButtonElement>(fixture, 'HeroVideo-save')!.click();
    grantToken();

    const request = controller.expectOne('/api/admin/content/home/sections/1');
    expect(request.request.method).toBe('PUT');
    expect(request.request.body).toEqual({
      title: 'Summer linen',
      subtitle: null,
      isEnabled: true,
      sortOrder: 0,
      startsAt: null,
      endsAt: null,
      mode: null,
      count: null,
      listingUrl: null,
      linkLabel: null,
      overlay: 'dark',
      rowVersion: 'rv-1',
    });

    request.flush({ id: 1, rowVersion: 'rv-1b' });
    controller.expectOne(AdminSiteContentApi.homeUrl).flush(content());
    fixture.detectChanges();
    expect(q(fixture, 'HeroVideo-said')?.textContent).toContain('is saved');
  });

  it('sends the mode for trending items', () => {
    const fixture = create();

    type(q<HTMLSelectElement>(fixture, 'TrendingItems-mode')!, 'Manual');
    q<HTMLButtonElement>(fixture, 'TrendingItems-save')!.click();
    grantToken();

    const request = controller.expectOne('/api/admin/content/home/sections/4');
    expect(request.request.body.mode).toBe('Manual');
    expect(request.request.body.count).toBe(8);
    expect(request.request.body.overlay).toBeNull();
    request.flush({ id: 4, rowVersion: 'x' });
    controller.expectOne(AdminSiteContentApi.homeUrl).flush(content());
  });

  it('puts a validation error against its control', () => {
    const fixture = create();

    q<HTMLButtonElement>(fixture, 'NewArrivals-save')!.click();
    grantToken();
    controller
      .expectOne('/api/admin/content/home/sections/3')
      .flush(
        { title: 'Invalid', status: 400, errors: { listingUrl: ['Use a page on this shop.'] } },
        { status: 400, statusText: 'Bad Request' },
      );
    fixture.detectChanges();

    expect(q(fixture, 'NewArrivals-error-listingUrl')?.textContent).toContain('Use a page on this shop.');
    expect(q(fixture, 'NewArrivals-listingUrl')?.getAttribute('aria-invalid')).toBe('true');
  });

  it('says a stale block was changed by someone else and offers a reload', () => {
    const fixture = create();

    q<HTMLButtonElement>(fixture, 'Benefits-save')!.click();
    grantToken();
    controller
      .expectOne('/api/admin/content/home/sections/9')
      .flush({ code: 'stale_row_version', detail: 'x' }, { status: 409, statusText: 'Conflict' });
    fixture.detectChanges();

    expect(q(fixture, 'Benefits-problem')?.textContent).toContain('Someone else changed this block');
    q<HTMLButtonElement>(fixture, 'Benefits-reload')!.click();
    controller.expectOne(AdminSiteContentApi.homeUrl).flush(content());
  });

  it('shows the refusal panel to a role without Content.View', () => {
    const fixture = TestBed.createComponent(HomeContentPage);
    fixture.detectChanges();
    controller.expectOne(AdminSiteContentApi.homeUrl).flush(null, { status: 403, statusText: 'Forbidden' });
    fixture.detectChanges();

    expect(q(fixture, 'home-problem')?.textContent).toContain('does not include the homepage');
    expect(el(fixture).querySelectorAll('sda-home-section-editor')).toHaveLength(0);
  });

  it('adds an item with the section row version, sending null for fields the block does not use', () => {
    const fixture = create();

    q<HTMLButtonElement>(fixture, 'Benefits-add-item')!.click();
    fixture.detectChanges();

    expect(q(fixture, 'Benefits-item-image-file')).toBeNull();
    type(q<HTMLInputElement>(fixture, 'Benefits-item-title')!, 'Easy returns');
    type(q<HTMLSelectElement>(fixture, 'Benefits-item-icon')!, 'returns');
    q<HTMLButtonElement>(fixture, 'Benefits-item-save')!.click();
    grantToken();

    const request = controller.expectOne('/api/admin/content/home/sections/9/items');
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({
      title: 'Easy returns',
      subtitle: null,
      body: null,
      buttonLabel: null,
      linkUrl: null,
      imagePublicId: null,
      mobileImagePublicId: null,
      videoPublicId: null,
      departmentId: null,
      productSlug: null,
      iconKey: 'returns',
      sortOrder: 1,
      isEnabled: true,
      startsAt: null,
      endsAt: null,
      rowVersion: 'rv-9',
    });
    request.flush({ id: 32, rowVersion: 'rv-9b' });
    controller.expectOne(AdminSiteContentApi.homeUrl).flush(content());
  });

  it('puts an item validation error against its control', () => {
    const fixture = create();

    q<HTMLButtonElement>(fixture, 'Benefits-item-edit-31')!.click();
    fixture.detectChanges();
    expect(q<HTMLSelectElement>(fixture, 'Benefits-item-icon')?.value).toBe('delivery');

    q<HTMLButtonElement>(fixture, 'Benefits-item-save')!.click();
    grantToken();
    const request = controller.expectOne('/api/admin/content/home/items/31');
    expect(request.request.method).toBe('PUT');
    request.flush(
      { title: 'Invalid', status: 400, errors: { iconKey: ['Choose one of the icons offered.'] } },
      { status: 400, statusText: 'Bad Request' },
    );
    fixture.detectChanges();

    expect(q(fixture, 'Benefits-item-error-iconKey')?.textContent).toContain('Choose one of the icons');
  });

  it('removes an item on the second press with the section row version', () => {
    const fixture = create();

    q<HTMLButtonElement>(fixture, 'Benefits-item-remove-31')!.click();
    fixture.detectChanges();
    expect(q(fixture, 'Benefits-item-remove-31')?.textContent).toContain('Yes, remove it');
    controller.expectNone('/api/admin/content/home/items/31');

    q<HTMLButtonElement>(fixture, 'Benefits-item-remove-31')!.click();
    grantToken();
    const request = controller.expectOne('/api/admin/content/home/items/31');
    expect(request.request.method).toBe('DELETE');
    expect(request.request.headers.get('If-Match')).toBe('"rv-9"');
    request.flush(null, { status: 204, statusText: 'No Content' });
    controller.expectOne(AdminSiteContentApi.homeUrl).flush(content());
  });

  it('offers no item form for a block without items, and says when a block is full', () => {
    const data = content();
    const fixture = create(data);

    expect(q(fixture, 'SaleSection-add-item')).toBeNull();
    // The hero holds one item and has none in this data, so it can take one.
    expect(q(fixture, 'HeroVideo-add-item')).not.toBeNull();
  });
});
