import { Locator, Page, expect, test } from '@playwright/test';

import { apiURL } from '../playwright.config';
import { edgesWithoutRing, horizontalOverflow, undersized } from './support/measure';

/**
 * Browser checks for the search overlay and the search results page (W2-07, SRCH-01, SRCH-02).
 *
 * These are the checks jsdom cannot make. The unit suite already pins the term normalisation, the
 * URL ↔ state mapping, the suggestion list and the overlay's state machine; what needs a real
 * browser is **the keyboard driven as a keyboard**, layout at a real viewport, a real focus model,
 * and the status code and HTML on the wire.
 *
 * The catalogue comes from `support/catalog-api-stub.mjs`, whose `/api/catalog/search` mirrors the
 * real endpoint's validation, its filter resolution and the **shape of its search index** rather than
 * being shaped to suit the page. What that buys these tests: "test" and "jeans" match all 120
 * products, "sd-0007" matches exactly one by product code, "cotton" and "linen" match by material,
 * "kanjivaram" matches the one product carrying the longest name the demo catalogue really seeds, and
 * "zzzznothing" matches none.
 *
 * "women" matches **none**, and that is the point of the index shape: the real
 * `ProductSearchIndex.SearchText` is built from category *path names* and carries no department name
 * and no slugs, so a stub that joined those in would have made this suite agree with a production
 * API that behaves differently.
 */

const RESULTS = '/search?q=test';

/** docs/04 §4. */
const BREAKPOINTS = [320, 375, 390, 768, 1024, 1280, 1440, 1920] as const;

/**
 * The longest product name `DemoCatalogueData` seeds (41 characters, `SD-W-0016`).
 *
 * Written out once here rather than at each use, because it carries Tamil combining marks and two
 * copies of it in one file is two chances for them to differ invisibly — which is exactly how the
 * first version of the unit suite's Sinhala/Tamil test compared one virama with another.
 */
const LONGEST_SEEDED_NAME = 'Kanjivaram (காஞ்சிபுரம்) Silk-Blend Saree';

/** The page's own search box, which is visible at every width (the header's is not, below 1280). */
function pageBox(page: Page): Locator {
  return page.locator('.search__field input[role="combobox"]');
}

function pageOptions(page: Page): Locator {
  return page.locator('.search__field [role="option"]');
}

/**
 * Types into the page's box and waits for the list to **settle**.
 *
 * Waiting for the first option to be visible is not enough, and the first version of these tests did
 * exactly that. The category suggestions come out of the cached department tree and appear on the
 * keystroke; the products arrive from the API a debounce later. So "an option is visible" is true
 * while the list still holds only categories, and a test that read the option ids there was reading a
 * list that was about to change underneath it — which is how an arrow-key assertion failed against
 * perfectly correct behaviour.
 */
async function search(page: Page, term: string, settledLabel: string): Promise<Locator> {
  const box = pageBox(page);
  await box.click();
  await box.fill('');
  await box.pressSequentially(term, { delay: 20 });
  await expect(pageOptions(page).first()).toContainText(settledLabel);
  return box;
}

/**
 * The browser's own `/api` calls — every suggestion request, and everything after "Load More" — go
 * to the storefront's origin, which in production is a reverse proxy in front of the API.
 */
async function useStubApi(page: Page): Promise<void> {
  await page.route('**/api/catalog/**', async (route) => {
    const url = new URL(route.request().url());
    const response = await route.fetch({ url: `${apiURL}${url.pathname}${url.search}` });
    await route.fulfill({ response });
  });
}

test.beforeEach(async ({ page }) => {
  await useStubApi(page);
});

