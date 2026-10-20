import { AdminMediaRef } from './admin-site-content.model';

/**
 * `/api/admin/content/mega-menu` (GLB-05, ADM-17; issue #215): the picture cards at the end of each department's
 * desktop mega menu.
 *
 * A hand-written copy of the API's contracts, on #192's rules: every request type lists **every** key the server
 * reads, so a missing key is a compile error rather than a 400; schedules are the shop's own wall-clock time,
 * `yyyy-MM-ddTHH:mm` with no offset.
 */

export interface AdminMegaMenuCard {
  readonly id: number;
  readonly departmentId: number;
  readonly title: string;
  readonly subtitle: string | null;
  readonly buttonLabel: string;
  readonly linkUrl: string;
  readonly image: AdminMediaRef | null;
  readonly sortOrder: number;
  readonly isActive: boolean;
  readonly startsAt: string | null;
  readonly endsAt: string | null;
  /** Switched on and inside its schedule. */
  readonly isLiveNow: boolean;
  /** The one card the shop's menu draws for this department right now. */
  readonly isShownNow: boolean;
  readonly rowVersion: string;
}

export interface AdminMegaMenuDepartment {
  readonly id: number;
  readonly name: string;
  readonly slug: string;
  readonly isActive: boolean;
  /** What the menu shows under the heading when no card is on. */
  readonly description: string | null;
  /** The picture the menu shows when no card is on. */
  readonly heroImage: AdminMediaRef | null;
  readonly cards: readonly AdminMegaMenuCard[];
}

export interface AdminMegaMenuContent {
  readonly departments: readonly AdminMegaMenuDepartment[];
  readonly maxCardsPerDepartment: number;
}

/** POST omits `rowVersion`; PUT carries it. */
export interface MegaMenuCardRequest {
  readonly departmentId: number | null;
  readonly title: string | null;
  readonly subtitle: string | null;
  readonly buttonLabel: string | null;
  readonly linkUrl: string | null;
  readonly imagePublicId: string | null;
  readonly sortOrder: number | null;
  readonly isActive: boolean;
  readonly startsAt: string | null;
  readonly endsAt: string | null;
}

export interface MegaMenuCardSaved {
  readonly id: number;
  readonly rowVersion: string | null;
}

/** The server's column widths (doc 08 §3.7), so the boxes stop where the API would refuse. */
export const MEGA_MENU_CARD_LIMITS = {
  title: 100,
  subtitle: 200,
  buttonLabel: 40,
  linkUrl: 500,
} as const;

/** "On the menu now" / "Waiting its turn" / "Scheduled" / "Off" for one card. */
export function cardStatus(card: Pick<AdminMegaMenuCard, 'isActive' | 'isLiveNow' | 'isShownNow'>): string {
  if (card.isShownNow) {
    return 'On the menu now';
  }

  if (card.isLiveNow) {
    // On, in its window, and behind an earlier card (or without a usable picture).
    return 'Waiting its turn';
  }

  return card.isActive ? 'Scheduled' : 'Off';
}
