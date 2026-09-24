import { HomeSectionDto, HomeSectionItemDto, HomeSectionType } from './content.model';

/**
 * Whether a section has anything to draw.
 *
 * The API has already dropped every section that is switched off or outside its schedule, so the
 * only question left is whether what arrived amounts to a block a shopper can use. A section that
 * does not must render **nothing at all** — no empty frame, no heading over a gap, no "coming
 * soon". Four of the ten sections are seeded switched off precisely because they are nothing but
 * artwork and there is no artwork yet (W1-04); the moment the client switches one on without
 * finishing it, this is what stops a hole appearing on the front page.
 *
 * It is a pure function rather than a method on each component so the rule can be read in one
 * place and tested without a browser.
 */
export function hasContent(section: HomeSectionDto): boolean {
  const items = section.items ?? [];

  switch (section.type) {
    // HOME-01, HOME-05, HOME-06: a banner is its artwork. Without a picture there is nothing to
    // show but a coloured rectangle with a heading in it, which is worse than no banner.
    case 'HeroVideo':
    case 'CampaignBanner':
    case 'ActivewearFeature':
      return items.some(hasArtwork);

    // HOME-08: a gallery of no photographs is not a gallery.
    case 'SocialGallery':
      return items.some((item) => !!item.imageUrl);

    // HOME-02: a tile needs somewhere to go and something to call itself. "Somewhere to go" is
    // measured with `toLink`, so a tile whose stored link cannot safely reach an href does not
    // count as content and the section is not drawn half-dead.
    case 'ShopByDepartment':
      return items.some(isUsableTile);

    // HOME-03, HOME-04, HOME-07: either the products the row was drawn from, or — for a trending
    // section the client arranged by hand — its own tiles.
    case 'NewArrivals':
    case 'TrendingItems':
    case 'SaleSection':
      return section.products.length > 0 || items.some(isUsableTile);

    // HOME-09: a benefit is a line of copy; it needs no media at all.
    case 'Benefits':
      return items.some((item) => !!item.title?.trim() || !!item.body?.trim());

    // HOME-10 is a form. It has content by being itself.
    case 'Newsletter':
      return true;

    default:
      return neverSection(section.type);
  }
}

/** Sections in the order the API gave them, minus the ones with nothing in them. */
export function renderableSections(
  sections: readonly HomeSectionDto[] | null | undefined,
): readonly HomeSectionDto[] {
  if (!Array.isArray(sections)) {
    return [];
  }

  return sections.filter((section) => isUsable(section) && hasContent(section));
}

/**
 * Guards against a payload that is a successful response and still unusable — a proxy answering
 * `200 {"oops":true}`, or a JSON naming policy change that makes every field `undefined`. The menu
 * learned this the hard way in W2-03: `catchError` cannot help, because nothing failed.
 */
function isUsable(section: HomeSectionDto | null | undefined): section is HomeSectionDto {
  return (
    !!section &&
    typeof section.id === 'number' &&
    typeof section.type === 'string' &&
    Array.isArray(section.items) &&
    Array.isArray(section.products)
  );
}

/** Whether a tile can be drawn as a link with a name on it. */
export function isUsableTile(item: HomeSectionItemDto): boolean {
  return toLink(destinationOf(item)) !== null && labelOf(item) !== null;
}

/** A tile's destination: its own link, or the department it names. */
export function destinationOf(item: HomeSectionItemDto): string | null {
  if (item.linkUrl?.trim()) {
    return item.linkUrl;
  }

  if (item.departmentSlug?.trim()) {
    return `/${item.departmentSlug}`;
  }

  if (item.productSlug?.trim()) {
    return `/product/${item.productSlug}`;
  }

  return null;
}

/**
 * What a tile calls itself: the title the client typed, or the name of the department it opens.
 * A tile with neither is not rendered, so this never returns an empty string to the template.
 */
export function labelOf(item: HomeSectionItemDto): string | null {
  return item.title?.trim() || item.departmentName?.trim() || null;
}

