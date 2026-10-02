import { routes } from '../app/app.routes';
import { RETRY_AFTER_SECONDS } from '../app/core/platform/service-unavailable';
import {
  CACHE_SECONDS,
  FAILURE_BACKOFF_SECONDS,
  MAX_URLS,
  STALE_SECONDS,
  STATIC_PATHS,
  SitemapCaps,
  SitemapDocument,
  renderSitemapXml,
  sitemapEntries,
} from './sitemap-xml';

/** No cap reached, which is every ordinary render. */
const NO_CAPS: SitemapCaps = { departments: false, categories: false, products: false };

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
    const xml = renderSitemapXml(sitemapEntries(payload() as never), 'https://shop.example.com', NO_CAPS);

    expect(xml.startsWith('<?xml version="1.0" encoding="UTF-8"?>\n<urlset')).toBe(true);
    expect(xml).toContain('<loc>https://shop.example.com/women/jeans</loc>');
    expect(xml).toContain('<lastmod>2026-09-30</lastmod>');
    expect(xml.trimEnd().endsWith('</urlset>')).toBe(true);
    // One <url> per entry, and nothing extra.
    expect(xml.match(/<url>/g)).toHaveLength(7);
  });

  /**
   * **All three caps, not just the products.** The first version bounded only the product query, so the
   * one list an admin can grow without limit (W1-12 gives unlimited category creation) was the one with
   * no ceiling, while the design note claimed the read was "bounded in SQL".
   */
  it('names each cap that was reached, and only those', () => {
    const entries = sitemapEntries(payload() as never);

    const all = renderSitemapXml(entries, 'https://shop.example.com', {
      departments: true,
      categories: true,
      products: true,
    });
    expect(all).toContain('more visible departments than one sitemap may list (Catalog:SitemapMaxDepartments)');
    expect(all).toContain('more visible categories than one sitemap may list (Catalog:SitemapMaxCategories)');
    expect(all).toContain('more visible products than one sitemap may list (Catalog:SitemapMaxProducts)');

    const categoriesOnly = renderSitemapXml(entries, 'https://shop.example.com', {
      ...NO_CAPS,
      categories: true,
    });
    expect(categoriesOnly).toContain('more visible categories');
    expect(categoriesOnly).not.toContain('more visible products');
    expect(categoriesOnly).not.toContain('more visible departments');

    expect(renderSitemapXml(entries, 'https://shop.example.com', NO_CAPS)).not.toContain('<!--');
  });

  it('refuses to exceed the protocol’s per-file limit, and says how many it dropped', () => {
    const many = Array.from({ length: MAX_URLS + 5 }, (_, index) => ({
      path: `/product/p-${index}`,
      lastModified: null,
    }));

    const xml = renderSitemapXml(many, 'https://shop.example.com', NO_CAPS);

    expect(xml.match(/<url>/g)).toHaveLength(MAX_URLS);
    expect(xml).toContain('5 further addresses were dropped');
  });

  it('escapes anything XML cares about', () => {
    const xml = renderSitemapXml([{ path: '/a&b', lastModified: null }], 'https://x.test', NO_CAPS);

    expect(xml).toContain('<loc>https://x.test/a&amp;b</loc>');
  });
});