test.describe('server-side rendering (GLB-11, SRCH-02)', () => {
  test('a shared link resolves to HTML with the products already in it', async ({ request }) => {
    const response = await request.get(RESULTS);
    const html = await response.text();

    expect(response.status()).toBe(200);
    expect(html).toContain('Test Product 1');
    expect(html).toContain('Test Product 24');
    expect(html).toContain('120');
    expect(html).toMatch(/<h1[^>]*>[\s\S]*?Results for [\s\S]*?test[\s\S]*?<\/h1>/);
  });

  test('the box on the page carries the term, so a phone shows what was searched for', async ({
    request,
  }) => {
    const html = await (await request.get('/search?q=linen%20shirt')).text();

    expect(html).toContain('value="linen shirt"');
  });

  test('the title and the meta description name the search', async ({ request }) => {
    const html = await (await request.get(RESULTS)).text();

    expect(html).toContain('<title>Search: test | Colombo Wardrobe</title>');
    expect(html).toContain('Products matching');
  });

  /**
   * A results page is unbounded crawl space over products that are already indexed at their own
   * addresses. It asks not to be indexed, and it claims no canonical address — but it is still a
   * **200**, because a shopper's shared link has to be a page.
   */
  test('it asks not to be indexed, and is still a 200 with no canonical address', async ({
    request,
  }) => {
    const response = await request.get(RESULTS);
    const html = await response.text();

    expect(response.status()).toBe(200);
    expect(html).toContain('<meta name="robots" content="noindex">');
    expect(html).not.toContain('rel="canonical"');
  });

  test('the page carries no second copy of the catalogue, and no internal address', async ({
    request,
  }) => {
    const html = await (await request.get(RESULTS)).text();
    const state = /<script[^>]*id="ng-state"[^>]*>([\s\S]*?)<\/script>/.exec(html)?.[1] ?? '';

    expect(state).not.toContain('/api/catalog/');
    // From the config rather than written out: a literal port matches nothing once the environment
    // moves it, and the check would go on passing over HTML full of internal addresses.
    expect(html).not.toContain(new URL(apiURL).host);
    expect(html).not.toContain('API_INTERNAL_URL');
  });

  /**
   * The header carries a search box on every page — two of them below 1280 px — and the popup's
   * contents are rendered only once it is open. Otherwise every page view in the shop would ship a
   * few kilobytes of hidden suggestion rows, which is the listing's "every control twice in the
   * server HTML" lesson in a smaller size.
   */
  test('no page ships the suggestions list before it is opened', async ({ request }) => {
    for (const path of ['/', '/women/jeans', RESULTS]) {
      const html = await (await request.get(path)).text();

      expect(html, path).not.toContain('role="option"');
      // The listbox element itself stays, because `aria-controls` must name something real.
      expect(html, path).toContain('role="listbox"');
    }
  });

  test('the browser does not fetch what the server already sent', async ({ page }) => {
    const calls: string[] = [];
    page.on('request', (request) => {
      if (request.url().includes('/api/catalog/')) {
        calls.push(new URL(request.url()).pathname);
      }
    });

    await page.goto(RESULTS);
    await expect(page.locator('.product-grid > li')).toHaveCount(24);

    expect(calls).toEqual([]);
  });

  /**
   * Three `role="search"` landmarks are in this page's DOM at 1280 px — the header's inline box, the
   * header's panel box, and the page's own — and exactly one of the header's two is ever visible. The
   * names have to differ anyway: "only one is visible" is a fact about the stylesheet, and a screen
   * reader's landmark list is not reading the stylesheet.
   */
  test('every search landmark on the page has its own name', async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await page.goto(RESULTS);

    const names = await page
      .locator('[role="search"]')
      .evaluateAll((nodes) => nodes.map((node) => node.getAttribute('aria-label') ?? ''));

    expect(names).toHaveLength(3);
    expect(new Set(names).size).toBe(3);
    expect(names.every((name) => name.length > 0)).toBe(true);
  });

  /**
   * The generated ids restart with every render, which is what a per-request counter means. A
   * module-level `let nextId = 0` lives for the life of the Node process, so two renders of the same
   * page served different ids and every browser that hydrated one started again at 0.
   */
  test('two renders of the same page generate the same ids', async ({ request }) => {
    const ids = async () => {
      const html = await (await request.get(RESULTS)).text();
      return [...html.matchAll(/id="(sd-search-\d+)"/g)].map((match) => match[1]);
    };

    const first = await ids();
    const second = await ids();

    expect(first.length).toBeGreaterThan(0);
    expect(second).toEqual(first);
  });

  test('a single result is rendered as one card, not as a listing of one', async ({ request }) => {
    const html = await (await request.get('/search?q=sd-0007')).text();

    expect(html).toContain('Test Product 7');
    // The count, with Angular's own attributes on the element between the tag and the number.
    expect(html).toMatch(/<strong[^>]*>1<\/strong>\s*product/);
    expect(html).not.toMatch(/<strong[^>]*>1<\/strong>\s*products/);
  });

  test('a restored deep page shows each product once', async ({ page }) => {
    await page.goto('/search?q=test&pages=2');

    await expect(page.locator('.product-grid > li')).toHaveCount(48);

    const names = await page
      .locator('.card__name')
      .evaluateAll((nodes) => nodes.map((node) => node.textContent?.trim() ?? ''));

    expect(new Set(names).size).toBe(48);
  });
});

/**
 * Every one of these used to be a 500 on the public API or an uncaught error in the browser, and all
 * of them are addresses a shopper can reach by hand, by sharing a mangled link, or by pasting.
 */
