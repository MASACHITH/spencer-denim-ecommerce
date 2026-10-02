import { APIResponse, expect, test } from '@playwright/test';

import { faultyCatalogueURL } from '../playwright.config';

/**
 * What the storefront puts on the wire for a crawler (GLB-11).
 *
 * **Only a real server can show any of this.** `robots.txt` and `sitemap.xml` are answered by Express
 * ahead of the Angular router, so a unit test cannot see them at all; a 404's value is its **status
 * code**, which a component test cannot produce; and the Open Graph tags matter precisely because they
 * are in the HTML before a line of script has run. So everything here is measured with
 * `request.get`, by address, with no browser in the way.
 *
 * `sitemap-xml.spec.ts`, `robots-txt.spec.ts`, `origins.spec.ts` and `app.routes.spec.ts` hold the
 * parts that are decisions rather than answers.
 */

/** The media type a crawler is given, with the parameters it is given. */
function contentType(response: APIResponse): string {
  return response.headers()['content-type'] ?? '';
}

test.describe('robots.txt', () => {
  /**
   * **The one that matters most, and the reason it is answered by Express.**
   *
   * Before this there was no such file in the build, so the request fell through to the Angular
   * router. A crawler answered 5xx for `robots.txt` treats the **whole site** as disallowed until it
   * can read one, so a route table is the last place that answer should be able to come from.
   */
  test('is a real text file, not a rendered page', async ({ request }) => {
    const response = await request.get('/robots.txt');

    expect(response.status()).toBe(200);
    expect(contentType(response)).toContain('text/plain');

    const body = await response.text();
    // If the router had answered, this would be a document.
    expect(body).not.toContain('<!DOCTYPE');
    expect(body).not.toContain('<sd-root');
    expect(body.startsWith('# Colombo Wardrobe')).toBe(true);
  });

  test('lets a crawler into the shop and names the sitemap absolutely', async ({ request, baseURL }) => {
    const body = await (await request.get('/robots.txt')).text();

    expect(body).toContain('User-agent: *');
    expect(body).toContain('Allow: /');
    // Absolute, on the origin the request arrived at, which is the only form the line accepts.
    expect(body).toContain(`Sitemap: ${baseURL}/sitemap.xml`);
  });

  test('keeps a shopper’s own session and the API out', async ({ request }) => {
    const body = await (await request.get('/robots.txt')).text();

    for (const path of ['/account', '/bag', '/checkout', '/api/']) {
      expect(body).toContain(`Disallow: ${path}`);
    }
  });

  /**
   * `/search` answers `noindex` (SRCH-02, W2-07). A `Disallow` would read as the same wish and do the
   * opposite: a crawler that may not fetch the page never reads the `noindex`, and the bare URL stays
   * indexable. Allowing the fetch is what makes the `noindex` effective.
   */
  test('does not disallow /search, which would undo its noindex', async ({ request }) => {
    expect(await (await request.get('/robots.txt')).text()).not.toContain('Disallow: /search');
  });

  /**
   * The other half of that argument, measured rather than asserted in prose: `/search` really does
   * carry `noindex` (SRCH-02, W2-07). The two tests only mean something together — a `Disallow` with
   * no `noindex` behind it would leave the page indexable, and a `noindex` behind a `Disallow` would
   * never be read. This branch does not set that tag and must not contradict it.
   */
  test('/search carries the noindex that makes allowing the crawl the right answer', async ({
    request,
  }) => {
    const html = await (await request.get('/search?q=test')).text();

    expect(html).toContain('<sd-search-page');
    expect(html).toMatch(/<meta[^>]*name="robots"[^>]*content="[^"]*noindex/);
  });

  test('answers HEAD, which link checkers and monitors lead with', async ({ request }) => {
    const response = await request.head('/robots.txt');

    expect(response.status()).toBe(200);
    expect(contentType(response)).toContain('text/plain');
  });

  /**
   * **The whole point of serving it from Express.** This server's catalogue API answers 500 to every
   * product query, which is the state in which `sitemap.xml` cannot be built — and `robots.txt` still
   * has to be a 200, because the alternative is a crawler deciding the entire site is off limits.
   */
  test('is still a 200 when the catalogue API is unhealthy', async ({ request }) => {
    const response = await request.get(`${faultyCatalogueURL}/robots.txt`);

    expect(response.status()).toBe(200);
    expect(await response.text()).toContain('User-agent: *');
  });
});

