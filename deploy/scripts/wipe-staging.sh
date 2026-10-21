#!/usr/bin/env bash
# Usage: sudo deploy/scripts/wipe-staging.sh
#
# DESTRUCTIVE, UAT ONLY. Wipes the staging (UAT) environment so it can be built again from B8 of docs/16: stops both
# staging stacks, deletes EVERYTHING inside the staging database files, the staging database backups and the staging
# application data (uploads, key ring, upload spool), proves those three directories are empty, then runs
# install.sh staging to recreate the empty directories with their owners.
#
# Why a script and not a pasted block (review of #219, M1): the old block ran `sudo rm -rf "$DB"/data/* ...` from a
# non-root shell. The glob was expanded by that user, who cannot read the mode-770 database directories, so it stayed
# a literal path; rm -f on a path that does not exist exits 0, and nothing was deleted. Here the whole script runs as
# root, deletion is `find -mindepth 1 -delete` (no glob at all, dot-files included), and an emptiness check that
# fails loudly follows it.
#
# It can never touch production:
#   * it takes NO environment argument: "staging" is a literal below, and any argument at all is refused;
#   * every path it deletes in is checked to sit under .../staging/... ;
#   * the database stack is stopped with an explicit project name (sd-staging-database), and only if staging's own
#     db.env says SD_ENV=staging (so a db.env copied from production cannot make it stop production's SQL Server);
#   * a person has to type "wipe staging" at the terminal.
#
# Kept as it is: config/ (app.env, db.env) and config/dataprotection/ (the .pfx). A fresh key ring with the same
# certificate is fine. Run it from the repository checkout (install.sh needs it); an installed scripts/ copy refuses.
#
# Test-only overrides: SD_WIPE_TTY (the "terminal"), SD_WIPE_ALLOW_NONROOT=1, SD_WIPE_INSTALL (the install script).
source "$(dirname "$(readlink -f "$0")")/lib.sh"

[ $# -eq 0 ] || die "this script takes no arguments: it only ever wipes staging (got '$*'). There is no way to point it at production"
readonly ENV=staging

if [ "$(id -u)" -ne 0 ]; then
  # Not optional: as another user the database directories (mode 770, uid 10001) cannot even be listed.
  [ "${SD_WIPE_ALLOW_NONROOT:-}" = 1 ] || die "run this with sudo: the database directories are readable by root and mssql only"
  warn "not running as root (SD_WIPE_ALLOW_NONROOT=1 is for tests only)"
fi

here="$(cd "$(dirname "$(readlink -f "$0")")" && pwd)"
repo="$(cd "$here/../.." && pwd)"
install_sh="${SD_WIPE_INSTALL:-$here/install.sh}"
[ -f "$repo/deploy/app/compose.yml" ] || die "run this from the repository checkout (sudo /opt/apps/src/spencer-denim-ecommerce/deploy/scripts/wipe-staging.sh), not from an installed scripts/ copy: install.sh needs the checkout"

app="$(app_root "$ENV")"
db="$(db_root "$ENV")"
targets=("$db/data" "$db/backups" "$app/data")
for d in "${targets[@]}"; do
  case "$d" in
    */staging/*) ;;
    *) die "refusing: $d is not under a staging directory" ;;
  esac
  case "$d" in *production*) die "refusing: $d mentions production" ;; esac
  [ -d "$d" ] || die "$d does not exist: is staging installed here? Nothing was changed"
done

db_env="$db/config/db.env"
if [ -f "$db_env" ]; then
  db_sd_env="$(env_value "$db_env" SD_ENV)"
  [ "$db_sd_env" = staging ] || die "$db_env says SD_ENV=$db_sd_env, not staging: refusing to stop that database stack. Nothing was changed"
fi

# --- confirmation -------------------------------------------------------------------------------------------------
tty="${SD_WIPE_TTY:-/dev/tty}"
( : >> "$tty" ) 2>/dev/null || die "there is no terminal to confirm on (use ssh -t). Nothing was changed"
{
  printf '\n  THIS DELETES ALL STAGING (UAT) DATA, for good:\n'
  for d in "${targets[@]}"; do printf '      everything inside %s\n' "$d"; done
  printf '  config/ (app.env, db.env) and the Data Protection certificate are kept.\n'
  printf '  Production is not touched.\n\n  Type   wipe staging   to go ahead, anything else to stop: '
} >> "$tty"
answer=""
IFS= read -r answer < "$tty" || true
[ "$answer" = "wipe staging" ] || die "not confirmed (got '$answer'). Nothing was changed"

# --- stop both staging stacks (never -v) ----------------------------------------------------------------------------
if [ -f "$app/compose/compose.yml" ] && [ -f "$app/compose/compose.staging.yml" ]; then
  log "Stopping the staging application stack (sd-staging)..."
  ( cd "$app/compose" && env SD_IMAGE_TAG=unused docker compose -p sd-staging --project-directory . \
      -f compose.yml -f compose.staging.yml --env-file ../config/app.env down ) \
    || die "could not stop the staging application stack; nothing was deleted"
else
  warn "no staging application compose files in $app/compose: assuming the stack is not there"
fi
if [ -f "$db/compose/compose.yml" ] && [ -f "$db_env" ]; then
  log "Stopping the staging database stack (sd-staging-database)..."
  ( cd "$db/compose" && docker compose -p sd-staging-database --env-file ../config/db.env down ) \
    || die "could not stop the staging database stack; nothing was deleted"
else
  warn "no staging database compose file or db.env in $db: assuming SQL Server is not running for staging"
fi
for p in sd-staging sd-staging-database; do
  left="$(docker ps -q --filter "label=com.docker.compose.project=$p")" || die "could not ask docker which containers are running; nothing was deleted"
  [ -z "$left" ] || die "containers of $p are still running ($(printf '%s' "$left" | tr '\n' ' ')): nothing was deleted"
done

# --- delete, then prove it ------------------------------------------------------------------------------------------
log "Deleting everything inside: ${targets[*]}"
# -xdev: never crosses into another file system mounted inside (that would leave the directory non-empty, and the check
# below says so). find does not follow symbolic links: a link is deleted, never what it points at.
find "${targets[@]}" -xdev -mindepth 1 -delete || warn "find -delete reported errors (above); checking what is left"
remaining=0
for d in "${targets[@]}"; do
  # A find error (an unreadable directory) prints to the captured stderr, so it also counts as "not empty".
  listing="$(find "$d" -mindepth 1 2>&1 | head -n 5 || true)"
  if [ -n "$listing" ]; then
    warn "$d is NOT empty after the wipe:"; printf '%s\n' "$listing" | sed 's/^/        /' >&2
    remaining=1
  fi
done
[ "$remaining" -eq 0 ] || die "the wipe did not complete; install.sh was NOT run. Investigate before going on"
log "Verified empty: ${targets[*]}"

# --- recreate the directories ---------------------------------------------------------------------------------------
log "Recreating the staging layout (install.sh staging)..."
bash "$install_sh" staging
log "Staging wiped. Next: docs/16 B8 (db-up.sh staging) onwards."