test.describe('terms that are not words', () => {
  const cases: readonly { readonly label: string; readonly query: string }[] = [
    { label: 'no value at all', query: '?q=' },
    { label: 'no parameter at all', query: '' },
    { label: 'one character', query: '?q=a' },
    { label: 'only one-character words', query: '?q=a%20b' },
    { label: 'two thousand characters', query: `?q=${'x'.repeat(2_000)}` },
    { label: 'a NUL', query: '?q=%00' },
    { label: 'a NUL inside a word', query: '?q=te%00st' },
    // The two unpaired-surrogate cases are not in this loop: they never reach the application, so
    // they cannot assert which page the application rendered. They have their own tests below.
    { label: 'an emoji', query: '?q=%F0%9F%91%97' },
    { label: 'punctuation only', query: '?q=%21%21%21' },
    { label: 'nothing but short words', query: '?q=a%20an%20the' },
    { label: 'more tokens than the API keeps', query: '?q=test%20product%20women%20jeans%20indigo%20black' },
    { label: 'a LIKE wildcard', query: '?q=%25%25' },
    { label: 'a Sinhala term', query: '?q=%E0%B6%94%E0%B7%83%E0%B6%BB%E0%B7%92%E0%B6%BA' },
  ];

  for (const { label, query } of cases) {
    test(`${label} answers the search page, not a fault`, async ({ request }) => {
      const response = await request.get(`/search${query}`);
      const html = await response.text();

      expect(response.status()).toBe(200);
      // **Which page**, not merely which status. The first version of this asserted only a 200 and
      // the absence of the not-found copy — and the home page satisfies both, so the whole loop was
      // blind to a query string that fell through the router to `{path:'**', redirectTo:''}`. That is
      // precisely what one of these cases does (see the surrogate test below).
      expect(html).toContain('<sd-search-page');
      expect(html).not.toContain('<sd-home-page');
      expect(html).not.toContain('<sd-listing-page');
      expect(html).not.toContain('We could not find that page');
      expect(html).not.toContain('sd-catalog-unavailable-page');
    });
  }

  /**
   * An unpaired surrogate in the query string, which **does not reach the application at all**.
   *
   * My first report of this task said the storefront "answers < 500, so there is no site-wide
   * query-string hazard". That was wrong, and wrong in the way a status-code-only assertion makes
   * easy: it is a 200 **rendering the home page**. Angular's `DefaultUrlSerializer` calls
   * `decodeURIComponent`, which throws `URIError` on half a surrogate pair; the router logs NG04018
   * twice and falls through to `{path:'**', redirectTo:''}`. A genuine U+FFFD (`%EF%BF%BD`) renders
   * the search page perfectly, which is what proves U+FFFD is not what arrives.
   *
   * It is **site-wide and pre-existing** — `/?q=`, `/women?colour=` and `/product/x?y=` all do the
   * same — and it is filed as **#79**, not this branch's to fix. What is this branch's is that the
   * suite can see it: the assertion below is the behaviour the storefront **must** have, and
   * `test.fail()` records that it does not have it yet. When #79 lands this test reports "passed
   * unexpectedly" and the marker comes off, rather than quietly locking the wrong answer in.
   */
  test('an unpaired surrogate in the query string still renders the search page', async ({
    request,
  }) => {
    test.fail();

    // A real term with one bad character in it, not the bad character alone: the storefront's own
    // normaliseTerm drops the surrogate and keeps "linen", so a router that handed the query string
    // over at all would produce a search for "linen" — which is what this asserts.
    const response = await request.get('/search?q=linen%ED%A0%BD');
    const html = await response.text();

    expect(response.status()).toBe(200);
    expect(html).toContain('<sd-search-page');
    // **Not merely "the search page".** A #79 fix that rendered /search with the bad q silently
    // dropped — an empty box and "What are you looking for?" — would satisfy a 200 and the tag, and
    // the marker above would come off on a materially different outcome from the one the companion
    // test below documents as intended. A searched term is the outcome. The other direction is
    // already safe: a fix to a 400 keeps this test failing and the marker on.
    expect(html).toContain('Results for');
  });

  /** The same address with a real replacement character, which is what #79's fix has to produce. */
  test('a replacement character is an ordinary term the search page renders', async ({ request }) => {
    const response = await request.get('/search?q=%EF%BF%BD%EF%BF%BD');
    const html = await response.text();

    expect(response.status()).toBe(200);
    expect(html).toContain('<sd-search-page');
  });

  test('a term of only short words shows the prompt rather than an error', async ({ page }) => {
    await page.goto('/search?q=a%20b');

    await expect(page.getByRole('heading', { level: 1 })).toHaveText('Search');
    await expect(page.getByText('at least 2 characters')).toBeVisible();
  });

  test('a term that matches nothing shows the no-results state at a 200', async ({ page }) => {
    // Not "linen": the corpus carries materials now, exactly as the real index does, so "linen"
    // matches forty products here and in production. A term that matches nothing has to be a term
    // that matches nothing.
    const response = await page.goto('/search?q=zzzznothing');

    expect(response?.status()).toBe(200);
    await expect(page.getByText('We found nothing for')).toBeVisible();
    await expect(page.getByText('Check the spelling.')).toBeVisible();
    await expect(page.locator('.product-grid')).toHaveCount(0);
  });
});

