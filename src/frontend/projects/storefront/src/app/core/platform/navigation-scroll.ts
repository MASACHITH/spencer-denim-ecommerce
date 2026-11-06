import { DOCUMENT, ViewportScroller, isPlatformBrowser } from '@angular/common';
import { DestroyRef, Injectable, InjectionToken, NgZone, PLATFORM_ID, inject } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import {
  NavigationCancel,
  NavigationEnd,
  NavigationError,
  NavigationSkipped,
  NavigationStart,
  Router,
} from '@angular/router';

/** How long a Back or Forward waits for the page to be tall enough before it scrolls as far as it can. */
export const RESTORE_PATIENCE_MS = new InjectionToken<number>('RESTORE_PATIENCE_MS', {
  providedIn: 'root',
  factory: () => 2000,
});

/**
 * Where a navigation leaves the page: a link that opens a different page opens it at the top, and Back and Forward
 * return to where the shopper was.
 *
 * **The bug.** The router has no scroll handling, so a shopper who clicked "About us" or "Returns and exchanges"
 * in the footer arrived on the new page still scrolled to the bottom of the old one, looking at a footer under an
 * empty-looking page.
 *
 * **Why this and not `withInMemoryScrolling`, which was the first version.** Angular's `enabled` restoration
 * switches `history.scrollRestoration` to `manual`, and then only restores in memory, so a reload lost its
 * position (the browser restores a reload exactly when it is left alone: 8114 px on the home page at 390 px).
 * It also scrolls to the top on *every* navigation, so each filter, sort and Load More needed its own
 * `scroll: 'manual'` opt-out; it skips the scroll for as long as the freshly hydrated app is not yet stable (a
 * click in the first two seconds was left where it was); and its anchor scrolling ignores `scroll-padding-top`.
 * This does two small things, and everything else is the browser's, as before (`scrollRestoration` stays `auto`):
 *
 * - **A link opens a different page at the top.** A navigation started by the shopper or the app (`imperative`: a
 *   link, a button, `router.navigate`; not the load's own navigation, which is the first) that ends on a different
 *   page than the one on screen (a different path: a query string or a fragment is the same page; the page the
 *   document was loaded with counts, so a click before the load's navigation has ended still scrolls) scrolls to
 *   the top, when it ends, with no wait. A filter, the sort, Load More, a colour or a size change only the query
 *   string, so they never trigger it and there is nothing to opt out of. Focus moves into `main` as well (same
 *   `tabindex="-1"` target as the skip link): otherwise it stays on the link that was just activated, now at the
 *   bottom of a page scrolled back to its top, and the shopper's next Tab jumps the page straight back down to it.
 * - **A link to the page already on screen also lands at the top.** The router's own `onSameUrlNavigation: 'ignore'`
 *   (the default, left as-is: changing it to `'reload'` would re-run every resolver for a filter click that happens
 *   to produce a URL already applied) fires no event at all for that click, so nothing above ever runs. A footer
 *   link is still a link, so a `click` on an internal, unmodified, non-fragment anchor whose target is the address
 *   already shown gets the same top-and-focus treatment here, outside the router entirely.
 * - **Back and Forward return to where the shopper was.** The browser restores them the moment the history
 *   changes, before the page has been drawn again, so on a wide screen it clamps to a short page (measured: Back
 *   from the Sale page to the home page landed at 0 at 1280 px, on the base as well as with this, and at the right
 *   place at 390 px). So the position of the page being left is remembered, and given back once the page is
 *   there. Remembered by the page's address, query string included, and not by a navigation id: ids start again
 *   after a reload, and at the shopper's first click the load's own navigation may not have ended, so there may be
 *   no id at all. A reload itself is not touched; the browser restores it.
 *
 * Browser only: the server has no scroll position.
 */
@Injectable({ providedIn: 'root' })
export class NavigationScroll {
  private readonly router = inject(Router);
  private readonly viewport = inject(ViewportScroller);
  private readonly document = inject(DOCUMENT);
  private readonly zone = inject(NgZone);
  private readonly destroyRef = inject(DestroyRef);
  private readonly isBrowser = isPlatformBrowser(inject(PLATFORM_ID));
  private readonly patienceMs = inject(RESTORE_PATIENCE_MS);

  private started = false;

