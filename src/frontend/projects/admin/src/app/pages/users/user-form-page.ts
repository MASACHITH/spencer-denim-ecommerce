import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { forkJoin, of } from 'rxjs';
import { catchError, map } from 'rxjs/operators';

import { AdminAuthApi } from '../../core/auth/admin-auth-api';
import { STAFF_PASSWORD_RULE } from '../../core/auth/password-rule';
import { StaffUserActionFailure, UsersApi, toStaffUserActionFailure } from '../../core/users/users-api';
import { RoleSummary, StaffUserDetail } from '../../core/users/users.model';

interface RoleOption {
  readonly role: RoleSummary;
  /** Informational only (doc: "nothing meaningful client-side that matters for security"): the backend's
   * subset rule (doc 08 §3.1 DB4) is the real boundary — this only hides a choice the API would refuse anyway. */
  readonly disabled: boolean;
}

/**
 * Create or edit one staff user (ADM-02, W1-08, issue #8). One route (`/users/new` vs `/users/:id`) drives
 * both create and edit, because the fields are almost identical — the differences (initial password,
 * deactivate/reactivate, which fields exist yet to change) are handled inline via `isEditMode()`.
 * <para>
 * Profile fields and role assignment are saved separately (two API calls, matching the two backend
 * endpoints) so a rejected role change (the subset rule) never also discards an unrelated name edit, and
 * vice versa.
 * </para>
 */
