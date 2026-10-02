import { canonicalTarget, effectivePath, redirectStatusFor } from './canonical-path';

/**
 * The leading-slash run, which was a **500 on `robots.txt`** (F1).
 *
 * `//robots.txt` answered HTTP 500 — and a 5xx there tells a crawler the whole site is disallowed,
 * which is the single outcome the storefront's SEO design exists to prevent. The cause is in
 * `canonical-path.ts`; what matters here is the two properties the fix has to have and the one it must
 * not accidentally acquire.
 */
describe('canonicalTarget', () => {
  it('leaves an already-canonical target alone, so nothing is redirected for no reason', () => {
    for (const url of ['/', '/robots.txt', '/women', '/women/jeans?colour=navy', '/product/x', '/a//b']) {
      expect(canonicalTarget(url), url).toBeNull();
    }
  });

  it('collapses any leading run to one slash', () => {
    expect(canonicalTarget('//robots.txt')).toBe('/robots.txt');
    expect(canonicalTarget('///robots.txt')).toBe('/robots.txt');
    expect(canonicalTarget('////////robots.txt')).toBe('/robots.txt');
    expect(canonicalTarget('//')).toBe('/');
    expect(canonicalTarget('////')).toBe('/');
  });

  /**
   * Backslashes are in scope, and not out of pedantry: `/\robots.txt` threw the same NG05702, and a
   * fix that stripped only forward slashes would have left it answering 500.
   */
  it('treats a backslash as a slash, in any mixture', () => {
    for (const url of ['/\\robots.txt', '\\/robots.txt', '/\\\\robots.txt', '//\\/robots.txt']) {
      expect(canonicalTarget(url), url).toBe('/robots.txt');
    }
  });

  /**
   * **F9: the raw target was the wrong thing to test.** A URL parser removes dot segments first, so a
   * path can arrive with one leading slash and end up with two — and nine shapes went on answering 500
   * on a build whose whole point was that they could not.
   */
  describe('a run that only appears after the dot segments are resolved', () => {
    for (const [url, expected] of [
      ['/.//robots.txt', '/robots.txt'],
      ['/..//robots.txt', '/robots.txt'],
      ['/a/..//robots.txt', '/robots.txt'],
      ['/a/../..//women', '/women'],
      ['/%2e//robots.txt', '/robots.txt'],
      ['/%2E//robots.txt', '/robots.txt'],
      ['/.%2e//robots.txt', '/robots.txt'],
      ['/%2e%2e//robots.txt', '/robots.txt'],
      ['/.//', '/'],
      ['/.//women?colour=navy', '/women?colour=navy'],
      ['/.//search?q=a%20b', '/search?q=a%20b'],
    ] as const) {
      it(`${url} canonicalises to ${expected}`, () => {
        expect(canonicalTarget(url)).toBe(expected);
      });
    }

    /**
     * **Deliberately left alone**, because a dot segment that does not reach the front is safe and
     * redirecting it would be a crawl-canonicalisation change rather than a fix.
     */
    for (const url of ['/./robots.txt', '/a/../robots.txt', '/women/./jeans', '/women//jeans', '/..']) {
      it(`${url} is not redirected`, () => {
        expect(canonicalTarget(url)).toBeNull();
      });
    }
  });

  /**
   * The path a URL parser produces, which is what everything downstream sees. Checked directly as well
   * as through `canonicalTarget`, because the two empty-segment rules are the easy thing to get wrong:
   * a single-dot segment is dropped **except** as the last one, where it leaves a trailing empty
   * segment — which is why `/./` resolves to `/` while `/.//x` resolves to `//x`.
   */
  describe('effectivePath', () => {
    for (const [raw, expected] of [
      ['/robots.txt', '/robots.txt'],
      ['/./robots.txt', '/robots.txt'],
      ['/./', '/'],
      ['/.//x', '//x'],
      ['/..//x', '//x'],
      ['/a/b/../c', '/a/c'],
      ['/a/../..//x', '//x'],
      ['/..', '/'],
      ['/a/..', '/a/..'.replace('/a/..', '/')],
      ['/women//jeans', '/women//jeans'],
      ['/\\robots.txt', '//robots.txt'],
      ['/.well-known/x', '/.well-known/x'],
      ['/..foo/x', '/..foo/x'],
    ] as const) {
      it(`${raw} resolves to ${expected}`, () => {
        expect(effectivePath(raw)).toBe(expected);
      });
    }

    /** It cannot climb above the root, whatever is asked of it. */
    it('never escapes the root', () => {
      for (const raw of ['/..', '/../..', '/../../etc/passwd', '/a/../../..', '/%2e%2e/%2e%2e']) {
        expect(effectivePath(raw).startsWith('/'), raw).toBe(true);
        expect(effectivePath(raw), raw).not.toContain('..');
      }
    });
  });

  /**
   * **The property that matters most, because getting it wrong would be worse than the bug.**
   *
   * A redirect built from a path is an open redirect the moment the result stops being same-origin, and
   * `//evil.example.com/x` and `/\evil.example.com/x` are both read as protocol-relative by some user
   * agents. Every result must begin with exactly one slash followed by something that is neither a
   * slash nor a backslash, which is what makes it a path on this origin and not an address somewhere
   * else.
   *
   * The list is the one the round-2 tester proved airtight — 21 shapes including backslashes,
   * credentials, `@`, punycode, explicit ports, `////` and traversal — plus the dot-segment spellings
   * that F9 opened, because changing the matcher is exactly when this property is at risk.
   */
  it('never produces a target that could leave this origin', () => {
    const hostile = [
      '//evil.example.com/x',
      '///evil.example.com/x',
      '////evil.example.com/x',
      '/\\evil.example.com/x',
      '\\\\evil.example.com/x',
      '//\\evil.example.com',
      '/\\/evil.example.com',
      '//evil.example.com',
      '////evil.example.com/%2e%2e',
      '//user:pw@evil.example.com/x',
      '//evil.example.com%2f..',
      '//evil.example.com:8443/x',
      '//evil.example.com:80',
      '//xn--80ak6aa92e.com/x',
      '//ev\u0131l.example.com/x',
      '//evil.example.com/../../x',
      '//evil.example.com?x=//y',
      '//evil.example.com#//y',
      '//:@evil.example.com',
      '//@evil.example.com',
      '//.evil.example.com',
      // F9's family, where the authority only appears after normalisation.
      '/.//evil.example.com/x',
      '/..//evil.example.com/x',
      '/a/..//evil.example.com',
      '/%2e//evil.example.com',
      '/.%2e//evil.example.com:8443/x',
    ];

    for (const url of hostile) {
      const target = canonicalTarget(url);

      // **The invariant is about the path the application ends up serving, not about whether a redirect
      // happened**, and the first version of this test got that wrong: it demanded a redirect for every
      // hostile-looking input, and `//evil.example.com/../../x` resolves to plain `/x`, so there is
      // nothing to redirect and nothing unsafe. Asserting the redirect would have been asserting the
      // mechanism rather than the property.
      const served = target ?? effectivePath(url.split(/[?#]/)[0]);

      expect(served, url).toMatch(/^\/$|^\/(?![\\/])/);
      // Not merely "starts with a slash": a host must never survive as an authority.
      expect(served, url).not.toMatch(/^\/\//);
      // And resolved against any base, it stays on that base.
      expect(new URL(served, 'https://shop.example.com').origin, url).toBe('https://shop.example.com');
    }
  });

  it('keeps the query string, because //women?colour=navy is a filtered listing', () => {
    expect(canonicalTarget('//women?colour=navy')).toBe('/women?colour=navy');
    expect(canonicalTarget('//search?q=a%20b')).toBe('/search?q=a%20b');
  });

  /** The canonical form must itself be canonical, or the redirect loops. */
  it('produces a target it would not redirect again', () => {
    for (const url of ['//robots.txt', '////', '/\\x', '//evil.example.com/x']) {
      const once = canonicalTarget(url);
      expect(once).not.toBeNull();
      expect(canonicalTarget(once as string), url).toBeNull();
    }
  });
});

describe('redirectStatusFor', () => {
  it('is a cacheable 301 for the two methods a crawler uses', () => {
    expect(redirectStatusFor('GET')).toBe(301);
    expect(redirectStatusFor('HEAD')).toBe(301);
  });

  /**
   * 308 for everything else: a 301 permits a client to re-issue the request as a GET, and silently
   * dropping a request body would be a worse bug than the one being fixed. Nothing posts to this
   * server today; this is so that staying true is not something anyone has to remember.
   */
  it('is a 308 for anything that may carry a body', () => {
    for (const method of ['POST', 'PUT', 'PATCH', 'DELETE']) {
      expect(redirectStatusFor(method), method).toBe(308);
    }
  });
});
