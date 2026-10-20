import {
  ChangeDetectionStrategy,
  Component,
  Injector,
  afterNextRender,
  inject,
  signal,
} from '@angular/core';
import { DOCUMENT } from '@angular/common';
import { RouterLink } from '@angular/router';

import {
  AdminSiteContentApi,
  ContentFailure,
  toContentFailure,
} from '../../core/content/admin-site-content-api';
import {
  ANNOUNCEMENT_LONG_ON_PHONES,
  ANNOUNCEMENT_MAX_LENGTH,
  AdminAnnouncement,
  liveLabel,
} from '../../core/content/admin-site-content.model';
import { blankToNull, toIntegerOrNull, toLocalInput } from './home-section-editor';

/**
 * The announcement bar (GLB-03, doc 11 P13 step 9; #192): short messages across the top of every page,
 * which the bar rotates between. Up to 200 characters each, an optional link, an order and a schedule.
 *
 * The message is plain text and is shown by interpolation only. Nothing is seeded: which messages the
 * shop runs is the client's to write.
 */
@Component({
  selector: 'sda-announcements-page',
  imports: [RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="adm-panel">
      <h1 class="adm-panel__title">Announcement bar</h1>
      <p class="adm-panel__hint">
        Short messages shown in the bar across the top of every page. When more than one is showing, the
        bar takes turns between them in the order below. Dates and times are this shop's own time.
      </p>
    </section>

    @if (problem(); as failure) {
      <div class="adm-state adm-state--error" role="alert" data-testid="ann-problem">
        <p>{{ failure.message }}</p>
        @if (failure.kind === 'forbidden') {
          <p><a class="adm-link" routerLink="/">Back to the dashboard</a></p>
        } @else {
          <div class="adm-actions">
            <button class="adm-btn" type="button" data-testid="ann-reload" (click)="reload()">
              {{ failure.stale ? 'Reload' : 'Try again' }}
            </button>
          </div>
        }
      </div>
    }

    <p class="adm-muted" role="status" tabindex="-1" data-testid="ann-said">{{ said() }}</p>

    @if (!forbidden()) {
      <section class="adm-panel" aria-labelledby="ann-form-title">
        <h2 class="adm-panel__title" id="ann-form-title">
          {{ editingId() === null ? 'Add a message' : 'Edit this message' }}
        </h2>
        <form (submit)="save($event)">
          <div class="adm-fields">
            <label class="adm-field adm-field--wide">
              <span class="adm-label">Message</span>
              <input
                class="adm-input"
                type="text"
                autocomplete="off"
                [attr.maxlength]="max"
                [attr.aria-describedby]="message().trim().length > longOnPhones ? 'ann-count ann-long' : 'ann-count'"
                [value]="message()"
                [attr.aria-invalid]="errorFor('message') ? 'true' : null"
                data-testid="ann-message"
                (input)="type($event, message)"
              />
              <span class="adm-panel__hint" id="ann-count" data-testid="ann-count">
                {{ message().length }} of {{ max }} characters
              </span>
              @if (message().trim().length > longOnPhones) {
                <span class="adm-panel__hint" id="ann-long" data-testid="ann-long-hint">
                  Long messages take up more of the screen on phones, where this one runs to several lines.
                  Keep it short if you can.
                </span>
              }
              @if (errorFor('message'); as text) {
                <span class="adm-field-error" role="alert" data-testid="ann-error-message">{{ text }}</span>
              }
            </label>

            <label class="adm-field adm-field--wide">
              <span class="adm-label">Link (optional)</span>
              <input
                class="adm-input"
                type="text"
                maxlength="500"
                autocomplete="off"
                aria-describedby="ann-link-hint"
                [value]="linkUrl()"
                [attr.aria-invalid]="errorFor('linkUrl') ? 'true' : null"
                data-testid="ann-link"
                (input)="type($event, linkUrl)"
              />
              <span class="adm-panel__hint" id="ann-link-hint">
                A page on this shop starting with /, such as /sale, or a full https:// address.
              </span>
              @if (errorFor('linkUrl'); as text) {
                <span class="adm-field-error" role="alert" data-testid="ann-error-linkUrl">{{ text }}</span>
              }
            </label>

            <label class="adm-field">
              <span class="adm-label">Order (0 to 999)</span>
              <input
                class="adm-input"
                type="number"
                min="0"
                max="999"
                step="1"
                [value]="sortOrder()"
                [attr.aria-invalid]="errorFor('sortOrder') ? 'true' : null"
                data-testid="ann-order"
                (input)="type($event, sortOrder)"
              />
              @if (errorFor('sortOrder'); as text) {
                <span class="adm-field-error" role="alert" data-testid="ann-error-sortOrder">{{ text }}</span>
              }
            </label>

            <label class="adm-field">
              <span class="adm-label">Show from (optional)</span>
              <input
                class="adm-input"
                type="datetime-local"
                [value]="startsAt()"
                [attr.aria-invalid]="errorFor('startsAt') ? 'true' : null"
                data-testid="ann-starts"
                (input)="type($event, startsAt)"
              />
              @if (errorFor('startsAt'); as text) {
                <span class="adm-field-error" role="alert" data-testid="ann-error-startsAt">{{ text }}</span>
              }
            </label>

            <label class="adm-field">
              <span class="adm-label">Show until (optional)</span>
              <input
                class="adm-input"
                type="datetime-local"
                [value]="endsAt()"
                [attr.aria-invalid]="errorFor('endsAt') ? 'true' : null"
                data-testid="ann-ends"
                (input)="type($event, endsAt)"
              />
              @if (errorFor('endsAt'); as text) {
                <span class="adm-field-error" role="alert" data-testid="ann-error-endsAt">{{ text }}</span>
              }
            </label>

            <label class="adm-check adm-field--wide" data-testid="ann-active-field">
              <input type="checkbox" [checked]="isActive()" data-testid="ann-active" (change)="toggleActive($event)" />
              <span>Show this message</span>
            </label>
          </div>

          @if (otherErrors().length > 0) {
            <ul class="adm-field-error" role="alert" data-testid="ann-errors">
              @for (text of otherErrors(); track text) {
                <li>{{ text }}</li>
              }
            </ul>
          }

          <div class="adm-actions">
            <button
              class="adm-btn--primary"
              type="submit"
              [attr.aria-disabled]="isBusy() ? 'true' : null"
              data-testid="ann-save"
            >
              {{ editingId() === null ? 'Add the message' : 'Save the message' }}
            </button>
            @if (editingId() !== null) {
              <button class="adm-btn" type="button" data-testid="ann-cancel" (click)="startNew()">Cancel</button>
            }
          </div>
        </form>
      </section>

      @if (isLoading()) {
        <p class="adm-muted">Loading messages…</p>
      } @else if (announcements().length === 0) {
        <p class="adm-muted" data-testid="ann-empty">
          No messages yet, so the bar is not shown. Add the first one above.
        </p>
      } @else {
        <ul class="ann-list" data-testid="ann-list">
          @for (row of announcements(); track row.id) {
            <li class="ann-row" [attr.data-testid]="'ann-' + row.id">
              <p class="ann-row__message">{{ row.message }}</p>
              <dl class="ann-row__facts">
                <dt>Status</dt>
                <dd>
                  <span class="adm-pill" [class.adm-pill--hidden]="!row.isLiveNow" [attr.data-testid]="'ann-status-' + row.id">{{
                    status(row)
                  }}</span>
                </dd>
                <dt>Order</dt>
                <dd>{{ row.sortOrder }}</dd>
                @if (row.linkUrl) {
                  <dt>Link</dt>
                  <dd>{{ row.linkUrl }}</dd>
                }
                @if (row.startsAt || row.endsAt) {
                  <dt>Dates</dt>
                  <dd>{{ row.startsAt ?? 'Now' }} – {{ row.endsAt ?? 'no end' }}</dd>
                }
              </dl>
              <div class="adm-actions">
                <button class="adm-btn" type="button" [attr.data-testid]="'ann-edit-' + row.id" (click)="edit(row)">
                  Edit
                </button>
                <button
                  class="adm-btn--danger"
                  type="button"
                  [attr.aria-disabled]="isBusy() ? 'true' : null"
                  [attr.data-testid]="'ann-remove-' + row.id"
                  (click)="remove(row)"
                >
                  {{ removingId() === row.id ? 'Yes, remove it' : 'Remove' }}
                </button>
                @if (removingId() === row.id) {
                  <button class="adm-btn" type="button" [attr.data-testid]="'ann-keep-' + row.id" (click)="removingId.set(null)">
                    Keep it
                  </button>
                }
              </div>
            </li>
          }
        </ul>
      }
    }
  `,
  styles: `
    :host {
      /* The side gutter every admin page has (dashboard, catalogue, inventory): #192 tester UX. */
      padding: var(--gutter);
      display: grid;
      /* One track that may shrink below its content, so a wide table scrolls in its own region
         rather than widening the page. */
      grid-template-columns: minmax(0, 1fr);
      gap: var(--space-3);
      min-width: 0;
    }
    .ann-list {
      display: grid;
      gap: var(--space-2);
      list-style: none;
      padding: 0;
      margin: 0;
    }
    .ann-row {
      display: grid;
      gap: var(--space-1);
      padding: var(--space-2);
      border: 1px solid var(--color-border);
      background: var(--color-surface);
      min-width: 0;
    }
    .ann-row__message {
      font-weight: var(--weight-semibold);
      overflow-wrap: anywhere;
      margin: 0;
    }
    .ann-row__facts {
      display: grid;
      grid-template-columns: auto 1fr;
      gap: 2px var(--space-2);
      font-size: var(--text-small);
      margin: 0;
      min-width: 0;
    }
    .ann-row__facts dt {
      color: var(--color-text-muted);
    }
    .ann-row__facts dd {
      margin: 0;
      overflow-wrap: anywhere;
    }
  `,
})
export class AnnouncementsPage {
  private readonly api = inject(AdminSiteContentApi);
  private readonly document = inject(DOCUMENT);
  private readonly injector = inject(Injector);

  protected readonly max = ANNOUNCEMENT_MAX_LENGTH;

  /**
   * Past this many characters the strip wraps onto three or more lines at 320 px (the delivery line, 37
   * characters, already takes two), so the editor is told — a hint, not a limit: 200 stays the limit (#192).
   */
  protected readonly longOnPhones = ANNOUNCEMENT_LONG_ON_PHONES;

  protected readonly announcements = signal<readonly AdminAnnouncement[]>([]);
  protected readonly isLoading = signal(true);
  protected readonly isBusy = signal(false);
  protected readonly problem = signal<ContentFailure | null>(null);
  protected readonly forbidden = signal(false);
  protected readonly said = signal('');

  protected readonly editingId = signal<number | null>(null);
  protected readonly removingId = signal<number | null>(null);

  protected readonly message = signal('');
  protected readonly linkUrl = signal('');
  protected readonly sortOrder = signal('0');
  protected readonly isActive = signal(true);
  protected readonly startsAt = signal('');
  protected readonly endsAt = signal('');

  private readonly fieldErrors = signal<Readonly<Record<string, string>>>({});
  private version: string | null = null;

  constructor() {
    this.load();
  }

  protected errorFor(field: string): string | null {
    return this.fieldErrors()[field] ?? null;
  }

  protected otherErrors(): string[] {
    const shown = ['message', 'linkUrl', 'sortOrder', 'startsAt', 'endsAt'];
    return Object.entries(this.fieldErrors())
      .filter(([key]) => !shown.includes(key))
      .map(([, text]) => text);
  }

  protected status(row: AdminAnnouncement): string {
    return liveLabel({ isEnabled: row.isActive, isLiveNow: row.isLiveNow });
  }

  protected type(event: Event, target: { set(value: string): void }): void {
    target.set((event.target as HTMLInputElement | null)?.value ?? '');
  }

  protected toggleActive(event: Event): void {
    this.isActive.set((event.target as HTMLInputElement | null)?.checked ?? false);
  }

  protected startNew(): void {
    this.editingId.set(null);
    this.version = null;
    this.removingId.set(null);
    this.message.set('');
    this.linkUrl.set('');
    this.sortOrder.set(String(this.announcements().length));
    this.isActive.set(true);
    this.startsAt.set('');
    this.endsAt.set('');
    this.fieldErrors.set({});
  }

  protected edit(row: AdminAnnouncement): void {
    this.editingId.set(row.id);
    this.version = row.version;
    this.removingId.set(null);
    this.message.set(row.message);
    this.linkUrl.set(row.linkUrl ?? '');
    this.sortOrder.set(String(row.sortOrder));
    this.isActive.set(row.isActive);
    this.startsAt.set(toLocalInput(row.startsAt));
    this.endsAt.set(toLocalInput(row.endsAt));
    this.fieldErrors.set({});
    this.said.set('Editing the message.');
  }

  /** Saves the form. Every key is sent; blanks are null. The server's checks are the ones that count. */
  protected save(event: Event): void {
    event.preventDefault();

    if (this.isBusy()) {
      return;
    }

    const id = this.editingId();
    this.isBusy.set(true);
    this.problem.set(null);
    this.fieldErrors.set({});
    this.said.set(id === null ? 'Adding the message…' : 'Saving the message…');

    this.api
      .saveAnnouncement(
        id,
        {
          message: this.message().trim(),
          linkUrl: blankToNull(this.linkUrl()),
          sortOrder: toIntegerOrNull(this.sortOrder()),
          isActive: this.isActive(),
          startsAt: blankToNull(this.startsAt()),
          endsAt: blankToNull(this.endsAt()),
        },
        this.version,
      )
      .subscribe({
        next: () => {
          const text = id === null ? 'The message is added.' : 'The message is saved.';
          this.load(() => {
            this.startNew();
            this.said.set(text);
            this.rehomeFocus();
          });
        },
        error: (error: unknown) => this.fail(error),
      });
  }

  protected remove(row: AdminAnnouncement): void {
    if (this.isBusy()) {
      return;
    }

    if (this.removingId() !== row.id) {
      this.removingId.set(row.id);
      this.said.set('Press Remove again to delete this message.');
      return;
    }

    this.isBusy.set(true);
    this.problem.set(null);
    this.said.set('Removing the message…');

    this.api.deleteAnnouncement(row.id, row.version).subscribe({
      next: () => {
        this.removingId.set(null);
        if (this.editingId() === row.id) {
          this.startNew();
        }
        this.load(() => {
          this.said.set('The message is removed.');
          this.rehomeFocus();
        });
      },
      error: (error: unknown) => {
        this.removingId.set(null);
        this.fail(error);
      },
    });
  }

  protected reload(): void {
    this.problem.set(null);
    this.load();
  }

  private load(then?: () => void): void {
    this.api.announcements().subscribe({
      next: (rows) => {
        this.announcements.set(rows);
        this.isLoading.set(false);
        this.isBusy.set(false);
        this.forbidden.set(false);
        if (this.editingId() === null && then === undefined) {
          this.sortOrder.set(String(rows.length));
        }
        then?.();
      },
      error: (error: unknown) => {
        this.isLoading.set(false);
        const failure = toContentFailure(error, 'this message');

        if (failure.kind === 'forbidden') {
          // A role without Content.View: nothing to edit, so the form goes and the refusal stays.
          this.isBusy.set(false);
          this.forbidden.set(true);
          this.announcements.set([]);
          this.problem.set({
            ...failure,
            message:
              'Your role does not include the announcement bar. Ask an administrator for the Content permission.',
          });
          this.said.set(failure.message);
          return;
        }

        this.fail(error);
      },
    });
  }

  private fail(error: unknown): void {
    this.isBusy.set(false);
    const failure = toContentFailure(error, 'this message');

    if (Object.keys(failure.fields).length > 0) {
      this.fieldErrors.set(failure.fields);
      this.said.set(Object.values(failure.fields).join(' '));
      return;
    }

    this.problem.set(failure);
    this.said.set(failure.message);
  }

  private rehomeFocus(): void {
    afterNextRender(
      () => {
        const active = this.document.activeElement;
        if (active !== null && active !== this.document.body && active.isConnected) {
          return;
        }
        this.document.querySelector<HTMLElement>('[data-testid="ann-said"]')?.focus({ preventScroll: true });
      },
      { injector: this.injector },
    );
  }
}
