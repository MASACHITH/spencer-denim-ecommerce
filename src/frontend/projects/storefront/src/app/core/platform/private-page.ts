import { DestroyRef, RESPONSE_INIT, inject } from '@angular/core';
import { Meta, Title } from '@angular/platform-browser';
import { brandPageTitle } from '@spencer-denim/shared';

import { RobotsMeta } from './robots-meta';

/**
 * The four things a page about one person has to do, in one place.
 *
 * `/bag`, `/wishlist` and `/checkout` each wrote this block out by hand, and the account pages are eight
 * more. Eight copies of a cache header is eight chances for one of them to be the copy that is wrong —
 * and the one that is wrong is the page that hands one shopper another shopper's order history out of a
 * shared cache. So it is written once:
 *
 * 1. **`Cache-Control: no-store, no-cache, private`.** These payloads hold a name, a telephone number, a
 *    street address and an order history.
 * 2. **`Vary: Cookie`.** `CheckoutEndpoints` makes the same note for the same reason: the two headers say
 *    different things, and a cache honouring only one of them has already handed one shopper another
 *    shopper's checkout on this project. It is set even on the pages whose server markup is impersonal,
 *    because which of them that is depends on a rendering decision made in a component — and if a later
 *    task renders a row on the server, the header has to already be right.
 * 3. **`<meta name="robots" content="noindex">`, cleared on the way out.** A client-side navigation would
 *    otherwise carry `noindex` onto the next page, which is the defect `RobotsMeta`'s own docblock
 *    records.
 * 4. **The title and description**, through `brandPageTitle`, so the shop is named from the one place it
 *    is written down (`BRAND`) and the separator matches the backend's seeded SEO titles.
 *
 * Called from a component's constructor, which is an injection context. `RESPONSE_INIT` is absent in the
 * browser, where a client-side navigation has no response to write a header onto — so the header half is
 * a server-only effect and the meta half runs in both.
 *
 * @param subject the page's own name, without the shop's: `'Sign in'`, not `'Sign in | …'`.
 * @param description one sentence for `<meta name="description">`. It is set even though the page is
 * `noindex`, because a shared link's preview card reads it and a crawler's refusal to index is not a
 * refusal to fetch.
 */
export function privatePage(subject: string, description: string): void {
  inject(Title).setTitle(brandPageTitle(subject));
  inject(Meta).updateTag({ name: 'description', content: description });

  const robots = inject(RobotsMeta);
  robots.noindex();
  inject(DestroyRef).onDestroy(() => robots.clear());

  const response = inject(RESPONSE_INIT, { optional: true });
  if (response) {
    const headers = new Headers(response.headers ?? {});
    headers.set('Cache-Control', 'no-store, no-cache, private');
    headers.set('Vary', 'Cookie');
    response.headers = headers;
  }
}
