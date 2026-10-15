import {
  PAGE_VIEW_TIMEOUT_MS,
  VISIT_COOKIE,
  createTrafficContext,
  externalReferrer,
  isRecordableRequest,
  sendServerPageView,
  serverPageView,
  shouldRecordServerView,
  visitCookie,
  visitSession,
} from './page-view';

/**
 * The server half of "one visit, one row" (W4-03): which renders this process records, the visit cookie it
 * sets, and that the recording call can never hold up or fail a page.
 */
describe('server page-view recording (W4-03)', () => {
  const html = { status: 200, contentType: 'text/html; charset=utf-8' };

  describe('which requests are recorded', () => {
    it('records a rendered GET answered with a 200 HTML page', () => {
      const context = createTrafficContext('GET', {});
      context.rendered = true;

      expect(shouldRecordServerView(context, html)).toBe(true);
    });

    it('does not record when the render never ran — the engine served the client-side shell', () => {
      expect(shouldRecordServerView(createTrafficContext('GET', {}), html)).toBe(false);
    });

    it.each([404, 503, 301, 302, 500])('does not record a %i', (status) => {
      const context = createTrafficContext('GET', {});
      context.rendered = true;

      expect(shouldRecordServerView(context, { status, contentType: 'text/html' })).toBe(false);
    });

    it('does not record something that is not HTML, or no answer at all', () => {
      const context = createTrafficContext('GET', {});
      context.rendered = true;

      expect(shouldRecordServerView(context, { status: 200, contentType: 'application/json' })).toBe(false);
      expect(shouldRecordServerView(context, { status: 200, contentType: null })).toBe(false);
      expect(shouldRecordServerView(context, null)).toBe(false);
    });

    it('does not record a HEAD, a POST, or a speculative prefetch or prerender', () => {
      expect(isRecordableRequest('HEAD', {})).toBe(false);
      expect(isRecordableRequest('POST', {})).toBe(false);
      expect(isRecordableRequest('GET', { 'sec-purpose': 'prefetch' })).toBe(false);
      expect(isRecordableRequest('GET', { 'sec-purpose': 'prefetch;prerender' })).toBe(false);
      expect(isRecordableRequest('GET', { purpose: 'prefetch' })).toBe(false);
      expect(isRecordableRequest('GET', { 'x-moz': 'prefetch' })).toBe(false);
      expect(isRecordableRequest('GET', {})).toBe(true);
    });

    it('does not record a rendered prefetch even with a 200', () => {
      const context = createTrafficContext('GET', { 'sec-purpose': 'prefetch' });
      context.rendered = true;

      expect(shouldRecordServerView(context, html)).toBe(false);
    });
  });

  describe('the visit session', () => {
    const existing = '7d0c3f8e-2b1a-4c5d-9e8f-0a1b2c3d4e5f';

    it('keeps the session the browser sent', () => {
      expect(visitSession(`sd_cart=abc; ${VISIT_COOKIE}=${existing}; other=1`)).toBe(existing);
      expect(visitSession(`${VISIT_COOKIE}=${existing.toUpperCase()}`)).toBe(existing);
    });

    it.each([
      [undefined],
      [''],
      ['sd_cart=abc'],
      [`${VISIT_COOKIE}=not-a-guid`],
      [`${VISIT_COOKIE}=00000000-0000-0000-0000-000000000000`],
      [`x${VISIT_COOKIE}=${existing}`],
    ])('starts a new random session for %s', (header) => {
      const session = visitSession(header);

      expect(session).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/);
      expect(session).not.toBe(existing);
    });

    it('is a thirty-minute, HttpOnly, Secure, SameSite=Lax cookie on the whole site', () => {
      expect(visitCookie(existing)).toBe(
        `${VISIT_COOKIE}=${existing}; Max-Age=1800; Path=/; HttpOnly; Secure; SameSite=Lax`,
      );
    });
  });

  describe('what is sent', () => {
    it('sends another site as the referrer and drops this one', () => {
      const host = { host: 'shop.example' };

      expect(externalReferrer({ ...host, referer: 'https://www.google.com/' }, undefined)).toBe('https://www.google.com/');
      expect(externalReferrer({ ...host, referer: 'https://shop.example/sale' }, undefined)).toBeNull();
      expect(
        externalReferrer({ host: 'storefront:4000', 'x-forwarded-host': 'shop.example', referer: 'https://shop.example/' }, undefined),
      ).toBeNull();
      expect(
        externalReferrer({ host: 'storefront:4000', referer: 'https://www.shop.example/' }, 'https://www.shop.example'),
      ).toBeNull();
      expect(externalReferrer({ ...host, referer: 'not a url' }, undefined)).toBeNull();
      expect(externalReferrer(host, undefined)).toBeNull();
    });

    it('sends the visitor’s user agent, an empty one when they sent none, and never their address', () => {
      const view = serverPageView(
        '/women?sizes=M',
        {
          host: 'shop.example',
          'user-agent': 'Mozilla/5.0 (iPhone)',
          'x-forwarded-for': '203.0.113.77',
          'x-real-ip': '203.0.113.77',
          forwarded: 'for=203.0.113.77',
        },
        undefined,
      );

      expect(view).toEqual({ path: '/women?sizes=M', referrer: null, userAgent: 'Mozilla/5.0 (iPhone)' });
      expect(JSON.stringify(view)).not.toContain('203.0.113.77');
      expect(serverPageView('/', {}, undefined).userAgent).toBe('');
    });

    it('posts to the API with the session as the visit cookie and nothing else identifying', async () => {
      const send = vi.fn<typeof fetch>().mockResolvedValue(new Response(null, { status: 204 }));

      await sendServerPageView('http://api:8080', { path: '/', referrer: null, userAgent: 'UA' }, 'abc', send);

      expect(send).toHaveBeenCalledTimes(1);
      const [url, init] = send.mock.calls[0];
      expect(String(url)).toBe('http://api:8080/api/traffic/page-views');
      expect(init?.method).toBe('POST');
      expect(init?.headers).toEqual({ 'content-type': 'application/json', cookie: `${VISIT_COOKIE}=abc` });
      expect(JSON.parse(String(init?.body))).toEqual({ path: '/', referrer: null, userAgent: 'UA' });
      expect(init?.signal).toBeInstanceOf(AbortSignal);
      expect(PAGE_VIEW_TIMEOUT_MS).toBeLessThanOrEqual(2000);
    });

    it('never rejects: an API that is down, refuses or throws costs the view, not the page', async () => {
      const view = { path: '/', referrer: null, userAgent: 'UA' };

      await expect(
        sendServerPageView('http://api:8080', view, 'abc', vi.fn<typeof fetch>().mockRejectedValue(new TypeError('fetch failed'))),
      ).resolves.toBeUndefined();
      await expect(
        sendServerPageView('http://api:8080', view, 'abc', vi.fn<typeof fetch>().mockResolvedValue(new Response(null, { status: 503 }))),
      ).resolves.toBeUndefined();
      await expect(
        sendServerPageView('http://api:8080', view, 'abc', (() => {
          throw new Error('synchronous');
        }) as typeof fetch),
      ).resolves.toBeUndefined();
      await expect(sendServerPageView('not a url', view, 'abc', vi.fn<typeof fetch>())).resolves.toBeUndefined();
    });
  });
});
