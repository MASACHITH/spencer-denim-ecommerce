import { robotsTxt } from './robots-txt';

/**
 * What `robots.txt` says. The fact that it is answered **at all**, ahead of the Angular router, is
 * provable only on the wire and is `e2e/seo.e2e.ts`'s job.
 */
describe('robots.txt', () => {
  const saved = process.env['PUBLIC_SITE_ORIGIN'];

  afterEach(() => {
    process.env['PUBLIC_SITE_ORIGIN'] = saved;
  });

  function file(): string {
    return robotsTxt({ host: 'localhost:4420' });
  }

  it('lets a crawler into the shop', () => {
    expect(file()).toContain('User-agent: *');
    expect(file()).toContain('Allow: /');
  });

  it('points at the sitemap with an absolute URL, which is the only form the line accepts', () => {
    process.env['PUBLIC_SITE_ORIGIN'] = 'https://colombowardrobecompany.com';

    expect(robotsTxt({})).toContain('Sitemap: https://colombowardrobecompany.com/sitemap.xml');
  });

  it('keeps a shopper’s own session out', () => {
    for (const path of ['/account', '/checkout']) {
      expect(file()).toContain(`Disallow: ${path}`);
    }
  });

  it('keeps the API out: it answers on this origin behind the proxy and is not the shop', () => {
    expect(file()).toContain('Disallow: /api/');
  });

  /**
   * **The one rule here that looks like a mistake and is not.**
   *
   * `/search` carries `<meta name="robots" content="noindex">` (SRCH-02), and `/bag` carries one too
   * since W2-09 built it (CART-01). `Disallow` and `noindex` read as the same wish and are opposites in
   * effect: a crawler that is disallowed never fetches the page, so it never reads the `noindex`, and a
   * link to the URL from anywhere else is enough for it to be listed. Allowing the fetch is what makes
   * the `noindex` work. The assertion exists so that whoever "tidies up" this file later has to read
   * the reason first.
   *
   * `Disallow: /bag` really was in this file, added while `/bag` was still a redirect to the home page
   * with no `noindex` of its own to protect. W2-09 removed it in the same change that gave the page the
   * tag, because the header links to `/bag` from every page of the site and a disallowed-but-linked URL
   * is the one that gets listed bare.
   */
  it.each([['/search'], ['/bag']])(
    'does not disallow %s, because that would undo its noindex rather than reinforce it',
    (path) => {
      expect(file()).not.toContain(`Disallow: ${path}`);
    },
  );

  it('ends with a newline, as a line-oriented format should', () => {
    expect(file().endsWith('\n')).toBe(true);
  });
});
