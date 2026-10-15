import {
  ChangeDetectionStrategy,
  Component,
  Injector,
  afterNextRender,
  computed,
  inject,
  signal,
} from '@angular/core';
import { DOCUMENT } from '@angular/common';

import { AdminAuthApi } from '../../core/auth/admin-auth-api';
import { NewsletterApi } from '../../core/newsletter/newsletter-api';
import {
  NEWSLETTER_STATUSES,
  NewsletterFilter,
  NewsletterSubscriberPage,
  NewsletterSubscriberStatus,
} from '../../core/newsletter/newsletter.model';
import { colomboDate } from '../../core/orders/order-formatting';
import { CatalogFailure, toCatalogFailure } from '../../core/catalog/admin-catalog-api';

const PAGE_SIZE = 25;

/** The permission the export route requires (`Permissions.Newsletter.Export`). */
const EXPORT_PERMISSION = 'Newsletter.Export';

const NO_FILTER: NewsletterFilter = { search: '', status: null, from: '', to: '' };

/**
 * Newsletter subscribers (ADM-21, doc 11 P15 step 4): the list, filtered by status and date, and the CSV
 * export for an external email tool.
 *
 * **The export is subscribed addresses only, and the screen says so beside the button.** The status filter
 * narrows the list, never the file: an unsubscribed address in a mailing list is a person who asked not
 * to be mailed. The server enforces it whatever this screen sends.
 *
 * **The export is audited**, and the screen says that too — a staff member should know a bulk copy of
 * personal data is recorded against their name before they make one.
 *
 * **Sending newsletters is not in scope** (doc 02 §J); this screen collects and exports, nothing more.
 */
