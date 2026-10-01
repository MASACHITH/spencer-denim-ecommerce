import { routes } from '../app/app.routes';
import { RETRY_AFTER_SECONDS } from '../app/core/platform/service-unavailable';
import {
  CACHE_SECONDS,
  MAX_URLS,
  STALE_SECONDS,
  STATIC_PATHS,
  SitemapDocument,
  renderSitemapXml,
  sitemapEntries,
} from './sitemap-xml';

/** The shape the API answers with, as this module reads it. */
function payload(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    generatedAtUtc: '2026-10-02T00:00:00Z',
    departments: [
      {
        slug: 'women',
        lastModifiedUtc: '2026-09-01T08:00:00Z',
        categories: [
          { slugTrail: ['jeans'], lastModifiedUtc: '2026-09-20T08:00:00Z' },
          { slugTrail: ['girls', 'girls-dresses'], lastModifiedUtc: null },
        ],
      },
    ],
    products: [{ slug: 'wide-leg-jean', lastModifiedUtc: '2026-09-30T11:22:33Z' }],
    productsTruncated: false,
    ...overrides,
  };
}

describe('sitemapEntries', () => {
  it('builds the addresses the route table declares, from the catalogue’s own slugs', () => {
    const paths = sitemapEntries(payload() as never).map((entry) => entry.path);

    expect(paths).toEqual([
      '/',
      '/new-arrivals',
      '/sale',
      '/women',
      '/women/jeans',
      '/women/girls/girls-dresses',
      '/product/wide-leg-jean',
    ]);
  });

  it('reports lastmod as a date, because a timestamp would change on every unrelated save', () => {
    const entries = sitemapEntries(payload() as never);

    expect(entries.find((entry) => entry.path === '/product/wide-leg-jean')?.lastModified).toBe(
      '2026-09-30',
    );
    expect(entries.find((entry) => entry.path === '/women/girls/girls-dresses')?.lastModified).toBeNull();
  });

  /**
   * The slug is written into a public document, and the one thing worse than a short sitemap is an
   * invalid one. `CatalogSlug` and `CK_Products_Slug` make this unreachable from the API; it is checked
   * because the sitemap is the wrong place to find out that something else let a bad slug through.
   */
  it('drops anything that is not a slug rather than writing it into a <loc>', () => {
    const paths = sitemapEntries(
      payload({
        departments: [
          { slug: '../etc', lastModifiedUtc: null, categories: [] },
          {
            slug: 'women',
            lastModifiedUtc: null,
            categories: [{ slugTrail: ['jeans', 'a/b'], lastModifiedUtc: null }],
          },
        ],
        products: [{ slug: 'Not A Slug', lastModifiedUtc: null }, { slug: 'fine-one', lastModifiedUtc: null }],
      }) as never,
    ).map((entry) => entry.path);

    expect(paths).not.toContain('/../etc');
    expect(paths).not.toContain('/women/jeans/a/b');
    expect(paths).toContain('/women');
    expect(paths).toContain('/product/fine-one');
  });

  /**
   * The list in `sitemap-xml.ts` is the sitemap's copy of what `app.routes.ts` declares. This is the
   * guard that stops the two drifting: if one of these stops being a route, the sitemap would be
   * offering a crawler an address the storefront answers 404 for.
   */
  it('only offers static paths the route table still has', () => {
    const declared = new Set(routes.map((route) => route.path));

    for (const path of STATIC_PATHS) {
      expect(declared.has(path.replace(/^\//, '')), `${path} is no longer a route`).toBe(true);
    }
  });

  /** `/search` answers noindex (SRCH-02), so offering it here would be arguing with ourselves. */
  it('never offers /search', () => {
    expect([...STATIC_PATHS]).not.toContain('/search');
  });
});

describe('renderSitemapXml', () => {
  it('writes a valid urlset of absolute addresses', () => {
    const xml = renderSitemapXml(sitemapEntries(payload() as never), 'https://shop.example.com', false);

    expect(xml.startsWith('<?xml version="1.0" encoding="UTF-8"?>\n<urlset')).toBe(true);
    expect(xml).toContain('<loc>https://shop.example.com/women/jeans</loc>');
    expect(xml).toContain('<lastmod>2026-09-30</lastmod>');
    expect(xml.trimEnd().endsWith('</urlset>')).toBe(true);
    // One <url> per entry, and nothing extra.
    expect(xml.match(/<url>/g)).toHaveLength(7);
  });

  it('says so in the document when the catalogue is larger than one sitemap may list', () => {
    const xml = renderSitemapXml(sitemapEntries(payload() as never), 'https://shop.example.com', true);

    expect(xml).toContain('<!-- The catalogue holds more published products');
  });

  it('refuses to exceed the protocol’s per-file limit, and says how many it dropped', () => {
    const many = Array.from({ length: MAX_URLS + 5 }, (_, index) => ({
      path: `/product/p-${index}`,
      lastModified: null,
    }));

    const xml = renderSitemapXml(many, 'https://shop.example.com', false);

    expect(xml.match(/<url>/g)).toHaveLength(MAX_URLS);
    expect(xml).toContain('5 further addresses were dropped');
  });

  it('escapes anything XML cares about', () => {
    const xml = renderSitemapXml([{ path: '/a&b', lastModified: null }], 'https://x.test', false);

    expect(xml).toContain('<loc>https://x.test/a&amp;b</loc>');
  });
});

describe('SitemapDocument', () => {
  let clock = 0;
  const now = () => clock;

  beforeEach(() => {
    clock = 1_000_000;
  });

  function document(fetchJson: (url: string, timeoutMs: number) => Promise<unknown>): SitemapDocument {
    return new SitemapDocument(fetchJson, now);
  }

  const headers = { host: 'localhost:4420' };

  it('serves the catalogue it was given', async () => {
    const answer = await document(() => Promise.resolve(payload())).answer(headers);

    expect(answer.status).toBe(200);
    expect(answer.contentType).toBe('application/xml; charset=utf-8');
    expect(answer.body).toContain('<loc>http://localhost:4420/women/jeans</loc>');
    expect(answer.cacheControl).toContain('max-age=');
  });

  /** A public endpoint that enumerates the catalogue must not be a database query per request. */
  it('reads the catalogue once per cache window, however often it is asked', async () => {
    let reads = 0;
    const subject = document(() => {
      reads++;
      return Promise.resolve(payload());
    });

    await subject.answer(headers);
    await subject.answer(headers);
    clock += (CACHE_SECONDS - 1) * 1000;
    await subject.answer(headers);

    expect(reads).toBe(1);

    clock += 2000;
    await subject.answer(headers);

    expect(reads).toBe(2);
  });

  it('shares one read between callers that miss the cache together', async () => {
    let reads = 0;
    const subject = document(() => {
      reads++;
      return new Promise((resolve) => setTimeout(() => resolve(payload()), 5));
    });

    await Promise.all([subject.answer(headers), subject.answer(headers), subject.answer(headers)]);

    expect(reads).toBe(1);
  });

  describe('when the catalogue is unhappy', () => {
    /**
     * #30's lesson, applied to a document rather than a page: an empty `urlset` is a valid sitemap
     * that says this shop has no pages, and a crawler cannot tell it from the truth. 503 keeps every
     * URL it already knows, and a 500 would be the storefront reporting its own internals.
     */
    it('answers 503 with Retry-After, never 500 and never an empty urlset', async () => {
      const answer = await document(() => Promise.reject(new Error('the catalogue answered 500'))).answer(
        headers,
      );

      expect(answer.status).toBe(503);
      expect(answer.retryAfter).toBe(RETRY_AFTER_SECONDS);
      expect(answer.body).not.toContain('<urlset');
      // Never the API's own words in front of a caller.
      expect(answer.body).not.toContain('500');
      expect(answer.cacheControl).toBe('no-store');
    });

    it('keeps serving the last good document rather than going dark', async () => {
      let healthy = true;
      const subject = document(() =>
        healthy ? Promise.resolve(payload()) : Promise.reject(new Error('down')),
      );

      await subject.answer(headers);
      healthy = false;
      clock += (CACHE_SECONDS + 10) * 1000;

      const answer = await subject.answer(headers);

      expect(answer.status).toBe(200);
      expect(answer.body).toContain('<loc>http://localhost:4420/women/jeans</loc>');
      // Short, so the next crawler gives the catalogue another chance soon.
      expect(answer.cacheControl).toBe('public, max-age=60');
    });

    it('stops serving a stale document once the outage is no longer a blip', async () => {
      let healthy = true;
      const subject = document(() =>
        healthy ? Promise.resolve(payload()) : Promise.reject(new Error('down')),
      );

      await subject.answer(headers);
      healthy = false;
      clock += (STALE_SECONDS + 1) * 1000;

      expect((await subject.answer(headers)).status).toBe(503);
    });

    /** #30's `garbage` mode: a proxy or a bad deploy answering 200 with something else entirely. */
    for (const body of [null, 'a string', { oops: true }, { departments: [], products: 'no' }]) {
      it(`does not publish a sitemap from ${JSON.stringify(body)}`, async () => {
        const answer = await document(() => Promise.resolve(body)).answer(headers);

        expect(answer.status).toBe(503);
      });
    }
  });
});
