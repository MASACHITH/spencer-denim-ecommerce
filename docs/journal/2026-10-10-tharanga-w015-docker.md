# 2026-10-10 · Tharanga · W0-15 (#41) — Docker packaging, resumed and tested

**Branch** `feature/W0-15-docker-packaging` · **Issues** #41, #96 (key ring), #86 (public origin) · no migration.

## Where it stood
Paused 2026-09-29 with a WIP commit: four images, compose per environment, host nginx templates, scripts, runbook
doc 15. Open: the storefront returned an empty shell through the gateway; nothing had been run. Rebased onto main
(52 commits; one conflict, `ApiFactory`'s service-override parameter, kept main's name `configureServices`).

## What was done
- **SSR through the gateway.** Angular's SSR engine falls back to the empty client-side shell when it receives any
  `X-Forwarded-*` header it was not told to trust. Compose sets `NG_TRUST_PROXY_HEADERS=x-forwarded-host,x-forwarded-proto`,
  the gateway sends the storefront only those two, and `server.ts` drops every other `x-forwarded-*` before rendering.
- **#96 key ring.** Persisted to a volume, application name pinned, optional certificate (`DataProtection__CertificatePath`).
  Production refuses to start without `DataProtection__KeysPath`. The migrator builds the API host, so it gets a throwaway path.
- **#86.** One `SD_PUBLIC_ORIGIN` feeds `PUBLIC_SITE_ORIGIN` and `Email__StorefrontBaseUrl`; `check-env.sh` refuses anything but
  scheme://host[:port]; `deploy.sh` checks that `robots.txt` names this host.
- **Media (W1-04 landed after the branch was cut).** The API serves `/media/public/`; the gateway proxies it. The rest of
  `/media` is the storefront's webfonts. Public/private/spool volumes mounted on the API only.
- **Gateway body limits.** 2 MB by default; `/api/admin/media/` 67 MB and the payment-slip route 6 MB, both streamed
  (`proxy_request_buffering off`). The RAM-backed `/tmp` is where nginx buffers bodies.
- **IPv6.** Throttles key a public IPv6 visitor by /64 (`ClientAddressPartition`).
- **Local development.** `deploy/database/compose.local.yml`: SQL Server cannot keep its files on a Windows bind mount
  under Docker Desktop (`[AppLoader] Failed to load LSA`), so named volumes.

## How it was tested
Full stack built and run locally; the admin session survives `docker restart` and `up --force-recreate`; rehearsal of the
server flow (install, check-env, db-up, db-provision, deploy, deploy with pre-deploy backup, rollback, backup-db) in a
docker-in-docker stand-in for the VPS. Two tester rounds: round 1 FAIL (1 Blocker, 2 Major, several Minor — all fixed),
round 2 PASS with one Minor (the video+poster upload ceiling, fixed in the final commit).

## Not done / for the owner
- **Nothing here has run on the real VPS.** docs/15 §10 lists the decisions (host nginx not Caddy; one SQL Server per
  environment; ports 38080/38081 and subnets 10.251.0.0/24 and 10.251.1.0/24 to be checked free on the server).
- A full authenticated 50 MB upload through the gateway was not exercised (the seeded admin must change its password first).
- Certificate for the key ring: decide before the first secret (PayPal, SMTP) is saved.
- The storefront unit tests are load-flaky on this machine (a different spec fails each run); they pass alone.
