import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';

import { RoleDetail, RoleSummary } from '../../core/users/users.model';
import { UsersApi, toStaffUserActionFailure } from '../../core/users/users-api';

/**
 * Read-only role list and permission detail (ADM-02, W1-08, issue #8). Role management (create/edit/delete
 * a role, change its permissions) is GrantsAll-only (doc 08 §3.1 DB4 rule 1) and was not asked for by this
 * task — see the backend's `AppRole` remarks. Expanding a row fetches that role's full permission list on
 * demand (the list endpoint only returns a count, to keep the initial page light) and caches it, so
 * collapsing and re-expanding the same row does not refetch.
 */
@Component({
  selector: 'sda-roles-list-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="roles">
      <h1 class="roles__title">Roles</h1>
      <p class="roles__lead">Default roles and the permissions they grant. Assign these to staff users from a user's edit page.</p>

      <p class="sr-only" role="status">{{ announcement() }}</p>

      @if (failure(); as message) {
        <div class="roles__state roles__state--error" role="alert">
          <p>{{ message }}</p>
          <button class="roles__submit" type="button" (click)="load()">Try again</button>
        </div>
      } @else if (loading()) {
        <p class="roles__state">Loading roles…</p>
      } @else if (rows().length === 0) {
        <p class="roles__state">No roles are defined yet.</p>
      } @else {
        <ul class="roles__list">
          @for (role of rows(); track role.id) {
            <li class="roles__item">
              <button
                class="roles__row"
                type="button"
                [attr.aria-expanded]="isExpanded(role.id)"
                [attr.aria-controls]="'role-detail-' + role.id"
                (click)="toggle(role.id)"
              >
                <span class="roles__name">
                  {{ role.name }}
                  @if (role.grantsAllPermissions) {
                    <span class="roles__badge">Super Admin — every permission</span>
                  }
                </span>
                <span class="roles__summary">
                  {{ role.grantsAllPermissions ? 'All permissions' : role.permissionCount + ' permission' + (role.permissionCount === 1 ? '' : 's') }}
                  · {{ role.userCount }} user{{ role.userCount === 1 ? '' : 's' }}
                </span>
                <span class="roles__chevron" aria-hidden="true">{{ isExpanded(role.id) ? '−' : '+' }}</span>
              </button>

              @if (role.description) {
                <p class="roles__description">{{ role.description }}</p>
              }

              @if (isExpanded(role.id)) {
                <div class="roles__detail" [id]="'role-detail-' + role.id">
                  @if (detailFailure(role.id); as message) {
                    <p class="roles__error" role="alert">{{ message }}</p>
                  } @else if (!detail(role.id)) {
                    <p class="roles__muted">Loading permissions…</p>
                  } @else if (role.grantsAllPermissions) {
                    <p class="roles__muted">Grants every permission in the system.</p>
                  } @else {
                    <ul class="roles__permissions">
                      @for (permission of detail(role.id)?.permissions; track permission) {
                        <li class="roles__permission">{{ permission }}</li>
                      }
                    </ul>
                  }
                </div>
              }
            </li>
          }
        </ul>
      }
    </section>
  `,
  styles: `
    :host { display: block; padding: var(--gutter); }
    .roles { display: grid; gap: var(--space-3); max-width: var(--container-max); }
    .roles__title { font-size: var(--text-h2); font-weight: var(--weight-regular); }
    .roles__lead { color: var(--color-text-muted); font-size: var(--text-small); }

    .roles__state { display: grid; gap: var(--space-1); padding: var(--space-3); background: var(--color-surface); border: 1px solid var(--color-border); font-size: var(--text-data); }
    .roles__state--error { color: var(--color-sale); background: color-mix(in srgb, var(--color-sale) 8%, transparent); border-color: var(--color-sale); }
    .roles__submit { min-height: var(--tap); padding-inline: var(--space-2); font-weight: var(--weight-semibold); color: var(--color-on-primary); background: var(--color-primary); border: none; cursor: pointer; }

    .roles__list { display: grid; gap: var(--space-1); margin: 0; padding: 0; list-style: none; }
    .roles__item { background: var(--color-surface); border: 1px solid var(--color-border); }
    .roles__row {
      display: grid;
      grid-template-columns: 1fr auto auto;
      align-items: center;
      gap: var(--space-2);
      width: 100%;
      min-height: calc(var(--tap) + var(--space-1));
      padding-inline: var(--space-2);
      font: inherit;
      text-align: left;
      color: var(--color-text);
      background: transparent;
      border: none;
      cursor: pointer;
    }
    .roles__name { font-weight: var(--weight-semibold); }
    .roles__badge { margin-left: var(--space-1); padding: 1px var(--space-1); font-size: var(--text-small); font-weight: var(--weight-regular); color: var(--color-on-primary); background: var(--color-primary); }
    .roles__summary { font-size: var(--text-small); color: var(--color-text-muted); white-space: nowrap; }
    .roles__chevron { font-size: var(--text-h3, 1.2rem); color: var(--color-text-muted); }
    .roles__description { margin: 0; padding-inline: var(--space-2); padding-bottom: var(--space-1); font-size: var(--text-small); color: var(--color-text-muted); }

    .roles__detail { padding: var(--space-2); border-top: 1px solid var(--color-border); background: var(--color-bg-alt); }
    .roles__muted { margin: 0; color: var(--color-text-muted); font-size: var(--text-small); }
    .roles__error { margin: 0; color: var(--color-sale); font-size: var(--text-small); }
    .roles__permissions { display: flex; flex-wrap: wrap; gap: var(--space-0-5); margin: 0; padding: 0; list-style: none; }
    .roles__permission { padding: 2px var(--space-1); font-size: var(--text-small); background: var(--color-bg); border: 1px solid var(--color-border); }
  `,
})
export class RolesListPage {
  private readonly api = inject(UsersApi);

  protected readonly rows = signal<readonly RoleSummary[]>([]);
  protected readonly loading = signal(true);
  protected readonly failure = signal<string | null>(null);

  protected readonly expandedIds = signal<ReadonlySet<number>>(new Set());
  private readonly details = signal<ReadonlyMap<number, RoleDetail>>(new Map());
  private readonly detailFailures = signal<ReadonlyMap<number, string>>(new Map());

  protected readonly announcement = signal('Loading roles…');

  constructor() {
    this.load();
  }

  protected load(): void {
    this.loading.set(true);
    this.failure.set(null);

    this.api.listRoles().subscribe({
      next: (roles) => {
        this.loading.set(false);
        this.rows.set(roles);
        this.announcement.set(`Loaded ${roles.length} role${roles.length === 1 ? '' : 's'}.`);
      },
      error: (error: unknown) => {
        this.loading.set(false);
        this.rows.set([]);
        const message = toStaffUserActionFailure(error).message;
        this.failure.set(message);
        this.announcement.set(message);
      },
    });
  }

  protected isExpanded(roleId: number): boolean {
    return this.expandedIds().has(roleId);
  }

  protected detail(roleId: number): RoleDetail | undefined {
    return this.details().get(roleId);
  }

  protected detailFailure(roleId: number): string | undefined {
    return this.detailFailures().get(roleId);
  }

  protected toggle(roleId: number): void {
    const expanded = this.expandedIds();
    const next = new Set(expanded);

    if (next.has(roleId)) {
      next.delete(roleId);
      this.expandedIds.set(next);
      return;
    }

    next.add(roleId);
    this.expandedIds.set(next);

    if (this.details().has(roleId) || this.detailFailures().has(roleId)) {
      return;
    }

    this.api.getRole(roleId).subscribe({
      next: (detail) => {
        const updated = new Map(this.details());
        updated.set(roleId, detail);
        this.details.set(updated);
      },
      error: (error: unknown) => {
        const updated = new Map(this.detailFailures());
        updated.set(roleId, toStaffUserActionFailure(error).message);
        this.detailFailures.set(updated);
      },
    });
  }
}
