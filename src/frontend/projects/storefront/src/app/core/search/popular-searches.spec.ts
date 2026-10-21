import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { PLATFORM_ID } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import {
  POPULAR_SEARCHES_LIMIT,
  POPULAR_SEARCHES_ROUTE,
  PopularSearches,
  SEARCH_SUBMISSIONS_ROUTE,
  SearchSubmissions,
  vetPopular,
} from './popular-searches';

describe('popular searches (#78)', () => {
  let http: HttpTestingController | null = null;

  function setUp(platform: 'browser' | 'server' = 'browser'): HttpTestingController {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), { provide: PLATFORM_ID, useValue: platform }],
    });

    http = TestBed.inject(HttpTestingController);
    return http;
  }

  afterEach(() => {
    http?.verify();
    http = null;
  });

  describe('vetPopular', () => {
    it('keeps the terms in order', () => {
      expect(vetPopular({ terms: ['jeans', 'linen shirt'] })).toEqual(['jeans', 'linen shirt']);
    });

    it.each([null, undefined, 'jeans', 42, {}, { terms: 'jeans' }, { terms: null }])(
      'answers nothing for a response of the wrong shape: %p',
      (response) => {
        expect(vetPopular(response)).toEqual([]);
      },
    );

    it('drops what is not a usable, shareable term, and duplicates', () => {
      expect(
        vetPopular({
          terms: [
            'jeans',
            42,
            null,
            'a',
            'someone@example.com',
            '077 123 4567',
            'x'.repeat(41),
            'JEANS',
            '  linen   shirt ',
          ],
        }),
      ).toEqual(['jeans', 'linen shirt']);
    });

    it('never holds more than the API sends at most', () => {
      const terms = Array.from({ length: 20 }, (_, index) => `term ${index}`);
      expect(vetPopular({ terms })).toHaveLength(POPULAR_SEARCHES_LIMIT);
    });
  });

  describe('PopularSearches', () => {
    it('asks once per page load however often it is told to', () => {
      const http = setUp();
      const popular = TestBed.inject(PopularSearches);

      popular.load();
      popular.load();
      http.expectOne(POPULAR_SEARCHES_ROUTE).flush({ terms: ['linen'] });
      popular.load();

      expect(popular.terms()).toEqual(['linen']);
    });

    it('is an empty list when the request fails', () => {
      const http = setUp();
      const popular = TestBed.inject(PopularSearches);

      popular.load();
      http.expectOne(POPULAR_SEARCHES_ROUTE).flush('down', { status: 503, statusText: 'Unavailable' });

      expect(popular.terms()).toEqual([]);
    });

    it('asks for nothing on the server', () => {
      setUp('server');
      const popular = TestBed.inject(PopularSearches);

      popular.load();

      expect(popular.terms()).toEqual([]);
    });
  });

  describe('SearchSubmissions', () => {
    it('sends the normalised term and the automation flag, and nothing else', () => {
      const http = setUp();

      TestBed.inject(SearchSubmissions).record('  Linen   Shirt ');

      const request = http.expectOne(SEARCH_SUBMISSIONS_ROUTE);
      expect(request.request.method).toBe('POST');
      expect(request.request.body).toEqual({ term: 'Linen Shirt', automated: false });
      request.flush(null, { status: 204, statusText: 'No Content' });
    });

    it.each(['someone@example.com', '077 123 4567', 'a', '', 'a b'])(
      'never puts %p on the wire',
      (term) => {
        setUp();
        TestBed.inject(SearchSubmissions).record(term);
      },
    );

    it('swallows a failure', () => {
      const http = setUp();

      TestBed.inject(SearchSubmissions).record('jeans');

      expect(() =>
        http.expectOne(SEARCH_SUBMISSIONS_ROUTE).flush('no', { status: 500, statusText: 'Error' }),
      ).not.toThrow();
    });

    it('sends nothing from the server', () => {
      setUp('server');
      TestBed.inject(SearchSubmissions).record('jeans');
    });
  });
});
