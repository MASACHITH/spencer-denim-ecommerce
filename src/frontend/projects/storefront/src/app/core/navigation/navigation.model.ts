/** A single destination in the navigation. `path` is an absolute storefront route. */
export interface NavLink {
  readonly label: string;
  readonly path: string;
}

/**
 * One category in a mega-menu column.
 *
 * A category that has children of its own titles the group, with the children listed under it. The
 * title is a paragraph and the child list is `aria-labelledby` it, **not** a heading: the mega menu
 * sits ahead of the page's own `h1` in the DOM, so headings here were read out before the page said
 * what it was (#76, #20 SH11). A category with no children is simply a link in the column.
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
 * Where a promo card goes, already vetted (#215). A path on this site goes through the router, its query
 * string split off; an admin-set http(s) address is an ordinary anchor that opens in a new tab.
 */
export type MegaMenuPromoTarget =
  | {
      readonly kind: 'internal';
      readonly path: string;
      readonly queryParams: Readonly<Record<string, string | string[]>> | null;
    }
  | { readonly kind: 'external'; readonly href: string };

/**
 * The promo card slot at the end of a mega menu (GLB-05).
 *
 * Since #215 it is the first admin-managed card the API sends for the department; without one it is the card the
 * menu has always drawn from the department itself ("New in …", its description and picture, "Shop now" to New
 * Arrivals). `imageUrl` is `null` until that picture exists; the card then shows a neutral placeholder rather
 * than a broken image.
 */
export interface MegaMenuPromo {
  readonly title: string;
  readonly caption: string;
  readonly cta: { readonly label: string; readonly target: MegaMenuPromoTarget };
  readonly imageUrl: string | null;
  /** The candidate list for {@link imageUrl}; `null` for an asset with no renditions. */
  readonly imageSrcSet: string | null;
  /**
   * The picture's alt text: the media library's description for an admin card, and empty — decorative, the
   * card's own words name the link — for the department fallback, exactly as before.
   */
  readonly imageAlt: string;
}

/**
 * A promo card with the window it may be drawn in, in epoch milliseconds (`null` for open-ended). The department
 * fallback is always last and has no window, so a department's list always draws something (#215).
 */
export interface MegaMenuPromoCandidate {
  readonly promo: MegaMenuPromo;
  readonly startsAt: number | null;
  readonly endsAt: number | null;
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
  /**
   * Every card that could be drawn, best first, ending with the department fallback; empty without a menu. The
   * desktop menu picks the first whose window includes the moment it opens (`promoAt`), so a long-lived page does
   * not keep showing a card past its end.
   */
  readonly promos: readonly MegaMenuPromoCandidate[];
}

/** The first candidate live at `now` (epoch ms), or `null` when there are none. */
export function promoAt(promos: readonly MegaMenuPromoCandidate[], now: number): MegaMenuPromo | null {
  const live = promos.find(
    (candidate) =>
      (candidate.startsAt === null || candidate.startsAt <= now) && (candidate.endsAt === null || candidate.endsAt > now),
  );

  return live?.promo ?? null;
}

/** Whether this entry opens a mega menu (desktop) or an expandable section (mobile drawer). */
export function hasMenu(item: NavItem): boolean {
  return item.columns.length > 0;
}
