import { isPlatformBrowser } from '@angular/common';
import { Injectable, PLATFORM_ID, Signal, afterNextRender, inject, signal } from '@angular/core';
import { SafeLocalStorage } from '@spencer-denim/shared';

import { MAX_TERM_LENGTH, isSearchable, normaliseTerm } from './search-term';

/** Where this browser's recent searches live. */
export const RECENT_SEARCHES_KEY = 'sd.recent-searches';

/** How many are kept. Six fills the overlay without pushing the suggestions off a 320 px screen. */
export const RECENT_SEARCHES_LIMIT = 6;

/**
 * The at-sign, in every spelling a search box can receive.
 *
 * ASCII, the fullwidth form a Japanese or Chinese keyboard produces, and the small form. Written as
 * escapes rather than as the characters themselves so the set is readable in a diff and cannot be
 * mistaken for the ASCII one by an editor or a reviewer.
 */
const AT_SIGNS = /[@\uFF20\uFE6B]/u;

/** Doc 08 DB25: seven or more digits once separators are gone is a telephone number. */
const MAX_DIGITS = 6;

/**
 * Everything that is **not** a decimal digit, in any script.
 *
 * `\d` and `\D` are ASCII-only even under the `u` flag, so a telephone number typed on an Arabic or
 * a Japanese keyboard counted as zero digits and was stored: Arabic-Indic
 * `\u0660\u0667\u0667\u0661\u0662\u0663\u0664\u0665\u0666\u0667` and fullwidth
 * `\uFF10\uFF17\uFF17\uFF11\uFF12\uFF13\uFF14\uFF15\uFF16\uFF17` are both ten digits to a reader
 * and none to `\d`. `\p{Nd}` is the Unicode decimal-number category and counts all of them.
 */
const NOT_A_DIGIT = /\P{Nd}/gu;

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
 * **What is deliberately not kept.** A term carrying an at-sign anywhere in it, or seven or more
 * digits in any script — an email address in the middle of a sentence, an order number, a telephone
 * number. This is a clothing shop on a shared family
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

/**
 * Whether a term is something this browser should not remember having been asked.
 *
 * **Two rules, both deliberately unanchored.** The first version tested
 * `/^[^@\s]+@[^@\s]+\.[^@\s]+$/`, which only matched a term that *was* an email address and
 * nothing else — so "contact someone@example.com", "my email is bob@example.com" and
 * "someone@example.com please" were all stored and all rendered back in the overlay. A shopper does
 * not type a bare address into a search box; they type a sentence with one in it.
 *
 * **Why any at-sign at all, rather than an email pattern.** An unanchored pattern still has to
 * decide what an address looks like, and every answer loses to a space: "john &#64; example.com" is
 * an address to a human and not to a regular expression. Nothing a shopper searches a clothing shop
 * for contains an at-sign, so the at-sign alone is the rule. That is the one widening that costs the
 * feature nothing — and the cases that must keep working do: "32 waist jeans", "size 10 dress" and
 * "order 123456" are all remembered.
 *
 * **This rule is shared with the server by design, and the server's copy has not been written yet.**
 * The same test decides what reaches `analytics.SearchQueriesDaily` (doc 08 §3, DB25), which has
 * **13-month retention** — so the anchored version would have kept a shopper's email address on the
 * server for over a year rather than only on their own device. Issue **#78** carries the obligation
 * that the server side inherits this corrected rule, not the one it was first written as.
 */
export function isPrivate(term: string): boolean {
  if (AT_SIGNS.test(term)) {
    return true;
  }

  return term.replace(NOT_A_DIGIT, '').length > MAX_DIGITS;
}