/**
 * `/search` is a new single-segment route, and `/:department` matches any single segment. The last
 * time a single-segment route was added it swallowed eleven site-wide links.
 */
/**
 * The filter-rejection path, end to end.
 *
 * `searchFailure`'s `'query'` branch and `searchErrorMessage`'s pass-through of the API's own wording
 * existed from the first commit and **could not be reached in a browser**, because the stub answered
 * 200 to every filter the real API rejects. That is the whole reason the stub now mirrors
 * `ProductListQuery.Create` and `ResolveFiltersAsync`: a failure path with no way to reach it is a
 * failure path nobody has seen.
 */
test.describe('a filter the catalogue cannot answer', () => {
  test('shows the API’s own message, not a generic apology', async ({ page }) => {
    // "jeans" is a category under Women *and* under Men, and a category slug is unique only per
    // department and parent, so the API refuses to guess (doc 08 §3.2).
    await page.goto('/search?q=test&category=jeans');

    // Scoped to the error block: the page also announces the message in its `role="status"` live
    // region, which is correct and which makes an unscoped text locator ambiguous.
    const state = page.locator('.search__results .search__state');
    await expect(state).toContainText('We could not run that search');
    await expect(state).toContainText('exists in more than one department');
    // Not a 400 page and not an empty grid: the search page, saying what to do.
    await expect(page.locator('sd-search-page')).toHaveCount(1);
    await expect(page.locator('.product-grid')).toHaveCount(0);
  });

  test('the same filter with a department is answered normally', async ({ page }) => {
    await page.goto('/search?q=test&category=jeans&department=women');

    await expect(page.locator('.product-grid > li').first()).toBeVisible();
    await expect(page.getByText('We could not run that search')).toHaveCount(0);
  });

  test('a filter that narrows to nothing is the no-results state, not an error', async ({ page }) => {
    // Green, not cream: every third product in this corpus carries the cream swatch, so "cream"
    // narrows to forty rather than to none. The colour that matches nothing has to be one the corpus
    // genuinely does not have — which is the same mistake, one level smaller, as the corpus shape
    // this round was failed for.
    await page.goto('/search?q=test&colour=green');

    await expect(page.locator('.search__results .search__state')).toContainText(
      'We found nothing for',
    );
    await expect(page.getByText('We could not run that search')).toHaveCount(0);
  });

  test('a size filter narrows rather than emptying the page', async ({ page }) => {
    // The stub answered nothing for *every* size and called that parity with "a value it cannot
    // resolve", so the search page showed "no products found" where production shows products — with
    // the panel's own size checkboxes as the way in. A waist label, because this corpus is Women >
    // Jeans and jeans are on the waist scale; the real API's behaviour is pinned in
    // `ProductDetailAndSearchTests.A_size_filter_narrows_a_search_rather_than_emptying_it`.
    await page.goto('/search?q=test&size=26');

    await expect(page.locator('.product-grid > li').first()).toBeVisible();
    await expect(page.getByText('We found nothing for')).toHaveCount(0);
    await expect(page.locator('.search__count')).toContainText('40');
  });

  test('a size from another scale empties it, as it does in production', async ({ page }) => {
    // Measured on the real API: `?q=jeans&size=M` is 0 and `?q=jeans&size=30` is 5. "No results for a
    // size" is sometimes the right answer, which is exactly why a stub answering it *always* could
    // look plausible for so long.
    await page.goto('/search?q=test&size=M');

    await expect(page.locator('.search__results .search__state')).toContainText(
      'We found nothing for',
    );
  });

  test('a price written with a bare decimal point is a filter, not an error', async ({ page }) => {
    // `decimal.TryParse` with `AllowDecimalPoint` accepts "5.", measured against the real
    // `ProductListQuery.Create`; the stub used to reject it, so the storefront's own error path was
    // being exercised against a 400 production never sends.
    await page.goto('/search?q=test&minPrice=5.');

    await expect(page.locator('.product-grid > li').first()).toBeVisible();
    await expect(page.getByText('We could not run that search')).toHaveCount(0);
  });

  test('a material filter really narrows, which is what the no-results copy promises', async ({
    page,
  }) => {
    const all = await (await page.request.get('/search?q=test')).text();
    // Angular's own attributes sit between the tag and the number.
    expect(all).toMatch(/<strong[^>]*>120<\/strong>/);

    await page.goto('/search?q=test&material=linen');

    // Forty of the hundred and twenty, by material — the field the first stub's corpus did not carry
    // at all, while the no-results copy was promising a shopper they could search by it.
    await expect(page.locator('.search__count')).toContainText('40');
  });
});

