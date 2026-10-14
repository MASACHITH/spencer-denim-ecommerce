import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { NewsletterSubscribersPage } from './newsletter-subscribers-page';
import { NewsletterApi, fileNameFrom } from '../../core/newsletter/newsletter-api';
import { NewsletterSubscriberPage } from '../../core/newsletter/newsletter.model';

const ANTIFORGERY = '/api/admin/auth/antiforgery-token';

function page(overrides: Partial<NewsletterSubscriberPage> = {}): NewsletterSubscriberPage {
  return {
    rows: [
      {
        subscriberId: 3,
        email: 'nimali@example.com',
        status: 'Subscribed',
        source: 'footer',
        consentAtUtc: '2026-10-01T04:30:00Z',
        unsubscribedAtUtc: null,
      },
      {
        subscriberId: 4,
        email: 'kasun@example.com',
        status: 'Unsubscribed',
        source: 'homepage',
        consentAtUtc: '2026-09-01T04:30:00Z',
        unsubscribedAtUtc: '2026-09-20T04:30:00Z',
      },
    ],
    page: 1,
    pageSize: 25,
    totalCount: 2,
    subscribedCount: 120,
    unsubscribedCount: 7,
    ...overrides,
  };
}

describe('NewsletterSubscribersPage (ADM-21)', () => {
  let fixture: ComponentFixture<NewsletterSubscribersPage>;
  let element: HTMLElement;
  let http: HttpTestingController;

  async function settle(): Promise<void> {
    fixture.detectChanges();
    await fixture.whenStable();
  }

  async function open(payload = page()): Promise<void> {
    fixture = TestBed.createComponent(NewsletterSubscribersPage);
    element = fixture.nativeElement as HTMLElement;
    document.body.appendChild(element);

    await settle();
    http.expectOne(`${NewsletterApi.url}?page=1&pageSize=25`).flush(payload);
    await settle();
  }

  function byTestId<T extends HTMLElement = HTMLElement>(testId: string): T {
    return element.querySelector<T>(`[data-testid="${testId}"]`)!;
  }

  function set(testId: string, value: string, event: 'input' | 'change'): void {
    const control = byTestId<HTMLInputElement | HTMLSelectElement>(testId);
    control.value = value;
    control.dispatchEvent(new Event(event));
  }

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    });

    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    http.verify();
    element?.remove();
  });

  it('shows each subscriber, their status and the whole-list counts', async () => {
    await open();

    expect(byTestId('newsletter-counts').textContent).toContain('120 subscribed · 7 unsubscribed');
    expect(byTestId('subscriber-3').textContent).toContain('nimali@example.com');
    expect(byTestId('subscriber-3').textContent).toContain('Footer');
    expect(byTestId('subscriber-4').textContent).toContain('Unsubscribed');
    expect(byTestId('subscriber-4').textContent).toContain('Homepage');
  });

  it('sends the status and the Colombo dates only when Apply is pressed', async () => {
    await open();

    set('newsletter-search', 'gmail', 'input');
    set('newsletter-status', 'Unsubscribed', 'change');
    set('newsletter-from', '2026-10-01', 'change');
    set('newsletter-to', '2026-10-31', 'change');
    http.expectNone(() => true);

    byTestId('newsletter-apply').click();
    await settle();

    http
      .expectOne(
        `${NewsletterApi.url}?search=gmail&from=2026-10-01&to=2026-10-31&page=1&pageSize=25&status=Unsubscribed`,
      )
      .flush(page());
  });

  it('refuses a to-date before the from-date without losing the list', async () => {
    await open();

    set('newsletter-from', '2026-10-02', 'change');
    set('newsletter-to', '2026-10-01', 'change');
    byTestId('newsletter-apply').click();
    await settle();

    http.expectNone(() => true);
    expect(byTestId('newsletter-date-error').textContent).toContain('before');
    expect(byTestId('subscriber-3')).not.toBeNull();
  });

  /**
   * **The export never sends a status**, even when the list is filtered to Unsubscribed: the file is
   * subscribed addresses only, and the server does not read one. It does send the dates and the search, and
   * the antiforgery token, because the server audits every export.
   */
  it('exports with the dates and search applied, the token, and no status', async () => {
    await open();

    set('newsletter-search', 'gmail', 'input');
    set('newsletter-status', 'Unsubscribed', 'change');
    set('newsletter-from', '2026-10-01', 'change');
    byTestId('newsletter-apply').click();
    await settle();
    http.expectOne(() => true).flush(page());
    await settle();

    byTestId('newsletter-export').click();
    await settle();

    http.expectOne(ANTIFORGERY).flush({ token: 'the-token' });

    const request = http.expectOne((candidate) => candidate.url === `${NewsletterApi.url}/export`);
    expect(request.request.method).toBe('POST');
    expect(request.request.headers.get('X-XSRF-TOKEN')).toBe('the-token');
    expect(request.request.params.get('search')).toBe('gmail');
    expect(request.request.params.get('from')).toBe('2026-10-01');
    expect(request.request.params.has('status')).toBe(false);

    request.flush(new Blob(['"Email"\r\n']), {
      headers: { 'Content-Disposition': 'attachment; filename=newsletter-subscribers-2026-10-14.csv' },
    });
    await settle();

    expect(byTestId('newsletter-status-message').textContent).toContain(
      'newsletter-subscribers-2026-10-14.csv',
    );
    expect(byTestId('newsletter-status-message').textContent).toContain('subscribed addresses only');
  });

  it('refuses a second export while one is being prepared, without disabling the button', async () => {
    await open();

    const button = byTestId('newsletter-export');
    button.focus();
    button.click();
    await settle();

    expect(button.getAttribute('aria-disabled')).toBe('true');
    expect(button.hasAttribute('disabled')).toBe(false);
    expect(document.activeElement).toBe(button);

    button.click();
    await settle();

    // Only the one token request went out.
    http.expectOne(ANTIFORGERY).flush({ token: 't' });
    http.expectOne((candidate) => candidate.url === `${NewsletterApi.url}/export`).flush(new Blob([]));
    await settle();

    expect(button.getAttribute('aria-disabled')).toBeNull();
  });

  it('says so in the live region when the export is refused', async () => {
    await open();

    byTestId('newsletter-export').click();
    await settle();

    http.expectOne(ANTIFORGERY).flush({ token: 't' });
    http
      .expectOne((candidate) => candidate.url === `${NewsletterApi.url}/export`)
      .flush(new Blob([]), { status: 403, statusText: 'Forbidden' });
    await settle();

    expect(byTestId('newsletter-status-message').textContent).toContain('permission');
  });

  it('tells the staff member the export is subscribed-only and audited before they press it', async () => {
    await open();

    const hint = element.querySelector('#newsletter-export-hint')?.textContent ?? '';
    expect(hint).toContain('subscribed');
    expect(hint).toContain('audit log');
    expect(byTestId('newsletter-export').getAttribute('aria-describedby')).toBe('newsletter-export-hint');
  });

  it('reads the file name from Content-Disposition, with a fallback', () => {
    expect(fileNameFrom('attachment; filename=a.csv; filename*=UTF-8\'\'a.csv')).toBe('a.csv');
    expect(fileNameFrom('attachment; filename="b.csv"')).toBe('b.csv');
    expect(fileNameFrom(null)).toBe('newsletter-subscribers.csv');
  });

  it('keeps headings in order: one h1, then h2s', async () => {
    await open();

    const headings = Array.from(element.querySelectorAll('h1, h2, h3')).map((heading) => heading.tagName);
    expect(headings).toEqual(['H1', 'H2']);
  });
});
