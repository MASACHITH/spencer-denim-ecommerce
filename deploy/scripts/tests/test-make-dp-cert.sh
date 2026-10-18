#!/usr/bin/env bash
# Tests deploy/scripts/make-dp-cert.sh, the Data Protection certificate rules in check-env.sh (lib.sh check_dp_cert),
# and that deploy/app/compose.yml passes the two settings through only when they are set (#195).
#
# Everything happens in a temp directory (SD_APPS_BASE / SD_DB_BASE point at it). The script is run as the current user
# (SD_DP_CERT_ALLOW_NONROOT=1: it cannot chown here), and "the terminal" the password is shown on is a plain file
# (SD_DP_CERT_TTY), so the test can read what a person would have seen, and prove it was shown nowhere else.
# A fake `stat` reports mode 640 / owner root for the .pfx, which is what install does on the server.
#
#   bash deploy/scripts/tests/test-make-dp-cert.sh
export MSYS2_ARG_CONV_EXCL='/CN='
here="$(cd "$(dirname "$0")" && pwd)"
scripts="$here/.."
repo="$(cd "$here/../../.." && pwd)"
command -v openssl >/dev/null 2>&1 || { echo "SKIP: this test needs openssl"; exit 0; }
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
real_stat="$(command -v stat)"

stubs="$work/stubs"; mkdir -p "$stubs"
cat > "$stubs/stat" <<EOF
#!/bin/sh
# only the two format strings lib.sh asks for are faked, and only for the certificate
fmt=""; path=""
while [ \$# -gt 0 ]; do case "\$1" in -c) fmt="\$2"; shift ;; *) path="\$1" ;; esac; shift; done
case "\$path" in
  *.pfx) case "\$fmt" in %a) echo "\${FAKE_PFX_MODE:-640}"; exit 0 ;; %U) echo "\${FAKE_PFX_OWNER:-root}"; exit 0 ;; esac ;;
  *) case "\$fmt" in %a) echo 600; exit 0 ;; esac ;;
esac
exec "$real_stat" -c "\$fmt" "\$path"
EOF
chmod +x "$stubs/stat"

pass=0 fail=0
ok()  { pass=$((pass + 1)); printf 'ok    %s\n' "$*"; }
bad() { fail=$((fail + 1)); printf 'FAIL  %s\n' "$*"; }
expect_in() { # label output needle
  if printf '%s\n' "$2" | grep -qF -- "$3"; then ok "$1"; else bad "$1 (wanted: $3)"; printf '%s\n' "$2" | sed 's/^/        | /' | head -n 12; fi
}
expect_not_in() {
  if printf '%s\n' "$2" | grep -qF -- "$3"; then bad "$1 (found: $3)"; else ok "$1"; fi
}

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
  : > "$b/tty"
  printf '%s' "$b"
}
mk() {   # base args... -> runs make-dp-cert.sh in that layout, output in $OUT, status in $RC
  local b="$1"; shift
  OUT="$(SD_APPS_BASE="$b/apps" SD_DB_BASE="$b/db" SD_DP_CERT_ALLOW_NONROOT=1 SD_DP_CERT_TTY="$b/tty" PATH="$stubs:$PATH" bash "$scripts/make-dp-cert.sh" "$@" 2>&1 < "${MK_STDIN:-/dev/null}")"
  RC=$?
}
chk() {   # base -> runs check-env.sh, output in $OUT, status in $RC
  local b="$1"
  OUT="$(SD_APPS_BASE="$b/apps" SD_DB_BASE="$b/db" PATH="$stubs:$PATH" bash "$scripts/check-env.sh" staging 2>&1)"
  RC=$?
}
set_env() {   # base KEY VALUE
  local f="$1/apps/staging/spencer-denim-app/config/app.env"
  sed -i "s|^$2=.*|$2=$3|" "$f"
}
pfx_of() { printf '%s/apps/staging/spencer-denim-app/config/dataprotection/dataprotection.pfx' "$1"; }

