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

    /**
     * **F6: it was refused in silence.** The docblock says a deployment is better served by the brand
     * fallback "and a look at its configuration" than by a plausible-looking wrong answer - and then
     * said nothing, so there was nothing to prompt the look. A value that was *set* and refused is a
     * configuration mistake rather than a default, and the only case here worth an error.
     */
    it('says so, once, when a configured value is refused', () => {
      const said: string[] = [];
      const original = console.error;
      console.error = (message: unknown) => said.push(String(message));

      try {
        process.env['PUBLIC_SITE_ORIGIN'] = 'https://evil.example.com/shop';

        expect(publicOrigin({})).toBe(`https://${BRAND.domain}`);
        expect(publicOrigin({})).toBe(`https://${BRAND.domain}`);
      } finally {
        console.error = original;
      }

      // Once per process, not once per request: it is a standing condition, and `sitemap.xml` plus every
      // rendered page would otherwise repeat it until the disk filled.
      expect(said).toHaveLength(1);
      expect(said[0]).toContain('PUBLIC_SITE_ORIGIN');
      expect(said[0]).toContain('/shop');
      expect(said[0]).toContain(BRAND.domain);
    });

    /**
     * The message describes the value rather than echoing it. There is no attacker in a deployment's own
     * environment, but one of the shapes this refuses is `https://user:password@host`, and a log line is
     * the wrong place for a credential.
     */
    it('describes a refused value without repeating a credential in it', () => {
      const said: string[] = [];
      const original = console.error;
      console.error = (message: unknown) => said.push(String(message));

      try {
        process.env['PUBLIC_SITE_ORIGIN'] = 'https://admin:s3cret@example.com';
        publicOrigin({});
      } finally {
        console.error = original;
      }

      expect(said).toHaveLength(1);
      expect(said[0]).toContain('credentials');
      expect(said[0]).not.toContain('s3cret');
      expect(said[0]).not.toContain('admin:');
    });

    it('says nothing when the variable is simply unset, which is not a mistake', () => {
      const said: string[] = [];
      const original = console.error;
      console.error = (message: unknown) => said.push(String(message));

      try {
        delete process.env['PUBLIC_SITE_ORIGIN'];
        publicOrigin({ host: 'localhost:4420' });
      } finally {
        console.error = original;
      }

      expect(said).toEqual([]);
    });
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

    /**
     * **F7: an allow-listed host's case was echoed verbatim.** `Host: LOCALHOST:5200` produced
     * `Sitemap: http://LOCALHOST:5200/sitemap.xml`, so one deployment could publish several spellings of
     * a single address across its sitemap and its `og:url` tags. The allow-list already lower-cases the
     * name to make its decision; publishing what it matched is what stops the answer disagreeing with
     * the check.
     */
    it('publishes the normalised host, not the spelling the client sent', () => {
      expect(publicOrigin({ host: 'LOCALHOST:5200' })).toBe('http://localhost:5200');
      expect(publicOrigin({ host: 'LocalHost' })).toBe('http://localhost');

      process.env['NG_ALLOWED_HOSTS'] = 'shop.example.com';
      expect(publicOrigin({ host: 'SHOP.Example.COM:8443' })).toBe('http://shop.example.com:8443');
    });

    /** The port is part of an origin, so it is kept - only the case is settled. */
    it('keeps the port, which ng serve and the browser tests are reached on', () => {
      expect(publicOrigin({ host: 'localhost:4960' })).toBe('http://localhost:4960');
      expect(publicOrigin({ host: '127.0.0.1:4421' })).toBe('http://127.0.0.1:4421');
    });
  });

  it('always returns an absolute origin, because a relative og:url is simply dropped', () => {
    for (const headers of [{}, { host: '' }, { host: 'evil.example.com' }]) {
      expect(publicOrigin(headers)).toMatch(/^https?:\/\/[^/]+$/);
    }
  });
});
