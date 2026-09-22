import { Injectable, Signal, computed, signal } from '@angular/core';

/** One message in the rotating announcement bar (GLB-03). */
export interface AnnouncementMessage {
  readonly id: string;
  readonly text: string;
}

/**
 * Placeholder copy. The free-delivery threshold, the returns window and the campaign line are all
 * examples: docs/04 §4 decision 8 has the client confirming the real values before go-live.
 */
export const PLACEHOLDER_ANNOUNCEMENTS: readonly AnnouncementMessage[] = [
  { id: 'delivery', text: 'Free island-wide delivery on orders over Rs. 10,000' },
  { id: 'returns', text: 'Easy 14-day returns and exchanges on every order' },
  { id: 'new-collection', text: 'New season linen has arrived. Shop the edit' },
];

/**
 * The announcement messages (GLB-03).
 *
 * TODO(ADM-17): replace the placeholder list with the admin-managed messages endpoint. Only the
 * `source` signal below changes; the bar component reads `messages` either way.
 */
@Injectable({ providedIn: 'root' })
export class AnnouncementService {
  private readonly source = signal<readonly AnnouncementMessage[]>(PLACEHOLDER_ANNOUNCEMENTS);

  /**
   * Blank messages are dropped rather than rendered as an empty bar (lesson L1): once these are
   * typed in admin, an accidentally empty row must not leave a blank strip across every page.
   */
  readonly messages: Signal<readonly AnnouncementMessage[]> = computed(() =>
    this.source().filter((message) => message.text.trim().length > 0),
  );
}
