# 2026-10-17 — Tharanga: #201, the API's log and the storefront's log no longer hold a credential

Branch `fix/201-token-logging` (worktree `worktrees/sd-tokenlogs`), cut from `main` at `154b5e4`. **No migration,
no dependency, no setting.** Follow-up to #179: the nginx half is `fix/179-…` (`worktrees/sd-gwlogs`); this is the
two layers behind it.

## What leaked, and where

| Where | What was written | Why |
|---|---|---|
| API request-completion line, `SerilogSetup.cs` (`UseSerilogRequestLoggingWithHealthFilter`) | `RequestPath` = `/api/orders/{token}`, `/api/orders/{token}/payment-slip`, `/api/payments/paypal/return/{token}`, `/api/admin/orders/…` | Serilog logs the request path as it came, and the order token is a path segment |
| **Every other event written during a request** (EF Core connection and query errors, the exception handler's "unhandled exception" error, throttle warnings) | the same `RequestPath` property, plus `RequestId` and `ConnectionId` | ASP.NET Core's hosting layer opens a logging scope with the raw path, and Serilog's `ILogger` bridge copies a scope's properties onto every event written inside it. The deployed JSON console writes all properties. **This one is not in the issue**; fixing only the line it names would have left the token in every error. |
| ASP.NET Core's own `Request starting …/{token}?…`, `Request finished`, the matcher's "candidate(s) found for the request path", Kestrel's bad-request lines | the path and, for the first two, the query string | Off today only because `appsettings.json` sets `Microsoft.AspNetCore` to Warning. An environment variable that lowers it writes every order token |
| Storefront `server/render-failure.ts` (was line 60) | `GET /account/reset-password?token=… failed` | `request.originalUrl` carries the query string |
| Storefront, same call | the raw error object | Node's invalid-URL error has an `input` property holding the whole URL, which `console.error(error)` prints |
| `@angular/ssr` itself (`node.mjs:344`, `ssr.mjs:1323`, `ssr.mjs:1597`) | `An error occurred while writing the response body for: ${req.url}`, `…inlining critical CSS for: ${url}`, `ERROR: Bad Request ("${request.url}")` | Library code, query string included; the last one is written for every request with a `Host` the engine will not trust |

**Checked and clean:** no visitor address is written by either. Serilog's request line has no address property and
nothing enriches with one; `EnableSensitiveDataLogging` is not set anywhere, so EF Core prints `'?'` for every
parameter; the SSR server never prints a header. The one place the API ever logs an address is the four throttles'
"requests are arriving from {Address}, which is not a visitor's own address" warning, once per process, and only when
the address is loopback, private or unknown (`WarnIfTheAddressIsNotAVisitor`): the gateway's, not a shopper's.

## The one rule

`RequestPathRedaction.ForLog` (`SpencerDenim.Application.Privacy`) is the single definition of which segments are
secret **for anything that logs a path**. It is `PagePath`'s slug rule (W4-03: what traffic recording may store)
plus the two extra checks the gateway's map adds (a GUID with its hyphens anywhere, a 16-character word), plus the
five routes that carry a credential in the path, named so a token whose shape changes cannot slip through:
`/order`, `/api/orders`, `/api/payments/paypal/{start|return|cancel}`, `/api/admin/orders/bank-transfers/{x}`,
`/api/admin/orders/{x}`. Everything else is an allow-list: the first segment that is not a safe slug, and all after
it, become `*`. The query string and fragment are cut first.

**Three implementations, one table.** The gateway's `$sd_log_path` map (nginx), the C# above and the storefront's
TypeScript port (`server/log-path.ts`) are checked against `tests/Shared/log-path-vectors.json`: 103 spellings (case,
trailing and double slashes, `;param`, percent-encoded separators, long tokens, token-shaped segments on unknown
routes, line feeds, seven segments deep, 100/101 characters). **Every row was run through the gateway's actual map**
(from `worktrees/sd-gwlogs`, evaluated the way nginx evaluates a `map`) before it went in: 103 of 103 agree.
`GatewayLogMapParityTests` goes one step further and compares the *patterns* in `deploy/gateway/nginx.conf` with
`RequestPathRedaction.CredentialRoutes` and the numbers in the generic rule. **It skips until #179 puts the map on
`main`, then runs by itself**; I ran it by hand against the #179 file (3 passed) and against two altered copies (a
dropped `cancel`, `{1,100}` changed to `{1,120}`: each turned the matching test red).

One deliberate difference: the gateway also keeps a static file name as the last segment and strips a final line
feed; the code ports do not and star it. Both print no line break, so neither is a leak; no row in the table has one.

## What changed

- `Application/Privacy/RequestPathRedaction.cs` (new): the rule.
- `Api/Logging/SerilogSetup.cs`: the request line prints `ForLog(path)` under the same four property names
  (`RequestMethod`, `RequestPath`, `StatusCode`, `Elapsed`), so a consumer of the log sees the same shape; a
  `RequestPathRedactingEnricher` redacts `RequestPath` on **every** event; and events below Warning from
  `Microsoft.AspNetCore.*` are dropped in code (`IsAspNetCoreBelowWarning`) whatever level the configuration asks for.
  That last one is a trade: a deployment can no longer turn those categories on to chase a routing problem. They are
  off today in `appsettings.json`, so nothing is lost relative to the deployed behaviour, and the alternative is a
  switch that writes every order token to a file Docker keeps.
- `Application/Analytics/PagePath.cs`: `SlugPattern` ends `\z` instead of `$`. In .NET `$` also matches before a
  final line feed, so a decoded `%0A` made `women\n` a slug: stored in `PageViews.Path`, and, now that the log
  prints the same segments, a forged log line in text mode.
