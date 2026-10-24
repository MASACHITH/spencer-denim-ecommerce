import { Component } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { LegalLinks } from './legal-links';
import { flushContentPages, primeContentPages } from '../../core/content/content-pages.testing';
import { ContentPageLinkDto } from '../../core/content/content-page.model';

const TERMS: Partial<ContentPageLinkDto> = { slug: 'terms-of-sale', title: 'Terms and conditions', systemKey: 'terms' };
const PRIVACY: Partial<ContentPageLinkDto> = { slug: 'privacy', title: 'Privacy policy', systemKey: 'privacy' };

/**
 * A host so the inputs are set the way a template sets them. The component's whole contract is "these sentences, with
 * these pages, linked when published" (issue #252), and a template is how every real caller uses it.
 */
@Component({
  imports: [LegalLinks],
  template: `
    <sd-legal-links
      termsLead="You agree to our"
      privacyLead="Details are in our"
      [terms]="terms"
      [privacy]="privacy"
      [termsFallback]="termsFallback"
      [newTab]="newTab"
    />
  `,
})
class Host {
  terms = false;
  privacy = false;
  termsFallback = false;
  newTab = false;
}

describe('LegalLinks (issue #252)', () => {
  let fixture: ComponentFixture<Host>;
  let http: HttpTestingController;

  function setUp(inputs: Partial<Host>): HTMLElement {
    TestBed.configureTestingModule({
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });

    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(Host);
    Object.assign(fixture.componentInstance, inputs);
    fixture.detectChanges();

    return fixture.nativeElement as HTMLElement;
  }

  async function publish(pages: readonly Partial<ContentPageLinkDto>[]): Promise<void> {
    flushContentPages(http, pages);
    fixture.detectChanges();
    await fixture.whenStable();
  }

  function sentence(element: HTMLElement): string {
    return (element.textContent ?? '').replace(/\s+/g, ' ').trim();
  }

  afterEach(() => http.verify());

  describe('while the index has not answered', () => {
    it('says nothing, rather than naming pages that may not exist', () => {
      const element = setUp({ terms: true, privacy: true });

      expect(sentence(element)).toBe('');

      flushContentPages(http, []);
    });

    it('still says the plain terms where the sentence already existed (termsFallback)', async () => {
      const element = setUp({ terms: true, termsFallback: true });

      await publish([]);

      expect(sentence(element)).toBe('You agree to our terms and conditions.');
      expect(element.querySelector('a')).toBeNull();
    });
  });

  describe('once pages are published', () => {
    it('makes the terms and the privacy policy two sentences, each linked by its own slug', async () => {
      const element = setUp({ terms: true, privacy: true });

      await publish([TERMS, PRIVACY]);

      expect(sentence(element)).toBe('You agree to our terms and conditions. Details are in our privacy policy.');
      expect(
        [...element.querySelectorAll('a')].map((link) => [link.getAttribute('href'), link.textContent]),
      ).toEqual([
        ['/terms-of-sale', 'terms and conditions'],
        ['/privacy', 'privacy policy'],
      ]);
    });

    it('writes the page title in lower case, in every state, whatever capitals the admin gave it', async () => {
      const element = setUp({ terms: true, privacy: true });

      await publish([
        { ...TERMS, title: 'Terms Of Sale' },
        { ...PRIVACY, title: 'PRIVACY POLICY' },
      ]);

      expect(sentence(element)).toBe('You agree to our terms of sale. Details are in our privacy policy.');
    });

    it('reads the same with only the privacy policy published as it does with the terms in plain words', async () => {
      const element = setUp({ terms: true, privacy: true, termsFallback: true });

      await publish([PRIVACY]);

      expect(sentence(element)).toBe('You agree to our terms and conditions. Details are in our privacy policy.');
      expect(element.querySelectorAll('a')).toHaveLength(1);
    });

    it('follows an edited slug, because the address comes from the index and is never written here', async () => {
      const element = setUp({ privacy: true });

      await publish([{ ...PRIVACY, slug: 'how-we-use-your-data' }]);

      expect(element.querySelector('a')?.getAttribute('href')).toBe('/how-we-use-your-data');
    });

    it('names a page only when the caller asked for it', async () => {
      const element = setUp({ terms: true });

      await publish([TERMS, PRIVACY]);

      expect(sentence(element)).toBe('You agree to our terms and conditions.');
    });

    it('links the page that is published and leaves the unpublished one out', async () => {
      const element = setUp({ terms: true, privacy: true });

      await publish([PRIVACY]);

      expect(sentence(element)).toBe('Details are in our privacy policy.');
    });

    it('leaves the sentences out entirely when every page they name is unpublished', async () => {
      const element = setUp({ terms: true, privacy: true });

      await publish([{ slug: 'about', title: 'About us', systemKey: 'about' }]);

      expect(sentence(element)).toBe('');
      expect(element.querySelector('a')).toBeNull();
    });

    it('never falls back to plain words for the privacy policy', async () => {
      const element = setUp({ privacy: true, termsFallback: true });

      await publish([]);

      expect(sentence(element)).toBe('');
    });

    it('matches a page by its system key and not by what its slug happens to be', async () => {
      const element = setUp({ privacy: true });

      // A page whose slug is "privacy" but which is not the privacy page (no system key) is not it.
      await publish([{ slug: 'privacy', title: 'Something else', systemKey: null }]);

      expect(sentence(element)).toBe('');
    });
  });

  describe('where leaving the page would lose what the shopper typed (newTab)', () => {
    it('opens each link in a new tab, without giving the new page a handle on this one', async () => {
      const element = setUp({ terms: true, privacy: true, newTab: true });

      await publish([TERMS, PRIVACY]);

      const links = [...element.querySelectorAll('a')];
      expect(links).toHaveLength(2);
      for (const link of links) {
        expect(link.getAttribute('target')).toBe('_blank');
        expect(link.getAttribute('rel')).toBe('noopener');
      }
    });

    it('says so to a screen reader, inside the link, so the link name carries it', async () => {
      const element = setUp({ terms: true, newTab: true });

      await publish([TERMS]);

      const link = element.querySelector('a') as HTMLAnchorElement;
      expect(link.querySelector('.sr-only')?.textContent?.trim()).toBe('(opens in a new tab)');
      expect(link.textContent?.replace(/\s+/g, ' ').trim()).toBe('terms and conditions (opens in a new tab)');
    });

    it('does not ask for a new tab, or say so, when leaving loses nothing', async () => {
      const element = setUp({ privacy: true });

      await publish([PRIVACY]);

      const link = element.querySelector('a') as HTMLAnchorElement;
      expect(link.hasAttribute('target')).toBe(false);
      expect(link.hasAttribute('rel')).toBe(false);
      expect(link.querySelector('.sr-only')).toBeNull();
      expect(link.textContent?.trim()).toBe('privacy policy');
    });
  });

  it('shares the one published-page request with everything else on the page', () => {
    // The service is a root singleton and replays its answer, so a page that already read the index (the shell's
    // footer does, on every page) costs this component no request of its own.
    TestBed.configureTestingModule({
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpTestingController);
    primeContentPages(http, [PRIVACY]);

    fixture = TestBed.createComponent(Host);
    Object.assign(fixture.componentInstance, { privacy: true });
    fixture.detectChanges();

    expect(http.match((request) => request.url === '/api/content/pages')).toEqual([]);
    expect(sentence(fixture.nativeElement as HTMLElement)).toBe('Details are in our privacy policy.');
  });
});
