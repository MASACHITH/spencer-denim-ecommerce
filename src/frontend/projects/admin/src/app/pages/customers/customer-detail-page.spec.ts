import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';

import { CustomerDetailPage } from './customer-detail-page';
import { AdminCustomerApi } from '../../core/customers/admin-customer-api';
import {
  AdminCustomerDetail,
  AdminCustomerOrderPage,
} from '../../core/customers/admin-customer.model';

const ORDER = '44444444-4444-4444-8444-444444444444';

function customer(overrides: Partial<AdminCustomerDetail> = {}): AdminCustomerDetail {
  return {
    customerId: 41,
    firstName: 'Dilani',
    lastName: 'Wickramasinghe',
    email: 'dilani@example.com',
    emailConfirmed: true,
    phone: '+94761112223',
    districtName: 'Gampaha',
    isActive: true,
    registeredAtUtc: '2026-09-01T04:30:00Z',
    lastSignInAtUtc: '2026-10-10T04:30:00Z',
    orderCount: 2,
    lifetimeSpend: 9_800,
    currency: 'LKR',
    lastOrderAtUtc: '2026-10-05T04:30:00Z',
    addresses: [
      {
        addressId: 7,
        label: 'Home',
        recipientName: 'Dilani at home',
        phone: '+94771234567',
        line1: '14 Galle Road',
        line2: 'Flat 2',
        city: 'Kollupitiya',
        districtName: 'Gampaha',
        postalCode: '00300',
        landmark: 'the temple',
        isDefault: true,
      },
    ],
    ...overrides,
  };
}

function orders(overrides: Partial<AdminCustomerOrderPage> = {}): AdminCustomerOrderPage {
  return {
    rows: [
      {
        orderPublicId: ORDER,
        orderNumber: 'SD-2026-000123',
        status: 'Paid',
        statusLabel: 'Paid',
        grandTotal: 9_800,
        currency: 'LKR',
        placedAtUtc: '2026-10-05T04:30:00Z',
        paidAtUtc: '2026-10-05T09:00:00Z',
      },
    ],
    page: 1,
    pageSize: 10,
    totalCount: 1,
    ...overrides,
  };
}

describe('CustomerDetailPage (ADM-15)', () => {
  let fixture: ComponentFixture<CustomerDetailPage>;
  let element: HTMLElement;
  let http: HttpTestingController;

  async function settle(): Promise<void> {
    fixture.detectChanges();
    await fixture.whenStable();
  }

  function setUp(id: string): void {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: convertToParamMap({ customerId: id }) } } },
      ],
    });

    http = TestBed.inject(HttpTestingController);
  }

  async function open(person = customer(), history = orders()): Promise<void> {
    setUp('41');
    fixture = TestBed.createComponent(CustomerDetailPage);
    element = fixture.nativeElement as HTMLElement;
    document.body.appendChild(element);

    await settle();
    http.expectOne(`${AdminCustomerApi.url}/41`).flush(person);
    await settle();
    http.expectOne(`${AdminCustomerApi.url}/41/orders?page=1&pageSize=10`).flush(history);
    await settle();
  }

  function byTestId(testId: string): HTMLElement {
    return element.querySelector<HTMLElement>(`[data-testid="${testId}"]`)!;
  }

  afterEach(() => {
    http.verify();
    element?.remove();
  });

  it('shows the profile, the statistics and the address', async () => {
    await open();

    expect(byTestId('customer-name').textContent).toContain('Dilani Wickramasinghe');
    expect(byTestId('customer-email').textContent).toContain('dilani@example.com');
    expect(byTestId('customer-district').textContent).toContain('Gampaha');
    expect(byTestId('customer-order-count').textContent).toContain('2');
    expect(byTestId('customer-spend').textContent).toContain('Rs. 9,800.00');

    const address = byTestId('address-7').textContent ?? '';
    expect(address).toContain('Dilani at home');
    expect(address).toContain('Flat 2');
    expect(address).toContain('Kollupitiya, Gampaha');
    expect(address).toContain('00300');
    expect(address).toContain('Near the temple');
    expect(byTestId('address-default').textContent).toContain('Default');
  });

  it('links each order to the admin order screen rather than rebuilding it', async () => {
    await open();

    expect(byTestId('open-order-SD-2026-000123').getAttribute('href')).toBe(`/orders/${ORDER}`);
    expect(byTestId('customer-order-SD-2026-000123').textContent).toContain('Paid');
  });

  it('offers no delete, anonymise or edit action', async () => {
    await open();

    const buttons = Array.from(element.querySelectorAll('button')).map((button) => button.textContent ?? '');
    expect(buttons.join(' ')).not.toMatch(/delete|anonymi|edit|remove/i);
  });

  it('explains an unconfirmed address and an empty history', async () => {
    await open(customer({ emailConfirmed: false, addresses: [] }), orders({ rows: [], totalCount: 0 }));

    expect(byTestId('customer-unconfirmed').textContent).toContain('confirm');
    expect(byTestId('customer-no-addresses')).not.toBeNull();
    expect(byTestId('customer-no-orders')).not.toBeNull();
  });

  it('pages the history with aria-disabled and re-homes focus when the button is replaced', async () => {
    await open(customer(), orders({ totalCount: 15 }));

    const previous = byTestId('customer-orders-prev');
    expect(previous.getAttribute('aria-disabled')).toBe('true');
    expect(previous.hasAttribute('disabled')).toBe(false);
    previous.click();
    http.expectNone(() => true);

    // A whole page of new rows replaces the link that had focus.
    byTestId('open-order-SD-2026-000123').focus();
    byTestId('customer-orders-next').click();
    await settle();

    http
      .expectOne(`${AdminCustomerApi.url}/41/orders?page=2&pageSize=10`)
      .flush(
        orders({
          page: 2,
          totalCount: 15,
          rows: [{ ...orders().rows[0], orderPublicId: '55555555-5555-4555-8555-555555555555', orderNumber: 'SD-2026-000124' }],
        }),
      );
    await settle();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(byTestId('customer-orders-paging').textContent).toContain('11–15 of 15');
    expect(document.activeElement).toBe(byTestId('customer-orders-status'));
  });

  it('says there is no such customer on a 404, with no retry', async () => {
    setUp('99');
    fixture = TestBed.createComponent(CustomerDetailPage);
    element = fixture.nativeElement as HTMLElement;
    await settle();

    http.expectOne(`${AdminCustomerApi.url}/99`).flush({}, { status: 404, statusText: 'Not Found' });
    await settle();

    expect(byTestId('customer-problem').textContent).toContain('no such customer');
    expect(element.querySelector('[data-testid="customer-retry"]')).toBeNull();
    expect(element.querySelector('h1')).not.toBeNull();
  });

  it('asks nobody for an id that is not a number', async () => {
    setUp('abc');
    fixture = TestBed.createComponent(CustomerDetailPage);
    element = fixture.nativeElement as HTMLElement;
    await settle();

    http.expectNone(() => true);
    expect(byTestId('customer-problem').textContent).toContain('no such customer');
  });

  it('keeps headings in order: one h1, then h2s', async () => {
    await open();

    const headings = Array.from(element.querySelectorAll('h1, h2, h3')).map((heading) => heading.tagName);
    expect(headings).toEqual(['H1', 'H2', 'H2']);
  });
});
