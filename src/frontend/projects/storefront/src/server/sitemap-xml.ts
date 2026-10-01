import { RETRY_AFTER_SECONDS } from '../app/core/platform/service-unavailable';
import { RequestHeaders, apiInternalUrl, publicOrigin } from './origins';

/**
 * `sitemap.xml` for the storefront (GLB-11).
 *
 * **It comes from the live catalogue, not from a list in this file.** W1-12 gave the client the ability
 * to add, hide and remove categories, so a hand-written sitemap would be wrong within a day: it would
 * be offering a crawler listings that answer 404 and withholding the ones the client has just created.
 * The API's `/api/catalog/sitemap` answers with the slugs a visitor may see, decided by the very same
 * predicates the listing and the mega menu use, and this module turns them into addresses — because the
 * route table is the storefront's, not the API's.
 *
 * **Three things a public endpoint that enumerates a catalogue has to get right**, and what is done
 * about each:
 *
 * 1. *It must not be a database query per request.* The rendered document is cached here for
 *    {@link CACHE_SECONDS}, and concurrent misses share one fetch rather than starting one each.
 * 2. *It must not grow without a ceiling.* The API caps the product list (`Catalog:SitemapMaxProducts`)
 *    and says when it did; {@link MAX_URLS} is the sitemap protocol's own per-file limit and this
 *    refuses to exceed it whatever the catalogue holds.
 * 3. *It must not fail loudly when the catalogue is unhappy.* #30's lesson, applied here: the last good
 *    document is served while the API is down, and only when there has never been one does this answer
 *    **503 with `Retry-After`** — never a 500, and never an empty `urlset`, which would read as "this
 *    shop has no pages" and is the one answer that could cost the site its index.
 */

/**
 * How long a rendered document is reused. Fifteen minutes: long enough that a crawler cannot turn this
 * into a catalogue scan, short enough that a product the client published this morning is offered by
 * lunchtime. Crawlers re-read a sitemap on their own schedule, measured in hours, so nothing is lost.
 */
export const CACHE_SECONDS = 900;

/**
 * How long a stale document may still be served once the catalogue has stopped answering. A day: a
 * sitemap that is hours out of date is overwhelmingly better than no sitemap, and past a day the
 * outage is not a blip and a `503` is the more honest answer.
 */
export const STALE_SECONDS = 86_400;

/**
 * How long to wait for the API. Longer than the 2 s the departments feed is given, because this is one
 * query over the whole catalogue and nobody is watching a page render on it; short enough that a stuck
 * API cannot pile up requests on this route.
 */
export const FETCH_TIMEOUT_MS = 10_000;

/**
 * The sitemap protocol's limit for one file. The API's own cap is well below it, so this is the
 * belt-and-braces check rather than the working limit — and if it is ever reached, the answer is a
 * sitemap index pointing at several files, which is a change to make on purpose.
 */
export const MAX_URLS = 50_000;

/**
 * The addresses that are pages in their own right and are not in the catalogue feed.
 *
 * `app.routes.ts` is where they are really declared; this list is the sitemap's copy and
 * `sitemap-xml.spec.ts` fails if one of them stops being a route, so the two cannot drift apart
 * silently. `/search` is absent on purpose — it answers `noindex` (SRCH-02) — and so is every address
 * that is a shopper's own session.
 */
export const STATIC_PATHS = ['/', '/new-arrivals', '/sale'] as const;

/** One address in the document. */
export interface SitemapEntry {
  readonly path: string;
  /** `YYYY-MM-DD`, or null when the catalogue does not know. */
  readonly lastModified: string | null;
}

/** The shape `/api/catalog/sitemap` answers with, as far as this module cares. */
interface ApiSitemap {
  readonly departments: readonly ApiSitemapDepartment[];
  readonly products: readonly ApiSitemapProduct[];
  readonly productsTruncated?: boolean;
}

interface ApiSitemapDepartment {
  readonly slug: string;
  readonly lastModifiedUtc: string | null;
  readonly categories: readonly { readonly slugTrail: readonly string[]; readonly lastModifiedUtc: string | null }[];
}

interface ApiSitemapProduct {
  readonly slug: string;
  readonly lastModifiedUtc: string | null;
}

/** What a caller should put on the wire. */
export interface SitemapAnswer {
  readonly status: number;
  readonly body: string;
  readonly contentType: string;
  readonly cacheControl: string;
  readonly retryAfter?: number;
}

