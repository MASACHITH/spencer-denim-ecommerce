#!/usr/bin/env bash
# Tests deploy/scripts/wipe-staging.sh (review of #219, M1).
#
# Everything happens in a temp directory (SD_APPS_BASE / SD_DB_BASE point at it), with a production tree next to the
# staging one that must come out byte for byte the same. docker is a fake that logs its arguments; install.sh is a fake
# that logs that it ran (SD_WIPE_INSTALL); "the terminal" is a file holding the typed answer (SD_WIPE_TTY).
#
#   bash deploy/scripts/tests/test-wipe-staging.sh
here="$(cd "$(dirname "$0")" && pwd)"
script="$here/../wipe-staging.sh"
work="$(mktemp -d)"
acl_dir=""
# MSYS2_ARG_CONV_EXCL: Git Bash would otherwise rewrite icacls' "/deny" into a Windows path.
unlock() {
  [ -n "$acl_dir" ] || return 0
  if command -v icacls >/dev/null 2>&1; then MSYS2_ARG_CONV_EXCL='*' icacls "$(cygpath -w "$acl_dir")" /remove:d "$USERNAME" >/dev/null 2>&1; fi
  chmod u+rwx "$acl_dir" 2>/dev/null
  return 0
}
cleanup() { unlock; rm -rf "$work"; }
trap cleanup EXIT

stubs="$work/stubs"; mkdir -p "$stubs"
cat > "$stubs/docker" <<'EOF'
#!/bin/sh
echo "docker $*" >> "$FAKE_LOG"
case "$1" in
  compose) exit 0 ;;
  ps) [ -n "${FAKE_RUNNING:-}" ] && case "$*" in *"project=$FAKE_RUNNING"*) echo abc123def456 ;; esac; exit 0 ;;
esac
exit 1
EOF
cat > "$work/fake-install.sh" <<'EOF'
echo "install $*" >> "$FAKE_LOG"
EOF
# A find that ignores -delete: stands in for "the deletion silently did not happen" (the M1 failure mode).
lazy="$work/lazy"; mkdir -p "$lazy"
real_find="$(command -v find)"
cat > "$lazy/find" <<EOF
#!/bin/sh
for a in "\$@"; do [ "\$a" = -delete ] && exit 0; done
exec "$real_find" "\$@"
EOF
chmod +x "$stubs/docker" "$lazy/find"

pass=0 fail=0
ok()  { pass=$((pass + 1)); printf 'ok    %s\n' "$*"; }
bad() { fail=$((fail + 1)); printf 'FAIL  %s\n' "$*"; }
expect_in() { if printf '%s\n' "$2" | grep -qF -- "$3"; then ok "$1"; else bad "$1 (wanted: $3)"; printf '%s\n' "$2" | sed 's/^/        | /' | head -n 12; fi; }

populate() {   # base env -> a layout with files everywhere, dot-files and nested directories included
  local b="$1" e="$2" d
  local app="$b/apps/$e/spencer-denim-app" db="$b/db/$e"
  mkdir -p "$app/compose" "$app/config/dataprotection" "$db/compose" "$db/config"
  : > "$app/compose/compose.yml"; : > "$app/compose/compose.staging.yml"; : > "$db/compose/compose.yml"
  printf 'SD_PUBLIC_HOSTS=x\n' > "$app/config/app.env"
  printf 'SD_ENV=%s\n' "$e" > "$db/config/db.env"
  printf 'pfx' > "$app/config/dataprotection/dataprotection.pfx"
  for d in "$db/data" "$db/backups" "$app/data"; do
    mkdir -p "$d/sub/deeper" "$d/.hidden-dir"
    printf 'x' > "$d/file.mdf"; printf 'x' > "$d/.dotfile"; printf 'x' > "$d/sub/deeper/f"; printf 'x' > "$d/.hidden-dir/.y"
  done
}
new_layout() {   # name -> base dir holding a populated staging AND production
  local b="$work/$1"; populate "$b" staging; populate "$b" production; printf 'wipe staging\n' > "$b/tty"; : > "$b/log"; printf '%s' "$b"
}
snap() { ( cd "$1" && find . | sort | while read -r p; do if [ -f "$p" ]; then printf '%s %s\n' "$p" "$(cksum < "$p")"; else echo "$p/"; fi; done ); }
wipe() {   # base [args...] -> OUT, RC
  local b="$1"; shift
  OUT="$(SD_APPS_BASE="$b/apps" SD_DB_BASE="$b/db" SD_WIPE_ALLOW_NONROOT=1 SD_WIPE_TTY="$b/tty" SD_WIPE_INSTALL="$work/fake-install.sh" \
         FAKE_LOG="$b/log" PATH="${EXTRA_PATH:+$EXTRA_PATH:}$stubs:$PATH" bash "$script" "$@" 2>&1 </dev/null)"
  RC=$?
}
empty() { [ -d "$1" ] && [ -z "$(find "$1" -mindepth 1 | head -n 1)" ]; }
staging_dirs() { printf '%s\n' "$1/db/staging/data" "$1/db/staging/backups" "$1/apps/staging/spencer-denim-app/data"; }
untouched() {   # base label -> the staging data is all still there, docker never stopped anything, install never ran
  local b="$1" left=0 d
  for d in $(staging_dirs "$b"); do [ -f "$d/.dotfile" ] && [ -f "$d/sub/deeper/f" ] && left=$((left + 1)); done
  [ "$left" = 3 ] && ok "$2: nothing was deleted" || bad "$2: something was deleted"
  grep -q 'down' "$b/log" && bad "$2: a stack was stopped" || ok "$2: no stack was stopped"
  grep -q '^install' "$b/log" && bad "$2: install.sh ran" || ok "$2: install.sh did not run"
}