/** Whether an item carries something to look at. */
function hasArtwork(item: HomeSectionItemDto): boolean {
  return !!item.imageUrl || !!item.mobileImageUrl || !!item.videoUrl;
}

/**
 * The compiler proves the switch is exhaustive. A new section type added to the API without a
 * decision here is a build error rather than a silent blank on the front page.
 */
function neverSection(type: never): boolean {
  void type;
  return false;
}

/** Every type, for tests that must cover all ten blocks rather than the two they remembered. */
export const HOME_SECTION_TYPES: readonly HomeSectionType[] = [
  'HeroVideo',
  'ShopByDepartment',
  'NewArrivals',
  'TrendingItems',
  'CampaignBanner',
  'ActivewearFeature',
  'SaleSection',
  'SocialGallery',
  'Benefits',
  'Newsletter',
];

/**
 * A destination a homepage tile may carry, resolved into something safe to put in the DOM.
 *
 * Most links are site-relative and go through the router. The social gallery (HOME-08) is the
 * exception: those point at profiles on other sites, so they are ordinary anchors, opened in a new
 * tab with `rel="noopener noreferrer"`.
 *
 * Anything that is neither — a `javascript:` URL, a `data:` URL, a protocol-relative
 * `//evil.example` that looks site-relative at a glance — is `null`, and a tile with a null
 * destination is not rendered as a link at all. The admin screens (ADM-17) will validate on the
 * way in; this is what stops an already-stored value reaching an `href`.
 */
export type SectionLink =
  | {
      readonly kind: 'internal';
      readonly path: string;
      /** The query string, already split up, or null when the URL has none. */
      readonly queryParams: Readonly<Record<string, string | string[]>> | null;
    }
  | { readonly kind: 'external'; readonly href: string }
  | null;

export function toLink(url: string | null | undefined): SectionLink {
  const trimmed = url?.trim();
  if (!trimmed) {
    return null;
  }

  if (trimmed.startsWith('//')) {
    return null;
  }

  if (trimmed.startsWith('/')) {
    // `routerLink` takes a *path*: given the whole URL as one command it percent-encodes the `?`
    // and a trending tile pointing at `/women/jeans?fit=wide-leg` lands on a literal path called
    // "jeans?fit=wide-leg", which is a 404 dressed as a filter. The query has to be handed over
    // separately, which is what HOME-04's "each card opens the matching filtered listing" needs.
    const split = trimmed.indexOf('?');
    if (split < 0) {
      return { kind: 'internal', path: trimmed, queryParams: null };
    }

    return {
      kind: 'internal',
      path: trimmed.slice(0, split) || '/',
      queryParams: parseQuery(trimmed.slice(split + 1)),
    };
  }

  const lower = trimmed.toLowerCase();
  if (lower.startsWith('https://') || lower.startsWith('http://')) {
    return { kind: 'external', href: trimmed };
  }

  return null;
}

/**
 * A query string into the shape `routerLink`'s `queryParams` wants. A key that appears more than
 * once becomes an array, because every listing filter is repeatable (`?colour=navy&colour=black`)
 * and keeping only the last value would quietly narrow the listing the tile promised.
 */
function parseQuery(query: string): Readonly<Record<string, string | string[]>> | null {
  const params: Record<string, string | string[]> = {};
  let any = false;

  for (const pair of query.split('&')) {
    if (!pair) {
      continue;
    }

    const equals = pair.indexOf('=');
    const key = decode(equals < 0 ? pair : pair.slice(0, equals));
    const value = equals < 0 ? '' : decode(pair.slice(equals + 1));

    if (!key) {
      continue;
    }

    any = true;
    const existing = params[key];

    if (existing === undefined) {
      params[key] = value;
    } else if (Array.isArray(existing)) {
      existing.push(value);
    } else {
      params[key] = [existing, value];
    }
  }

  return any ? params : null;
}

/** `+` is a space in a form-encoded query; a malformed escape is left as typed rather than thrown. */
function decode(value: string): string {
  const plussed = value.replace(/\+/g, ' ');

  try {
    return decodeURIComponent(plussed);
  } catch {
    return plussed;
  }
}
