import { Route } from '@angular/router';

import { UNBUILT_PATHS, routes } from './app.routes';

/**
 * The shape of the route table, where #28 lived.
 *
 * `/not-a-dept` answered **200 with the home page** — a soft 404: the wrong page under the wrong URL,
 * and nothing anywhere reporting it. The status code itself can only be proved on the wire, which
 * `e2e/seo.e2e.ts` does. What belongs here is the structure that produces it, and in particular the
 * invariant that made #28 hard to fix: the catch-all may only answer 404 while every address the shop
 * links to and has not built is listed ahead of it.
 */
describe('the storefront route table (#28)', () => {
  /** Every path in the table, flattened, in declaration order. */
  const declared = routes.map((route) => route.path);

  function routeFor(path: string): Route | undefined {
    return routes.find((route) => route.path === path);
  }

  it('answers the catch-all with the not-found page rather than a redirect home', () => {
    const catchAll = routeFor('**');

    expect(catchAll).toBeDefined();
    // The defect, stated as the assertion: a redirect is what made it a soft 404.
    expect(catchAll?.redirectTo).toBeUndefined();
    expect(catchAll?.loadComponent).toBeDefined();
  });

  it('puts the catch-all last, so nothing it would swallow is matched by it first', () => {
    expect(declared.indexOf('**')).toBe(declared.length - 1);
  });

  it('keeps every unbuilt page the shell links to on a redirect home, ahead of the catch-all', () => {
    for (const path of UNBUILT_PATHS) {
      const route = routeFor(path);

      expect(route, `${path} has no route, so the catch-all would 404 it`).toBeDefined();
      expect(route?.redirectTo, `${path} should still redirect home until its page exists`).toBe('');
      expect(declared.indexOf(path)).toBeLessThan(declared.indexOf('**'));
      // Measured, not assumed: without this, `path: 'account'` matched `/account/orders` as a
      // prefix and the redirect answered **404 with an empty body** — a blank page for a shopper
      // and nothing at all for a crawler. `/account/orders`, `/bag/checkout` and `/about/x` all did
      // it. `seo.e2e.ts` holds the same property on the wire.
      expect(route?.pathMatch, `${path} must claim only its own address`).toBe('full');
    }
  });

  /**
   * The half of the invariant a list in a file cannot keep on its own: an entry that stays behind
   * after its page is built is dead, and an entry that is missing is a 404 on a link in the footer.
   * This is the "missing" half; the "dead" half is caught by the route for the real page being
   * declared above it, which `declared.indexOf` proves below.
   */
  it('lists every single-segment address the unbuilt pages are linked at, and nothing already built', () => {
    // Read off the shell: site-header.ts, site-footer.ts and mobile-nav.ts. `/search`, `/bag` and
    // `/wishlist` are not here any more — W2-07 built the search page (SRCH-02), W2-09 the bag page
    // (CART-01) and W2-08 the wishlist page (WISH-01), so each has a route of its own. All three are
    // still linked from the shell; what changed is that the link now goes to a page, which is why the
    // test below checks the list against the route table rather than against this list.
    //
    // **The six content pages are not here any more either (W2-11, GLB-10)**, and they left in a
    // different way from the rest: they did not gain six routes, they gained one `:slug` route behind
    // a guard, because a page's slug is editable (doc 11 P13 step 12). They are also no longer
    // unconditionally linked from the shell — the footer draws a policy link only once the page is
    // published, which on a fresh shop is none of them. So there is nothing for this list to hold:
    // an address the shell does not link cannot be a broken link in the footer.
    //
    // **`account` is no longer here either, and it was the last one (W3-01).** `/account` has a real
    // route with eight pages under it, so the entry claiming the site has no such address is gone and
    // this list is empty. The list is kept rather than deleted because it is the half of the invariant
    // the route table cannot state — "every address the shell links and has not built is listed" — and
    // the next task to link a page before building it needs it. `linkedFromTheShell.length === 0` is
    // therefore the current state and not the permanent one.
    const linkedFromTheShell: readonly string[] = [];

    for (const path of linkedFromTheShell) {
      expect(UNBUILT_PATHS).toContain(path);
    }

    for (const path of UNBUILT_PATHS) {
      expect(declared.indexOf(path)).toBeLessThan(declared.indexOf(':department'));
    }
  });

  /**
   * **The other half of the invariant, which a list in a file cannot keep on its own.**
   *
   * An entry whose page has since been built is dead — the real route is declared above it, so it
   * cannot take effect — and it is a lie about what the site has. It is also exactly what nearly
   * shipped: `/search` was in this list, W2-07 landed the page while this branch was in flight, and
   * **git merged both sides without a conflict**, leaving a redirect for an address that now has a
   * page. Nothing broke, because the real route won; nothing would have told anyone either.
   *
   * So the list is checked against the table rather than against a second list: a path with a route
   * of its own does not belong in it, whichever task built that route.
   */
  it('holds no path that already has a route of its own', () => {
    const realRoutes = routes
      .filter((route) => route.redirectTo === undefined && route.path !== undefined)
      .map((route) => route.path);

    for (const path of UNBUILT_PATHS) {
      expect(realRoutes, `${path} has a real route now, so its redirect is dead`).not.toContain(path);
    }
  });

  /**
   * #30, which the #28 fix must not undo. The three-answer routing it introduced — a slug the live
   * catalogue says is unknown is a 404, one it could not confirm is a 503 — depends on those routes
   * being matched before the catch-all, which now 404s everything that reaches it.
   */
  it('still matches the 503 routes before the catch-all', () => {
    const catchAll = declared.indexOf('**');
    const guarded = routes.filter(
      (route) =>
        (route.path === ':department/:category' || route.path === ':department/:category/:subcategory') &&
        route.canMatch !== undefined,
    );

    // Three guards on each of the two depths: known, missing, unconfirmed.
    expect(guarded).toHaveLength(6);
    for (const route of guarded) {
      expect(routes.indexOf(route)).toBeLessThan(catchAll);
    }
  });

  /**
   * GLB-10's content pages, and the three properties the ordering has to have.
   *
   * The route itself is `:slug`, which matches any single segment, so **where** it sits is the whole
   * of its correctness: behind `:department` so a department listing is never looked up as a page,
   * ahead of `**` so a published page is not swept into the 404, and paired with a 503 sibling so an
   * address the content feed could not be asked about is not called missing (#30).
   */
  describe('the content pages (GLB-10)', () => {
    const contentRoutes = routes.filter(
      (route) => route.path === ':slug' && route.canMatch !== undefined,
    );

    it('declares exactly two guarded :slug routes: the page and the 503', () => {
      // Two rather than three, unlike the listing: the "missing" verdict needs no route of its own,
      // because a single segment that is not a page falls through to the catch-all, which is already
      // a real 404 with `noindex`. A third route would be a second implementation of that page.
      expect(contentRoutes).toHaveLength(2);
    });

    it('matches them after the department routes and before the catch-all', () => {
      const lastDepartmentRoute = Math.max(
        ...routes
          .filter((route) => route.path?.startsWith(':department') === true)
          .map((route) => routes.indexOf(route)),
      );

      for (const route of contentRoutes) {
        expect(routes.indexOf(route)).toBeGreaterThan(lastDepartmentRoute);
        expect(routes.indexOf(route)).toBeLessThan(declared.indexOf('**'));
      }
    });

    it('loads a real component rather than redirecting, so a page can answer 404 itself', () => {
      for (const route of contentRoutes) {
        expect(route.redirectTo).toBeUndefined();
        expect(route.loadComponent).toBeDefined();
      }
    });
  });

  /**
   * "Friendly URLs" in GLB-11, confirmed rather than rebuilt.
   *
   * Every page a shopper can reach is addressed by the catalogue's own slugs — `/women`,
   * `/women/jeans`, `/kids/girls/girls-dresses`, `/product/<slug>` — with no ids, no query-string
   * routing and no file extensions anywhere in the table. There was nothing to build here; this is the
   * check that it stays true.
   */
  it('addresses every page by slug, with no ids or extensions in the table', () => {
    for (const path of declared) {
      if (path === '**' || path === '') {
        continue;
      }

      expect(path, `${path} is not a slug-shaped route`).toMatch(
        /^:?[a-z0-9-]+(\/:?[a-z0-9-]+)*$/,
      );
      expect(path).not.toMatch(/:id\b|\.html?$|\.aspx?$/);
    }
  });
});