echo "== confirmed: wipes the three staging directories, dot-files and all, and nothing else"
b="$(new_layout good)"
prod_before="$(snap "$b/apps/production")$(snap "$b/db/production")"
wipe "$b"
[ "$RC" = 0 ] && ok "exits 0" || { bad "exit $RC"; printf '%s\n' "$OUT" | head -n 15; }
n=0; for d in $(staging_dirs "$b"); do empty "$d" && n=$((n + 1)); done
[ "$n" = 3 ] && ok "db/data, db/backups and app/data exist and are empty (dot-files and nested directories gone)" || bad "$n of 3 directories empty: $(find "$b/db/staging/data" "$b/db/staging/backups" "$b/apps/staging/spencer-denim-app/data" -mindepth 1 | head -n 5 | tr '\n' ' ')"
prod_after="$(snap "$b/apps/production")$(snap "$b/db/production")"
[ "$prod_before" = "$prod_after" ] && ok "the production tree is byte for byte unchanged" || bad "the production tree changed"
[ -f "$b/apps/staging/spencer-denim-app/config/app.env" ] && [ -f "$b/apps/staging/spencer-denim-app/config/dataprotection/dataprotection.pfx" ] && [ -f "$b/db/staging/config/db.env" ] \
  && ok "staging config/ and the .pfx are kept" || bad "staging config/ or the .pfx was deleted"
log="$(cat "$b/log")"
expect_in "the application stack is stopped as sd-staging" "$log" "docker compose -p sd-staging --project-directory . -f compose.yml -f compose.staging.yml --env-file ../config/app.env down"
expect_in "the database stack is stopped by explicit name sd-staging-database" "$log" "docker compose -p sd-staging-database --env-file ../config/db.env down"
grep -q -- 'down.*-v\|--volumes\|production' "$b/log" && bad "docker was given -v / --volumes or a production name" || ok "no -v, no --volumes, no production name passed to docker"
expect_in "install.sh staging runs afterwards" "$log" "install staging"
expect_in "it reports the directories verified empty" "$OUT" "Verified empty:"

echo "== the guards: each one stops it before anything is stopped or deleted"
b="$(new_layout noconfirm)"; printf 'yes\n' > "$b/tty"; wipe "$b"
[ "$RC" != 0 ] && ok "a wrong confirmation is refused" || bad "a wrong confirmation went ahead"; expect_in "... saying so" "$OUT" "not confirmed"
untouched "$b" "wrong confirmation"
b="$(new_layout emptyconfirm)"; : > "$b/tty"; wipe "$b"
[ "$RC" != 0 ] && ok "no answer at all is refused" || bad "an empty answer went ahead"; untouched "$b" "no answer"
for arg in production staging --yes; do
  b="$(new_layout "arg$arg")"; wipe "$b" "$arg"
  [ "$RC" != 0 ] && ok "argument '$arg' refused" || bad "argument '$arg' accepted"
  untouched "$b" "argument '$arg'"
