import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { AuditLogApi, toAuditLogFailure } from './audit-log-api';
import { emptyFilters } from './audit-log.model';

describe('AuditLogApi', () => {
  let api: AuditLogApi;
  let controller: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    api = TestBed.inject(AuditLogApi);
    controller = TestBed.inject(HttpTestingController);
  });

  afterEach(() => controller.verify());

  it('requests the page and size with no filter parameters when none are set', () => {
    api.list(emptyFilters(), 1, 25).subscribe();

    const request = controller.expectOne(
      (r) => r.url === AuditLogApi.url,
    );
    expect(request.request.params.get('page')).toBe('1');
    expect(request.request.params.get('pageSize')).toBe('25');
    expect(request.request.params.has('entityType')).toBe(false);
    expect(request.request.params.has('entityId')).toBe(false);
    expect(request.request.params.has('userId')).toBe(false);
    expect(request.request.params.has('action')).toBe(false);
    expect(request.request.params.has('fromUtc')).toBe(false);
    expect(request.request.params.has('toUtc')).toBe(false);
    request.flush({ items: [], page: 1, pageSize: 25, totalCount: 0 });
  });

  it('sends trimmed, non-blank filters as query parameters', () => {
    api
      .list(
        {
          entityType: '  Product  ',
          entityId: '42',
          userId: '7',
          action: 'Update',
          fromUtc: '2026-09-01',
          toUtc: '',
        },
        2,
        50,
      )
      .subscribe();

    const request = controller.expectOne((r) => r.url === AuditLogApi.url);
    expect(request.request.params.get('entityType')).toBe('Product');
    expect(request.request.params.get('entityId')).toBe('42');
    expect(request.request.params.get('userId')).toBe('7');
    expect(request.request.params.get('action')).toBe('Update');
    expect(request.request.params.get('fromUtc')).toBe('2026-09-01');
    expect(request.request.params.has('toUtc')).toBe(false);
    expect(request.request.params.get('page')).toBe('2');
    expect(request.request.params.get('pageSize')).toBe('50');
    request.flush({ items: [], page: 2, pageSize: 50, totalCount: 0 });
  });

  it('returns the page from the server on success', () => {
    const page = {
      items: [
        {
          id: 1,
          occurredAtUtc: '2026-09-30T12:00:00Z',
          userId: 5,
          userEmail: 'staff@example.com',
          action: 'Update',
          entityType: 'Product',
          entityId: '42',
          beforeJson: null,
          afterJson: null,
          ipAddress: '203.0.113.1',
          userAgent: 'test-agent',
          correlationId: 'corr-1',
        },
      ],
      page: 1,
      pageSize: 25,
      totalCount: 1,
    };
    let result: unknown;

    api.list(emptyFilters(), 1, 25).subscribe((value) => (result = value));

    controller.expectOne((r) => r.url === AuditLogApi.url).flush(page);

    expect(result).toEqual(page);
  });
});

describe('toAuditLogFailure', () => {
  it('maps a 403 to forbidden', () => {
    const failure = toAuditLogFailure(fakeHttpError(403, null));
    expect(failure.kind).toBe('forbidden');
  });

  it('maps a 400 with a validation problem body to validation, keeping the field errors', () => {
    const failure = toAuditLogFailure(
      fakeHttpError(400, { title: 'One or more query parameters are invalid.', errors: { userId: ['userId must be a whole number between 1 and 2147483647.'] } }),
    );
    expect(failure.kind).toBe('validation');
    expect(failure.fieldErrors).toEqual({ userId: ['userId must be a whole number between 1 and 2147483647.'] });
  });

  it('maps a status-0 network failure to offline', () => {
    const failure = toAuditLogFailure(fakeHttpError(0, null));
    expect(failure.kind).toBe('offline');
  });

  it('maps a 500 to unknown', () => {
    const failure = toAuditLogFailure(fakeHttpError(500, null));
    expect(failure.kind).toBe('unknown');
  });

  it('maps a non-HTTP error to unknown', () => {
    const failure = toAuditLogFailure(new Error('boom'));
    expect(failure.kind).toBe('unknown');
  });
});

function fakeHttpError(status: number, body: unknown): HttpErrorResponse {
  return new HttpErrorResponse({ status, error: body });
}
