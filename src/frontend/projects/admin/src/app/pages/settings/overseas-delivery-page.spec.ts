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
import { OverseasDeliveryPage } from './overseas-delivery-page';

function region(overrides: Partial<DeliveryRegionRow> = {}): DeliveryRegionRow {
  return {
    code: 'EUROPE',
    name: 'Europe',
    fee: 9500,
    estimatedDaysMin: 7,
    estimatedDaysMax: 14,
    expressFee: null,
    expressDaysMin: 3,
    expressDaysMax: 7,
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
    expressFeeOverride: null,
    expressDaysMinOverride: null,
    expressDaysMaxOverride: null,
    postalCodeRequired: true,
    regionRequired: false,
    isAvailable: true,
    effectiveFee: 9500,
    effectiveDaysMin: 7,
    effectiveDaysMax: 14,
    effectiveExpressFee: null,
    effectiveExpressDaysMin: null,
    effectiveExpressDaysMax: null,
    refusal: 'None',
    expressRefusal: 'ExpressNotPriced',
    rowVersion: 'AAAAAAAAB9F=',
    ...overrides,
  };
}

function screen(overrides: Partial<OverseasDeliveryScreen> = {}): OverseasDeliveryScreen {
  return {
    internationalEnabled: true,
    internationalRowVersion: 'AAAAAAAAB9A=',
    regions: [region(), region({ code: 'ASIA', name: 'Asia', fee: 6500, estimatedDaysMin: 5, estimatedDaysMax: 10, countryCount: 30, rowVersion: 'AAAAAAAAB9B=' })],
    countries: [
      country(),
      country({ code: 'JP', name: 'Japan', regionCode: 'ASIA', effectiveFee: 6500, effectiveDaysMin: 5, effectiveDaysMax: 10, rowVersion: 'AAAAAAAAB9C=' }),
      country({
        code: 'AQ',
        name: 'Antarctica',
        regionCode: 'ASIA',
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
 * The Overseas delivery screen (owner decision 2026-10-21). The request bodies are the heart of it: the stubs and
 * the real API both refuse anything looser than `OverseasDeliveryRules`, so what matters here is that the page
 * sends every field, the right token, and keeps the server's per-field sentences beside the boxes.
 */
describe('OverseasDeliveryPage', () => {
  let fixture: ComponentFixture<OverseasDeliveryPage>;
  let element: HTMLElement;
  let http: HttpTestingController;

  async function settle(): Promise<void> {
    fixture.detectChanges();
    await fixture.whenStable();
  }

  async function open(permissions: string[] | 'all' = 'all', payload = screen()): Promise<void> {
    fixture = TestBed.createComponent(OverseasDeliveryPage);
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

  function type(testId: string, value: string): void {
    const input = byTestId<HTMLInputElement>(testId)!;
    input.value = value;
    input.dispatchEvent(new Event('input'));
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

  it('shows each region, each country with what a shopper pays, and the reason a country is refused', async () => {
    await open();

    expect(byTestId('region-EUROPE')?.textContent).toContain('Europe');
    expect(byTestId('region-EUROPE')?.textContent).toContain('52 countries');
    expect(byTestId('country-effective-GB')?.textContent).toContain('9,500.00');
    expect(byTestId('country-effective-GB')?.textContent).toContain('7–14 days');
    expect(byTestId('country-effective-AQ')?.textContent).toContain('Switched off for this country');
    expect(byTestId('overseas-placeholder-note')?.textContent).toContain('placeholders');
  });

  it('filters the countries by name and by region', async () => {
    await open();

    const search = byTestId<HTMLInputElement>('country-search')!;
    search.value = 'jap';
    search.dispatchEvent(new Event('input'));
    await settle();
    expect(byTestId('country-JP')).not.toBeNull();
    expect(byTestId('country-GB')).toBeNull();

    search.value = '';
    search.dispatchEvent(new Event('input'));
    const select = byTestId<HTMLSelectElement>('country-region-filter')!;
    select.value = 'EUROPE';
    select.dispatchEvent(new Event('change'));
    await settle();
    expect(byTestId('country-GB')).not.toBeNull();
    expect(byTestId('country-JP')).toBeNull();
    expect(byTestId('country-count')?.textContent).toContain('1 of 3');
  });

  it('saves a region with every field and its row version, and redraws from the reply', async () => {
    await open();

    type('region-fee-EUROPE', '11000');
    type('region-min-EUROPE', '6');
    type('region-max-EUROPE', '12');
    byTestId('region-save-EUROPE')!.click();
    await answerAntiforgery();

    const request = http.expectOne(`${OverseasDeliveryApi.url}/regions/EUROPE`);
    expect(request.request.method).toBe('PUT');
    expect(request.request.body).toEqual({
      fee: 11000,
      estimatedDaysMin: 6,
      estimatedDaysMax: 12,
      expressFee: null,
      expressDaysMin: 3,
      expressDaysMax: 7,
      isEnabled: true,
      rowVersion: 'AAAAAAAAB9E=',
    });

    request.flush(screen({ regions: [region({ fee: 11000, estimatedDaysMin: 6, estimatedDaysMax: 12 })] }));
    await settle();

    expect(byTestId('overseas-said')?.textContent).toContain('Europe is saved');
    expect(byTestId<HTMLInputElement>('region-fee-EUROPE')!.value).toBe('11000');
  });

  it('sends a blank region fee as null — "not priced"', async () => {
    await open();

    type('region-fee-EUROPE', '');
    byTestId('region-save-EUROPE')!.click();
    await answerAntiforgery();

    const request = http.expectOne(`${OverseasDeliveryApi.url}/regions/EUROPE`);
    expect(request.request.body.fee).toBeNull();
    request.flush(screen());
    await settle();
  });

  it('refuses a fee with three decimals and days in the wrong order without a request', async () => {
    await open();

    type('region-fee-EUROPE', '10.555');
    type('region-min-EUROPE', '9');
    type('region-max-EUROPE', '3');
    byTestId('region-save-EUROPE')!.click();
    await settle();

    expect(byTestId('region-error-fee-EUROPE')?.textContent).toContain('two decimal places');
    expect(byTestId('region-error-days-EUROPE')?.textContent).toContain('fewest no more than the most');
  });

  it('keeps the server’s per-field sentence beside the box on a 400', async () => {
    await open();

    byTestId('region-save-EUROPE')!.click();
    await answerAntiforgery();

    http
      .expectOne(`${OverseasDeliveryApi.url}/regions/EUROPE`)
      .flush(
        { title: 'One or more validation errors occurred.', errors: { fee: ['Enter a fee from Rs. 0 to Rs. 1,000,000.'] } },
        { status: 400, statusText: 'Bad Request' },
      );
    await settle();

    expect(byTestId('region-error-fee-EUROPE')?.textContent).toContain('Rs. 1,000,000');
  });

  it('says the row changed and reloads on a 409', async () => {
    await open();

    byTestId('region-save-EUROPE')!.click();
    await answerAntiforgery();

    http
      .expectOne(`${OverseasDeliveryApi.url}/regions/EUROPE`)
      .flush({ detail: 'Somebody else changed this while you were editing it.', code: 'stale_row_version' }, { status: 409, statusText: 'Conflict' });
    await settle();

    http.expectOne(OverseasDeliveryApi.url).flush(screen());
    await settle();

    expect(byTestId('overseas-problem')?.textContent).toContain('reloaded');
  });

  it('overrides a country’s fee and estimate and sends blank overrides as null', async () => {
    await open();

    byTestId('country-edit-GB')!.click();
    await settle();
    type('country-fee-GB', '12500');
    type('country-min-GB', '4');
    type('country-max-GB', '8');
    byTestId('country-save-GB')!.click();
    await answerAntiforgery();

    const request = http.expectOne(`${OverseasDeliveryApi.url}/countries/GB`);
    expect(request.request.body).toEqual({
      isShippingEnabled: true,
      deliveryFeeOverride: 12500,
      estimatedDaysMinOverride: 4,
      estimatedDaysMaxOverride: 8,
      expressFeeOverride: null,
      expressDaysMinOverride: null,
      expressDaysMaxOverride: null,
      rowVersion: 'AAAAAAAAB9F=',
    });
    request.flush(screen());
    await settle();

    byTestId('country-edit-JP')!.click();
    await settle();
    const enabled = byTestId<HTMLInputElement>('country-enabled-JP')!;
    enabled.checked = false;
    enabled.dispatchEvent(new Event('change'));
    byTestId('country-save-JP')!.click();
    await answerAntiforgery();

    const second = http.expectOne(`${OverseasDeliveryApi.url}/countries/JP`);
    expect(second.request.body).toEqual({
      isShippingEnabled: false,
      deliveryFeeOverride: null,
      estimatedDaysMinOverride: null,
      estimatedDaysMaxOverride: null,
      expressFeeOverride: null,
      expressDaysMinOverride: null,
      expressDaysMaxOverride: null,
      rowVersion: 'AAAAAAAAB9C=',
    });
    second.flush(screen());
    await settle();
  });

  it('refuses one override day without the other, without a request', async () => {
    await open();

    byTestId('country-edit-GB')!.click();
    await settle();
    type('country-min-GB', '4');
    byTestId('country-save-GB')!.click();
    await settle();

    expect(byTestId('country-error-days-GB')?.textContent).toContain('or neither');
  });

  it('switches International delivery off with its token', async () => {
    await open();

    const box = byTestId<HTMLInputElement>('overseas-international-switch')!;
    box.checked = false;
    box.dispatchEvent(new Event('change'));
    await answerAntiforgery();

    const request = http.expectOne(`${OverseasDeliveryApi.url}/international`);
    expect(request.request.body).toEqual({ isEnabled: false, rowVersion: 'AAAAAAAAB9A=' });
    request.flush(screen({ internationalEnabled: false }));
    await settle();

    expect(element.textContent).toContain('checkout refuses every country but Sri Lanka');
  });

  it('shows the figures without any control to a role with Settings.View only', async () => {
    await open(['Settings.View']);

    expect(byTestId('region-fee-EUROPE')).toBeNull();
    expect(byTestId('country-edit-GB')).toBeNull();
    expect(byTestId('overseas-international-switch')).toBeNull();
    expect(byTestId('overseas-international-state')?.textContent).toContain('accepted');
    expect(byTestId('region-EUROPE')?.textContent).toContain('9,500.00');
  });

  it('explains a 403 rather than showing an empty page', async () => {
    fixture = TestBed.createComponent(OverseasDeliveryPage);
    element = fixture.nativeElement as HTMLElement;
    await settle();
    http.expectOne(AdminAuthApi.whoAmIUrl).flush({ grantsAllPermissions: false, permissions: [] });
    http.expectOne(OverseasDeliveryApi.url).flush(null, { status: 403, statusText: 'Forbidden' });
    await settle();

    expect(byTestId('overseas-problem')?.textContent).toContain('permission');
  });
});
