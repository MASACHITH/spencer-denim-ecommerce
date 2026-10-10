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
install -d -m 700 -o "$SD_API_UID" -g "$SD_API_UID" \
  "$app/data/dataprotection-keys" "$app/data/media-private" "$app/data/upload-spool"
install -d -m 755 -o "$SD_API_UID" -g "$SD_API_UID" "$app/data/media-public"

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
log "Smoke test through 127.0.0.1:$port ..."
for path in /gateway-health /health /admin/ /; do
  code="$(curl -s -o /dev/null -w '%{http_code}' -H "X-Forwarded-Proto: https" "http://127.0.0.1:$port$path" || true)"
  [ "$code" = 200 ] || die "GET $path answered $code, expected 200"
done
code="$(curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:$port/api/catalog/departments" || true)"
[ "$code" = 200 ] || die "GET /api/catalog/departments answered $code, expected 200"

# The home page must be server-rendered, not Angular's empty client-side fallback (docs/15 §3 step 5).
curl -s "http://127.0.0.1:$port/" | grep -q 'ng-server-context="ssr"' \
  || die "GET / returned the client-side shell, not a server-rendered page (see docs/15 §3, NG_TRUST_PROXY_HEADERS)"

# #86: robots.txt must advertise this environment's own sitemap, never production's on staging.
origin="$(env_value "$app/config/app.env" SD_PUBLIC_ORIGIN)"
sitemap_line="$(curl -s "http://127.0.0.1:$port/robots.txt" | tr -d '\r' | sed -n 's/^Sitemap: //p')"
[ "$sitemap_line" = "$origin/sitemap.xml" ] \
  || die "robots.txt names '$sitemap_line' as the sitemap, expected '$origin/sitemap.xml' (check SD_PUBLIC_ORIGIN)"

printf '%s %s %s %s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$env" "$tag" "${SUDO_USER:-$(id -un)}" >> "$app/data/deploy-history.log"
app_compose "$env" "$compose_dir" ps
log "$env is running $tag."
