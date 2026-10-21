#!/usr/bin/env bash
# Usage: sudo db-up.sh <production|staging>
# Starts (or updates, if the pinned image changed) the SQL Server stack for <env> and waits until it accepts a
# login. Never removes anything. (W0-15, docs/15 §5.)
source "$(dirname "$(readlink -f "$0")")/lib.sh"

env="${1:-}"; require_env "$env"
check_env_file "$(db_root "$env")/config/db.env" db
# A db.env copied from the other environment would start the wrong project, network and data mounts (#195 tester).
[ "$(env_value "$(db_root "$env")/config/db.env" SD_ENV)" = "$env" ]   || die "$(db_root "$env")/config/db.env says SD_ENV=$(env_value "$(db_root "$env")/config/db.env" SD_ENV), not $env: it belongs to the other environment"

log "Starting SQL Server ($env)..."
db_compose "$env" up -d --wait --wait-timeout 300
db_compose "$env" ps
