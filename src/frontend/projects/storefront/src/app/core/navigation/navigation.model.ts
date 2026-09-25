/** A single destination in the navigation. `path` is an absolute storefront route. */
export interface NavLink {
  readonly label: string;
  readonly path: string;
}

/**
 * One category in a mega-menu column.
 *
 * A category that has children of its own heads the group as a heading, with the children listed
 * under it. A category with no children is simply a link in the column, and no empty heading is
 * drawn for it.
 */
export interface MegaMenuGroup {
  readonly link: NavLink;
  readonly children: readonly NavLink[];
}

/** One column of the desktop mega menu. */
export interface MegaMenuColumn {
  readonly key: string;
  readonly groups: readonly MegaMenuGroup[];
}

/**
 * The promo card slot at the end of a mega menu (GLB-05).
 * `imageUrl` is `null` until an image is uploaded in admin; the card then shows a neutral
 * placeholder rather than a broken image.
 */
export interface MegaMenuPromo {
  readonly title: string;
  readonly caption: string;
  readonly cta: NavLink;
  readonly imageUrl: string | null;
  /** The candidate list for {@link imageUrl}; `null` for an asset with no renditions. */
  readonly imageSrcSet: string | null;
}

/** `sale` paints the label in the sale colour; nothing else changes. */
export type NavTone = 'default' | 'sale';

/**
 * One of the five main navigation entries (GLB-05).
 * Departments carry a mega menu; New Arrivals and Sale are plain links, so `columns` is empty.
 */
export interface NavItem {
  readonly id: string;
  readonly label: string;
  readonly path: string;
  readonly tone: NavTone;
  /** "Shop all Women": its own link above the columns, not one entry among the categories. */
  readonly shopAll: NavLink | null;
  readonly columns: readonly MegaMenuColumn[];
  readonly promo: MegaMenuPromo | null;
}

/** Whether this entry opens a mega menu (desktop) or an expandable section (mobile drawer). */
export function hasMenu(item: NavItem): boolean {
  return item.columns.length > 0;
}