echo "== make-dp-cert.sh: generated password, --write-env"
b="$(new_layout gen)"
mk "$b" staging --write-env
[ "$RC" = 0 ] && ok "exits 0" || { bad "exit $RC"; printf '%s\n' "$OUT" | head -n 15; }
pw="$(sed -n 's/^ *\([A-Za-z0-9]\{32\}\)$/\1/p' "$b/tty" | head -n 1)"
[ "${#pw}" = 32 ] && ok "the 32-character password was shown on the terminal" || bad "no password on the terminal: $(cat "$b/tty")"
expect_not_in "the password is NOT in the script's own output (stdout/stderr)" "$OUT" "$pw"
hits="$(grep -rlF -- "$pw" "$b" | sed "s|$b/||" | sort | tr '\n' ' ')"
[ "$hits" = "apps/staging/spencer-denim-app/config/app.env tty " ] && ok "the password is in app.env (--write-env) and on the terminal, and in no other file" || bad "password found in: $hits"
openssl pkcs12 -in "$(pfx_of "$b")" -passin "pass:$pw" -noout >/dev/null 2>&1 && ok "the .pfx opens with that password" || bad "the .pfx does not open"
openssl pkcs12 -in "$(pfx_of "$b")" -passin "pass:${pw}x" -noout >/dev/null 2>&1 && bad "the .pfx opens with a WRONG password" || ok "the .pfx refuses a wrong password"
openssl pkcs12 -in "$(pfx_of "$b")" -passin "pass:$pw" -nokeys -clcerts 2>/dev/null | openssl x509 -noout -text 2>/dev/null | grep -q 'Public-Key: (3072 bit)' && ok "3072-bit RSA" || bad "not a 3072-bit key"
openssl pkcs12 -in "$(pfx_of "$b")" -passin "pass:$pw" -nokeys -clcerts 2>/dev/null | openssl x509 -noout -checkend $((3000 * 86400)) >/dev/null 2>&1 && ok "valid for about ten years" || bad "validity shorter than expected"
grep -q '^SD_DP_CERT_FILE=dataprotection.pfx$' "$b/apps/staging/spencer-denim-app/config/app.env" && ok "SD_DP_CERT_FILE set in app.env" || bad "SD_DP_CERT_FILE not set"
chk "$b"
[ "$RC" = 0 ] && ok "check-env.sh accepts the result" || { bad "check-env.sh rejects the result"; printf '%s\n' "$OUT" | head; }
mk "$b" staging --write-env
[ "$RC" != 0 ] && ok "a second run refuses" || bad "a second run did not refuse"
expect_in "... and says why" "$OUT" "already holds dataprotection.pfx"

echo "== make-dp-cert.sh: without --write-env nothing but the terminal sees the password"
b="$(new_layout nowrite)"
mk "$b" staging
pw="$(sed -n 's/^ *\([A-Za-z0-9]\{32\}\)$/\1/p' "$b/tty" | head -n 1)"
hits="$(grep -rlF -- "$pw" "$b" | sed "s|$b/||" | tr '\n' ' ')"
[ "$hits" = "tty " ] && ok "the password is on the terminal and in no file at all" || bad "password found in: $hits"
expect_in "the two env lines are printed for the operator, without the password" "$OUT" "SD_DP_CERT_PASSWORD=<the password>"
grep -q '^SD_DP_CERT_FILE=$' "$b/apps/staging/spencer-denim-app/config/app.env" && ok "app.env untouched" || bad "app.env was changed"

echo "== make-dp-cert.sh: --password-stdin and --prompt"
b="$(new_layout stdin)"
MK_STDIN="$work/in1"; printf 'Correct.Horse-Battery_Staple9\n' > "$MK_STDIN"
mk "$b" staging --password-stdin
[ "$RC" = 0 ] && ok "--password-stdin works" || { bad "--password-stdin failed"; printf '%s\n' "$OUT" | head; }
expect_not_in "... and never echoes the password" "$OUT" "Correct.Horse"
[ ! -s "$b/tty" ] && ok "... and shows nothing on the terminal" || bad "something was written to the terminal in --password-stdin mode"
openssl pkcs12 -in "$(pfx_of "$b")" -passin "pass:Correct.Horse-Battery_Staple9" -noout >/dev/null 2>&1 && ok "the .pfx opens with the given password" || bad "the .pfx does not open with the given password"
for badpw in 'short' 'has space in it 1234567890' 'dollar$sign1234567890' 'quote"mark1234567890'; do
  b2="$(new_layout "bad$(printf '%s' "$badpw" | cksum | cut -d' ' -f1)")"
  printf '%s\n' "$badpw" > "$work/in2"; MK_STDIN="$work/in2"
  mk "$b2" staging --password-stdin
  if [ "$RC" != 0 ] && [ ! -e "$(pfx_of "$b2")" ]; then ok "refused password: $badpw"; else bad "accepted a bad password: $badpw"; fi
