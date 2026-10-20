import { toLink } from '../content/home-sections';
import { CategoryNodeDto, DepartmentDto, MegaMenuPromoCardDto } from './catalog-departments.model';
import {
  MegaMenuColumn,
  MegaMenuGroup,
  MegaMenuPromo,
  MegaMenuPromoCandidate,
  NavItem,
  promoAt,
} from './navigation.model';

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
    promos: [],
  },
  {
    id: 'sale',
    label: 'Sale',
    path: '/sale',
    tone: 'sale',
    shopAll: null,
    columns: [],
    promo: null,
    promos: [],
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
    promos: columns.length > 0 ? toPromos(department) : [],
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

/**
 * The department's promo card (GLB-05, ADM-17; #215): the first admin-managed card the API sends that can
 * actually be drawn, or — when there is none — the card the menu has always drawn from the department itself.
 *
 * The fallback is today's card to the character, so a fresh database (whose seeded cards are switched off) looks
 * exactly as it did before #215.
 */
export function toPromo(department: DepartmentDto, now: number = Date.now()): MegaMenuPromo {
  // Never null: the fallback is always the last candidate and has no window.
  return promoAt(toPromos(department), now)!;
}

/**
 * Every drawable admin card with its window, best first, then the department fallback. The window is re-checked
 * where the menu opens (`promoAt`): the feed only sends cards that were live when it answered, but the tree is
 * kept for the life of the page.
 */
export function toPromos(department: DepartmentDto): readonly MegaMenuPromoCandidate[] {
  const cards = Array.isArray(department.promoCards) ? department.promoCards : [];
  const candidates: MegaMenuPromoCandidate[] = [];

  for (const card of cards) {
    const promo = fromCard(card);
    if (promo !== null) {
      candidates.push({ promo, startsAt: instant(card.startsAtUtc), endsAt: instant(card.endsAtUtc) });
    }
  }

  candidates.push({ promo: fallbackPromo(department), startsAt: null, endsAt: null });
  return candidates;
}

/** An ISO instant as epoch ms; missing or unreadable is open-ended. */
function instant(value: string | null | undefined): number | null {
  if (typeof value !== 'string' || value.length === 0) {
    return null;
  }

  const parsed = Date.parse(value);
  return Number.isNaN(parsed) ? null : parsed;
}

/** The card the menu has always drawn from the department itself. */
function fallbackPromo(department: DepartmentDto): MegaMenuPromo {
  return {
    title: `New in ${department.name}`,
    caption: department.description ?? '',
    cta: { label: 'Shop now', target: { kind: 'internal', path: '/new-arrivals', queryParams: null } },
    imageUrl: department.heroImageUrl,
    imageSrcSet: department.heroImageSrcSet,
    imageAlt: '',
  };
}

/**
 * An admin card as the menu draws it, or `null` for one it cannot draw: a blank heading or button, a missing
 * picture, or a link that is neither a path on this site nor an http(s) address. The API already refuses each of
 * these on save; this is what stops a row written some other way (a script, a restore) reaching the page, and the
 * next card — or the fallback — is drawn instead.
 */
function fromCard(card: MegaMenuPromoCardDto | null | undefined): MegaMenuPromo | null {
  if (card === null || typeof card !== 'object') {
    return null;
  }

  const title = typeof card.title === 'string' ? card.title.trim() : '';
  const label = typeof card.buttonLabel === 'string' ? card.buttonLabel.trim() : '';
  const imageUrl = typeof card.imageUrl === 'string' && card.imageUrl.length > 0 ? card.imageUrl : null;
  const target = toLink(typeof card.linkUrl === 'string' ? card.linkUrl : null);

  if (title.length === 0 || label.length === 0 || imageUrl === null || target === null) {
    return null;
  }

  return {
    title,
    caption: typeof card.subtitle === 'string' ? card.subtitle.trim() : '',
    cta: { label, target },
    imageUrl,
    imageSrcSet: typeof card.imageSrcSet === 'string' && card.imageSrcSet.length > 0 ? card.imageSrcSet : null,
    imageAlt: typeof card.imageAlt === 'string' ? card.imageAlt.trim() : '',
  };
}
