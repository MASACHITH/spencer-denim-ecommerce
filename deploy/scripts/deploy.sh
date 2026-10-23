#!/usr/bin/env bash
# Usage: sudo deploy.sh <production|staging> <tag> [--skip-backup]
#
# Deploys already-built images spencer-denim/*:<tag> to <env> (W0-15, docs/15 §6):
#
#   1. checks the env files and that all four images exist
#   2. production only: a verified COPY_ONLY backup of the database first (skip only with --skip-backup)
#   3. snapshots the compose files as compose/releases/<tag>/, so a rollback uses the files that went with <tag>
#   4. docker compose up -d --wait: the migrator runs to completion first (the API waits on it), then the API
#      (healthy = can reach SQL Server), the storefront and finally the gateway
#   5. smoke-tests through the published loopback port and records the release in data/deploy-history.log
#
# Idempotent: re-running with the same tag changes nothing that is already up to date. It never removes volumes,
# data or backups, and never runs `down`.
source "$(dirname "$(readlink -f "$0")")/lib.sh"

env="${1:-}"; require_env "$env"
tag="${2:-}"; case "$tag" in *[!A-Za-z0-9_.-]*|'') die "usage: deploy.sh <env> <tag> [--skip-backup]" ;; esac
skip_backup=0
[ "${3:-}" = "--skip-backup" ] && skip_backup=1
# rollback.sh passes this so the release's own compose files are used rather than the current ones.
compose_dir="${SD_RELEASE_COMPOSE_DIR:-}"

app="$(app_root "$env")"
"$(dirname "$(readlink -f "$0")")/check-env.sh" "$env"

repository="$(env_value "$app/config/app.env" SD_IMAGE_REPOSITORY)"
repository="${repository:-spencer-denim}"
for image in api migrator storefront web; do
  docker image inspect "$repository/$image:$tag" >/dev/null 2>&1 \
    || die "image $repository/$image:$tag is not on this machine (build it with deploy/scripts/build-images.sh)"
done

docker network inspect "sd-$env-db" >/dev/null 2>&1 \
  || die "network sd-$env-db does not exist: start the database first ($app/scripts/db-up.sh $env)"

# Directories the containers write to must exist with the right owner, or Docker creates them as root.
owned_dir 700 "$SD_API_UID:$SD_API_UID" \
  "$app/data/dataprotection-keys" "$app/data/media-private" "$app/data/upload-spool"
owned_dir 755 "$SD_API_UID:$SD_API_UID" "$app/data/media-public"

if [ "$env" = production ] && [ "$skip_backup" -eq 0 ]; then
  "$(dirname "$(readlink -f "$0")")/backup-db.sh" "$env" "pre-deploy-$tag"
fi

if [ -z "$compose_dir" ]; then
  compose_dir="$app/compose/releases/$tag"
  install -d -m 755 "$compose_dir"
  install -m 644 "$app/compose/compose.yml" "$app/compose/compose.$env.yml" "$compose_dir/"
fi
[ -f "$compose_dir/compose.yml" ] && [ -f "$compose_dir/compose.$env.yml" ] || die "no compose files in $compose_dir"

log "Deploying $env at $tag ..."
export SD_IMAGE_TAG="$tag"
if ! app_compose "$env" "$compose_dir" up -d --wait --wait-timeout 300 --remove-orphans; then
  app_compose "$env" "$compose_dir" ps -a || true
  app_compose "$env" "$compose_dir" logs --tail 80 migrator api || true
  die "deployment of $tag did not become healthy (see the logs above; roll back with rollback.sh $env)"
fi

port="$(http_port "$env")"

