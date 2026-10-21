#!/usr/bin/env bash
# Tests that install.sh (and deploy.sh) never ask `install` for an owner or group (#195, found on the first run).
#
# Ubuntu 26.04's default coreutils is uutils. Its `install -o 1654` / `-g 1654` fails with "invalid user" / "invalid group"
# when the number has no passwd or group entry, and the containers' users (API 1654, SQL Server 10001) have none on the
# host. GNU install accepted them. install.sh therefore stopped at its first owned directory on the real server.
#
# Three checks, because deploy.sh cannot be run without docker:
#   1. install.sh staging, run in a temp tree with an `install` that behaves like uutils (any spelling of -o/-g/--owner/--group
#      with a number other than 0 is refused) and a `chown` that logs. It must finish, and for every directory the layout needs
#      the log must show `install -d -m <mode>` immediately followed by `chown <owner> <same directories>`.
#   2. No script in deploy/scripts passes `install` an owner or group option at all, in any spelling, on one line or over a
#      continuation (the owner is `owned_dir`'s job). The pattern is first shown to catch every spelling it must.
#   3. deploy.sh still calls owned_dir for the API's four writable directories with the same modes and owner.
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
#!/usr/bin/env bash
# uutils install 0.8.0 for what this test cares about: an owner or group that is a number other than 0 is "invalid" (no
# passwd/group entry on this host), in every spelling. A named owner is passed over: this test is not root. The mode is logged
# and not applied (Git Bash on NTFS cannot apply 700 to a directory; the server shows the real modes).
mode=""; dir=0; rest=()
while [ \$# -gt 0 ]; do
  kind=""; val=""
  case "\$1" in
    -o|--owner|-g|--group) kind="\$1"; val="\${2:-}"; shift ;;
    --owner=*|--group=*)   kind="\${1%%=*}"; val="\${1#*=}" ;;
    -o?*|-g?*)             kind="\${1:0:2}"; val="\${1:2}" ;;
    -m|--mode)             mode="\${2:-}"; shift ;;
    --mode=*)              mode="\${1#*=}" ;;
    -d|--directory)        dir=1 ;;
    *)                     rest+=("\$1") ;;
  esac
  if [ -n "\$kind" ]; then
    case "\$val" in
      0|*[!0-9]*|"") ;;
      *) case "\$kind" in -o|--owner) echo "install: invalid user: '\$val'" >&2 ;; *) echo "install: invalid group: '\$val'" >&2 ;; esac; exit 1 ;;
    esac
  fi
  shift
done
if [ "\$dir" = 1 ]; then echo "install -d -m \${mode:-none} \${rest[*]}" >> "\$FAKE_LOG"; exec '$real_install' -d "\${rest[@]}"; fi
exec '$real_install' "\${rest[@]}"
EOF
cat > "$stubs/chown" <<'EOF'
#!/bin/sh
echo "chown $*" >> "$FAKE_LOG"
EOF
chmod +x "$stubs/install" "$stubs/chown"

echo "== the stand-in refuses numeric owners in every spelling (so a pass below means something)"
refuses() { FAKE_LOG="$work/probe.log" "$stubs/install" -d "$@" "$work/probe-dir" >/dev/null 2>&1; [ $? -ne 0 ]; }
for spelling in "-o 1654" "-o1654" "-g 1654" "-g1654" "--owner 1654" "--owner=1654" "--group 1654" "--group=1654" "-o 10001 -g 0"; do
  # shellcheck disable=SC2086
  refuses $spelling && ok "refused: install -d $spelling" || bad "NOT refused: install -d $spelling"
done
# shellcheck disable=SC2086
refuses -g 0 && bad "install -g 0 was refused (uutils accepts 0)" || ok "accepted: install -d -g 0 (group 0 exists)"

echo "== install.sh staging, with an install that refuses numeric owners like uutils"
apps="$work/apps"; db="$work/db"; log="$work/log"; : > "$log"
OUT="$(SD_APPS_BASE="$apps" SD_DB_BASE="$db" FAKE_LOG="$log" PATH="$stubs:$PATH" bash "$scripts/install.sh" staging 2>&1 </dev/null)"; RC=$?
[ "$RC" = 0 ] && ok "install.sh finishes" || { bad "install.sh stopped (exit $RC): $(printf '%s' "$OUT" | tail -n 1)"; }
printf '%s\n' "$OUT" | grep -q 'Done\.' && ok "... and says Done." || bad "no 'Done.' line"

