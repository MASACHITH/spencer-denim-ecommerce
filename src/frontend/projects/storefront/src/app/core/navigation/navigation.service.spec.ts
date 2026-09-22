import { TestBed } from '@angular/core/testing';

import { DepartmentDto } from './catalog-departments.model';
import { MEGA_MENU_COLUMN_COUNT, toNavigation } from './navigation.mapper';
import { NavItem, hasMenu } from './navigation.model';
import { NavigationService } from './navigation.service';

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
    TestBed.configureTestingModule({});
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

  it('puts "Shop all" first and builds category routes under the department', () => {
    const women = find(service.items(), 'women');

    expect(women.columns[0].groups[0].link).toEqual({
      label: 'Shop all Women',
      path: '/women',
    });
    expect(pathsOf(women)).toContain('/women/dresses');
    expect(pathsOf(women)).toContain('/women/accessories');
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
    // Column 1 holds "Shop all", column 3 holds both categories; column 2 is dropped as empty.
    expect(women.columns.map((column) => column.key)).toEqual(['column-1', 'column-3']);
    expect(women.columns[1].groups.map((group) => group.link.label)).toEqual(['Dresses', 'Jeans']);
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
    expect(women.path).toBe('/women');
  });

  it('still returns New Arrivals and Sale when the API sends nothing', () => {
    expect(toNavigation([]).map((item) => item.id)).toEqual(['new-arrivals', 'sale']);
  });
});
