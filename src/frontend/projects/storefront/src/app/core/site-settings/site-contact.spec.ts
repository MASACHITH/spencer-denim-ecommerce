import { MAP_EMBED_HOSTS } from './site-settings.model';
import { EMPTY_SITE_CONTACT, toContact, toMapEmbedUrl, toPhoneHref } from './site-settings.service';

/**
 * The shop's contact details on the way to the storefront (GLB-07, GLB-10, ADM-22 part 2).
 *
 * **The map address is the security-relevant one and most of this file is about it.** The value becomes
 * an `iframe src` inside a page served from the shop's own origin, so whoever can edit settings could
 * otherwise frame anything there — a credential-harvesting form under the shop's address bar, for
 * instance. The API checks the host on write and again on read; this is the third check, in the
 * browser's own parser, and it is the one that decides what is actually framed. Checking one string and
 * following another is the gap W1-12's banner-link validator was defeated through twice.
 */
describe('toMapEmbedUrl (GLB-10)', () => {
  it.each([
    'https://www.google.com/maps/embed?pb=!1m18!1m12',
    'https://maps.google.com/maps/embed?pb=x',
    'https://www.google.lk/maps/embed?pb=x',
    'https://www.openstreetmap.org/export/embed.html?bbox=1,2,3,4',
    'https://openstreetmap.org/export/embed.html?bbox=1,2,3,4',
  ])('accepts %s', (url) => {
    expect(toMapEmbedUrl(url)).not.toBeNull();
  });

  /**
   * Every one of these is a value an admin could paste and a browser would happily load. The host
   * allow-list is the only thing that refuses them.
   */
  it.each([
    // A perfectly valid https URL on a host that is not a map provider — the case the allow-list exists
    // for, and the one a "must be https" rule would wave through.
    'https://evil.example/pretend-map',

    // The provider's name in the path, in a subdomain, and as a userinfo part: three ways a value reads
    // as Google Maps and resolves somewhere else.
    'https://evil.example/www.google.com/maps/embed',
    'https://www-google-com.evil.example/maps/embed',
    'https://www.google.com@evil.example/maps/embed',

    // Mixed content: a map framed over http inside an https page is blocked by the browser, so storing
    // one would silently show nothing.
    'http://www.google.com/maps/embed?pb=x',

    // Schemes, which `normaliseExternalHttpUrl` refuses before the host is even considered.
    'javascript:alert(1)',
    'data:text/html,<script>alert(1)</script>',
    'file:///etc/passwd',

    // A browser strips TAB, CR and LF before parsing a URL and reads a backslash as a slash, so a value
    // containing either is not the value `new URL` is about to see.
    'https://www.google.com\\@evil.example/maps',
    'https://www.google.com\t.evil.example/maps',

    // Relative and empty: an href with no host resolves against the current page.
    '/maps/embed',
    '',
    '   ',
  ])('refuses %p', (url) => {
    expect(toMapEmbedUrl(url)).toBeNull();
  });

  it.each([null, undefined, 42, {}, []])('refuses %p, which is not a string at all', (value) => {
    expect(toMapEmbedUrl(value)).toBeNull();
  });

  /**
   * The allow-list is the control, so the list itself is asserted rather than only its effects: a future
   * edit that adds a host fails here with the name of what was added.
   */
  it('holds only the two map providers', () => {
    expect([...MAP_EMBED_HOSTS].sort()).toEqual([
      'maps.google.com',
      'openstreetmap.org',
      'www.google.com',
      'www.google.lk',
      'www.openstreetmap.org',
    ]);
  });
});

describe('toPhoneHref (GLB-07)', () => {
  /**
   * The `tel:` target is built from the digits rather than taken from the payload, because a displayed
   * number and a dialled one are two different strings and the API stores only the one a person reads.
   */
  it.each([
    ['+94 11 234 5678', '+94112345678'],
    ['011 234 5678', '0112345678'],
    ['(011) 234-5678', '0112345678'],
    ['+94 (0)77 123 4567', '+94077123 4567'.replace(' ', '')],
  ])('turns %p into %p', (typed, expected) => {
    expect(toPhoneHref(typed)).toBe(expected);
  });

  it('refuses a value with too few digits to dial', () => {
    expect(toPhoneHref('12345')).toBeNull();
  });

  it('answers null for a number that was never given', () => {
    expect(toPhoneHref(null)).toBeNull();
  });
});

