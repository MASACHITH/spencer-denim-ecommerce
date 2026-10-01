import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  TestRequest,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';

import { ServerHandover } from '../platform/server-handover';
import { DepartmentDto } from './catalog-departments.model';
import { MEGA_MENU_COLUMN_COUNT, toNavigation } from './navigation.mapper';
import { NavItem, hasMenu } from './navigation.model';
import {
  CatalogTree,
  DEPARTMENTS_TIMEOUT_MS,
  FALLBACK_TREE,
  NavigationService,
} from './navigation.service';
import { PLACEHOLDER_DEPARTMENTS } from './navigation.data';

function department(overrides: Partial<DepartmentDto> = {}): DepartmentDto {
  return {
    id: 1,
    name: 'Women',
    slug: 'women',
    description: null,
    heroImageUrl: null,
    heroImageSrcSet: null,
    seoTitle: null,
    seoDescription: null,
    categories: [],
    ...overrides,
  };
}

function category(
  name: string,
  slug: string,
  overrides: Partial<DepartmentDto['categories'][number]> = {},
) {
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
    ...overrides,
  };
}

function find(items: readonly NavItem[], id: string): NavItem {
  const item = items.find((candidate) => candidate.id === id);
  if (!item) {
    throw new Error(`No navigation item "${id}"`);
  }
  return item;
}

function pathsOf(item: NavItem): string[] {
  return item.columns.flatMap((column) =>
    column.groups.flatMap((group) => [
      group.link.path,
      ...group.children.map((child) => child.path),
    ]),
  );
}

describe('NavigationService', () => {
  let service: NavigationService;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(NavigationService);
  });

  it('lists exactly the five main entries, in order (GLB-05)', () => {
    expect(service.items().map((item) => item.label)).toEqual([
      'Women',
      'Men',
      'Kids',
      'New Arrivals',
      'Sale',
    ]);
  });

  it('gives each entry a route', () => {
    expect(service.items().map((item) => item.path)).toEqual([
      '/women',
      '/men',
      '/kids',
      '/new-arrivals',
      '/sale',
    ]);
  });

  it('opens a mega menu for departments only', () => {
    expect(service.itemsWithMenu().map((item) => item.id)).toEqual(['women', 'men', 'kids']);
    expect(hasMenu(find(service.items(), 'new-arrivals'))).toBe(false);
    expect(hasMenu(find(service.items(), 'sale'))).toBe(false);
  });

  it('marks Sale, and only Sale, with the sale tone', () => {
    expect(service.items().filter((item) => item.tone === 'sale').map((item) => item.id)).toEqual([
      'sale',
    ]);
  });

  it('offers "Shop all" as its own link, not as one of the categories', () => {
    const women = find(service.items(), 'women');

    expect(women.shopAll).toEqual({ label: 'Shop all Women', path: '/women' });
    expect(pathsOf(women).filter((path) => path === '/women')).toHaveLength(0);
  });

  it('builds category routes under the department', () => {
    const women = find(service.items(), 'women');

    expect(pathsOf(women)).toContain('/women/dresses');
    expect(pathsOf(women)).toContain('/women/accessories');
  });

  it('heads a group with a category that has children, and leaves leaf columns untitled', () => {
    const kids = find(service.items(), 'kids');
    const groups = kids.columns.flatMap((column) => column.groups);

    const girls = groups.find((group) => group.link.label === 'Girls');
    expect(girls?.children.map((child) => child.label)).toEqual([
      'Girls Dresses',
      'Girls Tops',
      'Girls Bottoms',
    ]);

    // Women is a flat department: every group is a leaf, so no headings are drawn.
    const women = find(service.items(), 'women');
    expect(
      women.columns.flatMap((column) => column.groups).every((group) => group.children.length === 0),
    ).toBe(true);
  });

  it('nests a child category under its parent category', () => {
    const kids = find(service.items(), 'kids');

    expect(pathsOf(kids)).toContain('/kids/girls');
    expect(pathsOf(kids)).toContain('/kids/girls/girls-dresses');
  });

  it('gives every department a promo card slot', () => {
    for (const item of service.itemsWithMenu()) {
      expect(item.promo).not.toBeNull();
      expect(item.promo?.cta.path.length).toBeGreaterThan(0);
    }
  });

  it('never builds more than four category columns', () => {
    for (const item of service.itemsWithMenu()) {
      expect(item.columns.length).toBeLessThanOrEqual(MEGA_MENU_COLUMN_COUNT);
    }
  });
});