/**
 * Turns the API's answer into the addresses the storefront serves.
 *
 * A department is `/{slug}`, a category is its trail joined under the department, and a product is
 * `/product/{slug}` — the shapes `app.routes.ts` declares. Anything whose slug is not a slug is
 * dropped rather than escaped into a URL: the API should never send one, and a sitemap is read by
 * machines that will not forgive a malformed `<loc>`.
 */
export function sitemapEntries(payload: ApiSitemap): SitemapEntry[] {
  const entries: SitemapEntry[] = [];

  for (const path of STATIC_PATHS) {
    entries.push({ path, lastModified: null });
  }

  for (const department of payload.departments) {
    if (!isSlug(department.slug)) {
      continue;
    }

    entries.push({ path: `/${department.slug}`, lastModified: toDate(department.lastModifiedUtc) });

    for (const category of department.categories ?? []) {
      const trail = category.slugTrail ?? [];
      if (trail.length === 0 || !trail.every(isSlug)) {
        continue;
      }

      entries.push({
        path: `/${department.slug}/${trail.join('/')}`,
        lastModified: toDate(category.lastModifiedUtc),
      });
    }
  }

  for (const product of payload.products) {
    if (!isSlug(product.slug)) {
      continue;
    }

    entries.push({ path: `/product/${product.slug}`, lastModified: toDate(product.lastModifiedUtc) });
  }

  return entries;
}

/**
 * The document. Entries past {@link MAX_URLS} are dropped and said to be dropped: a comment in the
 * file is read by whoever is debugging it, where a silently short sitemap is read by nobody.
 */
export function renderSitemapXml(
  entries: readonly SitemapEntry[],
  origin: string,
  productsTruncated: boolean,
): string {
  const published = entries.slice(0, MAX_URLS);
  const notes: string[] = [];

  if (productsTruncated) {
    notes.push('The catalogue holds more published products than one sitemap may list (Catalog:SitemapMaxProducts).');
  }
  if (entries.length > published.length) {
    notes.push(`${entries.length - published.length} further addresses were dropped at the ${MAX_URLS}-URL limit.`);
  }

  const lines = [
    '<?xml version="1.0" encoding="UTF-8"?>',
    ...notes.map((note) => `<!-- ${escapeXml(note)} -->`),
    '<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">',
  ];

  for (const entry of published) {
    lines.push('  <url>');
    lines.push(`    <loc>${escapeXml(absolute(origin, entry.path))}</loc>`);
    if (entry.lastModified) {
      lines.push(`    <lastmod>${entry.lastModified}</lastmod>`);
    }
    lines.push('  </url>');
  }

  lines.push('</urlset>', '');
  return lines.join('\n');
}

/**
 * The cache and the fetch, kept together because they are one decision: what this route answers is
 * "the newest document we have been able to build, and how old we are willing to let that get".
 *
 * A class rather than module-level variables so a test can have its own, and so two of them cannot
 * share state by accident.
 */
export class SitemapDocument {
  /** The catalogue's own answer, which does not depend on who asked for it. */
  private snapshot: { sitemap: ApiSitemap; truncated: boolean; builtAt: number } | null = null;

  /**
   * The last rendered document, so the common case — one origin, many crawlers — renders once per
   * snapshot rather than once per request. Keyed by the snapshot it was built from as well as by the
   * origin, so it can never outlive either.
   */
  private memo: { origin: string; builtAt: number; body: string } | null = null;

  private inFlight: Promise<void> | null = null;

  constructor(
    private readonly fetchJson: (url: string, timeoutMs: number) => Promise<unknown> = fetchJsonOverHttp,
    private readonly now: () => number = () => Date.now(),
  ) {}

  /** The answer for one request. Never throws, and never answers 500. */
  async answer(headers: RequestHeaders): Promise<SitemapAnswer> {
    const origin = publicOrigin(headers);

    const cachedAge = this.ageSeconds();
    if (cachedAge === null || cachedAge >= CACHE_SECONDS) {
      await this.rebuild();
    }

    const age = this.ageSeconds();

    if (age !== null && age < CACHE_SECONDS) {
      return this.served(this.bodyFor(origin), CACHE_SECONDS - age);
    }

    // The rebuild failed and we still hold something. It is better than nothing, up to a point — and
    // a short max-age rather than the full one, so the next crawler gives the catalogue another go.
    if (age !== null && age < STALE_SECONDS) {
      return this.served(this.bodyFor(origin), 60);
    }

    return {
      status: 503,
      // Not an empty <urlset>: that is a valid document which says the shop has no pages, and a
      // crawler has no way to tell it from the truth. 503 keeps every URL it already knows.
      body: 'The sitemap could not be built because the catalogue could not be read. Please retry.\n',
      contentType: 'text/plain; charset=utf-8',
      cacheControl: 'no-store',
      retryAfter: RETRY_AFTER_SECONDS,
    };
  }

