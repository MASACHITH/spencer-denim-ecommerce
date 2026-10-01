import { TestBed } from '@angular/core/testing';
import { Title } from '@angular/platform-browser';

import { PageMeta } from './page-metadata';
import { SITE_ORIGIN } from './site-origin';

/**
 * The social card and the canonical address, together, because they are set and cleared together
 * (GLB-11).
 *
 * The defects this area has already had were both leaks rather than wrong values: a canonical the
 * listing set and nothing removed, so the home page claimed to be the jeans listing; and a `noindex`
 * a 404 left behind on the page the shopper went to next. So the clearing half is tested as carefully
 * as the setting half.
 */
describe('PageMeta', () => {
  const ORIGIN = 'https://shop.example.com';

  function subject(): PageMeta {
    TestBed.configureTestingModule({ providers: [{ provide: SITE_ORIGIN, useValue: ORIGIN }] });
    return TestBed.inject(PageMeta);
  }

  function tag(selector: string): string | null {
    return document.head.querySelector(`meta[${selector}]`)?.getAttribute('content') ?? null;
  }

  function canonical(): string | null {
    return document.head.querySelector('link[rel="canonical"]')?.getAttribute('href') ?? null;
  }

  afterEach(() => {
    document.head.querySelectorAll('link[rel="canonical"], meta[property^="og:"], meta[name="twitter:card"]')
      .forEach((element) => element.remove());
  });

  it('sets the title, the description and the canonical address', () => {
    subject().set({ title: 'Jeans | Colombo Wardrobe', description: 'Denim.', path: '/women/jeans' });

    expect(TestBed.inject(Title).getTitle()).toBe('Jeans | Colombo Wardrobe');
    expect(tag('name="description"')).toBe('Denim.');
    expect(canonical()).toBe('/women/jeans');
  });

  /**
   * The canonical stays relative — `CanonicalLink` says why — while `og:url` is absolute, because the
   * Open Graph consumers do not resolve a relative URL against the document; they drop the tag, and
   * the share card goes with it. Both halves are asserted together so neither can be "tidied" into
   * matching the other.
   */
  it('publishes og:url absolute while the canonical stays relative', () => {
    subject().set({ title: 'T', description: 'D', path: '/women/jeans?colour=navy' });

    expect(canonical()).toBe('/women/jeans?colour=navy');
    expect(tag('property="og:url"')).toBe(`${ORIGIN}/women/jeans?colour=navy`);
  });

  it('describes the page to a share card', () => {
    subject().set({ title: 'Wide-leg jean', description: 'A jean.', path: '/product/wide-leg', kind: 'product' });

    expect(tag('property="og:site_name"')).toBe('Colombo Wardrobe');
    expect(tag('property="og:type"')).toBe('product');
    expect(tag('property="og:title"')).toBe('Wide-leg jean');
    expect(tag('property="og:description"')).toBe('A jean.');
    expect(tag('property="og:locale"')).toBe('en_GB');
  });

  it('defaults a page with no kind of its own to a website', () => {
    subject().set({ title: 'T', description: 'D', path: '/' });

    expect(tag('property="og:type"')).toBe('website');
  });

  describe('the picture', () => {
    it('makes a site-relative media key absolute, which is how the catalogue publishes them', () => {
      subject().set({
        title: 'T',
        description: 'D',
        path: '/product/x',
        imageUrl: '/media/public/renditions/abc/large.webp',
        imageAlt: 'A jean on a hanger',
      });

      expect(tag('property="og:image"')).toBe(`${ORIGIN}/media/public/renditions/abc/large.webp`);
      expect(tag('property="og:image:alt"')).toBe('A jean on a hanger');
      expect(tag('name="twitter:card"')).toBe('summary_large_image');
    });

    /** The day `CatalogOptions.MediaBaseUrl` points at a CDN, these arrive absolute already. */
    it('leaves a URL that is already absolute alone', () => {
      subject().set({
        title: 'T',
        description: 'D',
        path: '/product/x',
        imageUrl: 'https://cdn.example.net/a.webp',
      });

      expect(tag('property="og:image"')).toBe('https://cdn.example.net/a.webp');
    });

    it('shrinks the card instead of publishing an empty image tag', () => {
      subject().set({ title: 'T', description: 'D', path: '/', imageUrl: null });

      expect(tag('property="og:image"')).toBeNull();
      expect(tag('property="og:image:alt"')).toBeNull();
      expect(tag('name="twitter:card"')).toBe('summary');
    });

    /**
     * The home page's picture arrives with the content API, a moment after the first render, so the
     * same page sets its metadata twice — once without an image and once with. The second call has to
     * overwrite rather than add, and has to upgrade the card type with it.
     */
    it('upgrades the card when the picture arrives on a later render', () => {
      const meta = subject();
      meta.set({ title: 'T', description: 'D', path: '/' });
      meta.set({ title: 'T', description: 'D', path: '/', imageUrl: '/media/public/hero.webp' });

      expect(document.head.querySelectorAll('meta[property="og:image"]')).toHaveLength(1);
      expect(tag('name="twitter:card"')).toBe('summary_large_image');
    });

    it('drops the alt when the next page has a picture and nothing to say about it', () => {
      const meta = subject();
      meta.set({ title: 'T', description: 'D', path: '/a', imageUrl: '/a.webp', imageAlt: 'Something' });
      meta.set({ title: 'T', description: 'D', path: '/b', imageUrl: '/b.webp' });

      expect(tag('property="og:image:alt"')).toBeNull();
    });
  });

  it('takes the whole card and the canonical back down on the way out', () => {
    const meta = subject();
    meta.set({
      title: 'T',
      description: 'D',
      path: '/product/x',
      kind: 'product',
      imageUrl: '/media/public/a.webp',
      imageAlt: 'A',
    });

    meta.clear();

    expect(canonical()).toBeNull();
    expect(document.head.querySelectorAll('meta[property^="og:"]')).toHaveLength(0);
    expect(tag('name="twitter:card"')).toBeNull();
    // Deliberately kept: every route sets both, so blanking them would only flicker the tab.
    expect(TestBed.inject(Title).getTitle()).toBe('T');
    expect(tag('name="description"')).toBe('D');
  });
});
