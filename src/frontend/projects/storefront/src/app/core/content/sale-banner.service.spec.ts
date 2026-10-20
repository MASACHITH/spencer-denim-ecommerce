import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { PLATFORM_ID, TransferState, makeStateKey } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';

import { SALE_BANNER_TIMEOUT_MS, SaleBannerDto, SaleBannerService, toBanner } from './sale-banner.service';

const BANNER: SaleBannerDto = {
  banner: {
    imageUrl: '/media/renditions/b/large.webp',
    imageSrcSet: '/media/renditions/b/card.webp 600w, /media/renditions/b/large.webp 1200w',
    width: 1600,
    height: 500,
    altText: 'Linen Week: 20% off shirts',
  },
};

/**
 * The Sale page's banner service (#191): the payload handed from the server render to the browser, and
 * every way a bad answer becomes "no banner" rather than a broken picture or a held-up page.
 */
describe('SaleBannerService (#191)', () => {
  function setUp(platform: 'browser' | 'server'): { service: SaleBannerService; http: HttpTestingController; state: TransferState } {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), { provide: PLATFORM_ID, useValue: platform }],
    });

    return {
      service: TestBed.inject(SaleBannerService),
      http: TestBed.inject(HttpTestingController),
      state: TestBed.inject(TransferState),
    };
  }

  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('reads the live banner from the content API', async () => {
    const { service, http } = setUp('browser');

    const banner = firstValueFrom(service.load());
    http.expectOne('/api/content/sale-banner').flush(BANNER);

    expect(await banner).toEqual(BANNER.banner);
  });

  it('hands the whole payload to the browser when it renders on the server, "no banner" included', async () => {
    const { service, http, state } = setUp('server');

    const banner = firstValueFrom(service.load());
    http.expectOne('/api/content/sale-banner').flush({ banner: null });

    expect(await banner).toBeNull();
    expect(state.get(makeStateKey<SaleBannerDto | null>('content:sale-banner'), null)).toEqual({ banner: null });
  });

  it('uses what the server handed over instead of asking again', async () => {
    const { service, http, state } = setUp('browser');
    state.set(makeStateKey<SaleBannerDto>('content:sale-banner'), BANNER);

    expect(await firstValueFrom(service.load())).toEqual(BANNER.banner);
    http.expectNone('/api/content/sale-banner');
  });

  it('is no banner when the API fails', async () => {
    const { service, http } = setUp('browser');

    const banner = firstValueFrom(service.load());
    http.expectOne('/api/content/sale-banner').flush(null, { status: 503, statusText: 'Unavailable' });

    expect(await banner).toBeNull();
  });

  it('gives up after its timeout rather than holding the page', async () => {
    vi.useFakeTimers();

    try {
      const { service, http } = setUp('browser');

      const banner = firstValueFrom(service.load());
      const request = http.expectOne('/api/content/sale-banner');

      vi.advanceTimersByTime(SALE_BANNER_TIMEOUT_MS + 1);

      expect(await banner).toBeNull();
      expect(request.cancelled).toBe(true);
    } finally {
      vi.useRealTimers();
    }
  });
});

describe('toBanner (#191)', () => {
  it('refuses a payload of the wrong shape', () => {
    expect(toBanner(null)).toBeNull();
    expect(toBanner({ banner: null })).toBeNull();
    expect(toBanner({ oops: true } as unknown as SaleBannerDto)).toBeNull();
    expect(toBanner({ banner: { ...BANNER.banner!, imageUrl: '  ' } })).toBeNull();
  });

  it('drops what an image tag cannot use and keeps an empty description empty', () => {
    expect(
      toBanner({
        banner: { imageUrl: '/b.jpg', imageSrcSet: '', width: 0, height: -1, altText: null as unknown as string },
      }),
    ).toEqual({ imageUrl: '/b.jpg', imageSrcSet: null, width: null, height: null, altText: '' });
  });

  it('trims the description', () => {
    expect(toBanner({ banner: { ...BANNER.banner!, altText: '  Linen Week  ' } })?.altText).toBe('Linen Week');
  });
});
