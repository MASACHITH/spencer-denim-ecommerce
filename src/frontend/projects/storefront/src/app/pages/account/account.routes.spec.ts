import { Route } from '@angular/router';

import { accountRoutes } from './account.routes';
import { customerSignedIn } from '../../core/account/signed-in.guard';

/**
 * The shape of the account route table.
 *
 * Two invariants are worth holding here rather than in a browser, because both are the kind of thing a
 * later edit breaks silently:
 *
 * - **which routes are guarded.** Guarding `reset-password` or `confirm-email` would make a link from an
 *   email unusable in exactly the case it is needed — the browser that opened the message may have no
 *   session, and `confirm-email` is the route that *creates* one, so requiring one would be circular.
 * - **`pathMatch: 'full'` on the redirect.** Without it, `path: ''` prefix-matches every child address and
 *   sends `/account/orders` to itself. `UNBUILT_PATHS` in `app.routes.ts` records measuring the same
 *   lesson from the other direction.
 */
describe('the account route table (W3-01)', () => {
  function routeFor(path: string): Route | undefined {
    return accountRoutes.find((route) => route.path === path);
  }

  const SIGNED_IN_ONLY = ['orders', 'addresses', 'profile'] as const;
  const PUBLIC = ['sign-in', 'register', 'forgot-password', 'reset-password', 'confirm-email'] as const;

  it('declares the eight pages W3-01 builds, and nothing else', () => {
    expect(accountRoutes.map((route) => route.path)).toEqual([
      '',
      ...PUBLIC,
      ...SIGNED_IN_ONLY,
    ]);
  });

  describe('/account itself', () => {
    /**
     * A hub whose entire content is three links is a page a shopper has to get past in order to reach
     * anything, and doc 11 P11 step 12's order list is what they signed in to look at. The three links
     * live on each signed-in page as `AccountNav`, so nothing is lost but a click.
     */
    it('redirects to the order history rather than rendering a hub', () => {
      const redirect = routeFor('');

      expect(redirect?.redirectTo).toBe('orders');
      expect(redirect?.loadComponent).toBeUndefined();
    });

    /** Load-bearing: without it, `path: ''` prefix-matches `/account/orders` and redirects it to itself. */
    it('claims only its own address', () => {
      expect(routeFor('')?.pathMatch).toBe('full');
    });

    it('is declared first, so nothing deeper is matched by it', () => {
      expect(accountRoutes[0].path).toBe('');
    });
  });

  describe('which routes need a session', () => {
    it.each(SIGNED_IN_ONLY)('guards /%s', (path) => {
      expect(routeFor(path)?.canActivate).toEqual([customerSignedIn]);
    });

    /**
     * **Each of these is deliberately open**, and the reason differs: the first two are for people who
     * are not signed in by definition, and the last three are opened from a link in an email by whatever
     * browser read it. `confirm-email` is also the one route that creates a session.
     */
    it.each(PUBLIC)('leaves /%s open', (path) => {
      expect(routeFor(path)?.canActivate).toBeUndefined();
      expect(routeFor(path)?.canMatch).toBeUndefined();
    });

    /**
     * **`canActivate` and never `canMatch`**, and the difference is where a signed-out visitor lands. A
     * `canMatch` that answered no would let the router carry on down the storefront's table to
     * `:department/:category`, ask the catalogue about a department called "account", and land them on
     * the 404 page instead of the sign-in form.
     */
    it('uses canActivate so a refusal can redirect rather than fall through to the 404', () => {
      for (const route of accountRoutes) {
        expect(route.canMatch, route.path).toBeUndefined();
      }
    });
  });

  it('loads a component for every page and redirects from nowhere else', () => {
    for (const route of accountRoutes) {
      if (route.path === '') {
        continue;
      }

      expect(route.loadComponent, route.path).toBeDefined();
      expect(route.redirectTo, route.path).toBeUndefined();
    }
  });

  /**
   * GLB-11's friendly URLs, inside this table as well as the parent's: slug-shaped paths, no ids and no
   * query-string routing. The token on `reset-password` and `confirm-email` is a query parameter the page
   * strips, not a segment — which is also why neither path has one.
   */
  it('addresses every page by a slug, with no ids', () => {
    for (const route of accountRoutes) {
      if (route.path === '') {
        continue;
      }

      expect(route.path, `${route.path} is not slug-shaped`).toMatch(/^[a-z0-9-]+$/);
    }
  });

  /**
   * **No catch-all here, deliberately.** An address deeper under `/account` that no child matches makes
   * the parent route fail to match, and the router backtracks to the rest of the storefront's table and
   * ends at `**` — a real 404 with `noindex`. A catch-all here would be a second implementation of that
   * page, and a redirect to `orders` would be the soft 404 #28 exists to remove: the wrong page under the
   * wrong URL, answered 200.
   */
  it('declares no catch-all of its own', () => {
    expect(accountRoutes.map((route) => route.path)).not.toContain('**');
  });
});
