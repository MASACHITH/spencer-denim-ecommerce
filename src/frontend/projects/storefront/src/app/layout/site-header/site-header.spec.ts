import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { SiteHeader } from './site-header';
import { CartApi } from '../../core/cart/cart-api';
import { CartDto } from '../../core/cart/cart.model';
import { CartStore } from '../../core/cart/cart.store';

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
   * Answers everything the shell asks for on its own behalf: the announcement strip (GLB-03), the
   * department tree (GLB-05) and the bag count (GLB-04). They were all going to the real backend before
   * this spec had an HTTP client of its own, which is to say they were uncontrolled requests in a jsdom
   * test; `verify()` in `afterEach` only means something once they are answered here.
   */
  function flushShell(cart: CartDto = EMPTY_BAG): void {
    const http = TestBed.inject(HttpTestingController);
    http.match((request) => request.url === '/api/content/announcements').forEach((request) => request.flush([]));
    http.match((request) => request.url === '/api/catalog/departments').forEach((request) => request.flush([]));
    flushBag(cart);
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
});