done
expect_in "... saying it cannot be pointed at production" "$OUT" "There is no way to point it at production"
b="$(new_layout dbenvprod)"; printf 'SD_ENV=production\n' > "$b/db/staging/config/db.env"; wipe "$b"
[ "$RC" != 0 ] && ok "a staging db.env saying SD_ENV=production is refused" || bad "db.env SD_ENV=production accepted"
expect_in "... saying so" "$OUT" "says SD_ENV=production, not staging"; untouched "$b" "db.env says production"
b="$(new_layout running)"; FAKE_RUNNING=sd-staging-database wipe "$b"
[ "$RC" != 0 ] && ok "a staging container still running after 'down' stops it" || bad "deleted while a container was running"
expect_in "... saying so" "$OUT" "still running"
left=0; for d in $(staging_dirs "$b"); do [ -f "$d/.dotfile" ] && left=$((left + 1)); done
[ "$left" = 3 ] && ok "... and nothing was deleted" || bad "files were deleted while a container was running"
b="$(new_layout missing)"; rm -rf "$b/db/staging/backups"; wipe "$b"
[ "$RC" != 0 ] && ok "a missing staging directory is refused" || bad "a missing directory went ahead"; expect_in "... saying so" "$OUT" "does not exist"
if [ "$(id -u)" -ne 0 ]; then
  b="$(new_layout nonroot)"
  OUT="$(SD_APPS_BASE="$b/apps" SD_DB_BASE="$b/db" SD_WIPE_TTY="$b/tty" SD_WIPE_INSTALL="$work/fake-install.sh" FAKE_LOG="$b/log" PATH="$stubs:$PATH" bash "$script" 2>&1 </dev/null)"; RC=$?
  [ "$RC" != 0 ] && printf '%s\n' "$OUT" | grep -q 'run this with sudo' && ok "refuses to run without sudo" || bad "ran without root"
  untouched "$b" "without sudo"
fi

echo "== links to production are refused, whatever the text of the path says (re-check of #219, F1)"
# mklink <link> <target>: a Windows junction (no admin rights needed; Git Bash treats it as a symlink), else ln -s.
mklink() {
  if command -v cygpath >/dev/null 2>&1 && command -v powershell >/dev/null 2>&1; then
    powershell -NoProfile -Command "New-Item -ItemType Junction -Path '$(cygpath -w "$1")' -Target '$(cygpath -w "$2")' | Out-Null" >/dev/null 2>&1
  else
    ln -s "$2" "$1"
  fi
  [ -L "$1" ] || [ "$(readlink -f -- "$1")" != "$1" ]
}
prod_intact() {   # base label -> production's files all still there
  local b="$1" n=0 d
  for d in "$b/db/production/data" "$b/db/production/backups" "$b/apps/production/spencer-denim-app/data"; do
    [ -f "$d/file.mdf" ] && [ -f "$d/.dotfile" ] && [ -f "$d/sub/deeper/f" ] && n=$((n + 1))
  done
  [ "$n" = 3 ] && ok "$2: production's files are all still there" || bad "$2: PRODUCTION FILES WERE DELETED ($n of 3 directories intact)"
}
# A: the whole staging base is a junction to production, and there is no db.env to give it away.
b="$(new_layout linkA)"
rm -rf "$b/db/staging" "$b/apps/staging"; rm -f "$b/db/production/config/db.env"
if mklink "$b/db/staging" "$b/db/production" && mklink "$b/apps/staging" "$b/apps/production"; then
  wipe "$b"
  [ "$RC" != 0 ] && ok "A (staging base -> production, no db.env): refused" || bad "A: went ahead"
  expect_in "A: ... saying it resolves elsewhere" "$OUT" "resolves to"
  expect_not_in_out() { printf '%s\n' "$OUT" | grep -qF -- "$1" && bad "A: output says '$1'" || ok "A: does not claim '$1'"; }
  expect_not_in_out "Staging wiped"
  prod_intact "$b" "A"
  grep -q 'down' "$b/log" && bad "A: a stack was stopped" || ok "A: no stack was stopped"
  grep -q '^install' "$b/log" && bad "A: install.sh ran" || ok "A: install.sh did not run"
else
  bad "A: could not create the junction/link for the test"
