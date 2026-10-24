import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { AdminAuthApi } from '../../core/auth/admin-auth-api';
import { OverseasDeliveryApi } from '../../core/delivery/overseas-delivery-api';
import {
  CountryDeliveryRow,
  DeliveryRegionRow,
  OverseasDeliveryScreen,
} from '../../core/delivery/overseas-delivery.model';
import { BlockedCountriesPage } from './blocked-countries-page';

function region(overrides: Partial<DeliveryRegionRow> = {}): DeliveryRegionRow {
  return {
    code: 'EUROPE',
    name: 'Europe',
    fee: 9500,
    estimatedDaysMin: 7,
    estimatedDaysMax: 14,
    isEnabled: true,
    countryCount: 52,
    rowVersion: 'AAAAAAAAB9E=',
    ...overrides,
  };
}

function country(overrides: Partial<CountryDeliveryRow> = {}): CountryDeliveryRow {
  return {
    code: 'GB',
    name: 'United Kingdom',
    regionCode: 'EUROPE',
    isShippingEnabled: true,
    deliveryFeeOverride: null,
    estimatedDaysMinOverride: null,
    estimatedDaysMaxOverride: null,
    postalCodeRequired: true,
    regionRequired: false,
    isAvailable: true,
    effectiveFee: 9500,
    effectiveDaysMin: 7,
    effectiveDaysMax: 14,
    refusal: 'None',
    rowVersion: 'AAAAAAAAB9F=',
    ...overrides,
  };
}

/** 249-country list for the large-set test. */
function makeCountries(count: number): CountryDeliveryRow[] {
  return Array.from({ length: count }, (_, i) => {
    const code = `C${String(i).padStart(3, '0')}`;
    return country({
      code,
      name: `Country ${i}`,
      regionCode: i % 2 === 0 ? 'EUROPE' : 'ASIA',
      rowVersion: `version-${i}`,
    });
  });
}

function screen(overrides: Partial<OverseasDeliveryScreen> = {}): OverseasDeliveryScreen {
  return {
    internationalEnabled: true,
    internationalRowVersion: 'AAAAAAAAB9A=',
    regions: [
      region(),
      region({ code: 'ASIA', name: 'Asia', fee: 6500, estimatedDaysMin: 5, estimatedDaysMax: 10, countryCount: 30, rowVersion: 'AAAAAAAAB9B=' }),
    ],
    countries: [
      country(),
      country({ code: 'JP', name: 'Japan', regionCode: 'ASIA', effectiveFee: 6500, effectiveDaysMin: 5, effectiveDaysMax: 10, rowVersion: 'AAAAAAAAB9C=' }),
      country({
        code: 'US',
        name: 'United States',
        regionCode: 'EUROPE',
        isShippingEnabled: false,
        isAvailable: false,
        effectiveFee: null,
        effectiveDaysMin: null,
        effectiveDaysMax: null,
        refusal: 'CountryDisabled',
        rowVersion: 'AAAAAAAAB9D=',
      }),
    ],
    ...overrides,
  };
}

/**
 * Blocked countries page (#240). The heart of the tests: the request body on each toggle, the row
 * state during and after saving, and that search filters the full list without a round trip.
 */
