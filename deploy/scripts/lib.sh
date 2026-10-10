#!/usr/bin/env bash
# Spencer Denim deployment helpers (W0-15, docs/15-deployment-runbook.md). Sourced by the other scripts.
#
# Server layout (docs/15 §2), overridable for a dry run on another machine:
#   SD_APPS_BASE  default /opt/apps              -> $SD_APPS_BASE/<env>/spencer-denim-app/{compose,config,data,scripts}
#   SD_DB_BASE    default /opt/server/database   -> $SD_DB_BASE/<env>/{compose,config,data,backups,scripts}

set -Eeuo pipefail

SD_APPS_BASE="${SD_APPS_BASE:-/opt/apps}"
SD_DB_BASE="${SD_DB_BASE:-/opt/server/database}"

# Image users that must own their writable bind mounts.
SD_API_UID=1654      # "app" in the mcr.microsoft.com/dotnet images
SD_MSSQL_UID=10001   # "mssql" in the mcr.microsoft.com/mssql/server image

log()  { printf '[%s] %s\n' "$(date -u +%H:%M:%SZ)" "$*"; }
warn() { printf '[%s] WARNING: %s\n' "$(date -u +%H:%M:%SZ)" "$*" >&2; }
die()  { printf '[%s] ERROR: %s\n' "$(date -u +%H:%M:%SZ)" "$*" >&2; exit 1; }

# require_env <env>: only the two real environments are deployable with these scripts.
require_env() {
  case "${1:-}" in
    production|staging) ;;
    *) die "environment must be 'production' or 'staging' (got '${1:-}')" ;;
  esac
}

app_root() { printf '%s/%s/spencer-denim-app' "$SD_APPS_BASE" "$1"; }
db_root()  { printf '%s/%s' "$SD_DB_BASE" "$1"; }

# Published loopback port per environment (must match compose.<env>.yml).
http_port() {
  case "$1" in
    production) echo 38080 ;;
    staging)    echo 38081 ;;
  esac
}

# env_value <file> <KEY>: the raw value of KEY in a compose-style env file (no quote processing, like compose).
env_value() {
  local file="$1" key="$2"
  sed -n "s/^${key}=//p" "$file" | tail -n 1
}

# conn_part <connection string> <part name>: e.g. conn_part "$cs" "User Id".
conn_part() {
  printf '%s' "$1" | tr ';' '\n' | sed -n "s/^[[:space:]]*$2[[:space:]]*=//Ip" | head -n 1
}

