import { isPlatformBrowser } from '@angular/common';
import { DestroyRef, Injectable, PLATFORM_ID, Signal, inject, signal } from '@angular/core';

/** The media query behind the `prefers-reduced-motion` user setting. */
export const REDUCED_MOTION_QUERY = '(prefers-reduced-motion: reduce)';

/**
 * Whether the visitor asked the operating system to reduce motion (docs/04 §4, GLB-12).
 *
 * Components read {@link enabled} to switch off anything that moves by itself, such as the
 * rotating announcement bar. On the server it is `true`: server-rendered HTML must never
 * depend on an animation having run.
 */
@Injectable({ providedIn: 'root' })
export class ReducedMotion {
  private readonly state = signal(true);

  /** `true` when animation should be suppressed. */
  readonly enabled: Signal<boolean> = this.state.asReadonly();

  constructor() {
    if (!isPlatformBrowser(inject(PLATFORM_ID)) || typeof window.matchMedia !== 'function') {
      return;
    }

    const query = window.matchMedia(REDUCED_MOTION_QUERY);
    this.state.set(query.matches);

    const onChange = (event: MediaQueryListEvent) => this.state.set(event.matches);
    query.addEventListener('change', onChange);
    inject(DestroyRef).onDestroy(() => query.removeEventListener('change', onChange));
  }
}
