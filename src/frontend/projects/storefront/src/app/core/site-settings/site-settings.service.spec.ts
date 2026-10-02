import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, TestRequest, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import {
  EMPTY_SITE_SETTINGS,
  SiteSettingsService,
  isExternalHttpUrl,
  normaliseExternalHttpUrl,
  toSiteSettings,
} from './site-settings.service';
import { SiteSettingsDto } from './site-settings.model';
import { ServerHandover } from '../platform/server-handover';

/**
 * The shell's settings (GLB-02, GLB-07, GLB-08, ADM-22).
 *
 * Most of this file is about the mapping rather than the service, because the mapping is where a bad
 * payload has to stop. A `200` with the wrong shape never reaches `catchError` — the lesson
 * `announcement.service.ts` records — so every field is tested against what the template will do with it.
 */
describe('toSiteSettings (ADM-22)', () => {
  /** The honest default for a shop that has configured nothing, which is every shop today. */
  const NOTHING: SiteSettingsDto = { logo: null, whatsAppNumber: null, social: [] };

  it('maps the payload a configured shop sends', () => {
    const settings = toSiteSettings({
      logo: { url: '/media/logo.webp', width: 600, height: 150, altText: 'Our logo' },
      whatsAppNumber: '94771234567',
      social: [
        { network: 'instagram', url: 'https://www.instagram.com/colombowardrobe' },
        { network: 'facebook', url: 'https://www.facebook.com/colombowardrobe' },
      ],
    });

    expect(settings.logo).toEqual({
      url: '/media/logo.webp',
      width: 600,
      height: 150,
      alt: 'Our logo',
    });
    expect(settings.whatsAppNumber).toBe('94771234567');

    // The API's order is kept; the footer draws them in the order it is given.
    expect(settings.social.map((link) => link.network)).toEqual(['instagram', 'facebook']);
    expect(settings.social[0].icon).toBe('instagram');
    expect(settings.social[0].label).toBe('Colombo Wardrobe on Instagram');
  });

  it('maps an unconfigured shop to the empty settings', () => {
    expect(toSiteSettings(NOTHING)).toEqual(EMPTY_SITE_SETTINGS);
  });

  it.each([null, undefined, 'not an object' as never, 42 as never])(
    'falls back for a payload that is not an object (%p)',
    (payload) => {
      expect(toSiteSettings(payload as SiteSettingsDto | null)).toEqual(EMPTY_SITE_SETTINGS);
    },
  );

  describe('the logo', () => {
    /**
     * An `<img src="">` re-requests the current document and then fails to decode it, so the header
     * would show a broken-image glyph exactly where the wordmark belongs — the outcome the fallback
     * exists to avoid, arrived at from the other direction.
     */
    it.each(['', '   '])('treats a blank url (%p) as no logo', (url) => {
      expect(toSiteSettings({ ...NOTHING, logo: { url, width: 1, height: 1, altText: 'x' } }).logo).toBeNull();
    });

    it('treats a non-string url as no logo', () => {
      const logo = { url: 42, width: 1, height: 1, altText: 'x' } as unknown as SiteSettingsDto['logo'];

      expect(toSiteSettings({ ...NOTHING, logo }).logo).toBeNull();
    });

    it('falls back to the shop name when the asset has no alt text', () => {
      expect(toSiteSettings({ ...NOTHING, logo: { url: '/l.webp', width: null, height: null, altText: null } }).logo)
        .toEqual({ url: '/l.webp', width: null, height: null, alt: 'Colombo Wardrobe' });
    });

    it('falls back to the shop name when the alt text is only whitespace', () => {
      expect(toSiteSettings({ ...NOTHING, logo: { url: '/l.webp', width: null, height: null, altText: '  ' } })
        .logo?.alt).toBe('Colombo Wardrobe');
    });

    /** `width="0"` collapses the image to nothing; so does a negative or non-finite one. */
    it.each([0, -1, Number.NaN, Number.POSITIVE_INFINITY, '600' as never])(
      'drops an unusable dimension (%p) rather than writing it to the attribute',
      (width) => {
        const logo = { url: '/l.webp', width, height: 150, altText: 'x' } as SiteSettingsDto['logo'];

        expect(toSiteSettings({ ...NOTHING, logo }).logo?.width).toBeNull();
      },
    );

    it('rounds a fractional dimension rather than emitting it', () => {
      const logo = { url: '/l.webp', width: 600.4, height: 150.6, altText: 'x' };

      expect(toSiteSettings({ ...NOTHING, logo }).logo).toMatchObject({ width: 600, height: 151 });
    });
  });

  describe('the WhatsApp number', () => {
    it('keeps a number in international form', () => {
      expect(toSiteSettings({ ...NOTHING, whatsAppNumber: ' 94771234567 ' }).whatsAppNumber).toBe('94771234567');
    });

    it.each([
      ['a leading zero, which is a national number', '0771234567'],
      ['a plus sign, which wa.me does not take', '+94771234567'],
      ['spaces inside', '94 77 123 4567'],
      ['too few digits', '941234'],
      ['more than E.164 allows', '9477123456789012'],
      ['letters', '94771234567x'],
      ['not a string', 42 as never],
    ])('drops %s', (_why, value) => {
      expect(toSiteSettings({ ...NOTHING, whatsAppNumber: value as string }).whatsAppNumber).toBeNull();
    });
  });

  describe('the social links', () => {
    it('drops a network this build has no icon for', () => {
      const settings = toSiteSettings({
        ...NOTHING,
        social: [
          { network: 'youtube', url: 'https://www.youtube.com/@shop' },
          { network: 'pinterest', url: 'https://pinterest.com/shop' },
        ],
      });

      // Not "renders a blank icon": an sd-icon with an unrecognised name draws nothing, which would leave
      // a 44 px hole with a live link in it.
      expect(settings.social).toEqual([]);
    });

    it('matches a network name case-insensitively', () => {
      const settings = toSiteSettings({
        ...NOTHING,
        social: [{ network: 'Instagram', url: 'https://www.instagram.com/shop' }],
      });

      expect(settings.social.map((link) => link.network)).toEqual(['instagram']);
    });

    it('stores the normalised address, so the icon links where the text says', () => {
      const settings = toSiteSettings({
        ...NOTHING,
        social: [{ network: 'instagram', url: 'https://INSTAGRAM.com。evil.example/shop' }],
      });

      expect(settings.social[0].url).toBe('https://instagram.com.evil.example/shop');
    });

    it('keeps the first of two rows for the same network, not both icons', () => {
      const settings = toSiteSettings({
        ...NOTHING,
        social: [
          { network: 'tiktok', url: 'https://www.tiktok.com/@first' },
          { network: 'tiktok', url: 'https://www.tiktok.com/@second' },
        ],
      });

      expect(settings.social.map((link) => link.url)).toEqual(['https://www.tiktok.com/@first']);
    });

    it('falls back for a social list that is not an array', () => {
      const payload = { ...NOTHING, social: { network: 'tiktok' } } as unknown as SiteSettingsDto;

      expect(toSiteSettings(payload).social).toEqual([]);
    });
  });
});

