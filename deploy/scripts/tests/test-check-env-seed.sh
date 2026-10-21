#!/usr/bin/env bash
# Tests that check-env.sh refuses a first-Super-Admin password the API would refuse (#195, found on the first UAT run).
#
# The API holds SD_SEED_ADMIN_PASSWORD to the staff rule: at least PasswordPolicy.MinimumLength characters with a digit, a
# lower-case letter, an upper-case letter and a non-alphanumeric character (IdentityRegistration). check-env.sh used to check
# only the length, so a generated 32-character password of letters and digits passed it, the deploy went green, and the API
# logged SeedingFailed with no account created. Everything happens in a temp tree (SD_APPS_BASE / SD_DB_BASE).
#
#   bash deploy/scripts/tests/test-check-env-seed.sh
here="$(cd "$(dirname "$0")" && pwd)"
scripts="$here/.."
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
pass=0; fail=0
ok()  { pass=$((pass + 1)); printf 'ok    %s
' "$1"; }
bad() { fail=$((fail + 1)); printf 'FAIL  %s
' "$1"; }
expect_in()     { if printf '%s
' "$2" | grep -qF -- "$3"; then ok "$1"; else bad "$1 (not found: $3)"; fi; }
expect_not_in() { if printf '%s
' "$2" | grep -qF -- "$3"; then bad "$1 (found: $3)"; else ok "$1"; fi; }

new_layout() {   # name -> prints the base dir; a staging layout with valid, certificate-less env files
  local b="$work/$1"
  mkdir -p "$b/apps/staging/spencer-denim-app/config" "$b/apps/staging/spencer-denim-app/data/dataprotection-keys" "$b/db/staging/config"
  cat > "$b/db/staging/config/db.env" <<'EOF'
SD_ENV=staging
SD_MSSQL_SA_PASSWORD=Aa1TestOnlyNotASecret00
SD_MSSQL_PID=Express
SD_MSSQL_MEMORY_LIMIT_MB=1024
SD_MSSQL_CONTAINER_MEMORY=1536m
EOF
  cat > "$b/apps/staging/spencer-denim-app/config/app.env" <<'EOF'
SD_PUBLIC_HOSTS=staging.example.test
SD_PUBLIC_ORIGIN=https://staging.example.test
SD_APP_DB_CONNECTION=Server=sqlserver,1433;Database=SpencerDenim_Staging;User Id=sd_staging_app;Password=Aa1AppTestOnly0000;Encrypt=True;TrustServerCertificate=True
SD_MIGRATOR_DB_CONNECTION=Server=sqlserver,1433;Database=SpencerDenim_Staging;User Id=sd_staging_migrator;Password=Aa1MigTestOnly0000;Encrypt=True;TrustServerCertificate=True
SD_DB_COMPATIBILITY_LEVEL=170
SD_DP_CERT_FILE=
SD_DP_CERT_PASSWORD=
EOF
  printf '%s' "$b"
}
chk() {   # base -> runs check-env.sh, output in $OUT, status in $RC
  OUT="$(SD_APPS_BASE="$1/apps" SD_DB_BASE="$1/db" bash "$scripts/check-env.sh" staging 2>&1)"
  RC=$?
}

echo "== check-env.sh: the first Super Admin password follows the staff rule (found on the first UAT run)"
seedbase="$(new_layout seedrule)"
seed() {   # enabled password -> check-env.sh on a copy whose app.env turns the seed on with that password
  rm -rf "$work/s"; cp -r "$seedbase" "$work/s"
  printf 'SD_SEED_ADMIN_ENABLED=%s
SD_SEED_ADMIN_EMAIL=owner@example.test
SD_SEED_ADMIN_PASSWORD=%s
' "$1" "$2" >> "$work/s/apps/staging/spencer-denim-app/config/app.env"
  chk "$work/s"
}
seed true 'Aa1-Qx7Rk2Vm9Zt4Lp8Wn3Bd6Hs0Jc1'; [ "$RC" = 0 ] && ok "a password with all four kinds, 32 characters: accepted" || { bad "a good seed password was refused"; printf '%s
' "$OUT" | head -n 4; }
old_gen='Aa1TestOnlyLettersAndDigits0123456789'   # the shape the sheet's old generator made: 32+ characters, letters and digits only
seed true "$old_gen"; [ "$RC" != 0 ] && ok "letters and digits only (the old B5 generator): refused" || bad "a password with no symbol was accepted"
expect_in "... naming the missing kind" "$OUT" "is missing a symbol"
expect_not_in "... without printing the password" "$OUT" "$old_gen"
seed true 'aa1-nouppercase-xyz'; [ "$RC" != 0 ] && ok "no upper-case letter: refused" || bad "no upper-case accepted"; expect_in "... naming it" "$OUT" "an upper-case letter"
seed true 'AA1-NOLOWERCASE-XYZ'; [ "$RC" != 0 ] && ok "no lower-case letter: refused" || bad "no lower-case accepted"; expect_in "... naming it" "$OUT" "a lower-case letter"
seed true 'Aa-NoDigitsHere-xyz';  [ "$RC" != 0 ] && ok "no digit: refused" || bad "no digit accepted"; expect_in "... naming it" "$OUT" "a number"
seed true 'Aa1-x';               [ "$RC" != 0 ] && ok "too short: still refused" || bad "a short password was accepted"; expect_in "... naming the length" "$OUT" "shorter than"
seed true 'abcdefghijkl';        [ "$RC" != 0 ] && ok "several kinds missing: refused" || bad "a weak password was accepted"
for kind in "a number" "an upper-case letter" "a symbol"; do expect_in "... and the same line names $kind" "$OUT" "$kind"; done
seed false 'weak';               [ "$RC" = 0 ] && ok "seed switched off: the password is not checked" || bad "a switched-off seed was checked"

echo
printf '%s as expected, %s not
' "$pass" "$fail"
[ "$fail" -eq 0 ]
