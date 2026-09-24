import { Injectable, Signal, computed, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { Observable, catchError, map, of, tap, timeout } from 'rxjs';

import { ContentApi } from '../content/content-api';
import { AnnouncementDto } from '../content/content.model';
import { ServerHandover } from '../platform/server-handover';

/** One message in the rotating announcement bar (GLB-03). */
export interface AnnouncementMessage {
  readonly id: string;
  readonly text: string;
  /** Where the line goes, when the client gave it somewhere to go. Site-relative only. */
  readonly linkUrl: string | null;
}

/**
 * What the strip shows when the content service cannot be reached.
 *
 * These are the two lines doc 08 §9 seeds, plus the campaign line the style tile used, and all
 * three are placeholders the client replaces before go-live (docs/04 §4 decision 8). The strip is
 * on every page of the site, so it is the last thing that may break: a shopper should never meet a
 * blank band where the delivery promise lives.
 */
export const PLACEHOLDER_ANNOUNCEMENTS: readonly AnnouncementMessage[] = [
  { id: 'delivery', text: 'Free island-wide delivery on orders over Rs. 10,000', linkUrl: null },
  { id: 'returns', text: 'Easy 14-day returns and exchanges on every order', linkUrl: null },
  { id: 'new-collection', text: 'New season linen has arrived. Shop the edit', linkUrl: null },
];

/**
 * How long the strip waits before it falls back. The header is on every page, so a slow content
 * feed would otherwise be a slow site — the same lesson the department menu learned in W2-03,
 * where `catchError` could not help because an answer that never comes is not a failure.
 */
export const ANNOUNCEMENTS_TIMEOUT_MS = 2_000;

/** Where the server leaves the strip for the browser. */
const ANNOUNCEMENTS_HANDOVER = 'content:announcements';

/**
 * The announcement messages (GLB-03), from `GET /api/content/announcements`.
 *
 * They are the client's copy, so they come from `content.AnnouncementMessages` and not from a
 * constant in a component. The fetch runs during server-side rendering and the result is handed to
 * the browser with the page, so the served HTML already carries the real strip and hydration does
 * not swap it for the fallback.
 */
@Injectable({ providedIn: 'root' })
export class AnnouncementService {
  private readonly api = inject(ContentApi);
  private readonly handover = inject(ServerHandover);

  private readonly source: Signal<readonly AnnouncementMessage[]> = toSignal(this.load(), {
    initialValue: PLACEHOLDER_ANNOUNCEMENTS,
  });

  /**
   * Blank messages are dropped rather than rendered as an empty bar (lesson L1). The API drops
   * them too, but this is the reader every component passes through, and the defensive read
   * belongs where every reader passes — not on the one caller you were looking at.
   */
  readonly messages: Signal<readonly AnnouncementMessage[]> = computed(() =>
    this.source().filter((message) => message.text.trim().length > 0),
  );

  private load(): Observable<readonly AnnouncementMessage[]> {
    const handedOver = this.handover.take<readonly AnnouncementMessage[]>(ANNOUNCEMENTS_HANDOVER);
    if (handedOver) {
      return of(handedOver);
    }

    return this.api.announcements().pipe(
      map(toMessages),
      tap((messages) => this.handover.give(ANNOUNCEMENTS_HANDOVER, messages)),
      timeout(ANNOUNCEMENTS_TIMEOUT_MS),
      catchError(() => of(PLACEHOLDER_ANNOUNCEMENTS)),
    );
  }
}

/**
 * Maps the API's rows, and falls back when what came back cannot be used.
 *
 * An empty list is a legitimate answer — the client is allowed to clear the strip — and it is
 * honoured, which is why the emptiness check below is about the *shape* of the payload rather than
 * its length. A `200 {"oops":true}` is not an answer, and `catchError` will never see it.
 */
export function toMessages(rows: readonly AnnouncementDto[]): readonly AnnouncementMessage[] {
  if (!Array.isArray(rows)) {
    return PLACEHOLDER_ANNOUNCEMENTS;
  }

  const usable = rows.filter(
    (row) => typeof row?.message === 'string' && row.message.trim().length > 0,
  );

  // Rows arrived, but not one of them had a message in it: the payload is the wrong shape, not an
  // empty strip the client chose.
  if (rows.length > 0 && usable.length === 0) {
    return PLACEHOLDER_ANNOUNCEMENTS;
  }

  return usable.map((row) => ({
    id: String(row.id),
    text: row.message.trim(),
    // Only a path on this site. The API already refuses anything else in a section's settings; an
    // announcement's link comes from the same admin screens and gets the same treatment before it
    // reaches an href.
    linkUrl: sitePath(row.linkUrl),
  }));
}

function sitePath(value: string | null | undefined): string | null {
  const trimmed = value?.trim();

  if (!trimmed || trimmed[0] !== '/' || trimmed.startsWith('//')) {
    return null;
  }

  return trimmed;
}