done
b="$(new_layout prompt)"
printf 'Another.Fine-Passw0rd_123\nAnother.Fine-Passw0rd_123\n' > "$b/tty"; MK_STDIN=/dev/null
mk "$b" staging --prompt
[ "$RC" = 0 ] && ok "--prompt works (typed twice)" || { bad "--prompt failed"; printf '%s\n' "$OUT" | head; }
b="$(new_layout promptdiff)"
printf 'Another.Fine-Passw0rd_123\nSomething.Else-Passw0rd_9\n' > "$b/tty"
mk "$b" staging --prompt
[ "$RC" != 0 ] && ok "--prompt refuses two different answers" || bad "--prompt accepted different answers"
expect_in "... saying so" "$OUT" "the two passwords differ"
MK_STDIN=/dev/null

echo "== make-dp-cert.sh: the guards"
b="$(new_layout ring)"
printf '<key id="x"><descriptor><masterKey><!-- Warning: the key below is in an unencrypted form. --></masterKey></descriptor></key>\n' > "$b/apps/staging/spencer-denim-app/data/dataprotection-keys/key-1.xml"
mk "$b" staging
[ "$RC" != 0 ] && [ ! -e "$(pfx_of "$b")" ] && ok "refuses while a key ring exists, and writes nothing" || bad "did not refuse with a key ring present"
expect_in "... explaining" "$OUT" "A certificate only encrypts keys created AFTER it is switched on"
mk "$b" staging --allow-existing-keyring
[ "$RC" = 0 ] && ok "--allow-existing-keyring goes ahead" || bad "--allow-existing-keyring failed"
expect_in "... and warns the old key stays in clear text" "$OUT" "1 unencrypted"
b="$(new_layout days)"
mk "$b" staging --days 100
[ "$RC" != 0 ] && ok "--days under 365 refused" || bad "--days 100 accepted"
mk "$b" staging --nonsense
[ "$RC" != 0 ] && ok "unknown option refused" || bad "unknown option accepted"
mk "$b" qa
[ "$RC" != 0 ] && ok "unknown environment refused" || bad "unknown environment accepted"
if [ "$(id -u)" -ne 0 ]; then
  OUT="$(SD_APPS_BASE="$b/apps" SD_DB_BASE="$b/db" SD_DP_CERT_TTY="$b/tty" bash "$scripts/make-dp-cert.sh" staging 2>&1 </dev/null)"; RC=$?
  [ "$RC" != 0 ] && printf '%s\n' "$OUT" | grep -q 'run this with sudo' && ok "refuses to run without sudo" || bad "ran without root"
fi

