#!/usr/bin/env bash
# Usage: sudo deploy/scripts/install.sh <production|staging>
#
# Run from a checkout of the repository (the one the images are built from). Creates or refreshes the server
# layout for <env> (docs/15 §2):
#
#   /opt/apps/<env>/spencer-denim-app/{compose,config,config/dataprotection,data,scripts}
#   /opt/server/database/<env>/{compose,config,data,backups,scripts}
#
# Idempotent. Compose files and scripts are replaced with this checkout's copies; config/*.env files are created
# from the templates only when missing and are NEVER overwritten; data and backups are never touched beyond
# creating the directories and setting their owners.
source "$(dirname "$(readlink -f "$0")")/lib.sh"

env="${1:-}"; require_env "$env"
repo="$(cd "$(dirname "$(readlink -f "$0")")/../.." && pwd)"
[ -f "$repo/deploy/app/compose.yml" ] || die "run this from a repository checkout ($repo has no deploy/app/compose.yml)"

app="$(app_root "$env")"
db="$(db_root "$env")"

log "Installing $env from $repo"
install -d -m 755 "$app" "$app/compose" "$app/scripts" "$app/data"
install -d -m 700 "$app/config"
# Where scripts/make-dp-cert.sh puts the optional Data Protection certificate (#195): root-owned, readable by the
# API's group only, mounted read-only into the API container. Empty until a certificate is made; never cleaned here.
owned_dir 750 "root:$SD_API_UID" "$app/config/dataprotection"
# Written by the API (uid 1654) only: the key ring, the uploads and the upload spool.
owned_dir 700 "$SD_API_UID:$SD_API_UID" \
  "$app/data/dataprotection-keys" "$app/data/media-private" "$app/data/upload-spool"
owned_dir 755 "$SD_API_UID:$SD_API_UID" "$app/data/media-public"

install -d -m 755 "$db" "$db/compose" "$db/scripts"
install -d -m 700 "$db/config"
owned_dir 770 "$SD_MSSQL_UID:0" "$db/data" "$db/backups"

# Application stack files.
install -m 644 "$repo/deploy/app/compose.yml" "$repo/deploy/app/compose.$env.yml" "$app/compose/"
install -m 755 "$repo"/deploy/scripts/*.sh "$app/scripts/"

# Database stack files and provisioning SQL.
install -m 644 "$repo/deploy/database/compose.yml" "$db/compose/"
install -m 644 "$repo/tools/db/provision-database.sql" "$repo/deploy/database/sql/logins.sql" "$db/scripts/"

if [ ! -f "$app/config/app.env" ]; then
  install -m 600 "$repo/deploy/app/.env.example" "$app/config/app.env"
  warn "created $app/config/app.env from the template: fill it in before deploying"
fi
if [ ! -f "$db/config/db.env" ]; then
  install -m 600 "$repo/deploy/database/.env.example" "$db/config/db.env"
  sed -i "s/^SD_ENV=.*/SD_ENV=$env/" "$db/config/db.env"
  warn "created $db/config/db.env from the template: fill it in before starting SQL Server"
fi
chmod 600 "$app/config/app.env" "$db/config/db.env"

log "Done. Next (docs/15 §5): fill in both env files, then $app/scripts/check-env.sh $env"
