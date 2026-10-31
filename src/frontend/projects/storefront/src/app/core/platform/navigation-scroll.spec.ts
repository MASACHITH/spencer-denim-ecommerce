import { Location, ViewportScroller } from '@angular/common';
import { SpyLocation, provideLocationMocks } from '@angular/common/testing';
import { Component, PLATFORM_ID } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { NavigationEnd, Router, provideRouter } from '@angular/router';
import { filter, firstValueFrom } from 'rxjs';

import { NavigationScroll, RESTORE_PATIENCE_MS, addressOf, pageOf } from './navigation-scroll';

@Component({ template: '' })
class Blank {}

/**
 * A link that opens a different page opens it at the top, and nothing else moves the page. The browser half of that
 * is `navigation-scroll.e2e.ts`; this pins the rule itself, including every case that must NOT scroll.
 */
describe('NavigationScroll', () => {
  let router: Router;
  let scrollToPosition: ReturnType<typeof vi.spyOn>;
  let getScrollPosition: ReturnType<typeof vi.spyOn>;

  function create(platform: 'browser' | 'server' = 'browser'): void {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        provideRouter([
          { path: '', component: Blank },
          { path: 'women', component: Blank },
          { path: 'women/jeans', component: Blank },
          { path: 'men', component: Blank },
          { path: 'search', component: Blank },
          { path: '**', component: Blank },
        ]),
        provideLocationMocks(),
        { provide: PLATFORM_ID, useValue: platform },
        // A jsdom page has no layout, so it is never "tall enough"; a short patience keeps these tests short.
        { provide: RESTORE_PATIENCE_MS, useValue: 40 },
      ],
    });

    router = TestBed.inject(Router);
    const viewport = TestBed.inject(ViewportScroller);
    // Not called through: jsdom has no layout and its `window.scrollTo` is a stub that only logs.
    scrollToPosition = vi.spyOn(viewport, 'scrollToPosition').mockImplementation(() => undefined);
    getScrollPosition = vi.spyOn(viewport, 'getScrollPosition').mockReturnValue([0, 0]);
    TestBed.inject(NavigationScroll).start();
  }

  /** A navigation the shopper starts: a link or a button. */
  async function go(url: string): Promise<void> {
    await router.navigateByUrl(url);
  }

  /**
   * Back or Forward: the browser tells the router, and the trigger is `popstate`. Through the mock `Location`'s own
   * `back()` and `forward()`, which hand the router the history entry's state as a browser does (the navigation id
   * the router stored in it); `simulateUrlPop` carries no state, so the router would have nothing to restore from.
   */
  async function history(direction: 'back' | 'forward'): Promise<void> {
    const ended = firstValueFrom(router.events.pipe(filter((event) => event instanceof NavigationEnd)));
    const location = TestBed.inject(Location) as SpyLocation;

    if (direction === 'back') {
      location.back();
    } else {
      location.forward();
    }

    await ended;
  }

  /** The restore waits up to its patience (40 ms here) for a page that jsdom never makes tall enough; a little more. */
  const drawn = (): Promise<void> => new Promise((resolve) => setTimeout(resolve, 250));

  // The shell's own `#main`, which `app.ts` gives `tabindex="-1"` for the skip link and which this service focuses
  // the same way. A real element, not a stub: `document.getElementById('main')` is what the service calls.
  let main: HTMLElement;

  beforeEach(() => {
    main = document.createElement('main');
    main.id = 'main';
    main.tabIndex = -1;
    document.body.appendChild(main);
  });

  afterEach(() => {
    main.remove();
  });

  /**
   * A link `RouterLink` took over: it calls `preventDefault()` itself, before the router decides the URL is one it
   * can ignore, so a plain `dispatchEvent` has to do the same or the service's own `!event.defaultPrevented` guard
   * would (rightly) ignore it, same as a plain, un-routed `<a>` or a modified click.
   */
  function clickLink(href: string, { preventDefault = true } = {}): void {
    const anchor = document.createElement('a');
    anchor.href = href;
    document.body.appendChild(anchor);

    if (preventDefault) {
      anchor.addEventListener('click', (event) => event.preventDefault());
    }

    anchor.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, button: 0 }));
    anchor.remove();
  }

  it('does not scroll for the first navigation, which is the load itself', async () => {
    create();

    await go('/women');

    expect(scrollToPosition).not.toHaveBeenCalled();
  });

  it('scrolls for a click that arrives before the load\'s own navigation has ended', async () => {
    create();

    // On a server-rendered page the load's navigation ends only after the start-up requests answer, which can be
    // after the shopper's first click. The click cancels it, and the first navigation to end is the shopper's.
    void router.navigateByUrl('/women');
    await go('/men');

    expect(scrollToPosition).toHaveBeenCalledTimes(1);
  });

  it('scrolls to the top when a link opens a different page', async () => {
    create();
    await go('/women');

    await go('/men');

    expect(scrollToPosition).toHaveBeenCalledTimes(1);
    expect(scrollToPosition).toHaveBeenCalledWith([0, 0]);
  });

  it('scrolls for a category inside the same department: the path is what decides, not the first segment', async () => {
    create();
    await go('/women');

    await go('/women/jeans');
    await go('/women');

    expect(scrollToPosition).toHaveBeenCalledTimes(2);
  });

  it('does not scroll when only the query string changes: a filter, the sort, Load More, a size', async () => {
    create();
    await go('/women/jeans');

    await go('/women/jeans?colour=blue');
    await go('/women/jeans?colour=blue&sort=price-asc');
    await go('/women/jeans?colour=blue&sort=price-asc&pages=3');
    await go('/women/jeans?pages=3');

    expect(scrollToPosition).not.toHaveBeenCalled();
  });

  it('does not scroll for the same page spelled differently: a trailing slash or capitals', async () => {
    create();
    await go('/women/jeans');

    await go('/Women/Jeans/');
    await go('/women/jeans/?pages=2');
    await go('/women/jeans');

    expect(scrollToPosition).not.toHaveBeenCalled();
  });

  it('does not scroll for a fragment on the same page', async () => {
    create();
    await go('/women');

    await go('/women#filters');

    expect(scrollToPosition).not.toHaveBeenCalled();
  });

  /** The shopper scrolls: the position the service tracks. */
  function scrolledTo(y: number): void {
    getScrollPosition.mockReturnValue([0, y]);
    window.dispatchEvent(new Event('scroll'));
  }

  /**
   * What a browser does for Back and Forward, in its order: it restores the scroll position of the entry it is going
   * to (here a clamped `restoredY`) and *then* fires `popstate`; the scroll event for that restore arrives after.
   */
  function browserTraverses(restoredY: number): void {
    getScrollPosition.mockReturnValue([0, restoredY]);
    window.dispatchEvent(new PopStateEvent('popstate'));
    window.dispatchEvent(new Event('scroll'));
  }

  it('gives Back and Forward the position they were left at, once the page is drawn, and never the top', async () => {
    create();

    // Starts the router's listener for the browser's Back and Forward; a bare TestBed does not until it is asked.
    router.initialNavigation();
    await go('/women');

    scrolledTo(900);
    await go('/men'); // leaves /women at 900
    scrollToPosition.mockClear();
    scrolledTo(40); // on /men

    // Back. The browser has already put a clamped 3147 on screen by the time the router hears of it; /women's own
    // 900 was remembered from before, and /men must be remembered as 40, not as the 3147 now on screen.
    browserTraverses(3147);
    await history('back');
    await drawn();
    expect(scrollToPosition).toHaveBeenCalledTimes(1);
    expect(scrollToPosition).toHaveBeenLastCalledWith([0, 900]);
    scrolledTo(900); // the restore landed

    // Forward: /men comes back at 40.
    scrollToPosition.mockClear();
    browserTraverses(7);
    await history('forward');
    await drawn();
    expect(scrollToPosition).toHaveBeenCalledTimes(1);
    expect(scrollToPosition).toHaveBeenLastCalledWith([0, 40]);
  });

  it('remembers the position of a page left by Back and Forward as it was, for the next time', async () => {
    create();
    router.initialNavigation();
    await go('/women');
    scrolledTo(900);
    await go('/men');
    scrolledTo(40);
    browserTraverses(900);
    await history('back'); // leaves /men at 40
    await drawn();
    scrolledTo(120); // the shopper scrolls /women a little, then goes Forward and Back again
    browserTraverses(40);
    await history('forward'); // leaves /women at 120
    await drawn();
    scrollToPosition.mockClear();

    browserTraverses(500);
    await history('back');
    await drawn();

    expect(scrollToPosition).toHaveBeenLastCalledWith([0, 120]);
  });

  it('does not scroll to the top for Back or Forward, whatever the page', async () => {
    create();
    router.initialNavigation();
    await go('/women');
    scrolledTo(500);
    await go('/men');
    scrollToPosition.mockClear();

    browserTraverses(100);
    await history('back');
    await drawn();

    expect(scrollToPosition).not.toHaveBeenCalledWith([0, 0]);
    expect(scrollToPosition).toHaveBeenLastCalledWith([0, 500]);
  });

  it('scrolls to the top for the page a redirect lands on, which is the page that is shown', async () => {
    create();
    await go('/women');

    // The router settles the URL it ends on; `/Search/` and `/search` are one page, and `/men` is another.
    await go('/search');
    await go('/Search/');

    expect(scrollToPosition).toHaveBeenCalledTimes(1);
  });

  it('does nothing on the server, which has no scroll position', async () => {
    create('server');
    await go('/women');

    await go('/men');

    expect(scrollToPosition).not.toHaveBeenCalled();
  });

  it('starts once however many times it is asked', async () => {
    create();
    TestBed.inject(NavigationScroll).start();
    await go('/women');

    await go('/men');

    expect(scrollToPosition).toHaveBeenCalledTimes(1);
  });

  describe('focus', () => {
    it('moves into main when a link opens a different page, not just the scroll', async () => {
      create();
      await go('/women');
      const footerLink = document.createElement('a');
      document.body.appendChild(footerLink);
      footerLink.focus(); // where keyboard activation of the link that caused this navigation left it

      await go('/men');

      expect(document.activeElement).toBe(main);
      footerLink.remove();
    });

    it('does not move focus for Back or Forward, which give the position back, not the top', async () => {
      create();
      router.initialNavigation();
      await go('/women');
      await go('/men');
      const elsewhere = document.createElement('button');
      document.body.appendChild(elsewhere);
      elsewhere.focus();

      browserTraverses(0);
      await history('back');
      await drawn();

      expect(document.activeElement).toBe(elsewhere);
      elsewhere.remove();
    });
  });

  describe('a link to the page already shown', () => {
    it('scrolls to the top, even though the router ignores the navigation', async () => {
      create();
      await go('/women');

      clickLink('/women');

      expect(scrollToPosition).toHaveBeenCalledWith([0, 0]);
    });

    it('moves focus into main as well', async () => {
      create();
      await go('/women');
      const elsewhere = document.createElement('button');
      document.body.appendChild(elsewhere);
      elsewhere.focus();

      clickLink('/women');

      expect(document.activeElement).toBe(main);
      elsewhere.remove();
    });

    it('does nothing for a plain link the router was never given a chance to ignore', async () => {
      // `preventDefault` not called: the stand-in for a link `RouterLink` did not take over, or scripts off — a
      // full page load is already on its way, which lands at the top on its own.
      create();
      await go('/women');

      clickLink('/women', { preventDefault: false });

      expect(scrollToPosition).not.toHaveBeenCalled();
    });

    it('does nothing for a fragment link to the current page: the skip link and a same-page anchor are not this', async () => {
      create();
      await go('/women');

      clickLink('/women#filters');

      expect(scrollToPosition).not.toHaveBeenCalled();
    });

    it('does nothing for a modified click, which the browser is left to handle itself', async () => {
      create();
      await go('/women');
      const anchor = document.createElement('a');
      anchor.href = '/women';
      document.body.appendChild(anchor);
      anchor.addEventListener('click', (event) => event.preventDefault());

      anchor.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, button: 0, ctrlKey: true }));

      expect(scrollToPosition).not.toHaveBeenCalled();
      anchor.remove();
    });

    it('does nothing when the link opens a different page', async () => {
      create();
      await go('/women');

      clickLink('/men');

      expect(scrollToPosition).not.toHaveBeenCalled();
    });
  });

  describe('a pending Back/Forward restore that never gets to fire', () => {
    it('is cancelled by a second Back that starts before its patience is up', async () => {
      create();
      router.initialNavigation();
      await go('/women');
      scrolledTo(900);
      await go('/men'); // leaves /women at 900
      scrolledTo(40);
      await go('/women/jeans'); // leaves /men at 40
      scrollToPosition.mockClear();

      browserTraverses(999);
      await history('back'); // -> /men; a restore to 40 is now waiting, patience not yet up
      browserTraverses(999);
      await history('back'); // -> /women; a restore to 900 is now waiting; the one above must never fire

      await drawn(); // long past both patience windows

      expect(scrollToPosition).toHaveBeenCalledTimes(1);
      expect(scrollToPosition).toHaveBeenLastCalledWith([0, 900]);
    });

    it('is cancelled by the shopper navigating on, instead of landing a stale position on the new page', async () => {
      create();
      router.initialNavigation();
      await go('/women');
      scrolledTo(900);
      await go('/men'); // leaves /women at 900
      browserTraverses(999);
      await history('back'); // -> /women; a restore to 900 is now waiting
      scrollToPosition.mockClear();

      await go('/women/jeans'); // the shopper clicks a link before that restore ever fires

      expect(scrollToPosition).toHaveBeenCalledTimes(1);
      expect(scrollToPosition).toHaveBeenCalledWith([0, 0]);

      await drawn(); // long past the restore's patience

      expect(scrollToPosition).toHaveBeenCalledTimes(1);
    });

    it('is cancelled by a shopper gesture, which is left alone rather than overridden once the patience is up', async () => {
      create();
      router.initialNavigation();
      await go('/women');
      scrolledTo(900);
      await go('/men'); // leaves /women at 900
      browserTraverses(999);
      await history('back'); // -> /women; a restore to 900 is now waiting, and jsdom is never tall enough for it
      scrollToPosition.mockClear();

      window.dispatchEvent(new Event('wheel'));

      await drawn(); // long past the patience window the gesture should have pre-empted

      expect(scrollToPosition).not.toHaveBeenCalled();
    });
  });
});

describe('pageOf', () => {
  it('is the path alone, in lower case, with no trailing slash', () => {
    expect(pageOf('/women/jeans?colour=blue#top')).toBe('/women/jeans');
    expect(pageOf('/Women/Jeans/')).toBe('/women/jeans');
    expect(pageOf('/')).toBe('/');
    expect(pageOf('/?q=1')).toBe('/');
    expect(pageOf('')).toBe('/');
    expect(pageOf('/search/?q=jeans')).toBe('/search');
  });
});

describe('addressOf', () => {
  it('is the page and its query string, without the fragment', () => {
    expect(addressOf('/women/jeans?pages=3&colour=blue#top')).toBe('/women/jeans?pages=3&colour=blue');
    expect(addressOf('/Women/Jeans/?pages=3')).toBe('/women/jeans?pages=3');
    expect(addressOf('/#newsletter')).toBe('/');
    expect(addressOf('/search?q=jeans')).toBe('/search?q=jeans');
  });

  it('tells a deep listing from a shallow one: a position in one means nothing in the other', () => {
    expect(addressOf('/women/jeans?pages=1')).not.toBe(addressOf('/women/jeans?pages=3'));
  });
});