describe('toContact (GLB-07, GLB-10)', () => {
  /**
   * **Nothing is invented, and this is the state a fresh shop is in.** ADM-22 removed three placeholder
   * social links because "there is no placeholder value for this shop's Instagram that is not a lie";
   * an address is the same case and sharper, because a shopper can drive to it.
   */
  it.each([null, undefined, 'nope', 42])('answers the empty contact for %p', (payload) => {
    expect(toContact(payload as never)).toEqual(EMPTY_SITE_CONTACT);
  });

  it('keeps what the client has supplied and says there is something to draw', () => {
    const contact = toContact({
      phone: ' +94 11 234 5678 ',
      email: ' hello@example.com ',
      addressLines: ['Colombo Wardrobe', '  ', '42 Galle Road'],
      openingHours: ['Monday to Friday, 9am to 6pm'],
      mapEmbedUrl: 'https://www.google.com/maps/embed?pb=x',
    });

    expect(contact.phone).toBe('+94 11 234 5678');
    expect(contact.phoneHref).toBe('+94112345678');
    expect(contact.email).toBe('hello@example.com');

    // The blank line is dropped rather than rendered as an empty paragraph.
    expect(contact.addressLines).toEqual(['Colombo Wardrobe', '42 Galle Road']);
    expect(contact.openingHours).toEqual(['Monday to Friday, 9am to 6pm']);
    expect(contact.mapEmbedUrl).not.toBeNull();
    expect(contact.hasAny).toBe(true);
  });

  /** A blank string is "not given", not an empty value to render. */
  it('reads a blank field as absent', () => {
    const contact = toContact({
      phone: '   ',
      email: '',
      addressLines: [],
      openingHours: [],
      mapEmbedUrl: '   ',
    });

    expect(contact).toEqual(EMPTY_SITE_CONTACT);
    expect(contact.hasAny).toBe(false);
  });

  /** A value of the wrong type is dropped rather than rendered as `[object Object]`. */
  it('drops a line that is not a string', () => {
    const contact = toContact({
      phone: null,
      email: null,
      addressLines: [{ nope: true }, 'Colombo'] as never,
      openingHours: 'not an array' as never,
      mapEmbedUrl: null,
    });

    expect(contact.addressLines).toEqual(['Colombo']);
    expect(contact.openingHours).toEqual([]);
  });

  /**
   * Capped, because a value that somehow held two hundred newlines would otherwise become two hundred
   * paragraphs on every page of the shop. The API caps it too; this is the side that draws it.
   */
  it('caps the lines it will draw', () => {
    const contact = toContact({
      phone: null,
      email: null,
      addressLines: Array.from({ length: 40 }, (_, index) => `Line ${index}`),
      openingHours: [],
      mapEmbedUrl: null,
    });

    expect(contact.addressLines).toHaveLength(8);
  });

  /**
   * `hasAny` is true when *anything* is set, because the Contact page uses it to decide between a panel
   * and nothing: a heading called "Find us" with nothing under it is worse than no heading.
   */
  it.each([
    [{ phone: '+94 11 234 5678' }],
    [{ email: 'hello@example.com' }],
    [{ addressLines: ['Colombo'] }],
    [{ openingHours: ['Monday'] }],
    [{ mapEmbedUrl: 'https://www.google.com/maps/embed?pb=x' }],
  ])('says there is something to draw when only %p is set', (partial) => {
    const contact = toContact({
      phone: null,
      email: null,
      addressLines: [],
      openingHours: [],
      mapEmbedUrl: null,
      ...partial,
    } as never);

    expect(contact.hasAny).toBe(true);
  });
});
