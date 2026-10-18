import { FailedRequest, FailingResponse, answerRenderFailure } from './render-failure';

/**
 * The second line of defence behind the path canonicaliser (F1).
 *
 * It exists because the first should not be load-bearing alone, and it is tested because a second line
 * of defence that has never been exercised is a comment rather than a defence.
 */
describe('answerRenderFailure', () => {
  interface Recorded extends FailingResponse {
    readonly statuses: number[];
    readonly types: string[];
    readonly headers: Record<string, string>;
    readonly bodies: string[];
  }

  function response(headersSent = false): Recorded {
    const statuses: number[] = [];
    const types: string[] = [];
    const headers: Record<string, string> = {};
    const bodies: string[] = [];

    return {
      headersSent,
      statuses,
      types,
      headers,
      bodies,
      status: (code) => statuses.push(code),
      type: (value) => types.push(value),
      setHeader: (name, value) => (headers[name] = value),
      send: (body) => bodies.push(body),
    };
  }

  function request(path: string, overrides: Partial<FailedRequest> = {}): FailedRequest {
    return {
      method: 'GET',
      originalUrl: path,
      path,
      headers: { host: 'localhost:4420' },
      ...overrides,
    };
  }

  /** The real one, with its own stack and the absolute build path Express would have published. */
  const failure = new Error(
    'NG05702: Protocol relative URLs are not allowed in this context\n' +
      '    at Xa (D:\\Projects\\e com\\worktrees\\sd-rename\\src\\frontend\\dist\\storefront\\server\\main.server.mjs:1:2)',
  );

  describe('an ordinary page', () => {
    it('answers one plain sentence at 500', () => {
      const res = response();

      expect(answerRenderFailure(failure, request('/women'), res, () => undefined)).toBe('error');

      expect(res.statuses).toEqual([500]);
      expect(res.types).toEqual(['text/plain; charset=utf-8']);
      expect(res.headers['Cache-Control']).toBe('no-store');
      expect(res.bodies).toEqual(['Something went wrong on our side. Please try again.\n']);
    });

    /**
     * **The measured leak.** Express's default handler answers `text/html` carrying the stack and the
     * absolute directory the server was built in, whenever `NODE_ENV` is not `production` — which is
     * this project's own development default. That is reconnaissance handed to anyone who can provoke
     * an error, so the body is asserted not to contain any of it rather than merely to be short.
     */
    it('leaks no stack, no path and nothing about the error', () => {
      const res = response();
      answerRenderFailure(failure, request('/women'), res, () => undefined);

      const body = res.bodies.join('');
      for (const secret of ['NG05702', 'Protocol relative', 'sd-rename', 'D:\\', 'main.server', '    at ']) {
        expect(body, secret).not.toContain(secret);
      }
    });

    it('still records the error, including its message and its stack, in the log', () => {
      const logged: [string, unknown][] = [];
      answerRenderFailure(failure, request('/women'), response(), (message, cause) =>
        logged.push([message, cause]),
      );

      expect(logged).toHaveLength(1);
      expect(logged[0][0]).toBe('GET /women failed');
      // Described, not the object itself (#201): the text of the error, so nothing Node attaches to it prints.
      expect(typeof logged[0][1]).toBe('string');
      expect(logged[0][1]).toContain('NG05702: Protocol relative URLs are not allowed in this context');
      expect(logged[0][1]).toContain('main.server.mjs:1:2');
    });
  });

  /**
   * **Issue #201: the line that says a render failed names the path and never the query string.**
   *
   * A failed server render of `/account/reset-password?token=…` or `/account/confirm-email?token=…` logged
   * `originalUrl` as it came, so a live credential went to the container's log, which Docker keeps. Everything
   * the request could put in front of the log is searched, not only the message: the error that goes with it
   * is a Node `TypeError [ERR_INVALID_URL]` whose `input` property is the whole URL.
   */
  describe('what it writes to the log about the request (#201)', () => {
    const orderToken = '7d1e5c0a-4b3f-4a8e-9c2d-1f6b8a3e5d90';
    const resetToken = 'CfDJ8Abc-reset-Zk93TmXp41';

    /** Everything the handler logged for one failed request, as one string. */
    function logFor(url: string, error: unknown = failure): string {
      const lines: string[] = [];
      answerRenderFailure(
        error,
        request(url.split('?')[0], { originalUrl: url }),
        response(),
        (message, cause) => lines.push(message, String(cause)),
      );

      return lines.join('\n');
    }

    it.each([
      [`/account/reset-password?token=${resetToken}&email=jane%40example.com`, 'GET /account/reset-password failed'],
      [`/account/confirm-email?token=${resetToken}&userId=42`, 'GET /account/confirm-email failed'],
      [`/search?q=0771234567`, 'GET /search failed'],
      [`/order/${orderToken}?paypal=return&token=EC-8AB12345CD678901E&PayerID=ABC123`, 'GET /order/* failed'],
      [`/women?sort=price-asc#top`, 'GET /women failed'],
    ])('logs %s as the path only', (url, expected) => {
      const lines: string[] = [];
      answerRenderFailure(failure, request(url.split('?')[0], { originalUrl: url }), response(), (message) =>
        lines.push(message),
      );

      expect(lines).toEqual([expected]);
    });

    it('prints no credential from the address, in the message or in the error', () => {
      const urls = [
        `/account/reset-password?token=${resetToken}&email=jane%40example.com`,
        `/order/${orderToken}?token=${resetToken}`,
      ];
      const invalidUrl = (url: string) =>
        Object.assign(new TypeError('Invalid URL'), { code: 'ERR_INVALID_URL', input: `http://localhost:4000${url}` });

      for (const url of urls) {
        const text = logFor(url, invalidUrl(url));

        for (const secret of [resetToken, orderToken, 'jane%40example.com', 'jane@example.com', 'token=', 'input']) {
          expect(text, `${secret} in the log of ${url}`).not.toContain(secret);
        }

        expect(text).toContain('ERR_INVALID_URL');
      }
    });

    it('prints no credential an error message carries from the address either', () => {
      const url = `/account/reset-password?token=${resetToken}`;
      const text = logFor(url, new Error(`Render of ${url} failed: cannot read 'x' (http://localhost:4000${url})`));

      expect(text).not.toContain(resetToken);
      expect(text).toContain('Render of /account/reset-password');
    });
  });

  /**
   * **`robots.txt` is answered rather than failed.** Its content depends on nothing that can break — a
   * constant plus this request's own origin — so there is no reading of a failure here in which 5xx is
   * the honest answer, and a 5xx on that file tells a crawler the **entire site** is disallowed. It
   * should be unreachable now that the path is canonicalised first; this is what makes "should be" not
   * load-bearing.
   */
  describe('robots.txt', () => {
    it('is served at 200 even from the failure path', () => {
      const res = response();

      expect(answerRenderFailure(failure, request('/robots.txt'), res, () => undefined)).toBe('robots');

      expect(res.statuses).toEqual([200]);
      expect(res.types).toEqual(['text/plain; charset=utf-8']);
      expect(res.bodies.join('')).toContain('User-agent: *');
      expect(res.bodies.join('')).toContain('Sitemap: ');
      // Not cached: this copy came from a failure, so the next request should ask again.
      expect(res.headers['Cache-Control']).toBe('no-store');
    });

    it('is never a 5xx, which is the whole reason for the special case', () => {
      const res = response();
      answerRenderFailure(failure, request('/robots.txt'), res, () => undefined);

      expect(res.statuses.some((status) => status >= 500)).toBe(false);
    });

    /**
     * The half of F9's fix that nothing else can reach, and the shapes are not the obvious ones.
     *
     * A path like `/.//robots.txt` resolves to `//robots.txt` — a leading run — so the canonicaliser
     * in front redirects it and it never reaches a render at all. The shapes that matter here are the
     * ones whose effective path **is** `/robots.txt` while the raw path is not: a dot segment that
     * collapses without leaving a run. Those are left alone by the canonicaliser, by design, so they
     * are the ones that can arrive at this branch — and `request.path` compared raw missed every one
     * of them, on the one file where a 5xx tells a crawler the whole site is disallowed.
     *
     * No e2e test can gate this: such a path 404s cleanly without a render failure, so the branch is
     * unreachable from the wire by construction. A unit test is the only possible gate, and this
     * file's own opening says a second line of defence that has never been exercised is a comment
     * rather than a defence.
     */
    it.each(['/./robots.txt', '/a/../robots.txt', '/%2e/robots.txt', '/a/b/../../robots.txt'])(
      'recognises %s, whose effective path is /robots.txt though its raw path is not',
      (path) => {
        const res = response();

        expect(answerRenderFailure(failure, request(path), res, () => undefined)).toBe('robots');

        expect(res.statuses).toEqual([200]);
        expect(res.bodies.join('')).toContain('User-agent: *');
      },
    );
  });

  /** Half a response is already on the wire; adding to it would corrupt the body. */
  it('writes nothing when the response has already started', () => {
    const res = response(true);

    expect(answerRenderFailure(failure, request('/women'), res, () => undefined)).toBe(
      'already-answered',
    );

    expect(res.statuses).toEqual([]);
    expect(res.bodies).toEqual([]);
  });
});
