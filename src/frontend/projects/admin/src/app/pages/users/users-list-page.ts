import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';

import { AdminAuthApi } from '../../core/auth/admin-auth-api';
import { StaffUserActionFailure, UsersApi, toStaffUserActionFailure } from '../../core/users/users-api';
import {
  RoleSummary,
  StaffUserListFilters,
  StaffUserSummary,
  emptyStaffUserListFilters,
} from '../../core/users/users.model';

const PAGE_SIZE = 25;

/**
 * The staff user list (ADM-02, W1-08, issue #8). The API is the real security boundary (doc 03 §3a): a
 * viewer without `Users.View` never reaches this route in practice (it needs a permission policy this
 * screen does not itself enforce), and one without `Users.Manage` sees every row but no write actions —
 * hiding those buttons is pure UX, not a security control, so the backend refuses the write regardless.
 */
@Component({
  selector: 'sda-users-list-page',
  imports: [FormsModule, DatePipe, RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="users">
      <div class="users__header">
        <h1 class="users__title">Staff users</h1>
        @if (canManage()) {
          <a class="users__new" routerLink="/users/new">New user</a>
        }
      </div>

      <form class="users__filters" (ngSubmit)="applyFilters()" novalidate aria-label="Filter staff users">
        <label class="users__field">
          <span class="users__label">Search</span>
          <input
            class="users__input"
            type="search"
            name="search"
            placeholder="Name or email"
            [ngModel]="draft().search"
            (ngModelChange)="updateDraft('search', $event)"
          />
        </label>

        <label class="users__field">
          <span class="users__label">Role</span>
          <select class="users__input" name="roleId" [ngModel]="draft().roleId" (ngModelChange)="updateDraft('roleId', $event)">
            <option value="">All roles</option>
            @for (role of roles(); track role.id) {
              <option [value]="role.id">{{ role.name }}</option>
            }
          </select>
        </label>

        <label class="users__field">
          <span class="users__label">Status</span>
          <select class="users__input" name="isActive" [ngModel]="draft().isActive" (ngModelChange)="updateDraft('isActive', $event)">
            <option value="">All</option>
            <option value="true">Active</option>
            <option value="false">Deactivated</option>
          </select>
        </label>

        <div class="users__filter-actions">
          <button class="users__submit" type="submit" [disabled]="loading()">Apply filters</button>
          <button class="users__secondary" type="button" (click)="clearFilters()" [disabled]="loading()">Clear filters</button>
        </div>
      </form>

      <p class="sr-only" role="status">{{ announcement() }}</p>

      @if (actionError(); as message) {
        <p class="users__state users__state--error" role="alert">{{ message }}</p>
      }

      <div class="users__results" [attr.aria-busy]="loading()">
        @if (failure(); as problem) {
          <div class="users__state users__state--error" role="alert">
            <p class="users__state-title">We could not load the staff user list</p>
            <p>{{ problem.message }}</p>
            <button class="users__submit" type="button" (click)="retry()">Try again</button>
          </div>
        } @else if (loading()) {
          <p class="users__state">Loading staff users…</p>
        } @else if (rows().length === 0) {
          <p class="users__state">No staff users match these filters.</p>
        } @else {
          <div class="users__table-scroll" role="region" aria-label="Staff users" tabindex="0">
            <table class="users__table">
              <caption class="sr-only">Staff users</caption>
              <thead>
                <tr>
                  <th scope="col">Name</th>
                  <th scope="col">Email</th>
                  <th scope="col">Roles</th>
                  <th scope="col">Status</th>
                  <th scope="col">Last sign-in</th>
                  <th scope="col"><span class="sr-only">Actions</span></th>
                </tr>
              </thead>
              <tbody>
                @for (row of rows(); track row.id) {
                  <tr>
                    <td>{{ row.firstName }} {{ row.lastName }}</td>
                    <td>{{ row.email }}</td>
                    <td>
                      @if (row.roles.length === 0) {
                        <span class="users__muted">No roles</span>
                      } @else {
                        <ul class="users__roles">
                          @for (role of row.roles; track role.id) {
                            <li class="users__role-badge">{{ role.name }}</li>
                          }
                        </ul>
                      }
                    </td>
                    <td>
                      <span class="users__status" [class.users__status--inactive]="!row.isActive">
                        {{ row.isActive ? 'Active' : 'Deactivated' }}
                      </span>
                    </td>
                    <td>{{ row.lastLoginAtUtc ? (row.lastLoginAtUtc | date: 'medium' : 'UTC') : 'Never' }}</td>
                    <td class="users__row-actions">
                      @if (canManage()) {
                        <a class="users__link" [routerLink]="['/users', row.id]">Edit</a>
                        @if (row.isActive) {
                          <button
                            class="users__toggle"
                            type="button"
                            [disabled]="busyRowId() === row.id"
                            (click)="deactivate(row)"
                          >
                            Deactivate
                          </button>
                        } @else {
                          <button
                            class="users__toggle"
                            type="button"
                            [disabled]="busyRowId() === row.id"
                            (click)="reactivate(row)"
                          >
                            Reactivate
                          </button>
                        }
                      } @else {
                        <a class="users__link" [routerLink]="['/users', row.id]">View</a>
                      }
                    </td>
                  </tr>
                }
              </tbody>
            </table>
          </div>

          <nav class="users__pagination" aria-label="Staff user pages">
            <button class="users__secondary" type="button" (click)="previousPage()" [disabled]="loading() || page() <= 1">
              Previous
            </button>
            <span class="users__page-info">Page {{ page() }} of {{ totalPages() }} ({{ totalCount() }} total)</span>
            <button class="users__secondary" type="button" (click)="nextPage()" [disabled]="loading() || page() >= totalPages()">
              Next
            </button>
          </nav>
        }
      </div>
    </section>
  `,
  styles: `
    :host { display: block; padding: var(--gutter); }
    .users { display: grid; gap: var(--space-3); max-width: var(--container-max); }
    .users__header { display: flex; flex-wrap: wrap; align-items: center; justify-content: space-between; gap: var(--space-2); }
    .users__title { font-size: var(--text-h2); font-weight: var(--weight-regular); }
    .users__new {
      display: inline-flex;
      align-items: center;
      min-height: var(--tap);
      padding-inline: var(--space-2);
      font-size: var(--text-button);
      font-weight: var(--weight-semibold);
      letter-spacing: var(--tracking-label);
      text-transform: uppercase;
      color: var(--color-on-primary);
      background: var(--color-primary);
      text-decoration: none;
    }
    .users__new:hover { background: var(--color-primary-hover); }

    .users__filters {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(160px, 1fr));
      gap: var(--space-2);
      align-items: end;
      padding: var(--space-2);
      background: var(--color-surface);
      border: 1px solid var(--color-border);
    }
    .users__field { display: grid; gap: var(--space-0-5); min-width: 0; }
    .users__label {
      font-size: var(--text-label);
      letter-spacing: var(--tracking-label);
      text-transform: uppercase;
      color: var(--color-text-muted);
    }
    .users__input {
      width: 100%;
      min-width: 0;
      min-height: var(--tap);
      padding-inline: var(--space-1-5);
      font: inherit;
      color: var(--color-text);
      background: var(--color-bg);
      border: 1px solid var(--color-border-strong);
    }
    .users__filter-actions { display: flex; flex-wrap: wrap; gap: var(--space-1); grid-column: 1 / -1; }

    .users__submit,
    .users__secondary,
    .users__toggle {
      min-height: var(--tap);
      padding-inline: var(--space-2);
      font-size: var(--text-button);
      letter-spacing: var(--tracking-label);
      text-transform: uppercase;
      cursor: pointer;
    }
    .users__submit { font-weight: var(--weight-semibold); color: var(--color-on-primary); background: var(--color-primary); border: none; }
    .users__submit:hover:not(:disabled) { background: var(--color-primary-hover); }
    .users__secondary,
    .users__toggle { color: var(--color-text); background: var(--color-bg); border: 1px solid var(--color-border-strong); }
    .users__submit:disabled,
    .users__secondary:disabled,
    .users__toggle:disabled { opacity: 0.6; cursor: not-allowed; }

    .users__results { display: grid; gap: var(--space-2); }
    .users__state { display: grid; gap: var(--space-1); padding: var(--space-3); background: var(--color-surface); border: 1px solid var(--color-border); font-size: var(--text-data); }
    .users__state--error { color: var(--color-sale); background: color-mix(in srgb, var(--color-sale) 8%, transparent); border-color: var(--color-sale); }
    .users__state-title { font-weight: var(--weight-semibold); }

    .users__table-scroll { max-width: 100%; overflow-x: auto; border: 1px solid var(--color-border); }
    .users__table { width: 100%; min-width: 720px; border-collapse: collapse; font-size: var(--text-data); }
    .users__table th,
    .users__table td { padding: var(--space-1-5); text-align: left; vertical-align: top; border-bottom: 1px solid var(--color-border); }
    .users__table th { font-size: var(--text-label); letter-spacing: var(--tracking-label); text-transform: uppercase; color: var(--color-text-muted); background: var(--color-bg-alt); white-space: nowrap; }
    .users__muted { color: var(--color-text-muted); }

    .users__roles { display: flex; flex-wrap: wrap; gap: var(--space-0-5); margin: 0; padding: 0; list-style: none; }
    .users__role-badge { padding: 2px var(--space-1); font-size: var(--text-small); color: var(--color-text); background: var(--color-bg-alt); border: 1px solid var(--color-border); white-space: nowrap; }

    .users__status { font-weight: var(--weight-semibold); color: var(--color-success, #2e7d32); }
    .users__status--inactive { color: var(--color-text-muted); font-weight: var(--weight-regular); }

    .users__row-actions { display: flex; flex-wrap: wrap; gap: var(--space-1); white-space: nowrap; }
    .users__link { display: inline-flex; align-items: center; min-height: var(--tap); color: var(--color-text); text-decoration: underline; }

    .users__pagination { display: flex; flex-wrap: wrap; align-items: center; justify-content: space-between; gap: var(--space-1); }
    .users__page-info { font-size: var(--text-small); color: var(--color-text-muted); }
  `,
})
export class UsersListPage {
  private readonly api = inject(UsersApi);
  private readonly authApi = inject(AdminAuthApi);

  protected readonly draft = signal<StaffUserListFilters>(emptyStaffUserListFilters());
  private readonly appliedFilters = signal<StaffUserListFilters>(emptyStaffUserListFilters());

  protected readonly page = signal(1);
  protected readonly rows = signal<readonly StaffUserSummary[]>([]);
  protected readonly totalCount = signal(0);
  protected readonly loading = signal(true);
  protected readonly failure = signal<StaffUserActionFailure | null>(null);
  protected readonly actionError = signal<string | null>(null);
  protected readonly busyRowId = signal<number | null>(null);

  protected readonly roles = signal<readonly RoleSummary[]>([]);

  /** Informational only (doc: "nothing meaningful client-side that matters for security") — the API is the real gate. */
  protected readonly canManage = signal(false);

  protected readonly totalPages = computed(() => Math.max(1, Math.ceil(this.totalCount() / PAGE_SIZE)));

  protected readonly announcement = computed(() => {
    if (this.loading()) {
      return 'Loading staff users…';
    }

    const problem = this.failure();
    if (problem) {
      return problem.message;
    }

    const count = this.rows().length;
    if (count === 0) {
      return 'No staff users match these filters.';
    }

    return `Showing ${count} of ${this.totalCount()} staff users, page ${this.page()} of ${this.totalPages()}.`;
  });

  constructor() {
    this.authApi.whoAmI().subscribe({
      next: (session) => this.canManage.set(session.grantsAllPermissions || session.permissions.includes('Users.Manage')),
      error: () => this.canManage.set(false),
    });
    this.api.listRoles().subscribe({ next: (roles) => this.roles.set(roles), error: () => this.roles.set([]) });
    this.load();
  }

  protected updateDraft<K extends keyof StaffUserListFilters>(field: K, value: StaffUserListFilters[K]): void {
    this.draft.update((current) => ({ ...current, [field]: value }));
  }

  protected applyFilters(): void {
    this.appliedFilters.set({ ...this.draft() });
    this.page.set(1);
    this.load();
  }

  protected clearFilters(): void {
    const cleared = emptyStaffUserListFilters();
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

  protected deactivate(row: StaffUserSummary): void {
    this.setActive(row, this.api.deactivate(row.id));
  }

  protected reactivate(row: StaffUserSummary): void {
    this.setActive(row, this.api.reactivate(row.id));
  }

  private setActive(row: StaffUserSummary, request: ReturnType<UsersApi['deactivate']>): void {
    if (this.busyRowId() !== null) {
      return;
    }

    this.busyRowId.set(row.id);
    this.actionError.set(null);

    request.subscribe({
      next: () => {
        this.busyRowId.set(null);
        this.load();
      },
      error: (error: unknown) => {
        this.busyRowId.set(null);
        this.actionError.set(toStaffUserActionFailure(error).message);
      },
    });
  }

  private load(): void {
    this.loading.set(true);
    this.failure.set(null);

    this.api.listUsers(this.appliedFilters(), this.page(), PAGE_SIZE).subscribe({
      next: (result) => {
        this.loading.set(false);
        this.rows.set(result.items);
        this.totalCount.set(result.totalCount);
      },
      error: (error: unknown) => {
        this.loading.set(false);
        this.rows.set([]);
        this.totalCount.set(0);
        this.failure.set(toStaffUserActionFailure(error));
      },
    });
  }
}