test.describe('the rest of the site still resolves', () => {
  const addresses: readonly { readonly path: string; readonly component: string }[] = [
    { path: '/', component: 'sd-home-page' },
    { path: '/women', component: 'sd-listing-page' },
    { path: '/women/jeans', component: 'sd-listing-page' },
    { path: '/kids/girls/girls-dresses', component: 'sd-listing-page' },
    { path: '/new-arrivals', component: 'sd-listing-page' },
    { path: '/sale', component: 'sd-listing-page' },
    { path: '/product/test-product-1', component: 'sd-product-page' },
    { path: '/search?q=test', component: 'sd-search-page' },
  ];

  for (const { path, component } of addresses) {
    test(`${path} still renders ${component}`, async ({ page }) => {
      const response = await page.goto(path);

      expect(response?.status()).toBe(200);
      await expect(page.locator(component)).toHaveCount(1);
      await expect(page.locator('sd-search-page')).toHaveCount(path.startsWith('/search') ? 1 : 0);
    });
  }

  for (const path of ['/account']) {
    test(`${path} still goes to the home page`, async ({ page }) => {
      await page.goto(path);

      await expect(page).toHaveURL('/');
      await expect(page.locator('sd-home-page')).toHaveCount(1);
    });
  }

  // `/about` and `/privacy` left that list in W2-11 (GLB-10): they are content-page addresses now, and
  // unpublished ones answer a real 404. What this block is about is unchanged — `search` is a
  // single-segment route and must not shadow, or be shadowed by, the other single-segment routes.
  for (const path of ['/about', '/privacy']) {
    test(`${path} is its own address rather than the search page`, async ({ page }) => {
      const response = await page.goto(path);

      expect(response?.status()).toBe(404);
      await expect(page).toHaveURL(path);
      await expect(page.locator('sd-search-page')).toHaveCount(0);
    });
  }

  // `/bag` left that list in W2-09: it is a real page now (CART-01), and `search` and `bag` are both
  // single-segment routes listed ahead of `:department`, so this also says the two do not shadow each
  // other.
  test('/bag still resolves to the bag page', async ({ page }) => {
    await page.goto('/bag');

    await expect(page).toHaveURL('/bag');
    await expect(page.locator('sd-bag-page')).toHaveCount(1);
    await expect(page.locator('sd-search-page')).toHaveCount(0);
  });

  test('a category from another department is still a real 404', async ({ request }) => {
    const response = await request.get('/women/shirts');

    expect(response.status()).toBe(404);
    expect(await response.text()).toContain('We could not find that page');
  });

  test('the search page is not a department, so no department feed decides it', async ({ page }) => {
    // The listing routes are decided by `canMatch` against the department tree. If /search had been
    // left to fall through to `:department`, this would render the home page instead.
    await page.goto('/search?q=test');

    await expect(page.locator('sd-search-page')).toHaveCount(1);
    await expect(page).toHaveURL('/search?q=test');
  });
});

/**
 * The keyboard, driven as a keyboard. Calling the handler would prove the handler runs; it would not
 * prove that `aria-activedescendant` reaches the DOM, that focus stays in the field, or that Escape
 * does not reach the header.
 */
