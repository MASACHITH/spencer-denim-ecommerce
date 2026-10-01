import { Injectable, inject } from '@angular/core';
import { Meta } from '@angular/platform-browser';

/**
 * The single `<meta name="robots">` in the document head.
 *
 * The same shape, and for the same reason, as {@link CanonicalLink}: a page that sets one is
 * responsible for clearing it when it goes. A server render starts from a fresh document, so a
 * crawler never saw the leak — but a shopper who hit a real 404 and then clicked "Back to the home
 * page" carried `noindex` with them onto a page that is perfectly indexable, and anything in the
 * page that reads the tag (and any future prerender or in-browser crawler) reads it there.
 *
 * One place that knows the word `noindex`, so the only question left is *which* pages ask for it:
 * a 404 does, and a 503 must not (#30) — a crawler that takes the body of an error page at its word
 * would deindex the URL the status code exists to preserve.
 */
@Injectable({ providedIn: 'root' })
export class RobotsMeta {
  private readonly meta = inject(Meta);

  /** Asks for this page to be left out of the index. For a 404, never for a 503. */
  noindex(): void {
    this.meta.updateTag({ name: 'robots', content: 'noindex' });
  }

  /** Removes the tag. A page that is fine must not inherit one from the page before it. */
  clear(): void {
    this.meta.removeTag('name="robots"');
  }
}
