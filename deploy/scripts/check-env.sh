#!/usr/bin/env bash
# Usage: check-env.sh <production|staging>
# Validates both environment files for <env> without printing any secret (W0-15, docs/15 §4).
source "$(dirname "$(readlink -f "$0")")/lib.sh"

env="${1:-}"; require_env "$env"
app_env="$(app_root "$env")/config/app.env"
db_env="$(db_root "$env")/config/db.env"

check_env_file "$db_env" db
check_env_file "$app_env" app

app_db="$(conn_part "$(env_value "$app_env" SD_APP_DB_CONNECTION)" Database)"
mig_db="$(conn_part "$(env_value "$app_env" SD_MIGRATOR_DB_CONNECTION)" Database)"
[ "$app_db" = "$mig_db" ] || die "the API ($app_db) and the migrator ($mig_db) connection strings name different databases"
[ "$(env_value "$db_env" SD_ENV)" = "$env" ] || die "$db_env: SD_ENV must be '$env'"

log "$env: config/app.env and config/db.env look complete (database $app_db)."