describe('toNavigation', () => {
  it('honours the column admin assigned', () => {
    const items = toNavigation([
      department({
        categories: [
          category('Dresses', 'dresses', { megaMenuColumn: 3 }),
          category('Jeans', 'jeans', { megaMenuColumn: 3 }),
        ],
      }),
    ]);

    const women = find(items, 'women');
    // Both categories sit in column 3; the empty columns are dropped rather than drawn blank.
    expect(women.columns.map((column) => column.key)).toEqual(['column-3']);
    expect(women.columns[0].groups.map((group) => group.link.label)).toEqual(['Dresses', 'Jeans']);
  });

  it('spreads categories across the columns when no column is assigned yet', () => {
    const items = toNavigation([
      department({
        categories: [
          category('A', 'a'),
          category('B', 'b'),
          category('C', 'c'),
          category('D', 'd'),
        ],
      }),
    ]);

    expect(find(items, 'women').columns).toHaveLength(4);
  });

  it('leaves out categories that are not marked for the mega menu', () => {
    const items = toNavigation([
      department({
        categories: [
          category('Dresses', 'dresses'),
          category('Staff only', 'staff-only', { showInMegaMenu: false }),
        ],
      }),
    ]);

    expect(pathsOf(find(items, 'women'))).not.toContain('/women/staff-only');
  });

  it('keeps a department with no categories as a plain link', () => {
    const items = toNavigation([department({ categories: [] })]);

    const women = find(items, 'women');
    expect(women.columns).toHaveLength(0);
    expect(women.promo).toBeNull();
    expect(women.shopAll).toBeNull();
    expect(women.path).toBe('/women');
  });

  it('still returns New Arrivals and Sale when the API sends nothing', () => {
    expect(toNavigation([]).map((item) => item.id)).toEqual(['new-arrivals', 'sale']);
  });
});

/**
 * The departments now come from `GET /api/catalog/departments`. The header is on every page, so
 * what matters as much as the happy path is that nothing about it can empty the menu (lesson L1).
 */
