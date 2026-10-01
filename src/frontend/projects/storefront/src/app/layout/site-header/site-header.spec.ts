import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { SiteHeader } from './site-header';

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
      providers: [provideRouter([])],
    });

    fixture = TestBed.createComponent(SiteHeader);
    element = fixture.nativeElement as HTMLElement;
    await settle();
  });

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
});
