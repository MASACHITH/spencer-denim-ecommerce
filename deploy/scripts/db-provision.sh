#!/usr/bin/env bash
# Usage: sudo db-provision.sh <production|staging>
#
# Creates the <env> database with the mandated collation (tools/db/provision-database.sql, doc 08 N12) and the
# API and migrator logins (deploy/database/sql/logins.sql, doc 08 §10.4), using the database name, user names and
# passwords in config/app.env — so there is one place those live. Idempotent: safe to re-run, and re-running it is
# how a login password is rotated. Passwords reach sqlcmd on stdin, never on a command line, and are never printed.
# (W0-15, docs/15 §5.)
source "$(dirname "$(readlink -f "$0")")/lib.sh"

env="${1:-}"; require_env "$env"
check_env_file "$(db_root "$env")/config/db.env" db
# A db.env copied from the other environment would start the wrong project, network and data mounts (#195 tester).
[ "$(env_value "$(db_root "$env")/config/db.env" SD_ENV)" = "$env" ]   || die "$(db_root "$env")/config/db.env says SD_ENV=$(env_value "$(db_root "$env")/config/db.env" SD_ENV), not $env: it belongs to the other environment"
check_env_file "$(app_root "$env")/config/app.env" app

app_env="$(app_root "$env")/config/app.env"
sql_dir="$(db_root "$env")/scripts"
app_cs="$(env_value "$app_env" SD_APP_DB_CONNECTION)"
mig_cs="$(env_value "$app_env" SD_MIGRATOR_DB_CONNECTION)"
db_name="$(conn_part "$app_cs" Database)"
app_login="$(conn_part "$app_cs" 'User Id')"
mig_login="$(conn_part "$mig_cs" 'User Id')"

[ -n "$db_name" ] && [ -n "$app_login" ] && [ -n "$mig_login" ] \
  || die "could not read Database / User Id from the connection strings in $app_env"
case "$db_name$app_login$mig_login" in
  *[!A-Za-z0-9_]*) die "database and login names may only contain letters, digits and _" ;;
esac
# Names are checked by shape as well as by character set: this script runs as SQL Server's administrator inside the
# environment's own SQL container, and ALTER LOGIN would rewrite ANY login it is handed (sa included).
case "$db_name" in
  SpencerDenim|SpencerDenim_*) ;;
  *) die "database name '$db_name' must be SpencerDenim or start with SpencerDenim_ (never master, model, msdb, tempdb or another project's database)" ;;
esac
for login in "$app_login" "$mig_login"; do
  case "$login" in
    "sd_${env}_"*) ;;
    *) die "login name '$login' must start with sd_${env}_ (this script rewrites a login's password: it must never be handed sa or another application's login)" ;;
  esac
done

log "Creating database [$db_name] if missing ($env)..."
{ printf ':setvar DbName "%s"\n' "$db_name"; cat "$sql_dir/provision-database.sql"; } | sqlcmd_stdin "$env" master

log "Creating or updating logins [$app_login] and [$mig_login]..."
{
  printf ':setvar DbName "%s"\n' "$db_name"
  printf ':setvar AppLogin "%s"\n' "$app_login"
  printf ':setvar AppPassword "%s"\n' "$(conn_part "$app_cs" Password)"
  printf ':setvar MigratorLogin "%s"\n' "$mig_login"
  printf ':setvar MigratorPassword "%s"\n' "$(conn_part "$mig_cs" Password)"
  cat "$sql_dir/logins.sql"
} | sqlcmd_stdin "$env" master

log "Done."
