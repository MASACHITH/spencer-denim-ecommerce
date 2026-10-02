import { BRAND } from '@spencer-denim/shared';

import { RequestHeaders, publicOrigin } from './origins';

/**
 * How long a crawler may keep `robots.txt`. An hour: the file barely changes, and a crawler that has
 * one in hand is a crawler that is not blocked.
 */
export const ROBOTS_MAX_AGE_SECONDS = 3600;

/**
 * `robots.txt` for the storefront (GLB-11).
 *
 * **Why it is generated and not a file in `public/`.** The `Sitemap:` line has to be an absolute URL,
 * and only the running process knows what the public origin is — doc 03 §5 says nothing may depend on
 * the host. A checked-in file would have to hard-code the domain and would be wrong on staging, wrong
 * under `ng serve` and wrong the day the domain changes.
 *
 * **What is deliberately *not* disallowed.**
 *
 * - **`/search`.** It carries `<meta name="robots" content="noindex">` (SRCH-02). `Disallow` and
 *   `noindex` look like the same wish and are not: a crawler that is disallowed never fetches the page,
 *   so it never reads the `noindex` — and it may still list the bare URL, because a link to it from
 *   elsewhere is enough. Allowing the fetch is what makes the `noindex` effective, so disallowing it
 *   here would quietly undo the thing it looks like it reinforces.
 * - **everything else in the shop.** A storefront exists to be indexed. Only addresses that are
 *   somebody's private session, or not the shop at all, are listed below.
 *
 * It is served by Express ahead of the Angular router, and that is a requirement rather than a
 * preference: with no such file the request reaches the router, and **a crawler that is answered 5xx
 * for `robots.txt` treats the whole site as disallowed** until it can read one. A route table is the
 * last place that answer should be able to come from.
 */
export function robotsTxt(headers: RequestHeaders): string {
  const origin = publicOrigin(headers);

  return [
    `# ${BRAND.name}`,
    '# Generated per request: the Sitemap line below carries this deployment’s own origin.',
    '',
    'User-agent: *',
    'Allow: /',
    '',
    '# A shopper’s own session. Nothing here should ever be a search result.',
    'Disallow: /account',
    'Disallow: /bag',
    'Disallow: /checkout',
    '',
    '# The API answers on this origin behind the reverse proxy. It is not the shop.',
    'Disallow: /api/',
    '',
    '# /search is deliberately crawlable: it answers noindex, and a crawler has to be allowed to',
    '# fetch a page in order to read that. Disallowing it would leave the URL indexable instead.',
    '',
    `Sitemap: ${origin}/sitemap.xml`,
    '',
  ].join('\n');
}
