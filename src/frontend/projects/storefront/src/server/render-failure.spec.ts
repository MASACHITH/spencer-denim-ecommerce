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

    it('still records the whole error, including the stack, in the log', () => {
      const logged: [string, unknown][] = [];
      answerRenderFailure(failure, request('/women'), response(), (message, cause) =>
        logged.push([message, cause]),
      );

      expect(logged).toHaveLength(1);
      expect(logged[0][0]).toBe('GET /women failed');
      expect(logged[0][1]).toBe(failure);
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