  /** One rebuild at a time: a cache miss on a public endpoint must not become one query per caller. */
  private rebuild(): Promise<void> {
    this.inFlight ??= this.build().finally(() => {
      this.inFlight = null;
    });

    return this.inFlight;
  }

  private async build(): Promise<void> {
    try {
      const payload = await this.fetchJson(`${apiInternalUrl()}/api/catalog/sitemap`, FETCH_TIMEOUT_MS);
      const sitemap = asSitemap(payload);

      this.snapshot = {
        sitemap,
        truncated: sitemap.productsTruncated === true,
        builtAt: this.now(),
      };
    } catch (error) {
      // Logged at warning rather than error, and the API's own words never travel further than this:
      // a crawler retrying through a long outage must not fill the disk, and the answer above already
      // tells the caller what happened.
      console.warn(`sitemap.xml could not be rebuilt: ${describe(error)}`);
    }
  }

  private bodyFor(origin: string): string {
    const snapshot = this.snapshot;
    if (!snapshot) {
      throw new Error('bodyFor was called with nothing to render');
    }

    if (this.memo?.origin === origin && this.memo.builtAt === snapshot.builtAt) {
      return this.memo.body;
    }

    const body = renderSitemapXml(sitemapEntries(snapshot.sitemap), origin, snapshot.truncated);
    this.memo = { origin, builtAt: snapshot.builtAt, body };

    return body;
  }

  private ageSeconds(): number | null {
    return this.snapshot ? (this.now() - this.snapshot.builtAt) / 1000 : null;
  }

  private served(body: string, maxAge: number): SitemapAnswer {
    return {
      status: 200,
      body,
      contentType: 'application/xml; charset=utf-8',
      cacheControl: `public, max-age=${Math.max(60, Math.floor(maxAge))}`,
    };
  }
}

/**
 * The API's answer, vetted before it is believed.
 *
 * #30's `garbage` mode is why: a proxy or a bad deploy answers 200 with something else entirely, and a
 * reader that trusted the body turned that into a page-wide failure. Here the stakes are a published
 * sitemap, so a body that is not the shape we asked for is a failure and the last good document stands.
 */
function asSitemap(payload: unknown): ApiSitemap {
  if (typeof payload !== 'object' || payload === null) {
    throw new Error('the catalogue answered with something that is not an object');
  }

  const candidate = payload as Partial<ApiSitemap>;
  if (!Array.isArray(candidate.departments) || !Array.isArray(candidate.products)) {
    throw new Error('the catalogue answered without a departments or products list');
  }

  return {
    departments: candidate.departments as readonly ApiSitemapDepartment[],
    products: candidate.products as readonly ApiSitemapProduct[],
    productsTruncated: candidate.productsTruncated === true,
  };
}

async function fetchJsonOverHttp(url: string, timeoutMs: number): Promise<unknown> {
  const response = await fetch(url, {
    signal: AbortSignal.timeout(timeoutMs),
    headers: { accept: 'application/json' },
  });

  if (!response.ok) {
    throw new Error(`the catalogue answered ${response.status}`);
  }

  return response.json();
}

/** A slug as the catalogue spells them, and as `CatalogSlug`/`CK_Products_Slug` enforce. */
function isSlug(value: unknown): value is string {
  return typeof value === 'string' && /^[a-z0-9]+(?:-[a-z0-9]+)*$/.test(value);
}

/** `lastmod` as a plain date. A timestamp would change on every unrelated save; the day is the news. */
function toDate(value: string | null | undefined): string | null {
  if (!value) {
    return null;
  }

  const parsed = new Date(value);
  return Number.isNaN(parsed.getTime()) ? null : parsed.toISOString().slice(0, 10);
}

function absolute(origin: string, path: string): string {
  return `${origin}${path}`;
}

/** Everything XML needs escaped, even though a vetted slug contains none of it. */
function escapeXml(value: string): string {
  return value
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&apos;');
}

/** The API's own words never reach a response body; they only reach the log. */
function describe(error: unknown): string {
  return error instanceof Error ? `${error.name}: ${error.message}` : String(error);
}
