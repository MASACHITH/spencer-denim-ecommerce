import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  TestRequest,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { PLATFORM_ID, RESPONSE_INIT } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Meta, Title } from '@angular/platform-browser';
import { Router, provideRouter, withComponentInputBinding } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';

import { OrdersPage } from './orders-page';
import {
  CustomerOrderPageDto,
  CustomerOrderSummaryDto,
  CustomerSessionDto,
} from '../../core/account/account.model';
import { AccountStore } from '../../core/account/account.store';

const CUSTOMER: CustomerSessionDto = {
  userId: 7,
  email: 'nimal@example.invalid',
  firstName: 'Nimal',
  lastName: 'Perera',
  emailConfirmed: true,
};

/**
 * One order row, as the API answers for it.
 *
 * **`tracking` is absent by default, not null**, because that is what the server does: the block is
 * omitted for an order that has not shipped and for a store-pickup order, which has no courier at all.
 * A fixture that sent `tracking: null` would be a fixture the real API never produces.
 */
function order(overrides: Partial<CustomerOrderSummaryDto> = {}): CustomerOrderSummaryDto {
  return {
    orderNumber: 'SD-2026-00041',
    token: '8f1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d',
    status: 'Processing',
    statusLabel: 'Processing',
    placedAtUtc: '2026-10-07T04:30:00Z',
    grandTotal: 7490,
    currency: 'LKR',
    itemCount: 3,
    firstLineThumbnailUrl: '/media/products/shirt-thumb.webp',
    ...overrides,
  };
}

function orderPage(overrides: Partial<CustomerOrderPageDto> = {}): CustomerOrderPageDto {
  const orders = overrides.orders ?? [order()];
  const page = overrides.page ?? 1;
  const pageSize = overrides.pageSize ?? 10;
  const totalCount = overrides.totalCount ?? orders.length;

  return {
    orders,
    page,
    pageSize,
    totalCount,
    hasMore: overrides.hasMore ?? page * pageSize < totalCount,
  };
}

/**
 * My account's order history (ACC-02, doc 11 P11 step 12) — **the acceptance test for W3-01's front end**.
 *
 * Before this page a shopper who placed an order had no way to look it up again unless they still had
 * the emailed link, and the tracking number existed only in the admin panel. So the tests that matter
 * most are the ones about the tracking block and about the row leading to the order: everything else is
 * a list.
 *
 * **A router harness rather than a component fixture**, because paging lives in the address bar: `?page=`
 * is bound by `withComponentInputBinding()`, and a test that set the input by hand would not be testing
 * the thing a shopper's Back button uses.
 */
