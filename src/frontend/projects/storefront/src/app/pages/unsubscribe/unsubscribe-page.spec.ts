import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  TestRequest,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { RESPONSE_INIT } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';

import { UnsubscribePage } from './unsubscribe-page';

const TOKEN = '0f8fad5b-d9cb-469f-a165-70867728950e';

/**
 * The newsletter's unsubscribe link (#262, doc 11 P15 step 3).
 *
 * The properties this file exists for:
 * - **nothing happens on arrival** — a mail scanner that opens the link must not unsubscribe anyone; only a press does;
 * - **one answer for every token** — the page never says a link is invalid, because the API never does;
 * - **the token leaves the address bar** once used, and the page is `noindex` and `no-store`.
 */
describe('UnsubscribePage (#262)', () => {
  let harness: RouterTestingHarness;
  let fixture: ComponentFixture<unknown>;
  let element: HTMLElement;
  let http: HttpTestingController;
  let response: ResponseInit;

  async function settle(): Promise<void> {
    harness.detectChanges();
    await fixture.whenStable();
    harness.detectChanges();
  }

  async function open(url: string): Promise<void> {
    response = {};
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        provideRouter([
          { path: 'unsubscribe', component: UnsubscribePage },
          { path: 'contact', children: [] },
          { path: '', children: [] },
        ]),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: RESPONSE_INIT, useValue: response },
      ],
    });

    http = TestBed.inject(HttpTestingController);
    harness = await RouterTestingHarness.create(url);
    fixture = harness.fixture;
    element = harness.routeNativeElement as HTMLElement;
    await settle();
  }

  function text(): string {
    return (element.textContent ?? '').replace(/\s+/g, ' ').trim();
  }

  function button(): HTMLButtonElement | null {
    return element.querySelector<HTMLButtonElement>('[data-testid="unsubscribe-confirm"]');
  }

  function request(): TestRequest {
    return http.expectOne(
      (candidate) => candidate.url === '/api/content/newsletter/unsubscribe' && candidate.method === 'POST',
    );
  }

  afterEach(() => http.verify());

  it('sends nothing on arrival, and asks for one press', async () => {
    await open(`/unsubscribe?token=${TOKEN}`);

    http.expectNone('/api/content/newsletter/unsubscribe');
    expect(button()?.textContent?.trim()).toBe('Unsubscribe');
    expect(text()).toContain('Unsubscribe from our newsletter');
  });

  it('sends the token from the link on the press, says it is done and moves focus to the outcome', async () => {
    await open(`/unsubscribe?token=${TOKEN}`);

    button()!.click();
    await settle();

    const sent = request();
    expect(sent.request.body).toEqual({ token: TOKEN });
    expect(button()?.getAttribute('aria-disabled')).toBe('true');

    sent.flush(null, { status: 204, statusText: 'No Content' });
    await settle();

    expect(text()).toContain('You have been unsubscribed');
    expect(element.querySelector('[data-testid="unsubscribe-done"]')).not.toBeNull();
    expect(button()).toBeNull();
    expect(document.activeElement?.classList.contains('unsub__title')).toBe(true);
  });

  it('takes the token out of the address once it has been used', async () => {
    await open(`/unsubscribe?token=${TOKEN}`);

    button()!.click();
    await settle();
    request().flush(null, { status: 204, statusText: 'No Content' });
    await settle();

    expect(TestBed.inject(Router).url).toBe('/unsubscribe');
    // And the page stays on its outcome rather than reading the now-empty address as a broken link.
    expect(text()).toContain('You have been unsubscribed');
  });

  it('ignores a second press while the first is in flight', async () => {
    await open(`/unsubscribe?token=${TOKEN}`);

    button()!.click();
    await settle();
    button()!.click();
    await settle();

    request().flush(null, { status: 204, statusText: 'No Content' });
    await settle();
  });

  it('says the shop could not be reached, and lets the visitor press again', async () => {
    await open(`/unsubscribe?token=${TOKEN}`);

    button()!.click();
    await settle();
    request().error(new ProgressEvent('error'));
    await settle();

    expect(element.querySelector('[data-testid="unsubscribe-error"]')?.textContent).toContain(
      'We could not reach the shop just now.',
    );
    expect(button()?.getAttribute('aria-disabled')).toBeNull();

    button()!.click();
    await settle();
    request().flush(null, { status: 204, statusText: 'No Content' });
    await settle();

    expect(text()).toContain('You have been unsubscribed');
  });

  it('explains an incomplete link and offers the Contact page, sending nothing', async () => {
    await open('/unsubscribe');

    expect(button()).toBeNull();
    expect(element.querySelector('[data-testid="unsubscribe-missing"]')).not.toBeNull();
    expect(element.querySelector('a[href="/contact"]')).not.toBeNull();
  });

  it('is never cached by anything', async () => {
    await open(`/unsubscribe?token=${TOKEN}`);

    expect(new Headers(response.headers).get('Cache-Control')).toBe('no-store, no-cache, private');
  });

  it('asks not to be indexed', async () => {
    await open(`/unsubscribe?token=${TOKEN}`);

    expect(document.head.querySelector('meta[name="robots"]')?.getAttribute('content')).toContain('noindex');
  });
});
