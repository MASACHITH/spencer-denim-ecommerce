import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { OrdersListPage } from './orders-list-page';
import { AdminOrderApi } from '../../core/orders/admin-order-api';
import { AdminOrderListPage, AdminOrderListRow } from '../../core/orders/admin-order.model';

const ORDER = '44444444-4444-4444-8444-444444444444';

/**
 * Every field carries a real value.
 *
 * W1-12's blocker survived 1,571 passing tests because every fixture set the columns it cleared to
 * `null`, so the round trip compared null with null and agreed. A fixture whose courier, tracking number
 * and invoice number are real is the only kind that can fail when the row stops rendering them.
 */
function row(overrides: Partial<AdminOrderListRow> = {}): AdminOrderListRow {
  return {
    orderPublicId: ORDER,
    orderNumber: 'SD-2026-000123',
    status: 'Shipped',
    statusLabel: 'Shipped',
    customerName: 'Nimali Perera',
    customerEmail: 'nimali@example.com',
    grandTotal: 15_596.6,
    currency: 'LKR',
    paymentProviderCode: 'BankTransfer',
    deliveryMethodName: 'Standard delivery',
    isPickup: false,
    courierName: 'A carrier',
    trackingNumber: 'TRACK-0001',
    invoiceNumber: 'INV-2026-000045',
    placedAtUtc: '2026-10-05T04:30:00Z',
    paidAtUtc: '2026-10-05T09:00:00Z',
    secondsUntilExpiry: null,
    ...overrides,
  };
}

function page(overrides: Partial<AdminOrderListPage> = {}): AdminOrderListPage {
  return {
    rows: [row()],
    page: 1,
    pageSize: 25,
    totalCount: 1,
    sort: 'Newest',
    statusCounts: {
      PendingPayment: 2,
      Paid: 11,
      Processing: 1,
      Shipped: 4,
      Delivered: 9,
      Cancelled: 0,
      Expired: 3,
      Refunded: 0,
    },
    ...overrides,
  };
}

/**
 * The admin order list (ADM-09, doc 11 P9 step 1).
 *
 * **The outgoing request and the DOM after a filter are the heart of this file**, for the reason
 * `bank-transfers-page.spec.ts` states: the API decides what comes back, so the thing that can be wrong
 * is what the *client* asks for and what it then shows. Issue #126 is the other half — a browser test
 * cannot observe either, because Playwright runs against Node stubs.
 */
