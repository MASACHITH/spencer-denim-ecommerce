import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { DepartmentDto } from './catalog-departments.model';
import { MEGA_MENU_COLUMN_COUNT, toNavigation } from './navigation.mapper';
import { NavItem, hasMenu } from './navigation.model';
import { NavigationService } from './navigation.service';
import { PLACEHOLDER_DEPARTMENTS } from './navigation.data';

function department(overrides: Partial<DepartmentDto> = {}): DepartmentDto {
  return {
    id: 1,
    name: 'Women',
    slug: 'women',
    description: null,
    heroImageUrl: null,
    seoTitle: null,
    seoDescription: null,
    categories: [],
    ...overrides,
  };
}

function category(
  name: string,
  slug: string,
  overrides: Partial<DepartmentDto['categories'][number]> = {},
) {
  return {
    id: 1,
    name,
    slug,
    depth: 1,
    description: null,
    imageUrl: null,
    showInMegaMenu: true,
    megaMenuColumn: null,
    ageGroupSlug: null,
    children: [],
    ...overrides,
  };
}

function find(items: readonly NavItem[], id: string): NavItem {
  const item = items.find((candidate) => candidate.id === id);
  if (!item) {
    throw new Error(`No navigation item "${id}"`);
  }
  return item;
}

function pathsOf(item: NavItem): string[] {
  return item.columns.flatMap((column) =>
    column.groups.flatMap((group) => [
      group.link.path,
      ...group.children.map((child) => child.path),
    ]),
  );
}

describe('NavigationService', () => {
  let service: NavigationService;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(NavigationService);
  });

  it('lists exactly the five main entries, in order (GLB-05)', () => {
    expect(service.items().map((item) => item.label)).toEqual([
      'Women',
      'Men',
      'Kids',
      'New Arrivals',
      'Sale',
    ]);
  });

  it('gives each entry a route', () => {
    expect(service.items().map((item) => item.path)).toEqual([
      '/women',
      '/men',
      '/kids',
      '/new-arrivals',
      '/sale',
    ]);
  });

  it('opens a mega menu for departments only', () => {
    expect(service.itemsWithMenu().map((item) => item.id)).toEqual(['women', 'men', 'kids']);
    expect(hasMenu(find(service.items(), 'new-arrivals'))).toBe(false);
    expect(hasMenu(find(service.items(), 'sale'))).toBe(false);
  });

  it('marks Sale, and only Sale, with the sale tone', () => {
    expect(service.items().filter((item) => item.tone === 'sale').map((item) => item.id)).toEqual([
      'sale',
    ]);
  });

  it('offers "Shop all" as its own link, not as one of the categories', () => {
    const women = find(service.items(), 'women');

    expect(women.shopAll).toEqual({ label: 'Shop all Women', path: '/women' });
    expect(pathsOf(women).filter((path) => path === '/women')).toHaveLength(0);
  });

  it('builds category routes under the department', () => {
    const women = find(service.items(), 'women');

    expect(pathsOf(women)).toContain('/women/dresses');
    expect(pathsOf(women)).toContain('/women/accessories');
  });

  it('heads a group with a category that has children, and leaves leaf columns untitled', () => {
    const kids = find(service.items(), 'kids');
    const groups = kids.columns.flatMap((column) => column.groups);

    const girls = groups.find((group) => group.link.label === 'Girls');
    expect(girls?.children.map((child) => child.label)).toEqual([
      'Girls Dresses',
      'Girls Tops',
      'Girls Bottoms',
    ]);

    // Women is a flat department: every group is a leaf, so no headings are drawn.
    const women = find(service.items(), 'women');
    expect(
      women.columns.flatMap((column) => column.groups).every((group) => group.children.length === 0),
    ).toBe(true);
  });

  it('nests a child category under its parent category', () => {
    const kids = find(service.items(), 'kids');

    expect(pathsOf(kids)).toContain('/kids/girls');
    expect(pathsOf(kids)).toContain('/kids/girls/girls-dresses');
  });

  it('gives every department a promo card slot', () => {
    for (const item of service.itemsWithMenu()) {
      expect(item.promo).not.toBeNull();
      expect(item.promo?.cta.path.length).toBeGreaterThan(0);
    }
  });

  it('never builds more than four category columns', () => {
    for (const item of service.itemsWithMenu()) {
      expect(item.columns.length).toBeLessThanOrEqual(MEGA_MENU_COLUMN_COUNT);
    }
  });
});

