import { CategoryNodeDto, DepartmentDto } from './catalog-departments.model';
import { MegaMenuColumn, MegaMenuGroup, MegaMenuPromo, NavItem, NavLink } from './navigation.model';

/** The desktop mega menu lays categories out in at most four columns (design/style-tile §06). */
export const MEGA_MENU_COLUMN_COUNT = 4;

/**
 * The two entries that are not departments. GLB-05 fixes the main navigation at exactly
 * `Women · Men · Kids · New Arrivals · Sale`, and the catalogue API only knows the first three.
 */
export const STATIC_NAV_ITEMS: readonly NavItem[] = [
  {
    id: 'new-arrivals',
    label: 'New Arrivals',
    path: '/new-arrivals',
    tone: 'default',
    shopAll: null,
    columns: [],
    promo: null,
  },
  {
    id: 'sale',
    label: 'Sale',
    path: '/sale',
    tone: 'sale',
    shopAll: null,
    columns: [],
    promo: null,
  },
];

/**
 * Turns the catalogue API payload into the five main navigation entries.
 *
 * A pure function on purpose: it is the only thing that has to keep working when
 * `NavigationService` stops using placeholder data and starts using the live API (W1-02).
 * Departments with no menu-visible categories still appear, as plain links.
 */
export function toNavigation(departments: readonly DepartmentDto[]): readonly NavItem[] {
  return [...departments.map(toNavItem), ...STATIC_NAV_ITEMS];
}

function toNavItem(department: DepartmentDto): NavItem {
  const path = `/${department.slug}`;
  const visible = department.categories.filter((category) => category.showInMegaMenu);
  const columns = toColumns(department, visible);

  return {
    id: department.slug,
    label: department.name,
    path,
    tone: 'default',
    // A link of its own above the columns, so the department page stays one click away even
    // when a shopper opens the menu instead of following the department link.
    shopAll: columns.length > 0 ? { label: `Shop all ${department.name}`, path } : null,
    columns,
    promo: columns.length > 0 ? toPromo(department) : null,
  };
}

function toColumns(
  department: DepartmentDto,
  categories: readonly CategoryNodeDto[],
): readonly MegaMenuColumn[] {
  if (categories.length === 0) {
    return [];
  }

  const buckets: MegaMenuGroup[][] = Array.from({ length: MEGA_MENU_COLUMN_COUNT }, () => []);
  const departmentPath = `/${department.slug}`;

  categories.forEach((category, index) => {
    const column = columnIndex(category, index, categories.length);
    buckets[column].push(toGroup(category, departmentPath));
  });

  return buckets
    .map((groups, index) => ({ key: `column-${index + 1}`, groups }))
    .filter((column) => column.groups.length > 0);
}

/**
 * Admin decides the column (`megaMenuColumn`, 1–4). Until it is set the categories are spread
 * evenly, so the menu is never a single long list.
 */
function columnIndex(category: CategoryNodeDto, index: number, total: number): number {
  const assigned = category.megaMenuColumn;
  if (assigned !== null && assigned >= 1 && assigned <= MEGA_MENU_COLUMN_COUNT) {
    return assigned - 1;
  }

  return Math.min(
    MEGA_MENU_COLUMN_COUNT - 1,
    Math.floor((index * MEGA_MENU_COLUMN_COUNT) / Math.max(total, 1)),
  );
}

function toGroup(category: CategoryNodeDto, parentPath: string): MegaMenuGroup {
  const path = `${parentPath}/${category.slug}`;

  return {
    link: { label: category.name, path },
    children: category.children
      .filter((child) => child.showInMegaMenu)
      .map((child) => ({ label: child.name, path: `${path}/${child.slug}` })),
  };
}

/** TODO(ADM-17): the promo card's image, headline and link become admin-managed content. */
function toPromo(department: DepartmentDto): MegaMenuPromo {
  return {
    title: `New in ${department.name}`,
    caption: department.description ?? '',
    cta: { label: 'Shop now', path: '/new-arrivals' } satisfies NavLink,
    imageUrl: department.heroImageUrl,
  };
}
