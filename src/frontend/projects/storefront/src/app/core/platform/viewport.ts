import { isPlatformBrowser } from '@angular/common';
import {
  DestroyRef,
  Injectable,
  PLATFORM_ID,
  Signal,
  afterNextRender,
  inject,
  signal,
} from '@angular/core';

/** docs/04 §4: from 1024 px the listing shows its filters as a sidebar rather than a drawer. */
export const DESKTOP_QUERY = '(min-width: 1024px)';

/**
 * Whether the viewport is wide enough for the desktop layout.
 *
 * This exists so a page can render one thing or the other rather than both. A listing that
 * rendered its filter panel twice — once for the sidebar, once for the drawer — shipped every
 * control twice in the server HTML and kept two copies of a hundred checkboxes in the DOM.
 *
 * It answers `true` until the page has hydrated, which is what the server assumed, and only then
 * asks the browser. Reading `matchMedia` any earlier would let a phone render the drawer while
 * the server had sent the sidebar, and hydration refuses that: it can claim nodes the server
 * sent, not nodes it has never seen. Swapping one for the other a frame later is an ordinary
 * update, and nothing visible moves — the sidebar is `display: none` below 1024 px either way.
 */
@Injectable({ providedIn: 'root' })
export class Viewport {
  private readonly state = signal(true);

  readonly isDesktop: Signal<boolean> = this.state.asReadonly();

  constructor() {
    if (!isPlatformBrowser(inject(PLATFORM_ID)) || typeof window.matchMedia !== 'function') {
      return;
    }

    const destroyRef = inject(DestroyRef);

    afterNextRender(() => {
      const query = window.matchMedia(DESKTOP_QUERY);
      this.state.set(query.matches);

      const onChange = (event: MediaQueryListEvent) => this.state.set(event.matches);
      query.addEventListener('change', onChange);
      destroyRef.onDestroy(() => query.removeEventListener('change', onChange));
    });
  }
}
