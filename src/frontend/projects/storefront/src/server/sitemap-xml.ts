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
 * How long a stale document may still be served once the feed has stopped answering.
 *
 * **Six hours, reduced from a day, and the reason is a narrower case than "an outage".** During a
 * whole-catalogue outage a stale `<loc>` costs nothing: the product pages answer 503 too, so a crawler
 * retries and keeps the URL. The case that bites is this feed failing **while the rest of the catalogue
 * is healthy** - it is the heaviest read in the system and so the first thing to time out - because a URL
 * an admin deactivated this morning is then still being offered while its page answers a real 404. Six
 * hours bounds that to a quarter of a day and still covers any plausible deploy, restart or load spike,
 * since a crawler re-reads a sitemap on a scale of hours.
 *
 * The other half of the answer is {@link SitemapDocument}, which logs once when it starts serving a
 * stale document: a shorter window reduces the exposure, and the log is what stops it being silent.
 */
export const STALE_SECONDS = 21_600;

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
 * How long to leave a failing feed alone before asking it again.
 *
 * Every cache miss used to block for the whole of {@link FETCH_TIMEOUT_MS} before answering 503, so a
 * sequence of requests to a public route each paid ten seconds of a server thread - a cheap way to hold
 * request slots. Concurrent misses already shared one rebuild; sequential ones are what this covers. Ten
 * seconds of backoff turns one timeout per request into one per twenty, and costs nothing in recovery:
 * no crawler is waiting on the next ten seconds.
 */
export const FAILURE_BACKOFF_SECONDS = 10;

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
  readonly departmentsTruncated?: boolean;
  readonly categoriesTruncated?: boolean;
}

