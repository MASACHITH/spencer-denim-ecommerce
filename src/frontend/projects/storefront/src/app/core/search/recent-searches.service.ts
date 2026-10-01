import { isPlatformBrowser } from '@angular/common';
import { Injectable, PLATFORM_ID, Signal, afterNextRender, inject, signal } from '@angular/core';
import { SafeLocalStorage } from '@spencer-denim/shared';

import { MAX_TERM_LENGTH, isSearchable, normaliseTerm } from './search-term';

/** Where this browser's recent searches live. */
export const RECENT_SEARCHES_KEY = 'sd.recent-searches';

/** How many are kept. Six fills the overlay without pushing the suggestions off a 320 px screen. */
export const RECENT_SEARCHES_LIMIT = 6;

/**
 * An address the shopper may have been looking up rather than shopping for.
 *
 * Deliberately the same pattern the API uses to decide what never reaches
 * `analytics.SearchQueriesDaily` (doc 08 §3, `SearchQueriesDaily` rules, DB25), so the two sides of
 * the same privacy rule cannot drift apart.
 */
const EMAIL_LIKE = /^[^@\s]+@[^@\s]+\.[^@\s]+$/u;

/** Doc 08 DB25: seven or more digits once separators are gone is a telephone number. */
const MAX_DIGITS = 6;

/**
 * The searches this browser has run (SRCH-01).
 *
 * **Why the browser and not a table.** Doc 08 §1.7 decided it: recent searches are per visitor,
 * there is no customer account yet (ACC-01), and a logged-out shopper has nothing to key a row on.
 * So this is `localStorage` through {@link SafeLocalStorage}, which never throws — a private
 * window, blocked site data and a full quota all raise, and all three degrade to "no history"
 * rather than to a broken header.
 *
 * **It is never rendered on the server.** The server has no storage and would render an empty list;
 * a browser that then filled it during hydration would be adding nodes Angular never sent, which
 * hydration refuses. The list is read in `afterNextRender`, the same shape `RecentlyViewed` and
 * `Viewport` use, so it appears one frame after the page is live.
 *
 * **What is deliberately not kept.** A term that looks like an email address, or that carries seven
 * or more digits — an order number, a telephone number. This is a clothing shop on a shared family
 * laptop and an internet café machine as often as it is a private phone, and a search box is where
 * people type the thing they were trying to look up. The same rule keeps those terms out of the
 * server-side aggregate (doc 08 DB25), so neither side of the site remembers them.
 *
 * **A shopper can clear it**, from the overlay, in one action — {@link clear}. A history nobody can
 * delete is the part of this feature that would be a privacy problem rather than a convenience.
 * Clearing removes the key itself, not just its contents, so nothing is left behind to inspect.
 *
 * **Only submitted searches are recorded**, never keystrokes: the overlay fires a request on every
 * few characters, and storing those would turn a history of six searches into a history of every
 * half-typed word. It is also what `SearchQueriesDaily` records (DB25: "results-page requests"), so
 * "recent" and "popular" would have meant the same kind of event if the second had a source.
 */
@Injectable({ providedIn: 'root' })
export class RecentSearches {
  private readonly storage = inject(SafeLocalStorage);
  private readonly isBrowser = isPlatformBrowser(inject(PLATFORM_ID));

  private readonly terms = signal<readonly string[]>([]);

  /** Most recent first. Always empty on the server and until the first frame after hydration. */
  readonly recent: Signal<readonly string[]> = this.terms.asReadonly();

  constructor() {
    if (this.isBrowser) {
      afterNextRender(() => this.terms.set(this.read()));
    }
  }

  /**
   * Records a submitted search, moving it to the front if it was already there.
   *
   * Safe to call during a render: on the server it does nothing at all, so a server-rendered page
   * never pretends to know what this browser has looked for.
   */
  remember(raw: string): void {
    if (!this.isBrowser) {
      return;
    }

    const term = normaliseTerm(raw);
    if (!isSearchable(term) || isPrivate(term)) {
      return;
    }

    const folded = term.toLowerCase();
    const next = [
      term,
      ...this.read().filter((entry) => entry.toLowerCase() !== folded),
    ].slice(0, RECENT_SEARCHES_LIMIT);

    this.terms.set(next);
    this.storage.write(RECENT_SEARCHES_KEY, JSON.stringify(next));
  }

  /** Removes one term, for a shopper who wants a single search gone rather than all of them. */
  forget(raw: string): void {
    const folded = normaliseTerm(raw).toLowerCase();
    const next = this.read().filter((entry) => entry.toLowerCase() !== folded);

    this.terms.set(next);
    if (next.length === 0) {
      this.storage.remove(RECENT_SEARCHES_KEY);
      return;
    }

    this.storage.write(RECENT_SEARCHES_KEY, JSON.stringify(next));
  }

  /** Forgets everything, key and all. The shopper's own control over their history. */
  clear(): void {
    this.terms.set([]);
    this.storage.remove(RECENT_SEARCHES_KEY);
  }

  /**
   * Reads and vets the stored list (lesson L1): another tab, an older build of the site or a
   * curious visitor may have left anything at all under that key, so every entry is checked and
   * anything unusable is dropped.
   *
   * The privacy rule is applied on the way **out** as well as on the way in. A term stored by a
   * build that did not have the rule — or by a visitor editing the key by hand — must not be put
   * back on screen just because it is already in storage.
   */
  private read(): readonly string[] {
    const raw = this.storage.read(RECENT_SEARCHES_KEY);
    if (!raw) {
      return [];
    }

    try {
      const parsed: unknown = JSON.parse(raw);
      if (!Array.isArray(parsed)) {
        return [];
      }

      const kept: string[] = [];
      for (const candidate of parsed) {
        if (typeof candidate !== 'string' || candidate.length > MAX_TERM_LENGTH * 2) {
          continue;
        }

        const term = normaliseTerm(candidate);
        if (!isSearchable(term) || isPrivate(term)) {
          continue;
        }

        const folded = term.toLowerCase();
        if (!kept.some((entry) => entry.toLowerCase() === folded)) {
          kept.push(term);
        }

        if (kept.length === RECENT_SEARCHES_LIMIT) {
          break;
        }
      }

      return kept;
    } catch {
      return [];
    }
  }
}

/** Whether a term is something this browser should not remember having been asked. */
export function isPrivate(term: string): boolean {
  if (EMAIL_LIKE.test(term)) {
    return true;
  }

  const digits = term.replace(/\D/gu, '');
  return digits.length > MAX_DIGITS;
}