# Every smoke request carries the PUBLIC host name, because 127.0.0.1 is the one host that proves nothing.
#
# The gateway sends the storefront "Host: $host" and "X-Forwarded-Host: $host", and Angular's SSR engine
# validates both against its allowed-host set. That set is NG_ALLOWED_HOSTS (= SD_PUBLIC_HOSTS) **unioned with
# the build manifest's own ["localhost", "127.0.0.1"]** (AngularAppEngine.getAllowedHosts), so loopback is
# always allowed however SD_PUBLIC_HOSTS is set, while any other unlisted host is answered 400 Bad Request.
#
# Measured on the built storefront with NG_ALLOWED_HOSTS=shop.example.test:
#     Host: shop.example.test        -> 200 server-rendered
#     Host: 127.0.0.1:<port>         -> 200 server-rendered   <- what this smoke test used to assert
#     Host: localhost:<port>         -> 200 server-rendered
#     Host: evil.example.com         -> 400 Bad Request
#     Host: staging.shop.example.test-> 400 Bad Request
#
# So addressing the loopback port told us only that loopback works. A wrong SD_PUBLIC_HOSTS — a typo, or a
# staging value left in a production file — answers **400 to every real visitor** while this smoke test still
# passed and deploy.sh still printed "<env> is running <tag>". Asking for the public host is what makes the
# check cover the path visitors actually take. check-env.sh now also refuses the mismatch up front.
#
# The first of SD_PUBLIC_HOSTS is used, so this stays correct when more than one host is served.
public_host="$(env_value "$app/config/app.env" SD_PUBLIC_HOSTS | cut -d, -f1 | tr -d '[:space:]')"
[ -n "$public_host" ] || die "SD_PUBLIC_HOSTS is empty in $app/config/app.env"

log "Smoke test through 127.0.0.1:$port as $public_host ..."
for path in /gateway-health /health /admin/ /; do
  code="$(curl -s -o /dev/null -w '%{http_code}' \
    -H "Host: $public_host" -H "X-Forwarded-Proto: https" "http://127.0.0.1:$port$path" || true)"
  [ "$code" = 200 ] || die "GET $path answered $code, expected 200"
done
code="$(curl -s -o /dev/null -w '%{http_code}' -H "Host: $public_host" \
  "http://127.0.0.1:$port/api/catalog/departments" || true)"
[ "$code" = 200 ] || die "GET /api/catalog/departments answered $code, expected 200"

# The home page must be server-rendered, not Angular's empty client-side fallback (docs/15 §3 step 5).
# Each response is read into a variable before it is searched: piping curl into "grep -q" under pipefail fails
# whenever grep stops reading before curl has finished writing (curl exits 23), about one deploy in ten.
home="$(curl -s -H "Host: $public_host" -H "X-Forwarded-Proto: https" "http://127.0.0.1:$port/" || true)"
case "$home" in
  *'ng-server-context="ssr"'*) ;;
  *) die "GET / was not server-rendered. Check NG_TRUST_PROXY_HEADERS against the gateway's 'location /' block, and SD_PUBLIC_HOSTS against the Host above (docs/15 §3 step 5)" ;;
esac

# #86: robots.txt must advertise this environment's own sitemap, never production's on staging.
origin="$(env_value "$app/config/app.env" SD_PUBLIC_ORIGIN)"
robots="$(curl -s "http://127.0.0.1:$port/robots.txt" || true)"
sitemap_line="$(printf '%s\n' "$robots" | tr -d '\r' | sed -n 's/^Sitemap: //p')"
[ "$sitemap_line" = "$origin/sitemap.xml" ] \
  || die "robots.txt names '$sitemap_line' as the sitemap, expected '$origin/sitemap.xml' (check SD_PUBLIC_ORIGIN)"

# Fifth field: "rollback" when rollback.sh asked for this deploy, so the next rollback can tell a release that
# was rolled back from (never returned to by default) from an ordinary earlier one.
printf '%s %s %s %s %s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$env" "$tag" "${SUDO_USER:-$(id -un)}" \
  "${SD_DEPLOY_KIND:-deploy}" >> "$app/data/deploy-history.log"
app_compose "$env" "$compose_dir" ps
log "$env is running $tag."
