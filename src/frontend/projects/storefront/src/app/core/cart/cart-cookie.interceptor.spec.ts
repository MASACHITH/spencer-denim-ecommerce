import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { REQUEST } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import { cartCookieInterceptor } from './cart-cookie.interceptor';

/**
 * The SSR half of the bag cookie.
 *
 * Without this interceptor the server would render every bag as empty — it has no cookie jar — and the
 * page would fill in only after hydration, which is both the thing SSR exists to avoid and a bug that
 * a browser-only test cannot see.
 */
describe('cartCookieInterceptor (CART-01, SSR)', () => {
  function setUp(cookie: string | null): { http: HttpClient; controller: HttpTestingController } {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([cartCookieInterceptor])),
        provideHttpClientTesting(),
        {
          provide: REQUEST,
          useValue:
            cookie === null
              ? null
              : new Request('http://localhost/bag', { headers: { cookie } }),
        },
      ],
    });

    return {
      http: TestBed.inject(HttpClient),
      controller: TestBed.inject(HttpTestingController),
    };
  }

  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('copies the bag cookie onto a cart request during a server render', () => {
    const token = '8e29b4c6-c0f9-4f0e-9b7e-2d1a5c3f6a7b';
    const { http, controller } = setUp(`sd_cart=${token}`);

    http.get('/api/cart').subscribe();

    expect(controller.expectOne('/api/cart').request.headers.get('Cookie')).toBe(`sd_cart=${token}`);
  });

  /**
   * **Only `sd_cart`.** The same browser may be carrying `sd_admin_auth`; relaying a staff session from
   * the SSR process to the API while rendering a shopper's page would hand the storefront an
   * authenticated identity it has no business holding.
   */
  it('forwards nothing but the bag cookie, whatever else the browser is carrying', () => {
    const { http, controller } = setUp(
      'sd_admin_auth=secret; sd_cart=8e29b4c6-c0f9-4f0e-9b7e-2d1a5c3f6a7b; other=x',
    );

    http.get('/api/cart').subscribe();

    const header = controller.expectOne('/api/cart').request.headers.get('Cookie');
    expect(header).toBe('sd_cart=8e29b4c6-c0f9-4f0e-9b7e-2d1a5c3f6a7b');
    expect(header).not.toContain('sd_admin_auth');
    expect(header).not.toContain('other');
  });

  it('leaves every other call alone, bag cookie or not', () => {
    const { http, controller } = setUp('sd_cart=8e29b4c6-c0f9-4f0e-9b7e-2d1a5c3f6a7b');

    http.get('/api/catalog/products').subscribe();
    http.get('/api/content/home').subscribe();

    expect(controller.expectOne('/api/catalog/products').request.headers.has('Cookie')).toBe(false);
    expect(controller.expectOne('/api/content/home').request.headers.has('Cookie')).toBe(false);
  });

  it('is a no-op in the browser, where the cookie travels by itself', () => {
    const { http, controller } = setUp(null);

    http.get('/api/cart').subscribe();

    expect(controller.expectOne('/api/cart').request.headers.has('Cookie')).toBe(false);
  });

  /**
   * Lesson L1: this is a string a browser handed us. A malformed header must read as "no cookie" rather
   * than throw during a render, which would be a 500 on the bag page.
   */
  it.each([
    ['no cart cookie at all', 'other=x; another=y'],
    ['an empty value', 'sd_cart='],
    ['a bare name with no value', 'sd_cart'],
    ['nothing but separators', ';;;'],
    ['a name that merely ends with ours', 'not_sd_cart=abc'],
  ])('sends no cookie for %s', (_name, cookie) => {
    const { http, controller } = setUp(cookie);

    http.get('/api/cart').subscribe();

    expect(controller.expectOne('/api/cart').request.headers.has('Cookie')).toBe(false);
  });

  /**
   * The value is passed through untouched — the API refuses anything that is not a canonical GUID, and
   * that one decision lives on the server rather than being made twice. The interceptor must not be the
   * thing that decides, but it must not mangle the value either.
   */
  it('passes a value through unchanged, leaving the API to judge it', () => {
    const { http, controller } = setUp('sd_cart=not-a-guid');

    http.get('/api/cart').subscribe();

    expect(controller.expectOne('/api/cart').request.headers.get('Cookie')).toBe('sd_cart=not-a-guid');
  });

  /**
   * During SSR `apiBaseUrlInterceptor` rewrites `/api/...` to the internal API address, so by the time
   * a request is inspected the URL may be absolute. Matching only a leading `/api/cart` would have
   * silently stopped forwarding the cookie in exactly the configuration that needs it.
   */
  /**
   * It decides on the **path**, not on a substring. This is the one place in the application that
   * attaches the bag token to an outbound request, so a URL that merely mentions `/api/cart` must not
   * collect it — `/api/cartoons` is the shape that would have, and a `returnTo` query carrying the path
   * is the shape somebody would add next.
   */
  it.each([
    ['a longer path that starts the same way', '/api/cartoons'],
    ['a sibling under a similar prefix', '/api/cart-summary'],
    ['the path in a query string', '/api/catalog/products?returnTo=/api/cart'],
    ['the path in a fragment', '/api/catalog/products#/api/cart'],
  ])('sends no cookie for %s', (_name, url) => {
    const { http, controller } = setUp('sd_cart=8e29b4c6-c0f9-4f0e-9b7e-2d1a5c3f6a7b');

    http.get(url).subscribe();

    expect(controller.expectOne(url).request.headers.has('Cookie')).toBe(false);
  });

  it('recognises a path below the bag, which is where the item routes live', () => {
    const { http, controller } = setUp('sd_cart=8e29b4c6-c0f9-4f0e-9b7e-2d1a5c3f6a7b');

    http.get('/api/cart/items/1').subscribe();

    expect(controller.expectOne('/api/cart/items/1').request.headers.has('Cookie')).toBe(true);
  });

  it('still recognises a cart call once the URL has been made absolute', () => {
    const { http, controller } = setUp('sd_cart=8e29b4c6-c0f9-4f0e-9b7e-2d1a5c3f6a7b');

    http.get('http://api:8080/api/cart').subscribe();

    expect(controller.expectOne('http://api:8080/api/cart').request.headers.has('Cookie')).toBe(true);
  });

  /**
   * **W3-02's checkout is per-visitor in exactly the same way, and this interceptor did not know it.**
   *
   * `/api/checkout` finds the bag by the same `sd_cart` cookie, so without the cookie the server
   * rendered every checkout with an empty bag: the page said "your bag is empty" in the HTML and then
   * filled itself in after hydration — the precise failure this interceptor's own docblock says SSR
   * exists to avoid. It was found by `checkout.e2e.ts` asserting the server's HTML, and this is the unit
   * test that should have caught it first.
   */
  it.each([
    ['the checkout itself', '/api/checkout'],
    ['the checkout with a quote', '/api/checkout?districtId=1&deliveryMethodId=2'],
    ['placing the order', '/api/checkout/orders'],
    ['an absolute checkout URL', 'http://api:8080/api/checkout'],
  ])('copies the bag cookie onto %s', (_name, url) => {
    const token = '8e29b4c6-c0f9-4f0e-9b7e-2d1a5c3f6a7b';
    const { http, controller } = setUp(`sd_cart=${token}`);

    http.get(url).subscribe();

    expect(controller.expectOne(url).request.headers.get('Cookie')).toBe(`sd_cart=${token}`);
  });

  /**
   * **And not onto the order link.** `/api/orders/{token}` is found by the unguessable token in the URL,
   * not by a cookie, so sending one would be relaying a bearer credential to a call that has no use for
   * it — the same rule that keeps `sd_admin_auth` out of every request above. A guest opening their order
   * link from a different browser must get the same answer, which is only true if the cookie is irrelevant.
   */
  it.each([
    ['an order by its public token', '/api/orders/22222222-2222-4222-8222-222222222222'],
    ['a near-miss prefix', '/api/checkoutish'],
  ])('sends no cookie for %s', (_name, url) => {
    const { http, controller } = setUp('sd_cart=8e29b4c6-c0f9-4f0e-9b7e-2d1a5c3f6a7b');

    http.get(url).subscribe();

    expect(controller.expectOne(url).request.headers.has('Cookie')).toBe(false);
  });
});