describe('NavigationService and the catalogue API', () => {
  let http: HttpTestingController;
  let service: NavigationService;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(NavigationService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('asks the catalogue for the departments, once', () => {
    service.items();

    http.expectOne('/api/catalog/departments').flush([]);
  });

  it('builds the menu from what the API sends', () => {
    const request = http.expectOne('/api/catalog/departments');
    request.flush([
      department({
        id: 9,
        name: 'Womenswear',
        slug: 'womenswear',
        categories: [category('Jumpsuits', 'jumpsuits')],
      }),
    ]);

    expect(service.items().map((item) => item.label)).toEqual([
      'Womenswear',
      'New Arrivals',
      'Sale',
    ]);
    expect(pathsOf(find(service.items(), 'womenswear'))).toContain('/womenswear/jumpsuits');
  });

  it('shows the seeded menu until the API answers, so SSR never renders an empty header', () => {
    expect(service.items().map((item) => item.label)).toEqual([
      'Women',
      'Men',
      'Kids',
      'New Arrivals',
      'Sale',
    ]);

    http.expectOne('/api/catalog/departments').flush([]);
  });

  it('falls back to the seeded menu when the API fails', () => {
    http
      .expectOne('/api/catalog/departments')
      .flush('boom', { status: 500, statusText: 'Server Error' });

    expect(service.departments()).toEqual(PLACEHOLDER_DEPARTMENTS);
    expect(service.items().map((item) => item.id)).toEqual([
      'women',
      'men',
      'kids',
      'new-arrivals',
      'sale',
    ]);
  });

  it('falls back when the API is unreachable', () => {
    http
      .expectOne('/api/catalog/departments')
      .error(new ProgressEvent('offline'));

    expect(service.departments()).toEqual(PLACEHOLDER_DEPARTMENTS);
  });

  /**
   * Every shape that leaves the storefront without a usable tree, checked on `tree$` as well as on
   * the signal. They have to agree: the vetting was applied only to the signal, so the route guards
   * read the raw response and a `200 []` — which the API legitimately answers for a freshly
   * migrated database before anyone runs the seeder — sent every listing URL to the home page
   * while the mega menu still listed all three departments. `catchError` cannot see either of
   * these: a useless body is still a successful response.
   *
   * Each case also pins `source`. A reader that cannot tell the fallback from a live answer cannot
   * tell "this page is gone" from "I could not find out", which is #30: every one of these is a
   * `fallback`, and no route guard may answer 404 on the strength of one.
   */
  describe('an answer it cannot use', () => {
    async function treeOf(): Promise<CatalogTree> {
      return firstValueFrom(service.tree$);
    }

    it('falls back on an empty catalogue, and the guards see the same tree', async () => {
      http.expectOne('/api/catalog/departments').flush([]);

      expect(service.departments()).toEqual(PLACEHOLDER_DEPARTMENTS);
      expect(await treeOf()).toEqual(FALLBACK_TREE);
    });

    it('falls back on a body that is not an array, rather than throwing in a route guard', async () => {
      http.expectOne('/api/catalog/departments').flush({ oops: true });

      expect(service.departments()).toEqual(PLACEHOLDER_DEPARTMENTS);
      expect(await treeOf()).toEqual(FALLBACK_TREE);
    });

    it('falls back on entries no route could ever match', async () => {
      // A non-empty array is not enough: every department URL would go home while the menu
      // rendered perfectly. `[{"Slug":"women"}]` is what turning off the JSON naming policy
      // would serve, and every `slug` there is `undefined`.
      http
        .expectOne('/api/catalog/departments')
        .flush([{ nope: 1 }, { Slug: 'women' }] as unknown as readonly DepartmentDto[]);

      expect(service.departments()).toEqual(PLACEHOLDER_DEPARTMENTS);
      expect(await treeOf()).toEqual(FALLBACK_TREE);
    });

    it('keeps a payload where at least one department can be routed to, and calls it live', async () => {
      const live = [department({ id: 7, name: 'Womenswear', slug: 'womenswear' })];
      http.expectOne('/api/catalog/departments').flush(live);

      expect(await treeOf()).toEqual({ departments: live, source: 'live' });
    });

    it('falls back on a failure', async () => {
      http
        .expectOne('/api/catalog/departments')
        .flush('boom', { status: 500, statusText: 'Server Error' });

      expect(service.departments()).toEqual(PLACEHOLDER_DEPARTMENTS);
      expect(await treeOf()).toEqual(FALLBACK_TREE);
    });

    it('gives the guards a department to match in every one of those cases', async () => {
      http.expectOne('/api/catalog/departments').flush([]);

      const tree = await treeOf();
      expect(tree.departments.map((candidate) => candidate.slug)).toEqual(['women', 'men', 'kids']);
    });

    /**
     * The one thing the fallback must never claim. Everything downstream of `source` is an SEO
     * decision — a 404 with `noindex`, or a 503 a crawler comes back to — so a fallback labelled
     * `live` would drop real listings out of the index silently (#30).
     */
    it('never labels the seeded tree live, whatever went wrong', async () => {
      http.expectOne('/api/catalog/departments').error(new ProgressEvent('offline'));

      expect((await treeOf()).source).toBe('fallback');
      expect(service.tree().source).toBe('fallback');
    });

    it('starts on the fallback, so nothing reads a live tree it was never given', () => {
      // Before any answer at all: what SSR renders the header from, and what a guard would read if
      // it took the signal instead of waiting for the observable.
      expect(service.tree().source).toBe('fallback');

      http.expectOne('/api/catalog/departments').flush([]);
    });
  });
});

/**
 * `catchError` catches a failure; an answer that never comes is not one. Without a timeout a slow
 * catalogue held every page open for as long as it took — the header is on every page, and the
 * route guards wait on this same feed. A 12 s API gave a department listing a 12 s time to first
 * byte.
 *
 * Its own TestBed: the clock has to be faked before the service subscribes, because that is when
 * the timeout is scheduled.
 */
describe('NavigationService and a catalogue that never answers', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
  });

  afterEach(() => vi.useRealTimers());

  it('settles on the seeded menu instead of holding the page open', async () => {
    const service = TestBed.inject(NavigationService);
    const http = TestBed.inject(HttpTestingController);
    const request = http.expectOne('/api/catalog/departments');

    await vi.advanceTimersByTimeAsync(DEPARTMENTS_TIMEOUT_MS + 1);

    expect(service.departments()).toEqual(PLACEHOLDER_DEPARTMENTS);
    expect(service.items()).toHaveLength(5);
    expect(request.cancelled).toBe(true);

    // And the guards get the same tree, not a pending observable — labelled the fallback, so a
    // slug it has never heard of is not reported as gone (#30).
    expect(await firstValueFrom(service.tree$)).toEqual(FALLBACK_TREE);
  });
});