# has_unsafe_password_chars <value>: true if it contains ' " ] \ ; or $.
has_unsafe_password_chars() {
  case "$1" in
    *[\'\"\]\\\;\$]*) return 0 ;;
  esac
  return 1
}

# check_env_file <file> <kind: app|db>: refuse placeholders, quoting and characters that break interpolation.
check_env_file() {
  local file="$1" kind="$2" problems=0
  [ -f "$file" ] || die "$file does not exist (copy it from the .env.example template and fill it in)"

  local mode
  mode="$(stat -c '%a' "$file" 2>/dev/null || echo unknown)"
  if [ "$mode" != "600" ] && [ "$mode" != "400" ] && [ "$mode" != "unknown" ]; then
    warn "$file is mode $mode; it holds secrets and should be 600 (chmod 600 $file)"
  fi

  # Values only: the templates' own comments mention CHANGE_ME, and a filled-in file keeps those comments.
  if grep -E '^[A-Za-z_][A-Za-z0-9_]*=' "$file" | grep -q 'CHANGE_ME'; then
    warn "$file still contains CHANGE_ME placeholders:"
    grep -nE '^[A-Za-z_][A-Za-z0-9_]*=.*CHANGE_ME' "$file" | sed 's/=.*/=…/' >&2
    problems=1
  fi

  # Values are read literally by compose; quotes would become part of the password, and $ would be interpolated.
  if grep -E '^[A-Za-z_][A-Za-z0-9_]*=' "$file" | grep -q -e '="' -e "='" -e '[$]'; then
    warn "$file has a value that starts with a quote or contains a dollar sign; compose would not read it as intended"
    problems=1
  fi

  # Compose strips a carriage return and cuts an unquoted value at " #", while these scripts read the raw line,
  # so a CRLF file or a password containing " #" provisions one password in SQL Server and hands the API another.
  if grep -q "$(printf '\r')" "$file"; then
    warn "$file has Windows (CRLF) line endings; convert it with: sed -i 's/\r$//' $file"
    problems=1
  fi
  if grep -E '^[A-Za-z_][A-Za-z0-9_]*=' "$file" | grep -q ' #'; then
    warn "$file has a value containing ' #'; compose treats the rest of that line as a comment"
    problems=1
  fi

  if [ "$kind" = app ]; then
    local key cs pw
    for key in SD_APP_DB_CONNECTION SD_MIGRATOR_DB_CONNECTION SD_PUBLIC_HOSTS SD_PUBLIC_ORIGIN; do
      [ -n "$(env_value "$file" "$key")" ] || { warn "$file: $key is empty"; problems=1; }
    done
    # The storefront ignores a value with a path, query, fragment, credentials or no host, and falls back to
    # production's domain (#86), so anything but scheme://host[:port] is refused here instead of being published.
    if ! printf '%s' "$(env_value "$file" SD_PUBLIC_ORIGIN)" \
        | grep -Eq '^https?://[A-Za-z0-9]([A-Za-z0-9.-]*[A-Za-z0-9])?(:[0-9]{1,5})?$'; then
      warn "$file: SD_PUBLIC_ORIGIN must be scheme and host only (http:// or https://, optional :port), e.g. https://colombowardrobecompany.com"
      problems=1
    fi
    for key in SD_APP_DB_CONNECTION SD_MIGRATOR_DB_CONNECTION; do
      cs="$(env_value "$file" "$key")"
      pw="$(conn_part "$cs" 'Password')"
      # Refused because db-provision.sh embeds the password in T-SQL, and ; ends a connection-string part.
      if has_unsafe_password_chars "$pw"; then
        warn "$file: the $key password contains one of ' \" ] \\ ; \$ — choose one without them"
        problems=1
      fi
    done
    if [ "$(conn_part "$(env_value "$file" SD_APP_DB_CONNECTION)" 'User Id')" = \
         "$(conn_part "$(env_value "$file" SD_MIGRATOR_DB_CONNECTION)" 'User Id')" ]; then
      warn "$file: the API and the migrator must use different logins (doc 08 §10.4)"
      problems=1
    fi
  fi

  if [ "$kind" = db ]; then
    [ -n "$(env_value "$file" SD_MSSQL_SA_PASSWORD)" ] || { warn "$file: SD_MSSQL_SA_PASSWORD is empty"; problems=1; }
  fi

  [ "$problems" -eq 0 ] || die "fix $file first"
}

# app_compose <env> <compose dir> <args...>: docker compose for the application stack of <env>.
app_compose() {
  local env="$1" dir="$2"; shift 2
  local root; root="$(app_root "$env")"
  docker compose \
    --project-directory "$root/compose" \
    -f "$dir/compose.yml" -f "$dir/compose.$env.yml" \
    --env-file "$root/config/app.env" \
    "$@"
}

# db_compose <env> <args...>: docker compose for the database stack of <env>.
db_compose() {
  local env="$1"; shift
  local root; root="$(db_root "$env")"
  docker compose \
    --project-directory "$root/compose" \
    -f "$root/compose/compose.yml" \
    --env-file "$root/config/db.env" \
    "$@"
}

# sqlcmd_stdin <env> <database>: run the T-SQL on stdin as sa inside the SQL Server container. The password goes in
# through the environment (SQLCMDPASSWORD), never on a command line.
sqlcmd_stdin() {
  local env="$1" database="$2"
  local sa_password; sa_password="$(env_value "$(db_root "$env")/config/db.env" SD_MSSQL_SA_PASSWORD)"
  SQLCMDPASSWORD="$sa_password" db_compose "$env" exec -T -e SQLCMDPASSWORD sqlserver \
    /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -b -d "$database" -I
}
