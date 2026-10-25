import { DOCUMENT } from '@angular/common';
import { Injectable, inject } from '@angular/core';

/**
 * Takes the reader to a section of the guide and puts keyboard focus on its heading.
 *
 * **Not `routerLink` with a fragment**, because this application's router has no anchor scrolling and
 * switching it on would change scrolling on every admin page. **Not a bare `href="#id"`** either: the
 * page has a `<base href>`, so that would leave the guide for the home page. The heading is focused as
 * well as scrolled to so that a keyboard or screen-reader user lands where the page now is.
 */
@Injectable({ providedIn: 'root' })
export class GuideScroller {
  private readonly document = inject(DOCUMENT);

  /**
   * Scrolls to the section with this id; returns whether there was one. `smooth` is for a reader who has just
   * pressed a link; a deep link that opens the page at a section jumps there without animating the whole way.
   */
  to(id: string, smooth = true): boolean {
    const section = this.document.getElementById(id);
    const heading = section?.querySelector<HTMLElement>('[data-guide-heading]');

    if (!section || !heading) {
      return false;
    }

    const calm = this.document.defaultView?.matchMedia('(prefers-reduced-motion: reduce)').matches ?? true;
    section.scrollIntoView({ behavior: smooth && !calm ? 'smooth' : 'auto', block: 'start' });
    heading.focus({ preventScroll: true });
    return true;
  }
}
