import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { PLATFORM_ID, TransferState, makeStateKey } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';

import { DeliveryReturnsDto, formatWorkingDays } from './delivery-returns.model';
import { PANEL } from './delivery-returns.testing';
import { DELIVERY_RETURNS_TIMEOUT_MS, DeliveryReturnsService, toDeliveryReturns } from './delivery-returns.service';

/**
 * The panel's feed (issue #33): read once, handed from the server render to the browser, and every bad answer —
 * a timeout, a 5xx, a payload of the wrong shape — turned into "no panel" rather than a broken one.
 */
describe('DeliveryReturnsService (#33)', () => {
  function setUp(platform: 'browser' | 'server') {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), { provide: PLATFORM_ID, useValue: platform }],
    });

    return {
      service: TestBed.inject(DeliveryReturnsService),
      http: TestBed.inject(HttpTestingController),
      state: TestBed.inject(TransferState),
    };
  }

  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('reads the panel once and shares it with every caller', async () => {
    const { service, http } = setUp('browser');

    const first = firstValueFrom(service.load());
    const second = firstValueFrom(service.load());
    http.expectOne('/api/content/delivery-returns').flush(PANEL);

    expect(await first).toEqual(PANEL);
    expect(await second).toEqual(PANEL);
  });

  it('hands the panel to the browser when it renders on the server', async () => {
    const { service, http, state } = setUp('server');

    const panel = firstValueFrom(service.load());
    http.expectOne('/api/content/delivery-returns').flush(PANEL);
    await panel;

    expect(state.get(makeStateKey<DeliveryReturnsDto | null>('content:delivery-returns'), null)).toEqual(PANEL);
  });

  it('uses what the server handed over instead of asking again', async () => {
    const { service, state } = setUp('browser');
    state.set(makeStateKey<DeliveryReturnsDto>('content:delivery-returns'), PANEL);

    expect(await firstValueFrom(service.load())).toEqual(PANEL);
  });

  it('is null on a server error, and hands nothing over', async () => {
    const { service, http, state } = setUp('server');

    const panel = firstValueFrom(service.load());
    http.expectOne('/api/content/delivery-returns').flush({}, { status: 500, statusText: 'Server Error' });

    expect(await panel).toBeNull();
    expect(state.get(makeStateKey<DeliveryReturnsDto | null>('content:delivery-returns'), null)).toBeNull();
  });

  it('is null when the answer never comes', async () => {
    vi.useFakeTimers();
    try {
      const { service, http } = setUp('browser');

      const panel = firstValueFrom(service.load());
      http.expectOne('/api/content/delivery-returns');
      vi.advanceTimersByTime(DELIVERY_RETURNS_TIMEOUT_MS + 1);

      expect(await panel).toBeNull();
    } finally {
      vi.useRealTimers();
    }
  });
});

// Tester (#33): a handover that crossed the process boundary in the wrong shape is "no panel", never a throw.
describe('DeliveryReturnsService handover (#33, tester)', () => {
  it('is null, not an error, when the handed-over panel has no zones', async () => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), { provide: PLATFORM_ID, useValue: 'browser' }],
    });
    TestBed.inject(TransferState).set(makeStateKey<unknown>('content:delivery-returns'), { methods: [] });

    expect(await firstValueFrom(TestBed.inject(DeliveryReturnsService).load())).toBeNull();
  });
});

describe('toDeliveryReturns', () => {
  it('refuses a payload that is not the panel', () => {
    expect(toDeliveryReturns(null)).toBeNull();
    expect(toDeliveryReturns('x')).toBeNull();
    expect(toDeliveryReturns({ oops: true })).toBeNull();
    expect(toDeliveryReturns({ zones: [], methods: {} })).toBeNull();
  });

  it('drops a malformed rate, zone, method or pickup point rather than drawing it', () => {
    const vetted = toDeliveryReturns({
      estimate: { daysMin: 'one', daysMax: 2 },
      zones: [{ zone: 'Colombo', label: ' Colombo ' }, { zone: 'Mars', label: 'Mars' }, { zone: 'Western', label: '' }],
      methods: [
        {
          code: 'STANDARD',
          name: 'Standard',
          rates: [
            { zone: 'Colombo', fee: 400, freeAboveAmount: 'lots', daysMin: 1, daysMax: 2 },
            { zone: 'Colombo', fee: -1, daysMin: 1, daysMax: 2 },
            { zone: 'Colombo', fee: Number.NaN, daysMin: 1, daysMax: 2 },
            { zone: 'Colombo', fee: 400, daysMin: 1.5, daysMax: 2 },
          ],
        },
        { code: 'EMPTY', name: 'No rates', rates: [{ zone: 'Nowhere', fee: 1, daysMin: 1, daysMax: 1 }] },
        { code: '', name: 'No code', rates: [] },
      ],
      pickupPoints: [
        { name: 'Store', city: 'Colombo', openingHours: '  ', fee: 0, daysMin: 1, daysMax: 2 },
        { name: '', city: 'Colombo', fee: 0, daysMin: 1, daysMax: 2 },
      ],
      deliveryInfoHtml: 42,
    });

    expect(vetted).toEqual({
      estimate: null,
      zones: [{ zone: 'Colombo', label: 'Colombo', districts: [] }],
      methods: [
        {
          code: 'STANDARD',
          name: 'Standard',
          description: null,
          rates: [{ zone: 'Colombo', fee: 400, freeAboveAmount: null, daysMin: 1, daysMax: 2 }],
        },
      ],
      pickupPoints: [{ name: 'Store', city: 'Colombo', openingHours: null, fee: 0, daysMin: 1, daysMax: 2 }],
      deliveryInfoHtml: '',
      returnsInfoHtml: '',
    });
  });

  it('keeps the district names of a zone, trimmed, and drops any that are not text', () => {
    const vetted = toDeliveryReturns({
      zones: [{ zone: 'Western', label: 'Gampaha and Kalutara', districts: [' Gampaha ', 7, '', 'Kalutara'] }],
      methods: [],
    });

    expect(vetted?.zones).toEqual([{ zone: 'Western', label: 'Gampaha and Kalutara', districts: ['Gampaha', 'Kalutara'] }]);
  });

  it('keeps a well-formed panel as it is', () => {
    expect(toDeliveryReturns(PANEL)).toEqual(PANEL);
  });
});

describe('formatWorkingDays', () => {
  it('says one day, a range, or a plural', () => {
    expect(formatWorkingDays(1, 1)).toBe('1 working day');
    expect(formatWorkingDays(2, 2)).toBe('2 working days');
    expect(formatWorkingDays(1, 2)).toBe('1–2 working days');
  });
});