@Component({
  selector: 'sda-newsletter-subscribers-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="adm-panel">
      <h1 class="adm-panel__title">Newsletter subscribers</h1>
      <p class="adm-panel__hint" data-testid="newsletter-counts">
        {{ subscribedCount() }} subscribed · {{ unsubscribedCount() }} unsubscribed
      </p>

      <form class="newsletter-filters" (submit)="apply($event)" aria-label="Filter subscribers">
        <label class="adm-field">
          <span class="adm-label">Email address contains</span>
          <input
            class="adm-input"
            type="search"
            name="search"
            autocomplete="off"
            maxlength="100"
            [value]="draft().search"
            data-testid="newsletter-search"
            (input)="edit('search', $event)"
          />
        </label>

        <label class="adm-field">
          <span class="adm-label">Status</span>
          <select class="adm-input" name="status" data-testid="newsletter-status" (change)="edit('status', $event)">
            <option value="" [selected]="draft().status === null">Subscribed and unsubscribed</option>
            @for (status of statuses; track status) {
              <option [value]="status" [selected]="draft().status === status">{{ status }}</option>
            }
          </select>
        </label>

        <label class="adm-field">
          <span class="adm-label">Consent from</span>
          <input
            class="adm-input"
            type="date"
            name="from"
            [value]="draft().from"
            data-testid="newsletter-from"
            (change)="edit('from', $event)"
          />
        </label>

        <label class="adm-field">
          <span class="adm-label">Consent to</span>
          <input
            class="adm-input"
            type="date"
            name="to"
            [value]="draft().to"
            data-testid="newsletter-to"
            (change)="edit('to', $event)"
          />
        </label>

        <p class="adm-panel__hint newsletter-filters__wide">Dates are Colombo days, and both are included.</p>

        @if (dateError()) {
          <p class="adm-field-error newsletter-filters__wide" data-testid="newsletter-date-error">
            {{ dateError() }}
          </p>
        }

        <div class="adm-actions newsletter-filters__wide">
          <button class="adm-btn--primary" type="submit" data-testid="newsletter-apply">Apply filters</button>
          <button class="adm-btn" type="button" data-testid="newsletter-clear" (click)="clear()">Clear</button>
        </div>
      </form>
    </section>

    <section class="adm-panel" aria-labelledby="newsletter-export-heading">
      <h2 class="adm-panel__title" id="newsletter-export-heading">Export</h2>
      <p class="adm-panel__hint" id="newsletter-export-hint">
        Downloads a CSV of <strong>subscribed</strong> addresses only, within the dates and search applied
        above — never an unsubscribed one, whatever the status filter shows. Every export is recorded in the
        audit log against your name.
      </p>
      <!-- Offered only to a role the server will let export: a button that always answers 403 is a
           defect even though the server is right to refuse (W3-07 tester). Until the session is known
           nothing is offered, so the button never appears and then vanishes. -->
      @if (canExport() === true) {
        <div class="adm-actions">
          <button
            class="adm-btn--primary"
            type="button"
            data-testid="newsletter-export"
            aria-describedby="newsletter-export-hint"
            [attr.aria-disabled]="exporting() ? 'true' : null"
            (click)="exportCsv()"
          >
            {{ exporting() ? 'Preparing the file…' : 'Export CSV' }}
          </button>
        </div>
      } @else if (canExport() === false) {
        <p class="adm-muted" data-testid="newsletter-export-unavailable">
          Your role can see subscribers but not export them. Ask an administrator for the newsletter
          export permission if you need the file.
        </p>
      }
    </section>

    @if (problem(); as failure) {
      <div class="adm-state adm-state--error" role="alert">
        <p data-testid="newsletter-problem">{{ failure.message }}</p>
        @if (failure.kind !== 'forbidden') {
          <div class="adm-actions">
            <button class="adm-btn" type="button" data-testid="newsletter-retry" (click)="reload()">
              Try again
            </button>
          </div>
        }
      </div>
    }

    <!-- One live region for the list and the export alike, never removed by an update: a message written
         into a node the same update removes is never heard. tabindex="-1" so focus can land here (#109). -->
    <p class="adm-muted" role="status" tabindex="-1" data-testid="newsletter-status-message">{{ said() }}</p>

    @if (isLoading()) {
      <p class="adm-muted">Loading subscribers…</p>
    } @else if (page() === null) {
      <!-- A failed read; the refusal is shown above. -->
    } @else if (page()!.totalCount === 0) {
      <p class="adm-muted" data-testid="newsletter-empty">{{ emptyMessage() }}</p>
    } @else {
      <ul class="newsletter-list" data-testid="newsletter-subscribers">
        @for (row of page()!.rows; track row.subscriberId) {
          <li class="newsletter-row" [attr.data-testid]="'subscriber-' + row.subscriberId">
            <p class="newsletter-row__email">{{ row.email }}</p>
            <dl class="newsletter-row__facts">
              <dt>Status</dt>
              <dd>
                <span class="adm-pill" [class.adm-pill--hidden]="row.status === 'Unsubscribed'">
                  {{ row.status }}
                </span>
              </dd>
              <dt>Consent given</dt>
              <dd>{{ day(row.consentAtUtc) }}</dd>
              <dt>Signed up on</dt>
              <dd>{{ sourceLabel(row.source) }}</dd>
              @if (row.unsubscribedAtUtc) {
                <dt>Unsubscribed</dt>
                <dd>{{ day(row.unsubscribedAtUtc) }}</dd>
              }
            </dl>
          </li>
        }
      </ul>

      <nav class="adm-actions newsletter-paging" aria-label="Pages">
        <button
          class="adm-btn"
          type="button"
          [attr.aria-disabled]="page()!.page <= 1 ? 'true' : null"
          data-testid="newsletter-prev"
          (click)="go(page()!.page - 1)"
        >
          Previous
        </button>
        <p class="adm-muted" data-testid="newsletter-paging">
          {{ firstOnPage() }}–{{ lastOnPage() }} of {{ page()!.totalCount }}
        </p>
        <button
          class="adm-btn"
          type="button"
          [attr.aria-disabled]="lastOnPage() >= page()!.totalCount ? 'true' : null"
          data-testid="newsletter-next"
          (click)="go(page()!.page + 1)"
        >
          Next
        </button>
      </nav>
    }
  `,
  styles: `
    :host {
      display: grid;
      gap: var(--space-3);
    }
    .newsletter-filters {
      display: grid;
      gap: var(--space-2);
      grid-template-columns: repeat(auto-fit, minmax(min(100%, 14rem), 1fr));
    }
    .newsletter-filters__wide {
      grid-column: 1 / -1;
      margin: 0;
    }
    .newsletter-list {
      display: grid;
      gap: var(--space-2);
      list-style: none;
      padding: 0;
      margin: 0;
    }
    .newsletter-row {
      display: grid;
      gap: var(--space-1);
      padding: var(--space-2);
      border: 1px solid var(--color-border);
      background: var(--color-surface);
      min-width: 0;
    }
    .newsletter-row__email {
      margin: 0;
      font-weight: var(--weight-semibold);
      overflow-wrap: anywhere;
    }
    .newsletter-row__facts {
      display: grid;
      grid-template-columns: auto 1fr;
      gap: 2px var(--space-2);
      font-size: var(--text-small);
      margin: 0;
    }
    .newsletter-row__facts dt {
      color: var(--color-text-muted);
    }
    .newsletter-row__facts dd {
      margin: 0;
    }
    .newsletter-paging {
      align-items: center;
    }
  `,
})
export class NewsletterSubscribersPage {
  private readonly api = inject(NewsletterApi);
  private readonly auth = inject(AdminAuthApi);
  private readonly document = inject(DOCUMENT);
  private readonly injector = inject(Injector);

  protected readonly statuses = NEWSLETTER_STATUSES;

  /** What the form shows, which becomes the applied filter only on Apply. */
  protected readonly draft = signal<NewsletterFilter>(NO_FILTER);

  /** What the list and the export are actually filtered by. */
  protected readonly applied = signal<NewsletterFilter>(NO_FILTER);

  protected readonly page = signal<NewsletterSubscriberPage | null>(null);
  protected readonly isLoading = signal(true);
  protected readonly problem = signal<CatalogFailure | null>(null);
  protected readonly dateError = signal<string | null>(null);
  protected readonly exporting = signal(false);

  /**
   * Whether this staff member holds `Newsletter.Export`: `null` until the session is read. Viewing and
   * exporting are separate permissions (doc 11 P15), so a role can reach this screen and still be refused
   * the file. If the session cannot be read the button is offered and the server decides, as before.
   */
  protected readonly canExport = signal<boolean | null>(null);
  protected readonly said = signal('');

  protected readonly subscribedCount = computed(() => this.page()?.subscribedCount ?? 0);
  protected readonly unsubscribedCount = computed(() => this.page()?.unsubscribedCount ?? 0);

  protected readonly firstOnPage = computed(() => {
    const answered = this.page();
    return answered === null || answered.totalCount === 0
      ? 0
      : (answered.page - 1) * answered.pageSize + 1;
  });

  protected readonly lastOnPage = computed(() => {
    const answered = this.page();
    return answered === null ? 0 : Math.min(answered.page * answered.pageSize, answered.totalCount);
  });

  protected readonly emptyMessage = computed(() => {
    const filter = this.applied();
    const filtered = filter.search !== '' || filter.status !== null || filter.from !== '' || filter.to !== '';

    return filtered ? 'No subscriber matches these filters.' : 'Nobody has subscribed yet.';
  });

  constructor() {
    this.load(1);

    this.auth.whoAmI().subscribe({
      next: (session) =>
        this.canExport.set(session.grantsAllPermissions || session.permissions.includes(EXPORT_PERMISSION)),
      error: () => this.canExport.set(true),
    });
  }

  protected day(utc: string): string {
    return colomboDate(utc);
  }

  /** The stored source code in words. An unknown code is shown as it is rather than guessed at. */
  protected sourceLabel(source: string): string {
    switch (source) {
      case 'homepage':
        return 'Homepage';
      case 'footer':
        return 'Footer';
      default:
        return source;
    }
  }

  protected edit(field: keyof NewsletterFilter, event: Event): void {
    const value = (event.target as HTMLInputElement | HTMLSelectElement | null)?.value ?? '';

    this.draft.update((current) => ({
      ...current,
      [field]: field === 'status' ? ((value || null) as NewsletterSubscriberStatus | null) : value,
    }));
  }

  protected apply(event: Event): void {
    event.preventDefault();

    const filter = this.draft();

    // The server refuses the same thing with a 400; saying it here keeps the list on screen.
    if (filter.from && filter.to && filter.from > filter.to) {
      this.dateError.set('The “to” date is before the “from” date.');
      this.announce('The “to” date is before the “from” date.');
      return;
    }

    this.dateError.set(null);
    this.applied.set(filter);
    this.load(1, 'Applying filters…');
  }

  protected clear(): void {
    this.draft.set(NO_FILTER);
    this.applied.set(NO_FILTER);
    this.dateError.set(null);
    this.load(1, 'Filters cleared.');
  }

  protected go(page: number): void {
    const answered = this.page();

    // aria-disabled plus this refusal, never [disabled] (#109).
    if (answered === null || page < 1 || (page - 1) * answered.pageSize >= answered.totalCount) {
      return;
    }

    this.load(page, `Page ${page}.`);
  }

  protected reload(): void {
    this.load(this.page()?.page ?? 1);
  }

  /**
   * Downloads the file.
   *
   * **`aria-disabled` and a refusal here, not `[disabled]`**: the button's state changes under the
   * pointer while the file is prepared, and a disabled button throws focus to `<body>` (#109). The
   * button itself is never removed, so focus stays on it throughout.
   */
  protected exportCsv(): void {
    if (this.exporting()) {
      return;
    }

    this.exporting.set(true);
    this.announce('Preparing the export…');

    this.api.export(this.applied()).subscribe({
      next: (file) => {
        this.exporting.set(false);
        this.save(file.blob, file.fileName);
        this.announce(`Export downloaded as ${file.fileName}. It contains subscribed addresses only.`);
      },
      error: (error: unknown) => {
        this.exporting.set(false);
        this.announce(`The export failed. ${toCatalogFailure(error).message}`);
      },
    });
  }

  private save(blob: Blob, fileName: string): void {
    const view = this.document.defaultView;

    if (!view?.URL?.createObjectURL) {
      return;
    }

    const href = view.URL.createObjectURL(blob);
    const link = this.document.createElement('a');
    link.href = href;
    link.download = fileName;
    link.rel = 'noopener';
    link.style.display = 'none';
    this.document.body.appendChild(link);
    link.click();
    link.remove();
    view.setTimeout(() => view.URL.revokeObjectURL(href), 0);
  }

  private load(page: number, announcement?: string): void {
    const focused = this.document.activeElement as HTMLElement | null;

    this.isLoading.set(true);
    this.problem.set(null);

    if (announcement !== undefined) {
      this.announce(announcement);
    }

    this.api.list(this.applied(), page, PAGE_SIZE).subscribe({
      next: (answered) => {
        this.page.set(answered);
        this.isLoading.set(false);
        this.announce(
          answered.totalCount === 0
            ? this.emptyMessage()
            : `${answered.totalCount} ${answered.totalCount === 1 ? 'subscriber' : 'subscribers'}, `
              + `most recent first. Showing ${this.firstOnPage()} to ${this.lastOnPage()}.`,
        );
        this.rehomeFocusIfLost(focused);
      },
      error: (error: unknown) => {
        this.isLoading.set(false);
        this.page.set(null);
        const failure = toCatalogFailure(error);
        this.problem.set(failure);
        this.announce(failure.message);
        this.rehomeFocusIfLost(focused);
      },
    });
  }

  /** Only if the element that had focus is gone; `afterNextRender`, never `queueMicrotask`. */
  private rehomeFocusIfLost(focused: HTMLElement | null): void {
    afterNextRender(
      () => {
        if (focused !== null && focused.isConnected) {
          return;
        }

        this.document
          .querySelector<HTMLElement>('[data-testid="newsletter-status-message"]')
          ?.focus({ preventScroll: true });
      },
      { injector: this.injector },
    );
  }

  private announce(message: string): void {
    if (this.said() !== message) {
      this.said.set(message);
      return;
    }

    this.said.set('');
    afterNextRender(() => this.said.set(message), { injector: this.injector });
  }
}