describe('BlockedCountriesPage', () => {
  let fixture: ComponentFixture<BlockedCountriesPage>;
  let element: HTMLElement;
  let http: HttpTestingController;

  async function settle(): Promise<void> {
    fixture.detectChanges();
    await fixture.whenStable();
  }

  async function open(permissions: string[] | 'all' = 'all', payload = screen()): Promise<void> {
    fixture = TestBed.createComponent(BlockedCountriesPage);
    element = fixture.nativeElement as HTMLElement;
    document.body.appendChild(element);

    await settle();
    http.expectOne(AdminAuthApi.whoAmIUrl).flush({
      grantsAllPermissions: permissions === 'all',
      permissions: permissions === 'all' ? [] : permissions,
    });
    http.expectOne(OverseasDeliveryApi.url).flush(payload);
    await settle();
  }

  function byTestId<T extends HTMLElement = HTMLElement>(testId: string): T | null {
    return element.querySelector<T>(`[data-testid="${testId}"]`);
  }

  async function answerAntiforgery(): Promise<void> {
    await settle();
    http.expectOne(AdminAuthApi.antiforgeryUrl).flush({ token: 'token' });
    await settle();
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

  it('renders all countries with region labels and a "Blocked" badge on blocked ones', async () => {
    await open();

    expect(byTestId('bc-country-GB')).not.toBeNull();
    expect(byTestId('bc-country-JP')).not.toBeNull();
    expect(byTestId('bc-country-US')).not.toBeNull();
    expect(byTestId('bc-region-GB')?.textContent?.trim()).toBe('Europe');
    expect(byTestId('bc-blocked-badge-US')).not.toBeNull();
    expect(byTestId('bc-blocked-badge-GB')).toBeNull();
  });

  it('shows the blocked summary listing blocked country codes', async () => {
    await open();

    expect(byTestId('bc-blocked-summary')?.textContent).toContain('US');
    expect(byTestId('bc-blocked-summary')?.textContent).not.toContain('All countries are open');
  });

  it('shows "All countries are open" when no country is blocked', async () => {
    await open('all', screen({ countries: [country(), country({ code: 'JP', name: 'Japan', regionCode: 'ASIA', rowVersion: 'X=' })] }));

    expect(byTestId('bc-blocked-summary')?.textContent).toContain('All countries are open');
  });

  it('filters the country list by name in real time without a round trip', async () => {
    await open();

    const search = byTestId<HTMLInputElement>('bc-search')!;
    search.value = 'jap';
    search.dispatchEvent(new Event('input'));
    await settle();

    expect(byTestId('bc-country-JP')).not.toBeNull();
    expect(byTestId('bc-country-GB')).toBeNull();
    expect(byTestId('bc-country-US')).toBeNull();
  });

  it('filters by exact country code', async () => {
    await open();

    const search = byTestId<HTMLInputElement>('bc-search')!;
    search.value = 'gb';
    search.dispatchEvent(new Event('input'));
    await settle();

    expect(byTestId('bc-country-GB')).not.toBeNull();
    expect(byTestId('bc-country-JP')).toBeNull();
  });

  it('shows the count of matched countries', async () => {
    await open();

    const search = byTestId<HTMLInputElement>('bc-search')!;
    search.value = 'jap';
    search.dispatchEvent(new Event('input'));
    await settle();

    expect(byTestId('bc-count')?.textContent).toContain('1 of 3');
  });

  it('sends isShippingEnabled: false with the row version when blocking an open country', async () => {
    await open();

    const toggle = byTestId<HTMLInputElement>('bc-toggle-GB')!;
    toggle.checked = true;
    toggle.dispatchEvent(new Event('change'));
    await answerAntiforgery();

    const request = http.expectOne(`${OverseasDeliveryApi.url}/countries/GB`);
    expect(request.request.method).toBe('PUT');
    expect(request.request.body).toEqual({
      isShippingEnabled: false,
      deliveryFeeOverride: null,
      estimatedDaysMinOverride: null,
      estimatedDaysMaxOverride: null,
      rowVersion: 'AAAAAAAAB9F=',
    });
    request.flush(screen());
    await settle();
  });

  it('sends isShippingEnabled: true with the row version when unblocking a blocked country', async () => {
    await open();

    const toggle = byTestId<HTMLInputElement>('bc-toggle-US')!;
    toggle.checked = false;
    toggle.dispatchEvent(new Event('change'));
    await answerAntiforgery();

    const request = http.expectOne(`${OverseasDeliveryApi.url}/countries/US`);
    expect(request.request.method).toBe('PUT');
    expect(request.request.body).toEqual({
      isShippingEnabled: true,
      deliveryFeeOverride: null,
      estimatedDaysMinOverride: null,
      estimatedDaysMaxOverride: null,
      rowVersion: 'AAAAAAAAB9D=',
    });
    request.flush(screen());
    await settle();
  });

  it('updates local state from the server response after a toggle', async () => {
    await open();

    const toggle = byTestId<HTMLInputElement>('bc-toggle-GB')!;
    toggle.checked = true;
    toggle.dispatchEvent(new Event('change'));
    await answerAntiforgery();

    const blockedGB = country({ code: 'GB', name: 'United Kingdom', isShippingEnabled: false, isAvailable: false, effectiveFee: null, effectiveDaysMin: null, effectiveDaysMax: null, refusal: 'CountryDisabled', rowVersion: 'NEWVERSION=' });
    http.expectOne(`${OverseasDeliveryApi.url}/countries/GB`).flush(screen({ countries: [blockedGB, country({ code: 'JP', name: 'Japan', regionCode: 'ASIA', rowVersion: 'AAAAAAAAB9C=' }), country({ code: 'US', name: 'United States', isShippingEnabled: false, isAvailable: false, effectiveFee: null, effectiveDaysMin: null, effectiveDaysMax: null, refusal: 'CountryDisabled', rowVersion: 'AAAAAAAAB9D=' })] }));
    await settle();

    expect(byTestId('bc-blocked-badge-GB')).not.toBeNull();
    expect(byTestId('bc-said')?.textContent).toContain('United Kingdom is now blocked');
  });

  it('shows a spinner on the row while the save is in flight', async () => {
    await open();

    const toggle = byTestId<HTMLInputElement>('bc-toggle-GB')!;
    toggle.checked = true;
    toggle.dispatchEvent(new Event('change'));
    await settle();

    expect(byTestId('bc-spinner-GB')).not.toBeNull();

    http.expectOne(AdminAuthApi.antiforgeryUrl).flush({ token: 'token' });
    await settle();
    http.expectOne(`${OverseasDeliveryApi.url}/countries/GB`).flush(screen());
    await settle();

    expect(byTestId('bc-spinner-GB')).toBeNull();
  });

  it('shows a row-level error banner and hides the spinner after an API failure', async () => {
    await open();

    const toggle = byTestId<HTMLInputElement>('bc-toggle-GB')!;
    toggle.checked = true;
    toggle.dispatchEvent(new Event('change'));
    await answerAntiforgery();

    http.expectOne(`${OverseasDeliveryApi.url}/countries/GB`).flush(
      { detail: 'Something went wrong on the server.' },
      { status: 500, statusText: 'Internal Server Error' },
    );
    await settle();

    expect(byTestId('bc-error-GB')?.textContent).toContain('Something went wrong');
    expect(byTestId('bc-spinner-GB')).toBeNull();
  });

  it('reloads and shows a conflict message on a 409', async () => {
    await open();

    const toggle = byTestId<HTMLInputElement>('bc-toggle-GB')!;
    toggle.checked = true;
    toggle.dispatchEvent(new Event('change'));
    await answerAntiforgery();

    http.expectOne(`${OverseasDeliveryApi.url}/countries/GB`).flush(
      { detail: 'Somebody else changed this while you were editing it.', code: 'stale_row_version' },
      { status: 409, statusText: 'Conflict' },
    );
    await settle();

    http.expectOne(OverseasDeliveryApi.url).flush(screen());
    await settle();

    expect(byTestId('bc-problem')?.textContent).toContain('reloaded');
  });

  it('renders 249 countries without error', async () => {
    const countries = makeCountries(249);
    await open('all', screen({ countries }));

    const rows = element.querySelectorAll('[data-testid^="bc-country-"]');
    expect(rows.length).toBe(249);
    expect(byTestId('bc-count')?.textContent).toContain('249 of 249');
  });

  it('shows the list without toggle controls to a role with Settings.View only', async () => {
    await open(['Settings.View']);

    expect(byTestId('bc-toggle-GB')).toBeNull();
    expect(byTestId('bc-toggle-US')).toBeNull();
    expect(byTestId('bc-country-GB')).not.toBeNull();
    expect(byTestId('bc-country-US')).not.toBeNull();
  });

  it('explains a 403 rather than showing an empty page', async () => {
    fixture = TestBed.createComponent(BlockedCountriesPage);
    element = fixture.nativeElement as HTMLElement;
    await settle();
    http.expectOne(AdminAuthApi.whoAmIUrl).flush({ grantsAllPermissions: false, permissions: [] });
    http.expectOne(OverseasDeliveryApi.url).flush(null, { status: 403, statusText: 'Forbidden' });
    await settle();

    expect(byTestId('bc-problem')?.textContent).toContain('permission');
    expect(byTestId('bc-countries')).toBeNull();
  });
});
