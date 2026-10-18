# 2026-10-17 — Tharanga: #179, no address and no credential in the web servers' logs; the page-view limit

Branch `fix/179-gateway-logs`, worktree `worktrees/sd-gwlogs`, cut from `origin/main` at `154b5e4`. Not pushed, no PR.
**There is no CI**: every figure below is from `tools/verify-gateway-logging.sh` on this machine, against real nginx
(the pinned `nginxinc/nginx-unprivileged:1.30.5-alpine3.24` for the gateway, `nginx:1.24-alpine` for the host tier,
because the VPS runs Ubuntu 24.04's 1.24).

## What changed

| Where | What |
|---|---|
| `deploy/gateway/nginx.conf` | Log line rebuilt: `path` (from `$uri`, then reduced by `$sd_log_path`) replaces `uri`; `remote_addr`, `peer` and `referer` removed; `referer_host` added (a dotted host name, nothing else). `error_log` is now `crit`. |
| `deploy/gateway/templates/gateway/sd-gateway.conf.template` | `limit_req_zone` + `location ~* ^/api/traffic/page-views/?$` (20r/s, burst 40, `nodelay`, 429 turned into 204 by `error_page`, a distinct named location so a real API 503 is not masked); one throttled request in 256 is logged. |
| `deploy/docker/frontend.Dockerfile`, `deploy/app/compose.yml` | `SD_PAGE_VIEW_RATE=20r/s`, `SD_PAGE_VIEW_BURST=40`. |
| `deploy/host-nginx/spencer-denim-{production,staging}.conf.template` | `access_log off` in **both** server blocks (the port-80 redirect used to land in the shared, combined-format `access.log`); `error_log … crit` in `/var/log/nginx/spencer-denim/<env>.error.log`. |
| `deploy/host-nginx/spencer-denim.logrotate` | New: 3 days, for that directory. |
| `docs/15` | §5 note on `install.sh` before `db-provision.sh` (RCSI), step 5 creates the log directory and installs the rotation, new §8a. |
| `tools/verify-gateway-logging.sh` | New: the issue's acceptance test, runnable. |

## Decisions

1. **Allow-list, not a deny-list, for the path.** W4-03's `PagePath` is an allow-list (a segment survives only if it
   is a slug) and the gateway copies that. On top: explicit rules for the routes that carry a credential **in the
   path**, because the allow-list recognises a token by shape only. Enumerated from `app.routes.ts` /
   `account.routes.ts` / the admin `app.routes.ts` and every API route template: `/order/{token}`,
   `/api/orders/{id}[/…]`, `/api/payments/paypal/{start,return,cancel}/{id}`, `/api/admin/orders/{id}[/…]`,
   `/api/admin/orders/bank-transfers/{id}/…`. The API ones are the find that "`/order/*` at minimum" would have
   missed: the order page calls `GET /api/orders/{token}`, so the token is in the path of an API call too.
2. **`$uri`, not `$request_uri`.** Path only, decoded and normalised, so `/%6frder/…`, `//order//…`, `/x/../order/…`
   are all `/order/*`; and behind `try_files` an admin screen is `/admin/index.html`, so `/admin/orders/{guid}` never
   reaches the log.
3. **No visitor address at all**, not a `/24`. Nothing in the issue needs one, the application stores none, and a
   truncated address is a decision for the owner with a retention (docs/15 §8a says how).
4. **Host nginx: `access_log off`, not a redacting format.** A redacting format needs a `log_format` + `map` in the
   `http` context, which means a shared include on a server that hosts the client's other sites, and a second
   definition of the same name would stop nginx. The gateway already logs every request, redacted. Cost: a 502 from
   the TLS hop is not logged anywhere (it means the gateway is down: `docker compose ps`).
5. **Error logs at `crit`.** nginx writes the client address, the whole request line with its query string, the
   upstream URL and the `Referer` into most error lines, and no format reaches them. They are written at `error`
   and `warn` and therefore on every deploy, while the gateway restarts. `crit` never carries a request in normal
   operation. The cost is nginx's own explanation of a failure; the access log still has status, upstream and
   duration. How to get it back, and what to purge afterwards, is in §8a.
6. **Rate-limit location is a case-insensitive regex with an optional trailing slash, not `location =`.** The API's
   routing ignores both, so `POST /api/traffic/page-views/` and `/API/Traffic/Page-Views` reached the endpoint
   unthrottled around an exact match. The script tries four spellings.
7. **429 internally, 204 to the browser**, in a named location: `limit_req_status` only takes 400–599 and 503 would
   hide a real 503 from the API.
8. **A host log directory of its own** (`/var/log/nginx/spencer-denim/`), because Ubuntu's `/etc/logrotate.d/nginx`
   already rotates `/var/log/nginx/*.log` for 14 days and logrotate refuses two stanzas for one file. Documented, not
   in `install.sh`: `install.sh` installs nothing on the host's nginx today. The directory must exist before
   `nginx -t` (docs/15 §5 step 5).

## Not done, found on the way

- **The API's request log writes the order token** (`UseSerilogRequestLogging`: `RequestPath` is the path, query
  excluded, and the hosting scope puts the same path on every other event in the request). No IP, no query string.
  Needs a change in `SerilogSetup.cs`; left alone, reported in docs/15 §8a "Still open".
- **The storefront's `answerRenderFailure` logs `GET <originalUrl> failed`** with the query string, on an
  unexpected render failure. Frontend code; left alone.
- Both containers keep 10 MB × 5 and are recreated by a deploy; that bounds, not removes, the exposure.
- The Docker `json-file` cap is a size, not a time. At the shop's traffic it is days to weeks; fine now that the
  gateway log holds no personal data.

## For the tester

Start with the path table in `tools/verify-gateway-logging.sh` (try spellings it does not have), the two `crit`
decisions (provoke an error that the access log would not explain), and the rate-limit spellings. Then run the
script against `git archive 154b5e4 deploy` and confirm it fails.
