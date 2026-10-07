import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { AdminContentApi } from '../../core/content/admin-content-api';
import { AdminContentPageListItem } from '../../core/content/admin-content.model';
import { AdminAuthApi } from '../../core/auth/admin-auth-api';
import { ContentPagesPage } from './content-pages-page';

/**
 * The content pages screen (ADM-17, GLB-10).
 *
 * **What this file is for is the things the brief names as repeat defects in this project**: a control
 * that disables itself under the user's finger (#109), a row destroyed with focus in it, and a Remove
 * button offered for a page that cannot be removed. Each is asserted on the rendered DOM rather than
 * reasoned about.
 */
describe('ContentPagesPage (ADM-17, GLB-10)', () => {
  let controller: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [ContentPagesPage],
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });

    controller = TestBed.inject(HttpTestingController);
  });

  afterEach(() => controller.verify());

  function page(overrides: Partial<AdminContentPageListItem> = {}): AdminContentPageListItem {
    return {
      id: 4,
      systemKey: 'returns',
      slug: 'returns',
      title: 'Returns and exchanges',
      isPublished: false,
      hasBody: false,
      imagesMissingAltText: 0,
      publishedAtUtc: null,
      updatedAtUtc: null,
      canBeDeleted: false,
      rowVersion: 'row-version',
      ...overrides,
    };
  }

  function create(pages: AdminContentPageListItem[]): ComponentFixture<ContentPagesPage> {
    const fixture = TestBed.createComponent(ContentPagesPage);
    fixture.detectChanges();

    controller.expectOne(AdminContentApi.pagesUrl).flush(pages);
    fixture.detectChanges();

    return fixture;
  }

  function text(fixture: ComponentFixture<ContentPagesPage>): string {
    return (fixture.nativeElement as HTMLElement).textContent ?? '';
  }

  function grantToken(): void {
    controller.expectOne(AdminAuthApi.antiforgeryUrl).flush({ token: 'xsrf-token' });
  }

  /**
   * **The screen's first job on a fresh shop.** All six pages arrive unwritten — doc 11 P13: the copy
   * is the client's — so the list has to say which are waiting for their words rather than looking like
   * six broken rows.
   */
  it('says how many pages are still waiting for the client’s copy', () => {
    const fixture = create([
      page({ id: 1, systemKey: 'about', slug: 'about', title: 'About us' }),
      page({ id: 2, systemKey: 'returns', slug: 'returns' }),
      page({ id: 3, systemKey: 'contact', slug: 'contact', title: 'Contact us' }),
    ]);

    // Two, not three: the Contact page needs no copy at all, because its content is the form and the
    // Settings details — so counting it would tell the client they have work they do not have.
    expect(text(fixture)).toContain('2 still waiting for your copy');
    expect(text(fixture)).toContain('no copy yet');
  });

  /**
   * And it says the one thing a client could not work out for themselves: Contact can go on the shop
   * immediately. Without it they reasonably assume all six are blocked on them, and the shop opens with
   * no way to send an enquiry except WhatsApp.
   */
  it('says the Contact page needs no writing from the client', () => {
    const fixture = create([page()]);

    expect(text(fixture)).toContain('Contact');
    expect(text(fixture)).toContain('needs no writing from you');
  });

  /**
   * Doc 11 P13: "System pages cannot be deleted." The button is not drawn at all, and the flag comes
   * from the payload rather than being inferred from `systemKey` on this side — so the screen and the
   * API cannot disagree about the rule.
   */
  it('offers no Remove button for a page that cannot be deleted', () => {
    const fixture = create([page({ canBeDeleted: false })]);
    const root = fixture.nativeElement as HTMLElement;

    expect(root.querySelector('[data-testid="remove-4"]')).toBeNull();
    expect(root.querySelector('[data-testid="publish-4"]')).not.toBeNull();
  });

  it('offers Remove for a custom page', () => {
    const fixture = create([page({ id: 9, systemKey: null, canBeDeleted: true, slug: 'size-guide' })]);

    expect((fixture.nativeElement as HTMLElement).querySelector('[data-testid="remove-9"]')).not.toBeNull();
  });

  /**
   * **`aria-disabled`, never `disabled`** (#109). Disabling the control that has focus moves focus to
   * `<body>`, which is six recorded instances in this project. Playwright's `toBeDisabled()` honours
   * `aria-disabled`, so the attribute is what assistive technology and the browser suite both read —
   * and the handler refuses the second activation, which is what makes it honest rather than decorative.
   */
  it('marks the publish button aria-disabled while busy and never disabled', () => {
    const fixture = create([page()]);
    const root = fixture.nativeElement as HTMLElement;
    const button = root.querySelector('[data-testid="publish-4"]') as HTMLButtonElement;

    button.click();
    fixture.detectChanges();

    expect(button.getAttribute('aria-disabled')).toBe('true');
    expect(button.disabled).toBe(false);

    grantToken();
    controller.expectOne(`${AdminContentApi.pagesUrl}/4/published`).flush(null);
    controller.expectOne(AdminContentApi.pagesUrl).flush([page({ isPublished: true })]);
    fixture.detectChanges();

    expect(button.getAttribute('aria-disabled')).toBeNull();
  });

  /** And a second press while the first is in flight is refused rather than sent. */
  it('refuses a second press while the first is still in flight', () => {
    const fixture = create([page()]);
    const button = (fixture.nativeElement as HTMLElement).querySelector(
      '[data-testid="publish-4"]',
    ) as HTMLButtonElement;

    button.click();
    fixture.detectChanges();
    button.click();

    // One antiforgery fetch and one publish, not two of each. `verify()` in afterEach is the other half
    // of this assertion: a second request nobody answered would fail the test.
    grantToken();
    controller.expectOne(`${AdminContentApi.pagesUrl}/4/published`).flush(null);
    controller.expectOne(AdminContentApi.pagesUrl).flush([page({ isPublished: true })]);
  });

  /**
   * The live region says a working message on the way in and the outcome on the way out, so a second
   * identical action still speaks — a live region announces a *change*, and setting it to the text it
   * already holds is not one (ADM-22).
   */
  it('announces the start and the end of a publish, so a repeated one speaks twice', () => {
    const fixture = create([page()]);
    const root = fixture.nativeElement as HTMLElement;

    function said(): string {
      return root.querySelector('[data-testid="content-status"]')?.textContent?.trim() ?? '';
    }

    (root.querySelector('[data-testid="publish-4"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(said()).toContain('Putting the page on the shop');

    grantToken();
    controller.expectOne(`${AdminContentApi.pagesUrl}/4/published`).flush(null);
    controller.expectOne(AdminContentApi.pagesUrl).flush([page({ isPublished: true })]);
    fixture.detectChanges();

    expect(said()).toContain('is on the shop');
  });

  /**
   * A refusal is said in the live region as well as shown in the banner: a screen-reader user who
   * pressed a button needs to hear that it did not work, and the banner is above the table they are in.
   */
  it('says a refusal in the live region, not only in the banner', () => {
    const fixture = create([page({ imagesMissingAltText: 1 })]);
    const root = fixture.nativeElement as HTMLElement;

    (root.querySelector('[data-testid="publish-4"]') as HTMLButtonElement).click();
    fixture.detectChanges();

    grantToken();
    controller.expectOne(`${AdminContentApi.pagesUrl}/4/published`).flush(
      {
        title: 'Cannot be applied',
        detail: 'This page holds an image with no alt text, so it cannot go on the shop yet.',
        code: 'alt_text_required',
        imagesMissingAltText: 1,
      },
      { status: 409, statusText: 'Conflict' },
    );
    fixture.detectChanges();

    const status = root.querySelector('[data-testid="content-status"]')?.textContent ?? '';
    expect(status).toContain('alt text');
    expect(root.querySelector('[role="alert"]')?.textContent).toContain('alt text');
  });

  /**
   * Doc 08 §3.3 and GLB-12, surfaced before the staff member tries: the list says how many images need
   * alt text, so a refused publish is not the first they hear of it.
   */
  it('warns on the list when an image has no alt text', () => {
    const fixture = create([page({ imagesMissingAltText: 2, hasBody: true })]);

    expect(text(fixture)).toContain('2 image(s) need alt text');
  });

  /**
   * **Removing a row re-homes focus**, because the row holds the button being pressed. Destroying the
   * focused node drops focus to `<body>` — the same harm as `[disabled]` from the other direction, and
   * the rule the project records as necessary and not sufficient.
   */
  it('moves focus out of a row it is about to destroy', async () => {
    const fixture = create([page({ id: 9, systemKey: null, canBeDeleted: true, slug: 'size-guide' })]);
    const root = fixture.nativeElement as HTMLElement;

    const remove = root.querySelector('[data-testid="remove-9"]') as HTMLButtonElement;
    remove.focus();
    expect(document.activeElement).toBe(remove);

    remove.click();
    fixture.detectChanges();

    grantToken();
    controller.expectOne(`${AdminContentApi.pagesUrl}/9`).flush(null);
    controller.expectOne(AdminContentApi.pagesUrl).flush([]);
    fixture.detectChanges();

    // The focus move is scheduled after the view catches up.
    await Promise.resolve();
    await new Promise((resolve) => setTimeout(resolve, 0));

    expect(document.activeElement?.tagName).not.toBe('BODY');
    expect(document.activeElement?.tagName).toBe('CAPTION');
  });
});
