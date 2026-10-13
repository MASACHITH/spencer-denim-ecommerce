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
  # The history read as a stack: a deploy pushes its tag, a rollback to T pops back down to T. The release below
  # the top is the one to return to, so rolling back twice goes back twice rather than forward again to the
  # release that was rolled back from.
  tag="$(awk '
    $5 == "rollback" { while (n > 0 && s[n] != $3) n--; if (n == 0) s[++n] = $3; next }
    { if (n == 0 || s[n] != $3) s[++n] = $3 }
    END { if (n >= 2) print s[n-1] }' "$history")"
  [ -n "$tag" ] || die "no earlier release than $current in $history"
  log "Current release is $current; rolling back to $tag"
fi
case "$tag" in *[!A-Za-z0-9_.-]*) die "invalid tag '$tag'" ;; esac

release_dir="$app/compose/releases/$tag"
[ -d "$release_dir" ] || die "no compose files for $tag in $release_dir"

SD_DEPLOY_KIND=rollback SD_RELEASE_COMPOSE_DIR="$release_dir" "$(dirname "$(readlink -f "$0")")/deploy.sh" "$env" "$tag" --skip-backup
