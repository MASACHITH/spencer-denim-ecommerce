import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component, PLATFORM_ID, REQUEST_CONTEXT, TransferState } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';

import { NOT_A_PAGE_VIEW, PAGE_VIEW_ROUTE, PageViewTracker, pathOf } from './page-view-tracker';
import { SERVER_PAGE_VIEW, TrafficRenderContext, markServerPageView } from './traffic-render-context';

@Component({ template: '' })
class Blank {}

/**
 * The browser half of "one visit, one row" (W4-03). The server half and the API are tested on their own
 * sides; this file proves the browser records exactly the views the server did not.
 */
describe('PageViewTracker (W4-03)', () => {
  let http: HttpTestingController;
  let router: Router;

  function create(options: { serverRecorded?: boolean; platform?: 'browser' | 'server' } = {}): void {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        provideRouter([
          { path: '', component: Blank },
          { path: 'women', component: Blank },
          { path: 'women/dresses', component: Blank },
          { path: 'product/:slug', component: Blank },
          { path: 'missing', component: Blank, data: NOT_A_PAGE_VIEW },
          { path: '**', component: Blank, data: NOT_A_PAGE_VIEW },
        ]),
        provideHttpClient(),
        provideHttpClientTesting(),
        ...(options.platform === 'server' ? [{ provide: PLATFORM_ID, useValue: 'server' }] : []),
      ],
    });

    if (options.serverRecorded) {
      TestBed.inject(TransferState).set(SERVER_PAGE_VIEW, true);
    }

    http = TestBed.inject(HttpTestingController);
    router = TestBed.inject(Router);
    TestBed.inject(PageViewTracker).start();
  }

  async function go(url: string): Promise<void> {
    await router.navigateByUrl(url);
  }

  function beacons(): { path: string; referrer: string | null; automated: boolean }[] {
    return http.match(PAGE_VIEW_ROUTE).map((request) => {
      expect(request.request.method).toBe('POST');
      const body = request.request.body as { path: string; referrer: string | null; automated: boolean };
      request.flush(null, { status: 204, statusText: 'No Content' });
      return body;
    });
  }

  afterEach(() => http.verify());

  it('does not record the first page when the server already recorded it, then records each click', async () => {
    create({ serverRecorded: true });

    await go('/women');
    expect(beacons()).toEqual([]);

    await go('/product/linen-shirt');
    await go('/women/dresses?sizes=M');
    expect(beacons().map((beacon) => beacon.path)).toEqual(['/product/linen-shirt', '/women/dresses?sizes=M']);
  });

  it('records the first page itself when the server did not — exactly once', async () => {
    create();

    await go('/women');
    const sent = beacons();

    expect(sent).toHaveLength(1);
    expect(sent[0].path).toBe('/women');
  });

  it('takes the server flag once, so it cannot suppress a later page', () => {
    create({ serverRecorded: true });

    expect(TestBed.inject(TransferState).hasKey(SERVER_PAGE_VIEW)).toBe(false);
  });

  it('does not record a filter, sort or Load More on the same listing as a new page view', async () => {
    create({ serverRecorded: true });

    await go('/women');
    await go('/women?sizes=M');
    await go('/women?sizes=M&sort=newest');
    await go('/women?sizes=M&sort=newest&pages=2');
    await go('/women#grid');

    expect(beacons()).toEqual([]);
  });

  it('records coming back to a page after leaving it', async () => {
    create({ serverRecorded: true });

    await go('/women');
    await go('/product/linen-shirt');
    await go('/women');

    expect(beacons().map((beacon) => beacon.path)).toEqual(['/product/linen-shirt', '/women']);
  });

  it('never records a 404 or 503 page', async () => {
    create();

    await go('/missing');
    await go('/no/such/page');

    expect(beacons()).toEqual([]);
  });

  it('records nothing on the server', async () => {
    create({ platform: 'server' });

    await go('/women');
    await go('/product/linen-shirt');

    expect(beacons()).toEqual([]);
  });

  it('sends only another site as the referrer, and only for a first page', async () => {
    vi.spyOn(document, 'referrer', 'get').mockReturnValue('https://www.google.com/search?q=dress');
    create();

    await go('/women');
    await go('/product/linen-shirt');

    expect(beacons().map((beacon) => beacon.referrer)).toEqual(['https://www.google.com/search?q=dress', null]);
  });

  it('does not send a referrer on this site', async () => {
    vi.spyOn(document, 'referrer', 'get').mockReturnValue(`${document.location.origin}/sale`);
    create();

    await go('/women');

    expect(beacons().map((beacon) => beacon.referrer)).toEqual([null]);
  });

  it('passes on navigator.webdriver so an automated browser is counted as a bot', async () => {
    // jsdom has no `webdriver` at all, so it is defined for this test and removed afterwards.
    Object.defineProperty(navigator, 'webdriver', { configurable: true, get: () => true });
    try {
      create();

      await go('/women');

      expect(beacons().map((beacon) => beacon.automated)).toEqual([true]);
    } finally {
      delete (navigator as { webdriver?: boolean }).webdriver;
    }
  });

  it('reports a browser that is not automated as not automated', async () => {
    create();

    await go('/women');

    expect(beacons().map((beacon) => beacon.automated)).toEqual([false]);
  });

  it('a failed beacon is swallowed: the next navigation still records', async () => {
    create({ serverRecorded: true });

    await go('/women');
    await go('/product/a');
    http.expectOne(PAGE_VIEW_ROUTE).flush(null, { status: 500, statusText: 'Server Error' });

    await go('/product/b');
    expect(beacons().map((beacon) => beacon.path)).toEqual(['/product/b']);
  });

  it('strips the query string and fragment for the same-page test', () => {
    expect(pathOf('/women?sizes=M#grid')).toBe('/women');
    expect(pathOf('/women#grid')).toBe('/women');
    expect(pathOf('/')).toBe('/');
  });
});

describe('markServerPageView (W4-03)', () => {
  function run(context: unknown): TransferState {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({ providers: [{ provide: REQUEST_CONTEXT, useValue: context }] });
    TestBed.runInInjectionContext(() => markServerPageView());
    return TestBed.inject(TransferState);
  }

  it('marks the render as run and leaves the flag when the server will record the page', () => {
    const context: TrafficRenderContext = { recordable: true, rendered: false };

    const state = run(context);

    expect(context.rendered).toBe(true);
    expect(state.get(SERVER_PAGE_VIEW, false)).toBe(true);
  });

  it('leaves no flag for a request the server will not record, so the browser records it', () => {
    const context: TrafficRenderContext = { recordable: false, rendered: false };

    const state = run(context);

    expect(context.rendered).toBe(true);
    expect(state.hasKey(SERVER_PAGE_VIEW)).toBe(false);
  });

  it('leaves no flag when nobody passed a context, so a page is never left unrecorded', () => {
    expect(run(null).hasKey(SERVER_PAGE_VIEW)).toBe(false);
    expect(run({ something: 'else' }).hasKey(SERVER_PAGE_VIEW)).toBe(false);
  });
});
