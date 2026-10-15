# 2026-10-10 · Tharanga · #48 — one authenticator code, one session

**Branch** `fix/48-totp-replay` · **Issue** #48 · no migration.

## What was wrong
`ReplayProtectedAuthenticatorTokenProvider.ValidateAsync` read the last accepted TOTP step and later wrote the new
one in two round trips, on an `identity.UserTokens` row with no concurrency token. Parallel requests carrying one
valid code all read the old step before any wrote. Measured before the fix: 6 of 20 concurrent callers accepted
one code; with no row yet, the losing inserts escaped as a primary-key violation (a 500).

## What was done
A single conditional statement now decides: `UPDATE … WHERE Value = <value read>` for an existing row, a guarded
`INSERT … WHERE NOT EXISTS (UPDLOCK, HOLDLOCK)` for the first code. A loser re-reads (at most 3 passes) and is
refused like a sequential replay. Stored format, drift window, recovery codes and lockout counting are unchanged.

## Tester (pass)
Provider-level race tests and the HTTP burst pass 5 of 5; the race tests fail on main's provider. Also checked with
READ_COMMITTED_SNAPSHOT on, with two adjacent codes racing, and with 20 accounts using their first code at once.

## Left for later (not caused by this change)
Two **different** valid codes (steps N+1 and N+2) sent at once on one pending sign-in can still give one 500:
both are accepted in turn, then both try to close the pending sign-in and the second hits
`Optimistic concurrency failure` in `AdminAuthenticator.ClosePendingSignInAsync` → `EnsureSucceeded`. Main's provider
gave 13–19 such 500s per burst; this fix leaves at most one. #46's branch makes `ClosePendingSignInAsync` an
idempotent delete, which should remove it — re-run the burst after both merge, and file an issue if it survives.
