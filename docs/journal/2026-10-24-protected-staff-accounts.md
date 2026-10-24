# 2026-10-24 — Protected and guarded staff accounts (#255)

Branch `feature/protected-staff-accounts`. **No migration.** Asked for by the owner: the technical owner's Super Admin must
never be deactivated, and the client gets their own Super Admin that only the technical owner's account can switch off or on.
The real addresses are deliberately in no file in the repository: they go only in each server's `app.env`.

## What changed

- **Protected accounts** (`AdminSecurity__ProtectedStaffEmails`, from `SD_PROTECTED_STAFF_EMAILS` in `app.env`). Nobody can
  deactivate one, a Super Admin included (409 `protected_account`), and its email address cannot be changed (the protection is
  keyed on the address, so without this it could be renamed and then deactivated). Reactivating is allowed.
- **Guarded accounts** (`AdminSecurity__GuardedStaffEmails`, from `SD_GUARDED_STAFF_EMAILS`). An account handed to someone else,
  such as the client's own Super Admin. Only a **protected** account may deactivate it, reactivate it or change its email address
  (403 `guarded_account`); a name change is still allowed. Read as: nobody else can, the client themself and other Super Admins
  included. If the owner meant something else it is one handler condition (`IsGuarded && !IsProtected(actor)`).
- **Server-side**, in `SetStaffUserActiveCommandHandler` and `UpdateStaffUserProfileCommandHandler`, after the permission checks, so
  a caller who may not manage the user learns nothing about it. A refusal writes no audit row.
- **The screens are told**: the staff list and detail carry `isProtected` and `lockedForViewer` (per viewer). A protected row shows
  "Protected" and a locked guarded row "Managed by the technical owner" instead of Deactivate/Reactivate; the edit form shows a
  note, fixes the email box and does not send the email.
- **Start-up refuses** a malformed address (a typo would leave the account unprotected without telling anyone), and a guarded list
  with no protected list (nobody could ever change that account). Separators alone name nobody.
- **The New staff user form can pick roles.** It had no role boxes, the server refuses a user with no role, and the refusal was never
  drawn, so Create user looked dead. Now: role checkboxes, "Choose at least one role for the new user." in the form (nothing is
  sent; the cursor goes to the first box and the message is tied to the group), and any other server refusal is drawn. The form's
  save buttons use `aria-disabled`, not `disabled` (issue #109), because Create user can now succeed.

## Before it does anything: each server must be told

Both default to empty, so merging changes nothing until `app.env` on **UAT and on Live** names the accounts (the second line needs
the first), then the stack is redeployed (documented in `deploy/app/.env.example` and `docs/15`):

```
SD_PROTECTED_STAFF_EMAILS=<the technical owner's Super Admin address>
SD_GUARDED_STAFF_EMAILS=<the client's Super Admin address>
```

Then create the client's account **after** the deploy, so it is guarded from its first minute: sign in as the technical owner →
Staff users → New staff user → email, first name, last name, a temporary Initial password you choose and hand over yourself (nothing
is emailed), tick **Super Admin** → Create user. The client must change the password at first sign-in and set up two-factor
sign-in (an authenticator app), because Super Admins are required to.

## Tested

Backend unit 4,322 / 4,322; integration `UserEndpoint|GuardedStaffAccount|StaffUser` 44 / 44 against SQL Server; admin frontend, lint
and production build clean (see the pull request for the final counts). An independent tester also drove the rules over HTTP with
real Super Admin sessions (case and padding in config, every non-protected Super Admin against protected and guarded targets in
both directions, the audit rows, per-viewer flags). Its five minor findings are fixed or listed below.

## Limits worth knowing

- **Roles are not locked**: any Super Admin could take the Super Admin role off the technical owner's or the client's account (not
  asked for), after which the protected account could no longer switch the guarded one off.
- **The guard follows the address.** If the protected account renames a guarded account, the new address is not guarded until
  `SD_GUARDED_STAFF_EMAILS` is changed and the stack redeployed.
- **A configured address with no account yet can be claimed** by a Super Admin who renames themself or creates a user onto it, and
  that account is then protected. Only while no such account exists: the technical owner's account is seeded first on every
  environment, so in practice the address is already taken. Reserving the addresses against rename is a follow-up if wanted.
- It protects against deactivation and renaming **through the app**; a change made to the database by hand is not blocked.
- The "Protected" / "Managed by the technical owner" reason on the list rows is a tooltip only (the edit page has the full note).
- Reset password, unlock and reset 2FA are #254 and are not here.
