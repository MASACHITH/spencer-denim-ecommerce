import { UrlSegment } from '@angular/router';

import { CategoryNodeDto, DepartmentDto } from './catalog-departments.model';
import { categoryVerdict, departmentVerdict } from './catalog-route.guards';
import { CatalogTree, FALLBACK_TREE } from './navigation.service';

function node(name: string, slug: string, children: CategoryNodeDto[] = []): CategoryNodeDto {
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
    children,
  };
}

function department(slug: string, categories: CategoryNodeDto[]): DepartmentDto {
  return {
    id: 1,
    name: slug,
    slug,
    description: null,
    heroImageUrl: null,
    heroImageSrcSet: null,
    seoTitle: null,
    seoDescription: null,
    categories,
  };
}

/** The live catalogue, with a category the seeded fallback has never heard of (`coats`). */
const LIVE: CatalogTree = {
  source: 'live',
  departments: [
    department('women', [node('Dresses', 'dresses'), node('Coats', 'coats')]),
    department('kids', [node('Girls', 'girls', [node('Girls Dresses', 'girls-dresses')])]),
  ],
};

/** The same tree, mislabelled — the one combination `toTree` must never produce. */
const FALLBACK_WITH_COATS: CatalogTree = { ...LIVE, source: 'fallback' };

function url(path: string): UrlSegment[] {
  return path
    .split('/')
    .filter((segment) => segment.length > 0)
    .map((segment) => new UrlSegment(segment, {}));
}

/**
 * The three-way answer the route table is built on (#30).
 *
 * `known` and `missing` were always there. `unconfirmed` is the one the storefront used to collapse
 * into `missing`: the tree in hand was the frozen seed copy, so it had never been told about
 * anything an admin added, and every one of those pages was reported gone — a 404 with `noindex` —
 * for as long as `GET /api/catalog/departments` was unhappy.
 *
 * These are the pure functions behind the guards. What they decide is checked on the wire in
 * `e2e/catalog-availability.e2e.ts`, because a status code is not a return value.
 */
describe('the catalogue verdict on a URL', () => {
  describe('with a live tree in hand', () => {
    it('knows a department it has', () => {
      expect(departmentVerdict(LIVE, url('/women'))).toBe('known');
    });

    it('calls a department it does not have missing: the catalogue has been asked', () => {
      expect(departmentVerdict(LIVE, url('/not-a-department'))).toBe('missing');
    });

    it('knows a category it has', () => {
      expect(categoryVerdict(LIVE, url('/women/coats'))).toBe('known');
    });

    it('calls a category it does not have missing — /women/shirts is a Men’s category', () => {
      expect(categoryVerdict(LIVE, url('/women/shirts'))).toBe('missing');
    });

    it('reads the deepest segment, so a three-level URL resolves', () => {
      expect(categoryVerdict(LIVE, url('/kids/girls/girls-dresses'))).toBe('known');
    });

    it('searches the whole department rather than trusting the middle segments', () => {
      // `/women/anything/coats` still resolves: the breadcrumb is drawn from the tree, not the URL.
      expect(categoryVerdict(LIVE, url('/women/anything/coats'))).toBe('known');
    });

    it('does not take a department as a category of itself', () => {
      expect(categoryVerdict(LIVE, url('/women/women'))).toBe('missing');
    });
  });

  describe('with the fallback tree in hand', () => {
    it('serves what the seeded tree does have: the menu and the routes still agree', () => {
      expect(departmentVerdict(FALLBACK_TREE, url('/women'))).toBe('known');
      expect(categoryVerdict(FALLBACK_TREE, url('/women/dresses'))).toBe('known');
    });

    /** The measured defect of #30: `Women › Coats` exists live and not in `navigation.data.ts`. */
    it('cannot say a category it has never heard of is gone', () => {
      expect(categoryVerdict(FALLBACK_TREE, url('/women/coats'))).toBe('unconfirmed');
    });

    /**
     * These two are about the verdict only, and the route table deliberately does **not** act on
     * either of them: an unknown *department* goes to the home page during an outage exactly as it
     * did before #30. A first pass did route them, and `:department` turned out to be the whole
     * catch-all surface of the site — `/checkout`, `/login`, the locale prefixes, `/assets/...` and
     * `/api/...` all answered 503 while the feed was unhealthy. `isUnconfirmedCatalogue` therefore
     * requires a department the tree already holds, which overrides the second verdict below.
     *
     * The functions keep the honest answer because the question will be asked again: W1-12 (ADM-11)
     * makes departments addable, and the case then wants an allow-list — a 503 only for a first
     * segment the live tree would plausibly own — built beside the thing that creates departments.
     * Until then, read `app.routes.ts` for what is actually served, not these two expectations.
     */
    it('will not call an unheard-of department gone, though nothing routes on that yet', () => {
      expect(departmentVerdict(FALLBACK_TREE, url('/accessories'))).toBe('unconfirmed');
    });

    it('will not call a category under an unknown department gone (overridden by the guard)', () => {
      expect(categoryVerdict(FALLBACK_TREE, url('/accessories/belts'))).toBe('unconfirmed');
    });

    /**
     * The property, rather than a list of cases: `missing` is a claim only a live answer earns.
     * Asserting it over a tree whose *contents* would otherwise justify `missing` is what makes
     * this about the label and not about the slugs.
     */
    it('never answers "missing" from a fallback tree, whatever the slugs are', () => {
      const paths = [
        '/women',
        '/women/dresses',
        '/women/shirts',
        '/anything',
        '/anything/at-all',
        '/anything/at/all',
      ];

      for (const tree of [FALLBACK_TREE, FALLBACK_WITH_COATS]) {
        for (const path of paths) {
          expect(departmentVerdict(tree, url(path))).not.toBe('missing');
          expect(categoryVerdict(tree, url(path))).not.toBe('missing');
        }
      }
    });
  });

  describe('a URL that is not a slug', () => {
    it('is unconfirmed against the fallback and missing against a live tree', () => {
      expect(departmentVerdict(LIVE, url('/ '))).toBe('missing');
      expect(departmentVerdict(FALLBACK_TREE, url('/ '))).toBe('unconfirmed');
    });

    it('reads a slug the way the catalogue spells it, whatever case the address bar used', () => {
      expect(departmentVerdict(LIVE, url('/WOMEN'))).toBe('known');
      expect(categoryVerdict(LIVE, url('/Women/Coats'))).toBe('known');
    });
  });
});
