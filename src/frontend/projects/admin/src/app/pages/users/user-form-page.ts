import { DOCUMENT, NgTemplateOutlet } from '@angular/common';
import { ChangeDetectionStrategy, Component, Injector, afterNextRender, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { forkJoin, of } from 'rxjs';
import { catchError, map } from 'rxjs/operators';

import { AdminAuthApi } from '../../core/auth/admin-auth-api';
import { STAFF_PASSWORD_RULE } from '../../core/auth/password-rule';
import { colomboTime } from '../../core/orders/order-formatting';
import { StaffUserActionFailure, UsersApi, toStaffRecoveryFailure, toStaffUserActionFailure } from '../../core/users/users-api';
import { RoleSummary, StaffUserDetail } from '../../core/users/users.model';

/** The profile fields that have their own error line in the form. */
const NAMED_PROFILE_FIELDS = ['email', 'firstName', 'initialPassword', 'roleIds'];

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
  imports: [FormsModule, RouterLink, NgTemplateOutlet],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <!-- The role boxes, drawn in the new-user form and in the Roles section of an existing one. -->
    <ng-template #roleChoices>
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
    </ng-template>

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
              [disabled]="emailFixed()"
              [attr.aria-describedby]="emailFixed() ? 'email-hint' : null"
            />
            @if (isEditMode() && isProtectedAccount()) {
              <span class="user-form__hint" id="email-hint">The email address of a protected account cannot be changed.</span>
            } @else if (isEditMode() && lockedForViewer()) {
              <span class="user-form__hint" id="email-hint">Only the technical owner's account can change the email address of this account.</span>
            } @else if (isEditMode() && !canChangeEmail()) {
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

            <!-- The server refuses a user with no role, so the form has to offer them. -->
            <fieldset
              class="user-form__roles"
              [attr.aria-describedby]="profileFieldError('roleIds') ? 'new-user-roles-hint new-user-roles-error' : 'new-user-roles-hint'"
              data-testid="new-user-roles"
            >
              <legend class="user-form__label">Roles</legend>
              <span class="user-form__hint" id="new-user-roles-hint">
                Choose at least one. The new user can do only what their roles allow.
              </span>
              @if (roleOptions().length === 0) {
                <p class="user-form__hint">No roles are available.</p>
              } @else {
                <ng-container [ngTemplateOutlet]="roleChoices" />
              }
            </fieldset>
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
          @if (profileFieldError('roleIds'); as message) {
            <p class="user-form__error" role="alert" id="new-user-roles-error" data-testid="role-error">{{ message }}</p>
          }
          <!-- Anything else the server refused, so that no refusal is ever silent. -->
          @for (message of otherFieldErrors(); track $index) {
            <p class="user-form__error" role="alert">{{ message }}</p>
          }

          <div class="user-form__actions">
            <button class="user-form__submit" type="submit" [attr.aria-disabled]="savingProfile() ? 'true' : null">
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
                <ng-container [ngTemplateOutlet]="roleChoices" />
              </fieldset>
            }

            @if (rolesError(); as message) {
              <p class="user-form__error" role="alert">{{ message }}</p>
            }

            <div class="user-form__actions">
              <button class="user-form__submit" type="submit" [attr.aria-disabled]="savingRoles() ? 'true' : null">
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
              @if (isActive() && isProtectedAccount()) {
                <p class="user-form__protected" data-testid="protected-note">
                  This account is protected. It can never be deactivated, and its email address cannot be changed.
                </p>
              } @else if (lockedForViewer()) {
                <p class="user-form__protected" data-testid="locked-note">
                  Only the technical owner's account can activate or deactivate this account, or change its email address.
                </p>
              } @else if (isActive()) {
                <button class="user-form__danger" type="button" [attr.aria-disabled]="togglingStatus() ? 'true' : null" (click)="deactivate()">
                  {{ togglingStatus() ? 'Deactivating…' : 'Deactivate' }}
                </button>
              } @else {
                <button class="user-form__submit" type="button" [attr.aria-disabled]="togglingStatus() ? 'true' : null" (click)="reactivate()">
                  {{ togglingStatus() ? 'Reactivating…' : 'Reactivate' }}
                </button>
              }
            </div>
          </div>

          <!-- #254: a Super Admin gets a staff member back in. Not on your own page (Security in your own menu is for
               that), not for a protected account (its owner uses the break-glass tool), not for an account only the
               technical owner may manage. The server refuses each of those too; this only stops offering them. -->
          @if (canRecover()) {
            <div class="user-form__section user-form__recovery" data-testid="account-recovery">
              <h2 class="user-form__section-title">Account recovery</h2>
              <p class="user-form__hint">
                For a staff member who forgot their password, is locked out, or lost the phone with their authenticator
                app. Each action is recorded in the audit log against your name.
              </p>
              <p class="user-form__said" role="status" tabindex="-1" data-testid="recovery-status">{{ recoveryMessage() }}</p>
              @if (recoveryError(); as message) {
                <p class="user-form__error" role="alert" data-testid="recovery-error">{{ message }}</p>
              }

              <h3 class="user-form__subtitle">Password</h3>
              @if (resettingPassword()) {
                <!-- A form of its own so Enter sets it, as on every other form here; not the profile form above. -->
                <form class="user-form__recovery-form" (submit)="$event.preventDefault(); resetPassword()" novalidate>
                <label class="user-form__field">
                  <span class="user-form__label">Temporary password</span>
                  <input
                    class="user-form__input"
                    id="temporary-password"
                    [type]="showTemporaryPassword() ? 'text' : 'password'"
                    name="temporaryPassword"
                    autocomplete="new-password"
                    data-testid="temporary-password"
                    [attr.aria-describedby]="temporaryPasswordError() ? 'temporary-password-rule temporary-password-error' : 'temporary-password-rule'"
                    [attr.aria-invalid]="temporaryPasswordError() ? 'true' : null"
                    [value]="temporaryPassword()"
                    (input)="temporaryPassword.set($any($event.target).value)"
                  />
                  <span class="user-form__hint" id="temporary-password-rule">
                    {{ passwordRule }} Tell it to them in person or by phone, never by email or chat. They must choose
                    a new password when they sign in, and every session they have open ends now.
                  </span>
                  @if (temporaryPasswordError(); as message) {
                    <span class="user-form__error" id="temporary-password-error">{{ message }}</span>
                  }
                </label>
                <!-- So the Super Admin can check what they are about to read out. -->
                <label class="user-form__check">
                  <input
                    type="checkbox"
                    data-testid="show-temporary-password"
                    [checked]="showTemporaryPassword()"
                    (change)="showTemporaryPassword.set($any($event.target).checked)"
                  />
                  Show the password
                </label>
                <div class="user-form__actions">
                  <button
                    class="user-form__danger"
                    type="submit"
                    data-testid="reset-password-confirm"
                    [attr.aria-disabled]="recoveryBusy() ? 'true' : null"
                    (click)="resetPassword()"
                  >
                    {{ recoveryBusy() === 'password' ? 'Setting…' : 'Set temporary password' }}
                  </button>
                  <button class="user-form__secondary" type="button" data-testid="reset-password-cancel" (click)="cancelPasswordReset()">
                    Cancel
                  </button>
                </div>
                </form>
              } @else {
                <div class="user-form__actions">
                  <button class="user-form__secondary" type="button" data-testid="reset-password" (click)="openPasswordReset()">
                    Reset password…
                  </button>
                </div>
              }

              <h3 class="user-form__subtitle">Lockout</h3>
              @if (lockedOutUntil(); as until) {
                <p data-testid="locked-out">Locked after too many wrong passwords, until {{ lockedOutUntilText() }}.</p>
                <div class="user-form__actions">
                  <button
                    class="user-form__submit"
                    type="button"
                    data-testid="unlock"
                    [attr.aria-disabled]="recoveryBusy() ? 'true' : null"
                    (click)="unlock()"
                  >
                    {{ recoveryBusy() === 'unlock' ? 'Unlocking…' : 'Unlock' }}
                  </button>
                </div>
              } @else {
                <p data-testid="not-locked">Not locked.</p>
              }

              <h3 class="user-form__subtitle">Two-factor authentication</h3>
              @if (twoFactorEnabled()) {
                @if (confirmingTwoFactorReset()) {
                  <p id="reset-two-factor-question" data-testid="reset-two-factor-question">
                    Reset two-factor authentication for {{ email() }}? Their authenticator app and recovery codes stop
                    working now and every session they have open ends.
                    @if (targetGrantsAll()) {
                      As a Super Admin they must set it up again before they can do anything else.
                    }
                  </p>
                  <div class="user-form__actions">
                    <button
                      class="user-form__danger"
                      type="button"
                      data-testid="reset-two-factor-yes"
                      aria-describedby="reset-two-factor-question"
                      [attr.aria-disabled]="recoveryBusy() ? 'true' : null"
                      (click)="resetTwoFactor()"
                    >
                      {{ recoveryBusy() === 'twoFactor' ? 'Resetting…' : 'Yes, reset two-factor' }}
                    </button>
                    <button class="user-form__secondary" type="button" data-testid="reset-two-factor-cancel" (click)="cancelTwoFactorReset()">
                      Cancel
                    </button>
                  </div>
                } @else {
                  <div class="user-form__actions">
                    <button class="user-form__secondary" type="button" data-testid="reset-two-factor" (click)="askTwoFactorReset()">
                      Reset two-factor…
                    </button>
                  </div>
                }
              } @else {
                <p data-testid="two-factor-off">Not set up.</p>
              }
            </div>
          }
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
    .user-form__protected { margin: 0; padding: var(--space-1-5); border-inline-start: 4px solid var(--color-text-muted); background: var(--color-bg-alt); font-size: var(--text-small); }
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
    .user-form__submit:hover:not([aria-disabled='true']) { background: var(--color-primary-hover); }
    .user-form__danger { color: var(--color-on-primary); background: var(--color-sale); }
    .user-form__submit[aria-disabled='true'],
    .user-form__danger[aria-disabled='true'] { opacity: 0.6; cursor: not-allowed; }
    /* #254: the quieter buttons that open a recovery step, and its sub-headings. */
    .user-form__secondary {
      min-height: var(--tap);
      padding-inline: var(--space-3);
      font-size: var(--text-button);
      font-weight: var(--weight-semibold);
      letter-spacing: var(--tracking-label);
      text-transform: uppercase;
      color: var(--color-text);
      background: var(--color-surface);
      border: 1px solid var(--color-border-strong, var(--color-text));
      cursor: pointer;
    }
    .user-form__subtitle { margin: var(--space-2) 0 0; font-size: var(--text-body); font-weight: var(--weight-semibold); }
    /* #254 tester: a long email address in the status line or the question widened the page past 320-390 px. The grid
       item takes its content's minimum width, so break-word alone cannot shrink it: anywhere can. */
    .user-form__recovery { min-width: 0; }
    .user-form__recovery p,
    .user-form__recovery .user-form__hint { overflow-wrap: anywhere; min-width: 0; }
    .user-form__recovery-form { display: grid; gap: var(--space-2); }
    .user-form__check { display: flex; align-items: center; gap: var(--space-1); min-height: var(--tap); }
    .user-form__said:empty { display: none; }
    .user-form__said { margin: 0; font-weight: var(--weight-medium); }
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

  private readonly document = inject(DOCUMENT);
  private readonly injector = inject(Injector);

  protected readonly loading = signal(true);
  protected readonly loadFailure = signal<StaffUserActionFailure | null>(null);

  protected readonly email = signal('');
  protected readonly firstName = signal('');
  protected readonly lastName = signal('');
  protected readonly initialPassword = signal('');

  /** The rule, shown with the initial-password box: the server's own sentence (issue #200). */
  protected readonly passwordRule = STAFF_PASSWORD_RULE;
  protected readonly isActive = signal(true);

  /** A protected account (server configuration): no Deactivate button, and its email address is fixed. */
  protected readonly isProtectedAccount = signal(false);

  /** A guarded account the viewer may not switch off, switch on or rename: only the technical owner's account may. */
  protected readonly lockedForViewer = signal(false);
  protected readonly selectedRoleIds = signal<ReadonlySet<number>>(new Set());

  protected readonly savingProfile = signal(false);
  protected readonly profileError = signal<string | null>(null);
  protected readonly profileFieldErrors = signal<Record<string, string[]>>({});

  protected readonly savingRoles = signal(false);
  protected readonly rolesError = signal<string | null>(null);

  protected readonly togglingStatus = signal(false);
  protected readonly statusError = signal<string | null>(null);

  // ---- #254 account recovery ----------------------------------------------------------------------------------
  private readonly viewerUserId = signal<number | null>(null);
  protected readonly targetGrantsAll = signal(false);
  protected readonly lockedOutUntil = signal<string | null>(null);
  protected readonly twoFactorEnabled = signal(false);
  protected readonly resettingPassword = signal(false);
  protected readonly temporaryPassword = signal('');
  protected readonly temporaryPasswordError = signal<string | null>(null);
  protected readonly showTemporaryPassword = signal(false);
  protected readonly confirmingTwoFactorReset = signal(false);
  protected readonly recoveryBusy = signal<'password' | 'unlock' | 'twoFactor' | null>(null);
  protected readonly recoveryMessage = signal('');
  protected readonly recoveryError = signal<string | null>(null);
  protected readonly lockedOutUntilText = computed(() => colomboTime(this.lockedOutUntil()));

  /** A Super Admin, on someone else's page, for an account that is neither protected nor locked for them. */
  protected readonly canRecover = computed(
    () =>
      this.viewerGrantsAll()
      && this.userId() !== null
      && this.userId() !== this.viewerUserId()
      && !this.isProtectedAccount()
      && !this.lockedForViewer(),
  );

  private readonly viewerGrantsAll = signal(false);
  private readonly roles = signal<readonly RoleSummary[]>([]);
  private readonly rolePermissions = signal<ReadonlyMap<number, readonly string[]>>(new Map());
  private readonly viewerPermissions = signal<ReadonlySet<string>>(new Set());

  protected readonly canChangeEmail = computed(() => this.viewerGrantsAll());

  /** Whether the email box is read-only: for someone who may not change it, and for an account that may not be renamed. */
  protected readonly emailFixed = computed(
    () => this.isEditMode() && (!this.canChangeEmail() || this.isProtectedAccount() || this.lockedForViewer()),
  );

  /** What the server refused beyond the boxes the form names, so that a refusal is never silent. */
  protected readonly otherFieldErrors = computed(() =>
    Object.entries(this.profileFieldErrors())
      .filter(([field]) => !NAMED_PROFILE_FIELDS.includes(field))
      .flatMap(([, messages]) => messages),
  );

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
        this.viewerUserId.set(result.session.userId);
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

  /**
   * Puts the cursor in the first role box that can be ticked, so the refusal is read where the fix is (and again on a
   * repeat press, which a message that did not change would not do). Waits for the render, as the other screens do.
   */
  private focusFirstRoleBox(): void {
    afterNextRender(
      () => {
        this.document
          .querySelector<HTMLInputElement>('[data-testid="new-user-roles"] input[type="checkbox"]:not(:disabled)')
          ?.focus();
      },
      { injector: this.injector },
    );
  }

  protected saveProfile(): void {
    if (this.savingProfile()) {
      return;
    }

    // The server refuses a user with no role. Said here too, in the form, instead of a round trip to be told.
    if (this.userId() === null && this.selectedRoleIds().size === 0) {
      this.profileError.set(null);
      this.profileFieldErrors.set({ roleIds: ['Choose at least one role for the new user.'] });
      this.focusFirstRoleBox();
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

    const request = this.canChangeEmail() && !this.isProtectedAccount() && !this.lockedForViewer() ? { firstName: this.firstName().trim(), lastName: this.lastName().trim() || null, email: this.email().trim() } : { firstName: this.firstName().trim(), lastName: this.lastName().trim() || null };

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
    this.isProtectedAccount.set(user.isProtected);
    this.lockedForViewer.set(user.lockedForViewer);
    this.selectedRoleIds.set(new Set(user.roles.map((role) => role.id)));
    this.targetGrantsAll.set(user.grantsAllPermissions);
    this.lockedOutUntil.set(user.lockedOutUntilUtc ?? null);
    this.twoFactorEnabled.set(user.twoFactorEnabled ?? false);
  }

  protected openPasswordReset(): void {
    this.resettingPassword.set(true);
    this.showTemporaryPassword.set(false);
    this.temporaryPassword.set('');
    this.temporaryPasswordError.set(null);
    this.recoveryError.set(null);
    this.focusAfterRender('#temporary-password');
  }

  protected cancelPasswordReset(): void {
    this.resettingPassword.set(false);
    this.temporaryPassword.set('');
    this.temporaryPasswordError.set(null);
    this.focusAfterRender('[data-testid="reset-password"]');
  }

  /**
   * Sets the temporary password. The box is emptied whatever happens next, so the password does not sit in the page
   * after it has been used.
   */
  protected resetPassword(): void {
    const userId = this.userId();
    const password = this.temporaryPassword();
    if (userId === null || this.recoveryBusy()) {
      return;
    }

    if (!password) {
      this.temporaryPasswordError.set('Type a temporary password.');
      this.focusAfterRender('#temporary-password');
      return;
    }

    this.recoveryBusy.set('password');
    this.recoveryError.set(null);
    this.temporaryPasswordError.set(null);

    this.api.resetPassword(userId, password).subscribe({
      next: () => {
        this.recoveryBusy.set(null);
        this.resettingPassword.set(false);
        this.temporaryPassword.set('');
        this.lockedOutUntil.set(null);
        this.announceRecovery(
          `Temporary password set for ${this.email()}. They must choose a new one when they sign in; their open sessions have ended.`,
          '[data-testid="reset-password"]',
        );
      },
      error: (error: unknown) => {
        this.recoveryBusy.set(null);
        const failure = toStaffRecoveryFailure(error);
        const fieldMessage = failure.fieldErrors?.['temporaryPassword']?.join(' ');
        if (fieldMessage) {
          this.temporaryPasswordError.set(fieldMessage);
          this.focusAfterRender('#temporary-password');
        } else {
          this.recoveryError.set(failure.message);
        }
      },
    });
  }

  protected unlock(): void {
    const userId = this.userId();
    if (userId === null || this.recoveryBusy()) {
      return;
    }

    this.recoveryBusy.set('unlock');
    this.recoveryError.set(null);

    this.api.unlock(userId).subscribe({
      next: (result) => {
        this.recoveryBusy.set(null);
        this.lockedOutUntil.set(null);
        this.announceRecovery(
          result.changed
            ? `${this.email()} is unlocked and can sign in again.`
            : `${this.email()} was not locked any more.`,
          null,
        );
      },
      error: (error: unknown) => {
        this.recoveryBusy.set(null);
        this.recoveryError.set(toStaffRecoveryFailure(error).message);
      },
    });
  }

  protected askTwoFactorReset(): void {
    this.confirmingTwoFactorReset.set(true);
    this.recoveryError.set(null);
    this.focusAfterRender('[data-testid="reset-two-factor-yes"]');
  }

  protected cancelTwoFactorReset(): void {
    this.confirmingTwoFactorReset.set(false);
    this.focusAfterRender('[data-testid="reset-two-factor"]');
  }

  protected resetTwoFactor(): void {
    const userId = this.userId();
    if (userId === null || this.recoveryBusy()) {
      return;
    }

    this.recoveryBusy.set('twoFactor');
    this.recoveryError.set(null);

    this.api.resetTwoFactor(userId).subscribe({
      next: () => {
        this.recoveryBusy.set(null);
        this.confirmingTwoFactorReset.set(false);
        this.twoFactorEnabled.set(false);
        this.announceRecovery(
          `Two-factor authentication is reset for ${this.email()}. Their open sessions have ended.`,
          null,
        );
      },
      error: (error: unknown) => {
        this.recoveryBusy.set(null);
        this.recoveryError.set(toStaffRecoveryFailure(error).message);
      },
    });
  }

  /**
   * Says what happened in the section's status line and puts focus somewhere that still exists: the control named,
   * or the status line itself when the control that was pressed has gone (Unlock, the Yes button).
   */
  private announceRecovery(message: string, focusSelector: string | null): void {
    this.recoveryMessage.set(message);
    this.focusAfterRender(focusSelector ?? '[data-testid="recovery-status"]');
  }

  private focusAfterRender(selector: string): void {
    afterNextRender(
      () => this.document.querySelector<HTMLElement>(selector)?.focus({ preventScroll: true }),
      { injector: this.injector },
    );
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
