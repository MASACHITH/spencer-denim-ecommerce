import { Injectable, inject } from '@angular/core';
import { Meta, Title } from '@angular/platform-browser';
import { BRAND } from '@spencer-denim/shared';

import { CanonicalLink } from './canonical-link';
import { SITE_ORIGIN, absoluteUrl } from './site-origin';

/** What a page is, as far as a crawler and a share card are concerned. */
export type PageKind = 'website' | 'product';

/** Everything a page says about itself in one call. */
export interface PageMetadata {
  /** The full `<title>`, already carrying the brand (see `brandPageTitle`). */
  readonly title: string;
  readonly description: string;
  /** The page's one canonical address, **site-relative** — see {@link CanonicalLink}. */
  readonly path: string;
  readonly kind?: PageKind;
  /** A picture of this page, relative or absolute. Omitted when the page has none worth sharing. */
  readonly imageUrl?: string | null;
  readonly imageAlt?: string | null;
}

/** Every Open Graph and Twitter tag this service owns, so `clear` can be exhaustive by construction. */
const SOCIAL_SELECTORS = [
  'property="og:site_name"',
  'property="og:type"',
  'property="og:title"',
  'property="og:description"',
  'property="og:url"',
  'property="og:locale"',
  'property="og:image"',
  'property="og:image:alt"',
  'name="twitter:card"',
] as const;

/**
 * The one place a page says what it is: title, description, canonical address and the social card
 * (GLB-11).
 *
 * **Why this exists rather than three calls in every page.** Before it, each page set its own title
 * and description and called {@link CanonicalLink} itself, and the Open Graph tags did not exist at
 * all. Adding them page by page would have meant four copies of "turn this page's path into an
 * absolute URL", which is the shape of the two defects this area has already had: a canonical the
 * listing set and nothing cleared, and a `noindex` a 404 left behind on the next page. One service
 * that sets the whole set and clears the whole set has one place to get that wrong.
 *
 * **It does not replace {@link CanonicalLink} or `RobotsMeta`, it uses them.** Those two each own a
 * single tag and know exactly one thing — the canonical address is relative on purpose, and `noindex`
 * belongs to a 404 and never to a 503 (#30). Reimplementing either here would be the second way to do
 * the same thing. `RobotsMeta` is deliberately left to the pages that are not pages, because whether
 * to be indexed is a decision about a status code rather than about metadata.
 *
 * **Why `og:url` is absolute while the canonical stays relative.** A crawler resolves a relative
 * canonical against the document; the Open Graph consumers do not resolve relative URLs at all and
 * drop the tag instead, taking the share card with it. {@link SITE_ORIGIN} is how this gets an origin
 * it is willing to publish.
 */
@Injectable({ providedIn: 'root' })
export class PageMeta {
  private readonly title = inject(Title);
  private readonly meta = inject(Meta);
  private readonly canonical = inject(CanonicalLink);
  private readonly origin = inject(SITE_ORIGIN);

  /** Describes the page in progress. Called again on every change, so it must only ever overwrite. */
  set(page: PageMetadata): void {
    this.title.setTitle(page.title);
    this.meta.updateTag({ name: 'description', content: page.description });
    this.canonical.set(page.path);

    this.meta.updateTag({ property: 'og:site_name', content: BRAND.name });
    this.meta.updateTag({ property: 'og:type', content: page.kind ?? 'website' });
    this.meta.updateTag({ property: 'og:title', content: page.title });
    this.meta.updateTag({ property: 'og:description', content: page.description });
    const url = absoluteUrl(this.origin, page.path);
    if (url) {
      this.meta.updateTag({ property: 'og:url', content: url });
    } else {
      // Unreachable from a route path, which always starts with a single slash — but a tag that is
      // present and wrong is worse than one that is absent, so it is handled rather than assumed.
      this.meta.removeTag('property="og:url"');
    }
    // The shop is in Sri Lanka and writes British English (docs/06 §5). `en_LK` is not a locale the
    // Open Graph consumers recognise, and an unrecognised one is worse than the nearest real one.
    this.meta.updateTag({ property: 'og:locale', content: 'en_GB' });

    const image = page.imageUrl ? absoluteUrl(this.origin, page.imageUrl) : null;

    if (image) {
      this.meta.updateTag({ property: 'og:image', content: image });
      // Only when there is something to say. An empty `og:image:alt` is a worse answer than none.
      if (page.imageAlt) {
        this.meta.updateTag({ property: 'og:image:alt', content: page.imageAlt });
      } else {
        this.meta.removeTag('property="og:image:alt"');
      }
      this.meta.updateTag({ name: 'twitter:card', content: 'summary_large_image' });
    } else {
      // A large-image card with no image renders as a blank slab, so the card shrinks with the page.
      this.meta.removeTag('property="og:image"');
      this.meta.removeTag('property="og:image:alt"');
      this.meta.updateTag({ name: 'twitter:card', content: 'summary' });
    }
  }

  /**
   * Takes the page's claims back down.
   *
   * A page that sets these owns clearing them, for the reason {@link CanonicalLink} records: a server
   * render starts from a fresh document and never saw the leak, but a shopper who navigated from a
   * product to the home page carried the product's `og:url` and picture with them, and anything in the
   * page that reads those tags — a share button, a future prerender — read the wrong ones.
   *
   * The title and description are deliberately **not** cleared: every route sets both, so blanking
   * them here would only flicker the tab through `index.html`'s fallback on the way between pages.
   */
  clear(): void {
    this.canonical.clear();

    for (const selector of SOCIAL_SELECTORS) {
      this.meta.removeTag(selector);
    }
  }
}
