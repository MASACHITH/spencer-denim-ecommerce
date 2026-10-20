# 16 · UAT and Live run sheet

Issue **#195** (OPS-01 hosting/domain/SSL, OPS-03 staging site), part of the handover **#187**. Companion to
[15 · Deployment runbook](15-deployment-runbook.md), which explains *how the platform is packaged*; this sheet is the
*ordered list of what to type, in which order, on the real server*, with the answer to expect after each step.

> **Status: not run.** Everything in this file is a plan. Nothing in it has been run on the VPS, and no sentence in it
> may be read as a result. The scripts it uses were exercised against stand-ins (fake `docker`, `ss`, `nginx`, `dig`; a mock
> shop; a real nginx 1.24 in a container) and nothing else: see [§G](#g-what-was-tested-and-what-only-the-server-can-tell-us).
> Record what really happened in [§F](#f-execution-log).

**Who runs it:** Tharanga (`tharanganuwan`), on the VPS **srv1983022**, pasting the output marked *Paste back* into issue #195.
**UAT is the `staging` environment** (host `staging.<domain>`, OPS-03). **Live is `production`**. Live is brought up
**only after UAT has passed the whole of §C**. Do not build a third environment.

## Conventions

- `TO FILL` is a value only the owner or the DNS/mail provider knows. It is never guessed. Replace it before you run the
  step that needs it.
- `<angle brackets>` are values you substitute while typing. `$HOST`, `$ENV`, `$APP`, `$DB`, `$SRC` are shell variables set in §B0.
- **Secrets never go into git, into an issue comment, or into a message.** Nothing marked *Paste back* prints a secret.
  The three steps that do print one (the generated Data Protection password, the seed admin password, and — only if you
  ask for it — `docker compose config`) say so in capitals and are never pasted.
- Every command is meant to be pasted as shown. If a result differs from *Expect*, **stop** and paste what you got.
- Scripts live in [`deploy/scripts/`](../deploy/scripts/): [`preflight.sh`](../deploy/scripts/preflight.sh) (read-only, before
  install), [`install.sh`](../deploy/scripts/install.sh), [`check-env.sh`](../deploy/scripts/check-env.sh),
  [`make-dp-cert.sh`](../deploy/scripts/make-dp-cert.sh), [`db-up.sh`](../deploy/scripts/db-up.sh),
  [`db-provision.sh`](../deploy/scripts/db-provision.sh), [`build-images.sh`](../deploy/scripts/build-images.sh),
  [`deploy.sh`](../deploy/scripts/deploy.sh), [`rollback.sh`](../deploy/scripts/rollback.sh),
  [`backup-db.sh`](../deploy/scripts/backup-db.sh), and, from any machine, [`verify-public.sh`](../deploy/scripts/verify-public.sh).
  Host nginx files are in [`deploy/host-nginx/`](../deploy/host-nginx/).

---

## A0. The server as it really is (inspected read-only on 2026-10-20)

Everything else in this sheet was first written for a fresh server. **This one is not.** What a read-only look as `deploy1`
(no secrets read, nothing changed) found, and what each fact changes below:

| Fact | Consequence |
|---|---|
| **A shared VPS**: Ubuntu **26.04.1**, nginx **1.28.3**, Docker 29.1.3, 7.9 GB RAM (about 6 GB free), **4 GB swap**, 96 GB disk (21% used) | Staging fits (preflight budgets 2,912 MB for it). Sudo needs a **password**: every step marked *root* is typed by a person at the terminal, not by an assistant |
| **An earlier stack of this project already serves `colombowardrobecompany.com` and `www.`**: compose project `spencer-denim-development` under `/opt/apps/development`, built from `aed9480` (branch `feature/W0-15-docker-packaging`, 29 Sep, the "Spencer Denim" build), web on `127.0.0.1:18080`, nginx site `spencer-denim-development`, certbot lineage `colombowardrobecompany.com` (apex + www) | **Do not touch it** while UAT is built. Going Live is a **cut-over of that nginx site and certificate**, not a new install: see §D0b. The apex DNS record already points at this VPS |
| **A second project** (`purchase-order-*`) with its own containers; `shop.colombowardrobecompany.com` proxies to **its** front end on `0.0.0.0:8080` | Never edit, stop or "tidy" anything of its. The firewall check in B0 will show port 8080 |
| **One SQL Server container** (`sqlserver-development`, `127.0.0.1:1433`, 3 GB cap, about 1 GB used) holds the databases of **both** projects | The staging stack starts its **own** SQL Server (docs/15 §10 #2). Nothing here may connect to, provision into or restart `sqlserver-development` |
| `/opt/server/database/{staging,production}` exist as **empty skeletons** (`compose config data scripts`, 0 files) and `/opt/apps/{staging,production}` hold only the other project's `purchase-order-app` | `install.sh` is safe on the skeletons; preflight now WARNs if any of those directories ever holds files |
| nginx: the stock `default` site is `default_server` on **80 only**; nothing owns 443 by default, so an unknown host on 443 is answered by the first 443 block (today `shop.`, the other project) | Skip the catch-all (B12-b) here. Installing a 443-only copy would turn unknown-host requests into refused handshakes, a change to the other project's behaviour |
| `/etc/letsencrypt/live` is not readable by `deploy1`; certbot's timer is active | Certificates are inspected and issued with `sudo`; every dry run is scoped with `--cert-name` so it cannot touch another tenant's lineage |

---

## A. Facts to gather, and decisions to take, before touching the server

Fill the right-hand column as you learn it. Rows marked **Owner** are the owner's call; **TG** is Tharanga's.

### A1. Facts

| # | Fact | Known value | Your answer |
|---|---|---|---|
| 1 | Live domain | `colombowardrobecompany.com` | |
| 2 | UAT host (OPS-03) | `staging.colombowardrobecompany.com` | |
| 3 | VPS | Hostinger KVM 2, name `srv1983022`, **Ubuntu 26.04.1 LTS, nginx 1.28.3** (inspected 2026-10-20; docs/03 §4 says 24.04) | |
| 4 | VPS public IPv4 | **TO FILL** (`preflight.sh` prints what the machine thinks it is) | |
| 5 | VPS public IPv6, if any | **TO FILL** (none is fine; then no AAAA records at all) | |
| 6 | Where DNS is edited (provider, who has the login) | **TO FILL** | |
| 7 | Is anything live at `colombowardrobecompany.com` today (an old site, a parked page, email-only)? | **Yes**: an earlier "development" stack of this project (A0). §D0b plans the cut-over; the apex and `www` A records already point at this VPS | |
| 8 | Existing MX / SPF / DKIM / DMARC records on the domain | **TO FILL**. Do not touch the MX records; the shop only *sends* mail | |
| 9 | Let's Encrypt contact e-mail (expiry notices) | **TO FILL** | |
| 10 | SSH user and whether it has `sudo` | **TO FILL** | |
| 11 | Other sites served by this VPS's nginx (names) | **TO FILL**: `sudo nginx -T \| grep server_name` after §B1. Matters for the catch-all (§B12-b) | |
| 12 | Release commit to deploy (full SHA) | **TO FILL**. Must contain `8bd27a7` (RCSI, #183). **For Live it must also contain the #179 fix** (gate G1 in §D) | |
| 13 | Where the Data Protection `.pfx` and its password are kept off the server | **TO FILL** (password manager name; who has access) | |

### A2. DNS records to create

Nothing here has been created. Create the UAT records first; **leave the Live record until §D** (see A1 #7). Lower the TTL
(300 s) on a record a day before you change it, so a mistake is quickly undone.

| Type | Name | Value | TTL | For | When |
|---|---|---|---|---|---|
| A | `staging` (`staging.colombowardrobecompany.com`) | **TO FILL**: VPS IPv4 | 300 | UAT | Before §B12 |
| AAAA | `staging` | **TO FILL**: VPS IPv6, **only if** the VPS has one and nginx listens on it; otherwise **no AAAA record** (a stale AAAA sends IPv6 visitors elsewhere) | 300 | UAT | Before §B12 |
| A | `@` (`colombowardrobecompany.com`) | **already** `201.18.193.171` | 300 | Live | nothing to change: the cut-over is in nginx (§D0b) |
| AAAA | `@` | **TO FILL** as above | 300 | Live | §D |
| A or CNAME | `www` | **TO FILL**: VPS IPv4, or CNAME to `@` | 300 | Live, only if the optional www→apex redirect is wanted (A4 #11) | §D |
| TXT (SPF), TXT (DKIM), TXT (DMARC) | on the **sending domain** | **TO FILL**: exactly what the mail provider specifies for the From address in A3 | provider's | Live mail deliverability | Before Live goes public |
| CAA (optional) | `@` | `0 issue "letsencrypt.org"` | 3600 | restricts who may issue certificates | Optional; skip unless the owner wants it |

`preflight.sh` (§B1) checks the A/AAAA rows against the machine's own address and says which is wrong.

### A3. Mail account (non-secret parts only)

The password is **never** written here. It is set once, in `config/app.env` on the server, and nowhere else (the
application reads `Email__Smtp__*` at start-up; there is no settings screen for it yet, `DeferredSettingKeys`).

| Item | Value (env variable) | Your answer |
|---|---|---|
| Provider / account that sends the shop's e-mail | | **TO FILL** |
| SMTP host | `SD_SMTP_HOST` | **TO FILL** |
| SMTP port (587 = STARTTLS, the application's default and its only mode) | `SD_SMTP_PORT` | **TO FILL** |
| SMTP user name | `SD_SMTP_USERNAME` | **TO FILL** |
| From address (required: with no host or no From address nothing is sent) | `SD_SMTP_FROM_ADDRESS` | **TO FILL** |
| From name | `SD_SMTP_FROM_NAME` | **TO FILL** |
| Is a *separate* test mailbox available for UAT? (A4 #9) | | **TO FILL** |
| Where the password is kept | | **TO FILL** |

### A4. Decisions

Each row names the recommended answer, so that "no objection" is also an answer. The first eight are
[docs/15 §10](15-deployment-runbook.md#10-decisions-for-the-owner-to-confirm); the rest are new in this sheet.

| # | Decision | Recommended | Why | Your / owner's answer |
|---|---|---|---|---|
| 1 | Host nginx + certbot in front, not Caddy (docs/15 §10 #1) | **Keep** | The VPS's nginx already owns 80/443 for other sites | |
| 2 | One SQL Server per environment, or UAT on the production instance (docs/15 §10 #2) | **One per environment if `preflight.sh` reports enough memory; otherwise the shared instance** | Two Express instances need ~1.5 GB more RAM. `preflight.sh` prints the numbers and the verdict. **The shared-instance option is not scripted** (see B2): it needs a compose change and manual provisioning, so if it is chosen, raise an issue first | |
| 3 | Ports 38080/38081 and subnets 10.251.0.0/24, 10.251.1.0/24 (§10 #3) | **Keep, if preflight finds them free** | Chosen to be unusual; the check is automatic | |
| 4 | Images built on the VPS from a checkout (§10 #4) | **Keep for the first release** | No registry account needed; build needs memory, see B10 | |
| 5 | Newsletter limit 20 per address per 10 minutes (§10 #5) | **Keep** | CGNAT on Sri Lankan mobile networks | |
| 6 | Migrator (db_owner) credentials in `app.env` (§10 #6) | **Keep** | The one-shot migrator needs them at each deploy | |
| 7 | `DENY UPDATE` on `identity.Roles(GrantsAllPermissions)` not applied (§10 #7) | **Leave** (not part of this task) | Would break role editing | |
| 8 | Issue the Data Protection certificate before the first secret is saved (§10 #8) | **Yes, on both environments, before the first deploy** (§B7) | The key ring is created by the first API start; a certificate switched on later does not re-encrypt keys that already exist (see B7) | |
| 9 | UAT and e-mail | **UAT starts with no SMTP.** If the §5 e-mail checks are required, use a *separate test mailbox* (A3) configured **before the first test order**, and only addresses the testers own | See A5: queued mail is sent the moment SMTP appears | |
| 10 | UAT and PayPal | **Never enter live PayPal credentials on UAT.** PayPal stays off, or on a PayPal *sandbox* app with the mode set to `sandbox` | UAT test orders must not take real money | |
| 11 | www → apex redirect on Live | **Yes, if `www.` exists in DNS; otherwise skip** | One canonical address for search engines and e-mail links | |
| 12 | Password-protect UAT with HTTP basic auth (B13) | **Yes, if the round involves people outside the team**; optional otherwise | `noindex` is only a request to crawlers | |
| 13 | `server_tokens off;` (hides the nginx version in the `Server:` header) | **Yes**, as a one-line http-level snippet (§C1) | `verify-public.sh` warns about it; the Live site template is deliberately left as it is | |
| 14 | Catch-all `default_server` | **Decided by `preflight.sh`**: install the template only if it reports none | A second default_server makes `nginx -t` fail | |
| 15 | Where the code comes from on the server | **Read-only deploy key** on the VPS (B3), or a `git bundle` copied over | The repository is private. The owner must add the key | |

### A5. What UAT must not be able to do (read this before B5)

1. **E-mail.** What the application does with SMTP unset, from the code (`EmailOptions`, `SmtpOptions.IsConfigured`,
   `SendEmailsService`): both `Email__Smtp__Host` and `Email__Smtp__FromAddress` must be set for anything to be sent. With
   either empty, every order, account and password e-mail is still **written to the outbox table `ops.EmailOutbox` as
   `Pending`** and none is sent; the API logs warning `3402` ("N email(s) are queued and cannot be sent…") every 30
   minutes while any are waiting, and the admin alert banner says so. **Nothing is discarded: the queued rows are sent as
   soon as the API next starts with SMTP configured.** So adding SMTP to UAT mid-round mails everything queued so far,
   including test orders for addresses that are not yours. Either leave UAT without SMTP for the whole round (check
   e-mail content by reading the outbox rows, below), or configure a test mailbox before the first test order.
   ```sql
   -- run as in B9 (sqlcmd_stdin staging SpencerDenim_Staging): what UAT has queued.
   -- Status: 0 Pending, 1 Sending, 2 Sent, 3 Failed, 4 Dead, 5 Cancelled
   SELECT TOP 20 Id, TemplateCode, ToEmail, Subject, Status, CreatedAtUtc FROM ops.EmailOutbox ORDER BY Id DESC;
   ```
2. **PayPal.** The PayPal client ID, secret and webhook ID are entered in the admin Settings (`Settings.Payments`, Super Admin
   only) and stored **encrypted with Data Protection in the database**. PayPal is not offered at checkout until they are set
   and the exchange rate is fresh. Its mode is `sandbox` or `live`. UAT has its own database, so nothing set on Live reaches
   it. Rule: on UAT, leave PayPal unconfigured or use sandbox credentials only.
3. **Search engines.** The storefront builds one `robots.txt` for every environment (`Allow: /` and a `Sitemap:` line), and
   nothing in the application sets `noindex`. The staging host-nginx template therefore answers `/robots.txt` itself
   (`Disallow: /`) and sends `X-Robots-Tag: noindex, nofollow` on every response. That asks well-behaved crawlers to stay away;
   it does not stop anyone who has the address. Basic auth (B13) does.
4. **Real customers.** UAT has its own database. Do not restore a Live backup into it without scrubbing e-mail addresses
   and phone numbers first.

---

## B. UAT bring-up (`staging`)

Every step has *Run*, *Expect*, and where useful *Paste back*. Do them in order. Steps B1–B2 change nothing.

### B0. Before you start

- [ ] A1 #4, #9, #10, #12 are filled in; the DNS records for UAT (A2) exist and, if you can, have been visible for the TTL.
- [ ] You can `ssh` in as `deploy1` (key login works) **and you know the `sudo` password**: it asks for one, so the steps marked *root* are typed by a person at the terminal.
- [ ] The firewall allows 22, 80 and 443 from outside (docs/03 §4). Read-only check: `sudo ufw status verbose`. If `ufw`
  is inactive, the provider's firewall (hPanel) is the one to look at. **Port 8080 is the other project's front end and is
  listening on all interfaces today**: that is not ours to change here, but tell the owner.
- [ ] The repository owner (Sachith) is available to add a deploy key during B3.
- [ ] A password manager entry is ready for: the Data Protection password and `.pfx` (B7), the seed admin password (B5).

Set these once per SSH session (they are plain shell variables, not secrets):

```bash
export ENV=staging
export HOST=staging.colombowardrobecompany.com        # A1 #2
export SRC=/opt/apps/src/spencer-denim-ecommerce       # a NEW clone. Never reuse /opt/apps/development/spencer-denim-repo: it belongs to the live stack
export APP=/opt/apps/$ENV/spencer-denim-app
export DB=/opt/server/database/$ENV
```

### B1. Preflight (read-only)

The checkout does not exist on the server yet, so copy just the two files from your own checkout:

```bash
# on YOUR machine, in the repository:
scp deploy/scripts/preflight.sh deploy/scripts/lib.sh <user>@<server>:~/
# on the server:
sudo bash ~/preflight.sh staging "$HOST" 2>&1 | tee ~/preflight-1.txt
```

(`staging` = budget memory and check ports and subnets for UAT only; `both` would also count a production SQL Server that does not
exist yet. Add other host names after `$HOST` only for records that already point at this VPS: the Live apex is added in §D, not now.)

It checks, and only reads: Docker engine and Compose ≥ 2.24, git, curl, openssl, nginx, certbot; ports 38080/38081; the two
subnets against every Docker network and host route; memory and disk against the budget; whether `/opt/apps`, `/opt/server`
exist and `install.sh` has run; whether nginx already has a `default_server`; `/var/www/certbot` and certbot's two TLS
include files; and DNS for the host(s) you name against this server's public address.

*Expect:* exit code 0 (no `FAIL`). `WARN` lines are decisions, handled in B2.
*Paste back:* the whole of `~/preflight-1.txt` (it contains no secret: host names, versions, free memory, one public IP).

### B2. Decide from the preflight

| Preflight says | Do |
|---|---|
| `FAIL … no A record` / `… but this server is <ip>` | Fix DNS at the provider, wait the TTL, re-run B1. Do not go on: certbot will fail. |
| `FAIL … port 3808x … in use` / `FAIL … overlaps 10.251…` | Stop. The addresses are wired into `deploy/app/compose.<env>.yml`, `lib.sh` (`http_port`) and the nginx template together (docs/15 §10 #3). Raise it on #195; do not edit them on the server. |
| `FAIL … memory: only N MB` | Stop; free memory or resize (docs/15 §10 #2). |
| `WARN … memory … covers 5344 MB` | Only when `both` was asked: two SQL Servers will not fit. (Asking for `staging` needs 2,912 MB, which this server has.) The docs/15 fallback (UAT on the production instance) is **not scripted**: it needs `networks.db.name` in `compose.staging.yml` pointed at `sd-production-db` and the staging database and logins created inside the production SQL container by hand (`db-provision.sh` only provisions into the stack of its own environment). Raise an issue and wait; do not improvise on the server. Otherwise continue only if the owner accepts the risk. |
| `WARN … no swap` | Not expected here (4 GB swap). If it appears, keep a second terminal on `watch -n5 free -m` during B10. |
| `WARN … <dir> already holds files` | Stop and look (`ls -laR <dir>`). Something else lives where `install.sh` is about to set owners and modes. An empty skeleton does not warn. |
| `WARN … already a server_name in …spencer-denim-development … THIS project but not the staging/production one` | Expected for the apex and `www` on this server. It means the cut-over is real: do not enable a production site for those names before §D0b. For `staging.…` it must **not** appear. |
| `WARN … NO default_server` + `DECISION: install …00-default…` | Do B12-b. |
| `PASS … default_server already exists … DECISION: do NOT install` | Skip B12-b. Read which file owns it. |
| `WARN … for some of 80/443 × IPv4/IPv6` | Do not install the template as it is; add `default_server` to the missing `listen` lines of the existing catch-all, or install a copy of the template that keeps only the missing lines. |
| `WARN … options-ssl-nginx.conf / ssl-dhparams.pem does not exist` | Do B12-a. |
| `WARN … /var/www/certbot does not exist` | Do B12-a. |
| `FAIL … docker … compose …` | Install/upgrade Docker Engine with the Compose plugin ≥ 2.24 (`docs/15 §5`) and re-run. |
| `WARN … <host> is already a server_name in <file> (not ours)` | Two server blocks for one name: find out who owns it before continuing. |

### B3. Get the code, then re-run preflight from it

The repository is private, so the VPS needs read access. **Route 1: a read-only deploy key** (docs/15 §2):

```bash
sudo install -d -m 700 /root/.ssh
sudo ssh-keygen -t ed25519 -N '' -C "srv1983022 read-only deploy key" -f /root/.ssh/sd_deploy
sudo cat /root/.ssh/sd_deploy.pub          # the PUBLIC half only. Send it to Sachith
```

Sachith adds it on GitHub: repository → *Settings* → *Deploy keys* → *Add deploy key*, **leave "Allow write access" unticked**.

```bash
sudo install -d -m 755 /opt/apps/src
sudo env GIT_SSH_COMMAND='ssh -i /root/.ssh/sd_deploy -o IdentitiesOnly=yes -o StrictHostKeyChecking=accept-new' \
  git clone git@github.com:MASACHITH/spencer-denim-ecommerce.git "$SRC"
cd "$SRC"
sudo git config core.sshCommand 'ssh -i /root/.ssh/sd_deploy -o IdentitiesOnly=yes'
sudo git fetch --tags origin
sudo git checkout --detach <RELEASE COMMIT, A1 #12>
sudo git log -1 --format='%H  %s'
sudo git merge-base --is-ancestor 8bd27a7 HEAD && echo "contains the RCSI fix"
sudo git status --short                    # must print nothing (build-images.sh refuses a dirty tree)
```

(`accept-new` trusts GitHub's host key the first time; compare it with GitHub's published fingerprints if you want to be strict.)
**Route 2, no key on the server:** on your machine `git bundle create sd.bundle --all`, `scp` it over, then
`sudo git clone sd.bundle "$SRC"` and `sudo git checkout --detach <commit>`; later releases arrive as a new bundle with `git fetch`.

Re-run the preflight from the checkout (it can now also check the checkout and the RCSI copy):

```bash
sudo "$SRC"/deploy/scripts/preflight.sh staging "$HOST" 2>&1 | tee ~/preflight-2.txt
```

*Expect:* `PASS … checkout contains the RCSI fix (8bd27a7, #183)`, still no `FAIL`.
*Paste back:* `git log -1` line and the PASS/FAIL summary lines of `~/preflight-2.txt`.

### B4. `install.sh`: **before** `db-provision.sh` (RCSI order, #183)

`install.sh` copies `tools/db/provision-database.sql` to the server; `db-provision.sh` runs *that copy*. Run them in the wrong
order and the database is silently created without `READ_COMMITTED_SNAPSHOT`.

```bash
cd "$SRC" && sudo deploy/scripts/install.sh "$ENV"
sudo ls -l "$APP" "$APP/config" "$APP/config/dataprotection" "$DB" "$DB/config"
sudo grep -c READ_COMMITTED_SNAPSHOT "$DB/scripts/provision-database.sql"
```

*Expect:* `Installing staging from …`, two `created … from the template: fill it in` warnings, `Done.`; `config` is
`drwx------ root`, `config/dataprotection` is `drwxr-x--- root 1654`; the `grep -c` prints `1` or more.
*Paste back:* the `ls -l` output (names and modes only; no file contents).

### B5. Fill in the two env files

This writes generated passwords **without printing them**. Edit the three marked lines first. **It is for `staging` only** (the names,
the database and the memory figures are hard-coded) and **running it a second time generates new passwords**: run it once. Live has its own
version in §D1.

```bash
sudo bash <<'EOF'
set -euo pipefail
ENV=staging
HOST=staging.colombowardrobecompany.com                 # A1 #2
ADMIN_EMAIL='TO-FILL@example.com'                        # TO FILL: first Super Admin's e-mail
APP=/opt/apps/$ENV/spencer-denim-app; DB=/opt/server/database/$ENV
gen() { printf 'Aa1%s' "$(openssl rand -base64 60 | tr -dc 'A-Za-z0-9' | cut -c1-29)"; }   # 32 chars, letters+digits only
sa="$(gen)"; app_pw="$(gen)"; mig_pw="$(gen)"; admin_pw="$(gen)"

# database stack: sa password, and STAGING's memory (the template carries production's 2048 / 2560m)
sed -i -e "s|^SD_MSSQL_SA_PASSWORD=.*|SD_MSSQL_SA_PASSWORD=$sa|" \
       -e 's|^SD_MSSQL_MEMORY_LIMIT_MB=.*|SD_MSSQL_MEMORY_LIMIT_MB=1024|' \
       -e 's|^SD_MSSQL_CONTAINER_MEMORY=.*|SD_MSSQL_CONTAINER_MEMORY=1536m|' "$DB/config/db.env"

# application stack: host, origin, database logins, first Super Admin. SMTP and the Data Protection lines stay EMPTY.
sed -i -e "s|^SD_PUBLIC_HOSTS=.*|SD_PUBLIC_HOSTS=$HOST|" \
       -e "s|^SD_PUBLIC_ORIGIN=.*|SD_PUBLIC_ORIGIN=https://$HOST|" \
       -e "s|^SD_APP_DB_CONNECTION=.*|SD_APP_DB_CONNECTION=Server=sqlserver,1433;Database=SpencerDenim_Staging;User Id=sd_staging_app;Password=$app_pw;Encrypt=True;TrustServerCertificate=True|" \
       -e "s|^SD_MIGRATOR_DB_CONNECTION=.*|SD_MIGRATOR_DB_CONNECTION=Server=sqlserver,1433;Database=SpencerDenim_Staging;User Id=sd_staging_migrator;Password=$mig_pw;Encrypt=True;TrustServerCertificate=True|" \
       -e 's|^SD_SEED_ADMIN_ENABLED=.*|SD_SEED_ADMIN_ENABLED=true|' \
       -e "s|^SD_SEED_ADMIN_EMAIL=.*|SD_SEED_ADMIN_EMAIL=$ADMIN_EMAIL|" \
       -e "s|^SD_SEED_ADMIN_PASSWORD=.*|SD_SEED_ADMIN_PASSWORD=$admin_pw|" "$APP/config/app.env"
chmod 600 "$APP/config/app.env" "$DB/config/db.env"

echo
echo "################ SHOWN ONCE: the first Super Admin's password. Put it in the password manager. DO NOT PASTE IT BACK ################"
echo "$ADMIN_EMAIL   $admin_pw"
echo "###############################################################################################################################"
EOF
```

*Expect:* the boxed line with the admin e-mail and password, and nothing else. **Save that password in the password manager now**, then
run `clear` so it does not stay in the terminal's scrollback.
Notes: the seed password is 32 characters, comfortably above the staff minimum of 10 that issue **#200** is introducing. The sa
and the two login passwords live only in the two root-only files; read them with `sudo` when needed, never paste them. The
`sed` lines are safe because the generated passwords contain only letters and digits.

### B6. Check the env files

```bash
sudo "$APP"/scripts/check-env.sh "$ENV"
```

*Expect:* `staging: config/app.env and config/db.env look complete (database SpencerDenim_Staging).` (It refuses `CHANGE_ME`
placeholders, quoted values, `$`, a wrong `SD_PUBLIC_ORIGIN` host, and, after B7, a bad certificate setting.)
*Paste back:* that one line.

### B7. The Data Protection certificate, **before the first deploy**

Why now: the key ring is created by the first API start. A certificate switched on *later* encrypts only keys created after
that; the ones already on disk stay in clear text until the ring is rebuilt (below). And once PayPal credentials are saved in
the admin Settings, rebuilding the ring would make them unreadable. So: certificate first, secrets after.

```bash
sudo "$APP"/scripts/make-dp-cert.sh "$ENV" --write-env
```

*Expect:* it prints the certificate's expiry (10 years), the SHA-256 of the certificate and of the `.pfx` file, and — **on the
terminal only, once** — a boxed password. It refuses if a `.pfx` or a key ring already exists. It has written
`$APP/config/dataprotection/dataprotection.pfx` (owner root, group 1654, mode 640) and set `SD_DP_CERT_FILE` /
`SD_DP_CERT_PASSWORD` in `app.env` (mode 600).

1. **Save the password now**, in the password manager, then `clear` the terminal.
2. **Copy the `.pfx` off the server** and prove the copy is intact:
   ```bash
   # on the server (the .pfx is encrypted with the password, but treat the text as secret anyway)
   sudo base64 -w0 "$APP"/config/dataprotection/dataprotection.pfx; echo
   # on YOUR machine: paste the text between the quotes
   echo '<the base64 text>' | base64 -d > dataprotection-staging.pfx
   sha256sum dataprotection-staging.pfx        # must equal "SHA-256 of the .pfx file" printed by the script
   ```
   Store it next to the password. Never commit it (`*.pfx` is git-ignored). **A key ring restored from a backup without
   this file and this password cannot be read**: every admin sign-in and every encrypted setting goes with it.
3. `sudo "$APP"/scripts/check-env.sh "$ENV"` — it now also checks that the file is mode ≤ 640, owned by root, and opens with the password.
4. After the first deploy (B11), every key file must be encrypted. The folder is readable by root only, so the wildcard has to be
   expanded by root: `sudo bash -c 'grep -L encryptedSecret "$1"/data/dataprotection-keys/key-*.xml' _ "$APP"` must print **nothing**
   (it lists any key file that is *not* encrypted).

*Paste back:* the script's non-secret lines (expiry, the two SHA-256 values) and the `check-env.sh` line. **Not** the password box.

**A key ring that already exists** (the script refuses without `--allow-existing-keyring`). Only two honest ways out:
(a) nothing was saved through the admin Settings yet: stop the API (`$C stop api`), move the ring aside
(`sudo mv "$APP"/data/dataprotection-keys "$APP"/data/dataprotection-keys.old-$(date +%F)` and recreate it empty, owner 1654, mode 700),
run `make-dp-cert.sh`, start the API; every admin session is signed out, nothing else is lost. (b) Secrets are already saved:
leave the ring alone, or rebuild it and re-enter each secret afterwards; do not do this on Live without the owner.
Either way the old ring may still hold clear-text keys: delete the `.old-…` copy once you are sure.

### B8. SQL Server up

```bash
sudo "$APP"/scripts/db-up.sh "$ENV"
```

*Expect:* `Starting SQL Server (staging)...`; the first run pulls the SQL Server image (~1.5 GB, a few minutes); the `ps` table at the end shows
`sqlserver … healthy`. *Paste back:* that table.

### B9. Create the database and logins, then prove READ_COMMITTED_SNAPSHOT is on

```bash
sudo "$APP"/scripts/db-provision.sh "$ENV"
sudo bash <<EOF
source $APP/scripts/lib.sh
sqlcmd_stdin $ENV master <<'SQL'
SELECT name, is_read_committed_snapshot_on FROM sys.databases WHERE name = N'SpencerDenim_Staging';
SQL
EOF
```

*Expect:* `Creating database [SpencerDenim_Staging] if missing (staging)...`, `Creating or updating logins [sd_staging_app] and
[sd_staging_migrator]...`, `Done.`; then a one-row result **`SpencerDenim_Staging  1`**.
**If the second column is `0`, stop**: `install.sh` was not run from a checkout at `8bd27a7` or later, or ran after this step.
Fix it with the command in docs/15 §5 ("Existing database without READ_COMMITTED_SNAPSHOT") before any API connects.
*Paste back:* the result row.

### B10. Build the images (a few minutes; memory-hungry)

**This host is shared.** An Angular plus .NET build can use several GB at its peak; with about 6 GB free, 4 GB of swap, and the other
project's API and the shared SQL Server running, an out-of-memory kill could hit **their** processes first. So either build at a
quiet moment with `watch -n5 free -m` in a second terminal (stop and tell the owner if swap starts filling), or build elsewhere and
ship the images (docs/15 §10 #4, **not yet exercised**):
`docker save spencer-denim/api:$tag spencer-denim/migrator:$tag spencer-denim/storefront:$tag spencer-denim/web:$tag | gzip | ssh deploy1@201.18.193.171 'gunzip | docker load'`.

```bash
cd "$SRC"
sudo deploy/scripts/build-images.sh 2>&1 | tee ~/build-staging.log | tail -n 3
tag=$(tail -n 1 ~/build-staging.log); echo "tag=$tag"
[ "${#tag}" = 12 ] || echo "NOT A TAG: the build failed, read ~/build-staging.log"
sudo docker images 'spencer-denim/*' --format '{{.Repository}}:{{.Tag}}  {{.Size}}'
```

*Expect:* `tag=` followed by 12 hex characters (the commit id), no "NOT A TAG" line, and four images (`api`, `migrator`, `storefront`, `web`).
If the build is killed ("Killed", exit 137) it ran out of memory: see the swap warning in B2 and docs/15 §10 #4 (build elsewhere,
`docker save | ssh … docker load`). *Paste back:* the `tag=` line and the image list.

### B11. Deploy

```bash
sudo "$APP"/scripts/deploy.sh "$ENV" "$tag" --skip-backup 2>&1 | tee ~/deploy-staging.log | tail -n 25
```

(`--skip-backup` is for the **first deploy on an empty database**: there is nothing yet to back up, docs/15 §5. On staging
`deploy.sh` never takes a backup, so the flag changes nothing here; it is written in so that the first Live deploy is the same
command. Every later deploy on Live is run **without** it.)

*Expect:* `Deploying staging at <tag> ...`, the migrator and API become healthy, `Smoke test through 127.0.0.1:38081 as
staging.colombowardrobecompany.com ...` passes, and the last line is **`staging is running <tag>.`** If it fails it prints
the tail of the migrator and API logs: **read them for the word `Password` before pasting**.

Then the two checks docs/15 §6 asks for after every deploy, and one more:

```bash
cd "$APP"/compose
C="sudo env SD_IMAGE_TAG=$tag docker compose -p sd-$ENV --project-directory . -f compose.yml -f compose.$ENV.yml --env-file ../config/app.env"
$C ps
$C logs --tail 500 api | grep "is missing"          # normal result: no output at all
sudo bash -c 'grep -L encryptedSecret "$1"/data/dataprotection-keys/key-*.xml' _ "$APP"    # normal: no output (every key encrypted)
sudo ss -ltn | grep -E ':(3808[01]|1433)\b'          # 127.0.0.1:38081 yes; 127.0.0.1:1433 is the OTHER SQL Server, expected
```

*Expect:* all services `running`/`healthy`; no "is missing" line (on a brand-new database a warning there means the role
seeding is wrong: docs/15 §6); the `ss` output shows `127.0.0.1:38081` and **not** `0.0.0.0`. On this server `127.0.0.1:1433` is also listed: that is `sqlserver-development`, not ours (our SQL Server publishes no port). Anything on `0.0.0.0:3808x` or `0.0.0.0:1433` is wrong.
*Paste back:* `$C ps` and the output of the last two commands.

Do **not** run `docker compose config` and paste it: it prints every secret.

### B12. Host nginx and the certificate

docs/15 §5 lists "certbot, then link the site". On a new host that order cannot work: the site template names a certificate
that does not exist yet, so `nginx -t` fails, and without the site nginx does not answer certbot's challenge for the new
name. So: a tiny port-80 site first, then the certificate, then the real site.

**a. Prerequisites** (only what B1 flagged)

```bash
sudo install -d -m 755 /var/www/certbot /etc/letsencrypt
# Is there a packaged copy of certbot's TLS options? (If so, prefer it: sudo cp <that file> /etc/letsencrypt/options-ssl-nginx.conf)
sudo find /usr/lib /snap -name options-ssl-nginx.conf 2>/dev/null | head -n 3
# Each of the next two commands does nothing if the file already exists, so the block is safe to paste as a whole:
[ -f /etc/letsencrypt/options-ssl-nginx.conf ] || sudo tee /etc/letsencrypt/options-ssl-nginx.conf >/dev/null <<'CONF'
ssl_session_cache shared:le_nginx_SSL:10m;
ssl_session_timeout 1440m;
ssl_session_tickets off;
ssl_protocols TLSv1.2 TLSv1.3;
ssl_prefer_server_ciphers off;
ssl_ciphers "ECDHE-ECDSA-AES128-GCM-SHA256:ECDHE-RSA-AES128-GCM-SHA256:ECDHE-ECDSA-AES256-GCM-SHA384:ECDHE-RSA-AES256-GCM-SHA384:ECDHE-ECDSA-CHACHA20-POLY1305:ECDHE-RSA-CHACHA20-POLY1305:DHE-RSA-AES128-GCM-SHA256:DHE-RSA-AES256-GCM-SHA384";
CONF
[ -f /etc/letsencrypt/ssl-dhparams.pem ] || sudo openssl dhparam -out /etc/letsencrypt/ssl-dhparams.pem 2048     # up to a minute
```

The fallback options file is written from memory of certbot's own (Mozilla "intermediate" settings), not copied from it, so prefer a
packaged copy if `find` printed one. If other sites here already use certbot's nginx installer, both files exist and the block does
nothing.

**b. The catch-all: SKIP on this server.** (A0: `default` is already `default_server` on 80, nothing owns 443, and the unknown-host
check in `verify-public.sh` accepts another site's answer. A 443-only copy would change what unknown-host requests receive, for the other
project too. The rest of this step is for a server where B1 said "install".)

First check what depends on being "first": `sudo nginx -T | grep -E 'server_name|listen'`. Every domain served from this VPS must
have its own `server_name`; a site that only works because it happens to be the first block (for example when reached by the
bare IP address or by a domain with no `server_name`) will stop answering once a catch-all exists.

```bash
sudo cp "$SRC"/deploy/host-nginx/spencer-denim-00-default.conf.template /etc/nginx/sites-available/spencer-denim-00-default.conf
sudo ln -sf ../sites-available/spencer-denim-00-default.conf /etc/nginx/sites-enabled/
sudo nginx -t
```

*Expect:* `syntax is ok` / `test is successful`. A message `a duplicate default server` means one exists after all: remove the
link you just made (`sudo rm /etc/nginx/sites-enabled/spencer-denim-00-default.conf`) and do not install it. Do not reload yet.

**c. A port-80 bootstrap site for certbot's challenge**

```bash
sudo tee /etc/nginx/sites-available/spencer-denim-$ENV-acme.conf >/dev/null <<CONF
server {
    listen 80;
    listen [::]:80;
    server_name $HOST;
    location ^~ /.well-known/acme-challenge/ { root /var/www/certbot; default_type text/plain; }
    location / { return 404; }
}
CONF
sudo ln -sf ../sites-available/spencer-denim-$ENV-acme.conf /etc/nginx/sites-enabled/
sudo nginx -t && sudo systemctl reload nginx
curl -sI "http://$HOST/" | head -n 1                  # expect: HTTP/1.1 404 Not Found   (it reaches OUR block)
```

**d. Certificate: rehearsal, then the real one**

```bash
LE_EMAIL='TO-FILL@example.com'                         # A1 #9
sudo certbot certonly --webroot -w /var/www/certbot -d "$HOST" -m "$LE_EMAIL" --agree-tos --no-eff-email --dry-run
sudo certbot certonly --webroot -w /var/www/certbot -d "$HOST" -m "$LE_EMAIL" --agree-tos --no-eff-email \
  --deploy-hook 'systemctl reload nginx'
sudo ls -l /etc/letsencrypt/live/"$HOST"/
```

(The dry run uses Let's Encrypt's staging service and proves the whole challenge path without spending the real service's
rate limit. `--deploy-hook` is what reloads nginx after each renewal: `certonly` does not do that by itself.)
*Expect:* `The dry run was successful.`, then `Successfully received certificate.`, and `fullchain.pem`, `privkey.pem` listed.

**e. Swap the bootstrap for the real site**

```bash
sudo sed -e "s/__SERVER_NAME__/$HOST/g" -e "s/__PRIMARY_HOST__/$HOST/g" \
  "$SRC"/deploy/host-nginx/spencer-denim-$ENV.conf.template | sudo tee /etc/nginx/sites-available/spencer-denim-$ENV.conf >/dev/null
grep -n '__[A-Z_]*__' /etc/nginx/sites-available/spencer-denim-$ENV.conf         # must print nothing
sudo rm /etc/nginx/sites-enabled/spencer-denim-$ENV-acme.conf
sudo ln -sf ../sites-available/spencer-denim-$ENV.conf /etc/nginx/sites-enabled/
sudo nginx -t && sudo systemctl reload nginx
sudo certbot renew --dry-run --cert-name "$HOST"     # expect: the simulated renewal of THIS certificate succeeded (never run it unscoped here)
```

*Expect:* `nginx -t` ok, reload silent, renewal dry run succeeds. (`--cert-name` matters: an unscoped dry run also simulates the live
development site's and the other project's certificates, with whatever hooks they use, and a failure there would look like ours.)
**Side effect to expect:** the templates say `listen 443 ssl http2`, and HTTP/2 is a property of the listening socket, so the other sites on
`:443` start speaking HTTP/2 as well (nginx also prints `protocol options redefined` warnings). It is harmless for ordinary sites but it is
a change to them; the nginx 1.25.1+ form (`listen 443 ssl;` plus `http2 on;`) avoids the warnings and is a template change for a later release.
**Before enabling the site:** `sudo nginx -T | grep -E '^\s*upstream'`: the templates define `upstream spencer_denim_staging` / `…_production`
and an upstream name is global, so a stack that already defined one would make `nginx -t` fail with `duplicate upstream`. (If `nginx -t` fails, nothing has been reloaded: fix or
remove the new link and nothing else on the machine has changed.)
*Paste back:* the `nginx -t` and `certbot renew --dry-run` result lines.

### B13. Optional: password-protect UAT (off by default)

Decision A4 #12. When on, the whole host asks for a user name and password, including `/robots.txt` and the API.

```bash
sudo apt-get install -y apache2-utils                         # provides htpasswd (skip if present)
sudo htpasswd -c /etc/nginx/spencer-denim-staging.htpasswd uatuser     # -c creates the file; it prompts for the password (not echoed)
sudo htpasswd /etc/nginx/spencer-denim-staging.htpasswd secondtester   # no -c: adds another user
sudo chown root:www-data /etc/nginx/spencer-denim-staging.htpasswd && sudo chmod 640 /etc/nginx/spencer-denim-staging.htpasswd
# without apache2-utils:  printf 'uatuser:%s\n' "$(openssl passwd -apr1)" | sudo tee -a /etc/nginx/spencer-denim-staging.htpasswd
```

Then switch the two lines on (they are commented in the site file, in the block headed "OPTIONAL, OFF by default") and reload:

```bash
sudo sed -i -e 's/^    #   auth_basic           "UAT";/    auth_basic           "UAT";/' \
            -e 's|^    #   auth_basic_user_file |    auth_basic_user_file |' /etc/nginx/sites-available/spencer-denim-staging.conf
grep -n '^    auth_basic' /etc/nginx/sites-available/spencer-denim-staging.conf      # must show both lines
sudo nginx -t && sudo systemctl reload nginx
```

If a PayPal **sandbox** is wired to UAT, also uncomment the `location = /api/payments/paypal/webhook` block in that file (PayPal's
servers cannot answer a password prompt). To run `verify-public.sh` against a protected host:
`SD_VERIFY_BASIC_AUTH='uatuser:<password>' deploy/scripts/verify-public.sh …`; without it the run stops at `https-200` with a 401 and says why.
To switch it off, put the `#   ` back in front of the two lines and reload.

### B14. What a good end of §B looks like

`staging is running <tag>`; `https://staging.colombowardrobecompany.com/` loads in a browser with a valid certificate; sign in at
`/admin/` with the seed admin. Then **set the seed admin back** (it is first-install only): in `$APP/config/app.env` set
`SD_SEED_ADMIN_ENABLED=false` and blank `SD_SEED_ADMIN_PASSWORD=`, then redeploy the same tag
(`sudo "$APP"/scripts/deploy.sh "$ENV" "$tag"`). Continue with §C.

---

## C. Verify UAT, then run the UAT round

### C1. Automated checks from outside

Run from **any machine that is not the server** (your laptop, with Git Bash on Windows). It needs `bash`, `curl`, `openssl`, nothing else.

```bash
deploy/scripts/verify-public.sh staging https://staging.colombowardrobecompany.com
# before DNS is changed, or to test one particular server:   --resolve <VPS IPv4>
# with a certificate that is not issued yet:                  --insecure
# behind basic auth (B13):                                    SD_VERIFY_BASIC_AUTH='user:password' …
```

It is read-only (every request is a GET; one POST to the page-view endpoint with an address the API ignores, answered
204 and not recorded). Requests carry a bot User-Agent, so the few page views it makes are counted as bot views.
What it checks: HTTP→HTTPS is a 301 that keeps path and query; HTTPS 200; the certificate chain verifies for the host name and
its days to expiry are printed (warns under 21); HSTS; the home page is server-rendered (`ng-server-context="ssr"` exactly
once); `robots.txt` is right for the environment (staging: disallow all, no sitemap; production: allow, `Sitemap:` with the https
origin); `sitemap.xml` names only the right host; `X-Robots-Tag: noindex` is present on staging and absent on production;
`/admin/`, `/api/catalog/departments`; **an unknown `Host` is not answered by the shop** (HTTPS and HTTP); a made-up `/order/…`
token and `/account/reset-password?token=x` do not 5xx and show no stack trace; the page-view endpoint answers 204; no version
in `Server:` and no `X-Powered-By`; 404s are clean; `/internal/…` and `/health/ready` are not reachable from outside; cookie
flags (Secure required, HttpOnly and SameSite warned).

*Expect:* `RESULT: no FAIL`. `WARN server-header` means the host nginx shows its version: A4 #13, fixed for the whole machine by
```bash
echo 'server_tokens off;' | sudo tee /etc/nginx/conf.d/00-server-tokens.conf && sudo nginx -t && sudo systemctl reload nginx
```
(it applies to every site on this nginx: harmless, but not ours alone, so ask first. Ubuntu's own `nginx.conf` carries a commented
`# server_tokens off;` in the `http` block; if you would rather uncomment that line, do that instead. If `nginx -t` complains the
directive is duplicate, it is already set there: remove the new file.) A `FAIL unknown-host-https` means the catch-all is
missing (B12-b).
*Paste back:* the table at the end of the output.

### C2. The UAT round (#187 §5)

Run on **a real phone, a tablet and a desktop**, and at the project's eight breakpoints:
**320 · 375 · 390 · 768 · 1024 · 1280 · 1440 · 1920 px**. **Record the result of every step**, in the table below (copy it into issue #195; #187 §6
also asks for the finished record as a document under `docs/`). Record every defect as an issue, fix it, and re-run the affected steps **on UAT**
before Live. Remember A5: UAT sends no e-mail unless you gave it a test mailbox.

Result codes: **P** pass, **F** fail (issue #…), **N/A**, **B** blocked. Fill *Device / px* with what you actually used.

| # | Step | Device / px | Result | Issue | By · date |
|---|---|---|---|---|---|
| **Shopper** | | | | | |
| S1 | Homepage | | | | |
| S2 | Department page | | | | |
| S3 | Category page, with filters and sort | | | | |
| S4 | Search | | | | |
| S5 | Product page: gallery | | | | |
| S6 | Product page: size, colour, size guide | | | | |
| S7 | Quick View | | | | |
| S8 | Wishlist | | | | |
| S9 | Bag and mini bag | | | | |
| S10 | Register → confirm the e-mail → sign in; **the guest bag survives** (#162) | | | | |
| S11 | Sign out | | | | |
| S12 | Checkout as a **guest**, delivery | | | | |
| S13 | Checkout as a guest, collection | | | | |
| S14 | Checkout as a **customer**, delivery | | | | |
| S15 | Checkout as a customer, collection | | | | |
| S16 | **Bank transfer**: instructions shown and e-mailed, slip upload | | | | |
| S17 | Promo code: percentage | | | | |
| S18 | Promo code: fixed amount | | | | |
| S19 | Promo code: free delivery | | | | |
| S20 | Sale campaign price | | | | |
| S21 | My account: order history **with tracking** | | | | |
| S22 | My account: saved addresses | | | | |
| S23 | My account: password reset | | | | |
| S24 | The guest `/order/{token}` page | | | | |
| **Admin** | | | | | |
| A1 | Approve a transfer → process → ship with a courier and tracking number → deliver | | | | |
| A2 | Print the invoice | | | | |
| A3 | Cancel an unpaid order | | | | |
| A4 | Accept a late payment | | | | |
| A5 | Create a promo code | | | | |
| A6 | Create a sale campaign | | | | |
| A7 | Customers list and detail | | | | |
| A8 | Newsletter list and **CSV export**: open it in Excel; check Sinhala and Tamil names | | | | |
| A9 | Dashboard figures against the orders you just placed | | | | |
| A10 | Reports and their exports | | | | |
| A11 | The traffic page | | | | |
| A12 | Alerts banner | | | | |
| A13 | Staff users and roles | | | | |
| A14 | Settings | | | | |
| **E-mails** (needs the SMTP account, see A5) | | | | | |
| E1 | Order placed | | | | |
| E2 | Bank instructions | | | | |
| E3 | Payment confirmed | | | | |
| E4 | Expired | | | | |
| E5 | Cancelled | | | | |
| E6 | Shipped | | | | |
| E7 | Password reset | | | | |
| E8 | E-mail confirmation | | | | |

The same round must also be walked once at each of the eight breakpoints; keep a row per breakpoint:

| Breakpoint | 320 | 375 | 390 | 768 | 1024 | 1280 | 1440 | 1920 |
|---|---|---|---|---|---|---|---|---|
| Layout intact, **no horizontal scroll** | | | | | | | | |
| Checkout completed | | | | | | | | |
| Admin screens usable | | | | | | | | |

### C3. Gate to Live

Live starts only when: C1 shows no `FAIL` on UAT; every C2 row is **P** (or has a fixed issue re-tested on UAT); the owner has
said go; and the §D gate items are met.

---

## D. Live bring-up (`production`): only after §C passed

Same commands as §B with the differences below. **Do not start before the gate.**

### D0. Gate

- [ ] §C passed on UAT, and the owner said go. Date / who: ________
- [ ] **#179 is merged and in the release commit** (both nginx access logs recorded order tokens and reset tokens). Check:
  `git log --oneline <commit> | grep -i 179`, then read the gateway's `log_format` and the host templates' `access_log` lines.
  Nothing is deployed yet, so no customer data sits in any log; keep it that way.
- [ ] #187 §1 and §7 "before launch" items are closed or consciously deferred by the owner: #84, #107, #185, #117, #159, #145, #147, #79, #29, and **#200** (staff passwords).
- [ ] **No automated backups exist yet (#196, in the signed scope, not built).** Decide with the owner whether Live takes real orders before it is. At minimum, take `backup-db.sh production` by hand before launch and copy it off the server.
- [ ] Traffic recording (#180) **counts real visitors from the first run, with no backfill**: do not bring Live up "to see it" and leave it public.
- [ ] A1 #7 answered (is anything live at the apex today?), and the TTL on that record was lowered a day ago.
- [ ] The Data Protection `.pfx` for Live is made **before** the first deploy, and kept off the server (B7). Live has its own, different certificate and password.
- [ ] PayPal: stays off at launch unless the owner wants it (#187 §2: never tested against the real sandbox, no independent review). Live credentials are entered only after the certificate exists.

### D0b. The cut-over from the earlier "development" stack

Both stacks cannot serve the same names. nginx accepts the second server block for `colombowardrobecompany.com` with only a warning
(`conflicting server name … ignored`) and the **first** one, `spencer-denim-development`, keeps the traffic. So the cut-over is one
planned switch of the nginx site, done only after UAT passed and the owner said go:

1. **Decide what happens to the development data** (the database in the shared SQL Server, uploads under `/opt/apps/development/spencer-denim-app/data`,
   the Data Protection key ring). The production stack starts on an **empty** database. If the development data is test data, say so in
   writing and leave it; if anything real was ever entered there, it needs a migration plan before this step. **Not decided: ask the owner.**
2. Bring the production stack up and healthy on `127.0.0.1:38080` as in D1, **without** touching nginx. `deploy.sh`'s smoke test uses the loopback port.
3. In one root session: write the production site file from the template (B12-e, using the **existing** certificate), then
   `sudo rm /etc/nginx/sites-enabled/spencer-denim-development && sudo ln -sf ../sites-available/spencer-denim-production.conf /etc/nginx/sites-enabled/ && sudo nginx -t && sudo systemctl reload nginx`.
   Leave `/etc/nginx/sites-available/spencer-denim-development` in place: **rollback is the reverse of that one line**, instantly, while the old
   containers are still running.
4. `verify-public.sh production https://colombowardrobecompany.com`, and look at the page.
5. Leave the development containers **running but unreachable** for an agreed period, then stop them (`docker compose … stop`, never `down -v`).
   The `shop.colombowardrobecompany.com` site and everything of `purchase-order-*` stay exactly as they are.
6. `www`: the existing certificate covers it. Decide with the owner whether it redirects to the apex (D3) or serves the shop.

### D1. Repeat B1–B12 with these differences

| Step | `staging` (UAT) | `production` (Live) |
|---|---|---|
| Variables (B0) | `ENV=staging`, `HOST=staging.colombowardrobecompany.com` | `ENV=production`, `HOST=colombowardrobecompany.com` |
| Preflight (B1/B3) | host `$HOST` | add `$HOST` (and `www.$HOST` if A4 #11): the apex's DNS may still point elsewhere, so the DNS line may FAIL until the cut-over below. Everything else must be clean |
| B4 install | `install.sh staging` | `install.sh production`. The checkout is the one that passed UAT: **same commit, same tag** |
| B5 env | database `SpencerDenim_Staging`, logins `sd_staging_*`, memory `1024` / `1536m` | database **`SpencerDenim`**, logins **`sd_production_app`** / **`sd_production_migrator`**, memory left at the template's **`2048` / `2560m`**. Use new generated passwords: never reuse UAT's |
| SMTP (B5) | empty | **Set** `SD_SMTP_HOST`, `SD_SMTP_PORT`, `SD_SMTP_USERNAME`, `SD_SMTP_PASSWORD`, `SD_SMTP_FROM_ADDRESS`, `SD_SMTP_FROM_NAME` (A3). The password goes through your own `sed`/editor straight into `app.env`, never into a message. Reminder: messages queued while SMTP was unset are sent at the first start with it set — on a new Live database there are none |
| Seed admin (B5) | test admin | the **owner's** e-mail; the generated password is ≥ 10 characters (the staff minimum rises to 10 with #200, so a shorter one would be refused). After the first sign-in set `SD_SEED_ADMIN_ENABLED=false`, blank the password, redeploy the same tag |
| B7 certificate | before first deploy | before first deploy, same procedure; **separate** `.pfx` and password; the off-server copy is not optional |
| B9 RCSI check | `SpencerDenim_Staging` → `1` | `SpencerDenim` → `1`. Replace the database name in the `SELECT`. |
| B10 build | build | **Do not rebuild.** Use the image tag UAT ran (`docker images`). If the images were lost, rebuild from the same commit |
| B11 deploy | `deploy.sh staging "$tag"` | **First deploy on the new, empty database only:** `deploy.sh production "$tag" --skip-backup` (docs/15 §5: there is nothing to back up). **Every later deploy: no flag**, so the verified `COPY_ONLY` backup runs first |
| B12 nginx | staging template; noindex and robots override | **production** template (`spencer-denim-production.conf.template`): no noindex, no robots override: the storefront's own `robots.txt` and sitemap are served. Sitemap and Search Console submission are the owner's |
| B13 basic auth | optional | **never** |
| Verify (C1) | `verify-public.sh staging https://staging…` | `verify-public.sh production https://colombowardrobecompany.com` (and `--www-redirect` if the block is on) |

### D2. DNS cut-over and the certificate (Live)

1. Everything in D1 up to B11 is done: `deploy.sh` ended with `production is running <tag>` (its smoke test is the pre-DNS check,
   it talks to the loopback port).
2. B12-a (prerequisites), B12-b (catch-all, only if still needed) and B12-c (the port-80 bootstrap site for `colombowardrobecompany.com`).
   The certificate cannot be issued before DNS points here, so nothing can be tested over HTTPS yet.
3. **The apex and `www` already point at this VPS** (A0), so there is no DNS change. The cut-over is in nginx: follow §D0b.
4. The certificate: **reuse the existing lineage** `colombowardrobecompany.com` (it already covers the apex and `www`; check with `sudo certbot certificates`). Do **not** run B12-d for the apex: a request for the apex alone would create a second lineage `colombowardrobecompany.com-0001` while the template points at the old one. Then B12-e with the **production** template, as part of the cut-over in §D0b.
5. From outside: `verify-public.sh production https://colombowardrobecompany.com` → `RESULT: no FAIL`, **and look at the page**: the
   storefront must say the new name, not "Spencer Denim" (`verify-public.sh` does not tell the two stacks apart); look at the certificate in a
   browser; `sudo certbot renew --dry-run --cert-name colombowardrobecompany.com` succeeds.

**HSTS is one year** (`max-age=31536000`) from the first HTTPS response a browser sees. Only go public once the dry-run renewal
works, or an expired certificate cannot be bypassed by visitors.

### D3. The optional www → apex redirect (A4 #11)

Only if `www.colombowardrobecompany.com` exists in DNS (A2) **and** the certificate names it. At the end of the production template
there is a commented block with the full explanation. Short version:

```bash
# 1. certificate that covers both names (the first -d decides the directory name):
sudo certbot certonly --webroot -w /var/www/certbot -d colombowardrobecompany.com -d www.colombowardrobecompany.com \
  --cert-name colombowardrobecompany.com --expand --deploy-hook 'systemctl reload nginx'
# 2. enable the block: strip the leading "# " from the two server blocks at the end of the file, fill in <domain>
sudo awk '/^# server \{/ { on = 1 } on { sub(/^# ?/, "") } { print }' /etc/nginx/sites-available/spencer-denim-production.conf \
  | sed 's/<domain>/colombowardrobecompany.com/g' | sudo tee /etc/nginx/sites-available/spencer-denim-production.conf.new >/dev/null
sudo cp /etc/nginx/sites-available/spencer-denim-production.conf /etc/nginx/sites-available/spencer-denim-production.conf.bak
sudo mv /etc/nginx/sites-available/spencer-denim-production.conf.new /etc/nginx/sites-available/spencer-denim-production.conf
sudo nginx -t && sudo systemctl reload nginx
# 3. from outside:
deploy/scripts/verify-public.sh production https://colombowardrobecompany.com --www-redirect
```

`SD_PUBLIC_HOSTS` and the main server blocks' `server_name` stay the apex alone. To undo: copy the `.bak` back, `nginx -t`, reload.

### D4. After go-live

Read `$C logs --tail 500 api | grep "is missing"` (nothing expected on a new database), check the key files are encrypted (B7 item 4),
confirm the admin banner shows no "e-mail not configured" message, and place the first test order with the owner's own address.
Backups: run `backup-db.sh production` by hand now and copy the file off the server until #196 is built.

---

## E. Rollback, abort, and what is safe to run again

| Step | Safe to re-run? | Undo / what happens |
|---|---|---|
| `preflight.sh`, `verify-public.sh`, `check-env.sh` | **Yes**, any time | Read-only |
| `install.sh` | **Yes** | Idempotent. Replaces compose files and scripts with the checkout's copy; **never** overwrites an existing `app.env` / `db.env`; never touches `data/`, `backups/` or `config/dataprotection/` contents |
| `make-dp-cert.sh` | **Refuses** if a `.pfx` or a key ring exists | Undo *before the first deploy*: `sudo rm "$APP"/config/dataprotection/dataprotection.pfx`, blank the two `SD_DP_CERT_*` lines. *After* a deploy: do not; the key ring is encrypted with it (B7) |
| `db-up.sh` | **Yes** | Starts or updates the SQL Server stack; never removes anything |
| `db-provision.sh` | **Yes** | Idempotent; re-running **rotates the two login passwords** to the ones now in `app.env`, so change them there first and redeploy |
| `build-images.sh` | **Yes** | Adds images; remove old ones by tag, never `docker system prune --volumes` |
| `deploy.sh <env> <tag>` | **Yes**, same tag changes nothing | Never `down`, never removes volumes or data. On failure it prints the migrator/API logs |
| `rollback.sh <env> [tag]` | Yes | Redeploys an earlier release with its own compose files. **The database is not rolled back** (migrations are expand-then-contract); to undo a migration restore the pre-deploy backup deliberately with the app stopped (docs/15 §7) |
| certbot issue | **Yes**, but real issuance is rate-limited | Always run `--dry-run` first. Remove a certificate: `sudo certbot delete --cert-name "$HOST"` |
| nginx site | **Yes** | Undo = `sudo rm /etc/nginx/sites-enabled/spencer-denim-<env>.conf && sudo nginx -t && sudo systemctl reload nginx`. Nothing else on the machine changes |
| Catch-all | Yes | Undo = remove its link in `sites-enabled`, `nginx -t`, reload |

**Abort UAT without destroying anything:**
```bash
cd /opt/apps/staging/spencer-denim-app/compose && sudo env SD_IMAGE_TAG=unused docker compose -p sd-staging --project-directory . -f compose.yml -f compose.staging.yml --env-file ../config/app.env stop   # literal paths; the data stays
sudo rm /etc/nginx/sites-enabled/spencer-denim-staging.conf && sudo nginx -t && sudo systemctl reload nginx
```
and remove the DNS records. Start again with `db-up.sh` then `deploy.sh` (same tag).

**Wipe UAT completely and start over: DESTRUCTIVE, UAT ONLY, NEVER `production`.** It deletes the staging database, uploads and key ring.
This block sets **its own** variables, from literals, and refuses to run for anything but staging, because the shell variables of a
session that also did Live (D1) point at production:
```bash
bash -c '
  set -euo pipefail
  APP=/opt/apps/staging/spencer-denim-app; DB=/opt/server/database/staging
  [ -d "$APP/data" ] && [ -d "$DB/data" ] || { echo "staging directories not found: stop"; exit 1; }
  (cd "$APP/compose" && sudo env SD_IMAGE_TAG=unused docker compose -p sd-staging --project-directory . -f compose.yml -f compose.staging.yml --env-file ../config/app.env down)   # no -v, ever
  (cd "$DB/compose" && sudo docker compose --env-file ../config/db.env down)
  sudo rm -rf -- "$DB"/data/* "$DB"/backups/* "$APP"/data/*
  sudo /opt/apps/src/spencer-denim-ecommerce/deploy/scripts/install.sh staging     # recreates the directories and their owners
'
```
Then B8 onwards. The `.pfx` in `config/dataprotection/` is untouched by this; after wiping `data/`, the key ring is new, so a fresh
start with the same certificate is fine.

**Stop conditions on Live:** `FAIL` in `verify-public.sh`; `deploy.sh` not ending in `production is running <tag>`; the RCSI query
not `1`; any `is missing` line on the first deploy. In each case stop, paste, and do not switch DNS.

---

## F. Execution log

Fill in as you go; one line per step actually performed. **A blank row means "not done"**, never "done".

| # | Step | Env | Who | Date / time (UTC) | Commit / tag | Result (OK / FAIL / skipped) | Pasted back? | Notes / deviation from this sheet |
|---|---|---|---|---|---|---|---|---|
| 1 | B1 preflight | staging | | | | | | |
| 2 | B2 decisions taken (A4) | staging | | | | | | |
| 3 | B3 code on server, preflight 2 | staging | | | | | | |
| 4 | B4 install.sh | staging | | | | | | |
| 5 | B5 env files, check-env | staging | | | | | | |
| 6 | B7 Data Protection certificate; copy kept off the server | staging | | | | | | |
| 7 | B8 db-up | staging | | | | | | |
| 8 | B9 db-provision; RCSI = 1 | staging | | | | | | |
| 9 | B10 build-images | staging | | | | | | |
| 10 | B11 deploy; "is missing" grep; key files encrypted | staging | | | | | | |
| 11 | B12 nginx (a–e), certificate, renew dry-run | staging | | | | | | |
| 12 | B13 basic auth (if chosen) | staging | | | | | | |
| 13 | B14 seed admin switched off, redeployed | staging | | | | | | |
| 14 | C1 verify-public (staging) | staging | | | | | | |
| 15 | C2 UAT round (S1–S24, A1–A14, E1–E8, breakpoints) | staging | | | | | | |
| 16 | C3 gate: owner's go | | | | | | | |
| 17 | D0 gate items | production | | | | | | |
| 18 | D1 install / env / certificate / db / deploy (first deploy `--skip-backup`) | production | | | | | | |
| 19 | D2 DNS cut-over, certificate, nginx | production | | | | | | |
| 20 | D2 verify-public (production) | production | | | | | | |
| 21 | D3 www redirect (if chosen) | production | | | | | | |
| 22 | D4 after go-live reads, manual backup | production | | | | | | |

Deviations from this sheet (anything done differently, with the reason; docs/15 is updated from this list, #187 §6):

| Step | What was done instead | Why |
|---|---|---|
| | | |

---

## G. What was tested, and what only the server can tell us

**Tested here, on a developer machine, against stand-ins** (all repeatable: `bash deploy/scripts/tests/run-all.sh`, see
[`deploy/scripts/tests/README.md`](../deploy/scripts/tests/README.md)):

- `verify-public.sh` against a mock shop that is right (all checks pass) and against 35 variants that are wrong in exactly one
  way (the matching check fails or warns, the exit status follows), including expired / wrong-name / self-signed certificates.
- `preflight.sh` with fake `docker`, `ss`, `ip`, `nginx`, `dig`, `curl` on its `PATH` and no `mkdir`, `rm`, `touch`, `cp` … at all:
  each `PASS`/`WARN`/`FAIL` branch fires, and the script changes nothing.
- The host-nginx templates and `verify-public.sh` against a real nginx 1.24 in a container (the version Ubuntu 24.04 ships):
  `nginx -t` ok for staging, production, the catch-all and the optional www block; a second `default_server` makes it fail, as
  stated; without the catch-all an unknown host is answered by the shop (the failure described in #187 §4), with it the TLS
  handshake is refused; staging answers `Disallow: /` and `noindex`.
- `make-dp-cert.sh` and the `check-env.sh` certificate rules; the API starting with an OpenSSL-made `.pfx`
  (`DataProtectionKeyRingTests`).
- `docker compose config` on the compose files with and without the certificate settings.

**Independent tester, round 1 (2026-10-21): FAIL on the sheet, scripts sound.** The tester ran the repo's tests (Git Bash, and an Ubuntu 26.04.1
container with uutils coreutils), read every script for destructive behaviour on a shared host (none: no prune, `volume rm`, `down -v`,
`chown -R`; names are project-scoped), mutated the preflight's stand-ins, and checked the host templates with `nginx -t` on 1.28 next
to copies of the three existing sites. Its Blocker and Majors were all about this server not being the fresh one the sheet assumed, and are
fixed above: the live "development" site on the apex (A0, §D0b, preflight WARN), the memory budget that counted a production SQL Server that does
not exist (`preflight.sh staging`), the unscoped `certbot renew --dry-run` (now `--cert-name`), the wipe/abort blocks that reused shell variables
(now literals and a guard), the catch-all advice (skip here), existing directories (preflight WARN), and the build on a shared host (B10).
Minors fixed: the `ss` expectation, the `db-up`/`db-provision` environment and name guards, B5 being staging-only, the Ubuntu 26.04 facts.
**Still open from that round:** `test-host-nginx.sh` leaves mock servers running (its cleanup trap does not see the pids); HTTP/2 is switched on for the other
sites sharing `:443` (templates would need the nginx 1.25.1+ form); `verify-public.sh` cannot tell the old stack from the new one on the apex.

**Only the VPS can show, and what to watch for on the first run:**

- the real memory and disk headroom, the real subnets, and the other sites' nginx files (`preflight.sh` reads them; nothing here could);
- that `sudo` keeps what the commands assume (`sudo env SD_IMAGE_TAG=…`), and the group `1654` reading the `.pfx` inside the container;
- the image build's memory use; SQL Server's start on Hostinger's virtual CPU (`preflight.sh` warns if the flags look wrong);
- certbot behaviour for this machine (snap vs apt; which TLS include files exist; the renewal timer);
- Let's Encrypt reaching the host over IPv4/IPv6; DNS propagation;
- that no other site depended on being nginx's implicit default server (B12-b);
- mail delivery (SPF/DKIM/DMARC) from the real account.

**Mentioned, not part of this task:** #179 (log redaction and the page-view limit; Live gate), #196 (automated backups), #200 (staff password
minimum), #84, #107, #185. The docs/15 §5 order (certbot before the site) is wrong for a new host, as B12 explains; docs/15 §4/§10 #8 still describes the certificate as
"supported but optional": both are for the owner to fold in from §F's deviation list.
