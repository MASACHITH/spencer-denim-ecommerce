import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';

import { App } from './app';
import { routes } from './app.routes';

describe('Storefront App', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [App],
      providers: [provideRouter(routes), provideHttpClient(), provideHttpClientTesting()],
    });
  });

  it('renders the Spencer Denim wordmark', () => {
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();

    const wordmark = (fixture.nativeElement as HTMLElement).querySelector('.wordmark');
    expect(wordmark?.textContent?.trim()).toBe('Spencer Denim');
  });

  /**
   * The scaffold's `/api/ping` wiring check lived on the placeholder home page and said so: "kept
   * out of the way until HOME-01 replaces this page". HOME-01..10 has replaced it, and the home
   * page now asks the content service for the sections the client arranged.
   */
  it('the home page names the store and asks the content service for its sections', async () => {
    const harness = await RouterTestingHarness.create('/');
    const controller = TestBed.inject(HttpTestingController);

    expect(harness.routeNativeElement?.querySelector('h1')?.textContent).toContain('Spencer Denim');

    controller.expectOne('/api/content/home').flush({
      sections: [
        {
          id: 9,
          type: 'Benefits',
          title: null,
          subtitle: null,
          sortOrder: 9,
          linkUrl: null,
          linkLabel: null,
          overlay: null,
          items: [
            {
              id: 1,
              title: 'Island-wide delivery',
              subtitle: null,
              body: 'We deliver across Sri Lanka.',
              buttonLabel: null,
              linkUrl: null,
              imageUrl: null,
              imageAltText: null,
              mobileImageUrl: null,
              mobileImageAltText: null,
              videoUrl: null,
              videoPosterUrl: null,
              iconKey: 'delivery',
              departmentSlug: null,
              departmentName: null,
              productSlug: null,
              sortOrder: 1,
            },
          ],
          products: [],
        },
      ],
    });

    await harness.fixture.whenStable();
    harness.detectChanges();

    expect(harness.routeNativeElement?.textContent).toContain('Island-wide delivery');

    // The department feed is the shell's, not this page's; it is not rendered by the harness.
    controller.match('/api/catalog/departments').forEach((request) => request.flush([]));
    controller.verify();
  });
});