echo "== check-env.sh: the certificate rules (lib.sh check_dp_cert)"
good="$(new_layout checks)"
mk "$good" staging --write-env
cp_layout() { rm -rf "$work/c"; cp -r "$good" "$work/c"; printf '%s' "$work/c"; }
pw="$(sed -n 's/^SD_DP_CERT_PASSWORD=//p' "$good/apps/staging/spencer-denim-app/config/app.env")"
c="$(cp_layout)"; chk "$c"; [ "$RC" = 0 ] && ok "both set, file present, password right: accepted" || { bad "good setup rejected"; printf '%s\n' "$OUT" | head; }
c="$(cp_layout)"; set_env "$c" SD_DP_CERT_FILE ""; set_env "$c" SD_DP_CERT_PASSWORD ""; chk "$c"
[ "$RC" = 0 ] && ok "both empty: accepted, nothing checked (today's behaviour)" || { bad "both empty rejected"; printf '%s\n' "$OUT" | head; }
c="$(cp_layout)"; set_env "$c" SD_DP_CERT_PASSWORD ""; chk "$c"
[ "$RC" != 0 ] && ok "file without password: refused" || bad "file without password accepted"; expect_in "... saying so" "$OUT" "SD_DP_CERT_FILE is set but SD_DP_CERT_PASSWORD is empty"
c="$(cp_layout)"; set_env "$c" SD_DP_CERT_FILE ""; chk "$c"
[ "$RC" != 0 ] && ok "password without file: refused" || bad "password without file accepted"; expect_in "... saying so" "$OUT" "SD_DP_CERT_PASSWORD is set but SD_DP_CERT_FILE is empty"
c="$(cp_layout)"; set_env "$c" SD_DP_CERT_FILE "../dataprotection.pfx"; chk "$c"
[ "$RC" != 0 ] && ok "a path instead of a file name: refused" || bad "path accepted"; expect_in "... saying so" "$OUT" "must be a plain file name"
c="$(cp_layout)"; set_env "$c" SD_DP_CERT_FILE "missing.pfx"; chk "$c"
[ "$RC" != 0 ] && ok "a file that does not exist: refused" || bad "missing file accepted"; expect_in "... saying so" "$OUT" "does not exist"
c="$(cp_layout)"; set_env "$c" SD_DP_CERT_PASSWORD "NotThePasswordAtAll12345"; chk "$c"
[ "$RC" != 0 ] && ok "the wrong password: refused" || bad "wrong password accepted"; expect_in "... saying so" "$OUT" "could not open"
expect_not_in "... without printing either password" "$OUT" "NotThePasswordAtAll12345"
expect_not_in "... or the real one" "$OUT" "$pw"
c="$(cp_layout)"; FAKE_PFX_MODE=644 chk "$c"
[ "$RC" != 0 ] && ok "a world-readable .pfx: refused" || bad "world-readable .pfx accepted"; expect_in "... saying so" "$OUT" "other users can read it"
c="$(cp_layout)"; FAKE_PFX_OWNER=bob chk "$c"
[ "$RC" != 0 ] && ok "a .pfx not owned by root: refused" || bad ".pfx owned by bob accepted"; expect_in "... saying so" "$OUT" "not owned by root"

echo "== compose.yml: the settings are passed through only when configured"
if docker compose version >/dev/null 2>&1; then
  cat > "$work/compose-base.env" <<'EOF'
SD_IMAGE_TAG=testtag
SD_PUBLIC_HOSTS=staging.example.test
SD_PUBLIC_ORIGIN=https://staging.example.test
SD_APP_DB_CONNECTION=Server=sqlserver,1433;Database=SpencerDenim_Staging;User Id=a;Password=Aa1xxxxxxxxxxxx;Encrypt=True
SD_MIGRATOR_DB_CONNECTION=Server=sqlserver,1433;Database=SpencerDenim_Staging;User Id=m;Password=Aa1yyyyyyyyyyyy;Encrypt=True
EOF
  cp "$work/compose-base.env" "$work/compose-cert.env"; printf 'SD_DP_CERT_FILE=dataprotection.pfx\nSD_DP_CERT_PASSWORD=TestOnlyNotASecret123\n' >> "$work/compose-cert.env"
  compose_config() { ( cd "$repo/deploy/app" && docker compose -p sd-staging -f compose.yml -f compose.staging.yml --env-file "$1" config 2>&1 ); }
  out="$(compose_config "$work/compose-base.env")"
  expect_in "unset: CertificatePath is empty" "$out" 'DataProtection__CertificatePath: ""'
  expect_in "unset: CertificatePassword is empty" "$out" 'DataProtection__CertificatePassword: ""'
  expect_in "the KeysPath is untouched" "$out" "DataProtection__KeysPath: /var/lib/spencer-denim/dataprotection-keys"
  out="$(compose_config "$work/compose-cert.env")"
  expect_in "set: CertificatePath points into the read-only mount" "$out" "DataProtection__CertificatePath: /etc/spencer-denim/dataprotection/dataprotection.pfx"
  expect_in "set: CertificatePassword is passed" "$out" "DataProtection__CertificatePassword: TestOnlyNotASecret123"
  expect_in "the certificate directory is mounted read-only" "$out" "target: /etc/spencer-denim/dataprotection"
else
  echo "SKIP  docker compose is not available: the pass-through cases were not run"
fi

echo
printf '%s as expected, %s not\n' "$pass" "$fail"
[ "$fail" -eq 0 ]
