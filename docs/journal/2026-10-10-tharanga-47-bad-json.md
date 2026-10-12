# 2026-10-10 · Tharanga · #47 — a request the API cannot read is a 400

**Branch** `fix/47-bad-json-400` · **Issue** #47 · no migration.

## What #47 really was
Two things sat under one title. The owner's diagnosis (2026-10-02) is the important one: `POST /api/admin/auth/login` with
a **well-formed object that leaves a property out** — `{}`, `{"email":"a@b.c"}` — answered 500. `System.Text.Json` ignores
nullable annotations, so the omitted property bound null and `ArgumentNullException.ThrowIfNull` inside
`AdminAuthenticator` answered. The other half: minimal APIs throw on a bad body in Development only, so `{bad` was a 500
there and a bodyless 400 elsewhere.

## What was done
- `AdminLoginRequest` is `(string? Email, string? Password)`; the endpoint returns a 400 validation problem
  (`code: missing_field`) naming each omitted field before the authenticator is reached. The `ThrowIfNull` guards stay.
  A missing-field request never touches the database, counts towards no lockout and writes no audit row (it was a 500
  with no audit row before). An empty string is still a wrong credential and takes the normal path.
- `ThrowOnBadRequest` is on in every environment and `BadRequestExceptionHandler` answers the throw with a problem
  document (`malformed_json` / `bad_request`). Its detail is fixed text plus the JSON path — never the exception message,
  which named internal CLR types and framework wording. A caller whose `Accept` header no problem writer matches gets the
  problem document anyway.

## Tester
Round 1 PASS on the handler but flagged that `{}` still 500'd (the real issue), the type-name leak and the Accept case — all
fixed. Round 2 (focused, no full suite) PASS: production and development responses byte-identical, OpenAPI still
generates, no new regression.

## Left alone, on purpose
`bad_request` here versus `empty_body` from `AdminWriteRequest.ReadJsonAsync` for "no body": the exception cannot tell an
empty body from a JSON `null`. The doc comment now says so.

## Owner review, 2026-10-12 — the named change, and what it did and did not establish
The owner verified this branch with no CI (Actions is down account-wide) and asked for one change: the 8
`A_sign_in_with_a_field_left_out_is_a_400_naming_the_field` cases had answered **500** in one full-assembly run beside a
second suite, then passed 174 times in a row, and the failure said only `Expected 400, Actual 500`. The cause was unknown.
- **Done: a failing request now explains itself.** Every assertion in `MalformedRequestBodyTests` goes through a `Probe`
  that keeps what the pipeline threw (an `IExceptionHandler` registered *after* the bad-request handler, so it sees only
  what that one declined — the exceptions that become a 500) and everything the host logged at Error, and puts both, with
  the response body, in the failure message. The log capture is a Serilog `ILogEventSink`, **not** an `ILoggerProvider`:
  `AddSerilog` without `writeToProviders` ignores logger providers, so a provider would have been accepted, never
  called, and looked like a quiet host.
- **Proven able to fire, by mutation:** with the missing-field guard neutered (the owner's mutation C) all 8 cases fail
  with the response body, `thrown in the request: System.ArgumentNullException … (Parameter 'email')`, the logged
  `ExceptionHandlerMiddleware` line, and the test host's other Error lines. Guard restored; only the test file differs.
- **Note for whoever reads one of those messages:** `CatalogSeedHostedService` logs an Error at the start of *every*
  test host here (the database is deliberately unreachable), so "nothing was logged at Error" will never appear; the
  `thrown in the request:` line is the one that names a cause.
- **Run: the whole integration assembly twice, at the same time on one LocalDB instance** (the condition the owner's
  failure happened in): **1,386 / 1,386 and 1,386 / 1,386**, 57 and 56 minutes. **The intermittent 500 did not recur, so
  its cause is still not known.** Two clean runs do not show it is gone; what they show is that the next occurrence will
  be diagnosable. A third, quiet run was stopped to free the machine for #146's.
- Run on LocalDB (docs/06), not the `TEST_SQL_CONNECTION` this machine's environment points at.
- **Not done (the owner's Minors, none blocking):** `BadRequestExceptionHandler` hardcodes `Title`/`code` so a Kestrel 413
  arrives titled "Bad request" (matters now the gateway streams large uploads, #143); `traceId` is added on the
  Accept-fallback path only and no test asserts it.
