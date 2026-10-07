import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  TestRequest,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { ApplicationRef } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';

import { routes } from '../../app.routes';
import { CategoryNodeDto, DepartmentDto } from '../../core/navigation/catalog-departments.model';
import { NavigationService } from '../../core/navigation/navigation.service';
import { primeContentPages } from '../../core/content/content-pages.testing';
import { PageReload } from '../../core/platform/page-reload';

function node(name: string, slug: string): CategoryNodeDto {
  return {
    id: 1,
    name,
    slug,
    depth: 1,
    description: null,
    imageUrl: null,
    imageSrcSet: null,
    showInMegaMenu: true,
    megaMenuColumn: null,
    ageGroupSlug: null,
    children: [],
  };
}

/**
 * The live catalogue, with `Women › Coats` — a category `navigation.data.ts` has never heard of,
 * which is what an admin adding one looks like to the storefront (ADM-11).
 */
const LIVE: readonly DepartmentDto[] = [
  {
    id: 1,
    name: 'Women',
    slug: 'women',
    description: null,
    heroImageUrl: null,
    heroImageSrcSet: null,
    seoTitle: null,
    seoDescription: null,
    categories: [node('Dresses', 'dresses'), node('Coats', 'coats')],
  },
];

/** The URL the whole of #30 is about: real, live, and absent from the frozen fallback. */
const LIVE_ONLY = '/women/coats';

/** Every way the departments feed can leave the storefront on the fallback tree. */
const UNHEALTHY: readonly (readonly [string, (request: TestRequest) => void])[] = [
  ['a 500', (request) => request.flush('boom', { status: 500, statusText: 'Server Error' })],
  ['an unreachable API', (request) => request.error(new ProgressEvent('offline'))],
  ['a 200 with an empty array', (request) => request.flush([])],
  ['a 200 with a body that is not a tree', (request) => request.flush({ oops: true })],
];

/**
 * What a shopper gets on a **client-side** navigation to a listing the storefront cannot look up.
 *
 * There is no response to set a status on here — this is the browser, after hydration — so the 503
 * is not what these check; `e2e/catalog-availability.e2e.ts` measures that on the wire, which is the
 * only place a status code exists. What these check is that the router lands somewhere honest at
 * all. Before the fix the guard that produced the 404 on the server produced the site's own
 * not-found page here; and the obvious alternative — letting the URL fall through to `**` — would
 * have bounced the shopper silently to the home page.
 */