@Component({
  selector: 'sda-user-form-page',
  imports: [FormsModule, RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="user-form">
      <h1 class="user-form__title">{{ isEditMode() ? 'Edit staff user' : 'New staff user' }}</h1>

      @if (loading()) {
        <p class="user-form__state">Loading…</p>
      } @else if (loadFailure(); as problem) {
        <div class="user-form__state user-form__state--error" role="alert">
          <p>{{ problem.message }}</p>
          <a class="user-form__link" routerLink="/users">Back to staff users</a>
        </div>
      } @else {
        <form class="user-form__section" (ngSubmit)="saveProfile()" novalidate aria-labelledby="profile-heading">
          <h2 class="user-form__section-title" id="profile-heading">Profile</h2>

          <label class="user-form__field">
            <span class="user-form__label">Email</span>
            <input
              class="user-form__input"
              type="email"
              name="email"
              autocomplete="off"
              required
              [ngModel]="email()"
              (ngModelChange)="email.set($event)"
              [disabled]="isEditMode() && !canChangeEmail()"
              [attr.aria-describedby]="isEditMode() && !canChangeEmail() ? 'email-hint' : null"
            />
            @if (isEditMode() && !canChangeEmail()) {
              <span class="user-form__hint" id="email-hint">Only a Super Admin can change a staff user's email.</span>
            }
          </label>

          <label class="user-form__field">
            <span class="user-form__label">First name</span>
            <input class="user-form__input" type="text" name="firstName" required [ngModel]="firstName()" (ngModelChange)="firstName.set($event)" />
          </label>

          <label class="user-form__field">
            <span class="user-form__label">Last name</span>
            <input class="user-form__input" type="text" name="lastName" [ngModel]="lastName()" (ngModelChange)="lastName.set($event)" />
          </label>

          @if (!isEditMode()) {
            <label class="user-form__field">
              <span class="user-form__label">Initial password</span>
              <input
                class="user-form__input"
                type="password"
                name="initialPassword"
                autocomplete="new-password"
                required
                aria-describedby="initial-password-rule"
                [ngModel]="initialPassword()"
                (ngModelChange)="initialPassword.set($event)"
              />
              <span class="user-form__hint" id="initial-password-rule">{{ passwordRule }}</span>
              <span class="user-form__hint">The new user must change this on first sign-in.</span>
            </label>
          }

          @if (profileError(); as message) {
            <p class="user-form__error" role="alert">{{ message }}</p>
          }
          @if (profileFieldError('email'); as message) {
            <p class="user-form__error" role="alert">{{ message }}</p>
          }
          @if (profileFieldError('firstName'); as message) {
            <p class="user-form__error" role="alert">{{ message }}</p>
          }
          @if (profileFieldError('initialPassword'); as message) {
            <p class="user-form__error" role="alert">{{ message }}</p>
          }

          <div class="user-form__actions">
            <button class="user-form__submit" type="submit" [disabled]="savingProfile()">
              {{ isEditMode() ? (savingProfile() ? 'Saving…' : 'Save profile') : (savingProfile() ? 'Creating…' : 'Create user') }}
            </button>
            <a class="user-form__link" routerLink="/users">Cancel</a>
          </div>
        </form>

        @if (isEditMode()) {
          <form class="user-form__section" (ngSubmit)="saveRoles()" novalidate aria-labelledby="roles-heading">
            <h2 class="user-form__section-title" id="roles-heading">Roles</h2>

            @if (roleOptions().length === 0) {
              <p class="user-form__state">No roles are available.</p>
            } @else {
              <fieldset class="user-form__roles">
                <legend class="sr-only">Assigned roles</legend>
                @for (option of roleOptions(); track option.role.id) {
                  <label class="user-form__role" [class.user-form__role--disabled]="option.disabled">
                    <input
                      type="checkbox"
                      [checked]="selectedRoleIds().has(option.role.id)"
                      [disabled]="option.disabled"
                      (change)="toggleRole(option.role.id)"
                    />
                    {{ option.role.name }}
                    @if (option.role.grantsAllPermissions) {
                      <span class="user-form__role-badge">Super Admin</span>
                    }
                  </label>
                }
              </fieldset>
            }

            @if (rolesError(); as message) {
              <p class="user-form__error" role="alert">{{ message }}</p>
            }

            <div class="user-form__actions">
              <button class="user-form__submit" type="submit" [disabled]="savingRoles()">
                {{ savingRoles() ? 'Saving…' : 'Save roles' }}
              </button>
            </div>
          </form>

          <div class="user-form__section">
            <h2 class="user-form__section-title">Account status</h2>
            <p>
              Currently
              <strong>{{ isActive() ? 'active' : 'deactivated' }}</strong>.
            </p>
            @if (statusError(); as message) {
              <p class="user-form__error" role="alert">{{ message }}</p>
            }
            <div class="user-form__actions">
              @if (isActive()) {
                <button class="user-form__danger" type="button" [disabled]="togglingStatus()" (click)="deactivate()">
                  {{ togglingStatus() ? 'Deactivating…' : 'Deactivate' }}
                </button>
              } @else {
                <button class="user-form__submit" type="button" [disabled]="togglingStatus()" (click)="reactivate()">
                  {{ togglingStatus() ? 'Reactivating…' : 'Reactivate' }}
                </button>
              }
            </div>
          </div>
        }
      }
    </section>
  `,
  styles: `
    :host { display: block; padding: var(--gutter); }
    .user-form { display: grid; gap: var(--space-3); max-width: 640px; }
    .user-form__title { font-size: var(--text-h2); font-weight: var(--weight-regular); }
    .user-form__section { display: grid; gap: var(--space-2); padding: var(--space-3); background: var(--color-surface); border: 1px solid var(--color-border); }
    .user-form__section-title { font-size: var(--text-h3, 1.1rem); font-weight: var(--weight-semibold); }

    .user-form__field { display: grid; gap: var(--space-0-5); min-width: 0; }
    .user-form__label { font-size: var(--text-label); letter-spacing: var(--tracking-label); text-transform: uppercase; color: var(--color-text-muted); }
    .user-form__input {
      min-height: var(--tap);
      padding-inline: var(--space-1-5);
      font: inherit;
      color: var(--color-text);
      background: var(--color-bg);
      border: 1px solid var(--color-border-strong);
    }
    .user-form__input:disabled { opacity: 0.6; }
    .user-form__hint { font-size: var(--text-small); color: var(--color-text-muted); }
    .user-form__error { margin: 0; font-size: var(--text-small); color: var(--color-sale); }

    .user-form__roles { display: grid; gap: var(--space-1); margin: 0; padding: 0; border: none; }
    .user-form__role { display: flex; align-items: center; gap: var(--space-1); min-height: var(--tap); }
    .user-form__role--disabled { color: var(--color-text-muted); }
    .user-form__role-badge { padding: 1px var(--space-1); font-size: var(--text-small); color: var(--color-on-primary); background: var(--color-primary); }

    .user-form__actions { display: flex; flex-wrap: wrap; align-items: center; gap: var(--space-2); }
    .user-form__submit,
    .user-form__danger {
      min-height: var(--tap);
      padding-inline: var(--space-3);
      font-size: var(--text-button);
      font-weight: var(--weight-semibold);
      letter-spacing: var(--tracking-label);
      text-transform: uppercase;
      border: none;
      cursor: pointer;
    }
    .user-form__submit { color: var(--color-on-primary); background: var(--color-primary); }
    .user-form__submit:hover:not(:disabled) { background: var(--color-primary-hover); }
    .user-form__danger { color: var(--color-on-primary); background: var(--color-sale); }
    .user-form__submit:disabled,
    .user-form__danger:disabled { opacity: 0.6; cursor: not-allowed; }
    .user-form__link { color: var(--color-text); text-decoration: underline; }

    .user-form__state { padding: var(--space-3); background: var(--color-surface); border: 1px solid var(--color-border); }
    .user-form__state--error { color: var(--color-sale); background: color-mix(in srgb, var(--color-sale) 8%, transparent); border-color: var(--color-sale); }
  `,
})
export class UserFormPage {
  private readonly api = inject(UsersApi);
  private readonly authApi = inject(AdminAuthApi);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);

  private readonly userId = signal<number | null>(null);
  protected readonly isEditMode = computed(() => this.userId() !== null);

  protected readonly loading = signal(true);
  protected readonly loadFailure = signal<StaffUserActionFailure | null>(null);

  protected readonly email = signal('');
  protected readonly firstName = signal('');
  protected readonly lastName = signal('');
  protected readonly initialPassword = signal('');

  /** The rule, shown with the initial-password box: the server's own sentence (issue #200). */
  protected readonly passwordRule = STAFF_PASSWORD_RULE;
  protected readonly isActive = signal(true);
  protected readonly selectedRoleIds = signal<ReadonlySet<number>>(new Set());

  protected readonly savingProfile = signal(false);
  protected readonly profileError = signal<string | null>(null);
  protected readonly profileFieldErrors = signal<Record<string, string[]>>({});

  protected readonly savingRoles = signal(false);
  protected readonly rolesError = signal<string | null>(null);

  protected readonly togglingStatus = signal(false);
  protected readonly statusError = signal<string | null>(null);

  private readonly viewerGrantsAll = signal(false);
  private readonly roles = signal<readonly RoleSummary[]>([]);
  private readonly rolePermissions = signal<ReadonlyMap<number, readonly string[]>>(new Map());
  private readonly viewerPermissions = signal<ReadonlySet<string>>(new Set());

  protected readonly canChangeEmail = computed(() => this.viewerGrantsAll());

  protected readonly roleOptions = computed<readonly RoleOption[]>(() => {
    const grantsAll = this.viewerGrantsAll();
    const viewerPermissions = this.viewerPermissions();
    const permissionsByRole = this.rolePermissions();

    return this.roles().map((role) => {
      if (grantsAll) {
        return { role, disabled: false };
      }

      if (role.grantsAllPermissions) {
        return { role, disabled: true };
      }

      const permissions = permissionsByRole.get(role.id) ?? [];
      const exceedsOwnPermissions = permissions.some((permission) => !viewerPermissions.has(permission));
      return { role, disabled: exceedsOwnPermissions };
    });
  });

  constructor() {
    const idParam = this.route.snapshot.paramMap.get('id');
    const id = idParam ? Number(idParam) : null;
    this.userId.set(Number.isFinite(id) ? id : null);

    forkJoin({
      session: this.authApi.whoAmI(),
      roles: this.api.listRoles(),
      user: id !== null ? this.api.getUser(id) : of(null),
    })
      .pipe(
        catchError((error: unknown) => {
          this.loadFailure.set(toStaffUserActionFailure(error));
          return of(null);
        }),
      )
      .subscribe((result) => {
        this.loading.set(false);
        if (!result) {
          return;
        }

        this.viewerGrantsAll.set(result.session.grantsAllPermissions);
        this.viewerPermissions.set(new Set(result.session.permissions));
        this.roles.set(result.roles);

        if (result.user) {
          this.applyUser(result.user);
        }

        this.loadRolePermissions(result.roles);
      });
  }

  protected profileFieldError(field: string): string | null {
    return this.profileFieldErrors()[field]?.[0] ?? null;
  }

  protected toggleRole(roleId: number): void {
    this.selectedRoleIds.update((current) => {
      const next = new Set(current);
      if (next.has(roleId)) {
        next.delete(roleId);
      } else {
        next.add(roleId);
      }

      return next;
    });
  }

  protected saveProfile(): void {
    if (this.savingProfile()) {
      return;
    }

    this.savingProfile.set(true);
    this.profileError.set(null);
    this.profileFieldErrors.set({});

    const userId = this.userId();
    if (userId === null) {
      this.api
        .createUser({
          email: this.email().trim(),
          firstName: this.firstName().trim(),
          lastName: this.lastName().trim() || null,
          initialPassword: this.initialPassword(),
          roleIds: [...this.selectedRoleIds()],
        })
        .subscribe({
          next: (created) => {
            this.savingProfile.set(false);
            void this.router.navigate(['/users', created.id]);
          },
          error: (error: unknown) => this.handleProfileError(error),
        });
      return;
    }

    const request = this.canChangeEmail() ? { firstName: this.firstName().trim(), lastName: this.lastName().trim() || null, email: this.email().trim() } : { firstName: this.firstName().trim(), lastName: this.lastName().trim() || null };

    this.api.updateProfile(userId, request).subscribe({
      next: () => this.savingProfile.set(false),
      error: (error: unknown) => this.handleProfileError(error),
    });
  }

  protected saveRoles(): void {
    const userId = this.userId();
    if (userId === null || this.savingRoles()) {
      return;
    }

    this.savingRoles.set(true);
    this.rolesError.set(null);

    this.api.assignRoles(userId, { roleIds: [...this.selectedRoleIds()] }).subscribe({
      next: () => this.savingRoles.set(false),
      error: (error: unknown) => {
        this.savingRoles.set(false);
        this.rolesError.set(toStaffUserActionFailure(error).message);
      },
    });
  }

  protected deactivate(): void {
    this.setActive(false);
  }

  protected reactivate(): void {
    this.setActive(true);
  }

  private setActive(active: boolean): void {
    const userId = this.userId();
    if (userId === null || this.togglingStatus()) {
      return;
    }

    this.togglingStatus.set(true);
    this.statusError.set(null);

    const request = active ? this.api.reactivate(userId) : this.api.deactivate(userId);
    request.subscribe({
      next: () => {
        this.togglingStatus.set(false);
        this.isActive.set(active);
      },
      error: (error: unknown) => {
        this.togglingStatus.set(false);
        this.statusError.set(toStaffUserActionFailure(error).message);
      },
    });
  }

  private handleProfileError(error: unknown): void {
    this.savingProfile.set(false);
    const failure = toStaffUserActionFailure(error);
    this.profileError.set(failure.fieldErrors ? null : failure.message);
    this.profileFieldErrors.set(failure.fieldErrors ?? {});
  }

  private applyUser(user: StaffUserDetail): void {
    this.email.set(user.email);
    this.firstName.set(user.firstName);
    this.lastName.set(user.lastName ?? '');
    this.isActive.set(user.isActive);
    this.selectedRoleIds.set(new Set(user.roles.map((role) => role.id)));
  }

  private loadRolePermissions(roles: readonly RoleSummary[]): void {
    if (roles.length === 0) {
      return;
    }

    forkJoin(roles.map((role) => this.api.getRole(role.id).pipe(map((detail) => [role.id, detail.permissions] as const))))
      .pipe(catchError(() => of([])))
      .subscribe((entries) => this.rolePermissions.set(new Map(entries)));
  }
}
