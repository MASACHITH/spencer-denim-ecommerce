import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { CustomersListPage } from './customers-list-page';
import { AdminCustomerApi } from '../../core/customers/admin-customer-api';
import { AdminCustomerListPage, AdminCustomerListRow } from '../../core/customers/admin-customer.model';

/** Every field carries a real value, so a field that stops rendering is a failing assertion (W1-12). */
function row(overrides: Partial<AdminCustomerListRow> = {}): AdminCustomerListRow {
  return {
    customerId: 41,
    name: 'Nimali Perera',
    email: 'nimali@example.com',
    emailConfirmed: true,
    phone: '+94771234567',
    districtName: 'Kandy',
    isActive: true,
    registeredAtUtc: '2026-09-01T04:30:00Z',
    orderCount: 3,
    lifetimeSpend: 15_596.6,
    currency: 'LKR',
    lastOrderAtUtc: '2026-10-05T04:30:00Z',
    countryName: 'Sri Lanka',
    ...overrides,
  };
}

function page(overrides: Partial<AdminCustomerListPage> = {}): AdminCustomerListPage {
  return {
    rows: [row()],
    page: 1,
    pageSize: 25,
    totalCount: 1,
    districts: [
      { id: 1, name: 'Colombo' },
      { id: 4, name: 'Kandy' },
    ],
    countries: [
      { code: 'CA', name: 'Canada' },
      { code: 'LK', name: 'Sri Lanka' },
    ],
    ...overrides,
  };
}

/**
 * The admin customer list (ADM-15). What can be wrong on this side is what the client asks for and what
 * it shows; the server's own behaviour is `AdminCustomerEndpointTests` against SQL Server (#126).
 */