describe('SitemapDocument', () => {
  let clock = 0;
  const now = () => clock;

  beforeEach(() => {
    clock = 1_000_000;
  });

  let said: [string, string][] = [];

  function document(fetchJson: (url: string, timeoutMs: number) => Promise<unknown>): SitemapDocument {
    said = [];
    return new SitemapDocument(fetchJson, now, (level, message) => said.push([level, message]));
  }

  const headers = { host: 'localhost:4420' };

  /**
   * It asks the **internal** feed, not `/api`. Doc 03 §4 routes `/api` from the public origin, and this
   * is the heaviest read in the system, so the one consumer that needs it is this process.
   */
  it('asks the internal catalogue feed', async () => {
    const asked: string[] = [];
    await document((url) => {
      asked.push(url);
      return Promise.resolve(payload());
    }).answer(headers);

    expect(asked).toHaveLength(1);
    expect(asked[0]).toContain('/internal/catalog/sitemap');
    expect(asked[0]).not.toContain('/api/');
  });

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

  /**
   * **F4: truncation was "reported" only in a public XML comment** - addressed to a crawler, which cannot
   * act on it, and to nobody who can. The party who needs to know that the catalogue has outgrown a
   * single sitemap file is whoever runs the site.
   */
  describe('when a cap is reached', () => {
    it('says so in the log, naming the lists and what to do', async () => {
      await document(() =>
        Promise.resolve(payload({ categoriesTruncated: true, productsTruncated: true })),
      ).answer(headers);

      const errors = said.filter(([level]) => level === 'error').map(([, message]) => message);
      expect(errors).toHaveLength(1);
      expect(errors[0]).toContain('sitemap.xml is incomplete');
      expect(errors[0]).toContain('categories and products');
      expect(errors[0]).toContain('sitemap index');
    });

    it('says it once per snapshot, not once per request', async () => {
      const subject = document(() => Promise.resolve(payload({ productsTruncated: true })));

      await subject.answer(headers);
      await subject.answer(headers);
      await subject.answer(headers);

      expect(said.filter(([level]) => level === 'error')).toHaveLength(1);
    });

    it('says nothing when no cap was reached', async () => {
      await document(() => Promise.resolve(payload())).answer(headers);

      expect(said).toEqual([]);
    });

    /** And it is still in the document, for whoever is reading the document. */
    it('still notes it in the XML', async () => {
      const answer = await document(() =>
        Promise.resolve(payload({ categoriesTruncated: true })),
      ).answer(headers);

      expect(answer.body).toContain('more visible categories');
    });
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

    /**
     * **Serving a stale document is said out loud.** The expensive case is not a whole-catalogue outage -
     * then the pages answer 503 too and a stale `<loc>` costs nothing - but this feed failing alone while
     * the catalogue is healthy, because a URL an admin hid this morning is then still offered while its
     * page answers a real 404, for as long as STALE_SECONDS. That used to happen in silence.
     */
    it('says, once, that the document it is serving is out of date', async () => {
      let healthy = true;
      const subject = document(() =>
        healthy ? Promise.resolve(payload()) : Promise.reject(new Error('down')),
      );

      await subject.answer(headers);
      healthy = false;
      clock += (CACHE_SECONDS + 600) * 1000;

      await subject.answer(headers);
      clock += (FAILURE_BACKOFF_SECONDS + 1) * 1000;
      await subject.answer(headers);

      const stale = said.filter(([, message]) => message.includes('being served from a copy'));
      expect(stale).toHaveLength(1);
      expect(stale[0][0]).toBe('error');
      expect(stale[0][1]).toContain('minutes old');
      expect(stale[0][1]).toContain('still being offered');
    });

    it('reports staleness again after a recovery and a second outage', async () => {
      let healthy = true;
      const subject = document(() =>
        healthy ? Promise.resolve(payload()) : Promise.reject(new Error('down')),
      );

      await subject.answer(headers);
      healthy = false;
      clock += (CACHE_SECONDS + 10) * 1000;
      await subject.answer(headers);

      healthy = true;
      clock += (FAILURE_BACKOFF_SECONDS + 1) * 1000;
      await subject.answer(headers);

      healthy = false;
      clock += (CACHE_SECONDS + 10) * 1000;
      await subject.answer(headers);

      expect(said.filter(([, message]) => message.includes('being served from a copy'))).toHaveLength(2);
    });

    /**
     * **F8: every miss during an outage blocked for the full fetch timeout.** Concurrent requests share
     * one rebuild, which bounds them; sequential ones each paid ten seconds, which is a cheap way to hold
     * request slots on a public route. The answer is unchanged - 503 with `Retry-After` - it just arrives
     * without another attempt behind it.
     */
    it('answers 503 without re-asking a feed that has just failed', async () => {
      let attempts = 0;
      const subject = document(() => {
        attempts++;
        return Promise.reject(new Error('down'));
      });

      expect((await subject.answer(headers)).status).toBe(503);
      expect((await subject.answer(headers)).status).toBe(503);
      expect((await subject.answer(headers)).status).toBe(503);

      expect(attempts).toBe(1);
    });

    it('tries again once the backoff has passed', async () => {
      let attempts = 0;
      const subject = document(() => {
        attempts++;
        return Promise.reject(new Error('down'));
      });

      await subject.answer(headers);
      clock += (FAILURE_BACKOFF_SECONDS + 1) * 1000;
      await subject.answer(headers);

      expect(attempts).toBe(2);
    });

    it('does not let the backoff delay a recovery past its own window', async () => {
      let healthy = false;
      const subject = document(() =>
        healthy ? Promise.resolve(payload()) : Promise.reject(new Error('down')),
      );

      expect((await subject.answer(headers)).status).toBe(503);

      healthy = true;
      clock += (FAILURE_BACKOFF_SECONDS + 1) * 1000;

      expect((await subject.answer(headers)).status).toBe(200);
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

    /** Six hours rather than a day; `sitemap-xml.ts` says why, and this pins the figure. */
    it('serves a five-hour-old copy and refuses a seven-hour-old one', async () => {
      let healthy = true;
      const subject = document(() =>
        healthy ? Promise.resolve(payload()) : Promise.reject(new Error('down')),
      );

      await subject.answer(headers);
      healthy = false;

      clock += 5 * 3600 * 1000;
      expect((await subject.answer(headers)).status).toBe(200);

      clock += 2 * 3600 * 1000;
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
