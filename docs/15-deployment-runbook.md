# 15 · Deployment runbook (Docker on the VPS)

Task **W0-15** (issue #41). How the platform is packaged and run on the Hostinger VPS **srv1983022**, for
**staging** and **production**. Files live in [`deploy/`](../deploy/). Nothing here has been run on the server
yet; §10 lists what the owner still has to confirm.

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
   │   /media/*       ──► data/media-public (read-only)                                                     │
   │   everything else ──► storefront (Angular SSR) ──► api (server-side calls, http://api:8080)            │
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
| `spencer-denim/migrator` | same, target `migrator` | `dotnet/runtime-deps:10.0.12-alpine3.24` (self-contained bundle) | `app` (1654) | one-shot, exit code |
| `spencer-denim/storefront` | `deploy/docker/frontend.Dockerfile` target `storefront` | `node:24.21.0-alpine3.24` | `node` (1000) | `GET /healthz` |
| `spencer-denim/web` | same, target `web` | `nginxinc/nginx-unprivileged:1.30.5-alpine3.24` | `nginx` (101) | `GET /gateway-health` |
| SQL Server | `deploy/database/compose.yml` | `mssql/server:2025-CU9-ubuntu-24.04` | `mssql` (10001) | `sqlcmd SELECT 1` |

Every application container runs with a read-only root filesystem (writable `/tmp` tmpfs only), all Linux
capabilities dropped and `no-new-privileges`. Logs rotate at 10 MB × 5 files per container. Build contexts are
allow-lists (`deploy/docker/*.Dockerfile.dockerignore`): secrets, `.env` files, `reference/`, `client-progress/`,
`worktrees/`, `node_modules`, `bin/obj` and the docs never enter an image.

## 2. Server layout

Follows the server's existing convention:

```
/opt/apps/<env>/spencer-denim-app/
├── compose/            compose.yml + compose.<env>.yml (copied by install.sh)
│   └── releases/<tag>/ the compose files each deployed release used (rollback uses these)
├── config/app.env      secrets for the stack (root, 600) — template deploy/app/.env.example
├── data/
│   ├── dataprotection-keys/   ASP.NET Data Protection key ring (owner 1654) — admin sign-ins survive restarts
│   ├── media-public/          served at /media (uploads pipeline W1-04 writes here later)
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

Never set `ASPNETCORE_FORWARDEDHEADERS_ENABLED`: that framework switch trusts every peer.

## 4. Configuration

Secrets live only in the two env files on the server (root-owned, mode 600), never in git or an image.
`scripts/check-env.sh <env>` validates both without printing a secret: it refuses `CHANGE_ME` placeholders,
quoted values, `$` (compose would interpolate it), the same login for API and migrator, and passwords containing
`' " ] \ ; $` (they are embedded in T-SQL and connection strings).

| File | Variable | Meaning |
|---|---|---|
| `config/app.env` | `SD_PUBLIC_HOSTS` | Public host name(s), comma-separated; the SSR engine renders only for these (`NG_ALLOWED_HOSTS`) |
| | `SD_APP_DB_CONNECTION` | API login `sd_<env>_app` (read/write). `Server=sqlserver,1433;…;TrustServerCertificate=True` |
| | `SD_MIGRATOR_DB_CONNECTION` | Migrator login `sd_<env>_migrator` (db_owner). The API never sees it |
| | `SD_DB_COMPATIBILITY_LEVEL` | `170` (SQL Server 2025) |
| | `SD_SEED_ADMIN_ENABLED/EMAIL/PASSWORD` | First Super Admin, first install only; set back to `false` afterwards |
| | `SD_IMAGE_REPOSITORY` | `spencer-denim` (image name prefix) |
| `config/db.env` | `SD_ENV` | `production` / `staging` |
| | `SD_MSSQL_SA_PASSWORD` | sa: healthcheck and provisioning only |
| | `SD_MSSQL_PID` | `Express` |
| | `SD_MSSQL_MEMORY_LIMIT_MB` / `SD_MSSQL_CONTAINER_MEMORY` | `2048`/`2560m` production, `1024`/`1536m` staging |

Everything else the API reads (README "Other settings") can be added to the `api` service's `environment:` in
`compose.<env>.yml`. Database names: `SpencerDenim` (production), `SpencerDenim_Staging` (staging).

## 5. First install (once per environment)

Prerequisites on the server: Docker Engine with the Compose plugin **≥ 2.24**, git, curl, the host nginx and
certbot. Run as a sudoer; `<env>` is `staging` first, then `production`.

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

# 3. SQL Server up, then the database (collation Latin1_General_100_CI_AS_SC) and the two logins
sudo /opt/apps/<env>/spencer-denim-app/scripts/db-up.sh <env>
sudo /opt/apps/<env>/spencer-denim-app/scripts/db-provision.sh <env>

# 4. Images, then the first deploy (production takes a verified backup first; on an empty database pass --skip-backup)
tag=$(sudo deploy/scripts/build-images.sh | tail -n 1)
sudo /opt/apps/<env>/spencer-denim-app/scripts/deploy.sh <env> "$tag"

# 5. Host nginx site + certificate
sudo cp deploy/host-nginx/spencer-denim-<env>.conf.template /etc/nginx/sites-available/spencer-denim-<env>.conf
sudo nano /etc/nginx/sites-available/spencer-denim-<env>.conf      # __SERVER_NAME__, __PRIMARY_HOST__
sudo certbot certonly --webroot -w /var/www/certbot -d <host>      # or: certbot --nginx -d <host>
sudo ln -s /etc/nginx/sites-available/spencer-denim-<env>.conf /etc/nginx/sites-enabled/
sudo nginx -t && sudo systemctl reload nginx
```

If `SD_SEED_ADMIN_ENABLED=true` was used for the first Super Admin: sign in once, set it back to `false`, blank the
password in `app.env`, and redeploy the same tag.

Still to do on the database after provisioning (doc 08 §10.4, not automated here): recovery model FULL for
production (SIMPLE for staging), RCSI and Query Store, and the scheduled backup job (doc 08 §8.5).

## 6. Deploy (every release)

```bash
cd /opt/apps/src/spencer-denim-ecommerce
sudo git fetch && sudo git checkout <commit>           # the commit that passed CI and the tester
tag=$(sudo deploy/scripts/build-images.sh | tail -n 1)  # tag = 12-character commit id
sudo deploy/scripts/install.sh <env>                   # refreshes compose files and scripts; config untouched
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

Building on the VPS takes a few minutes and some memory next to the running stack. The alternative is to build
elsewhere and copy: `docker save spencer-denim/{api,migrator,storefront,web}:<tag> | ssh srv1983022 docker load`
(then run `install.sh` from a checkout of the same commit and `deploy.sh`).

## 7. Rollback and backups

```bash
sudo /opt/apps/<env>/spencer-denim-app/scripts/rollback.sh <env>          # the release before the current one
sudo /opt/apps/<env>/spencer-denim-app/scripts/rollback.sh <env> <tag>    # a specific earlier release
```

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
$C logs -f --tail 100 api      # API (JSON, Serilog); also: web (JSON access log), storefront, migrator
$C exec api wget -q -O - http://127.0.0.1:8080/health/ready   # readiness detail (not exposed through the gateway)
tail -n 5 ../data/deploy-history.log

cd /opt/server/database/<env>/compose && docker compose --env-file ../config/db.env logs --tail 100 sqlserver
```

`SD_IMAGE_TAG` is required by the compose file; export it (`export SD_IMAGE_TAG=$(tail -n1 ../data/deploy-history.log | awk '{print $3}')`)
before using `$C` by hand. The host nginx logs are `/var/log/nginx/spencer-denim-<env>.*.log`.

**Upgrading a base image or SQL Server:** change the pinned tag (`deploy/docker/*.Dockerfile` `ARG …_IMAGE`,
`deploy/database/compose.yml`), open a pull request, deploy to staging, then production. For SQL Server read the
cumulative update's notes, take `backup-db.sh` first, then `install.sh <env>` and `db-up.sh <env>`.

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
SD_APP_DB_CONNECTION=Server=sqlserver,1433;Database=SpencerDenim_W015;User Id=sd_w015_app;Password=<pw>;Encrypt=True;TrustServerCertificate=True
SD_MIGRATOR_DB_CONNECTION=Server=sqlserver,1433;Database=SpencerDenim_W015;User Id=sd_w015_migrator;Password=<pw>;Encrypt=True;TrustServerCertificate=True
EOF

cd deploy/app
docker compose -f ../database/compose.yml --env-file /path/outside/repo/w015.env up -d --wait
# create the database and logins: see db-provision.sh (it expects the server layout, so on a laptop pipe
# tools/db/provision-database.sql and deploy/database/sql/logins.sql to sqlcmd with :setvar lines yourself)
docker compose -f compose.yml -f compose.local.yml --env-file /path/outside/repo/w015.env build
docker compose -f compose.yml -f compose.local.yml --env-file /path/outside/repo/w015.env up -d --wait
curl -s http://127.0.0.1:38997/api/catalog/products?pageSize=2
# tear down ONLY your own projects:
docker compose -f compose.yml -f compose.local.yml --env-file /path/outside/repo/w015.env down
docker compose -f ../database/compose.yml --env-file /path/outside/repo/w015.env down
```

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
| 8 | The Data Protection key ring is a file volume (`data/dataprotection-keys`), unencrypted at rest | Keeps admin sign-ins across restarts today | Doc 08 §3.8's certificate-protected key ring in the database, with the Settings work |