test.describe('the suggestions overlay (SRCH-01)', () => {
  test('typing opens a combobox popup and lists products and categories', async ({ page }) => {
    await page.goto(RESULTS);

    const box = await search(page, 'jeans', 'Test Product 1');

    await expect(box).toHaveAttribute('aria-expanded', 'true');
    await expect(page.locator('.search__field [role="listbox"]')).toHaveCount(1);
    await expect(page.locator('.search__field .suggestions__heading')).toHaveText([
      'Products',
      'Categories',
    ]);
    // The categories come out of the department tree the storefront already holds, so this list is
    // the API's five products plus the two "Jeans" categories.
    await expect(pageOptions(page)).toHaveCount(7);
  });

  test('the arrow keys move aria-activedescendant and focus never leaves the field', async ({
    page,
  }) => {
    await page.goto(RESULTS);
    const box = await search(page, 'jeans', 'Test Product 1');

    const ids = await pageOptions(page).evaluateAll((nodes) => nodes.map((node) => node.id));
    expect(ids.length).toBeGreaterThan(1);

    await box.press('ArrowDown');
    await expect(box).toHaveAttribute('aria-activedescendant', ids[0]);
    await expect(pageOptions(page).first()).toHaveAttribute('aria-selected', 'true');
    await expect(pageOptions(page).nth(1)).toHaveAttribute('aria-selected', 'false');

    await box.press('ArrowDown');
    await expect(box).toHaveAttribute('aria-activedescendant', ids[1]);

    // Wrapping, decided and written down: up twice from the second option lands on the last.
    await box.press('ArrowUp');
    await box.press('ArrowUp');
    await expect(box).toHaveAttribute('aria-activedescendant', ids[ids.length - 1]);

    // Down from the last wraps to the first.
    await box.press('ArrowDown');
    await expect(box).toHaveAttribute('aria-activedescendant', ids[0]);

    expect(await box.evaluate((element) => element === document.activeElement)).toBe(true);
  });

  test('Enter chooses the highlighted suggestion rather than submitting the typed term', async ({
    page,
  }) => {
    await page.goto(RESULTS);
    const box = await search(page, 'sd-0007', 'Test Product 7');

    await box.press('ArrowDown');
    await box.press('Enter');

    await expect(page).toHaveURL('/product/test-product-7');
  });

  test('Enter with nothing highlighted submits the term', async ({ page }) => {
    await page.goto(RESULTS);
    const box = await search(page, 'jeans', 'Test Product 1');

    await box.press('Enter');

    await expect(page).toHaveURL('/search?q=jeans');
  });

  test('Escape closes the popup without submitting, and the caret stays put', async ({ page }) => {
    await page.goto(RESULTS);
    const box = await search(page, 'jeans', 'Test Product 1');

    await box.press('Escape');

    await expect(box).toHaveAttribute('aria-expanded', 'false');
    // Hidden, not removed: the popup collapses with the `hidden` attribute, which is why the global
    // reset carries `[hidden]{display:none !important}` (lesson L2).
    await expect(page.locator('.search__field .suggestions')).toBeHidden();
    await expect(pageOptions(page).first()).toBeHidden();
    await expect(box).toHaveValue('jeans');
    await expect(page).toHaveURL(RESULTS);
    expect(await box.evaluate((element) => element === document.activeElement)).toBe(true);
  });

  /**
   * **A second Escape must not empty the field, and must not bring the popup back.**
   *
   * Chrome gives `input[type=search]` a native Escape-clear as the keydown's default action. While
   * `preventDefault()` was called only when the popup was open, the second Escape — the one whose
   * only job is to dismiss — cleared the field, and the clear dispatches an `input` event, which
   * `onInput` answered by re-opening the popup. Measured before the fix: `{expanded:"true",
   * value:"", caret:0}`. That is #20's shape in the component whose docblock says it is the opposite
   * of #20.
   *
   * The results page is where this has to be tested: it has no search panel, so nothing else absorbs
   * the second keystroke, and the earlier two-Escape test (in the header panel) asserted only that
   * the panel closed.
   */
  test('a second Escape leaves the field and the popup alone', async ({ page }) => {
    await page.goto(RESULTS);
    const box = await search(page, 'jeans', 'Test Product 1');

    await box.press('Escape');
    await box.press('Escape');
    await box.press('Escape');

    await expect(box).toHaveValue('jeans');
    await expect(box).toHaveAttribute('aria-expanded', 'false');
    await expect(page.locator('.search__field .suggestions')).toBeHidden();
    await expect(page).toHaveURL(RESULTS);
    // The caret is where the shopper left it, not reset to the start by a clear.
    expect(await box.evaluate((element: HTMLInputElement) => element.selectionStart)).toBe(5);
    expect(await box.evaluate((element) => element === document.activeElement)).toBe(true);
  });

  /**
   * #20 one component over: the mega menu re-opened as soon as focus came back to its trigger after
   * Escape. The popup here opens from typing, from ArrowDown and from a click — never from focus.
   */
  test('the popup does not re-open when focus returns after Escape', async ({ page }) => {
    await page.goto(RESULTS);
    const box = await search(page, 'jeans', 'Test Product 1');
    await box.press('Escape');

    await page.locator('h1').click();
    await box.focus();

    await expect(box).toHaveAttribute('aria-expanded', 'false');
    await expect(page.locator('.search__field .suggestions')).toBeHidden();
  });

  /**
   * Below 1280 px the header's box lives in a panel that `SiteHeader` closes on Escape anywhere
   * inside it. One Escape must not do both, or dismissing the suggestions would tear the panel down
   * and throw focus back to the toggle.
   */
  test('one Escape closes the suggestions, a second closes the header panel', async ({ page }) => {
    await page.setViewportSize({ width: 768, height: 900 });
    await page.goto('/');

    await page.getByTestId('search-toggle').click();
    const panel = page.locator('#header-search-panel');
    await expect(panel).toBeVisible();

    const box = panel.locator('input[role="combobox"]');
    await box.pressSequentially('jeans', { delay: 20 });
    await expect(panel.locator('[role="option"]').first()).toContainText('Test Product 1');

    await box.press('Escape');
    await expect(box).toHaveAttribute('aria-expanded', 'false');
    await expect(panel).toBeVisible();
    expect(await box.evaluate((element) => element === document.activeElement)).toBe(true);

    await box.press('Escape');
    await expect(panel).toBeHidden();
    // The two assertions this test was missing. The second Escape closes the panel; it must not also
    // empty the field, or a shopper who dismissed the suggestions and then dismissed the panel loses
    // their typing — and re-opening the panel would show them an empty box and the browse list.
    await expect(box).toHaveValue('jeans');
    await expect(box).toHaveAttribute('aria-expanded', 'false');

    await page.getByTestId('search-toggle').click();
    await expect(panel).toBeVisible();
    await expect(box).toHaveValue('jeans');
  });

  test('Tab closes the popup without choosing anything', async ({ page }) => {
    await page.goto(RESULTS);
    const box = await search(page, 'jeans', 'Test Product 1');
    await box.press('ArrowDown');

    await box.press('Tab');

    await expect(box).toHaveAttribute('aria-expanded', 'false');
    await expect(page).toHaveURL(RESULTS);
  });

  test('a mouse click on a suggestion opens it', async ({ page }) => {
    await page.goto(RESULTS);
    await search(page, 'sd-0007', 'Test Product 7');

    await pageOptions(page).first().click();

    await expect(page).toHaveURL('/product/test-product-7');
  });

  test('one request serves a word typed straight through', async ({ page }) => {
    // Started from a page with no term in the box, deliberately. Opening the popup on a page that
    // already has one asks for suggestions for *that* term, and whether the debounce has time to
    // fire for it before the first keystroke depends on how busy the machine is — which made the
    // first version of this test flaky rather than wrong.
    await page.goto('/search');

    const calls: string[] = [];
    page.on('request', (request) => {
      if (request.url().includes('/api/catalog/search')) {
        calls.push(new URL(request.url()).searchParams.get('q') ?? '');
      }
    });

    await search(page, 'jeans', 'Test Product 1');

    expect(calls).toEqual(['jeans']);
  });

  test('a term the API would refuse never costs a request', async ({ page }) => {
    // From a page with no term in the box, for the same reason as the test above: opening the popup
    // on a page that already has one asks for suggestions for *that* term, and whether the debounce
    // beats `fill('')` depends on how busy the machine is.
    await page.goto('/search');

    const calls: string[] = [];
    page.on('request', (request) => {
      if (request.url().includes('/api/catalog/search')) {
        calls.push(request.url());
      }
    });

    const box = pageBox(page);
    await box.click();
    await box.fill('');
    await box.pressSequentially('a b', { delay: 20 });
    await page.waitForTimeout(500);

    expect(calls).toEqual([]);
  });

  test('a submitted search is offered again next time, and can be cleared', async ({ page }) => {
    await page.goto('/search?q=jeans');
    await expect(page.locator('.product-grid > li').first()).toBeVisible();

    // A fresh page view, reading the history back out of this browser's storage.
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.goto('/');
    const header = page.locator('.site-header__search-inline input[role="combobox"]');
    await header.press('ArrowDown');

    await expect(page.locator('.site-header__search-inline .suggestions__heading')).toHaveText(
      'Recent searches',
    );
    await expect(page.locator('.site-header__search-inline [role="option"]')).toHaveText(['jeans']);

    await page.locator('.site-header__search-inline .suggestions__clear').click();

    await expect(page.locator('.site-header__search-inline .suggestions__heading')).toHaveText(
      'Browse the shop',
    );
    await expect(page.locator('.site-header__search-inline [role="option"]')).toHaveText([
      'Women',
      'Men',
      'Kids',
      'New Arrivals',
      'Sale',
    ]);
  });

  /**
   * SRCH-01 asks for popular searches and nothing in this system records what anyone has searched
   * for. What the overlay offers instead is the catalogue's own entrances, and the one thing it must
   * never do is call them popular.
   */
  test('nothing in the overlay claims to be popular', async ({ page }) => {
    await page.goto(RESULTS);
    const box = pageBox(page);
    await box.click();
    await box.fill('');
    await box.press('ArrowDown');
    await expect(pageOptions(page).first()).toBeVisible();

    const overlay = await page.locator('.search__field .suggestions').innerText();

    expect(overlay.toLowerCase()).not.toContain('popular');
  });
});

