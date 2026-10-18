# 2026-10-17 — #195, everything that can be prepared before the first UAT / Live deploy

Branch `feature/195-uat-live-prep`, worktree `worktrees/sd-deployprep`, cut from `origin/main` at **`154b5e4`**.
Not pushed, no PR. Nothing here was run on the VPS (it is not reachable from this machine and was never contacted). The
person who runs the server steps is Tharanga; the ordered sheet is `docs/16-uat-and-live-run-sheet.md`.

## What was added

| File | What |
|---|---|
| `deploy/scripts/preflight.sh` | Read-only server preflight, run before `install.sh`. Docker + Compose ≥ 2.24, git/curl/openssl/nginx/certbot, ports 38080/38081, subnets 10.251.0.0/24 and .1.0/24 against every docker network **and** host route (CIDR overlap, not string equality), RAM and disk against the container-limit budget with the docs/15 §10 #2 fallback named, `/opt/apps` + `/opt/server`, whether `install.sh` ran (and whether the installed `provision-database.sql` is the RCSI one), `default_server` on 80/443 for IPv4 and IPv6 from `nginx -T`, certbot webroot and its two TLS include files, DNS A/AAAA against the machine's public address |
| `deploy/scripts/verify-public.sh` | Public-URL check, runs from any machine (curl + openssl only). 20 checks, `--resolve`, `--insecure`, `--cacert`, `--www-redirect`, summary table, exit 1 on FAIL |
| `deploy/scripts/make-dp-cert.sh` | Self-signed Data Protection `.pfx` with openssl; password generated and shown once on the terminal, or prompted, or from stdin; root-owned, group 1654, mode 640 under `config/dataprotection/`; refuses to replace a certificate or to run over an existing key ring |
| `deploy/app/compose.yml` (+ `compose.local.yml`, `.env.example`, `install.sh`, `lib.sh`) | read-only mount `../config/dataprotection` → `/etc/spencer-denim/dataprotection`; `DataProtection__CertificatePath/Password` from `SD_DP_CERT_FILE` / `SD_DP_CERT_PASSWORD`, empty by default; `check-env.sh` now refuses one without the other, a path instead of a name, a missing file, a wrong password, a world-readable or non-root `.pfx` |
| `deploy/host-nginx/spencer-denim-staging.conf.template` | `X-Robots-Tag: noindex, nofollow` on every response, `location = /robots.txt` answered by nginx (`Disallow: /`), optional (commented) basic-auth lines + PayPal-webhook exception. All in one block near the end of the file |
| `deploy/host-nginx/spencer-denim-production.conf.template` | only an OPTIONAL, commented-out www → apex 301 block at the very end, with the certificate / `SD_PUBLIC_HOSTS` consequences written down |
| `deploy/host-nginx/spencer-denim-00-default.conf.template` | catch-all: port 80 `return 444`, port 443 `ssl_reject_handshake on`. Install **only** when preflight says no default_server exists |
| `docs/16-uat-and-live-run-sheet.md` | the run sheet (A facts/decisions, B UAT, C verify + UAT checklist, D Live, E rollback, F execution log, G what was and was not tested); one-line pointer added near the top of `docs/15` |
| `deploy/scripts/tests/` | `run-all.sh`, four shell tests, `mock-shop.mjs`, README. Plus two tests in `DataProtectionKeyRingTests` |

## Things found that the owner / Tharanga should know

1. **docs/15 §5 step 5 cannot work on a new host in the order written.** It copies the site template, then runs
   `certbot certonly --webroot`, then links the site. Until the certificate exists the template's `ssl_certificate` path is
   missing, so `nginx -t` fails; and until the site is enabled nothing answers certbot's challenge for the new name. The run
   sheet does: a tiny port-80 bootstrap site, certbot (dry run first), then the real site. docs/15 itself is left for the
   owner to fold in (the brief allowed one pointer line only).
2. **The templates `include /etc/letsencrypt/options-ssl-nginx.conf` and `ssl_dhparam /etc/letsencrypt/ssl-dhparams.pem`.**
   certbot creates both only through its nginx installer, not with `certonly --webroot`. Preflight reports them; B12-a says how to supply them.
3. **`certonly` does not reload nginx after renewal.** The run sheet passes `--deploy-hook 'systemctl reload nginx'` at issuance.
4. **The db.env template carries production's memory (2048 / 2560m).** `install.sh` only sets `SD_ENV`. B5 sets staging's 1024 / 1536m.
5. **The "UAT on the production SQL instance" fallback (docs/15 §10 #2) is not implementable with the scripts as they are**:
   `compose.staging.yml` fixes the network name `sd-staging-db`, and `db-provision.sh <env>` provisions into that environment's own
   stack. Preflight prints the numbers and names the fallback; the run sheet says it needs an issue first.
6. **SMTP is environment-only (`Email__Smtp__*`, read once at start-up) and is not protected by Data Protection.** What Data Protection
   encrypts at rest is the PayPal client secret and other `Settings.*` secrets in the database. Unset SMTP means mail is queued in
   `ops.EmailOutbox` as Pending, warning 3402 every 30 minutes, and **everything queued is sent as soon as SMTP is configured**.
   That is why UAT must either stay without SMTP or get a test mailbox before the first test order (A5).
7. **A certificate switched on after the key ring exists does not encrypt the existing keys** (new test
   `A_certificate_switched_on_later_does_not_encrypt_the_keys_that_already_exist`). Hence "before the first deploy", and the script's refusal.
8. `Server: nginx/1.24.0 (Ubuntu)` is sent by the host nginx unless `server_tokens off`. The brief froze the production template, so
   `verify-public.sh` reports it as WARN with the one-line http-level fix instead of failing the run. **Owner: confirm, or let the
   templates carry `server_tokens off;`.**
9. The shell scripts cannot be tested quickly on Windows: every process start costs ~150 ms in Git Bash, so each suite takes minutes
   there (seconds on Linux). `verify-public.sh` and its helpers were rewritten to use bash built-ins for header lookups for that reason.
10. `install -d -m 750` fails in Git Bash on NTFS ("cannot change permissions"); `make-dp-cert.sh` uses `mkdir` + `chmod` + `chown`
    so it can be tested here. `install.sh` keeps `install -d` (server only).

## Decisions taken without asking (confirm or reverse)

- Env variable names `SD_DP_CERT_FILE` (a file name inside `config/dataprotection/`, never a path) and `SD_DP_CERT_PASSWORD`.
- Certificate valid 3650 days, RSA 3072, CN `SpencerDenim Data Protection (<env>)`; one certificate per environment.
- The password is written to `app.env` only with `--write-env`; default is to print the two lines. Without `--write-env`
  the generated password exists nowhere but the terminal.
- `verify-public.sh` posts `{"path":"verify-public-probe"}` to the page-view endpoint: the path is not a storefront path, so the
  API answers 204 and records nothing; other probes are counted as **bot** views (User-Agent ends `bot)`).
- Disk thresholds in preflight (FAIL < 12 GB, WARN < 25 GB free) are the script's own working figures, not from the docs; stated as such in its output.
- The catch-all is deliberately a separate file with the name `spencer-denim-00-default`, so the decision stays visible in `sites-enabled`.

## Not done / left for others

#179 (gateway/host logs, page-view limit: another developer, `worktrees/sd-gwlogs`; my host-nginx edits are in blocks away from the `access_log` lines so the
merge is mechanical), #196 backups, #200 staff password length, #84, #107, #185. `MEMORY.md`, `TASKS.md`, `client-progress/` untouched.

**Next:** Tharanga runs docs/16 §B on the VPS and fills §F; the owner folds §F's deviations into docs/15.