  /**
   * Cancels whatever `restoreOnceDrawn` wait is still polling, if any. **Not cleared from inside that method's own
   * `finish`, read from here:** a stray reference to a finished wait is harmless (it is `null` or already another
   * wait's `finish` by the time anything would call it), but a wait left running past its own navigation is not —
   * a Back followed by a quick Forward, or by a plain link click, before the first wait's patience is up left it
   * polling in the background, and it could still land its remembered position on top of the page the shopper is
   * now looking at.
   */
  private cancelPendingRestore: (() => void) | null = null;

  /** Subscribes to navigation. Called once, from an app initializer; does nothing on the server. */
  start(): void {
    if (!this.isBrowser || this.started) {
      return;
    }

    this.started = true;

    // The page the document was loaded with. Seeded from the address bar and not from the first navigation to end,
    // because on a server-rendered page the load's own navigation ends only after the start-up requests have
    // answered, which can be after the shopper's first click. That click then cancels it, and the first navigation
    // to *end* is the shopper's: treating it as "the load itself" left the page where it was.
    const loaded = this.document.location;
    let shownPage = pageOf(loaded.pathname);
    let shownAddress = addressOf(loaded.pathname + loaded.search);
    let byTheShopper = false;
    let byHistory = false;
    const remembered = new Map<string, [number, number]>();

    // **Where the page is scrolled to is tracked as the shopper scrolls, not read when the navigation starts.** On
    // Back and Forward the browser restores the scroll position of the page it is going to *before* it fires
    // `popstate`, so by the time the router starts the navigation the position of the page being left has already
    // been overwritten (measured: Forward from the home page landed the Sale page at 3147, the home page's
    // clamped restore, instead of 0). The tracking is frozen from the `popstate` until the navigation is over.
    let latest = this.viewport.getScrollPosition();
    let frozen = false;
    const view = this.document.defaultView;
    const track = (): void => {
      if (!frozen) {
        latest = this.viewport.getScrollPosition();
      }
    };
    const freeze = (): void => {
      frozen = true;
    };

    // Scrolls to the top and hands focus to `main`, the one thing both an ordinary "different page" navigation and
    // a click on a link to the page already shown have in common. Cancels any Back/Forward restore still polling
    // in the background first, or it could land its remembered position on top of this.
    const landAtTop = (): void => {
      this.cancelPendingRestore?.();
      this.viewport.scrollToPosition([0, 0]);
      this.document.getElementById('main')?.focus({ preventScroll: true });
    };

    // Catches the click the router never tells us about: `onSameUrlNavigation` is left at its default `'ignore'`
    // (see the class doc), so a link to the page already on screen fires no `NavigationStart`/`NavigationEnd` at
    // all. **Requires `event.defaultPrevented`:** `RouterLink` itself calls `preventDefault()`, before the router
    // decides the URL is one it can ignore, only for the kind of click it means to take over (the same checks are
    // repeated below only so this does not depend on exactly how `RouterLink` reaches that decision) — a plain
    // `<a>` outside Angular's routing, or a modified click `RouterLink` left for the browser, never sets it, and
    // is rightly left alone here too. Reading `anchor.pathname`/`.search`/`.origin`, which the browser resolves
    // against `<base href>` for us, rather than parsing `getAttribute('href')`. A fragment link (the skip link, a
    // same-page "back to top", a table of contents) is left alone: `app.ts` gives the skip link its own
    // focus-and-scrollIntoView, and a plain in-page fragment is the browser's to scroll, same as
    // `navigation-scroll.e2e.ts` already checks.
    const landOnSamePageLink = (event: MouseEvent): void => {
      if (!event.defaultPrevented || event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) {
        return;
      }

      const target = event.target;
      const anchor = target instanceof Element ? target.closest('a[href]') : null;

      if (!(anchor instanceof HTMLAnchorElement) || anchor.target !== '' || anchor.hash !== '') {
        return;
      }

      if (anchor.origin === loaded.origin && addressOf(anchor.pathname + anchor.search) === shownAddress) {
        landAtTop();
      }
    };

    view?.addEventListener('scroll', track, { passive: true });
    view?.addEventListener('popstate', freeze);
    view?.addEventListener('click', landOnSamePageLink);
    this.destroyRef.onDestroy(() => {
      view?.removeEventListener('scroll', track);
      view?.removeEventListener('popstate', freeze);
      view?.removeEventListener('click', landOnSamePageLink);
      this.cancelPendingRestore?.();
    });

    this.router.events.pipe(takeUntilDestroyed(this.destroyRef)).subscribe((event) => {
      if (event instanceof NavigationStart) {
        // Where the page being left was scrolled to, under its address. By address and not by navigation id: ids
        // restart on a reload, and at the shopper's first click the load's own navigation may not have ended, so
        // there may be no id to file it under.
        remembered.set(shownAddress, latest);

        // `id === 1` is the load's own navigation, which leaves nothing behind and which the browser restores (or
        // not) itself. The last navigation to start is the one that ends, so one flag is enough when they overlap.
        byTheShopper = event.navigationTrigger === 'imperative' && event.id > 1;
        byHistory = event.navigationTrigger === 'popstate';
        return;
      }

      // A navigation that does not end in a page (cancelled, refused, failed) unfreezes the tracking as well.
      if (event instanceof NavigationCancel || event instanceof NavigationError || event instanceof NavigationSkipped) {
        frozen = false;
        return;
      }

      if (!(event instanceof NavigationEnd)) {
        return;
      }

      frozen = false;

      // Any navigation that actually ends retires a Back/Forward restore still waiting on an earlier page — the
      // branches below either give it a new position to wait for or need no restore at all.
      this.cancelPendingRestore?.();

      const page = pageOf(event.urlAfterRedirects);
      const address = addressOf(event.urlAfterRedirects);

      if (byTheShopper && page !== shownPage) {
        landAtTop();
      } else if (byHistory) {
        const saved = remembered.get(address);

        if (saved) {
          this.restoreOnceDrawn(saved);
        }
      }

      shownPage = page;
      shownAddress = address;
      latest = this.viewport.getScrollPosition();
    });
  }

