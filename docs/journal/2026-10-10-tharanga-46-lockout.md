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

## Owner review, 2026-10-12 — what was changed in response
The owner verified this branch with no CI (Actions is down account-wide) and returned one Major, a test fix and three Minors.
- **Major — the two post-verification lockout re-checks had no test** (`StaffAccountLockout.CheckPasswordAsync`,
  `AdminAuthenticator.CompleteTwoFactorSignInAsync`; deleting either left every test green). A fresh right-password request is stopped by
  Identity's own lock check before the second look matters, so the only shape that exercises it is a right secret *in flight* while a burst
  locks the account. `LockoutRecheckGateInterceptor` holds that one read (matched by its three-column projection without `PasswordHash`);
  `A_right_password_that_a_burst_of_wrong_ones_outruns_is_refused_as_locked` and
  `A_right_code_that_a_burst_of_wrong_passwords_outruns_is_refused_as_locked` lock the account with five wrong passwords while it waits and
  require a 423 with no session. **Proven by mutation, both ways:** keeping the query but making it never refuse gives `Got 200` with a full
  session body on both tests; deleting the second look entirely fails both on the gate's timeout, whose message names the removal as the
  first cause so it cannot be mistaken for a stale matcher.
- **Test that could not fail** (`Parallel_second_steps_with_the_same_authenticator_code_…`, `<= 1` → `== 1`): the owner's `776427f`,
  cherry-picked unchanged.
- **Minor 1:** the parallel gate's `TaskCompletionSource` now uses `RunContinuationsAsynchronously`.
- **Minor 3:** the claim comment in `AdminAuthenticator` names only the recovery code, and does *not* describe #142's compare-and-swap,
  because that is not on this branch yet.
- **Minor 2 — not done, deliberately:** the lockout counter runs as a raw `DbCommand`, outside `EnableRetryOnFailure`. Wrapping it in
  `CreateExecutionStrategy().ExecuteAsync` changes how the statement behaves inside a caller's transaction (an execution strategy refuses a
  user-initiated transaction that it does not own). Nothing opens one on a sign-in path today, but that is a behaviour change on the
  sign-in path three days before launch, so it is left for the owner's call.
- **Merge order is #144 → #146 → #142.** The #142 × #146 conflict is tests-only (`AdminTwoFactorEndpointTests.StartAsync`); keep this
  branch's signature — it is now `StartAsync(configuration, DbCommandInterceptor? commandInterceptor)`, a superset of #142's.
