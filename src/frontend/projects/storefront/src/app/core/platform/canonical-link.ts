import { DOCUMENT } from '@angular/common';
import { Injectable, inject } from '@angular/core';

/**
 * The single `<link rel="canonical">` in the document head (GLB-11).
 *
 * A page that sets one is responsible for clearing it when it goes, which is why this is a
 * service and not a few lines in a component: the listing set the tag and nothing removed it, so
 * following the wordmark from `/women/jeans` to the home page left the home page claiming to be
 * canonically the jeans listing. A crawler reads the server-rendered HTML and never saw that, but
 * anything in the page that reads the tag did.
 */
@Injectable({ providedIn: 'root' })
export class CanonicalLink {
  private readonly document = inject(DOCUMENT);

  /** Points the canonical tag at `href`, creating it if this is the first page to want one. */
  set(href: string): void {
    const link =
      this.find() ??
      this.document.head.appendChild(this.document.createElement('link'));

    link.setAttribute('rel', 'canonical');
    link.setAttribute('href', href);
  }

  /** Removes the tag. A page with no canonical address of its own must not inherit one. */
  clear(): void {
    this.find()?.remove();
  }

  private find(): HTMLLinkElement | null {
    return this.document.head.querySelector<HTMLLinkElement>('link[rel="canonical"]');
  }
}
