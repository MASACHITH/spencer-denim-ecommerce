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
