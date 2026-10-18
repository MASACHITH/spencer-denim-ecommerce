#!/usr/bin/env bash
# Usage: sudo make-dp-cert.sh <production|staging> [--prompt | --password-stdin] [--days N] [--write-env]
#                                                  [--allow-existing-keyring]
#
# Makes the self-signed certificate that encrypts the ASP.NET Data Protection key ring at rest (#195, docs/15 §10
# #8, docs/16 §B). The API supports it already (DataProtection__CertificatePath / __CertificatePassword,
# src/backend/SpencerDenim.Api/Http/DataProtectionSetup.cs); compose.yml passes the two settings through when
# SD_DP_CERT_FILE / SD_DP_CERT_PASSWORD are set in config/app.env.
#
# WHEN: before the first deploy, so that the very first key is born encrypted. If the key ring already exists the
# script refuses (see --allow-existing-keyring and docs/16 §B "a key ring that already exists").
#
# WHAT IT DOES
#   * writes <app root>/config/dataprotection/dataprotection.pfx, owner root, group 1654 (the API's group), mode 640:
#     the API reads it through a read-only mount, nobody else can. The private key inside is itself encrypted with
#     the password. It never overwrites an existing .pfx: replacing the certificate makes every key ring it
#     encrypted unreadable.
#   * the password is either generated (default), or typed at a silent prompt (--prompt), or read from the first
#     line of stdin (--password-stdin, for automation). A generated password is shown ONCE, on the terminal only
#     (written to /dev/tty, so redirecting or tee-ing this script's output does not capture it). It is not put in
#     any file by this script unless you pass --write-env, which stores it in config/app.env (root, mode 600) and
#     nowhere else. It is never passed on a command line, never logged, never in the repository.
#   * with --write-env it also sets SD_DP_CERT_FILE and SD_DP_CERT_PASSWORD in config/app.env (only when both are
#     still empty); otherwise it prints the two lines to add by hand.
#
# KEEP A COPY OFF THE SERVER of the .pfx and of the password (docs/16 §B): a key ring restored from a backup without
# them is unreadable, and every admin sign-in and every PayPal secret stored in the database goes with it.
source "$(dirname "$(readlink -f "$0")")/lib.sh"
export MSYS2_ARG_CONV_EXCL='/CN='   # Git Bash only (it would rewrite -subj "/CN=..." into a Windows path); harmless elsewhere

env="${1:-}"; require_env "$env"; shift
mode=generate days=3650 write_env=0 allow_ring=0
while [ $# -gt 0 ]; do
  case "$1" in
    --prompt)                   mode=prompt ;;
    --password-stdin)           mode=stdin ;;
    --write-env)                write_env=1 ;;
    --allow-existing-keyring)   allow_ring=1 ;;
    --days)                     shift; days="${1:-}" ;;
    *) die "unknown option '$1' (see the header of this script)" ;;
  esac
  shift
done
case "$days" in ''|*[!0-9]*) die "--days needs a whole number" ;; esac
[ "$days" -ge 365 ] || die "--days must be at least 365: an expired certificate is not worth issuing"

command -v openssl >/dev/null 2>&1 || die "openssl is not installed (apt install openssl)"

if [ "$(id -u)" -ne 0 ]; then
  [ "${SD_DP_CERT_ALLOW_NONROOT:-}" = 1 ] || die "run this with sudo: the certificate must be owned by root"
  warn "not running as root: file owners are NOT set (SD_DP_CERT_ALLOW_NONROOT=1 is for tests only)"
  as_root=0
else
  as_root=1
fi

umask 077
app="$(app_root "$env")"
cert_dir="$app/config/dataprotection"
keys_dir="$app/data/dataprotection-keys"
app_env="$app/config/app.env"
pfx="$cert_dir/dataprotection.pfx"

# --- refuse the two situations where issuing a certificate does harm --------------------------------------------
shopt -s nullglob
existing=("$cert_dir"/*.pfx)
if [ "${#existing[@]}" -gt 0 ]; then
  die "$cert_dir already holds ${existing[*]##*/}. A new certificate cannot decrypt a key ring the old one encrypted, so this script never replaces one. If the old one was never used (no deploy since), move it away by hand: sudo mv ${existing[0]} /root/"
