import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ApplicationRef } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';

import { SiteHeader } from './site-header';
import { AccountApi } from '../../core/account/account-api';
import { CustomerSessionDto } from '../../core/account/account.model';
import { CartApi } from '../../core/cart/cart-api';
import { CartDto } from '../../core/cart/cart.model';
import { CartStore } from '../../core/cart/cart.store';
import { SiteSettingsDto } from '../../core/site-settings/site-settings.model';

/** The honest default: the client has uploaded no logo and supplied no profile addresses (ADM-22). */
const NO_SETTINGS: SiteSettingsDto = { logo: null, whatsAppNumber: null, social: [] };

/**
 * A signed-in customer, for the greeting and the sign-out control (W3-01, ACC-01).
 *
 * `emailConfirmed: true`, because confirmation has nothing to do with the header: doc 11 §7 G-24 lets an
 * unconfirmed customer sign in, shop and check out, and the note about confirming lives on the account
 * pages. A header that drew anything different for an unconfirmed customer would be inventing a gate.
 */
const CUSTOMER: CustomerSessionDto = {
  userId: 7,
  email: 'nimal@example.invalid',
  firstName: 'Nimal',
  lastName: 'Perera',
  emailConfirmed: true,
};

const EMPTY_BAG: CartDto = {
  lines: [],
  totals: {
    itemCount: 0,
    buyableItemCount: 0,
    lineCount: 0,
    itemsOriginalTotal: 0,
    itemsSubtotal: 0,
    savedTotal: 0,
    currency: 'LKR',
  },
};

