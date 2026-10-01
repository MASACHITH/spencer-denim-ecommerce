import { DOCUMENT } from '@angular/common';
import { Injectable, inject } from '@angular/core';

/**
 * Re-requests the current address from the server.
 *
 * A service rather than two words inline, because `location.reload()` cannot be stubbed in a test
 * and a "Try again" button nothing can prove is a button that quietly stops working.
 *
 * Why a full request and not a client-side retry: the page that needs this is one the server
 * answered with a 503, and only the server can answer it with anything else. Re-fetching the
 * departments feed in the browser and re-running the route guard would repaint the right content
 * over a response that still said 503, and would leave the header, the footer and the guard reading
 * trees fetched at different moments. One request puts the whole page — content, menu and status —
 * back in agreement.
 */
@Injectable({ providedIn: 'root' })
export class PageReload {
  private readonly window = inject(DOCUMENT).defaultView;

  /** Does nothing on the server, where there is nothing to reload. */
  reload(): void {
    this.window?.location.reload();
  }
}