describe('OrdersListPage (ADM-09)', () => {
  let fixture: ComponentFixture<OrdersListPage>;
  let element: HTMLElement;
  let http: HttpTestingController;

  async function settle(): Promise<void> {
    fixture.detectChanges();
    await fixture.whenStable();
  }

  async function open(payload: AdminOrderListPage = page()): Promise<void> {
    fixture = TestBed.createComponent(OrdersListPage);
    element = fixture.nativeElement as HTMLElement;

    await settle();

    http.expectOne(`${AdminOrderApi.ordersUrl}?page=1&pageSize=25`).flush(payload);

    await settle();
  }

  function click(testId: string): void {
    element.querySelector<HTMLElement>(`[data-testid="${testId}"]`)!.click();
  }

  function type(testId: string, value: string): void {
    const input = element.querySelector<HTMLInputElement>(`[data-testid="${testId}"]`)!;
    input.value = value;
    input.dispatchEvent(new Event('input'));
  }

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    });

    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    http.verify();
  });

  it('shows what a staff member needs to triage a row', async () => {
    await open();

    const text = element.textContent ?? '';

    expect(text).toContain('SD-2026-000123');
    expect(text).toContain('Nimali Perera');
    expect(text).toContain('Standard delivery');
    expect(text).toContain('A carrier');
    expect(text).toContain('TRACK-0001');
    expect(text).toContain('INV-2026-000045');

    expect(
      element.querySelector('[data-testid="total-SD-2026-000123"]')?.textContent,
    ).toContain('15,596.60');
    expect(
      element.querySelector('[data-testid="status-SD-2026-000123"]')?.textContent,
    ).toContain('Shipped');

    // The order number is the link, so the whole row heading is the control a person aims at.
    expect(
      element.querySelector('[data-testid="open-SD-2026-000123"]')?.getAttribute('href'),
    ).toBe(`/orders/${ORDER}`);
  });

  it('renders the server’s own status wording, not its own', async () => {
    // A pickup order in `Shipped` reads as "Ready for collection" (doc 11 P9's pickup variant). The
    // screen must not re-derive it: the server sends the right wording per row, and two copies of that
    // conditional would be two chances for a pickup order to be described as shipped.
    await open(
      page({
        rows: [row({ status: 'Shipped', statusLabel: 'Ready for collection', isPickup: true })],
      }),
    );

    expect(
      element.querySelector('[data-testid="status-SD-2026-000123"]')?.textContent,
    ).toContain('Ready for collection');
    expect(element.textContent).not.toContain('>Shipped<');
  });

  it('shows the deadline only while the order is awaiting payment', async () => {
    await open(
      page({
        rows: [
          row({
            status: 'PendingPayment',
            statusLabel: 'Awaiting payment',
            secondsUntilExpiry: (5 * 60 * 60) + (30 * 60),
          }),
        ],
      }),
    );

    expect(element.textContent).toContain('5 h 30 min left');
  });

  it('says how overdue a row is rather than clamping it to zero', async () => {
    // "Overdue by eleven minutes" and "due now" are different facts to somebody deciding whether to
    // chase a payment, which is why the server sends a signed number of seconds.
    await open(
      page({
        rows: [row({ status: 'PendingPayment', statusLabel: 'Awaiting payment', secondsUntilExpiry: -11 * 60 })],
      }),
    );

    expect(element.textContent).toContain('overdue by 11 min');
  });

  it('sends the search term only when Search is pressed', async () => {
    await open();

    type('order-search', 'SD-2026-000123');

    // Nothing has gone out yet: the box does not search as you type, because the server classifies the
    // term and matches one column — a half-typed number is a different query, not a narrower one, and a
    // list that reshuffled on every keystroke would move the row the staff member is reaching for.
    http.expectNone(() => true);

    click('order-search-submit');
    await settle();

    const request = http.expectOne(
      `${AdminOrderApi.ordersUrl}?page=1&pageSize=25&search=SD-2026-000123`,
    );
    request.flush(page());

    await settle();
  });

  it('clears the search and asks for every order again', async () => {
    await open();

    type('order-search', 'Perera');
    click('order-search-submit');
    await settle();
    http.expectOne(`${AdminOrderApi.ordersUrl}?page=1&pageSize=25&search=Perera`).flush(page());
    await settle();

    click('order-search-clear');
    await settle();

    http.expectOne(`${AdminOrderApi.ordersUrl}?page=1&pageSize=25`).flush(page());
    await settle();
  });

  it('filters by status and sends the status by name', async () => {
    await open();

    click('filter-Paid');
    await settle();

    // By name, never by number: the numbers are the database's contract (doc 08 §1.4) and a renumbering
    // must not silently change what a bookmarked URL means.
    http.expectOne(`${AdminOrderApi.ordersUrl}?page=1&pageSize=25&status=Paid`).flush(
      page({ sort: 'Oldest' }),
    );

    await settle();
  });

  it('does not send a sort of its own, so the Paid filter gets doc 11 P9 step 1’s order', async () => {
    await open();

    click('filter-Paid');
    await settle();

    const request = http.expectOne(`${AdminOrderApi.ordersUrl}?page=1&pageSize=25&status=Paid`);

    // **No `sort` in the query.** The rule — "filter Paid, oldest first" — lives on the server so a
    // second client cannot forget it. The screen then labels the list from the answer.
    expect(request.request.params.has('sort')).toBe(false);

    request.flush(page({ sort: 'Oldest' }));
    await settle();

    expect(
      element.querySelector('[data-testid="sort-oldest"]')?.getAttribute('aria-pressed'),
    ).toBe('true');
    expect(
      element.querySelector('[data-testid="sort-newest"]')?.getAttribute('aria-pressed'),
    ).toBe('false');
  });

  it('labels the list from the server’s answer even when it disagrees with the request', async () => {
    await open();

    click('sort-oldest');
    await settle();

    const request = http.expectOne(`${AdminOrderApi.ordersUrl}?page=1&pageSize=25&sort=Oldest`);

    // The server answers Newest. The screen has to believe the answer, because the answer is what the
    // rows are actually in — labelling from the request would be a screen asserting a default it did not
    // get.
    request.flush(page({ sort: 'Newest' }));
    await settle();

    expect(
      element.querySelector('[data-testid="sort-newest"]')?.getAttribute('aria-pressed'),
    ).toBe('true');
  });

  it('drops a requested sort when the filter changes', async () => {
    await open();

    click('sort-oldest');
    await settle();
    http.expectOne(`${AdminOrderApi.ordersUrl}?page=1&pageSize=25&sort=Oldest`).flush(
      page({ sort: 'Oldest' }),
    );
    await settle();

    click('filter-Shipped');
    await settle();

    // No `sort`: moving to another tab gets that tab's own default back rather than inheriting the
    // previous one, which for the Paid tab is the difference between P9 step 1 and not.
    const request = http.expectOne(`${AdminOrderApi.ordersUrl}?page=1&pageSize=25&status=Shipped`);
    expect(request.request.params.has('sort')).toBe(false);

    request.flush(page());
    await settle();
  });

  it('shows the whole-table counts beside the tabs', async () => {
    await open();

    // Over the whole table, so a tab's count does not change when somebody types in the search box.
    expect(element.querySelector('[data-testid="filter-Paid"]')?.textContent).toContain('11');
    expect(element.querySelector('[data-testid="filter-Cancelled"]')?.textContent).toContain('0');

    // All is the sum, and a status at zero still has a count — a tab whose number disappeared would be
    // one a staff member could not tell from a tab that was never there.
    expect(element.querySelector('[data-testid="filter-all"]')?.textContent).toContain('30');
  });

  it('says what an empty search means rather than just “no orders”', async () => {
    await open();

    type('order-search', 'SD-1999-999999');
    click('order-search-submit');
    await settle();

    http
      .expectOne(`${AdminOrderApi.ordersUrl}?page=1&pageSize=25&search=SD-1999-999999`)
      .flush(page({ rows: [], totalCount: 0 }));

    await settle();

    const empty = element.querySelector('[data-testid="orders-empty"]')?.textContent ?? '';

    expect(empty).toContain('SD-1999-999999');
    expect(empty).toContain('email');
  });

  it('refuses a page move past the end rather than disabling the button', async () => {
    await open(page({ rows: [row()], page: 1, totalCount: 1 }));

    const next = element.querySelector<HTMLElement>('[data-testid="orders-next"]')!;

    // `aria-disabled`, never `[disabled]`: `[disabled]` on a button whose state changes under the user
    // throws focus to `<body>` (#109, six instances), so the handler refuses the activation instead.
    expect(next.getAttribute('aria-disabled')).toBe('true');
    expect(next.hasAttribute('disabled')).toBe(false);

    next.click();
    await settle();

    // Nothing went out.
    http.expectNone(() => true);
  });

  it('pages forward when there is another page', async () => {
    await open(page({ rows: [row()], page: 1, pageSize: 25, totalCount: 60 }));

    expect(element.querySelector('[data-testid="orders-paging"]')?.textContent).toContain('1–25 of 60');

    click('orders-next');
    await settle();

    http.expectOne(`${AdminOrderApi.ordersUrl}?page=2&pageSize=25`).flush(
      page({ rows: [row()], page: 2, pageSize: 25, totalCount: 60 }),
    );

    await settle();

    expect(element.querySelector('[data-testid="orders-paging"]')?.textContent).toContain('26–50 of 60');
  });

  it('shows the server’s sentence when the list cannot be read', async () => {
    fixture = TestBed.createComponent(OrdersListPage);
    element = fixture.nativeElement as HTMLElement;

    await settle();

    http
      .expectOne(`${AdminOrderApi.ordersUrl}?page=1&pageSize=25`)
      .flush({ detail: 'Nope.' }, { status: 403, statusText: 'Forbidden' });

    await settle();

    expect(element.querySelector('[role="alert"]')?.textContent).toContain('permission');
  });
});
