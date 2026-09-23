import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { SiteHeader } from '../layout/site-header/site-header';
import { LocalWishlist } from './wishlist/local-wishlist.service';
import { StorefrontCounts } from './storefront-counts.service';

@Component({
  imports: [SiteHeader],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<sd-site-header />`,
})
class HeaderHost {
  readonly wishlist = inject(LocalWishlist);
}

/**
 * The heart on a product card has to reach the header (GLB-04). It saved the product and left the
 * header reading "Wishlist, 0 items", which tells a shopper their tap did not work.
 */
describe('StorefrontCounts and the wishlist', () => {
  beforeEach(() => localStorage.clear());

  async function render() {
    TestBed.configureTestingModule({
      imports: [HeaderHost],
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });

    const fixture = TestBed.createComponent(HeaderHost);
    fixture.detectChanges();
    await fixture.whenStable();

    return {
      fixture,
      host: fixture.componentInstance,
      element: fixture.nativeElement as HTMLElement,
      settle: async () => {
        fixture.detectChanges();
        await fixture.whenStable();
      },
    };
  }

  it('starts at nothing saved', async () => {
    const { element } = await render();

    expect(element.textContent).toContain('Wishlist, 0 items');
    expect(element.querySelector('.count')).toBeNull();
  });

  it('counts what the product card saved', async () => {
    const { host, element, settle } = await render();

    host.wishlist.toggle('linen-wrap-midi-dress');
    host.wishlist.toggle('relaxed-straight-jeans');
    await settle();

    expect(element.textContent).toContain('Wishlist, 2 items');
    expect(element.querySelector('.count')?.textContent?.trim()).toBe('2');
  });

  it('counts back down when a product is unsaved', async () => {
    const { host, element, settle } = await render();

    host.wishlist.toggle('linen-wrap-midi-dress');
    await settle();
    host.wishlist.toggle('linen-wrap-midi-dress');
    await settle();

    expect(element.textContent).toContain('Wishlist, 0 items');
    expect(element.querySelector('.count')).toBeNull();
  });

  it('leaves the bag alone: nothing owns that number yet (BAG-01)', async () => {
    await render();

    expect(TestBed.inject(StorefrontCounts).bag()).toBe(0);
  });
});