describe('CustomersListPage (ADM-15)', () => {
  let fixture: ComponentFixture<CustomersListPage>;
  let element: HTMLElement;
  let http: HttpTestingController;

  async function settle(): Promise<void> {
    fixture.detectChanges();
    await fixture.whenStable();
  }

  async function open(payload: AdminCustomerListPage = page()): Promise<void> {
    fixture = TestBed.createComponent(CustomersListPage);
    element = fixture.nativeElement as HTMLElement;
    document.body.appendChild(element);

    await settle();
    http.expectOne(`${AdminCustomerApi.url}?page=1&pageSize=25`).flush(payload);
    await settle();
  }

  function byTestId<T extends HTMLElement = HTMLElement>(testId: string): T {
    return element.querySelector<T>(`[data-testid="${testId}"]`)!;
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

  it('shows the name, address, district, order count, spend and last order of a row', async () => {
    await open();

    const text = element.textContent ?? '';
    expect(text).toContain('Nimali Perera');
    expect(text).toContain('nimali@example.com');
    expect(text).toContain('+94771234567');
    expect(byTestId('district-41').textContent).toContain('Kandy');
    expect(byTestId('orders-41').textContent).toContain('3');
    expect(byTestId('spend-41').textContent).toContain('Rs. 15,596.60');
    expect(byTestId('open-customer-41').getAttribute('href')).toBe('/customers/41');

    // Confirmed and active, so neither flag shows.
    expect(element.querySelector('[data-testid="unconfirmed-41"]')).toBeNull();
  });

  it('says what an absent district, telephone and order mean rather than leaving gaps', async () => {
    await open(
      page({ rows: [row({ districtName: null, phone: null, orderCount: 0, lifetimeSpend: 0, lastOrderAtUtc: null })] }),
    );

    expect(byTestId('district-41').textContent).toContain('Not chosen');
    expect(element.textContent).toContain('Not given');
    expect(element.textContent).toContain('None yet');
  });

  it('flags an unconfirmed address, because it explains missing guest orders', async () => {
    await open(page({ rows: [row({ emailConfirmed: false })] }));

    expect(byTestId('unconfirmed-41').textContent).toContain('Email not confirmed');
  });

  it('offers the districts the server sent, and filters by one at once', async () => {
    await open();

    const select = byTestId<HTMLSelectElement>('customer-district');
    expect(Array.from(select.options).map((option) => option.textContent?.trim())).toEqual([
      'Every district',
      'Colombo',
      'Kandy',
    ]);

    select.value = '4';
    select.dispatchEvent(new Event('change'));
    await settle();

    http.expectOne(`${AdminCustomerApi.url}?page=1&pageSize=25&districtId=4`).flush(page());
    await settle();

    expect(byTestId('customers-status').textContent).toContain('1 customer');
  });

  /** UAT feedback (2026-10-24): the district filter only knows Sri Lankan districts. */
  it('offers the countries the server sent, and filters by one at once', async () => {
    await open();

    const select = byTestId<HTMLSelectElement>('customer-country');
    expect(Array.from(select.options).map((option) => option.textContent?.trim())).toEqual([
      'Every country',
      'Canada',
      'Sri Lanka',
    ]);

    select.value = 'CA';
    select.dispatchEvent(new Event('change'));
    await settle();

    http.expectOne(`${AdminCustomerApi.url}?page=1&pageSize=25&countryCode=CA`).flush(page({ rows: [row({ countryName: 'Canada', districtName: null })] }));
    await settle();

    expect(byTestId('customers-status').textContent).toContain('1 customer');
    expect(byTestId('country-41').textContent).toContain('Canada');
  });

  it('keeps a chosen country in the box even if a later answer no longer lists it', async () => {
    await open();

    const select = byTestId<HTMLSelectElement>('customer-country');
    select.value = 'CA';
    select.dispatchEvent(new Event('change'));
    await settle();

    // The last customer in Canada has gone: the server's list no longer has it.
    http.expectOne(`${AdminCustomerApi.url}?page=1&pageSize=25&countryCode=CA`).flush(
      page({ rows: [], totalCount: 0, countries: [{ code: 'LK', name: 'Sri Lanka' }] }),
    );
    await settle();

    // Still the chosen one, not "Every country" while CA is still being sent.
    expect(byTestId<HTMLSelectElement>('customer-country').value).toBe('CA');
  });

  it('hides the district filter, and drops a district already chosen, while another country is chosen', async () => {
    await open();

    const district = byTestId<HTMLSelectElement>('customer-district');
    district.value = '4';
    district.dispatchEvent(new Event('change'));
    await settle();
    http.expectOne(`${AdminCustomerApi.url}?page=1&pageSize=25&districtId=4`).flush(page());
    await settle();

    const country = byTestId<HTMLSelectElement>('customer-country');
    country.value = 'CA';
    country.dispatchEvent(new Event('change'));
    await settle();

    // The district is not sent any more: it would filter unseen.
    http.expectOne(`${AdminCustomerApi.url}?page=1&pageSize=25&countryCode=CA`).flush(page({ rows: [] , totalCount: 0 }));
    await settle();

    expect(element.querySelector('[data-testid="customer-district"]')).toBeNull();
    expect(byTestId('customer-district-note').textContent).toContain('Sri Lankan');
    expect(byTestId('customers-empty').textContent).toContain('default saved address is in Canada');

    // Sri Lanka brings the district back.
    country.value = 'LK';
    country.dispatchEvent(new Event('change'));
    await settle();
    http.expectOne(`${AdminCustomerApi.url}?page=1&pageSize=25&countryCode=LK`).flush(page());
    await settle();

    expect(element.querySelector('[data-testid="customer-district"]')).not.toBeNull();
  });

  it('puts the country back to every country when Clear is pressed', async () => {
    await open();

    const country = byTestId<HTMLSelectElement>('customer-country');
    country.value = 'CA';
    country.dispatchEvent(new Event('change'));
    await settle();
    http.expectOne(`${AdminCustomerApi.url}?page=1&pageSize=25&countryCode=CA`).flush(page());
    await settle();

    byTestId<HTMLButtonElement>('customer-search-clear').click();
    await settle();
    http.expectOne(`${AdminCustomerApi.url}?page=1&pageSize=25`).flush(page());
    await settle();

    expect(byTestId<HTMLSelectElement>('customer-country').value).toBe('');
  });

  it('sends the search only when Search is pressed', async () => {
    await open();

    const input = byTestId<HTMLInputElement>('customer-search');
    input.value = '0771234567';
    input.dispatchEvent(new Event('input'));

    http.expectNone(() => true);

    byTestId('customer-search-submit').click();
    await settle();

    http.expectOne(`${AdminCustomerApi.url}?page=1&pageSize=25&search=0771234567`).flush(
      page({ rows: [], totalCount: 0 }),
    );
    await settle();

    // A search that finds nobody says where a guest is found instead.
    const empty = byTestId('customers-empty').textContent ?? '';
    expect(empty).toContain('0771234567');
    expect(empty).toContain('Orders screen');
  });

  it('refuses a page move past the end rather than disabling the button', async () => {
    await open();

    const next = byTestId('customers-next');
    expect(next.getAttribute('aria-disabled')).toBe('true');
    expect(next.hasAttribute('disabled')).toBe(false);

    next.click();
    await settle();

    http.expectNone(() => true);
  });

  it('pages forward and puts focus somewhere real when the button it was on is replaced', async () => {
    await open(page({ totalCount: 60 }));

    expect(byTestId('customers-paging').textContent).toContain('1–25 of 60');

    const next = byTestId('customers-next');
    next.focus();
    next.click();
    await settle();

    http.expectOne(`${AdminCustomerApi.url}?page=2&pageSize=25`).flush(page({ page: 2, totalCount: 60 }));
    await settle();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(byTestId('customers-paging').textContent).toContain('26–50 of 60');

    // The list (and the Next button with it) was replaced by the loading line, so focus would have
    // fallen to <body>; it is re-homed on the live region instead (#109).
    expect(document.activeElement).toBe(byTestId('customers-status'));
  });

  it('shows the permission refusal without a retry button', async () => {
    fixture = TestBed.createComponent(CustomersListPage);
    element = fixture.nativeElement as HTMLElement;
    await settle();

    http
      .expectOne(`${AdminCustomerApi.url}?page=1&pageSize=25`)
      .flush({ title: 'Forbidden' }, { status: 403, statusText: 'Forbidden' });
    await settle();

    expect(element.querySelector('[role="alert"]')?.textContent).toContain('permission');
    expect(element.querySelector('[data-testid="customers-retry"]')).toBeNull();
  });

  it('has one h1 and no h2 above it', async () => {
    await open();

    const headings = Array.from(element.querySelectorAll('h1, h2, h3')).map((heading) => heading.tagName);
    expect(headings[0]).toBe('H1');
    expect(headings.filter((tag) => tag === 'H1')).toHaveLength(1);
  });

  it('shows the country of the default address, and says so when there is none', async () => {
    await open(
      page({
        rows: [row(), row({ customerId: 42, countryName: 'Australia' }), row({ customerId: 43, countryName: null })],
        totalCount: 3,
      }),
    );

    expect(element.querySelector('[data-testid="country-42"]')?.textContent).toContain('Australia');
    expect(element.querySelector('[data-testid="country-43"]')?.textContent).toContain('No saved address');
  });
});