test.describe('sitemap.xml', () => {
  test('is a valid urlset of absolute addresses', async ({ request }) => {
    const response = await request.get('/sitemap.xml');

    expect(response.status()).toBe(200);
    expect(contentType(response)).toContain('application/xml');

    const body = await response.text();
    expect(body.startsWith('<?xml version="1.0" encoding="UTF-8"?>')).toBe(true);
    expect(body).toContain('<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">');
    expect(body.trimEnd().endsWith('</urlset>')).toBe(true);

    for (const loc of body.matchAll(/<loc>([^<]*)<\/loc>/g)) {
      expect(loc[1]).toMatch(/^https?:\/\//);
    }
  });

  /**
   * It comes from the live catalogue, not from a list in a file: W1-12 lets the client add and hide
   * categories, so a hand-written sitemap would be wrong within a day. The stub's tree is what the
   * mega menu is drawn from, so a listing the menu links to is a listing the sitemap offers.
   */
  test('offers the departments, categories and products the catalogue has', async ({
    request,
    baseURL,
  }) => {
    const body = await (await request.get('/sitemap.xml')).text();
    const origin = baseURL;

    for (const path of [
      '/',
      '/new-arrivals',
      '/sale',
      '/women',
      '/women/jeans',
      // Three levels deep, which is the shape the Kids tree has (docs/08 §9).
      '/kids/girls/girls-dresses',
      '/product/test-product-1',
    ]) {
      expect(body, `${path} is missing from the sitemap`).toContain(`<loc>${origin}${path}</loc>`);
    }
  });

  test('dates what it can, as a date rather than a timestamp', async ({ request }) => {
    const body = await (await request.get('/sitemap.xml')).text();

    expect(body).toMatch(/<lastmod>\d{4}-\d{2}-\d{2}<\/lastmod>/);
    expect(body).not.toMatch(/<lastmod>[^<]*T/);
  });

  /** `/search` is `noindex`, so offering it here would be arguing with ourselves. */
  test('never offers /search, /bag or /account', async ({ request }) => {
    const body = await (await request.get('/sitemap.xml')).text();

    for (const path of ['/search', '/bag', '/account', '/checkout', '/wishlist']) {
      expect(body).not.toContain(`${path}</loc>`);
    }
  });

  /**
   * The storefront's own `API_INTERNAL_URL` is a container address. It must not travel: the sitemap is
   * the one document whose whole job is to be read by somebody else.
   */
  test('never carries the internal API address', async ({ request }) => {
    const body = await (await request.get('/sitemap.xml')).text();

    expect(body).not.toContain('localhost:4421');
    expect(body).not.toContain('/api/');
  });

  test('is cacheable, because it is a public endpoint that enumerates the catalogue', async ({
    request,
  }) => {
    const cacheControl = (await request.get('/sitemap.xml')).headers()['cache-control'] ?? '';

    expect(cacheControl).toContain('public');
    expect(Number(/max-age=(\d+)/.exec(cacheControl)?.[1] ?? 0)).toBeGreaterThan(0);
  });

  test('answers HEAD', async ({ request }) => {
    expect((await request.head('/sitemap.xml')).status()).toBe(200);
  });

  /**
   * #30's lesson, applied to a document. An empty `<urlset>` is a **valid** sitemap that says this
   * shop has no pages, and a crawler has no way to tell it from the truth; a 500 would be the
   * storefront reporting its own internals. 503 with `Retry-After` keeps every URL a crawler already
   * holds, which is the whole difference between an outage and a deletion.
   */
  test.describe('when the catalogue API is unhealthy', () => {
    test('answers 503 with Retry-After, never 500 and never an empty urlset', async ({ request }) => {
      const response = await request.get(`${faultyCatalogueURL}/sitemap.xml`);

      expect(response.status()).toBe(503);
      expect(Number(response.headers()['retry-after'])).toBeGreaterThan(0);

      const body = await response.text();
      expect(body).not.toContain('<urlset');
      // Never a status code, a stack or the API's own words in front of a caller.
      expect(body).not.toContain('Forced failure');
      expect(body).not.toContain('500');
    });
  });
});

test.describe('the metadata a page puts on the wire', () => {
  const PAGES = [
    { label: 'the home page', path: '/', kind: 'website', canonical: '/' },
    { label: 'a department listing', path: '/women', kind: 'website', canonical: '/women' },
    { label: 'a category listing', path: '/women/jeans', kind: 'website', canonical: '/women/jeans' },
    {
      label: 'a product page',
      path: '/product/test-product-1',
      kind: 'product',
      canonical: '/product/test-product-1',
    },
  ] as const;

  for (const page of PAGES) {
    test(`${page.label} describes itself to a crawler and a share card`, async ({
      request,
      baseURL,
    }) => {
      const html = await (await request.get(page.path)).text();
      const origin = baseURL;

      const tag = (selector: string): string | null => {
        const pattern = new RegExp(`<meta[^>]*${selector}[^>]*content="([^"]*)"`, 'i');
        const reversed = new RegExp(`<meta[^>]*content="([^"]*)"[^>]*${selector}`, 'i');
        return pattern.exec(html)?.[1] ?? reversed.exec(html)?.[1] ?? null;
      };

      expect(html).toMatch(/<title>[^<]*Colombo Wardrobe[^<]*<\/title>/);
      expect(tag('name="description"')).toBeTruthy();

      expect(tag('property="og:site_name"')).toBe('Colombo Wardrobe');
      expect(tag('property="og:type"')).toBe(page.kind);
      expect(tag('property="og:title"')).toBeTruthy();
      expect(tag('property="og:description"')).toBeTruthy();
      // Absolute, because the Open Graph consumers do not resolve a relative URL — they drop the tag.
      expect(tag('property="og:url"')).toBe(`${origin}${page.canonical}`);

      // The canonical stays relative on purpose; see `CanonicalLink`.
      expect(/<link[^>]*rel="canonical"[^>]*href="([^"]*)"/.exec(html)?.[1]).toBe(page.canonical);
    });
  }

  test('a product page carries an absolute picture for the share card', async ({ request }) => {
    const html = await (await request.get('/product/test-product-1')).text();

    const image = /<meta[^>]*property="og:image"[^>]*content="([^"]*)"/.exec(html)?.[1];
    expect(image).toBeTruthy();
    expect(image).toMatch(/^https?:\/\//);
    expect(/<meta[^>]*name="twitter:card"[^>]*content="([^"]*)"/.exec(html)?.[1]).toBe(
      'summary_large_image',
    );
  });

  /**
   * "Friendly URLs" (GLB-11) was already satisfied by the existing slug routing, so this confirms it
   * rather than rebuilding it: a department, a category, a three-level category and a product are all
   * addressed by the catalogue's own slugs, answer 200, and say that address is the canonical one.
   * `app.routes.spec.ts` holds the same property as a shape check on the route table.
   */
  test('every page is addressed by its slug, and says so', async ({ request }) => {
    for (const path of ['/women', '/women/jeans', '/kids/girls/girls-dresses', '/product/test-product-1']) {
      const response = await request.get(path);

      expect(response.status(), `${path} should be a page`).toBe(200);
      expect(/<link[^>]*rel="canonical"[^>]*href="([^"]*)"/.exec(await response.text())?.[1]).toBe(path);
    }
  });
});