fi
# C: only staging's data directory is a junction to production's.
b="$(new_layout linkC)"
rm -rf "$b/db/staging/data"
if mklink "$b/db/staging/data" "$b/db/production/data"; then
  wipe "$b"
  [ "$RC" != 0 ] && ok "C (staging/data -> production/data): refused" || bad "C: went ahead"
  expect_in "C: ... saying it resolves elsewhere" "$OUT" "resolves to"
  printf '%s\n' "$OUT" | grep -qF "Verified empty" && bad "C: claimed 'Verified empty'" || ok "C: does not claim 'Verified empty'"
  prod_intact "$b" "C"
  left=0; for d in "$b/db/staging/backups" "$b/apps/staging/spencer-denim-app/data"; do [ -f "$d/.dotfile" ] && left=$((left + 1)); done
  [ "$left" = 2 ] && ok "C: the real staging directories were not touched either" || bad "C: staging files deleted before the refusal"
  grep -q 'down\|^install' "$b/log" && bad "C: a stack was stopped or install.sh ran" || ok "C: no stack stopped, install.sh not run"
else
  bad "C: could not create the junction/link for the test"
fi
# A missing db.env (no link involved) is now refused instead of skipped.
b="$(new_layout nodbenv)"; rm -f "$b/db/staging/config/db.env"; wipe "$b"
[ "$RC" != 0 ] && ok "a missing staging db.env: refused" || bad "a missing db.env went ahead"
expect_in "... saying so" "$OUT" "is missing: cannot prove this is staging"; untouched "$b" "missing db.env"
# A relative base (an override) is refused.
b="$(new_layout relbase)"
OUT="$(cd "$b" && SD_APPS_BASE=apps SD_DB_BASE=db SD_WIPE_ALLOW_NONROOT=1 SD_WIPE_TTY="$b/tty" SD_WIPE_INSTALL="$work/fake-install.sh" FAKE_LOG="$b/log" PATH="$stubs:$PATH" bash "$script" 2>&1 </dev/null)"; RC=$?
[ "$RC" != 0 ] && ok "a relative SD_APPS_BASE/SD_DB_BASE: refused" || bad "relative bases accepted"
expect_in "... saying so" "$OUT" "is not an absolute path"; untouched "$b" "relative base"

echo "== a deletion that silently does nothing is caught (the M1 failure mode)"
b="$(new_layout lazy)"; EXTRA_PATH="$lazy" wipe "$b"
[ "$RC" != 0 ] && ok "exits non-zero when files are left" || bad "exit 0 although nothing was deleted"
expect_in "... names the directory that is not empty" "$OUT" "is NOT empty after the wipe"
expect_in "... and says install.sh was not run" "$OUT" "install.sh was NOT run"
grep -q '^install' "$b/log" && bad "install.sh ran after a failed wipe" || ok "install.sh did not run"

echo "== a directory this user cannot read (what root-only data looks like to deploy1) is caught, not skipped"
b="$(new_layout unreadable)"; acl_dir="$b/db/staging/data"
locked=0
if command -v icacls >/dev/null 2>&1 && [ -n "${USERNAME:-}" ]; then
  MSYS2_ARG_CONV_EXCL='*' icacls "$(cygpath -w "$acl_dir")" /deny "$USERNAME:(OI)(CI)(RD,DE,DC)" >/dev/null 2>&1 && locked=1
elif [ "$(id -u)" -ne 0 ]; then
  chmod 000 "$acl_dir" && locked=1
fi
if [ "$locked" = 1 ] && ! ls -A "$acl_dir" >/dev/null 2>&1; then
  wipe "$b"
  [ "$RC" != 0 ] && ok "exits non-zero when a directory cannot be emptied" || bad "exit 0 with an unreadable directory"
  expect_in "... saying it is not empty" "$OUT" "is NOT empty after the wipe"
  grep -q '^install' "$b/log" && bad "install.sh ran after a failed wipe" || ok "install.sh did not run"
  # The old doc block's form against the same directory, to show the bug this replaces: the glob is not expanded,
  # rm -rf gets a literal ".../data/*" that does not exist, exits 0, and every file is still there.
  ( set -e; rm -rf -- "$acl_dir"/* ) 2>/dev/null; old_rc=$?
  unlock
  old_left="$(ls -A "$acl_dir" | wc -l)"
  [ "$old_rc" = 0 ] && [ "$old_left" -gt 0 ] && ok "(the old 'rm -rf dir/*' form reproduces M1 here: exit 0, $old_left entries left)" \
    || bad "(the old form did not behave as described in M1: exit $old_rc, $old_left entries left)"
else
  echo "SKIP  could not make a directory unreadable to this user (running as root, or no icacls/chmod effect)"
fi

echo
printf '%s as expected, %s not\n' "$pass" "$fail"
[ "$fail" -eq 0 ]
