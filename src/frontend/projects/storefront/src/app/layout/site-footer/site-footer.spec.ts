import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { SiteFooter } from './site-footer';

describe('SiteFooter (GLB-07)', () => {
  let fixture: ComponentFixture<SiteFooter>;
  let element: HTMLElement;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [SiteFooter],
      providers: [provideRouter([])],
    });

    fixture = TestBed.createComponent(SiteFooter);
    element = fixture.nativeElement as HTMLElement;
    fixture.detectChanges();
  });

  it('renders one contentinfo landmark with named columns', () => {
    expect(element.querySelectorAll('footer')).toHaveLength(1);

    const headings = Array.from(element.querySelectorAll('.site-footer__heading')).map((heading) =>
      heading.textContent?.trim(),
    );
    expect(headings).toEqual(['Shop', 'Help', 'About', 'Contact']);
  });

  it('links to the five shop destinations and to the policy pages', () => {
    const hrefs = Array.from(element.querySelectorAll<HTMLAnchorElement>('a')).map(
      (link) => link.getAttribute('href') ?? '',
    );

    expect(hrefs).toEqual(expect.arrayContaining(['/women', '/men', '/kids', '/new-arrivals', '/sale']));
    expect(hrefs).toEqual(expect.arrayContaining(['/returns', '/shipping', '/privacy', '/terms']));
  });

  it('gives every social icon a text name and opens it safely', () => {
    const social = Array.from(element.querySelectorAll<HTMLAnchorElement>('.site-footer__social a'));

    expect(social.length).toBeGreaterThan(0);
    social.forEach((link) => {
      expect(link.querySelector('.sr-only')?.textContent?.trim().length).toBeGreaterThan(0);
      expect(link.getAttribute('rel')).toContain('noopener');
    });
  });

  it('says nothing until the newsletter form is used', () => {
    const status = element.querySelector('[data-testid="newsletter-status"]');

    expect(status?.getAttribute('role')).toBe('status');
    expect(status?.textContent?.trim()).toBe('');
  });

  it('reports the newsletter as not yet available instead of faking success', () => {
    const form = element.querySelector('form') as HTMLFormElement;
    const email = element.querySelector('#newsletter-email') as HTMLInputElement;

    email.value = 'shopper@example.com';
    form.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
    fixture.detectChanges();

    const status = element.querySelector('[data-testid="newsletter-status"]')?.textContent ?? '';
    expect(fixture.componentInstance.newsletterState()).toBe('coming-soon');
    expect(status).toContain('not switched on yet');
    expect(status.toLowerCase()).not.toContain('thank you');
  });
});
