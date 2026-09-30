import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';

import { AuditLogApi, AuditLogFailure, toAuditLogFailure } from '../../core/audit-log/audit-log-api';
import { AUDIT_ACTIONS, AuditLogEntry, AuditLogFilters, emptyFilters } from '../../core/audit-log/audit-log.model';

const PAGE_SIZE = 25;

/**
 * ADM-03 audit log viewer (W1-09, issue #8). Read-only: staff with `AuditLog.View` browse
 * `ops.AuditLogs` here, filtered and paged. The API is the security boundary (doc 03 §3a) — a user
 * without the permission reaches this page (the route itself only requires a session, matching the
 * rest of the admin shell) and sees the 403 state below instead of the table.
 */
@Component({
  selector: 'sda-audit-log-page',
  imports: [FormsModule, DatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="audit">
      <h1 class="audit__title">Audit log</h1>

      <form class="audit__filters" (ngSubmit)="applyFilters()" novalidate aria-label="Filter the audit log">
        <label class="audit__field">
          <span class="audit__label">Entity type</span>
          <input
            class="audit__input"
            type="text"
            name="entityType"
            placeholder="e.g. Product"
            [attr.aria-invalid]="fieldError('entityType') ? 'true' : null"
            [attr.aria-describedby]="fieldError('entityType') ? 'audit-error-entityType' : null"
            [ngModel]="draft().entityType"
            (ngModelChange)="updateDraft('entityType', $event)"
          />
          @if (fieldError('entityType'); as message) {
            <span class="audit__field-error" id="audit-error-entityType">{{ message }}</span>
          }
        </label>

        <label class="audit__field">
          <span class="audit__label">Entity ID</span>
          <input
            class="audit__input"
            type="text"
            name="entityId"
            placeholder="e.g. 42"
            [attr.aria-invalid]="fieldError('entityId') ? 'true' : null"
            [attr.aria-describedby]="fieldError('entityId') ? 'audit-error-entityId' : null"
            [ngModel]="draft().entityId"
            (ngModelChange)="updateDraft('entityId', $event)"
          />
          @if (fieldError('entityId'); as message) {
            <span class="audit__field-error" id="audit-error-entityId">{{ message }}</span>
          }
        </label>

        <label class="audit__field">
          <span class="audit__label">User ID</span>
          <input
            class="audit__input"
            type="text"
            inputmode="numeric"
            name="userId"
            placeholder="e.g. 7"
            [attr.aria-invalid]="fieldError('userId') ? 'true' : null"
            [attr.aria-describedby]="fieldError('userId') ? 'audit-error-userId' : null"
            [ngModel]="draft().userId"
            (ngModelChange)="updateDraft('userId', $event)"
          />
          @if (fieldError('userId'); as message) {
            <span class="audit__field-error" id="audit-error-userId">{{ message }}</span>
          }
        </label>

        <label class="audit__field">
          <span class="audit__label">Action</span>
          <select
            class="audit__input"
            name="action"
            [ngModel]="draft().action"
            (ngModelChange)="updateDraft('action', $event)"
          >
            <option value="">All actions</option>
            @for (action of actions; track action) {
              <option [value]="action">{{ action }}</option>
            }
          </select>
        </label>

        <label class="audit__field">
          <span class="audit__label">From (UTC)</span>
          <input
            class="audit__input"
            type="date"
            name="fromUtc"
            [attr.aria-invalid]="fieldError('fromUtc') ? 'true' : null"
            [attr.aria-describedby]="fieldError('fromUtc') ? 'audit-error-fromUtc' : null"
            [ngModel]="draft().fromUtc"
            (ngModelChange)="updateDraft('fromUtc', $event)"
          />
          @if (fieldError('fromUtc'); as message) {
            <span class="audit__field-error" id="audit-error-fromUtc">{{ message }}</span>
          }
        </label>

        <label class="audit__field">
          <span class="audit__label">To (UTC)</span>
          <input
            class="audit__input"
            type="date"
            name="toUtc"
            [attr.aria-invalid]="fieldError('toUtc') ? 'true' : null"
            [attr.aria-describedby]="fieldError('toUtc') ? 'audit-error-toUtc' : null"
            [ngModel]="draft().toUtc"
            (ngModelChange)="updateDraft('toUtc', $event)"
          />
          @if (fieldError('toUtc'); as message) {
            <span class="audit__field-error" id="audit-error-toUtc">{{ message }}</span>
          }
        </label>

        <div class="audit__filter-actions">
          <button class="audit__submit" type="submit" [disabled]="loading()">Apply filters</button>
          <button class="audit__secondary" type="button" (click)="clearFilters()" [disabled]="loading()">
            Clear filters
          </button>
        </div>
      </form>

      <p class="sr-only" role="status">{{ announcement() }}</p>

      <div class="audit__results" [attr.aria-busy]="loading()">
        @if (failure(); as problem) {
          <div class="audit__state audit__state--error" role="alert">
            <p class="audit__state-title">We could not load the audit log</p>
            <p>{{ problem.message }}</p>
            @if (problem.kind !== 'forbidden') {
              <button class="audit__submit" type="button" (click)="retry()">Try again</button>
            }
          </div>
        } @else if (loading()) {
          <p class="audit__state">Loading audit log…</p>
        } @else if (entries().length === 0) {
          <p class="audit__state">No audit log entries match these filters.</p>
        } @else {
          <div class="audit__table-scroll" role="region" aria-label="Audit log entries" tabindex="0">
            <table class="audit__table">
              <caption class="sr-only">Audit log entries, newest first</caption>
              <thead>
                <tr>
                  <th scope="col">Occurred (UTC)</th>
                  <th scope="col">User</th>
                  <th scope="col">Action</th>
                  <th scope="col">Entity</th>
                  <th scope="col">IP address</th>
                  <th scope="col"><span class="sr-only">Details</span></th>
                </tr>
              </thead>
              <tbody>
                @for (entry of entries(); track entry.id) {
                  <tr>
                    <td>{{ entry.occurredAtUtc | date: 'medium' : 'UTC' }}</td>
                    <td>
                      @if (entry.userEmail) {
                        {{ entry.userEmail }}
                      } @else {
                        <span class="audit__muted">System</span>
                      }
                    </td>
                    <td>{{ entry.action }}</td>
                    <td>{{ entry.entityType }}@if (entry.entityId) { / {{ entry.entityId }} }</td>
                    <td>
                      @if (entry.ipAddress) {
                        {{ entry.ipAddress }}
                      } @else {
                        <span class="audit__muted">—</span>
                      }
                    </td>
                    <td>
                      <button
                        class="audit__toggle"
                        type="button"
                        [attr.aria-expanded]="isExpanded(entry.id)"
                        [attr.aria-controls]="'audit-details-' + entry.id"
                        [attr.aria-label]="(isExpanded(entry.id) ? 'Hide' : 'View') + ' details for entry ' + entry.id"
                        (click)="toggle(entry.id)"
                      >
                        {{ isExpanded(entry.id) ? 'Hide details' : 'View details' }}
                      </button>
                    </td>
                  </tr>
                  @if (isExpanded(entry.id)) {
                    <tr class="audit__details-row">
                      <td colspan="6">
                        <div class="audit__details" [id]="'audit-details-' + entry.id">
                          <div class="audit__details-block">
                            <span class="audit__details-heading" [id]="'audit-before-' + entry.id">Before</span>
                            <pre class="audit__json" [attr.aria-labelledby]="'audit-before-' + entry.id">{{ entry.beforeJson ?? '(none)' }}</pre>
                          </div>
                          <div class="audit__details-block">
                            <span class="audit__details-heading" [id]="'audit-after-' + entry.id">After</span>
                            <pre class="audit__json" [attr.aria-labelledby]="'audit-after-' + entry.id">{{ entry.afterJson ?? '(none)' }}</pre>
                          </div>
                          <dl class="audit__meta">
                            <dt>User agent</dt>
                            <dd>{{ entry.userAgent ?? '(none)' }}</dd>
                            <dt>Correlation ID</dt>
                            <dd>{{ entry.correlationId ?? '(none)' }}</dd>
                          </dl>
                        </div>
                      </td>
                    </tr>
                  }
                }
              </tbody>
            </table>
          </div>

          <nav class="audit__pagination" aria-label="Audit log pages">
            <button
              class="audit__secondary"
              type="button"
              (click)="previousPage()"
              [disabled]="loading() || page() <= 1"
            >
              Previous
            </button>
            <span class="audit__page-info">
              Page {{ page() }} of {{ totalPages() }} ({{ totalCount() }} total)
            </span>
            <button
              class="audit__secondary"
              type="button"
              (click)="nextPage()"
              [disabled]="loading() || page() >= totalPages()"
            >
              Next
            </button>
          </nav>
        }
      </div>
    </section>
  `,
  styles: `
    :host { display: block; padding: var(--gutter); }
    .audit { display: grid; gap: var(--space-3); max-width: var(--container-max); }
    .audit__title { font-size: var(--text-h2); font-weight: var(--weight-regular); }

    .audit__filters {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(160px, 1fr));
      gap: var(--space-2);
      align-items: start;
      padding: var(--space-2);
      background: var(--color-surface);
      border: 1px solid var(--color-border);
    }
    .audit__field { display: grid; gap: var(--space-0-5); min-width: 0; }
    .audit__label {
      font-size: var(--text-label);
      letter-spacing: var(--tracking-label);
      text-transform: uppercase;
      color: var(--color-text-muted);
    }
    .audit__input {
      width: 100%;
      min-width: 0;
      min-height: var(--tap);
      padding-inline: var(--space-1-5);
      font: inherit;
      color: var(--color-text);
      background: var(--color-bg);
      border: 1px solid var(--color-border-strong);
    }
    .audit__input[aria-invalid='true'] { border-color: var(--color-sale); }
    .audit__field-error { font-size: var(--text-small); color: var(--color-sale); }

    .audit__filter-actions {
      display: flex;
      flex-wrap: wrap;
      align-items: end;
      gap: var(--space-1);
      grid-column: 1 / -1;
    }

    .audit__submit,
    .audit__secondary,
    .audit__toggle {
      min-height: var(--tap);
      padding-inline: var(--space-2);
      font-size: var(--text-button);
      letter-spacing: var(--tracking-label);
      text-transform: uppercase;
      cursor: pointer;
    }
    .audit__submit {
      font-weight: var(--weight-semibold);
      color: var(--color-on-primary);
      background: var(--color-primary);
      border: none;
    }
    .audit__submit:hover:not(:disabled) { background: var(--color-primary-hover); }
    .audit__secondary {
      color: var(--color-text);
      background: var(--color-bg);
      border: 1px solid var(--color-border-strong);
    }
    .audit__submit:disabled,
    .audit__secondary:disabled,
    .audit__toggle:disabled { opacity: 0.6; cursor: not-allowed; }

    .audit__results { display: grid; gap: var(--space-2); }
    .audit__state {
      display: grid;
      gap: var(--space-1);
      padding: var(--space-3);
      background: var(--color-surface);
      border: 1px solid var(--color-border);
      font-size: var(--text-data);
    }
    .audit__state--error {
      color: var(--color-sale);
      background: color-mix(in srgb, var(--color-sale) 8%, transparent);
      border-color: var(--color-sale);
    }
    .audit__state-title { font-weight: var(--weight-semibold); }

    /* Data tables reflow poorly at 320 px; an internally scrollable region (not the page) keeps
       every column intact without the page itself scrolling horizontally (project rule: no
       horizontal page scroll at any of the required breakpoints). */
    .audit__table-scroll {
      max-width: 100%;
      overflow-x: auto;
      border: 1px solid var(--color-border);
    }
    .audit__table {
      width: 100%;
      min-width: 720px;
      border-collapse: collapse;
      font-size: var(--text-data);
    }
    .audit__table th,
    .audit__table td {
      padding: var(--space-1-5);
      text-align: left;
      white-space: nowrap;
      border-bottom: 1px solid var(--color-border);
    }
    .audit__table th {
      font-size: var(--text-label);
      letter-spacing: var(--tracking-label);
      text-transform: uppercase;
      color: var(--color-text-muted);
      background: var(--color-bg-alt);
    }
    .audit__muted { color: var(--color-text-muted); }
    .audit__toggle {
      color: var(--color-text);
      background: var(--color-bg);
      border: 1px solid var(--color-border-strong);
    }

    .audit__details-row td { white-space: normal; background: var(--color-bg-alt); }
    .audit__details {
      display: grid;
      gap: var(--space-2);
      padding: var(--space-1);
    }
    .audit__details-block { display: grid; gap: var(--space-0-5); min-width: 0; }
    .audit__details-heading { font-size: var(--text-label); text-transform: uppercase; letter-spacing: var(--tracking-label); }
    .audit__json {
      margin: 0;
      padding: var(--space-1-5);
      overflow-x: auto;
      white-space: pre-wrap;
      overflow-wrap: anywhere;
      background: var(--color-bg);
      border: 1px solid var(--color-border);
      font-size: var(--text-small);
    }
    .audit__meta { display: grid; grid-template-columns: auto 1fr; gap: var(--space-0-5) var(--space-2); margin: 0; }
    .audit__meta dt { color: var(--color-text-muted); }
    .audit__meta dd { margin: 0; overflow-wrap: anywhere; }

    .audit__pagination {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      justify-content: space-between;
      gap: var(--space-1);
    }
    .audit__page-info { font-size: var(--text-small); color: var(--color-text-muted); }
  `,
})
export class AuditLogPage {
  private readonly api = inject(AuditLogApi);

  protected readonly actions = AUDIT_ACTIONS;

  /** The filter form's own state, edited freely; only `applyFilters()` sends it to the API. */
  protected readonly draft = signal<AuditLogFilters>(emptyFilters());
  /** The filters the current page of results was loaded with (used to detect "filters changed"). */
  private readonly appliedFilters = signal<AuditLogFilters>(emptyFilters());

  protected readonly page = signal(1);
  protected readonly entries = signal<readonly AuditLogEntry[]>([]);
  protected readonly totalCount = signal(0);
  protected readonly loading = signal(true);
  protected readonly failure = signal<AuditLogFailure | null>(null);
  protected readonly expandedIds = signal<ReadonlySet<number>>(new Set());

  protected readonly totalPages = computed(() => Math.max(1, Math.ceil(this.totalCount() / PAGE_SIZE)));

  protected readonly announcement = computed(() => {
    if (this.loading()) {
      return 'Loading audit log…';
    }

    const problem = this.failure();
    if (problem) {
      return problem.message;
    }

    const count = this.entries().length;
    if (count === 0) {
      return 'No audit log entries match these filters.';
    }

    return `Showing ${count} of ${this.totalCount()} audit log entries, page ${this.page()} of ${this.totalPages()}.`;
  });

  constructor() {
    this.load();
  }

  protected fieldError(field: keyof AuditLogFilters): string | null {
    return this.failure()?.fieldErrors?.[field]?.[0] ?? null;
  }

  protected updateDraft<K extends keyof AuditLogFilters>(field: K, value: AuditLogFilters[K]): void {
    this.draft.update((current) => ({ ...current, [field]: value }));
  }

  protected applyFilters(): void {
    this.appliedFilters.set({ ...this.draft() });
    this.page.set(1);
    this.load();
  }

  protected clearFilters(): void {
    const cleared = emptyFilters();
    this.draft.set(cleared);
    this.appliedFilters.set(cleared);
    this.page.set(1);
    this.load();
  }

  protected retry(): void {
    this.load();
  }

  protected previousPage(): void {
    if (this.page() > 1) {
      this.page.update((current) => current - 1);
      this.load();
    }
  }

  protected nextPage(): void {
    if (this.page() < this.totalPages()) {
      this.page.update((current) => current + 1);
      this.load();
    }
  }

  protected isExpanded(id: number): boolean {
    return this.expandedIds().has(id);
  }

  protected toggle(id: number): void {
    this.expandedIds.update((current) => {
      const next = new Set(current);
      if (next.has(id)) {
        next.delete(id);
      } else {
        next.add(id);
      }

      return next;
    });
  }

  private load(): void {
    this.loading.set(true);
    this.failure.set(null);

    this.api.list(this.appliedFilters(), this.page(), PAGE_SIZE).subscribe({
      next: (result) => {
        this.loading.set(false);
        this.entries.set(result.items);
        this.totalCount.set(result.totalCount);
        this.expandedIds.set(new Set());
      },
      error: (error: unknown) => {
        this.loading.set(false);
        this.entries.set([]);
        this.totalCount.set(0);
        this.failure.set(toAuditLogFailure(error));
      },
    });
  }
}
