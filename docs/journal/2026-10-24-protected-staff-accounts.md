# 2026-10-24 — Protected staff accounts: an account that can never be deactivated

Branch `feature/protected-staff-accounts`, worktree `worktrees/sd-protect`, cut from `c749181` (the pushed build fix for
`main`, `fix/blocked-countries-tier-fields`), so it must be rebased onto `main` once that fix is merged. **No migration.**
Not committed, not pushed. Asked for by the owner's side: `tu2slhosting2@gmail.com` is a Super Admin that must never be
deactivated, and its Deactivate option must go.

## What changed

- **The rule is on the server.** `IProtectedStaffAccounts` (Application) is read by `SetStaffUserActiveCommandHandler`: a
  deactivation of a protected account answers **409 `protected_account`** ("This account is protected and can never be
  deactivated."), whoever asks, a Super Admin included. A **reactivation is still allowed**. The check runs *after* the
  permission checks, so a caller who may not manage the user gets the ordinary refusal and learns nothing about the account.
- **The email address of a protected account cannot be changed** (`UpdateStaffUserProfileCommandHandler`, 409 as above). The
  protection is keyed on the address, so without this a Super Admin could rename the account and then deactivate it.
- **The list and the detail carry `isProtected`.** The Staff users list shows "Protected" instead of a Deactivate button; the
  edit page shows a note instead of the button and disables the email box.
- **Configuration, not code.** `AdminSecurity__ProtectedStaffEmails` (addresses separated by commas, semicolons or spaces,
  case-insensitive), passed through by `deploy/app/compose.yml` from **`SD_PROTECTED_STAFF_EMAILS`** in `app.env`
  (documented in `deploy/app/.env.example`). The address is deliberately in no file in the repository.
- **A typo stops the server at start-up** (`ValidateOnStart`: every entry needs one `@` with text either side). A mistyped
  address would otherwise leave the account unprotected and say nothing.

## ⚠ Not active until each server is told (the one step left)

The rule protects nobody until a server names an account. On **UAT and on Live**, add this line to `app.env` and redeploy:

```
SD_PROTECTED_STAFF_EMAILS=tu2slhosting2@gmail.com
```

(Several accounts: `a@x.com,b@y.com`.) Nothing in the repository sets it, on purpose (the project rule is that configuration
comes from the environment, and this keeps the address out of source control). After the deploy, open Staff users: the account
shows "Protected" and has no Deactivate button.

## Tested

Backend unit **4,250 / 4,250** (new: five deactivation, three profile, a configuration-parsing set, three registration, among
them the start-up refusal of a typo); integration `UserEndpointTests` **27 / 27** against the real API and SQL Server (new: a
protected account is flagged on the detail and the list, deactivation answers 409 `protected_account` and leaves it active, an
ordinary colleague with the same role still deactivates, and a protected account can be reactivated). Admin **945 / 945**, lint
clean, production build clean.

## Limits worth knowing

- It protects against **deactivation and renaming through the app**. Removing the account's Super Admin role is not blocked
  (not asked for); a database change by hand is, of course, not blocked either.
- Protected accounts are named per environment, so UAT and Live each need the line above.
- The user guide (`#199` branch) describes this under Staff users.

# The client's own Super Admin (guarded account) and the New staff user form

The owner asked for a second Super Admin for the client to use on UAT, which only the technical owner's account
(`tu2slhosting2@gmail.com`, protected above) may activate or deactivate. Read as: **nobody else can switch it off or on,
or change its email address, the client themself and other Super Admins included.** If the owner meant something else, it is
one handler condition (`IsGuarded && !IsProtected(actor)`).

## What was built

- **Guarded accounts**, a second server-side list beside the protected one: `AdminSecurity__GuardedStaffEmails`
  (`SD_GUARDED_STAFF_EMAILS` in `app.env`). `IProtectedStaffAccounts.IsGuarded`. A guarded account can be deactivated,
  reactivated or renamed **only by a protected account**. Enforced in `SetStaffUserActiveCommandHandler` (`GuardedAccount`,
  403 `guarded_account`, both directions, whatever state it is already in) and `UpdateStaffUserProfileCommandHandler`
  (`GuardedEmail`; a name change is still allowed). Start-up refuses a malformed address, and refuses a guarded list with no
  protected list (nobody could ever change the account).
- **The screens are told**: `lockedForViewer` on the list row and the detail, set per viewer by the two query handlers. A
  locked row shows "Managed by the technical owner" instead of Deactivate/Reactivate; the edit form shows a note, fixes the
  email box and does not send the email.
- **The New staff user form can pick roles.** It had no role boxes, the server refuses a user with no role, and the form drew
  no line for the refusal (`roleIds`), so Create user did nothing visible. Now: role checkboxes in the form (the same boxes
  as the Roles section), a message in the form if none is ticked (nothing is sent), and any server refusal for a field the
  form has no line for is drawn instead of dropped.

## Not active until each server is told

Add to `app.env` on **UAT and Live** (both lines; the second needs the first), then redeploy:

```
SD_PROTECTED_STAFF_EMAILS=tu2slhosting2@gmail.com
SD_GUARDED_STAFF_EMAILS=colombowardrobecompany@gmail.com
```

Then create the account: sign in as `tu2slhosting2@gmail.com` → Staff users → New staff user → Email (the guarded address),
First name, Last name, Initial password (a temporary one you choose and hand over yourself: nothing is emailed), tick the
**Super Admin** role → Create user. The client must change the password at first sign-in and then set up two-factor
sign-in (an authenticator app on their phone), because Super Admins are required to. Create the account **after** the
deploy, so it is guarded from its first minute.

## Tested

Backend unit **4,277 / 4,277** (new: `GuardedStaffAccountTests` (handlers and the locked flag), options and registration
tests); integration `UserEndpointTests` **28 / 28** (new: a colleague is refused both ways and shown the account locked; the
protected account switches it off and on). Admin **956 / 956**, lint clean, production build clean.

## Limits worth knowing

- Roles are not locked: any Super Admin could take the Super Admin role off the technical owner's or the client's account
  (not asked for). The protected account then could not switch the guarded one off.
- The user guide (`#199` branch) says creating a staff user does not work and does not mention guarded accounts; both need
  updating once this merges.