/** Which of the API's three caps were reached. All three are reported; see the DTO for why. */
export interface SitemapCaps {
  readonly departments: boolean;
  readonly categories: boolean;
  readonly products: boolean;
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
  caps: SitemapCaps,
): string {
  const published = entries.slice(0, MAX_URLS);
  const notes: string[] = [];

  for (const [list, option] of [
    ['departments', 'Catalog:SitemapMaxDepartments'],
    ['categories', 'Catalog:SitemapMaxCategories'],
    ['products', 'Catalog:SitemapMaxProducts'],
  ] as const) {
    if (caps[list]) {
      notes.push(`The catalogue holds more visible ${list} than one sitemap may list (${option}).`);
    }
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
  private snapshot: { sitemap: ApiSitemap; caps: SitemapCaps; builtAt: number } | null = null;

  /**
   * The last rendered document, so the common case - one origin, many crawlers - renders once per
   * snapshot rather than once per request. Keyed by the snapshot it was built from as well as by the
   * origin, so it can never outlive either.
   */
  private memo: { origin: string; builtAt: number; body: string } | null = null;

  private inFlight: Promise<void> | null = null;

  /** When the last rebuild failed, so a failing feed is not re-asked on every request. */
  private lastFailureAt: number | null = null;

  /** Which conditions have been reported already, so each is logged once rather than per request. */
  private reported = { truncatedAt: -1, staleAt: -1, gaveUp: false };

  constructor(
    private readonly fetchJson: (url: string, timeoutMs: number) => Promise<unknown> = fetchJsonOverHttp,
    private readonly now: () => number = () => Date.now(),
    private readonly log: (level: 'warn' | 'error', message: string) => void = (level, message) =>
      level === 'warn' ? console.warn(message) : console.error(message),
  ) {}

  /** The answer for one request. Never throws, and never answers 500. */
  async answer(headers: RequestHeaders): Promise<SitemapAnswer> {
    const origin = publicOrigin(headers);

    const cachedAge = this.ageSeconds();
    if ((cachedAge === null || cachedAge >= CACHE_SECONDS) && !this.failedRecently()) {
      await this.rebuild();
    }

    const age = this.ageSeconds();

    if (age !== null && age < CACHE_SECONDS) {
      return this.served(this.bodyFor(origin), CACHE_SECONDS - age);
    }

    // The rebuild failed and we still hold something. It is better than nothing, up to a point - and a
    // short max-age rather than the full one, so the next crawler gives the catalogue another go.
    if (age !== null && age < STALE_SECONDS) {
      this.reportStale(age);

      return this.served(this.bodyFor(origin), 60);
    }

    // The most severe state change in this class, and it used to produce the same warning as the first
    // failed minute: there is now no sitemap at all, where a moment ago there was a stale one.
    this.reportGaveUp();

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
      const payload = await this.fetchJson(
        `${apiInternalUrl()}/internal/catalog/sitemap`,
        FETCH_TIMEOUT_MS,
      );
      const sitemap = asSitemap(payload);

      this.snapshot = { sitemap, caps: capsOf(sitemap), builtAt: this.now() };
      this.lastFailureAt = null;
      // A fresh document ends the episode, so the next one is reported again.
      this.reported.staleAt = -1;
      this.reported.gaveUp = false;
      this.reportTruncation();
    } catch (error) {
      this.lastFailureAt = this.now();
      // At warning, and the API's own words never travel further than this: a crawler retrying through
      // a long outage must not fill the disk, and the answer above already tells the caller what
      // happened.
      this.log('warn', `sitemap.xml could not be rebuilt: ${describe(error)}`);
    }
  }

  /**
   * True while a failure is recent enough that asking again would only cost another timeout.
   *
   * This is what stops a sequential run of requests each blocking for FETCH_TIMEOUT_MS on a public
   * route. It deliberately does not suppress the answer, only the attempt: a caller still gets a 503
   * with `Retry-After`, it just gets it immediately.
   */
  private failedRecently(): boolean {
    return (
      this.lastFailureAt !== null &&
      (this.now() - this.lastFailureAt) / 1000 < FAILURE_BACKOFF_SECONDS
    );
  }

  /**
   * Says once, in the log, that the published document is short.
   *
   * **Why a log and not only the XML comment.** Truncation used to be "reported" in a comment inside the
   * document - addressed to a crawler, which cannot act on it, and to nobody who can. The one party who
   * needs to know that the catalogue has outgrown a single sitemap file is whoever runs the site.
   */
  private reportTruncation(): void {
    const snapshot = this.snapshot;
    if (!snapshot || snapshot.builtAt === this.reported.truncatedAt) {
      return;
    }

    const reached = (['departments', 'categories', 'products'] as const).filter(
      (list) => snapshot.caps[list],
    );
    if (reached.length === 0) {
      return;
    }

    this.reported.truncatedAt = snapshot.builtAt;
    this.log(
      'error',
      `sitemap.xml is incomplete: the catalogue has more visible ${reached.join(' and ')} than one ` +
        'sitemap may list. Raise the matching Catalog:SitemapMax* limit, or split the document into a ' +
        'sitemap index.',
    );
  }

  /**
   * Says once, in the log, that the document being served is out of date.
   *
   * The expensive case is not a whole-catalogue outage - then the pages answer 503 as well and a stale
   * `<loc>` costs nothing, because a crawler retries and keeps the URL. It is **this feed alone** failing
   * while the catalogue is healthy, because a URL an admin deactivated is then still being offered while
   * its page answers a real 404, for as long as STALE_SECONDS. Without this line that happens in silence.
   */
  private reportStale(ageSeconds: number): void {
    const snapshot = this.snapshot;
    if (!snapshot || snapshot.builtAt === this.reported.staleAt) {
      return;
    }

    this.reported.staleAt = snapshot.builtAt;
    this.log(
      'error',
      `sitemap.xml is being served from a copy ${Math.round(ageSeconds / 60)} minutes old because the ` +
        'catalogue feed is not answering. Addresses an admin has hidden since then are still being offered.',
    );
  }

  /**
   * Says once that there is no sitemap at all any more — the transition from serving a stale document to
   * serving nothing, which is the most severe state this class has and which used to be reported with the
   * same line as the first failed minute.
   */
  private reportGaveUp(): void {
    if (this.reported.gaveUp) {
      return;
    }

    this.reported.gaveUp = true;
    this.log(
      'error',
      'sitemap.xml is no longer being served at all: the catalogue feed has not answered for longer ' +
        'than the stale window, so every address is now withheld rather than offered out of date.',
    );
  }

  private bodyFor(origin: string): string {
    const snapshot = this.snapshot;
    if (!snapshot) {
      throw new Error('bodyFor was called with nothing to render');
    }

    if (this.memo?.origin === origin && this.memo.builtAt === snapshot.builtAt) {
      return this.memo.body;
    }

    const body = renderSitemapXml(sitemapEntries(snapshot.sitemap), origin, snapshot.caps);
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

/** Which caps the API reported, as the renderer and the log both want them. */
export function capsOf(sitemap: ApiSitemap): SitemapCaps {
  return {
    departments: sitemap.departmentsTruncated === true,
    categories: sitemap.categoriesTruncated === true,
    products: sitemap.productsTruncated === true,
  };
}

/**
 * The API's answer, vetted before it is believed.
 *
 * #30's `garbage` mode is why: a proxy or a bad deploy answers 200 with something else entirely, and a
 * reader that trusted the body turned that into a page-wide failure. Here the stakes are a published
 * sitemap, so a body that is not the shape we asked for is a failure and the last good document stands.
 *
 * (This docblock was deleted by accident when `capsOf` was inserted above it; the four tests that cover
 * the behaviour never went away.)
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
    departmentsTruncated: candidate.departmentsTruncated === true,
    categoriesTruncated: candidate.categoriesTruncated === true,
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
