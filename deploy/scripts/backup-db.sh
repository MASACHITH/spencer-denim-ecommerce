#!/usr/bin/env bash
# Usage: sudo backup-db.sh <production|staging> [label]
#
# A one-off COPY_ONLY full backup of the <env> database into /opt/server/database/<env>/backups/, e.g. before a
# production migration (deploy.sh calls this). COPY_ONLY, so it never disturbs the scheduled backup chain
# (doc 08 §8.5), which is separate work. (W0-15, docs/15 §7.)
source "$(dirname "$(readlink -f "$0")")/lib.sh"

env="${1:-}"; require_env "$env"
label="${2:-manual}"
case "$label" in *[!A-Za-z0-9_.-]*) die "label may only contain letters, digits, . _ and -" ;; esac

db_name="$(conn_part "$(env_value "$(app_root "$env")/config/app.env" SD_APP_DB_CONNECTION)" Database)"
[ -n "$db_name" ] || die "no Database in SD_APP_DB_CONNECTION"
case "$db_name" in *[!A-Za-z0-9_]*) die "unexpected database name '$db_name'" ;; esac
file="${db_name}_${label}_$(date -u +%Y%m%dT%H%M%SZ).bak"

log "Backing up [$db_name] to backups/$file ..."
sqlcmd_stdin "$env" master <<SQL
BACKUP DATABASE [$db_name] TO DISK = N'/var/opt/mssql/backup/$file' WITH COPY_ONLY, CHECKSUM, INIT, STATS = 25;
GO
RESTORE VERIFYONLY FROM DISK = N'/var/opt/mssql/backup/$file' WITH CHECKSUM;
GO
SQL
log "Backup written and verified: $(db_root "$env")/backups/$file"
