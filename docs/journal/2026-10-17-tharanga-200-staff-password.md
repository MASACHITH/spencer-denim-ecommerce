# 2026-10-17 — Tharanga: #200, staff passwords get the customer minimum

Branch `fix/200-staff-password-minimum` (worktree `worktrees/sd-staffpw`), cut from `main` at `154b5e4`.
**No migration, no dependency, no new setting.** Backend, the admin app, `deploy/` and `docs/15`.

## The defect

Staff and customers shared one `IdentityOptions.Password` and nothing configured it, so every password was
held to Identity's default: **six** characters and four character classes. W3-01 then gave customers a
ten-character minimum as a *separate* check beside Identity's validators (`CustomerPasswordPolicy`, used by
`CustomerAccountValidator` and `CustomerPasswordStore`) and left the staff on six, on the stated ground that
the global options sat in a startup path under review. The admin panel is the higher-value target and had the
weaker password. The admin change-password screen said "at least 6 characters"; the new-staff-user form said
nothing about a rule at all.

## Where the rule lives now

- **`SpencerDenim.Domain.Identity.PasswordPolicy`** (renamed from `CustomerPasswordPolicy`): `MinimumLength = 10`,
  the sentence `Requirement`, `IsLongEnough`. The only place the number is written on the server.
- **`IdentityRegistration`** sets `IdentityOptions.Password.RequiredLength = PasswordPolicy.MinimumLength` (and
  states the four character classes, which are Identity's defaults, so the sentence's promise is a fact of the
  configuration). Because the options are global this *is* the staff rule and the customer rule: every path
  that ends in `UserManager`'s validators is covered, including any added later.
- **`PasswordPolicyErrorDescriber`** words Identity's `PasswordTooShort` (code unchanged) with
  `PasswordPolicy.Requirement`, so a too-short staff password is refused with the same sentence the forms show,
  not a second wording of the length. `CustomerPasswordStore` keeps its own length check (still correct if the
  options are ever changed alone) and now drops the duplicate sentence with `Distinct`.
- The customer pre-check in `CustomerAccountValidator` (Application, cannot see Identity) still reads
  `PasswordPolicy`. Nothing else decides anything from a literal 10.

## Paths covered

| Path | Where it ends in the rule |
|---|---|
| Create staff user (`POST /api/admin/users`) | `StaffUserRoleManagementStore.CreateStaffUserAsync` -> `UserManager.CreateAsync(user, pw)`; refusal is `PasswordRejected` -> 400 keyed `initialPassword` |
| A staff member changes their own password, **and the forced first change** (same route) | `AdminPasswordChanger.ValidateNewPasswordAsync` -> `PasswordValidators`; 400 `new_password_rejected` keyed `newPassword` |
| Seeded first Super Admin (`Seed__Admin__Password` / `SD_SEED_ADMIN_PASSWORD`) | `AdminSeedHostedService` -> `CreateAsync`; **no account, host still starts, Error log carries the sentence** |
| Customer register / change / reset | unchanged behaviour, now also enforced by Identity |
| Deploy-time | `check_env_file` (`deploy/scripts/lib.sh`) refuses a short `SD_SEED_ADMIN_PASSWORD` while `SD_SEED_ADMIN_ENABLED=true`; its `SD_PASSWORD_MIN_LENGTH` is pinned to the policy by `PasswordHintConventionTests` |

**There is no staff "password reset" path to cover.** Doc 08 describes `/admin/forgot-password`, a Super Admin
reset and a break-glass CLI; none exists in the code (grep for `ResetPassword`/`AddPassword` finds only the
customer store, and `Api/Cli` holds only the client-asset import). The next author of any of them must go
through `UserManager` (which the rule now governs) and must not hash a password by hand.

## Existing staff are not locked out

Sign-in and the *current* password of a change only verify a password; the rule applies to a password being
chosen. `A_staff_member_whose_password_predates_the_minimum_can_still_sign_in_and_change_it` builds a six-character
account by writing the hash directly (the validator would refuse to create it), signs in, has a too-short new
password refused as a *new-password* problem (not "wrong current password"), then changes it. **No forced-reset
flow was added or is proposed**: the existing `MustChangePassword` mechanism is untouched, and the only
accounts likely to hold a short password are the seeded Super Admin (forced to change on first sign-in anyway)
and staff created before this change. If the owner wants those rotated, that is a one-line data change
(`MustChangePassword = 1`) per account and a product decision, not code.

## Admin screens

`projects/admin/src/app/core/auth/password-rule.ts` holds the admin app's one copy of the sentence, pinned
byte-for-byte to `PasswordPolicy.Requirement` by `PasswordHintConventionTests` (which also fails if a second
admin source file spells it out). `change-password-page` and `user-form-page` both read it and tie it to the
field with `aria-describedby`; the new-staff-user form previously stated no rule. The 400s render in the places
that already existed (the change-password error list; the red message under the create form). A shared-library
home for the sentence would make it one front-end copy instead of two (storefront + admin), but the shared
library is a coordinated change, so it is left as two pinned copies.

## Decisions worth a second look

1. **`check-env.sh` now fails** (not warns) when `SD_SEED_ADMIN_ENABLED=true` and the password is short. A
   deployment that left the flag on after first install with an old six-character password would be stopped at
   the next `deploy.sh` with a message saying to turn the flag off or lengthen the password. The runbook already
   says to turn it off after the first install; the failure mode of the alternative (a warning) is a first
   install that comes up with no way into the admin panel.
2. **The seeder deliberately does not fail host start** on a short password: a host that already has its Super
   Admin and left the flag on must keep booting. The existing options validation (`Enabled` needs an email and
   a password) is unchanged.
3. The tests write their lengths out (6, 9, 10) instead of reading `MinimumLength`, so they stay an independent
   oracle: a test built from the constant would follow it down if it were lowered. Raising the minimum on
   purpose means updating those rows, `PasswordPolicy.Requirement`, both front-end copies and
   `SD_PASSWORD_MIN_LENGTH`; `PasswordHintConventionTests` fails until all of them agree.

## Verification notes

- Red first (run against `154b5e4`'s behaviour with the new tests only): unit 4 of 6 failed (configured length
  6 not 10; 6-, 8- and 9-character passwords accepted); integration change-password at 9 and at 6 answered
  204 instead of 400; the legacy-account test failed on the same accepted 9-character password. Green after the
  change; mutation checks (minimum put back to Identity's default, describer removed, check-env constant
  changed) are in the task report.
- Integration tests ran on **LocalDB**, not the `TEST_SQL_CONNECTION` this machine had set. That variable
  pointed at the default SQL Server instance (`SW-LAP-0043`, as `sa`), which holds a dozen unrelated databases
  (`daycare_db`, `Fixed_Asset`, `PurchaseOrderDb`, `ReportServer*` ...) — exactly what doc 06 §4 / doc 13 say
  never to do. The fixture only creates and drops `SpencerDenim_Test_<guid>`, so nothing was touched, but the
  variable should be repointed at LocalDB.