describe('OrdersPage (ACC-02)', () => {
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

  async function open(
    url = '/account/orders',
    platform: 'browser' | 'server' = 'browser',
  ): Promise<void> {
    response = {};
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        provideRouter(
          [
            { path: '', children: [] },
            { path: 'account/orders', component: OrdersPage },
            { path: 'account/addresses', children: [] },
            { path: 'account/profile', children: [] },
            { path: 'order/:token', children: [] },
          ],
          withComponentInputBinding(),
        ),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: RESPONSE_INIT, useValue: response },
        ...(platform === 'server' ? [{ provide: PLATFORM_ID, useValue: 'server' }] : []),
      ],
    });

    http = TestBed.inject(HttpTestingController);

    // Settled before the page is built: the page's own `load()` then costs no request, and the guard is
    // not in play in this harness. `signed-in.guard.spec.ts` covers the guard.
    TestBed.inject(AccountStore).accept(CUSTOMER);

    harness = await RouterTestingHarness.create(url);
    fixture = harness.fixture;
    element = harness.routeNativeElement as HTMLElement;
    await settle();
  }

  /** The one outstanding order read, whatever its page. */
  function ordersRequest(): TestRequest {
    return http.expectOne((candidate) => candidate.url === '/api/account/orders');
  }

  async function withOrders(page = orderPage(), url = '/account/orders'): Promise<void> {
    await open(url);
    ordersRequest().flush(page);
    await settle();
  }

  function rows(): HTMLElement[] {
    return [...element.querySelectorAll<HTMLElement>('.acc-order')];
  }

  function one(testId: string, root: ParentNode = element): HTMLElement | null {
    return root.querySelector<HTMLElement>(`[data-testid="${testId}"]`);
  }

  function text(): string {
    return element.textContent ?? '';
  }

  afterEach(() => http?.verify());

  describe('what the server renders', () => {
    /**
     * **The shell and no list.** `sd_customer_auth` is not forwarded to the API during SSR —
     * `cartCookieInterceptor` forwards `sd_cart` and nothing else, on purpose — so the server has no way
     * to read this and does not pretend to.
     */
    it('renders the heading and the navigation, and asks the shop nothing', async () => {
      await open('/account/orders', 'server');

      expect(element.querySelector('h1')?.textContent?.trim()).toBe('Your orders');
      expect(one('account-nav-orders')).not.toBeNull();
      expect(http.match(() => true)).toEqual([]);
    });

    /**
     * **It gates on having asked, not on the list being empty.** "I have not looked yet" and "you have no
     * orders" are the same empty array, and telling a customer with thirty orders that they have none —
     * for the whole of the server's response — is a confident wrong answer rather than a slow one.
     */
    it('says it is loading rather than that there are no orders', async () => {
      await open('/account/orders', 'server');

      expect(one('orders-loading')).not.toBeNull();
      expect(one('orders-empty')).toBeNull();
      expect(text()).not.toContain('not placed an order');
    });

    it('renders the live region before there is anything to announce', async () => {
      await open('/account/orders', 'server');

      expect(one('orders-alert')?.getAttribute('role')).toBe('alert');
      expect(one('orders-status')?.getAttribute('role')).toBe('status');
      expect(one('orders-status')?.textContent).toBe('');
    });

    /** An order history in a shared cache or a search result is the worst case on this surface. */
    it('answers no-store with Vary: Cookie, and noindex', async () => {
      await withOrders();

      const headers = new Headers(response.headers ?? {});
      expect(headers.get('Cache-Control')).toContain('no-store');
      expect(headers.get('Cache-Control')).toContain('private');
      expect(headers.get('Vary')).toBe('Cookie');
      expect(TestBed.inject(Meta).getTag('name="robots"')?.content).toBe('noindex');
      expect(TestBed.inject(Title).getTitle()).toBe('Your orders | Colombo Wardrobe');
    });

    it('clears the robots tag on the way out', async () => {
      await withOrders();
      fixture.destroy();

      expect(TestBed.inject(Meta).getTag('name="robots"')).toBeNull();
    });
  });

  describe('a row (doc 11 P11 step 12)', () => {
    it('shows the number, the date, the status, the item count and the total', async () => {
      await withOrders();

      const row = rows()[0];
      expect(one('order-link', row)?.textContent?.trim()).toBe('SD-2026-00041');
      expect(one('order-date', row)?.textContent?.trim()).toBe('7 October 2026');
      expect(one('order-status', row)?.textContent?.trim()).toBe('Processing');
      expect(one('order-items', row)?.textContent?.trim()).toBe('3 items');
      expect(one('order-total', row)?.textContent?.trim()).toBe('Rs. 7,490');
    });

    it('uses the singular for a one-item order', async () => {
      await withOrders(orderPage({ orders: [order({ itemCount: 1 })] }));

      expect(one('order-items', rows()[0])?.textContent?.trim()).toBe('1 item');
    });

    /**
     * **The server's own label, never `status` lower-cased.** Doc 11 P9's pickup variant shows `Shipped`
     * as "Ready for collection" and `Delivered` as "Collected"; the label comes from the same function
     * the two admin screens and the guest order page use, so the four cannot disagree.
     */
    it('prints the label the server sent, not the status name', async () => {
      await withOrders(
        orderPage({
          orders: [order({ status: 'Shipped', statusLabel: 'Ready for collection' })],
        }),
      );

      expect(one('order-status', rows()[0])?.textContent?.trim()).toBe('Ready for collection');
      expect(text()).not.toContain('Shipped');
    });

    /**
     * **The row leads to the existing order page**, by the order's own unguessable token — the same
     * route a guest's emailed link uses, deliberately, so there is one ownership filter rather than two.
     */
    it('links to /order/{token}', async () => {
      await withOrders();

      expect(one('order-link', rows()[0])?.getAttribute('href')).toBe(
        '/order/8f1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d',
      );
    });

    it('draws the thumbnail with a reserved box, and leaves a gap when there is none', async () => {
      await withOrders();

      const image = rows()[0].querySelector('img') as HTMLImageElement;
      expect(image.getAttribute('src')).toBe('/media/products/shirt-thumb.webp');
      // Reserved before the bytes arrive, so the row does not jump (CLS).
      expect(image.getAttribute('width')).toBe('72');
      expect(image.getAttribute('height')).toBe('96');
      // Decorative: the order number beside it is the row's accessible name.
      expect(image.getAttribute('alt')).toBe('');

      await withOrders(orderPage({ orders: [order({ firstLineThumbnailUrl: null })] }));
      expect(rows()[0].querySelector('img')).toBeNull();
      expect(rows()[0].querySelector('.acc-order__thumb')).not.toBeNull();
    });

    /**
     * The thumbnail is a second link to the same place, taken out of the accessibility tree and out of
     * the tab order — the shape `bag-page.ts` uses. A screen reader hearing the same destination twice
     * is noise.
     */
    it('hides the thumbnail link from the tab order and the accessibility tree', async () => {
      await withOrders();

      const thumb = rows()[0].querySelector('.acc-order__thumb') as HTMLElement;
      expect(thumb.getAttribute('tabindex')).toBe('-1');
      expect(thumb.getAttribute('aria-hidden')).toBe('true');
    });

    it('keeps the newest first, which is the order the server sent', async () => {
      await withOrders(
        orderPage({
          orders: [
            order({ orderNumber: 'SD-2026-00041', token: 'aaa' }),
            order({ orderNumber: 'SD-2026-00033', token: 'bbb' }),
            order({ orderNumber: 'SD-2026-00012', token: 'ccc' }),
          ],
        }),
      );

      expect(rows().map((row) => one('order-link', row)?.textContent?.trim())).toEqual([
        'SD-2026-00041',
        'SD-2026-00033',
        'SD-2026-00012',
      ]);
    });
  });

  /**
   * **The tracking number and its link, which is the whole point of the task.**
   *
   * Doc 11 P11 step 12 asks for a "courier tracking link" and before W3-01 the number existed only in the
   * admin panel. Putting it on the *list* means the answer to "where is my parcel" is one page, not two.
   */
  describe('the tracking (ACC-02, doc 11 P11 step 12)', () => {
    it('shows the courier, the number and the link when the courier has online tracking', async () => {
      await withOrders(
        orderPage({
          orders: [
            order({
              status: 'Shipped',
              statusLabel: 'Shipped',
              tracking: {
                courierName: 'Pronto Express',
                trackingNumber: 'PE123456789LK',
                trackingUrl: 'https://pronto.example.invalid/track/PE123456789LK',
                shippedAtUtc: '2026-10-09T06:00:00Z',
              },
            }),
          ],
        }),
      );

      const block = one('order-tracking', rows()[0]);
      expect(block?.textContent).toContain('Pronto Express');

      const link = one('order-tracking-link', rows()[0]) as HTMLAnchorElement;
      expect(link.textContent).toContain('PE123456789LK');
      expect(link.getAttribute('href')).toBe('https://pronto.example.invalid/track/PE123456789LK');
    });

    /**
     * **`rel="noopener noreferrer"` because the URL is built from an admin-editable template** (doc 08
     * §3.5), so the destination is not the shop's own; and the new tab is announced, because WCAG 2.2
     * SC 3.2.5 asks for a warning rather than a surprise.
     */
    it('opens the courier in a new tab, safely, and says so', async () => {
      await withOrders(
        orderPage({
          orders: [
            order({
              tracking: {
                courierName: 'Pronto Express',
                trackingNumber: 'PE123456789LK',
                trackingUrl: 'https://pronto.example.invalid/track/PE123456789LK',
                shippedAtUtc: '2026-10-09T06:00:00Z',
              },
            }),
          ],
        }),
      );

      const link = one('order-tracking-link', rows()[0]) as HTMLAnchorElement;
      expect(link.getAttribute('target')).toBe('_blank');
      expect(link.getAttribute('rel')).toBe('noopener noreferrer');
      expect(link.textContent).toContain('opens in a new tab');
    });

    /**
     * **The number is still the thing the shopper needs**, so when the courier has no online tracking —
     * or has been deleted since, which leaves `trackingUrl` null — it is printed as text to quote rather
     * than hidden with the link.
     */
    it('prints the number as text when there is no link to give', async () => {
      await withOrders(
        orderPage({
          orders: [
            order({
              tracking: {
                courierName: 'Local Rider',
                trackingNumber: 'LR-99887',
                trackingUrl: null,
                shippedAtUtc: '2026-10-09T06:00:00Z',
              },
            }),
          ],
        }),
      );

      expect(one('order-tracking-link', rows()[0])).toBeNull();
      expect(one('order-tracking-number', rows()[0])?.textContent?.trim()).toBe('LR-99887');
      expect(one('order-tracking', rows()[0])?.textContent).toContain('Local Rider');
    });

    /**
     * **Absent, not null-filled.** An order that has not shipped and a store-pickup order, which has no
     * courier at all, both come back with no `tracking` property — so the row draws nothing rather than
     * an empty courier line.
     */
    it('draws no tracking at all for an order with nothing to track', async () => {
      await withOrders(orderPage({ orders: [order({ status: 'PendingPayment' })] }));

      expect(one('order-tracking', rows()[0])).toBeNull();
      expect(text()).not.toMatch(/courier|tracking/i);
    });

    it('shows tracking on one row and not on another, in the same list', async () => {
      await withOrders(
        orderPage({
          orders: [
            order({
              orderNumber: 'SD-2026-00041',
              token: 'aaa',
              tracking: {
                courierName: 'Pronto Express',
                trackingNumber: 'PE123456789LK',
                trackingUrl: 'https://pronto.example.invalid/track/PE123456789LK',
                shippedAtUtc: '2026-10-09T06:00:00Z',
              },
            }),
            order({ orderNumber: 'SD-2026-00033', token: 'bbb' }),
          ],
        }),
      );

      expect(one('order-tracking', rows()[0])).not.toBeNull();
      expect(one('order-tracking', rows()[1])).toBeNull();
    });
  });

  describe('paging', () => {
    /**
     * **`pageSize` is never sent.** The server's default is ten, it clamps anything outside 1–50 back to
     * ten, and it echoes the size back — so the page prints what it was given and cannot disagree with
     * the server about how many rows a page holds.
     */
    it('asks for page 1 and sends no page size', async () => {
      await open();

      const request = ordersRequest();
      expect(request.request.params.get('page')).toBe('1');
      expect(request.request.params.get('pageSize')).toBeNull();

      request.flush(orderPage());
      await settle();
    });

    it('asks for the page the address bar names', async () => {
      await open('/account/orders?page=3');

      expect(ordersRequest().request.params.get('page')).toBe('3');

      http.match(() => true).forEach((request) => request.flush(orderPage({ page: 3 })));
      await settle();
    });

    /** A hand-typed page number is a page number this component decides, not a `NaN` it passes on. */
    it('reads a nonsense page as page 1', async () => {
      await open('/account/orders?page=banana');

      expect(ordersRequest().request.params.get('page')).toBe('1');

      http.match(() => true).forEach((request) => request.flush(orderPage()));
      await settle();
    });

    it('draws no pager when everything fits on one page', async () => {
      await withOrders(orderPage({ totalCount: 4, orders: [order(), order({ token: 'b' })] }));

      expect(one('orders-next')).toBeNull();
      expect(one('orders-previous')).toBeNull();
      expect(one('orders-count')?.textContent?.trim()).toBe('4 orders.');
    });

    it('says which orders are on screen when there is more than one page', async () => {
      await withOrders(
        orderPage({
          orders: Array.from({ length: 10 }, (_, index) => order({ token: `t${index}` })),
          page: 2,
          pageSize: 10,
          totalCount: 34,
        }),
        '/account/orders?page=2',
      );

      expect(one('orders-count')?.textContent?.trim()).toBe('Orders 11 to 20 of 34.');
      expect(one('orders-pager-at')?.textContent?.replace(/\s+/g, ' ').trim()).toBe('Page 2 of 4');
    });

    it('navigates rather than fetching behind the address bar', async () => {
      await withOrders(
        orderPage({
          orders: Array.from({ length: 10 }, (_, index) => order({ token: `t${index}` })),
          totalCount: 34,
        }),
      );

      (one('orders-next') as HTMLButtonElement).click();
      await settle();

      expect(TestBed.inject(Router).url).toBe('/account/orders?page=2');
      expect(ordersRequest().request.params.get('page')).toBe('2');

      http.match(() => true).forEach((request) => request.flush(orderPage({ page: 2, totalCount: 34 })));
      await settle();
    });

    /**
     * Page 1's address is `/account/orders` and not `/account/orders?page=1`: two addresses for one page
     * is two entries in the back button.
     */
    it('goes back to page 1 without a query string', async () => {
      await withOrders(
        orderPage({
          orders: Array.from({ length: 10 }, (_, index) => order({ token: `t${index}` })),
          page: 2,
          totalCount: 34,
        }),
        '/account/orders?page=2',
      );

      (one('orders-previous') as HTMLButtonElement).click();
      await settle();

      expect(TestBed.inject(Router).url).toBe('/account/orders');

      http.match(() => true).forEach((request) => request.flush(orderPage({ totalCount: 34 })));
      await settle();
    });

    /**
     * **Neither control is ever removed or `disabled`** (#109, and W2-08's removal defect). The payoff is
     * concrete: because Next survives its own press, a keyboard shopper paging through a long history
     * keeps their place — so this test checks the attribute *and* where focus ended up.
     */
    it('refuses Previous on page 1 without disabling it, and keeps focus on it', async () => {
      await withOrders(
        orderPage({
          orders: Array.from({ length: 10 }, (_, index) => order({ token: `t${index}` })),
          totalCount: 34,
        }),
      );

      const previous = one('orders-previous') as HTMLButtonElement;
      expect(previous.hasAttribute('disabled')).toBe(false);
      expect(previous.getAttribute('aria-disabled')).toBe('true');

      previous.focus();
      previous.click();
      await settle();

      expect(TestBed.inject(Router).url).toBe('/account/orders');
      expect(document.activeElement).toBe(one('orders-previous'));
      expect(document.activeElement).not.toBe(document.body);
    });

    it('refuses Next on the last page without disabling it', async () => {
      await withOrders(
        orderPage({
          orders: Array.from({ length: 4 }, (_, index) => order({ token: `t${index}` })),
          page: 4,
          pageSize: 10,
          totalCount: 34,
          hasMore: false,
        }),
        '/account/orders?page=4',
      );

      const next = one('orders-next') as HTMLButtonElement;
      expect(next.hasAttribute('disabled')).toBe(false);
      expect(next.getAttribute('aria-disabled')).toBe('true');

      next.focus();
      next.click();
      await settle();

      expect(TestBed.inject(Router).url).toBe('/account/orders?page=4');
      expect(document.activeElement).toBe(one('orders-next'));
    });

    /** `role="status"` announces a *change*, so the sentence has to differ from page to page. */
    it('announces which page is on screen, differently each time', async () => {
      await withOrders(
        orderPage({
          orders: Array.from({ length: 10 }, (_, index) => order({ token: `t${index}` })),
          totalCount: 34,
        }),
      );

      const first = one('orders-status')?.textContent;
      expect(first).toContain('Orders 1 to 10 of 34');

      (one('orders-next') as HTMLButtonElement).click();
      await settle();
      ordersRequest().flush(
        orderPage({
          orders: Array.from({ length: 10 }, (_, index) => order({ token: `u${index}` })),
          page: 2,
          totalCount: 34,
        }),
      );
      await settle();

      expect(one('orders-status')?.textContent).toContain('Orders 11 to 20 of 34');
      expect(one('orders-status')?.textContent).not.toBe(first);
    });
  });

  describe('nothing to show, and nothing it could read', () => {
    it('says the history is empty, once it has actually looked', async () => {
      await withOrders(orderPage({ orders: [], totalCount: 0 }));

      expect(one('orders-empty')?.textContent).toContain('not placed an order');
      expect(element.querySelector('a.btn')?.textContent).toContain('Start shopping');
    });

    /**
     * **A failed read is not an empty history.** Telling a customer with thirty orders that they have
     * none because the shop was briefly unreachable is the confident wrong value again, so the page
     * offers Try again instead and the reason is in the alert region.
     */
    it('offers Try again rather than claiming there are no orders', async () => {
      await open();
      ordersRequest().error(new ProgressEvent('offline'), { status: 0 });
      await settle();

      expect(one('orders-empty')).toBeNull();
      expect(one('orders-retry')).not.toBeNull();
      expect(one('orders-alert')?.textContent).toContain('could not reach the store');

      (one('orders-retry') as HTMLButtonElement).click();
      await settle();
      ordersRequest().flush(orderPage());
      await settle();

      expect(rows()).toHaveLength(1);
      expect(one('orders-alert')?.textContent).toBe('');
    });

    /**
     * **A failed read keeps the rows it already had.** Paging onto a page the shop could not answer
     * leaves the previous page on screen with the reason beside it, which `wishlist-page.ts` does for
     * the same reason: an emptied list is a worse answer than a stale one.
     */
    it('keeps the page it is holding when the next one fails', async () => {
      await withOrders(
        orderPage({
          orders: Array.from({ length: 10 }, (_, index) => order({ token: `t${index}` })),
          totalCount: 34,
        }),
      );

      (one('orders-next') as HTMLButtonElement).click();
      await settle();
      ordersRequest().error(new ProgressEvent('offline'), { status: 0 });
      await settle();

      expect(rows()).toHaveLength(10);
      expect(one('orders-alert')?.textContent).toContain('could not reach the store');
    });
  });

  describe('why some orders are missing (doc 11 §7 G-24, doc 08 §3.1 D5)', () => {
    /**
     * Guest orders placed with the same address appear **only after the address is confirmed**. That is
     * specified behaviour and not a gap, so an unconfirmed customer is told it rather than left to wonder
     * why a list is short — and told that confirmation gates nothing else.
     */
    it('explains an unconfirmed address, without making it a gate', async () => {
      await open();
      TestBed.inject(AccountStore).accept({ ...CUSTOMER, emailConfirmed: false });
      ordersRequest().flush(orderPage());
      await settle();

      const note = one('account-confirm-note');
      expect(note?.textContent).toContain('not confirmed yet');
      expect(note?.textContent).toContain('shop and check out as usual');
      // The list is still there: nothing is withheld from an unconfirmed customer.
      expect(rows()).toHaveLength(1);
    });

    it('says nothing about confirming to a confirmed customer', async () => {
      await withOrders();

      expect(one('account-confirm-note')).toBeNull();
    });
  });

  describe('the checks axe did not make', () => {
    /**
     * **axe caught neither a wrongly-ranked heading nor a 20 px control on this project this week**, so
     * these are hand-written. jsdom has no layout, so the 44 px floor is asserted through the class that
     * sets it — which is the thing a refactor deletes — and the pixels are a browser test's job.
     */
    it('has exactly one h1 and skips no heading level', async () => {
      await withOrders();

      const levels = [...element.querySelectorAll('h1, h2, h3, h4, h5, h6')].map((node) =>
        Number(node.tagName.slice(1)),
      );

      expect(levels.filter((level) => level === 1)).toHaveLength(1);
      expect(levels[0]).toBe(1);

      for (let i = 1; i < levels.length; i += 1) {
        expect(levels[i] - levels[i - 1], `${levels[i - 1]} then ${levels[i]}`).toBeLessThanOrEqual(1);
      }
    });

    /** Each selector guarded on its own (#80): a group assertion hides the one that is wrong. */
    it('gives each control its own 44 px class', async () => {
      await withOrders(
        orderPage({
          orders: Array.from({ length: 10 }, (_, index) =>
            order({
              token: `t${index}`,
              tracking: {
                courierName: 'Pronto Express',
                trackingNumber: `PE${index}`,
                trackingUrl: 'https://pronto.example.invalid/x',
                shippedAtUtc: '2026-10-09T06:00:00Z',
              },
            }),
          ),
          totalCount: 34,
        }),
      );

      expect(one('order-link')?.className).toContain('acc-order__number');
      expect(one('order-tracking-link')?.className).toContain('acc-inline-action');
      expect(one('orders-next')?.className).toContain('btn');
      expect(one('orders-previous')?.className).toContain('btn');
      expect(one('account-nav-orders')?.className).toContain('acc-nav__link');
      expect(one('account-nav-addresses')?.className).toContain('acc-nav__link');
      expect(one('account-nav-profile')?.className).toContain('acc-nav__link');
    });

    it('marks the page you are on in the account navigation, with more than colour', async () => {
      await withOrders();

      expect(one('account-nav-orders')?.getAttribute('aria-current')).toBe('page');
      expect(one('account-nav-addresses')?.getAttribute('aria-current')).toBeNull();
    });

    it('names the pager as a navigation landmark', async () => {
      await withOrders(
        orderPage({
          orders: Array.from({ length: 10 }, (_, index) => order({ token: `t${index}` })),
          totalCount: 34,
        }),
      );

      expect(element.querySelector('.acc-pager')?.getAttribute('aria-label')).toBe(
        'Order history pages',
      );
    });

    it('invents no order, no date and no placeholder', async () => {
      await open('/account/orders', 'server');

      expect(element.querySelectorAll('.acc-order')).toHaveLength(0);
      expect(element.querySelectorAll('[placeholder]')).toHaveLength(0);
      expect(text()).not.toMatch(/SD-\d{4}-\d{5}/);
    });

    it('promises no email', async () => {
      await withOrders();

      expect(text()).not.toMatch(/email (you|your)/i);
      expect(text()).not.toMatch(/check your (inbox|email)/i);
    });
  });
});