test.describe('responsive and accessible', () => {
  for (const width of BREAKPOINTS) {
    test(`no horizontal scroll at ${width} px, with the overlay open`, async ({ page }) => {
      await page.setViewportSize({ width, height: 900 });
      await page.goto(RESULTS);

      expect(await horizontalOverflow(page)).toBe(0);

      // The longest product name the demo catalogue really seeds: 41 characters, Tamil script with
      // combining marks. The first version of this measured against "Tailored Wool-Look Trousers",
      // which is only the third longest — and the two above it are both Indic, where a combining
      // mark adds no break opportunity and `overflow-wrap: anywhere` is what has to save the layout.
      await search(page, 'kanjivaram', LONGEST_SEEDED_NAME);

      expect(await horizontalOverflow(page)).toBe(0);
    });
  }

  for (const width of [320, 390, 768, 1280]) {
    test(`every suggestion is a 44 px target at ${width} px`, async ({ page }) => {
      await page.setViewportSize({ width, height: 900 });
      await page.goto(RESULTS);
      await search(page, 'kanjivaram', LONGEST_SEEDED_NAME);

      // `undersized` reports an empty array both when everything passes and when its selector
      // matched nothing, so what it matched is counted first. Without this the assertion below
      // could not fail.
      expect(await pageOptions(page).count()).toBeGreaterThan(0);

      expect(await undersized(page, '.search__field [role="option"]')).toEqual([]);
    });
  }

  test('the Clear button on the history is a 44 px target at 320 px', async ({ page }) => {
    await page.setViewportSize({ width: 320, height: 900 });
    await page.goto('/search?q=jeans');
    await expect(page.locator('.product-grid > li').first()).toBeVisible();

    const box = pageBox(page);
    await box.click();
    await box.fill('');
    await box.press('ArrowDown');

    const clear = page.locator('.search__field .suggestions__clear');
    await expect(clear).toBeVisible();

    expect(await undersized(page, '.search__field .suggestions__clear')).toEqual([]);
  });

  for (const width of [320, 768, 1280]) {
    test(`the controls on the results page are 44 px targets at ${width} px`, async ({ page }) => {
      await page.setViewportSize({ width, height: 900 });
      await page.goto('/search?q=test&department=women');

      // Each clause guarded on its own. `count()` over the compound selector passes on the first
      // clause alone, so a later one could silently match nothing and the measurement over it would
      // be vacuous — the same hole as `undersized` returning [] for a selector that matched nothing,
      // one level up.
      const clauses = [
        '.search__bar button',
        '.search__bar select',
        '.search__chips button',
        '.search__more button',
      ];

      for (const clause of clauses) {
        expect(await page.locator(clause).count(), clause).toBeGreaterThan(0);
        expect(await undersized(page, clause), clause).toEqual([]);
      }
    });
  }

  /**
   * A focus ring that a stylesheet declares and an ancestor crops is the bug `edgesWithoutRing`
   * exists for (lesson L4). Reading `outlineWidth` back would pass over a cropped one.
   */
  test('the search field and Load More both draw a focus ring that reaches the screen', async ({
    page,
  }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await page.goto(RESULTS);

    const box = pageBox(page);
    await box.focus();
    expect(await edgesWithoutRing(page, box)).toEqual([]);

    const more = page.locator('.search__more button');
    await more.focus();
    expect(await edgesWithoutRing(page, more)).toEqual([]);
  });

  test('the filters are a drawer below 1024 px and a sidebar above it, never both', async ({
    page,
  }) => {
    await page.setViewportSize({ width: 390, height: 900 });
    await page.goto(RESULTS);

    await expect(page.locator('.search__sidebar')).toHaveCount(0);
    await expect(page.locator('sd-listing-filters')).toHaveCount(1);

    await page.setViewportSize({ width: 1280, height: 900 });
    await page.goto(RESULTS);

    await expect(page.locator('.search__sidebar')).toHaveCount(1);
    await expect(page.locator('sd-listing-filters')).toHaveCount(1);
  });

  test('Load More keeps the scroll position and adds a page', async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await page.goto(RESULTS);

    const button = page.locator('.search__more button');
    await button.scrollIntoViewIfNeeded();
    const before = await page.evaluate(() => window.scrollY);

    await button.click();
    await expect(page.locator('.product-grid > li')).toHaveCount(48);

    expect(await page.evaluate(() => window.scrollY)).toBe(before);
    await expect(page).toHaveURL(/pages=2/);
  });
});
