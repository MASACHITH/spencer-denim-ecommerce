#!/usr/bin/env bash
# Usage: sudo rollback.sh <production|staging> [tag]
#
# Redeploys an earlier release: <tag>, or by default the release before the current one in
# data/deploy-history.log. It uses that release's own compose files (compose/releases/<tag>/) and images, which
# must still be on the machine (docs/15 §7 keeps the last few).
#
# The DATABASE IS NOT ROLLED BACK. Migrations are expand-then-contract (doc 08 §10.4), so the previous release runs
# against the newer schema; an older migrations bundle finds nothing to apply and exits 0. Restoring the pre-deploy
# backup is a separate, deliberate step (docs/15 §7). No backup is taken on rollback.
source "$(dirname "$(readlink -f "$0")")/lib.sh"

env="${1:-}"; require_env "$env"
app="$(app_root "$env")"
history="$app/data/deploy-history.log"

tag="${2:-}"
if [ -z "$tag" ]; then
  [ -f "$history" ] || die "no deploy history at $history; name the tag to roll back to"
  current="$(tail -n 1 "$history" | awk '{print $3}')"
  tag="$(awk -v cur="$current" '$3 != cur {t=$3} END {print t}' "$history")"
  [ -n "$tag" ] || die "no earlier release than $current in $history"
  log "Current release is $current; rolling back to $tag"
fi
case "$tag" in *[!A-Za-z0-9_.-]*) die "invalid tag '$tag'" ;; esac

release_dir="$app/compose/releases/$tag"
[ -d "$release_dir" ] || die "no compose files for $tag in $release_dir"

SD_RELEASE_COMPOSE_DIR="$release_dir" "$(dirname "$(readlink -f "$0")")/deploy.sh" "$env" "$tag" --skip-backup