/**
 * **#28: a mistyped department answered 200 with the home page.**
 *
 * That is a soft 404 — the wrong page under the wrong URL, at a status code that tells a crawler it is
 * the right one, with nothing anywhere reporting it. The status on the wire is the whole of the
 * defect, so it is measured here and nowhere else.
 */
test.describe('an address this site does not have (#28)', () => {
  for (const path of ['/not-a-dept', '/bogus/thing', '/women/jeans/extra/segments', '/wp-admin/setup-config.php']) {
    test(`${path} answers 404, with noindex`, async ({ request }) => {
      const response = await request.get(path);

      expect(response.status()).toBe(404);

      const html = await response.text();
      expect(html).toContain('We could not find that page');
      expect(html).toContain('noindex');
      // A 404 is a deletion, not an outage: it must not also claim to be retryable.
      expect(response.headers()['retry-after']).toBeUndefined();
    });
  }

  /**
   * The other half of the fix, and the reason it is not simply "point the catch-all at the 404 page".
   * The header, the footer and the search form link to these on every page of the site, and their
   * pages belong to GLB-10, W2-07, W2-08, W2-09, W3-01 and W3-02. A 404 on a link the shop's own
   * footer offers would be a worse answer than the redirect.
   */
  // `/search` is deliberately absent: W2-07 built its page while this branch was in flight, so it has
  // a route of its own and must **not** redirect home. `app.routes.spec.ts` holds the same property on
  // the list itself.
  for (const path of ['/bag', '/wishlist', '/account', '/about', '/contact', '/privacy', '/terms', '/returns', '/shipping']) {
    test(`${path} still reaches the home page while its page is unbuilt`, async ({ request }) => {
      const response = await request.get(path);

      expect(response.status()).toBe(200);
      expect(new URL(response.url()).pathname).toBe('/');
    });
  }

  /**
   * **An address *under* an unbuilt page is a 404 with a page in it, not an empty one.**
   *
   * A route's `pathMatch` defaults to `'prefix'`, so the redirects above first claimed everything
   * beneath them too: `/account/orders` matched `account`, the redirect kept the remaining segment,
   * and the answer was a 404 with a **zero-byte body** — a blank screen for a shopper and nothing for
   * a crawler, which is worse than the soft 404 this change exists to remove. Measured on all three.
   */
  for (const path of ['/account/orders', '/bag/checkout', '/about/x']) {
    test(`${path} is a 404 with the not-found page in it, not an empty body`, async ({ request }) => {
      const response = await request.get(path);

      expect(response.status()).toBe(404);

      const html = await response.text();
      expect(html.length).toBeGreaterThan(0);
      expect(html).toContain('We could not find that page');
      expect(html).toContain('noindex');
    });
  }

  /**
   * **The search address, now that W2-07 has built the page, measured rather than reasoned about.**
   *
   * These four shapes were verified by hand on W2-07's branch and asserted nowhere, which is why they
   * are here: two of them **change answer** because of the `**` fix above, and a hand check does not
   * notice that. Angular's router matches paths case-sensitively, so `/SEARCH` never matched the
   * `search` route and fell through to the catch-all; it used to be answered with the home page at 200
   * and is now a 404.
   *
   * 404 is the intended answer and not an oversight. `/SEARCH` is not an address this site has, and
   * the home page served under it was exactly the soft 404 #28 is about — the same thing `/WOMEN` did,
   * and does, so nothing here is special-cased. A case-normalising redirect would be a new behaviour
   * for every route rather than for this one, and it is not in GLB-11's scope; it is written down here
   * so the next person reads a decision rather than guesses at a bug.
   */
  for (const path of ['/SEARCH?q=test', '/Search?q=test']) {
    test(`${path} is a 404, because routes are matched case-sensitively`, async ({ request }) => {
      const response = await request.get(path);

      expect(response.status()).toBe(404);
      expect(await response.text()).toContain('We could not find that page');
    });
  }

  /** Deeper than the search page goes. Its route has no children, so it cannot claim these. */
  for (const path of ['/search/foo', '/search/foo/bar']) {
    test(`${path} is a 404, not the search page and not the home page`, async ({ request }) => {
      const response = await request.get(path);

      expect(response.status()).toBe(404);

      const html = await response.text();
      expect(html).toContain('We could not find that page');
      expect(html).not.toContain('<sd-search-page');
    });
  }

  /** And the two shapes that must keep working: a trailing slash, and a differently-cased parameter. */
  for (const path of ['/search/', '/search?Q=test', '/search?q=test']) {
    test(`${path} still reaches the search page`, async ({ request }) => {
      const response = await request.get(path);

      expect(response.status()).toBe(200);
      expect(await response.text()).toContain('<sd-search-page');
    });
  }

  /** A category the live catalogue does not have was already a 404, and must have stayed one. */
  test('a category that belongs to another department is still a 404', async ({ request }) => {
    const response = await request.get('/women/shirts');

    expect(response.status()).toBe(404);
    expect(await response.text()).toContain('noindex');
  });
});