/**
 * The URL gate, tested on its own because it is the last thing between a stored value and an `href`.
 *
 * The API refuses all of these on the way in. This is the second check, and the reason it exists is that
 * a row can predate the rule, be written by a migration or survive a rule somebody loosened — and
 * W1-12's banner-link validator was defeated three times by values that only a browser's own parsing
 * reveals.
 */
describe('isExternalHttpUrl (ADM-22)', () => {
  it.each([
    'https://www.instagram.com/colombowardrobe',
    'http://example.com/shop',
    'https://example.com',
    'https://example.com/path?q=1#frag',
  ])('accepts %p', (value) => {
    expect(isExternalHttpUrl(value)).toBe(true);
  });

  it.each([
    ['a javascript scheme', 'javascript:alert(1)'],
    ['a javascript scheme in mixed case', 'JavaScript:alert(1)'],
    ['a data URL', 'data:text/html,<script>alert(1)</script>'],
    ['a vbscript scheme', 'vbscript:msgbox(1)'],
    ['a file URL', 'file:///etc/passwd'],
    ['a site-relative path', '/women/jeans'],
    ['a protocol-relative URL', '//evil.example/'],
    ['a backslash, which a browser reads as a slash', 'https:\\\\evil.example'],
    ['a backslash after the scheme', 'https://good.example\\@evil.example'],
    ['a tab inside', 'https://good.\texample.com'],
    ['a newline inside', 'https://good.\nexample.com'],
    ['a carriage return inside', 'https://good.\rexample.com'],
    ['a NUL', 'https://good.example\u0000'],
    ['credentials, which read as one destination and go to another', 'https://instagram.com@evil.example/'],
    ['a password', 'https://user:password@evil.example/'],
    ['nothing at all', ''],
    ['only whitespace', '   '],
    ['not a string', 42],
    ['null', null],
    ['undefined', undefined],
  ])('refuses %s', (_why, value) => {
    expect(isExternalHttpUrl(value)).toBe(false);
  });

  /**
   * **The two parsers disagree about this one, and the test records which is which rather than asserting
   * the version that reads better.**
   *
   * `http:///profile` has no authority. .NET refuses it — `Uri.TryCreate` returns false outright, which is
   * what was measured in round 1 after an earlier version of this comment claimed .NET gave it an *empty
   * host*. The WHATWG parser a browser and `new URL` use normalises it to `http://profile/` instead: host
   * `profile`, a perfectly ordinary absolute address to a host that does not exist.
   *
   * So on this side there is nothing to refuse — it is an absolute http URL with a host, and treating it
   * as dangerous would be describing the wrong parser. The value never reaches the database, because the
   * server is the stricter of the two; if one ever did, it would render as a dead link rather than a
   * same-page one. Written down because the first version of this test asserted `false` here, from the
   * server's reading, and was wrong.
   */
  /**
   * **The `href` is the parsed form, not the text.**
   *
   * The API normalises on write and on read, so a homograph host cannot reach the storefront from our own
   * server — but this is the function that chooses the `href`, and checking one string while rendering another
   * is the gap W1-12's validator was defeated through twice. Using `URL.href` costs nothing (it is what the
   * browser would resolve anyway) and removes the last place the two could differ.
   */
  it.each([
    ['https://INSTAGRAM.com/Shop', 'https://instagram.com/Shop'],
    ['https://example.com:443/shop', 'https://example.com/shop'],
    ['https://example.com', 'https://example.com/'],
    ['https://instagram.com。evil.example/', 'https://instagram.com.evil.example/'],
  ])('normalises %p to %p', (value, expected) => {
    expect(normaliseExternalHttpUrl(value)).toBe(expected);
  });

  it('returns null rather than a half-built address for something it refuses', () => {
    expect(normaliseExternalHttpUrl('javascript:alert(1)')).toBeNull();
    expect(normaliseExternalHttpUrl('/women')).toBeNull();
    expect(normaliseExternalHttpUrl(42)).toBeNull();
  });

  it('reads an authority-less http URL the way a browser does, as an absolute address', () => {
    expect(new URL('http:///profile').hostname).toBe('profile');
    expect(isExternalHttpUrl('http:///profile')).toBe(true);
  });
});

