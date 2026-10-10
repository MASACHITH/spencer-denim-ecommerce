# 2026-10-10 · Tharanga · #46 — parallel failures can no longer outrun the lockout

**Branch** `fix/46-parallel-lockout` · **Issue** #46 · no migration.

## What was wrong
Identity's `AccessFailedAsync` / `CheckPasswordSignInAsync(lockoutOnFailure: true)` is a read-modify-write on
`AccessFailedCount` with no concurrency check (`AppDbContext` is a plain `DbContext`, not `IdentityDbContext`), so
parallel failures overwrote each other: 60 parallel wrong passwords left the count at 4 and the account unlocked, and parallel
wrong 2FA codes got 8–17 checks per window instead of 5. A second problem sat under it: `UserStore.UpdateAsync` marks every
column modified, so any later save of a user loaded before the lockout (the 2FA password step, `LastLoginAtUtc`, recovery-code
redemption) wrote the old `AccessFailedCount`/`LockoutEnd` back and could undo a lockout.

## What was done
- `StaffAccountLockout.RecordFailedAttemptAsync`: one `UPDATE [identity].[Users] … OUTPUT` that adds one, sets `LockoutEnd` and
  resets the count at the threshold, skips an account already locked or with `LockoutEnabled = 0`, and tells only the attempt that
  trips the lock (so exactly one `Lockout` audit row). A correct password or code is accepted only if a fresh read shows the account unlocked.
- `AppUserStore` replaces the EF user store: a tracked user's save leaves `AccessFailedCount`/`LockoutEnd` out of the `UPDATE` unless this
  request changed them.
- Pending 2FA sign-in is now a **claim**: the success path deletes the pending-token row restricted to this sign-in's hash and treats the
  affected-row count as the claim; a loser answers `two_factor_session_expired`. (An earlier version of this branch made the close
  idempotent everywhere, which let one recovery code open up to 12 sessions — the tester caught it; main allowed exactly one.)
  The idempotent close stays on the wrong-code / lockout / stale paths so parallel failures do not 500.

## Tester
Round 1 FAIL (the claim regression above, `LockoutEnabled = 0` reporting a lockout that does not exist, a comment typo — all fixed).
Round 2 PASS: same code ×12 gives exactly one session with and without delayed token writes; mixed wrong/right codes, different valid recovery
codes, a racing second password step, sequential flows and lockout expiry all behave; `LockoutEnabled = 0` is inert; a normal account still locks at 5.

## Known, not caused by this change
A second password step racing the second step of the same account can answer 500 (`EnsureSucceeded` in `ClosePendingSignInAsync`/
pending-token update after the other request deleted the row). `origin/main` gives the same 500 in the same timing; the session outcome is
still correct and the user retries. Filed as an issue.
Also: a request that loses the claim has already spent its recovery code / TOTP step (as on main); #48's provider fix (PR #142) closes the TOTP
replay at the provider level independently.
Per-account / per-IP rate limiting on the login endpoints (the issue's "fix idea") is not included.
