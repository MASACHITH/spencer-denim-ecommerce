#!/usr/bin/env bash
# Runs the deployment-script tests (#195). One command:
#
#   bash deploy/scripts/tests/run-all.sh            # every test below
#   bash deploy/scripts/tests/run-all.sh --fast     # skip the docker one
#
# Each test is also runnable alone; see README.md in this folder. Exit status 1 if any test failed.
here="$(cd "$(dirname "$0")" && pwd)"
fast=0; [ "${1:-}" = "--fast" ] && fast=1
rc=0
run() { printf '\n######## %s\n' "$1"; shift; "$@" || rc=1; }

for f in "$here"/../*.sh; do bash -n "$f" || { echo "syntax error in $f"; rc=1; }; done
run "make-dp-cert.sh and the check-env.sh certificate rules" bash "$here/test-make-dp-cert.sh"
run "install.sh owners (an install that refuses numeric ids, like uutils on Ubuntu 26.04)" bash "$here/test-install-owners.sh"
run "check-env.sh: the first Super Admin password follows the staff rule" bash "$here/test-check-env-seed.sh"
run "wipe-staging.sh (temp tree, fake docker; staging only, verified empty)" bash "$here/test-wipe-staging.sh"
run "preflight.sh (fake docker/ss/ip/nginx/dig, no mutating commands on PATH)" bash "$here/test-preflight.sh"
run "verify-public.sh (mock shop: right, and wrong in one way at a time)" bash "$here/test-verify-public.sh"
if [ "$fast" -eq 0 ]; then
  run "host-nginx templates in a real nginx 1.24 (docker)" bash "$here/test-host-nginx.sh"
fi
[ "$rc" -eq 0 ] && printf '\nall deployment-script tests passed\n' || printf '\nSOME deployment-script tests FAILED\n'
exit "$rc"