describe('a listing the storefront could not look up (#30)', () => {
  let harness: RouterTestingHarness;
  let http: HttpTestingController;
  const reload = { reload: vi.fn() };

  beforeEach(() => {
    reload.reload.mockReset();
    TestBed.configureTestingModule({
      providers: [
        provideRouter(routes),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: PageReload, useValue: reload },
      ],
    });
  });

  afterEach(() => {
    TestBed.inject(HttpTestingController).verify();
    // The document outlives a test here, and a `robots` tag left behind would be read by the next
    // one as if its own page had set it.
    document.querySelector('meta[name="robots"]')?.remove();
  });

  function text(): string {
    return (harness.routeNativeElement as HTMLElement).textContent ?? '';
  }

  /**
   * Answers the department feed, then navigates.
   *
   * Priming before `RouterTestingHarness.create` rather than after: the guards cannot settle until
   * the feed has answered, and the harness does not resolve until the guards have.
   */
  async function goTo(url: string, answer: (request: TestRequest) => void): Promise<void> {
    http = TestBed.inject(HttpTestingController);
    TestBed.inject(NavigationService);
    answer(http.expectOne('/api/catalog/departments'));

    // GLB-10: a **single-segment** address waits on the published-page index too, so it is primed for
    // the same reason the departments feed is — the guards cannot settle until it has answered, and
    // the harness does not resolve until the guards have. An empty live index is a fresh shop, so
    // `/not-a-department` is genuinely not a page and the catch-all's 404 is the honest answer rather
    // than the 503 this file is about.
    primeContentPages(http);

    harness = await RouterTestingHarness.create(url);
    await TestBed.inject(ApplicationRef).whenStable();
    harness.detectChanges();
  }

  for (const [label, answer] of UNHEALTHY) {
    describe(`after ${label}`, () => {
      it('says so, rather than saying the page is gone', async () => {
        await goTo(LIVE_ONLY, answer);

        expect(text()).toContain('We could not load this page just now');
        expect(text()).not.toContain('We could not find that page');
      });

      it('keeps the address: no silent bounce to the home page', async () => {
        await goTo(LIVE_ONLY, answer);

        expect(TestBed.inject(Router).url).toBe(LIVE_ONLY);
      });

      it('asks no crawler to drop the URL, and clears one that was asked before', async () => {
        // As a page that really was missing would have left it, earlier in the same visit.
        document.head.insertAdjacentHTML('beforeend', '<meta name="robots" content="noindex">');

        await goTo(LIVE_ONLY, answer);

        expect(document.querySelector('meta[name="robots"]')).toBeNull();
      });
    });
  }

  it('offers a retry that re-requests the address, so the server can answer it properly', async () => {
    await goTo(LIVE_ONLY, (request) => request.flush('boom', { status: 500, statusText: 'x' }));

    const retry = (harness.routeNativeElement as HTMLElement).querySelector<HTMLButtonElement>(
      '.btn--primary',
    );
    expect(retry?.textContent?.trim()).toBe('Try again');

    retry?.click();
    expect(reload.reload).toHaveBeenCalledTimes(1);
  });

  it('offers the seeded departments, which are real pages even when nothing else can be read', async () => {
    await goTo(LIVE_ONLY, (request) => request.flush([]));

    const links = [
      ...(harness.routeNativeElement as HTMLElement).querySelectorAll<HTMLAnchorElement>(
        '.unavailable__links a',
      ),
    ].map((link) => link.getAttribute('href'));

    expect(links).toEqual(['/women', '/men', '/kids', '/new-arrivals', '/sale']);
  });

  /**
   * The other half of the fix, and the half that keeps it a fix rather than a way of switching 404s
   * off: once the live tree has been read, a slug it does not have really is gone.
   */
  describe('once the live catalogue has answered', () => {
    it('serves the live-only listing', async () => {
      await goTo(LIVE_ONLY, (request) => request.flush(LIVE));

      expect(text()).not.toContain('We could not load this page just now');
      expect(text()).not.toContain('We could not find that page');
      http
        .match((request) => request.url === '/api/catalog/products')
        .forEach((request) => request.flush({ items: [], page: 1, pageSize: 24, totalCount: 0 }));
    });

    it('still answers the not-found page, with noindex, for a category it really does not have', async () => {
      // Shirts is a Men's category, and the live tree in hand says Women has no such thing.
      await goTo('/women/shirts', (request) => request.flush(LIVE));

      expect(text()).toContain('We could not find that page');
      expect(document.querySelector('meta[name="robots"]')?.getAttribute('content')).toBe(
        'noindex',
      );
    });

    /**
     * A first segment that is not a department is a 404, not a 503 and not the home page.
     *
     * It used to reach the home page, which #28 is about; what matters to **this** file is the half
     * that has not changed — it is not swept into the 503 route. Nothing creates a department at
     * runtime (there is no create-department endpoint, and W1-12 settled that a department's name and
     * slug are not editable), so the seeded list is closed and the frozen fallback mirrors it: a
     * single segment the tree in hand does not have is not a department whichever tree that was, and
     * the ambiguity #30 exists for does not arise.
     */
    it('answers 404 for a first segment that is not a department, never 503', async () => {
      await goTo('/not-a-department', (request) => request.flush(LIVE));

      expect(TestBed.inject(Router).url).toBe('/not-a-department');
      expect(text()).toContain('We could not find that page');
      expect(text()).not.toContain('We could not load this page just now');
    });

    /**
     * The `noindex` a real 404 sets has to come off when the shopper leaves it. A server render
     * starts from a fresh document, so a crawler never saw this — but "Back to the home page" from
     * a 404 left the home page carrying the tag, and the home page is perfectly indexable.
     * `NotFoundPage` was the only `robots` setter in the storefront without a matching clear.
     */
    it('takes the noindex off again when the shopper leaves the not-found page', async () => {
      await goTo('/women/shirts', (request) => request.flush(LIVE));
      expect(document.querySelector('meta[name="robots"]')?.getAttribute('content')).toBe(
        'noindex',
      );

      await TestBed.inject(Router).navigateByUrl('/');
      harness.detectChanges();
      http
        .match((request) => request.url.startsWith('/api/content/'))
        .forEach((request) => request.flush({ sections: [] }));

      expect(document.querySelector('meta[name="robots"]')).toBeNull();
    });
  });
});
