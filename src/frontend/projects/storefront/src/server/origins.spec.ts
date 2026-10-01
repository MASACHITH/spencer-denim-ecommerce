import { BRAND } from '@spencer-denim/shared';

import { publicOrigin } from './origins';

/**
 * Which origin the server is willing to publish, which is a security question before it is an SEO one.
 *
 * `Host` and `X-Forwarded-Host` are sent by the client. W1-01 round 3 shipped a defect of exactly this
 * shape — a forged `X-Forwarded-For` written straight into the audit log — and the fix was to stop
 * trusting the header. Here a forged host would land in the `Sitemap:` line of `robots.txt` and in
 * every `og:url`, which is this shop telling a crawler to go and index someone else's site in its name.
 */
describe('publicOrigin', () => {
  const saved = { ...process.env };

  afterEach(() => {
    process.env['PUBLIC_SITE_ORIGIN'] = saved['PUBLIC_SITE_ORIGIN'];
    process.env['NG_ALLOWED_HOSTS'] = saved['NG_ALLOWED_HOSTS'];
  });

  beforeEach(() => {
    delete process.env['PUBLIC_SITE_ORIGIN'];
    delete process.env['NG_ALLOWED_HOSTS'];
  });

  describe('the deployment’s own answer wins', () => {
    it('uses PUBLIC_SITE_ORIGIN even when a host header disagrees', () => {
      process.env['PUBLIC_SITE_ORIGIN'] = 'https://colombowardrobecompany.com';

      expect(publicOrigin({ host: 'localhost:4000' })).toBe('https://colombowardrobecompany.com');
    });

    it('drops a trailing slash rather than doubling a segment into every URL', () => {
      process.env['PUBLIC_SITE_ORIGIN'] = 'https://staging.example.com/';

      expect(publicOrigin({ host: 'localhost' })).toBe('https://staging.example.com');
    });

    for (const bad of [
      'https://example.com/shop',
      'ftp://example.com',
      'https://user:pw@example.com',
      'https://example.com?x=1',
      'not a url',
      '   ',
    ]) {
      it(`refuses ${JSON.stringify(bad)} and falls back rather than publishing it`, () => {
        process.env['PUBLIC_SITE_ORIGIN'] = bad;

        expect(publicOrigin({})).toBe(`https://${BRAND.domain}`);
      });
    }
  });

  describe('a host header is only repeated back when we recognise it', () => {
    it('trusts localhost, which is ng serve and the browser tests', () => {
      expect(publicOrigin({ host: 'localhost:4420' })).toBe('http://localhost:4420');
    });

    it('trusts a host listed in NG_ALLOWED_HOSTS, which is what the renderer validates against', () => {
      process.env['NG_ALLOWED_HOSTS'] = 'staging.colombowardrobecompany.com, colombowardrobecompany.com';

      expect(publicOrigin({ host: 'staging.colombowardrobecompany.com' })).toBe(
        'http://staging.colombowardrobecompany.com',
      );
    });

    it('does not repeat back a forged Host', () => {
      expect(publicOrigin({ host: 'evil.example.com' })).toBe(`https://${BRAND.domain}`);
    });

    it('does not repeat back a forged X-Forwarded-Host either', () => {
      expect(publicOrigin({ host: 'localhost', 'x-forwarded-host': 'evil.example.com' })).toBe(
        `https://${BRAND.domain}`,
      );
    });

    it('rejects a host that is not shaped like one, before it reaches the allow-list', () => {
      expect(publicOrigin({ host: 'localhost/evil.example.com' })).toBe(`https://${BRAND.domain}`);
      expect(publicOrigin({ host: 'localhost\nX-Injected: 1' })).toBe(`https://${BRAND.domain}`);
    });

    it('takes https only when a proxy said so in so many words', () => {
      process.env['NG_ALLOWED_HOSTS'] = 'shop.example.com';

      expect(publicOrigin({ host: 'shop.example.com', 'x-forwarded-proto': 'https' })).toBe(
        'https://shop.example.com',
      );
      expect(publicOrigin({ host: 'shop.example.com', 'x-forwarded-proto': 'HTTPS, http' })).toBe(
        'https://shop.example.com',
      );
      expect(publicOrigin({ host: 'shop.example.com', 'x-forwarded-proto': 'gopher' })).toBe(
        'http://shop.example.com',
      );
    });

    it('reads the first value when a header arrives repeated', () => {
      expect(publicOrigin({ host: ['localhost:4420', 'evil.example.com'] })).toBe(
        'http://localhost:4420',
      );
    });
  });

  it('always returns an absolute origin, because a relative og:url is simply dropped', () => {
    for (const headers of [{}, { host: '' }, { host: 'evil.example.com' }]) {
      expect(publicOrigin(headers)).toMatch(/^https?:\/\/[^/]+$/);
    }
  });
});