describe('SiteSettingsService (ADM-22)', () => {
  function setUp() {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });

    const service = TestBed.inject(SiteSettingsService);
    const http = TestBed.inject(HttpTestingController);

    return { service, http };
  }

  afterEach(() => TestBed.resetTestingModule());

  it('reads the settings from /api/site/settings', () => {
    const { service, http } = setUp();

    // Reading the signal is what subscribes, so the request does not exist until something asks.
    expect(service.logo()).toBeNull();

    http.expectOne('/api/site/settings').flush({
      logo: { url: '/media/logo.webp', width: 600, height: 150, altText: 'Logo' },
      whatsAppNumber: '94771234567',
      social: [{ network: 'facebook', url: 'https://www.facebook.com/shop' }],
    });

    expect(service.logo()?.url).toBe('/media/logo.webp');
    expect(service.whatsAppNumber()).toBe('94771234567');
    expect(service.social()).toHaveLength(1);
    http.verify();
  });

  /**
   * W2-10's lesson: a shell element must never take the page down. Each of these is a different way the
   * read can go wrong, and all three end at the same rendering — the one every other test already
   * exercises.
   */
  it.each([
    ['a 500', (request: TestRequest) => request.flush('boom', { status: 500, statusText: 'Server Error' })],
    ['a 404', (request: TestRequest) => request.flush('gone', { status: 404, statusText: 'Not Found' })],
    ['a null body', (request: TestRequest) => request.flush(null)],
  ])('falls back to empty settings on %s', (_why, answer) => {
    const { service, http } = setUp();

    expect(service.logo()).toBeNull();
    answer(http.expectOne('/api/site/settings'));

    expect(service.settings()).toEqual(EMPTY_SITE_SETTINGS);
    http.verify();
  });

  /**
   * **What the server renders is what it hands over — including the fallback.**
   *
   * The first version put `tap(give)` upstream of `timeout`/`catchError`, so a read that failed or was slow
   * handed over nothing and the browser re-fetched on hydration. If that second attempt succeeded, the logo
   * appeared *after* hydration — the pop-in `site-header.ts` says the handover prevents. W2-07's Major 3, one
   * feature over: hand over only what has been vetted, and hand over whatever the render actually used.
   *
   * `ServerHandover.give` is a no-op in a browser, so the port is substituted to observe the call. The order of
   * the pipe is the thing under test, and it is not observable any other way.
   */
  it.each([
    ['a 500', (request: TestRequest) => request.flush('boom', { status: 500, statusText: 'Server Error' })],
    ['a null body', (request: TestRequest) => request.flush(null)],
  ])('hands over the fallback it rendered with after %s', (_why, answer) => {
    const recorded: unknown[] = [];

    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        {
          provide: ServerHandover,
          useValue: {
            take: () => null,
            give: (_key: string, value: unknown) => recorded.push(value),
          },
        },
      ],
    });

    const service = TestBed.inject(SiteSettingsService);
    const http = TestBed.inject(HttpTestingController);

    expect(service.logo()).toBeNull();
    answer(http.expectOne('/api/site/settings'));

    expect(recorded).toEqual([EMPTY_SITE_SETTINGS]);
    http.verify();
  });

  it('hands over the mapped settings on success', () => {
    const recorded: unknown[] = [];

    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        {
          provide: ServerHandover,
          useValue: {
            take: () => null,
            give: (_key: string, value: unknown) => recorded.push(value),
          },
        },
      ],
    });

    const service = TestBed.inject(SiteSettingsService);
    const http = TestBed.inject(HttpTestingController);

    expect(service.logo()).toBeNull();
    http.expectOne('/api/site/settings').flush({
      logo: { url: '/media/logo.webp', width: 600, height: 150, altText: 'Logo' },
      whatsAppNumber: null,
      social: [],
    });

    // The mapped value, not the raw payload: the handover is what the browser adopts without re-checking.
    expect(recorded).toHaveLength(1);
    expect((recorded[0] as { logo: { alt: string } }).logo.alt).toBe('Logo');
    http.verify();
  });

  it('falls back to empty settings when the network is unreachable', () => {
    const { service, http } = setUp();

    expect(service.logo()).toBeNull();
    http.expectOne('/api/site/settings').error(new ProgressEvent('error'), { status: 0 });

    expect(service.settings()).toEqual(EMPTY_SITE_SETTINGS);
    http.verify();
  });
});
