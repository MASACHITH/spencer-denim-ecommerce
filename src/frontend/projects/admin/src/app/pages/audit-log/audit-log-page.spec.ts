import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { AuditLogApi } from '../../core/audit-log/audit-log-api';
import { AuditLogEntry } from '../../core/audit-log/audit-log.model';
import { AuditLogPage } from './audit-log-page';

describe('AuditLogPage', () => {
  let controller: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [AuditLogPage],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    controller = TestBed.inject(HttpTestingController);
  });

  afterEach(() => controller.verify());

  function create(): ComponentFixture<AuditLogPage> {
    const fixture = TestBed.createComponent(AuditLogPage);
    fixture.detectChanges();
    return fixture;
  }

  function entry(overrides: Partial<AuditLogEntry> = {}): AuditLogEntry {
    return {
      id: 1,
      occurredAtUtc: '2026-09-30T12:00:00Z',
      userId: 5,
      userEmail: 'staff@example.com',
      action: 'Update',
      entityType: 'Product',
      entityId: '42',
      beforeJson: '{"name":"Old"}',
      afterJson: '{"name":"New"}',
      ipAddress: '203.0.113.1',
      userAgent: 'test-agent',
      correlationId: 'corr-1',
      ...overrides,
    };
  }

  it('requests the first page with no filters on load', () => {
    create();

    const request = controller.expectOne((r) => r.url === AuditLogApi.url);
    expect(request.request.params.get('page')).toBe('1');
    expect(request.request.params.get('pageSize')).toBe('25');
    request.flush({ items: [], page: 1, pageSize: 25, totalCount: 0 });
  });

  it('shows a loading state before the response arrives', () => {
    const fixture = create();

    expect((fixture.nativeElement as HTMLElement).textContent).toContain('Loading audit log');
    controller.expectOne((r) => r.url === AuditLogApi.url).flush({ items: [], page: 1, pageSize: 25, totalCount: 0 });
  });

  it('renders one row per entry with its action, entity and user', () => {
    const fixture = create();
    controller
      .expectOne((r) => r.url === AuditLogApi.url)
      .flush({ items: [entry()], page: 1, pageSize: 25, totalCount: 1 });
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    const rows = root.querySelectorAll('tbody tr');
    expect(rows.length).toBe(1);
    expect(root.textContent).toContain('staff@example.com');
    expect(root.textContent).toContain('Update');
    expect(root.textContent).toContain('Product');
    expect(root.textContent).toContain('42');
  });

  it('shows "System" for a row with no acting user', () => {
    const fixture = create();
    controller
      .expectOne((r) => r.url === AuditLogApi.url)
      .flush({ items: [entry({ userEmail: null })], page: 1, pageSize: 25, totalCount: 1 });
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).textContent).toContain('System');
  });

  it('shows the empty state when no rows match', () => {
    const fixture = create();
    controller.expectOne((r) => r.url === AuditLogApi.url).flush({ items: [], page: 1, pageSize: 25, totalCount: 0 });
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).textContent).toContain('No audit log entries match');
  });

  it('shows a forbidden message and no retry button for a 403', () => {
    const fixture = create();
    controller
      .expectOne((r) => r.url === AuditLogApi.url)
      .flush({ error: 'forbidden' }, { status: 403, statusText: 'Forbidden' });
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    expect(root.textContent).toContain('do not have permission');
    expect(root.querySelector('.audit__state--error button')).toBeNull();
  });

  it('shows a retry button for a server error', () => {
    const fixture = create();
    controller.expectOne((r) => r.url === AuditLogApi.url).flush(null, { status: 500, statusText: 'Error' });
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('.audit__state--error button')).not.toBeNull();
  });

  it('shows field-specific validation errors next to the offending filter', () => {
    const fixture = create();
    controller.expectOne((r) => r.url === AuditLogApi.url).flush(
      { title: 'One or more query parameters are invalid.', errors: { userId: ['userId must be a whole number between 1 and 2147483647.'] } },
      { status: 400, statusText: 'Bad Request' },
    );
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('#audit-error-userId')?.textContent).toContain('userId must be a whole number');
  });

  it('toggles the before/after details panel for a row', () => {
    const fixture = create();
    controller
      .expectOne((r) => r.url === AuditLogApi.url)
      .flush({ items: [entry()], page: 1, pageSize: 25, totalCount: 1 });
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    const toggle = root.querySelector('.audit__toggle') as HTMLButtonElement;
    expect(toggle.getAttribute('aria-expanded')).toBe('false');
    expect(root.querySelector('.audit__details')).toBeNull();

    toggle.click();
    fixture.detectChanges();

    expect(toggle.getAttribute('aria-expanded')).toBe('true');
    expect(root.querySelector('.audit__json')?.textContent).toContain('"name":"Old"');

    toggle.click();
    fixture.detectChanges();

    expect(root.querySelector('.audit__details')).toBeNull();
  });

  it('re-requests page 1 with the new filters when "Apply filters" is used', () => {
    const fixture = create();
    controller.expectOne((r) => r.url === AuditLogApi.url).flush({ items: [], page: 1, pageSize: 25, totalCount: 0 });
    fixture.detectChanges();

    fixture.componentInstance['updateDraft']('entityType', 'Product');
    fixture.componentInstance['applyFilters']();

    const request = controller.expectOne((r) => r.url === AuditLogApi.url);
    expect(request.request.params.get('entityType')).toBe('Product');
    expect(request.request.params.get('page')).toBe('1');
    request.flush({ items: [], page: 1, pageSize: 25, totalCount: 0 });
  });

  it('clearing filters resets the draft and reloads unfiltered', () => {
    const fixture = create();
    controller.expectOne((r) => r.url === AuditLogApi.url).flush({ items: [], page: 1, pageSize: 25, totalCount: 0 });
    fixture.detectChanges();

    fixture.componentInstance['updateDraft']('entityType', 'Product');
    fixture.componentInstance['applyFilters']();
    controller.expectOne((r) => r.url === AuditLogApi.url).flush({ items: [], page: 1, pageSize: 25, totalCount: 0 });

    fixture.componentInstance['clearFilters']();
    const request = controller.expectOne((r) => r.url === AuditLogApi.url);
    expect(request.request.params.has('entityType')).toBe(false);
    expect(fixture.componentInstance['draft']().entityType).toBe('');
    request.flush({ items: [], page: 1, pageSize: 25, totalCount: 0 });
  });

  it('moves to the next and previous page, disabling the buttons at the edges', () => {
    const fixture = create();
    controller
      .expectOne((r) => r.url === AuditLogApi.url)
      .flush({ items: [entry()], page: 1, pageSize: 25, totalCount: 50 });
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    const [previous, next] = Array.from(root.querySelectorAll('.audit__pagination button')) as HTMLButtonElement[];
    expect(previous.disabled).toBe(true);
    expect(next.disabled).toBe(false);

    next.click();
    const secondPageRequest = controller.expectOne((r) => r.url === AuditLogApi.url);
    expect(secondPageRequest.request.params.get('page')).toBe('2');
    secondPageRequest.flush({ items: [entry()], page: 2, pageSize: 25, totalCount: 50 });
    fixture.detectChanges();

    expect(previous.disabled).toBe(false);
    expect(next.disabled).toBe(true);
  });
});