  /**
   * Gives a position back once the page is tall enough to have it. **Not on the next frame, which was the first
   * version:** the page the shopper returns to draws its content as that content arrives, so on the frame after the
   * navigation the document is still short, the browser clamps the scroll to the end of it, and Back landed at 0
   * about one time in two at 1280 px. So it waits, a frame at a time, until the document can reach the offset, and
   * for no longer than `RESTORE_PATIENCE_MS`, when it scrolls as far as it can. A shopper who scrolls, taps, presses
   * a key or clicks in the meantime has taken over, and is left alone. Outside the zone, so the wait does not hold
   * the application unstable.
   *
   * Only one of these can be waiting at a time: starting a new one — or any navigation that ends, through
   * `cancelPendingRestore` — stops an earlier one from ever scrolling, instead of leaving it to land a stale
   * position on top of whatever page is on screen by the time its patience or its height check comes due.
   */
  private restoreOnceDrawn(position: [number, number]): void {
    this.cancelPendingRestore?.();

    const view = this.document.defaultView;
    const root = this.document.documentElement;
    const gesture = ['wheel', 'touchstart', 'keydown', 'mousedown'] as const;

    this.zone.runOutsideAngular(() => {
      const startedAt = Date.now();
      let finished = false;

      const finish = (): void => {
        finished = true;
        if (this.cancelPendingRestore === finish) {
          this.cancelPendingRestore = null;
        }
        for (const type of gesture) {
          view?.removeEventListener(type, finish);
        }
      };

      this.cancelPendingRestore = finish;

      for (const type of gesture) {
        view?.addEventListener(type, finish, { passive: true });
      }

      const attempt = (): void => {
        if (finished) {
          return;
        }

        const reachable = root.scrollHeight - (view?.innerHeight ?? 0) >= position[1];

        if (reachable || Date.now() - startedAt >= this.patienceMs) {
          this.viewport.scrollToPosition(position);
          finish();
          return;
        }

        setTimeout(attempt, 40);
      };

      attempt();
    });
  }
}

/**
 * The page a router URL is: its path, with no query string and no fragment, in lower case and without a trailing
 * slash. The last two because the listing writes its own canonical path (`/Women/Jeans/` and `/women/jeans` are one
 * page, and a state change that settles the first into the second is not a new page).
 */
export function pageOf(url: string): string {
  const end = url.search(/[?#]/);
  const path = (end < 0 ? url : url.slice(0, end)).toLowerCase().replace(/\/+$/, '');

  return path === '' ? '/' : path;
}

/**
 * The address a scroll position is remembered under: the page and its query string, no fragment. The query is part
 * of it because `?pages=3` is a different length of page from `?pages=1`, and a position inside one means nothing in
 * the other.
 */
export function addressOf(url: string): string {
  const hash = url.indexOf('#');
  const withoutFragment = hash < 0 ? url : url.slice(0, hash);
  const query = withoutFragment.indexOf('?');

  return query < 0 ? pageOf(withoutFragment) : `${pageOf(withoutFragment)}${withoutFragment.slice(query)}`;
}