/**
 * What the render leaves for the browser, and the one thing it must not leave.
 *
 * `tap(give)` used to sit above `timeout` and above the vetting, so whatever the API said was
 * handed over before anything judged it — and a `200 []` is a successful response. The browser then
 * took `[]` out of `TransferState`, found it non-null and made **no request at all**, so a page the
 * server had already answered 503 stayed on the 503 for the rest of the session. A 500, a destroyed
 * connection and a timeout all recovered on hydration, because those hand over nothing; a transient
 * empty catalogue was the one failure that stranded the shopper, and it is one of #30's four modes.
 *
 * `give` is a no-op in the browser, which jsdom is, so the spec watches the call rather than
 * `TransferState`. The behaviour on the wire is in `e2e/catalog-availability.e2e.ts`.
 */
describe('NavigationService and what it hands to the browser', () => {
  const handover = { take: vi.fn(() => null), give: vi.fn() };

  beforeEach(() => {
    handover.take.mockClear();
    handover.give.mockClear();
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: ServerHandover, useValue: handover },
      ],
    });
  });

  it('hands over a tree it is willing to route on', async () => {
    const service = TestBed.inject(NavigationService);
    const live = [department({ id: 7, name: 'Womenswear', slug: 'womenswear' })];
    TestBed.inject(HttpTestingController).expectOne('/api/catalog/departments').flush(live);

    expect((await firstValueFrom(service.tree$)).source).toBe('live');
    expect(handover.give).toHaveBeenCalledWith('catalog:departments', live);
  });

  for (const [label, answer] of [
    ['an empty catalogue', (request: TestRequest) => request.flush([])],
    ['a body that is not a tree', (request: TestRequest) => request.flush({ oops: true })],
    [
      'entries no route could match',
      (request: TestRequest) => request.flush([{ nope: 1 }] as unknown as readonly DepartmentDto[]),
    ],
    [
      'a failure',
      (request: TestRequest) => request.flush('boom', { status: 500, statusText: 'x' }),
    ],
    ['an unreachable API', (request: TestRequest) => request.error(new ProgressEvent('offline'))],
  ] as const) {
    it(`hands over nothing after ${label}, so the browser asks for itself`, async () => {
      const service = TestBed.inject(NavigationService);
      answer(TestBed.inject(HttpTestingController).expectOne('/api/catalog/departments'));

      expect((await firstValueFrom(service.tree$)).source).toBe('fallback');
      expect(handover.give).not.toHaveBeenCalled();
    });
  }

  it('vets what the server handed over rather than trusting it', async () => {
    // The server cannot hand over `[]` any more, but the browser is on the other side of a process
    // boundary and a stale or tampered payload costs one `toTree` call to refuse.
    handover.take.mockReturnValue([] as never);

    const service = TestBed.inject(NavigationService);

    expect(await firstValueFrom(service.tree$)).toEqual(FALLBACK_TREE);
    TestBed.inject(HttpTestingController).verify();
  });
});
