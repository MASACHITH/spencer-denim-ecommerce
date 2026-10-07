import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { AdminContentApi } from '../../core/content/admin-content-api';
import {
  AdminContactEnquiry,
  AdminContactEnquiryListItem,
} from '../../core/content/admin-content.model';
import { AdminAuthApi } from '../../core/auth/admin-auth-api';
import { EnquiriesPage } from './enquiries-page';

/**
 * The contact-enquiry inbox (GLB-10, doc 11 P15 step 7).
 *
 * **The assertions that matter here are about what the screen does *not* do**: it does not render a
 * visitor's message as markup, it does not offer a reply box for a reply nothing can send, it does not
 * draw a blank where an IP has been purged, and it does not drop focus when it destroys a row. Each is
 * a thing a reasonable change would introduce.
 */
describe('EnquiriesPage (GLB-10)', () => {
  let controller: HttpTestingController;

  const INBOX = AdminContentApi.enquiriesUrl;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [EnquiriesPage],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });

    controller = TestBed.inject(HttpTestingController);
  });

  afterEach(() => controller.verify());

  function item(overrides: Partial<AdminContactEnquiryListItem> = {}): AdminContactEnquiryListItem {
    return {
      id: 7,
      name: 'Nimal Perera',
      email: 'nimal@example.com',
      subject: 'A question about sizing',
      preview: 'Is the linen shirt true to size?',
      orderNumber: null,
      status: 'New',
      handledByUserEmail: null,
      handledAtUtc: null,
      createdAtUtc: '2026-10-07T09:30:00Z',
      ...overrides,
    };
  }

  function detail(overrides: Partial<AdminContactEnquiry> = {}): AdminContactEnquiry {
    return {
      id: 7,
      name: 'Nimal Perera',
      email: 'nimal@example.com',
      phone: null,
      subject: 'A question about sizing',
      message: 'Is the linen shirt true to size?',
      orderNumber: null,
      status: 'New',
      handledByUserEmail: null,
      handledAtUtc: null,
      ipAddress: '203.0.113.5',
      createdAtUtc: '2026-10-07T09:30:00Z',
      ...overrides,
    };
  }

  /** The inbox fetch the component makes from its constructor. */
  function listUrl(): (request: { url: string }) => boolean {
    return (request) => request.url.startsWith(INBOX);
  }

  function create(
    items: AdminContactEnquiryListItem[],
    counts: { total?: number; newCount?: number } = {},
  ): ComponentFixture<EnquiriesPage> {
    const fixture = TestBed.createComponent(EnquiriesPage);
    fixture.detectChanges();

    controller.expectOne(listUrl()).flush({
      items,
      total: counts.total ?? items.length,
      newCount: counts.newCount ?? items.filter((row) => row.status === 'New').length,
    });
    fixture.detectChanges();

    return fixture;
  }

  function root(fixture: ComponentFixture<EnquiriesPage>): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  function grantToken(): void {
    controller.expectOne(AdminAuthApi.antiforgeryUrl).flush({ token: 'xsrf-token' });
  }

  /** Opens enquiry 7 and answers both calls the screen makes. */
  function open(fixture: ComponentFixture<EnquiriesPage>, enquiry = detail()): void {
    (root(fixture).querySelector('[data-testid="open-7"]') as HTMLButtonElement).click();
    fixture.detectChanges();

    controller.expectOne(`${INBOX}/7`).flush(enquiry);
    fixture.detectChanges();

    grantToken();
    controller.expectOne(`${INBOX}/7/read`).flush(null);
    controller.expectOne(listUrl()).flush({ items: [item({ status: 'Read' })], total: 1, newCount: 0 });
    fixture.detectChanges();
  }

  it('lists an enquiry with its preview and says how many are unread', () => {
    const fixture = create([item()], { total: 1, newCount: 1 });

    expect(root(fixture).textContent).toContain('Nimal Perera');
    expect(root(fixture).textContent).toContain('Is the linen shirt true to size?');
    expect(root(fixture).textContent).toContain('1 unread of 1');
  });

  it('says so when nothing has arrived, rather than drawing an empty list', () => {
    const fixture = create([]);

    expect(root(fixture).querySelector('[data-testid="enquiries-empty"]')).not.toBeNull();
  });

  /**
   * **The message is rendered as text, never as markup.**
   *
   * It is a visitor's free text on an authenticated screen, so binding it as HTML would be a stored-XSS
   * surface aimed at staff — the mirror image of the one the content-page sanitiser closes. The payload
   * below renders as characters; if it were ever bound with `[innerHTML]`, the `<b>` would become an
   * element and this goes red.
   */
  it('renders the visitor’s message as text, never as markup', () => {
    const fixture = create([item()]);
    open(fixture, detail({ message: '<b>bold</b><img src=x onerror="alert(1)">' }));

    const message = root(fixture).querySelector('[data-testid="enquiry-message"]') as HTMLElement;

    expect(message.textContent).toContain('<b>bold</b>');
    expect(message.querySelector('b')).toBeNull();
    expect(message.querySelector('img')).toBeNull();
  });

  /**
   * Doc 11 P15: replying happens outside the system, by email or telephone, and doc 11's notifications
   * for v1 are none. So there is no reply box, and nothing on the screen promises an email — which is
   * the defect checkout was marked Major for.
   */
  it('offers no reply box and promises no email', () => {
    const fixture = create([item()]);
    open(fixture);

    const html = root(fixture);

    expect(html.querySelector('textarea')).toBeNull();
    expect(html.textContent?.toLowerCase()).not.toContain('send a reply');
    expect(html.textContent?.toLowerCase()).not.toContain('email the customer');
    expect(html.textContent).toContain('Reply by email or telephone');
  });

  /** Opening it marks it Read (doc 11 P15 step 7), through a POST rather than on the GET. */
  it('marks an enquiry read as it opens it', () => {
    const fixture = create([item()]);
    open(fixture);

    expect(root(fixture).textContent).toContain('Read');
  });

  /**
   * **A blank IP says why it might be blank.** Null means either that the server could not see an
   * address or that the ninety-day retention step has cleared it (doc 08 §8.4), and the screen cannot
   * tell the two apart — so it says so rather than drawing an empty field somebody reads as a bug.
   */
  it('says a missing address may have been cleared after ninety days', () => {
    const fixture = create([item()]);
    open(fixture, detail({ ipAddress: null }));

    expect(root(fixture).textContent).toContain('cleared after 90 days');
  });

  /** Marking Replied records who, and the status moves on the open row as well as in the list. */
  it('marks an enquiry Replied and shows who did it', () => {
    const fixture = create([item()]);
    open(fixture, detail({ status: 'Read' }));

    (root(fixture).querySelector('[data-testid="set-Replied-7"]') as HTMLButtonElement).click();
    fixture.detectChanges();

    grantToken();
    controller.expectOne(`${INBOX}/7/status`).flush(null);
    controller
      .expectOne(listUrl())
      .flush({
        items: [item({ status: 'Replied', handledByUserEmail: 'staff@example.com' })],
        total: 1,
        newCount: 0,
      });
    fixture.detectChanges();

    expect(root(fixture).textContent).toContain('Replied by staff@example.com');
  });

  /** The status it is already in is not offered, so a no-op press is not possible. */
  it('does not offer the status the enquiry is already in', () => {
    const fixture = create([item()]);
    open(fixture, detail({ status: 'Read' }));

    expect(root(fixture).querySelector('[data-testid="set-Read-7"]')).toBeNull();
    expect(root(fixture).querySelector('[data-testid="set-Replied-7"]')).not.toBeNull();
    expect(root(fixture).querySelector('[data-testid="set-Spam-7"]')).not.toBeNull();
  });

  /**
   * Doc 08 §3.7 requires the audit row to carry the staff member's reason, so the screen refuses to
   * send a deletion without one — and says so in the live region rather than silently doing nothing.
   */
  it('refuses to delete without a reason, and says why', () => {
    const fixture = create([item()]);
    open(fixture);

    (root(fixture).querySelector('[data-testid="delete-7"]') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(root(fixture).querySelector('[data-testid="enquiry-status"]')?.textContent).toContain(
      'Say why',
    );
  });

  /**
   * **Deleting re-homes focus**, because the row being removed holds the button that was pressed.
   * Destroying the focused node drops focus to `<body>` — the harm `[disabled]` causes from the other
   * direction, and the rule this project records as necessary and not sufficient.
   */
  it('moves focus out of the row it destroys', async () => {
    const fixture = create([item()]);
    open(fixture);

    const reason = root(fixture).querySelector('[data-testid="reason-7"]') as HTMLInputElement;
    reason.value = 'Contains card details';
    reason.dispatchEvent(new Event('input'));
    fixture.detectChanges();

    const remove = root(fixture).querySelector('[data-testid="delete-7"]') as HTMLButtonElement;
    remove.focus();
    expect(document.activeElement).toBe(remove);

    remove.click();
    fixture.detectChanges();

    grantToken();
    controller.expectOne(`${INBOX}/7/delete`).flush(null);
    controller.expectOne(listUrl()).flush({ items: [], total: 0, newCount: 0 });
    fixture.detectChanges();

    await Promise.resolve();
    await new Promise((resolve) => setTimeout(resolve, 0));

    expect(document.activeElement?.tagName).not.toBe('BODY');
    expect(document.activeElement?.getAttribute('data-testid')).toBe('enquiry-status');
  });

  /** The filter asks the API rather than filtering in the browser, so paging stays correct. */
  it('asks the API again when the filter changes', () => {
    const fixture = create([item()]);

    (root(fixture).querySelector('[data-testid="filter-Spam"]') as HTMLButtonElement).click();
    fixture.detectChanges();

    const request = controller.expectOne(listUrl());
    expect(request.request.urlWithParams).toContain('status=Spam');

    request.flush({ items: [], total: 0, newCount: 1 });
  });
});
