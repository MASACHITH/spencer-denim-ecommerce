# 15 · Deployment runbook (Docker on the VPS)

Task **W0-15** (issue #41). How the platform is packaged and run on the Hostinger VPS **srv1983022**, for
**staging** and **production**. Files live in [`deploy/`](../deploy/). Nothing here has been run on the server
yet; §10 lists what the owner still has to confirm.

> **First bring-up of UAT (staging) and Live (production):** follow the ordered run sheet [16 · UAT and Live run sheet](16-uat-and-live-run-sheet.md) (#195); it also wires the Data Protection certificate of §4 / §10 #8 and corrects the order of §5 step 5 for a new host.

## 1. What runs where

```
Internet ──► host nginx (80/443, certbot; already serves other sites on this VPS)
               │  X-Forwarded-For = $remote_addr (overwritten), X-Forwarded-Proto = $scheme
               ▼
             127.0.0.1:38080 (production) / 127.0.0.1:38081 (staging)      ← the stack's ONLY published port
               │
   ┌───────────┴──────────── application stack  sd-<env>  (/opt/apps/<env>/spencer-denim-app) ───────────┐
   │ web (nginx gateway, fixed IP on "edge")                                                               │
   │   /api/*  /health ──► api (.NET 10)  ──┐                                                              │
   │   /admin/*       ──► admin static files (baked into the web image)                                     │
   │   /media/public/* ──► api (serves data/media-public only for assets that are Ready)                    │
   │   everything else ──► storefront (Angular SSR, incl. its own /media webfonts) ──► api (http://api:8080) │
   │ migrator (one-shot EF Core bundle, runs before the API starts)                                        │
   └────────────────────────────────────────┼──────────────────────────────────────────────────────────────┘
                                            │ network sd-<env>-db (internal: no route to the internet)
   ┌────────────────────────────────────────┴──── database stack  sd-<env>-database (/opt/server/database/<env>) ┐
   │ sqlserver (SQL Server 2025 Express, no published port)                                                   │
   └──────────────────────────────────────────────────────────────────────────────────────────────────────────┘
```

| Image | Built from | Base (pinned) | Runs as | Healthcheck |
|---|---|---|---|---|
| `spencer-denim/api` | `deploy/docker/backend.Dockerfile` target `api` | `dotnet/aspnet:10.0.12-alpine3.24` + ICU | `app` (1654) | `GET /health` (image); compose uses `/health/ready` |
| `spencer-denim/migrator` | same, target `migrator` | `dotnet/aspnet:10.0.12-alpine3.24` + ICU (framework-dependent bundle, shares the API's layers) | `app` (1654) | one-shot, exit code |
| `spencer-denim/storefront` | `deploy/docker/frontend.Dockerfile` target `storefront` | `node:24.21.0-alpine3.24` | `node` (1000) | `GET /healthz` |
| `spencer-denim/web` | same, target `web` | `nginxinc/nginx-unprivileged:1.30.5-alpine3.24` | `nginx` (101) | `GET /gateway-health` |
| SQL Server | `deploy/database/compose.yml` | `mssql/server:2025-CU9-ubuntu-24.04` | `mssql` (10001) | `sqlcmd SELECT 1` |

Every application container runs with a read-only root filesystem (writable `/tmp` tmpfs only), all Linux
capabilities dropped and `no-new-privileges`. Logs rotate at 10 MB × 5 files per container; what the web
servers may write into them, and what they must never write, is §8a. Build contexts are
allow-lists (`deploy/docker/*.Dockerfile.dockerignore`): secrets, `.env` files, `reference/`, `client-progress/`,
`worktrees/`, `node_modules`, `bin/obj` and the docs never enter an image.

## 2. Server layout

Follows the server's existing convention:

```
/opt/apps/<env>/spencer-denim-app/
├── compose/            compose.yml + compose.<env>.yml (copied by install.sh)
│   └── releases/<tag>/ the compose files each deployed release used (rollback uses these)
├── config/app.env      secrets for the stack (root, 600) — template deploy/app/.env.example
├── data/                      all written by the API only (owner 1654)
│   ├── dataprotection-keys/   ASP.NET Data Protection key ring (#96) — admin sign-ins and encrypted settings
│   │                          survive restarts. Back it up with the database: without it they are unreadable
│   ├── media-public/          uploads (W1-04); the API serves them at /media/public/…
│   ├── media-private/         bank-transfer slips; never served by the gateway, only through the admin API
│   ├── upload-spool/          a video is written here while it streams in (keeps it out of the RAM tmpfs)
│   └── deploy-history.log     one line per deploy: time, env, tag, user
└── scripts/            deploy/scripts/*.sh (copied by install.sh)

/opt/server/database/<env>/
├── compose/compose.yml SQL Server stack
├── config/db.env       sa password, edition, memory (root, 600) — template deploy/database/.env.example
├── data/               /var/opt/mssql (owner 10001)
├── backups/            /var/opt/mssql/backup (owner 10001) — backup-db.sh writes here
└── scripts/            provision-database.sql, logins.sql
```

The source checkout the images are built from is outside this layout (suggested: `/opt/apps/src/spencer-denim-ecommerce`,
a clone with a read-only deploy key). **SQL Server is its own stack** so that nothing done to the application —
deploy, rollback, `docker compose down`, pruning — can stop it or touch its files.

## 3. Ports, networks and who is trusted

| | Production | Staging |
|---|---|---|
| Compose project | `sd-production` | `sd-staging` |
| Published port (loopback only) | `127.0.0.1:38080` | `127.0.0.1:38081` |
| `edge` subnet | `10.251.0.0/24` | `10.251.1.0/24` |
| Host side of `edge` (Docker's gateway) | `10.251.0.1` | `10.251.1.1` |
| Gateway container's fixed address | `10.251.0.10` | `10.251.1.10` |
| Dynamic addresses (api, storefront) | `10.251.0.128/25` | `10.251.1.128/25` |
| Database network | `sd-production-db` (internal) | `sd-staging-db` (internal) |
| Host nginx site | `deploy/host-nginx/spencer-denim-production.conf.template` | `…-staging.conf.template` |
| `ASPNETCORE_ENVIRONMENT` | `Production` | `Staging` (demo catalogue seeded) |

The values live in `deploy/app/compose.<env>.yml` (YAML anchors keep each repeated address identical). Before
the first install, check on the server that neither the ports nor the subnets are taken:
`ss -ltn | grep -E ':3808[01] '` and `docker network inspect $(docker network ls -q) --format '{{.Name}} {{range .IPAM.Config}}{{.Subnet}}{{end}}' | grep 10.251.`.

**The forwarded-headers chain** (W1-01 follow-up, launch blocker):

1. The host's nginx **overwrites** `X-Forwarded-For` with the address it saw, so a visitor's own header never
   survives, and sets `X-Forwarded-Proto` to `https`.
2. Traffic to the loopback-bound published port reaches the gateway container from the `edge` network's host-side
   address. The gateway believes `X-Forwarded-For` / `-Proto` **only from that address** (`SD_REAL_IP_FROM`,
   nginx `real_ip` module, right-most entry), and passes the visitor's address on by **overwriting** the header.
3. The API processes `X-Forwarded-For` / `-Proto` **only from the gateway's fixed address**
   (`ForwardedHeaders__KnownProxies`, `ForwardLimit = 1`). The storefront shares the `edge` network so it can
   call the API, but its address is not trusted. Without the setting (every developer machine, CI, tests) the API
   ignores `X-Forwarded-For` from everybody and takes `X-Forwarded-Proto` from loopback only — exactly the state
   W1-01 left it in. A malformed value, or a `/0` network, stops the API at start-up.
4. `HttpContext.Connection.RemoteIpAddress` — what the audit log, the newsletter consent record and the newsletter
   rate limit read — is therefore the visitor. The newsletter limit is now per visitor: **20 signups per address
   per 10 minutes** (was 600 while every visitor shared the proxy's address). If it logs warning 1101
   ("signups are arriving from … not a visitor's own address"), step 2 or 3 is misconfigured.
   The **order-placement limit** (`POST /api/checkout/orders`, #107) reads the same address: **20 orders per
   address per 10 minutes**, answering 429 with `Retry-After`; a resend of an order that was already placed is
   never counted or refused. Tune it in `config/app.env` with `SD_ORDER_THROTTLE_PERMITS`,
   `SD_ORDER_THROTTLE_SHARED_PERMITS` (the budget when the address is the proxy's, default 200 and never below the
   first) and `SD_ORDER_THROTTLE_WINDOW_SECONDS`, then redeploy: the API container has no `env_file`, so these reach
   it only through the lines in `compose.yml` that map them to `OrderPlacementThrottle__PermitsPerWindow`,
   `…SharedAddressPermitsPerWindow` and `…WindowSeconds`. A shared figure below the per-address one stops the API
   at start-up, by name. Warning 3311 ("orders are arriving from … not a visitor's
   own address") means step 2 or 3 is misconfigured, as for 1101.

5. The **storefront** gets only `X-Forwarded-Host` and `X-Forwarded-Proto` (both overwritten by the gateway) and
   trusts exactly those (`NG_TRUST_PROXY_HEADERS`). Angular's SSR engine falls back to the **empty client-side
   shell** — no content for crawlers, no `og:` tags — whenever it receives any `X-Forwarded-*` header it was not
   told to trust, so the gateway's `location /` drops `X-Forwarded-For`, `X-Real-IP`, `X-Forwarded-Port` and
   `X-Forwarded-Prefix`. If the storefront logs *"Received "x-…" header but "trustProxyHeaders" was not set up
   to allow it"*, that list and the gateway have drifted apart. Quick check: `curl -s https://<host>/ | grep -c
   'ng-server-context="ssr"'` must print 1.

Never set `ASPNETCORE_FORWARDEDHEADERS_ENABLED`: that framework switch trusts every peer.

## 4. Configuration

Secrets live only in the two env files on the server (root-owned, mode 600), never in git or an image.
`scripts/check-env.sh <env>` validates both without printing a secret: it refuses `CHANGE_ME` placeholders,
quoted values, `$` (compose would interpolate it), the same login for API and migrator, and passwords containing
`' " ] \ ; $` (they are embedded in T-SQL and connection strings). With `SD_SEED_ADMIN_ENABLED=true` it also refuses
a `SD_SEED_ADMIN_PASSWORD` shorter than 10 characters, because the API would create no first Super Admin with it
(issue #200).

| File | Variable | Meaning |
|---|---|---|
| `config/app.env` | `SD_PUBLIC_HOSTS` | Public host name(s), comma-separated; the SSR engine renders only for these (`NG_ALLOWED_HOSTS`) |
| | `SD_PUBLIC_ORIGIN` | `https://<host>`, scheme and host only. robots.txt, sitemap.xml, og:url (#86) and every link in an order email. Never derived from the request |
| | `SD_SMTP_HOST/PORT/USERNAME/PASSWORD/FROM_ADDRESS/FROM_NAME` | Optional until the client's SMTP account exists; unset, emails queue unsent and the admin banner says so |
| | `SD_TRAFFIC_RECORDING_ENABLED` | Optional, default `true` → `Traffic__RecordingEnabled`. `false` stops page views being recorded (the beacon still answers 204). Visits not recorded cannot be recovered later, so leave it on |
| | `SD_TRAFFIC_INTERNAL_HOSTS` | Optional → `Traffic__InternalHosts`. The shop's own host names, comma-separated (e.g. `colombowardrobecompany.com,www.colombowardrobecompany.com`), so a click from one to the other is not counted as a traffic source. The host a page view was sent to is always internal; this is only for the second name |
| | `SD_TRAFFIC_RAW_RETENTION_DAYS` / `SD_TRAFFIC_BOT_RETENTION_DAYS` | Optional, default `90` / `7` → `Traffic__RawRetentionDays` / `Traffic__BotRetentionDays`. **A fallback only** — see "Traffic retention" below. Start-up refuses anything outside 4–3,650 days, or bots longer than people |
| | `SD_APP_DB_CONNECTION` | API login `sd_<env>_app` (read/write). `Server=sqlserver,1433;…;TrustServerCertificate=True` |
| | `SD_MIGRATOR_DB_CONNECTION` | Migrator login `sd_<env>_migrator` (db_owner). The API never sees it |
| | `SD_DB_COMPATIBILITY_LEVEL` | `170` (SQL Server 2025) |
| | `SD_SEED_ADMIN_ENABLED/EMAIL/PASSWORD` | First Super Admin, first install only; set back to `false` afterwards. The password is held to the same rule as every account: **at least 10 characters** with an upper-case letter, a lower-case letter, a number and a symbol (not `$`, and not starting with a quote: compose reads the file literally). A shorter one creates no account, so `check-env.sh` refuses it while the seed is on |
| | `SD_IMAGE_REPOSITORY` | `spencer-denim` (image name prefix) |
| `config/db.env` | `SD_ENV` | `production` / `staging` |
| | `SD_MSSQL_SA_PASSWORD` | sa: healthcheck and provisioning only |
| | `SD_MSSQL_PID` | `Express` |
| | `SD_MSSQL_MEMORY_LIMIT_MB` / `SD_MSSQL_CONTAINER_MEMORY` | `2048`/`2560m` production, `1024`/`1536m` staging |

Set on the gateway by `compose.yml`, also not in the env file: `SD_PAGE_VIEW_RATE` (`20r/s`) and
`SD_PAGE_VIEW_BURST` (`40`), the page-view beacon's limit per visitor address (§8a).

Set by `compose.yml` itself, not the env file: `DataProtection__KeysPath`, `Media__PublicRoot`,
`Media__PrivateRoot` and `Media__UploadSpoolPath` (the four `data/` directories above). **Production refuses to
start without `DataProtection__KeysPath`**: a key ring inside the container would sign every admin out on every
restart and make encrypted settings unreadable (#96). To encrypt the key ring at rest (doc 03 §4), add
`DataProtection__CertificatePath` (a `.pfx` mounted read-only) and `DataProtection__CertificatePassword`; keep a
copy of the certificate and its password off the server, because without them a restored key ring is useless.

**Traffic retention: the Settings screen wins, configuration is the fallback (#193).** The nightly purge (01:30
Colombo) deletes page views older than `Traffic.RawRetentionDays` and bot page views older than
`Traffic.BotRetentionDays`. Both are `ops.Settings` rows, seeded **90** and **7** (doc 08 §3.8) by the start-up
seeder on a new *and* an existing database, and edited in the admin under **Settings → Traffic** by a Super Admin
or a Store Manager (`Settings.Edit`; every change is audited). The purge reads the rows every night and uses a row
whenever it holds a whole number of days inside the form's bounds (30–365, bots 7–90 and never longer than page
views). `Traffic__RawRetentionDays` / `Traffic__BotRetentionDays` are used **only** while a row is empty or holds
something outside those bounds (written by hand, say) — so setting them in `app.env` does **not** override what the
owner chose on the screen. The defaults are the same 90 and 7, so a deployment that sets nothing purges exactly as
it did before the form existed. Each purge logs the periods it applied and where each came from (`from Settings` /
`from Configuration`, event 7131); a row that was ignored logs a warning (event 7133) — re-save it on the screen.
The daily totals the Traffic page shows are never purged, whatever the periods say. **The purge never deletes a
day the nightly rollup has not counted**: such a day keeps its raw page views and the purge logs a warning naming
it (event 7134); it goes on the first night after the rollup has built it. Seeing 7134 more than once means the
rollup is failing — check its log (events 7121–7123) before anything else.

`Traffic__JobsEnabled` (default `true`) runs the nightly rollup and purge on this host; set it `false` only on a
second API host, if there ever is one. `Traffic__QueueCapacity`, `Traffic__BatchSize` and `Traffic__FlushSeconds`
tune the in-memory page-view queue and have no reason to change.

Everything else the API reads (README "Other settings") can be added to the `api` service's `environment:` in
`compose.<env>.yml`. Database names: `SpencerDenim` (production), `SpencerDenim_Staging` (staging).

## 5. First install (once per environment)

Prerequisites on the server: Docker Engine with the Compose plugin **≥ 2.24**, git, curl, the host nginx and
certbot. Run as a sudoer; `<env>` is `staging` first, then `production`.

> **Order matters: `install.sh <env>` first, then `db-provision.sh <env>`, always from the same checkout and a
> commit at or after `8bd27a7` (#163/#183).** `install.sh` copies `tools/db/provision-database.sql` onto the VPS
> (`/opt/server/database/<env>/scripts/`), and `db-provision.sh` pipes **that copy** to `sqlcmd`, not the file in the
> checkout. Run them the other way round, or run `db-provision.sh` on a server whose copy was installed before
> `8bd27a7`, and the old script runs: the database is created **without `READ_COMMITTED_SNAPSHOT`**, silently, while
> the code assumes it. After provisioning, check that
> `SELECT name, is_read_committed_snapshot_on FROM sys.databases;` shows **1** for the shop database (the command
> is in "Existing database without READ_COMMITTED_SNAPSHOT" below, with the one-off fix for a database that already
> exists).

```bash
# 0. Source checkout (once for both environments)
sudo git clone git@github.com:MASACHITH/spencer-denim-ecommerce.git /opt/apps/src/spencer-denim-ecommerce
cd /opt/apps/src/spencer-denim-ecommerce && sudo git checkout <release commit or tag>

# 1. Directories, compose files, scripts, env templates (never overwrites an existing env file)
sudo deploy/scripts/install.sh <env>

# 2. Fill in the secrets, then validate
sudo nano /opt/server/database/<env>/config/db.env
sudo nano /opt/apps/<env>/spencer-denim-app/config/app.env
sudo /opt/apps/<env>/spencer-denim-app/scripts/check-env.sh <env>

# 3. SQL Server up, then the database (collation Latin1_General_100_CI_AS_SC, READ_COMMITTED_SNAPSHOT ON) and the two logins.
#    db-provision.sh runs the copy of provision-database.sql that step 1's install.sh put on the server: step 1 FIRST
#    (see the note above), then verify is_read_committed_snapshot_on = 1.
sudo /opt/apps/<env>/spencer-denim-app/scripts/db-up.sh <env>
sudo /opt/apps/<env>/spencer-denim-app/scripts/db-provision.sh <env>

# 4. Images, then the first deploy (production takes a verified backup first; on an empty database pass --skip-backup)
tag=$(sudo deploy/scripts/build-images.sh | tail -n 1)
sudo /opt/apps/<env>/spencer-denim-app/scripts/deploy.sh <env> "$tag"

# 5. Host nginx site + certificate. The site file writes its (crit-only) error log to /var/log/nginx/spencer-denim/ and
#    has no access log (§8a), so create that directory and install the 3-day rotation BEFORE `nginx -t`, or nginx -t fails
#    on the missing directory. Once per server, not per environment.
sudo install -d -m 750 -o www-data -g adm /var/log/nginx/spencer-denim
sudo install -m 644 deploy/host-nginx/spencer-denim.logrotate /etc/logrotate.d/spencer-denim
sudo logrotate -d /etc/logrotate.d/spencer-denim                   # dry run: must end without an error
sudo cp deploy/host-nginx/spencer-denim-<env>.conf.template /etc/nginx/sites-available/spencer-denim-<env>.conf
sudo nano /etc/nginx/sites-available/spencer-denim-<env>.conf      # __SERVER_NAME__, __PRIMARY_HOST__
sudo certbot certonly --webroot -w /var/www/certbot -d <host>      # or: certbot --nginx -d <host>
sudo ln -s /etc/nginx/sites-available/spencer-denim-<env>.conf /etc/nginx/sites-enabled/
sudo nginx -t && sudo systemctl reload nginx
```

If `SD_SEED_ADMIN_ENABLED=true` was used for the first Super Admin: sign in once, set it back to `false`, blank the
password in `app.env`, and redeploy the same tag.

Still to do on the database after provisioning (doc 08 §10.4, not automated here): recovery model FULL for
production (SIMPLE for staging), Query Store, and the scheduled backup job (doc 08 §8.5). `READ_COMMITTED_SNAPSHOT`
is set by `db-provision.sh` (issue #163), as long as `install.sh` has copied this commit's
`tools/db/provision-database.sql` into `scripts/` first.

**Existing database without READ_COMMITTED_SNAPSHOT** (any database created before issue #163). Check it:

```bash
cd /opt/server/database/<env>/compose
docker compose --env-file ../config/db.env exec sqlserver /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -Q "SELECT name, is_read_committed_snapshot_on FROM sys.databases WHERE name = N'<database>'"
```

`db-provision.sh <env>` switches it on when nothing else is connected to the database, and otherwise stops with
error 50001 and this command — it never disconnects anybody itself. SQL Server will not change the option while
any other connection is open on the database, even an idle one, and without a termination clause the `ALTER`
waits for ever. So at a quiet moment either stop the API (`$C stop api` with `$C` from §8, then re-run `db-provision.sh` and
`$C start api`), or run as sa:

```sql
ALTER DATABASE [<database>] SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;
```

sqlcmd prompts for sa's password. `ROLLBACK IMMEDIATE` rolls back whatever is in flight — a checkout being placed,
a payment settling — and disconnects it. EF Core's retry-on-failure (doc 08 §10.3) can retry a unit of work whose
connection was cut, but treat it as an outage of a second or two and pick a quiet moment. On a developer machine
(`SpencerDenim_Dev`, a shared database: agree it with whoever else uses it) stop the API and close any other
connection to it (SSMS, Azure Data Studio, a running test host), then run `tools/db/provision-database.sql` with
`-v DbName="SpencerDenim_Dev"`.

## 6. Deploy (every release)

```bash
cd /opt/apps/src/spencer-denim-ecommerce
sudo git fetch && sudo git checkout <commit>           # the commit that passed CI and the tester
tag=$(sudo deploy/scripts/build-images.sh | tail -n 1)  # tag = 12-character commit id
sudo deploy/scripts/install.sh <env>                   # refreshes compose files, scripts and the provisioning SQL; config untouched
                                                       # (any db-provision.sh must come AFTER this, §5 note)
sudo /opt/apps/<env>/spencer-denim-app/scripts/deploy.sh <env> "$tag"
```

`deploy.sh` checks both env files and all four images, takes a verified COPY_ONLY backup (production only), runs
`docker compose up -d --wait`: the **migrator** applies pending migrations and exits (the API is
`depends_on: service_completed_successfully`, so it never starts on an unmigrated schema; migrations are never
applied at API start-up), then the **API** must become ready (it can reach SQL Server), then the **storefront**,
then the **gateway**. It then smoke-tests `/gateway-health`, `/health`, `/admin/`, `/` and
`/api/catalog/departments` through the loopback port and appends the release to `data/deploy-history.log`.
It never runs `down` and never removes volumes, data or backups. Deploy staging first; production only after the
same tag has been checked on staging.

**After every deploy, read one line out of the API log** (issue #122, doc 08 §9 / N13):

```bash
$C logs --tail 500 api | grep "is missing"      # $C from section 8
```

The reference-data phase applies the permissions added to a default role since this database's recorded
`Seed.Version`, and **reports by name, at `Warning`, anything a default role still lacks against the shipped
defaults** — it never grants those, because "the client removed it" and "it never arrived" are the same absent
row and only one of them is a defect. Nothing to read is the normal case.

- **On the first deploy of a new database** that line should not appear at all: the client has had no chance to
  remove anything, so a Warning there means the seeding is wrong. Grant what it names, per role:
  ```sql
  INSERT INTO identity.RoleClaims (RoleId, ClaimType, ClaimValue)
  SELECT r.Id, 'perm', N'<Permission>' FROM identity.Roles r
  WHERE r.Name = N'<Role>' AND NOT EXISTS (
      SELECT 1 FROM identity.RoleClaims c
      WHERE c.RoleId = r.Id AND c.ClaimType = 'perm' AND c.ClaimValue = N'<Permission>');
  ```
  `Settings.Security` is never insertable (`CK_RoleClaims_NoSecurity`); only a GrantsAll role reaches it.
- **On a later deploy** it is most likely the client's own edit, which doc 08 N13 says to keep. Ask before
  granting anything back. Staff need no sign-out: effective permissions are cached for five minutes and a deploy
  restarts the process anyway.
- **Never edit the `Seed.Version` row** in `ops.Settings`. Lowering it re-offers permissions the client may have
  removed on purpose; raising it suppresses ones that have not been applied. No screen exposes it.

Building on the VPS takes a few minutes and some memory next to the running stack. The alternative is to build
elsewhere and copy: `docker save spencer-denim/{api,migrator,storefront,web}:<tag> | ssh srv1983022 docker load`
(then run `install.sh` from a checkout of the same commit and `deploy.sh`).

## 7. Rollback and backups

```bash
sudo /opt/apps/<env>/spencer-denim-app/scripts/rollback.sh <env>          # the release before the current one
sudo /opt/apps/<env>/spencer-denim-app/scripts/rollback.sh <env> <tag>    # a specific earlier release
```

`rollback.sh` with no tag walks back through `data/deploy-history.log` like a stack: a rollback to B pops everything
above B, so running it again goes to the release before B rather than forward again. It answers "no earlier release"
when there is nothing below the current one — including after a rollback to the oldest release, or after an
explicit rollback to a tag older than the start of the history. Name the tag in those cases.

Rollback redeploys the earlier images with **their own** compose files (`compose/releases/<tag>/`). It does **not**
roll the database back: migrations are expand-then-contract (doc 08 §10.4), so the previous release runs on the
newer schema, and its older migrator finds nothing to apply. If a migration itself must be undone, restore the
pre-deploy backup (`/opt/server/database/<env>/backups/<db>_pre-deploy-<tag>_<time>.bak`) deliberately, with the
application stopped: `RESTORE DATABASE [SpencerDenim] FROM DISK = N'/var/opt/mssql/backup/<file>' WITH REPLACE`
through `docker compose exec sqlserver /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C` (enter the password
at the prompt).

Keep the last few releases' images; remove older ones by tag (`docker image rm spencer-denim/api:<old tag> …`).
Never `docker system prune --volumes`, and never `docker compose down -v` on either stack.

`backup-db.sh <env> [label]` takes an ad-hoc verified COPY_ONLY backup at any time. The real backup regime —
full + 15-minute log backups, uploads, shipped off the server, 35 days (doc 03 §4, doc 08 §8.5) — is separate
work and not part of W0-15.

## 8. Logs, status, upgrades

```bash
cd /opt/apps/<env>/spencer-denim-app/compose
C="docker compose -p sd-<env> --project-directory . -f compose.yml -f compose.<env>.yml --env-file ../config/app.env"
$C ps                          # state and health of every service
$C logs -f --tail 100 api      # API (JSON, Serilog); also: web (JSON access log, §8a), storefront, migrator
$C exec api wget -q -O - http://127.0.0.1:8080/health/ready   # readiness detail (not exposed through the gateway)
tail -n 5 ../data/deploy-history.log

cd /opt/server/database/<env>/compose && docker compose --env-file ../config/db.env logs --tail 100 sqlserver
```

`SD_IMAGE_TAG` is required by the compose file; export it (`export SD_IMAGE_TAG=$(tail -n1 ../data/deploy-history.log | awk '{print $3}')`)
before using `$C` by hand. The host nginx has no access log for these sites; its error log is
`/var/log/nginx/spencer-denim/<env>.error.log` (§8a).

**Upgrading a base image or SQL Server:** change the pinned tag (`deploy/docker/*.Dockerfile` `ARG …_IMAGE`,
`deploy/database/compose.yml`), open a pull request, deploy to staging, then production. For SQL Server read the
cumulative update's notes, take `backup-db.sh` first, then `install.sh <env>` and `db-up.sh <env>`.

## 8a. Logs and privacy, and the page-view limit (issue #179)

**The rule: no web-server log may hold a visitor's address or a credential.** This shop puts credentials in web
addresses by design, and a log line that copies the address copies the credential, for as long as the log is
kept and for everyone who can read it (hosting staff, a backup, a log shipper added later):

| Address | What a log reader could do with it |
|---|---|
| `/order/{token}` | Open that customer's order: name, delivery address, phone, items. The address is the order's only credential |
| `/account/reset-password?token=…` | Reset the password and take over the account, if used before it expires |
| `/account/confirm-email?token=…` | Confirm an address |
| `/api/payments/paypal/return/{id}?token=…&PayerID=…` | PayPal flow identifiers, and the order token in the path |
| the `Referer` of the next request | Copies any of the above a second time |

and the application itself stores **no IP address** (doc 08 §3.8, `analytics.PageViews`), so a web server that
keeps one would undo that on purpose. Until this was fixed (#179) both nginx hops wrote every visitor's address
and full address to a log.

### What is written where

| Log | Holds | Kept |
|---|---|---|
| **Gateway access log** (`$C logs web`) | One JSON line per request: method, the **rewritten path**, status, bytes, duration, upstream and its status, `Host`, the **referring host name** only, user agent. No address of any kind, no query string | Docker `json-file`, 10 MB × 5 files: at most 50 MB, and a deploy that changes the image recreates the container, which deletes its log. Docker has no time limit; the size cap is the limit |
| Gateway error log (same stream) | Level `crit` only: a crashed worker, a failed start or reload, and (rarely) a temporary file nginx could not write, which DOES carry the request: see the paragraph below | as above |
| **Host nginx access log** (production and staging sites) | **Off.** The gateway's log is the only access log. The port-80 redirect is off too, because it would otherwise land in the server-wide log the client's other sites share | — |
| Host nginx error log | `/var/log/nginx/spencer-denim/<env>.error.log`, level `crit`: normally empty | 3 days (`/etc/logrotate.d/spencer-denim`, from `deploy/host-nginx/spencer-denim.logrotate`) |
| API (Serilog, JSON) | See "Still open" below | Docker, 10 MB × 5 |
| Storefront (Node) | See "Still open" below | Docker, 10 MB × 5 |

The gateway line, exactly (one line in the log; wrapped here):

```json
{"time":"2026-10-17T09:41:07+00:00","method":"GET","path":"/order/*","status":200,"bytes":4212,"duration":0.031,
 "upstream":"10.251.0.130:4000","upstream_status":"200","host":"shop.example.com","referer_host":"www.example.org",
 "user_agent":"Mozilla/5.0 …"}
```

The old fields `remote_addr`, `peer`, `uri` and `referer` are gone. If a script of yours reads `uri`, read `path`.

### Which paths are rewritten, and how that list was made

The gateway logs `$uri` (the decoded, normalised **path**, never `$request_uri`), so every query-string credential
(`?token=` on the two account pages and the PayPal return, `?returnTo=`) is gone in one step, and an encoded
spelling of a secret route (`/%6frder/…`, `//order//…`, `/x/../order/…`) cannot dodge a rule. Then
`$sd_log_path` (in `deploy/gateway/nginx.conf`) reduces the path. The routes that carry a credential **in the
path** were found by reading the storefront's `app.routes.ts` and `account.routes.ts`, the admin's `app.routes.ts`
and every `MapGet`/`MapPost`/`MapMethods` template in `SpencerDenim.Api/Endpoints` for a `{token}`, an order `{publicId}`
or similar:

| Route | Credential | Logged as |
|---|---|---|
| `/order/{token}` (storefront `order/:token`) | the order token (`Orders.PublicId`, a GUID) | `/order/*` (also `/Order/…`, `/order;x=1/…`) |
| `/api/orders/{publicId}` and `…/payment-reference`, `/cancel`, `/payment-slip` | the same token, in the API path the order page calls | `/api/orders/*` |
| `/api/payments/paypal/{start,return,cancel}/{publicId}` | the same token (browser navigation) | `/api/payments/paypal/<action>/*` |
| `/api/admin/orders/{id}`, `…/invoice` | staff calls with the customer's order token | `/api/admin/orders/*` |
| `/api/admin/orders/bank-transfers/{id}/approve,reject,accept-late,slip` | same | `/api/admin/orders/bank-transfers/*` |
| `/admin/orders/{id}` (admin screen) | same | `/admin/index.html` (`try_files`) |
| `/account/reset-password`, `/account/confirm-email`, PayPal return query | token in the **query** | the path only (`$uri`) |
| `/media/public/{**key}`, `/api/admin/media/{guid}` | none (public media; ids) | slugs and file names kept, the GUID starred |

No other storefront or admin route puts a secret in its path (the rest are slugs, fixed words or integer ids).

Everything not on that list goes through the same rule as the traffic recorder's `PagePath`
(`SpencerDenim.Application.Analytics`): the leading segments that are **slugs** are kept, up to seven; at the
first that is not, it and everything after become `*`. A slug is lower-case letters and digits separated by single
hyphens, at most 100 characters, not a GUID, and without a run of seven digits (hyphens ignored; a phone number).
One check is added: no run of 16 or more letters and digits without a hyphen (a random token has one, a slug does
not). A static file as the last segment (`main-AB12CD34.js`, `inter.woff2`) is kept when its extension is on the
list in `nginx.conf`. It is an **allow-list**, so a route added later cannot leak by default. It is a net for
shapes and not a proof: a secret of fifteen lower-case letters would pass it. **When you add a route that puts a
secret in its path, add it to the list in `nginx.conf` and to the table above.**

### The error log: the one place a format cannot reach

nginx appends the client address, the request line with its query string, the upstream URL and the `Referer` to
most request-related error lines, for example

```
[error] connect() failed (111: Connection refused) while connecting to upstream, client: 203.0.113.9, server: _,
request: "GET /order/<token>?x=y HTTP/1.1", upstream: "http://10.251.0.130:4000/order/<token>?x=y", host: "…", referrer: "…"
```

and no `log_format` redacts it. Those lines are written at `error` (upstream refused, timed out, not resolved;
request body too large) and `warn` (a body or response spilled to a temporary file) — **so they appear during every
deploy**, while the gateway or the storefront restarts. The mitigation is the level: the gateway's `error_log` is
`crit` and so are the host sites'. At that level the ordinary failures above are not written at all, but **`crit` is not a
guarantee that no request is ever written**: when nginx cannot write a temporary file (a full disk, a directory it may
not write) it logs `[crit] pwrite() "…" failed (28: No space left on device), client: <IP>, request: "POST …?token=…",
referrer: "…"` — the address, the whole request line and the Referer (the #179 tester reproduced it on both tiers). Two
settings keep that rare: `proxy_max_temp_file_size 0` on the gateway **and on both host sites**, so a response is never
spilled to disk, and the host tier buffering a request body before it reaches the gateway (the gateway's own `/tmp` is a tmpfs inside its
128 MB memory limit, so a body that does not fit there is an out-of-memory kill, not a disk-full line). What remains is a request body larger
than `client_body_buffer_size` arriving while a disk is full or unwritable. Such a line means something is badly wrong;
**treat the host error files and the gateway's container log as sensitive** (next section). The access log still records
every failure (status 502/504, the upstream tried, the time taken) without the secret. What is lost is nginx's own
explanation of a failure (refused, timed out, not resolved). The rate limit's refusals are logged at `info`, so
they stay out of the error log even if its level is lowered.

**To get the explanation back** (an upstream problem the access log cannot explain): do it on staging with test
data if you can. Otherwise change `crit` to `error` in `deploy/gateway/nginx.conf` (and, for the TLS hop, in the
site file, then `nginx -t && systemctl reload nginx`), redeploy, reproduce once, **change it back**, redeploy, and
then delete what it wrote: `$C up -d --force-recreate web` removes the gateway's container log; for the host,
`sudo truncate -s 0 /var/log/nginx/spencer-denim/<env>.error.log` **and delete the rotated copies** that were made
while the level was lowered (`sudo sh -c 'rm -f /var/log/nginx/spencer-denim/<env>.error.log.*'` (inside `sh -c`, so that root expands the glob: the directory is `0750 www-data:adm`, and a sudoer who is not in group `adm` would otherwise get a glob that matches nothing and no error): a daily rotation leaves
`.1`, `.2.gz`, `.3.gz`, and each keeps what it held). Treat the window as one in which the logs hold credentials.

### Retention, and who can read it

The gateway log holds no personal data and no credential, so its size cap (50 MB; a deploy resets it) is a
sensible limit; it is not a time limit. Docker's container logs (`/var/lib/docker/containers/…`) are readable by root and
the `docker` group only: keep that group to the people who deploy. The host error files are created by nginx as `root:root 0644`
(logrotate's `create 0640 www-data adm` applies only to the files it recreates after a rotation); what keeps other users out
is the directory, `0750 www-data:adm` (§5 step 5). They rotate daily for 3 days. Nothing here needs months of history. **If an address is ever wanted for abuse
investigation** (a flood from one source, a fraud pattern), decide it with the owner as an exception, truncated
(IPv4 `/24`, IPv6 `/48`, by a `map` on `$remote_addr`), in its own field, and with a retention of days. It is not
in the log by default.

### Still open (found while checking the rest of the stack for #179; not fixed here)

- **The API's request log writes the order token.** `UseSerilogRequestLogging` logs
  `HTTP GET /api/orders/{publicId} responded 200 in …` at `Information` for every request, with `RequestPath`
  holding the **path** (not the query; confirmed on the built API image: a `GET /api/orders/<guid>?token=abc` logs the
  GUID and not `abc`). So the order token, the PayPal path ids and staff's order ids are in the API container's log.
  No IP and no query string. A fix needs a small change in
  `SerilogSetup.cs` (redact `RequestPath` with the same rules as the gateway's) and is a separate issue.
- **The storefront's render-failure line writes the full address.** `server/render-failure.ts` logs
  `GET <originalUrl> failed` with the query string, to stderr, when a render fails unexpectedly. Rare, but
  `/order/{token}` and `/account/reset-password?token=` are both rendered there. Also a code change.
- Both containers keep 10 MB × 5 and are recreated by a deploy that changes the image, which bounds the exposure
  but does not remove it. Treat their logs as secret until those two are fixed.

### The page-view rate limit

`POST /api/traffic/page-views` is public and has to stay so. Without a limit one client sent about 9,600 requests a
second, wrote 138,000 rows (each a visit in the owner's figures) in 20 seconds and slowed the catalogue API by 41%
(W4-03 tester). The gateway now limits it per visitor address:

- **`SD_PAGE_VIEW_RATE=20r/s`, `SD_PAGE_VIEW_BURST=40`** (`deploy/app/compose.yml`; override per environment in
  `compose.<env>.yml` under `web: environment:`). Generous on purpose: Sri Lankan mobile carriers put many shoppers
  behind one address (carrier-grade NAT), so a tight limit would throttle real people. Raise it if the owner's
  figures dip on busy days and the log shows the limit tripping; `r/m` is accepted too (`600r/m`).
- **When it trips the browser gets `204`**, the same empty answer the endpoint gives on success, so the shopper's
  page is unaffected. Internally the limit answers `429` and `error_page` turns it into `204`; it is not
  `limit_req`'s default `503`, so that a real `503` from the API still reaches the browser. The check is on the
  *path*, matched case-insensitively with an optional trailing slash, because the API's routing ignores both.
- **The storefront's own (server-side) recording is never throttled**: it posts to `http://api:8080` directly and
  does not pass through the gateway.
- **To see that it trips**, one throttled request in 256 is logged: `$C logs web | grep '"method":"POST"' | grep '"upstream":""' | grep -E '"path":"(/api/traffic/page-views|/\*)"'`
  (a normal hit has an upstream address). A request spelled in capitals, or with `%2D` or `//`, is logged as `/*`, which is why
  the last grep accepts both. A flood would otherwise write a line per request and push every other
  line out of the 50 MB.
- **It does not stop** a determined attacker: an IPv6 visitor can send from any address in a /64, and many
  addresses each get their own bucket. It stops a runaway client or a script on one machine. The API's 10,000-row
  queue still caps what a flood writes. If `SD_REAL_IP_FROM` is wrong (§3), every visitor shares one bucket and
  real beacons are dropped silently: the warning 1101 check in §3 is how to notice.

### Checking it

`tools/verify-gateway-logging.sh` builds the real gateway (the `web` stage of `frontend.Dockerfile`), the real host
site files, a stub upstream and a client with a known address, sends the requests of the issue (`/order/<token>`,
`/account/reset-password?token=abc`, `/account/confirm-email?token=xyz`, a page with a `Referer`, 2,000 beacons),
takes the dependencies down, and greps both hops' logs for the token, `abc`, `xyz`, the referring link and the
client's address. Needs Docker and python3; touches only containers named `sd179-*`. Run it after any change to
`deploy/gateway` or `deploy/host-nginx`. Against the configuration before #179 (`git archive 154b5e4 deploy`,
`--deploy-dir`) it fails every leak check, which is how you know the checks can fail.

On the server, after the first deploy, the same test by hand: request `/order/<a real token>` and
`/account/reset-password?token=abc` once each, then `$C logs --tail 50 web | grep -E '<token>|abc'` and
`sudo grep -rE '<token>|abc' /var/log/nginx/spencer-denim/` must print nothing.

## 9. Local smoke test (developer machine with Docker Desktop)

The same images and compose files, with `deploy/app/compose.local.yml` instead of an environment file. Use a
unique project name, port and subnet (docs/14 "Scratch scripts and ports are shared too") and keep the env file
**outside** the repository. Example (bash):

```bash
cat > /path/outside/repo/w015.env <<'EOF'
SD_ENV=w015
SD_MSSQL_SA_PASSWORD=<local password>
SD_MSSQL_PID=Developer
SD_MSSQL_MEMORY_LIMIT_MB=1536
SD_MSSQL_CONTAINER_MEMORY=2048m
SD_DB_ROOT=/path/outside/repo/db
SD_DATA_DIR=/path/outside/repo/appdata
SD_LOCAL_PROJECT=sdw015
SD_LOCAL_HTTP_PORT=38997
SD_LOCAL_EDGE_PREFIX=10.251.97
SD_IMAGE_REPOSITORY=sdw015
SD_IMAGE_TAG=dev
SD_PUBLIC_HOSTS=shop.w015.test
SD_PUBLIC_ORIGIN=http://shop.w015.test:38997
SD_APP_DB_CONNECTION=Server=sqlserver,1433;Database=SpencerDenim_W015;User Id=sd_w015_app;Password=<pw>;Encrypt=True;TrustServerCertificate=True
SD_MIGRATOR_DB_CONNECTION=Server=sqlserver,1433;Database=SpencerDenim_W015;User Id=sd_w015_migrator;Password=<pw>;Encrypt=True;TrustServerCertificate=True
EOF

cd deploy/app
# compose.local.yml: on Docker Desktop for Windows SQL Server cannot keep its files on a bind mount of a Windows
# folder ("[AppLoader] Failed to load LSA"), so the local override uses named volumes.
docker compose -f ../database/compose.yml -f ../database/compose.local.yml --env-file /path/outside/repo/w015.env up -d --wait
# create the database and logins: see db-provision.sh (it expects the server layout, so on a laptop pipe
# tools/db/provision-database.sql and deploy/database/sql/logins.sql to sqlcmd with :setvar lines yourself)
docker compose -f compose.yml -f compose.local.yml --env-file /path/outside/repo/w015.env build
docker compose -f compose.yml -f compose.local.yml --env-file /path/outside/repo/w015.env up -d --wait
curl -s http://127.0.0.1:38997/api/catalog/products?pageSize=2
# tear down ONLY your own projects:
docker compose -f compose.yml -f compose.local.yml --env-file /path/outside/repo/w015.env down
docker compose -f ../database/compose.yml -f ../database/compose.local.yml --env-file /path/outside/repo/w015.env down
```

In Git Bash, set `MSYS_NO_PATHCONV=1` before any `docker exec … /opt/mssql-tools18/bin/sqlcmd`, or the path is
rewritten to a Windows one and the exec fails.

## 10. Decisions for the owner to confirm

| # | Decision taken in W0-15 | Why | Alternative |
|---|---|---|---|
| 1 | **Host nginx + certbot** in front, one loopback port per environment, and an nginx **gateway container** inside the stack — not Caddy (doc 03 §2/§4) | The VPS's nginx already owns 80/443 for other sites; a second TLS terminator cannot bind them | Caddy only if the server stops hosting other sites |
| 2 | **SQL Server in its own stack per environment** under `/opt/server/database/<env>`, joined over an internal network | Follows the issue's layout; the application stack can never touch database files | Doc 03 §4 budgets **one** instance for both databases (RAM). Two Express instances cost ~1.5 GB more; if that is too much, point staging's connection strings at the production instance (database `SpencerDenim_Staging`, own logins) and skip the staging database stack |
| 3 | Ports **38080** (production) / **38081** (staging); subnets **10.251.0.0/24** / **10.251.1.0/24** | Unusual enough not to clash with the other sites; verify on the server (§3) | Any free loopback port / private /24; change `compose.<env>.yml`, the host nginx site and `lib.sh` `http_port` together |
| 4 | Images are **built on the VPS** from a checkout, tagged with the commit id; no registry | No registry account or credentials needed yet | GHCR (private) + `docker compose pull`, when CI builds the images |
| 5 | Newsletter limit **20 per address per 10 minutes** | Sri Lankan mobile carriers use carrier-grade NAT; 10 could refuse a real campaign | 10 (TASKS row's suggestion) |
| 6 | Migrator credentials (db_owner) are in `config/app.env` | The one-shot migrator needs them at every deploy | Doc 03 §3a wants break-glass CLI credentials entered interactively; that is a different tool (not built yet) |
| 7 | Doc 08 §10.4's `DENY UPDATE ON identity.Roles(GrantsAllPermissions)` is **not** applied yet | `RoleManager.UpdateAsync` writes every column, so the DENY would break role editing in production only | Apply it with the Users & Roles work (W1-09) plus a test that runs as the app login |
| 8 | The Data Protection key ring is a file volume (`data/dataprotection-keys`, #96), with the application name pinned. Encryption at rest with a certificate is **supported but optional** (`DataProtection__CertificatePath`) | A single API container needs no shared store; the volume keeps admin sign-ins and encrypted settings across restarts and deploys. The folder is readable only by root and the API | Doc 08 DB30's `ops.DataProtectionKeys` table — needed only with more than one API replica. **Owner: decide whether to issue the certificate before the first secret (PayPal, SMTP) is saved** |
