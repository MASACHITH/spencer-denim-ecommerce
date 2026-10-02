import { canonicalTarget, redirectStatusFor } from './canonical-path';

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
   * **The property that matters most, because getting it wrong would be worse than the bug.**
   *
   * A redirect built from a path is an open redirect the moment the result stops being same-origin, and
   * `//evil.example.com/x` and `/\evil.example.com/x` are both read as protocol-relative by some user
   * agents. Every result must begin with exactly one slash followed by something that is neither a
   * slash nor a backslash, which is what makes it a path on this origin and not an address somewhere
   * else.
   */
  it('never produces a target that could leave this origin', () => {
    const hostile = [
      '//evil.example.com/x',
      '///evil.example.com/x',
      '/\\evil.example.com/x',
      '\\\\evil.example.com/x',
      '//\\evil.example.com',
      '//evil.example.com',
      '////evil.example.com/%2e%2e',
      '//user:pw@evil.example.com/x',
    ];

    for (const url of hostile) {
      const target = canonicalTarget(url);

      expect(target, url).not.toBeNull();
      expect(target, url).toMatch(/^\/(?![\\/])/);
      // And it is not merely "starts with a slash": the host must have become a path segment.
      expect(target, url).not.toMatch(/^\/\//);
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