app="$apps/staging/spencer-denim-app"; dbd="$db/staging"
# pair <mode> <owner> <dirs...>: the log has `install -d -m <mode> <dirs>` and, on the very next line, `chown <owner> <dirs>`.
pair() {
  local mode="$1" owner="$2"; shift 2; local dirs="$*"
  local label="${dirs//$work\//}"
  if grep -xF -A1 -- "install -d -m $mode $dirs" "$log" | grep -qxF -- "chown $owner $dirs"; then ok "mode $mode then owner $owner: $label"
  else bad "no 'install -d -m $mode' followed by 'chown $owner' for: $label  (log: $(tr '\n' '|' < "$log"))"; fi
}
pair 750 "root:1654"    "$app/config/dataprotection"
pair 700 "1654:1654"    "$app/data/dataprotection-keys" "$app/data/media-private" "$app/data/upload-spool"
pair 755 "1654:1654"    "$app/data/media-public"
pair 770 "10001:0"      "$dbd/data" "$dbd/backups"
for d in "$app/config/dataprotection" "$app/data/dataprotection-keys" "$app/data/media-private" "$app/data/upload-spool" \
         "$app/data/media-public" "$dbd/data" "$dbd/backups"; do
  [ -d "$d" ] || bad "directory not created: $d"
done
[ "$(grep -c '^chown ' "$log")" = 4 ] && ok "exactly four chown calls (nothing else is re-owned)" || bad "unexpected chown calls: $(tr '\n' '|' < "$log")"

echo "== no script gives install an owner or group option"
# One logical line per command (a trailing backslash joins the next line), comment lines dropped, then: the word `install`
# followed, further along the line, by -o / -g (alone, joined to a value, or combined) or --owner / --group.
owner_pattern='(^|[^[:alnum:]_.-])install[[:space:]].*([[:space:]]-[a-zA-Z]*[og]([[:space:]=0-9"$]|$)|[[:space:]]--(owner|group)([[:space:]=]|$))'
logical_lines() { awk '{ line = line $0; if (sub(/\\$/, "", line)) { line = line " "; next } gsub(/[ \t]+/, " ", line); print FILENAME ":" FNR ": " line; line = "" }' "$@" | grep -vE '^[^:]*:[0-9]+:[[:space:]]*#'; }
hits="$(logical_lines "$scripts"/*.sh | grep -E "$owner_pattern" || true)"
[ -z "$hits" ] && ok "deploy/scripts/*.sh: no install with -o/-g/--owner/--group" || bad "install with an owner or group: $hits"

# the pattern has to be able to fail: every spelling the tester tried, each on its own line or over a continuation
cat > "$work/spellings.sh" <<'EOS'
install -d -m 700 -o "$SD_API_UID" -g "$SD_API_UID" "$app/data/x"
install -d -m 770 -o 10001 -g 0 "$db/data"
install -d -m 700 --owner="$SD_API_UID" --group="$SD_API_UID" "$app/data/x"
install -d -m 700 --owner "$SD_API_UID" "$app/data/x"
install -d -m 700 -o1654 -g1654 "$app/data/x"
install -d -m 700 -o "$api_uid" -g "$api_gid" "$app/data/x"
install -d -m 750 -o root -g "$SD_API_UID" "$app/config/dataprotection"
install -d -m 700 \
  -o "$SD_API_UID" \
  "$app/data/x"
sudo install -D -m 755 -o root -g root a b
EOS
seen="$(logical_lines "$work/spellings.sh" | grep -cE "$owner_pattern")"
[ "$seen" = 9 ] && ok "... and the pattern matches all 9 spellings (so it can fail)" || bad "the pattern matched $seen of 9 spellings"
cat > "$work/clean.sh" <<'EOS'
install -d -m 755 "$app" "$app/compose"
install -m 644 "$repo/deploy/app/compose.yml" "$app/compose/"
owned_dir 700 "$SD_API_UID:$SD_API_UID" "$app/data/x"
# install -d -o root -g 1654 is what the comment above owned_dir explains
echo "run install.sh staging -g later"
EOS
seen="$(logical_lines "$work/clean.sh" | grep -cE "$owner_pattern" || true)"
[ "$seen" = 0 ] && ok "... and it leaves owned_dir, plain install, comments and install.sh in a message alone" || bad "the pattern flagged $seen clean lines"

echo "== deploy.sh still sets the API's writable directories"
d="$(logical_lines "$scripts/deploy.sh")"
printf '%s\n' "$d" | grep -qF 'owned_dir 700 "$SD_API_UID:$SD_API_UID" "$app/data/dataprotection-keys" "$app/data/media-private" "$app/data/upload-spool"' \
  && ok "owned_dir 700 for the key ring, private media and upload spool" || bad "deploy.sh lost the owned_dir 700 call for the key ring, private media and upload spool"
printf '%s\n' "$d" | grep -qF 'owned_dir 755 "$SD_API_UID:$SD_API_UID" "$app/data/media-public"' \
  && ok "owned_dir 755 for public media" || bad "deploy.sh lost the owned_dir 755 call for public media"

echo
printf '%s passed, %s failed\n' "$pass" "$fail"
[ "$fail" -eq 0 ]
