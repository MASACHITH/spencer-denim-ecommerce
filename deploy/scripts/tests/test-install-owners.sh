#!/usr/bin/env bash
# Tests that install.sh (and deploy.sh) never ask `install` for a numeric owner or group (#195, found on the first run).
#
# Ubuntu 26.04's default coreutils is uutils. Its `install -o 1654` / `-g 1654` fails with "invalid user" / "invalid group"
# when the number has no passwd or group entry, and the containers' users (API 1654, SQL Server 10001) have none on the
# host. GNU install accepted them. install.sh therefore stopped at its first owned directory on the real server.
#
# Two checks, because deploy.sh cannot be run without docker:
#   1. install.sh staging, run in a temp tree with an `install` that behaves like uutils (rejects an -o/-g number that is not
#      0) and a `chown` that logs. It must finish, and the log must show every owner the layout needs.
#   2. No script in deploy/scripts passes `install` a numeric -o/-g (a number, or the SD_*_UID variables).
#
#   bash deploy/scripts/tests/test-install-owners.sh
here="$(cd "$(dirname "$0")" && pwd)"
scripts="$here/.."
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
pass=0; fail=0
ok()  { pass=$((pass + 1)); printf '  ok   %s\n' "$1"; }
bad() { fail=$((fail + 1)); printf '  FAIL %s\n' "$1"; }

real_install="$(command -v install)"
stubs="$work/stubs"; mkdir -p "$stubs"
cat > "$stubs/install" <<EOF
#!/bin/sh
# uutils install 0.8.0: an -o / -g value that is a number other than 0 is "invalid" (no passwd/group entry on this host).
# A named owner is passed over: this test is not root, so the real install is called without it, and without -m (the
# mode is not what is tested here, and Git Bash on NTFS cannot apply 700 to a directory; the server shows the modes).
args=""
while [ \$# -gt 0 ]; do
  case "\$1" in
    -o|--owner) case "\$2" in 0) ;; *[!0-9]*) ;; *) echo "install: invalid user: '\$2'" >&2; exit 1 ;; esac; shift ;;
    -g|--group) case "\$2" in 0) ;; *[!0-9]*) ;; *) echo "install: invalid group: '\$2'" >&2; exit 1 ;; esac; shift ;;
    -m|--mode) shift ;;
    *) args="\$args '\$1'" ;;
  esac
  shift
done
eval "exec '$real_install' \$args"
EOF
cat > "$stubs/chown" <<'EOF'
#!/bin/sh
echo "chown $*" >> "$FAKE_LOG"
EOF
chmod +x "$stubs/install" "$stubs/chown"

echo "== install.sh staging, with an install that refuses numeric owners like uutils"
apps="$work/apps"; db="$work/db"; log="$work/log"; : > "$log"
OUT="$(SD_APPS_BASE="$apps" SD_DB_BASE="$db" FAKE_LOG="$log" PATH="$stubs:$PATH" bash "$scripts/install.sh" staging 2>&1 </dev/null)"; RC=$?
[ "$RC" = 0 ] && ok "install.sh finishes" || { bad "install.sh stopped (exit $RC): $(printf '%s' "$OUT" | tail -n 1)"; }
printf '%s\n' "$OUT" | grep -q 'Done\.' && ok "... and says Done." || bad "no 'Done.' line"

app="$apps/staging/spencer-denim-app"; dbd="$db/staging"
want() {
  grep -qxF "chown $1" "$log" && ok "owner set: $1" || bad "missing chown $1 (log: $(tr '\n' '|' < "$log"))"
}
want "root:1654 $app/config/dataprotection"
want "1654:1654 $app/data/dataprotection-keys $app/data/media-private $app/data/upload-spool"
want "1654:1654 $app/data/media-public"
want "10001:0 $dbd/data $dbd/backups"
for d in "$app/config/dataprotection" "$app/data/dataprotection-keys" "$app/data/media-private" "$app/data/upload-spool" \
         "$app/data/media-public" "$dbd/data" "$dbd/backups"; do
  [ -d "$d" ] || bad "directory not created: $d"
done
[ "$(grep -c '^chown ' "$log")" = 4 ] && ok "exactly four chown calls (nothing else is re-owned)" || bad "unexpected chown calls: $(tr '\n' '|' < "$log")"

echo "== no script gives install a numeric owner"
hits="$(grep -nE '(^|[^[:alnum:]_-])install[[:space:]].*[[:space:]]-[og][[:space:]]+("?\$\{?SD_[A-Z_]*UID\}?"?|[0-9]+)([[:space:]]|$)' "$scripts"/*.sh | grep -vE '^[^:]*:[0-9]+:[[:space:]]*#' || true)"
[ -z "$hits" ] && ok "deploy/scripts/*.sh: no install -o/-g with a number or an SD_*_UID" || bad "install with a numeric owner: $hits"
# the check itself must be able to see the old form
printf '%s\n' 'install -d -m 700 -o "$SD_API_UID" -g "$SD_API_UID" "$app/data/x"' > "$work/old.sh"
printf '%s\n' 'install -d -m 770 -o 10001 -g 0 "$db/data"' >> "$work/old.sh"
seen="$(grep -cE '(^|[^[:alnum:]_-])install[[:space:]].*[[:space:]]-[og][[:space:]]+("?\$\{?SD_[A-Z_]*UID\}?"?|[0-9]+)([[:space:]]|$)' "$work/old.sh")"
[ "$seen" = 2 ] && ok "... and the pattern does match the old form (so it can fail)" || bad "the pattern missed the old form ($seen of 2)"

echo
printf '%s passed, %s failed\n' "$pass" "$fail"
[ "$fail" -eq 0 ]