fi
ring=("$keys_dir"/key-*.xml)
if [ "${#ring[@]}" -gt 0 ]; then
  plain=0
  for f in "${ring[@]}"; do
    grep -q 'encryptedSecret' "$f" 2>/dev/null || plain=$((plain + 1))
  done
  if [ "$allow_ring" -ne 1 ]; then
    die "a key ring already exists in $keys_dir (${#ring[@]} key file(s), $plain unencrypted). A certificate only encrypts keys created AFTER it is switched on, so the existing keys would stay in clear text on disk. Read docs/16 §B 'a key ring that already exists' first, then re-run with --allow-existing-keyring if you accept that"
  fi
  warn "a key ring already exists (${#ring[@]} key file(s), $plain unencrypted): those keys stay unencrypted until you remove them (docs/16 §B)"
fi
shopt -u nullglob

if [ "$write_env" -eq 1 ]; then
  [ -f "$app_env" ] || die "$app_env does not exist: run install.sh $env first"
  if [ -n "$(env_value "$app_env" SD_DP_CERT_FILE)" ] || [ -n "$(env_value "$app_env" SD_DP_CERT_PASSWORD)" ]; then
    die "$app_env already has SD_DP_CERT_FILE or SD_DP_CERT_PASSWORD set; refusing to overwrite them (edit by hand)"
  fi
fi

# --- the password -----------------------------------------------------------------------------------------------
# What it may contain: it ends up in a compose env file, so nothing compose or a shell would treat specially.
valid_password() {
  [ "${#1}" -ge 16 ] && case "$1" in *[!A-Za-z0-9._@%+:,^~-]*) return 1 ;; esac
}
password_rules="at least 16 characters from A-Z a-z 0-9 . _ @ % + : , ^ ~ -"

# The terminal the password is shown on / typed at. /dev/tty is the controlling terminal, so ssh needs -t. The
# override exists for the test (deploy/scripts/tests), which points it at a file.
tty="${SD_DP_CERT_TTY:-/dev/tty}"
can_use_tty() { ( : >> "$tty" ) 2>/dev/null; }

case "$mode" in
  generate)
    can_use_tty || die "there is no terminal to show the generated password on. Use ssh -t, or --prompt / --password-stdin"
    pw=""
    while [ "${#pw}" -lt 32 ]; do
      pw="$(openssl rand -base64 48 | tr -dc 'A-Za-z0-9' | cut -c1-32)"
    done
    ;;
  prompt)
    can_use_tty || die "--prompt needs a terminal"
    exec 3< "$tty"      # opened once, so both answers come from the same stream
    printf 'Password for the certificate (%s): ' "$password_rules" >> "$tty"
    IFS= read -rs -u 3 pw; printf '\n' >> "$tty"
    printf 'Again: ' >> "$tty"
    IFS= read -rs -u 3 pw2; printf '\n' >> "$tty"
    exec 3<&-
    [ "$pw" = "$pw2" ] || die "the two passwords differ"
    unset pw2
    ;;
  stdin)
    IFS= read -r pw || true
    ;;
esac
valid_password "$pw" || die "the password must be $password_rules"

# --- certificate and .pfx ---------------------------------------------------------------------------------------
# The unencrypted private key exists only here, for a moment: in RAM (/dev/shm) where there is one, else in $TMPDIR.
# mktemp -d creates it mode 700, and the EXIT trap removes it on every way out.
work="$(mktemp -d /dev/shm/sd-dp-cert.XXXXXX 2>/dev/null)" || work="$(mktemp -d)"
cleanup() { [ -n "$work" ] && rm -rf "$work"; unset pw; }
trap cleanup EXIT

log "Generating a 3072-bit RSA key and a self-signed certificate valid for $days days ($env)..."
openssl req -x509 -newkey rsa:3072 -nodes -sha256 -days "$days" \
  -keyout "$work/key.pem" -out "$work/cert.pem" \
  -subj "/CN=SpencerDenim Data Protection ($env)" >"$work/req.log" 2>&1 \
  || die "openssl could not create the certificate: $(grep -v '^[.+*]*$' "$work/req.log" | tail -n 3)"

SD_DP_PW_EXPORT="$pw" openssl pkcs12 -export -name "SpencerDenim Data Protection ($env)" \
  -inkey "$work/key.pem" -in "$work/cert.pem" -out "$work/dataprotection.pfx" \
  -passout env:SD_DP_PW_EXPORT >"$work/p12.log" 2>&1 \
  || die "openssl could not write the .pfx: $(grep -v '^[.+*]*$' "$work/p12.log" | tail -n 3)"