describe('toNavigation', () => {
  it('honours the column admin assigned', () => {
    const items = toNavigation([
      department({
        categories: [
          category('Dresses', 'dresses', { megaMenuColumn: 3 }),
          category('Jeans', 'jeans', { megaMenuColumn: 3 }),
        ],
      }),
    ]);

    const women = find(items, 'women');
    // Both categories sit in column 3; the empty columns are dropped rather than drawn blank.
    expect(women.columns.map((column) => column.key)).toEqual(['column-3']);
    expect(women.columns[0].groups.map((group) => group.link.label)).toEqual(['Dresses', 'Jeans']);
  });

  it('spreads categories across the columns when no column is assigned yet', () => {
    const items = toNavigation([
      department({
        categories: [
          category('A', 'a'),
          category('B', 'b'),
          category('C', 'c'),
          category('D', 'd'),
        ],
      }),
    ]);

    expect(find(items, 'women').columns).toHaveLength(4);
  });

  it('leaves out categories that are not marked for the mega menu', () => {
    const items = toNavigation([
      department({
        categories: [
          category('Dresses', 'dresses'),
          category('Staff only', 'staff-only', { showInMegaMenu: false }),
        ],
      }),
    ]);

    expect(pathsOf(find(items, 'women'))).not.toContain('/women/staff-only');
  });

  it('keeps a department with no categories as a plain link', () => {
    const items = toNavigation([department({ categories: [] })]);

    const women = find(items, 'women');
    expect(women.columns).toHaveLength(0);
    expect(women.promo).toBeNull();
    expect(women.shopAll).toBeNull();
    expect(women.path).toBe('/women');
  });

  it('still returns New Arrivals and Sale when the API sends nothing', () => {
    expect(toNavigation([]).map((item) => item.id)).toEqual(['new-arrivals', 'sale']);
  });
});

/**
 * The departments now come from `GET /api/catalog/departments`. The header is on every page, so
 * what matters as much as the happy path is that nothing about it can empty the menu (lesson L1).
 */
describe('NavigationService and the catalogue API', () => {
  let http: HttpTestingController;
  let service: NavigationService;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(NavigationService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('asks the catalogue for the departments, once', () => {
    service.items();

    http.expectOne('/api/catalog/departments').flush([]);
  });

  it('builds the menu from what the API sends', () => {
    const request = http.expectOne('/api/catalog/departments');
    request.flush([
      department({
        id: 9,
        name: 'Womenswear',
        slug: 'womenswear',
        categories: [category('Jumpsuits', 'jumpsuits')],
      }),
    ]);

    expect(service.items().map((item) => item.label)).toEqual([
      'Womenswear',
      'New Arrivals',
      'Sale',
    ]);
    expect(pathsOf(find(service.items(), 'womenswear'))).toContain('/womenswear/jumpsuits');
  });

  it('shows the seeded menu until the API answers, so SSR never renders an empty header', () => {
    expect(service.items().map((item) => item.label)).toEqual([
      'Women',
      'Men',
      'Kids',
      'New Arrivals',
      'Sale',
    ]);

    http.expectOne('/api/catalog/departments').flush([]);
  });

  it('falls back to the seeded menu when the API fails', () => {
    http
      .expectOne('/api/catalog/departments')
      .flush('boom', { status: 500, statusText: 'Server Error' });

    expect(service.departments()).toEqual(PLACEHOLDER_DEPARTMENTS);
    expect(service.items().map((item) => item.id)).toEqual([
      'women',
      'men',
      'kids',
      'new-arrivals',
      'sale',
    ]);
  });

  it('falls back when the API is unreachable', () => {
    http
      .expectOne('/api/catalog/departments')
      .error(new ProgressEvent('offline'));

    expect(service.departments()).toEqual(PLACEHOLDER_DEPARTMENTS);
  });

  it('falls back rather than draw a menu with no departments in it', () => {
    http.expectOne('/api/catalog/departments').flush([]);

    expect(service.departments()).toEqual(PLACEHOLDER_DEPARTMENTS);
  });
});