- Storefront `server/log-path.ts` (the port), `server/log-redaction.ts` (`redactLogText`, `describeError`,
  `installConsoleRedaction`), `render-failure.ts` (prints `logPath(originalUrl)` and `describeError(error)`),
  `server.ts` (wraps the console once, first). The wrapper is the net under what `@angular/ssr` prints on its own:
  every console line is formatted as Node would and then has any query string removed and any credential route
  starred. It cannot know a secret that is neither (a token in an unrecognised path, printed by a library outside a
  query string); that is why the line this server writes itself uses the allow-list and not the filter.

## Tests, and that they can fail

- **Integration, `RequestLogRedactionTests` (11):** the real API in memory, Production environment, the
  deployed `appsettings.json` levels (`everyLevel: false`) **and** every logger at Verbose (`everyLevel: true`).
  Every route with a GUID segment is read from the endpoint table (24 method/route pairs) and driven with a fake
  token in the path **and** a query carrying a reset token, a PayPal `PayerID`, an email and a `returnTo` that
  repeats the token; plus the failure paths (404 from a missing route and from a handler, 400 for a body and for a
  value, 405, 415, 401/403, 500 from an unreachable database), 20 other spellings, and a visitor on a public address
  with an `X-Forwarded-For`. Then **everything the host logged**, every property and the exception text included, is
  searched for the token (hyphenated, plain, upper case, first eight characters), the query secrets and both
  addresses. A second test fails when a route with a GUID segment is added that is in no credential family.
- **Red on the unmodified code:** 10 of 11 failed (the 11th is the route-family check, which does not read the log).
  At the deployed levels the token was in `RequestPath` of the request line **and** of EF Core's connection error,
  EF Core's query error and the exception handler's error. Output kept in the scratchpad as `tokenlogs-red2.txt`.
- **Green 11 of 11** after the change. Along the way the run with the enricher and the request-line fix but **no
  floor** was red only at Verbose (`Hosting.Diagnostics` and `DfaMatcher` lines), green at the deployed levels:
  that is the floor shown necessary.
- **Real process, real Kestrel** (`dotnet SpencerDenim.Api.dll`, Production, compact JSON console, run from its own
  folder: from another folder `appsettings.json` is not found and every level falls back to Information, which
  I found out by getting it wrong the first time). 25 requests with a fake token in the path and secrets in the query
  and two Kestrel-level bad requests; stdout and stderr searched afterwards:

  | | token | query secrets | forwarded address | lines |
  |---|---|---|---|---|
  | before, deployed levels | 44 lines | 0 | 0 | 63 |
  | **after, deployed levels** | **0** | **0** | **0** | 61 |
  | before, every logger Verbose | 335 lines | 46–52 | 0 | 690 |
  | **after, every logger Verbose** | **0** | **0** | **0** | 258 |

  The request-completion lines now read `/api/orders/*`, `/api/payments/paypal/return/*`,
  `/api/admin/orders/bank-transfers/*`, `/api/admin/orders/*`, `/api/nothing/*`, `/*`, and unchanged
  `/api/catalog/search`, `/api/account/reset-password`.
- **Unit:** the 103-row table through the C# rule; a seeded fuzz of 6,000 paths (every output segment is a safe slug,
  `*` or a PayPal action, no control character); idempotence; the enricher on a string, a `PathString`, a structured
  value and no property; the request-line properties; the floor, per category and level; `PagePath` and a line feed.
- **Storefront (vitest, 147 tests in the three server specs):** the same table through the port; the exact lines
  `@angular/ssr` prints, copied from its source; `describeError` on an invalid-URL error, a cause chain and an
  aggregate; the console wrapper on a fake console; and **a real Express app on a real socket** whose route throws
  with the request address in its message and in an invalid-URL error's `input`, the handler `server.ts` mounts,
  the wrapped console, four failing requests (reset link, confirm link, guest order link, a phone-number search):
  nothing printed contains the token, the query, the email, the phone number or `input`.
- **Mutations** (one at a time on committed code, restored after each): see "Mutation results" below.

## Not fixed, found on the way

1. **`SendEmailsService.cs:276`/`:407` writes an order token to the log.** When an email has failed every attempt,
   `LogMessageDead(logger, claim.TemplateCode, claim.RelatedEntityId, …)` prints `{OrderPublicId}` at Error, and for
   an order email `RelatedEntityId` **is the order's public token** (`OrderEmailFacts.cs:44`, "the order's public
   token … is the `RelatedEntityId` on the outbox row"). Rare, but it is the same credential in the same Docker log.
   The fix is to print the outbox row id, which is already in the message. Left because it is not a request log and
   the brief was scoped to the request path; **it wants its own issue**.
2. The gateway keeps a final line feed out of a path and the code ports star the segment (above). Harmless.
3. Below Warning, `Microsoft.AspNetCore.*` is now off whatever the configuration says. If someone ever needs those
   categories, the floor in `IsAspNetCoreBelowWarning` is the line to change, and the integration tests' `everyLevel`
   cases will say what they then write.
4. In Development, minimal-API binding errors (`ThrowOnBadRequest`) put the offending *value* in the exception
   message, which the exception handler logs. Not deployed, not checked further.

## Environment note

`TEST_SQL_CONNECTION` on this machine points at the **default** SQL Server instance (an `sa` login), which holds
unrelated databases, against docs/06. Every test I ran used LocalDB
(`Server=(localdb)\MSSQLLocalDB;Trusted_Connection=True;TrustServerCertificate=True`) set for that command only. The
only thing I did on the default instance was one read-only listing of database names at the start, to confirm what it
held.