# Prove the .pfx opens with the password before it is installed, and that the private key is inside it.
info="$(SD_DP_PW_EXPORT="$pw" openssl pkcs12 -in "$work/dataprotection.pfx" -passin env:SD_DP_PW_EXPORT -info -noout 2>&1)" \
  || die "the .pfx that was just written does not open with its own password; nothing was installed"
case "$info" in *[Kk]eybag*|*"Key bag"*) ;; *) die "the .pfx has no private key in it; nothing was installed" ;; esac

# Owner and group are set before the file is made readable to the group, so it is never readable by the wrong one.
mkdir -p "$cert_dir"
chmod 750 "$cert_dir"
install -m 600 "$work/dataprotection.pfx" "$pfx"
if [ "$as_root" -eq 1 ]; then
  chown "root:$SD_API_UID" "$cert_dir" "$pfx"
fi
chmod 640 "$pfx"

not_after="$(openssl x509 -in "$work/cert.pem" -noout -enddate | sed 's/^notAfter=//')"
fingerprint="$(openssl x509 -in "$work/cert.pem" -noout -fingerprint -sha256 | sed 's/^.*Fingerprint=//')"
pfx_sha="$(openssl dgst -sha256 -r "$pfx" | awk '{print $1}')"

# --- app.env ----------------------------------------------------------------------------------------------------
if [ "$write_env" -eq 1 ]; then
  tmp="$work/app.env.new"
  # Values travel in the environment, not in awk's argv, so the password is not on a command line.
  SD_NEW_FILE="dataprotection.pfx" SD_NEW_PW="$pw" awk '
    /^SD_DP_CERT_FILE=/     { print "SD_DP_CERT_FILE=" ENVIRON["SD_NEW_FILE"]; f = 1; next }
    /^SD_DP_CERT_PASSWORD=/ { print "SD_DP_CERT_PASSWORD=" ENVIRON["SD_NEW_PW"]; p = 1; next }
    { print }
    END {
      if (!f) print "SD_DP_CERT_FILE=" ENVIRON["SD_NEW_FILE"]
      if (!p) print "SD_DP_CERT_PASSWORD=" ENVIRON["SD_NEW_PW"]
    }' "$app_env" > "$tmp"
  cat "$tmp" > "$app_env"      # overwrite in place: the file keeps its owner and mode 600
  log "Set SD_DP_CERT_FILE and SD_DP_CERT_PASSWORD in $app_env."
fi

# --- report ------------------------------------------------------------------------------------------------------
if [ "$as_root" -eq 1 ]; then who="owner root, group $SD_API_UID"; else who="owner NOT set (test run)"; fi
log "Certificate written: $pfx ($who, mode 640)"
log "  valid until   : $not_after"
log "  SHA-256 of the certificate : $fingerprint"
log "  SHA-256 of the .pfx file   : $pfx_sha   (compare this with your off-server copy)"

if [ "$mode" = generate ]; then
  {
    printf '\n'
    printf '  ======================= SHOWN ONCE, ON THIS TERMINAL ONLY =======================\n'
    printf '   Data Protection certificate password (%s):\n\n' "$env"
    printf '       %s\n\n' "$pw"
    printf '   Put it in your password manager NOW, together with a copy of the .pfx (docs/16 B).\n'
    printf '   It is not stored anywhere by this script%s.\n' "$([ "$write_env" -eq 1 ] && echo ' except config/app.env on this server' || true)"
    printf '  ==================================================================================\n\n'
  } >> "$tty"
fi

if [ "$write_env" -eq 0 ]; then
  log "Add these two lines to $app_env (then run check-env.sh $env):"
  log "    SD_DP_CERT_FILE=dataprotection.pfx"
  log "    SD_DP_CERT_PASSWORD=<the password>"
fi
log "Next: $app/scripts/check-env.sh $env, then deploy. After the first API start check that every key file is encrypted:"
log "    sudo bash -c 'grep -L encryptedSecret \"\$1\"/key-*.xml' _ $keys_dir    # lists key files that are NOT encrypted: must print nothing"
if [ "$mode" = generate ]; then
  log "Close or clear this terminal's scrollback once the password is saved."
fi