describe('SiteHeader shell (GLB-04, GLB-05)', () => {
  let fixture: ComponentFixture<SiteHeader>;
  let element: HTMLElement;

  async function settle() {
    fixture.detectChanges();
    await fixture.whenStable();
  }

  beforeEach(async () => {
    TestBed.configureTestingModule({
      imports: [SiteHeader],
      // The header reads the bag count after the first render (GLB-04), so the HTTP client has to be
      // the testing one: with the real backend that read is an uncontrolled request in a jsdom test.
      providers: [
        // Stub routes for the links in the header, so a modified click that is deliberately left to
        // the router does not fail on an unmatched route.
        provideRouter([
          { path: '', children: [] },
          { path: 'bag', children: [] },
          { path: 'wishlist', children: [] },
          { path: 'account', children: [] },
        ]),
        provideHttpClient(),
        provideHttpClientTesting(),
      ],
    });

    fixture = TestBed.createComponent(SiteHeader);
    element = fixture.nativeElement as HTMLElement;
    await settle();
    flushShell();
    await settle();
  });

  afterEach(() => TestBed.inject(HttpTestingController).verify());

  /**
   * Builds a second header whose settings request this test answers itself.
   *
   * A fresh TestBed rather than a second flush: `beforeEach` has already answered the first request, and
   * `SiteSettingsService` is `providedIn: 'root'`, so its signal is settled for that injector.
   */
  async function rebuild(
    answerSettings: (http: HttpTestingController) => void,
    session: CustomerSessionDto | null = null,
  ): Promise<HTMLElement> {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      imports: [SiteHeader],
      providers: [
        provideRouter([
          { path: '', children: [] },
          { path: 'bag', children: [] },
          { path: 'wishlist', children: [] },
          { path: 'account', children: [] },
        ]),
        provideHttpClient(),
        provideHttpClientTesting(),
      ],
    });

    const rebuilt = TestBed.createComponent(SiteHeader);
    rebuilt.detectChanges();
    await rebuilt.whenStable();

    const http = TestBed.inject(HttpTestingController);
    http.match((request) => request.url === '/api/content/announcements').forEach((request) => request.flush([]));
    http.match((request) => request.url === '/api/catalog/departments').forEach((request) => request.flush([]));
    http.match((request) => request.url === CartApi.url && request.method === 'GET')
      .forEach((request) => request.flush(EMPTY_BAG));
    flushSession(session);
    answerSettings(http);

    rebuilt.detectChanges();
    await rebuilt.whenStable();
    rebuilt.detectChanges();

    return rebuilt.nativeElement as HTMLElement;
  }

  /**
   * Answers everything the shell asks for on its own behalf: the announcement strip (GLB-03), the
   * department tree (GLB-05) and the bag count (GLB-04). They were all going to the real backend before
   * this spec had an HTTP client of its own, which is to say they were uncontrolled requests in a jsdom
   * test; `verify()` in `afterEach` only means something once they are answered here.
   */
  function flushShell(cart: CartDto = EMPTY_BAG, settings: SiteSettingsDto = NO_SETTINGS): void {
    const http = TestBed.inject(HttpTestingController);
    http.match((request) => request.url === '/api/content/announcements').forEach((request) => request.flush([]));
    http.match((request) => request.url === '/api/catalog/departments').forEach((request) => request.flush([]));
    http.match((request) => request.url === '/api/site/settings').forEach((request) => request.flush(settings));
    flushSession(null);
    flushBag(cart);
  }

  /**
   * Answers the one session read the header makes on its own behalf (W3-01).
   *
   * **401 is the default, because it is the ordinary answer**: most visitors are not signed in, and
   * `AccountStore` treats it as an answer rather than an error. `match` rather than `expectOne`, for the
   * reason the shell's other reads use it: the request is made from `afterNextRender`, so a test that
   * never renders does not make it, and a helper that insisted on one would fail those tests for the
   * wrong reason.
   */
  function flushSession(session: CustomerSessionDto | null): void {
    TestBed.inject(HttpTestingController)
      .match((request) => request.url === `${AccountApi.url}/session`)
      .forEach((request) => request.flush({ customer: session }));
  }

  /** Answers the bag read the header makes on its own behalf, if one is outstanding. */
  function flushBag(cart: CartDto): void {
    TestBed.inject(HttpTestingController)
      .match((request) => request.url === CartApi.url && request.method === 'GET')
      .forEach((request) => request.flush(cart));
  }

  function bagToggle(): HTMLAnchorElement {
    return element.querySelector('[data-testid="bag-toggle"]') as HTMLAnchorElement;
  }

  function miniBagPanel(): HTMLElement {
    return element.querySelector('sd-mini-bag [role="dialog"]') as HTMLElement;
  }

  function menuToggle(): HTMLButtonElement {
    return element.querySelector('[data-testid="menu-toggle"]') as HTMLButtonElement;
  }

  function drawer(): HTMLElement {
    return element.querySelector('sd-drawer') as HTMLElement;
  }

  it('renders one banner landmark with the wordmark', () => {
    expect(element.querySelectorAll('header')).toHaveLength(1);
    expect(element.querySelector('.wordmark')?.textContent?.trim()).toBe('Colombo Wardrobe');
  });

  /**
   * GLB-02, ADM-22. The fallback, and it is the *normal* state rather than an error state: no logo is
   * uploaded on a fresh deployment, so the text wordmark is what most visitors see.
   *
   * It asserts the absence of the `<img>` as well as the presence of the text, because "renders the
   * wordmark" would also pass for a header that drew both — a text wordmark beside a broken image is the
   * failure this is guarding, and the text alone cannot see it.
   */
  it('keeps the text wordmark and renders no image when no logo is set', () => {
    expect(element.querySelector('[data-testid="wordmark-text"]')?.textContent?.trim()).toBe(
      'Colombo Wardrobe',
    );
    expect(element.querySelector('[data-testid="logo"]')).toBeNull();
  });

  /** The same, for a settings read that failed outright rather than one that answered "nothing set". */
  it('keeps the text wordmark when the settings read fails', async () => {
    const header = await rebuild((http) =>
      http
        .match((request) => request.url === '/api/site/settings')
        .forEach((request) => request.flush('boom', { status: 500, statusText: 'Server Error' })),
    );

    expect(header.querySelector('[data-testid="wordmark-text"]')?.textContent?.trim()).toBe(
      'Colombo Wardrobe',
    );
    expect(header.querySelector('[data-testid="logo"]')).toBeNull();
  });

  it('draws the uploaded logo instead of the text, with its box reserved and a real alt', async () => {
    const header = await rebuild((http) =>
      http.match((request) => request.url === '/api/site/settings').forEach((request) =>
        request.flush({
          logo: { url: '/media/public/renditions/abc/card.webp', width: 600, height: 150, altText: 'Our logo' },
          whatsAppNumber: null,
          social: [],
        }),
      ),
    );

    const logo = header.querySelector<HTMLImageElement>('[data-testid="logo"]');

    expect(logo).not.toBeNull();
    expect(logo!.getAttribute('src')).toBe('/media/public/renditions/abc/card.webp');
    expect(logo!.getAttribute('alt')).toBe('Our logo');
    expect(logo!.getAttribute('width')).toBe('600');
    expect(logo!.getAttribute('height')).toBe('150');
    expect(header.querySelector('[data-testid="wordmark-text"]')).toBeNull();

    // The logo still has to be the link home: one anchor serves both states, so the tap target and the
    // focus ring cannot drift apart between them.
    expect(logo!.closest('a')?.getAttribute('href')).toBe('/');
  });

  /**
   * An asset with no alt text is the ordinary case — nothing makes a staff user type one — and an
   * unlabelled image doing the job of the site's own name is a WCAG 1.1.1 failure (GLB-12). The shop's
   * name is the fallback, read from the one place it is written down.
   */
  it('falls back to the shop name for alt text when the asset has none', async () => {
    const header = await rebuild((http) =>
      http.match((request) => request.url === '/api/site/settings').forEach((request) =>
        request.flush({
          logo: { url: '/media/logo.webp', width: null, height: null, altText: null },
          whatsAppNumber: null,
          social: [],
        }),
      ),
    );

    const logo = header.querySelector<HTMLImageElement>('[data-testid="logo"]');

    expect(logo!.getAttribute('alt')).toBe('Colombo Wardrobe');

    // No dimensions reported means no attributes at all, rather than width="0", which would collapse it.
    expect(logo!.hasAttribute('width')).toBe(false);
    expect(logo!.hasAttribute('height')).toBe(false);
  });

  it('shows the five main navigation entries', () => {
    const labels = Array.from(
      element.querySelectorAll<HTMLAnchorElement>('sd-main-nav .site-nav__link'),
    ).map((link) => link.textContent?.trim());

    expect(labels).toEqual(['Women', 'Men', 'Kids', 'New Arrivals', 'Sale']);
  });

  it('keeps every mega menu closed and hidden until it is asked for', () => {
    const panels = element.querySelectorAll('sd-main-nav .mega');

    expect(panels.length).toBeGreaterThan(0);
    panels.forEach((panel) => expect(panel.hasAttribute('hidden')).toBe(true));
    element
      .querySelectorAll('sd-main-nav .site-nav__link[aria-controls]')
      .forEach((link) => expect(link.getAttribute('aria-expanded')).toBe('false'));
  });

  it('opens a department mega menu on keyboard focus and closes it on Escape', async () => {
    const women = element.querySelector<HTMLAnchorElement>(
      'sd-main-nav .site-nav__link[aria-controls="mega-women"]',
    );
    women?.dispatchEvent(new FocusEvent('focusin', { bubbles: true }));
    await settle();

    expect(women?.getAttribute('aria-expanded')).toBe('true');
    expect(element.querySelector('#mega-women')?.hasAttribute('hidden')).toBe(false);

    // Escape comes from wherever focus is inside the open menu and bubbles to the navigation.
    women?.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    await settle();

    expect(women?.getAttribute('aria-expanded')).toBe('false');
    expect(element.querySelector('#mega-women')?.hasAttribute('hidden')).toBe(true);
  });

  it('opens the mobile drawer from the hamburger and reports it with aria-expanded', async () => {
    expect(menuToggle().getAttribute('aria-expanded')).toBe('false');
    expect(menuToggle().getAttribute('aria-controls')).toBe('storefront-menu');

    menuToggle().focus();
    menuToggle().click();
    await settle();

    expect(menuToggle().getAttribute('aria-expanded')).toBe('true');
    expect(drawer().classList.contains('is-open')).toBe(true);
    expect(drawer().contains(document.activeElement)).toBe(true);
  });

  it('closes the drawer on Escape and puts focus back on the hamburger', async () => {
    menuToggle().focus();
    menuToggle().click();
    await settle();

    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    await settle();

    expect(menuToggle().getAttribute('aria-expanded')).toBe('false');
    expect(drawer().classList.contains('is-open')).toBe(false);
    expect(document.activeElement).toBe(menuToggle());
  });

  it('expands a department section inside the drawer', async () => {
    menuToggle().click();
    await settle();

    const toggle = element.querySelector<HTMLButtonElement>(
      'sd-mobile-nav button[aria-controls="m-nav-women"]',
    );
    expect(toggle?.getAttribute('aria-expanded')).toBe('false');
    expect(element.querySelector('#m-nav-women')?.hasAttribute('hidden')).toBe(true);

    toggle?.click();
    await settle();

    expect(toggle?.getAttribute('aria-expanded')).toBe('true');
    expect(element.querySelector('#m-nav-women')?.hasAttribute('hidden')).toBe(false);
  });

  it('toggles the collapsed search panel', async () => {
    const toggle = element.querySelector<HTMLButtonElement>('[data-testid="search-toggle"]');
    expect(toggle?.getAttribute('aria-expanded')).toBe('false');
    expect(element.querySelector('#header-search-panel')?.hasAttribute('hidden')).toBe(true);

    toggle?.click();
    await settle();

    expect(toggle?.getAttribute('aria-expanded')).toBe('true');
    expect(element.querySelector('#header-search-panel')?.hasAttribute('hidden')).toBe(false);
  });

  it('draws no count badge while the wishlist and bag are empty', () => {
    expect(element.querySelectorAll('.count')).toHaveLength(0);
    expect(element.textContent).toContain('Wishlist, 0 items');
    expect(element.textContent).toContain('Bag, 0 items');
  });

  describe('the bag control (GLB-04, CART-02)', () => {
    /**
     * It is an anchor, not a button, and that is deliberate: /bag is a real page, so the link works
     * before the application has hydrated and with scripting off. A button would break all of that for
     * the sake of the drawer.
     */
    it('is a real link to the bag page as well as a drawer trigger', () => {
      expect(bagToggle().tagName).toBe('A');
      expect(bagToggle().getAttribute('href')).toBe('/bag');
      expect(bagToggle().getAttribute('aria-haspopup')).toBe('dialog');
      expect(bagToggle().getAttribute('aria-controls')).toBe('mini-bag');
      expect(bagToggle().getAttribute('aria-expanded')).toBe('false');
    });

    it('opens the mini bag on a plain click instead of navigating', async () => {
      const event = new MouseEvent('click', { bubbles: true, cancelable: true, button: 0 });
      bagToggle().dispatchEvent(event);
      await settle();

      expect(event.defaultPrevented).toBe(true);
      expect(bagToggle().getAttribute('aria-expanded')).toBe('true');
      expect(element.querySelector('sd-mini-bag sd-drawer')?.classList).toContain('is-open');
    });

    /**
     * A modified click is the shopper asking the browser for a new tab. Swallowing it is one of the ways
     * a single-page application quietly becomes worse than a document, so these must fall through to
     * the link.
     */
    it.each([
      ['ctrl', { ctrlKey: true }],
      ['meta', { metaKey: true }],
      ['shift', { shiftKey: true }],
      ['alt', { altKey: true }],
      ['middle button', { button: 1 }],
    ])('leaves a %s click to the browser', async (_name, modifier) => {
      const event = new MouseEvent('click', {
        bubbles: true,
        cancelable: true,
        button: 0,
        ...modifier,
      });
      bagToggle().dispatchEvent(event);
      await settle();

      expect(event.defaultPrevented).toBe(false);
      expect(bagToggle().getAttribute('aria-expanded')).toBe('false');
    });

    it('closes on Escape and gives focus back to the bag control', async () => {
      // Focused first, because that is what a real click or an Enter press does, and it is the element
      // sd-drawer captures to return focus to.
      bagToggle().focus();
      bagToggle().dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));
      await settle();

      document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
      await settle();

      expect(bagToggle().getAttribute('aria-expanded')).toBe('false');
      expect(document.activeElement).toBe(bagToggle());
    });

    /** CART-02: adding something opens the bag, which is the point of a slide-out bag. */
    it('opens itself when something is added to the bag', async () => {
      const store = TestBed.inject(CartStore);

      store.add(7, 1, 'Linen shirt');
      TestBed.inject(HttpTestingController)
        .expectOne((request) => request.url === `${CartApi.url}/items` && request.method === 'POST')
        .flush(EMPTY_BAG);
      await settle();

      expect(bagToggle().getAttribute('aria-expanded')).toBe('true');
      expect(miniBagPanel()).not.toBeNull();
    });

    it('shows the count the bag reports, with the right singular', async () => {
      const store = TestBed.inject(CartStore);

      store.load();
      flushBag({ ...EMPTY_BAG, totals: { ...EMPTY_BAG.totals, itemCount: 1 } });
      await settle();

      expect(element.textContent).toContain('Bag, 1 item');
      expect(element.querySelector('.count')?.textContent?.trim()).toBe('1');
    });
  });

  /**
   * Who is signed in, in the shell (W3-01, ACC-01).
   *
   * The header is on every page of the shop, so this is also where the decision not to read the session
   * on the server is visible: a first name rendered here would make the home page, every listing and
   * every product page `no-store` with `Vary: Cookie`.
   */
  describe('the signed-in customer (W3-01)', () => {
    function accountLink(root: HTMLElement = element): HTMLAnchorElement {
      return root.querySelector('[data-testid="account-link"]') as HTMLAnchorElement;
    }

    it('reads the session once, and once only, however many times it renders', async () => {
      // `beforeEach` has already rendered and answered. Three more renders must ask nothing more:
      // `AccountStore.load` is single-flight, and the guard on an account route shares the same answer.
      await settle();
      await settle();
      await settle();

      expect(
        TestBed.inject(HttpTestingController).match(
          (request) => request.url === `${AccountApi.url}/session`,
        ),
      ).toEqual([]);
    });

    it('offers Account and no greeting while nobody is signed in', () => {
      expect(accountLink().textContent?.trim()).toBe('Account');
      expect(element.querySelector('[data-testid="account-greeting"]')).toBeNull();
      expect(element.querySelector('[data-testid="sign-out"]')).toBeNull();
    });

    /**
     * **The link's address does not change, and that is the point of the account routes' redirect.**
     * `/account` serves a signed-in shopper their orders and sends a signed-out one to the sign-in form
     * with `returnTo`, so this one anchor is correct in both states and the header does not have to know
     * which it is in.
     */
    it('keeps the /account link whatever the session says', async () => {
      expect(accountLink().getAttribute('href')).toBe('/account');

      const signedIn = await rebuild(
        (http) =>
          http
            .match((request) => request.url === '/api/site/settings')
            .forEach((request) => request.flush(NO_SETTINGS)),
        CUSTOMER,
      );

      expect(accountLink(signedIn).getAttribute('href')).toBe('/account');
    });

    it('greets the customer by name and offers Sign out once the session answers', async () => {
      const signedIn = await rebuild(
        (http) =>
          http
            .match((request) => request.url === '/api/site/settings')
            .forEach((request) => request.flush(NO_SETTINGS)),
        CUSTOMER,
      );

      expect(signedIn.querySelector('[data-testid="account-greeting"]')?.textContent?.trim()).toBe(
        'Nimal',
      );
      expect(signedIn.querySelector('[data-testid="sign-out"]')).not.toBeNull();
    });

    /**
     * The icon is the visible control, so the *name* a screen reader reads is the only place the account
     * link can say who is signed in. "Account" told a screen-reader user nothing about that.
     *
     * The greeting beside it is `aria-hidden`, so the name is announced once rather than twice.
     */
    it('names the customer in the account link, and only once', async () => {
      const signedIn = await rebuild(
        (http) =>
          http
            .match((request) => request.url === '/api/site/settings')
            .forEach((request) => request.flush(NO_SETTINGS)),
        CUSTOMER,
      );

      expect(accountLink(signedIn).textContent).toContain('Your account, Nimal');
      expect(
        signedIn.querySelector('[data-testid="account-greeting"]')?.getAttribute('aria-hidden'),
      ).toBe('true');
    });

    /**
     * Doc 11 P11 step B.7: the cookie goes and **the browser's guest bag is not restored**. The bag was
     * merged into the account bag at sign-in, so after signing out there is genuinely nothing in it —
     * and the badge beside the button has to be told, which is what the re-read is for.
     */
    it('signs out, re-reads the bag and goes to the home page', async () => {
      const signedIn = await rebuild(
        (http) =>
          http
            .match((request) => request.url === '/api/site/settings')
            .forEach((request) => request.flush(NO_SETTINGS)),
        CUSTOMER,
      );

      (signedIn.querySelector('[data-testid="sign-out"]') as HTMLButtonElement).click();

      const http = TestBed.inject(HttpTestingController);
      http
        .expectOne(
          (request) => request.url === `${AccountApi.url}/logout` && request.method === 'POST',
        )
        .flush(null, { status: 204, statusText: 'No Content' });

      // The bag is re-read on the way out.
      http
        .match((request) => request.url === CartApi.url && request.method === 'GET')
        .forEach((request) => request.flush(EMPTY_BAG));

      expect(TestBed.inject(Router).url).toBe('/');
    });

    /**
     * **No `disabled` on the sign-out button** (#109): disabling the control that currently has focus
     * throws focus to `<body>` and fires no `focusin`, so the press would throw the keyboard shopper's
     * place away. The button is removed by its own success instead, which is the same defect by the
     * other route — so focus is sent to the wordmark, the one control certainly on the page the shopper
     * is about to be looking at.
     */
    it('leaves focus on a real control after the button that was pressed is destroyed', async () => {
      const signedIn = await rebuild(
        (http) =>
          http
            .match((request) => request.url === '/api/site/settings')
            .forEach((request) => request.flush(NO_SETTINGS)),
        CUSTOMER,
      );

      const button = signedIn.querySelector('[data-testid="sign-out"]') as HTMLButtonElement;
      expect(button.hasAttribute('disabled')).toBe(false);

      button.focus();
      button.click();

      const http = TestBed.inject(HttpTestingController);
      http
        .expectOne(
          (request) => request.url === `${AccountApi.url}/logout` && request.method === 'POST',
        )
        .flush(null, { status: 204, statusText: 'No Content' });
      http
        .match((request) => request.url === CartApi.url && request.method === 'GET')
        .forEach((request) => request.flush(EMPTY_BAG));

      await TestBed.inject(ApplicationRef).whenStable();

      expect(signedIn.querySelector('[data-testid="sign-out"]')).toBeNull();
      expect(document.activeElement).toBe(signedIn.querySelector('[data-testid="wordmark"]'));
      expect(document.activeElement).not.toBe(document.body);
    });
  });
});
