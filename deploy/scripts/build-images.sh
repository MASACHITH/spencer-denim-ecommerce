#!/usr/bin/env bash
# Usage: deploy/scripts/build-images.sh [--allow-dirty] [tag]
#
# Builds the four images from this checkout and tags them spencer-denim/{api,migrator,storefront,web}:<tag>.
# The tag defaults to the checked-out commit (12 characters), so an image always says which code is inside it and
# rollback.sh can go back to any earlier one still on the machine. A dirty working tree is refused unless
# --allow-dirty is given (the tag would then lie about the contents). (W0-15, docs/15 §6.)
source "$(dirname "$(readlink -f "$0")")/lib.sh"

allow_dirty=0
if [ "${1:-}" = "--allow-dirty" ]; then allow_dirty=1; shift; fi

repo="$(cd "$(dirname "$(readlink -f "$0")")/../.." && pwd)"
cd "$repo"
[ -f deploy/docker/backend.Dockerfile ] || die "run this from a repository checkout"

if [ "$allow_dirty" -eq 0 ] && [ -n "$(git status --porcelain --untracked-files=no)" ]; then
  die "the working tree has uncommitted changes; commit them or pass --allow-dirty"
fi

tag="${1:-$(git rev-parse --short=12 HEAD)}"
case "$tag" in *[!A-Za-z0-9_.-]*|'') die "invalid tag '$tag'" ;; esac
repository="${SD_IMAGE_REPOSITORY:-spencer-denim}"
revision="$(git rev-parse HEAD)"
created="$(date -u +%Y-%m-%dT%H:%M:%SZ)"

export DOCKER_BUILDKIT=1
build() {
  local dockerfile="$1" target="$2"
  log "Building $repository/$target:$tag"
  docker build \
    --file "deploy/docker/$dockerfile" \
    --target "$target" \
    --label "org.opencontainers.image.revision=$revision" \
    --label "org.opencontainers.image.created=$created" \
    --label "org.opencontainers.image.title=spencer-denim-$target" \
    --tag "$repository/$target:$tag" \
    .
}

build backend.Dockerfile api
build backend.Dockerfile migrator
build frontend.Dockerfile storefront
build frontend.Dockerfile web

log "Built $repository/{api,migrator,storefront,web}:$tag"
echo "$tag"
