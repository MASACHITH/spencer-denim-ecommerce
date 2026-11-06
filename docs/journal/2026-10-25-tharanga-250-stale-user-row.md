# 2026-10-25 — Tharanga: issue #250, a sign-in must not save a stale user row

**Branch:** `fix/250-stale-user-row` (from `main` at `057d6f3`). **Status:** developed and tested; independent tester round 1 passed the
product code and asked for more tests (all done, below).

## The defect, in two sentences
Identity saved the **whole** user row, so a password step or sign-in that loaded the account, spent a few hundred milliseconds on the
password hash and then saved, put every column it had loaded back. A deactivation made in that window became "active" again, and a
password change was replaced by the old hash and the old security stamp (which also brought back every session the change had ended).

## What changed
1. **`AppUserStore.UpdateAsync`**: a user the context already tracks is saved with the columns the request changed, and no others (it
   no longer calls `DbContext.Update`). Every `UserManager` save inherits this: storing a token, redeeming a recovery code, resetting
   the counter, the staff and customer password changes, the 2FA settings, the admin's profile edit. The save runs with automatic change
   detection switched on whatever the caller left it at (the audit interceptor assigns `UpdatedAtUtc` just before the write and only
   detection notices an assignment); the caller's setting is given back.
2. **`AdminAuthenticator`**, the two writes that decide a sign-in are statements of their own, conditional on the account still being
   what the check was made against: **active**, with the **security stamp** the password or code was checked against, and **not locked**
   (the test `StaffAccountLockout` uses, against `DateTimeOffset.UtcNow`).
   - the pending-sign-in write (`UPDATE` of the row there is, `INSERT` of the first), `identity.UserTokens` only;
   - the last-login write, `LastLoginAtUtc` plus the two audit columns (a statement outside `SaveChanges` does not pass the audit
     interceptor).
   A refusal for a lockout is answered with the locked outcome (423, the answer the endpoint already gives, never a new status). A refusal
   for anything else is the answer the next attempt would get (401 `two_factor_session_expired` / `invalid_credentials`). A **closed**
   account is never answered as locked (doc 08 §3.1 DB10).
3. #145's lost-race behaviour is unchanged (a pending write that loses to a second step, or to another password step's insert, is a 401,
   not a 500); only a **unique-key violation** counts as a lost race, any other SQL error is still a 500.

## Intended side effects (nothing reads `ConcurrencyStamp`; it is not a concurrency token)
- A sign-in or a pending sign-in no longer moves the user's `ConcurrencyStamp`.
- The password step no longer stamps `UpdatedAtUtc`, and no longer overwrites `UpdatedByUserId` with null for an anonymous request. The
  last-login write stamps both itself: `UpdatedAtUtc` is the time it records, `UpdatedByUserId` is null for an anonymous sign-in, which
  is what the interceptor stamped before.
- A user marked modified **wholesale** (`Update(user)` or `Entry(user).State = Modified` before `UserManager.UpdateAsync`) is still written
  in full, and now that includes `AccessFailedCount` and `LockoutEnd`, which the old store held out of such a save. **No caller does this
  today.** The one that does must adopt the lockout state first (see `StaffAccountLockout.AdoptPersistedLockoutState`).
- A detached user (one this context never loaded) is still written in full. Reachable only by calling the store directly; through
  `UserManager` the validator loads the user first.

## Found, not fixed (do not widen this PR) — file as an issue
**A password hash that needs a rehash is saved from a stale user.** `UserManager.CheckPasswordAsync` (reached from
`StaffAccountLockout.CheckPasswordAsync` via `SignInManager.CheckPasswordSignInAsync`, which every staff password check goes through, and
from `CustomerPasswordStore` line ~280, the customer change-password's current-password check) rehashes a password whose stored hash is
out of date (`SuccessRehashNeeded`): it writes a new `PasswordHash` **and rotates the `SecurityStamp`**, from the user instance the request
loaded. If the password was changed from another session while the hash was being verified, that save puts the OLD password back (hashed
again) and rotates the stamp: the same defect class as #250. **Unreachable today:** nothing produces a hash that needs a rehash (the hasher
writes the current format and iteration count). It becomes reachable the day the iteration count or the hash version is raised. The
customer **sign-in** is already guarded (`CustomerAuthenticator.RehashAsync`: written only `WHERE PasswordHash` is still the one verified,
no stamp rotation). The #250 store change makes this write smaller (hash and stamp only, not the whole row) but not safe.
**Suggested fix:** a `UserManager<AppUser>` subclass that overrides `CheckPasswordAsync`, verifies with `VerifyPasswordAsync` and, on
`SuccessRehashNeeded`, writes the new hash with a compare-and-swap (`UPDATE ... SET PasswordHash = @new WHERE Id = @id AND PasswordHash
= @verified`), as `CustomerAuthenticator.RehashAsync` does; decide whether the stamp should rotate (the customer path says no, Identity
says yes). Test: create the user with a lower `PasswordHasherOptions.IterationCount` on one host, sign in on a second host with the
higher count, and change the password through the real route while the sign-in is held at its write (the `UserRowWriteGateInterceptor`
pattern); it needs a gate that matches the rehash statement. Needs an owner decision on the stamp; not small enough for this PR.

Ready-to-file issue text: *"Rehash after a successful password check saves a stale user (PasswordHash + SecurityStamp). Same defect class
as #250: UserManager.CheckPasswordAsync, reached from StaffAccountLockout.CheckPasswordAsync and CustomerPasswordStore (change-password),
rewrites the hash and the stamp from the user loaded at the start of the request, so a password change made meanwhile is undone and its
sessions come back. Unreachable until the hasher's iteration count / version is raised. Fix: compare-and-swap rehash as
CustomerAuthenticator.RehashAsync does, in a UserManager subclass; decide whether the stamp rotates."*

## Tests (all in `tests/SpencerDenim.IntegrationTests`)
- `Api/Admin/AdminTwoFactorEndpointTests`: gated (the `UserRowWriteGateInterceptor` and `PendingSignInWriteGateInterceptor` hold one
  write so the account can be changed inside the window; they fail loudly if nothing matches): deactivation, password change (through the
  real change-password route), lockout and, for the pending write, a rotated stamp, each on **both** branches of the pending write
  (no pending row yet: `INSERT`; one already there: `UPDATE`) and on the second step and the single-step sign-in; a closed-and-locked
  account is not answered as locked; a non-unique SQL error on the pending write is a 500; the recording write stamps the audit columns.
- `Identity/AppUserStoreChangedColumnsTests`: two scopes (two `DbContext`s), no sign-in needed; one-changed-column statement text; the
  normalised email and user name reach the database; a save with automatic change detection off.
- `Api/Admin/UserEndpointTests`: an administrator changes a staff user's email through the real route; the new address signs in, the old one
  does not.
- The natural timing window was reproduced **only with the gates**, never by plain timing.
