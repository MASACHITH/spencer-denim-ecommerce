#!/usr/bin/env bash
# Spencer Denim deployment helpers (W0-15, docs/15-deployment-runbook.md). Sourced by the other scripts.
#
# Server layout (docs/15 §2), overridable for a dry run on another machine:
#   SD_APPS_BASE  default /opt/apps              -> $SD_APPS_BASE/<env>/spencer-denim-app/{compose,config,data,scripts}
#   SD_DB_BASE    default /opt/server/database   -> $SD_DB_BASE/<env>/{compose,config,data,backups,scripts}

set -Eeuo pipefail

SD_APPS_BASE="${SD_APPS_BASE:-/opt/apps}"
SD_DB_BASE="${SD_DB_BASE:-/opt/server/database}"

# The fewest characters any password may have: PasswordPolicy.MinimumLength in SpencerDenim.Domain (issue #200).
# A shell script cannot read that constant, so this is a copy, and PasswordHintConventionTests fails the build
# if the two differ. It is used for SD_SEED_ADMIN_PASSWORD below, the one password an operator types in here.
SD_PASSWORD_MIN_LENGTH=10

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

# owned_dir <mode> <owner:group> <dir>...: create the directories with the mode, then give them the owner.
# Not `install -d -o <uid> -g <gid>`: Ubuntu 26.04's default coreutils (uutils) refuses a numeric id that has no entry in
# /etc/passwd or /etc/group ("install: invalid group: '1654'"), and the containers' users (1654, 10001) have none on the
# host. GNU install took them. chown takes a numeric id in both, so the owner is set in a second step.
owned_dir() {
  local mode="$1" owner="$2"
  shift 2
  install -d -m "$mode" "$@"
  chown "$owner" "$@"
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

# check_dp_cert <app.env>: the optional Data Protection certificate (#195, docs/16, make-dp-cert.sh).
#
# Two variables belong together: SD_DP_CERT_FILE (a file name inside config/dataprotection/) and
# SD_DP_CERT_PASSWORD. Both empty is today's behaviour (the key ring is stored unencrypted) and needs no checking.
# Anything else is checked here, because the API's own failure for a wrong setting is "the API does not start"
# (DataProtectionSetup throws), and for a *missing* one is "every admin is signed out after a restore".
# Warns once per problem and returns 1 if there was any; never prints the password.
check_dp_cert() {
  local file="$1" cert pw dir path mode problems=0
  cert="$(env_value "$file" SD_DP_CERT_FILE)"
  pw="$(env_value "$file" SD_DP_CERT_PASSWORD)"
  { [ -n "$cert" ] || [ -n "$pw" ]; } || return 0

  if [ -z "$cert" ]; then
    warn "$file: SD_DP_CERT_PASSWORD is set but SD_DP_CERT_FILE is empty, so no certificate is used and the key ring stays unencrypted. Set both (make-dp-cert.sh prints the lines) or neither"
    return 1
  fi
  if [ -z "$pw" ]; then
    warn "$file: SD_DP_CERT_FILE is set but SD_DP_CERT_PASSWORD is empty. The API would try to open the certificate with no password and refuse to start"
    problems=1
  fi
  case "$cert" in
    .*|*[!A-Za-z0-9._-]*)
      warn "$file: SD_DP_CERT_FILE must be a plain file name inside config/dataprotection/ (letters, digits, . _ -), not a path"
      return 1 ;;
  esac

  dir="$(dirname "$file")/dataprotection"
  path="$dir/$cert"
  if [ ! -f "$path" ]; then
    warn "$file: SD_DP_CERT_FILE names $cert, but $path does not exist (run make-dp-cert.sh, or restore the .pfx you kept off the server)"
    return 1
  fi

  mode="$(stat -c '%a' "$path" 2>/dev/null || echo unknown)"
  # 640 or stricter: any permission bit outside rw-r----- is refused (group write, anything for other users, execute,
  # setuid/setgid/sticky). 600, 440 and 400 pass; 660, 644, 604, 700 do not.
  case "$mode" in
    unknown) ;;
    *[!0-7]*|'') warn "$path: could not read its mode ('$mode')"; problems=1 ;;
    *)
      if [ $(( 8#$mode & ~8#640 )) -ne 0 ]; then
        warn "$path is mode $mode, looser than 640 (the API's group can write it, or other users can read it). chmod 640, owner root, group 1654"
        problems=1
      fi ;;
  esac
  if [ "$(stat -c '%U' "$path" 2>/dev/null || echo root)" != root ]; then
    warn "$path is not owned by root (chown root:1654 $path)"
    problems=1
  fi

  if command -v openssl >/dev/null 2>&1 && [ -n "$pw" ]; then
    # The password goes in through the environment, never on a command line. -legacy is for a .pfx that did not
    # come from make-dp-cert.sh (3DES/RC2) on OpenSSL 3; OpenSSL 1.1 rejects the flag, and then both attempts fail.
    local info
    if info="$(SD_DP_PW_CHECK="$pw" openssl pkcs12 -in "$path" -passin env:SD_DP_PW_CHECK -info -noout 2>&1)" \
       || info="$(SD_DP_PW_CHECK="$pw" openssl pkcs12 -legacy -in "$path" -passin env:SD_DP_PW_CHECK -info -noout 2>&1)"; then
      case "$info" in
        *[Kk]eybag*|*"Key bag"*) ;;
        *) warn "$path opens, but openssl found no private key in it; the API refuses a certificate without one"
           problems=1 ;;
      esac
    else
      warn "openssl could not open $path with SD_DP_CERT_PASSWORD (wrong password, or not a PKCS#12 file). The API would refuse to start"
      problems=1
    fi
    unset info
  fi

  [ "$problems" -eq 0 ]
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
    # SD_PUBLIC_ORIGIN's host must be one the SSR engine will render for.
    #
    # NG_ALLOWED_HOSTS comes from SD_PUBLIC_HOSTS, and @angular/ssr validates the request's Host and
    # X-Forwarded-Host against it; a host it does not allow is answered 400 Bad Request, not degraded. If the
    # two disagree, every page on the site answers 400 while robots.txt and sitemap.xml — which take their
    # origin from PUBLIC_SITE_ORIGIN and never touch the engine — keep answering correctly. Two variables
    # that must agree, a total outage when they do not, and a check that passed either way.
    #
    # Compared without the port: the engine strips it before deciding (origins.ts allowedHost, and
    # Angular's own isHostAllowed compares URL.hostname).
    # Compared in the shell rather than with grep: "grep -F -i" dies with SIGABRT on GNU grep 3.0 as shipped
    # in Git for Windows, which is where these scripts get read and tried out even though they only ever run
    # on the server.
    local origin_host listed_host public_origin_matched=0
    origin_host="$(env_value "$file" SD_PUBLIC_ORIGIN | sed -e 's#^https\?://##' -e 's#:[0-9]*$##')"
    origin_host="$(printf '%s' "$origin_host" | tr '[:upper:]' '[:lower:]')"
    if [ -n "$origin_host" ]; then
      while IFS= read -r listed_host; do
        listed_host="$(printf '%s' "$listed_host" | tr -d '[:space:]' | tr '[:upper:]' '[:lower:]')"
        listed_host="${listed_host%%:*}"
        [ "$listed_host" = "$origin_host" ] && public_origin_matched=1
      done <<EOF_HOSTS
$(env_value "$file" SD_PUBLIC_HOSTS | tr ',' '\n')
EOF_HOSTS
      if [ "$public_origin_matched" -eq 0 ]; then
        warn "$file: SD_PUBLIC_ORIGIN names host '$origin_host', which is not listed in SD_PUBLIC_HOSTS. The SSR engine renders only for SD_PUBLIC_HOSTS and answers 400 for any other host, so every page would fail while robots.txt and sitemap.xml kept working"
        problems=1
      fi
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
    check_dp_cert "$file" || problems=1

    # First Super Admin (docs/15 §4). The API holds this password to the same rule as every other account: one
    # shorter than SD_PASSWORD_MIN_LENGTH creates no account, logs an error and starts anyway, so a first install
    # would come up with no way into the admin panel. Refused here instead, before anything is deployed. Only
    # when the seed is switched on; the value itself is never printed.
    local seed_enabled
    seed_enabled="$(env_value "$file" SD_SEED_ADMIN_ENABLED | tr '[:upper:]' '[:lower:]')"
    if [ "$seed_enabled" = true ]; then
      pw="$(env_value "$file" SD_SEED_ADMIN_PASSWORD)"
      if [ "${#pw}" -lt "$SD_PASSWORD_MIN_LENGTH" ]; then
        warn "$file: SD_SEED_ADMIN_ENABLED is true but SD_SEED_ADMIN_PASSWORD is shorter than $SD_PASSWORD_MIN_LENGTH characters, so the first Super Admin would not be created. Use at least $SD_PASSWORD_MIN_LENGTH characters with an upper-case letter, a lower-case letter, a number and a symbol; or, if that account already exists, set SD_SEED_ADMIN_ENABLED=false and blank the password"
        problems=1
      fi
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
