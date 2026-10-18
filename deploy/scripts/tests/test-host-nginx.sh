#!/usr/bin/env bash
# Tests the host-nginx templates (deploy/host-nginx/*.template) in a REAL nginx 1.24, the version Ubuntu 24.04 ships,
# and runs verify-public.sh against it from a Linux container (#195). Optional: needs docker, node and openssl, and
# pulls nginx:1.24-alpine and alpine:3.20 on first use (apk then fetches bash, curl, openssl inside the throwaway container).
#
# What it shows:
#   * `nginx -t` accepts: staging + production; plus the catch-all; plus the OPTIONAL www block switched on; plus the
#     OPTIONAL basic-auth lines switched on. A second default_server makes `nginx -t` FAIL (why the catch-all is conditional).
#   * WITHOUT the catch-all an unknown host is answered by the shop (verify-public: unknown-host-https FAIL), the failure
#     described in #187 section 4; WITH it the TLS handshake is refused and the check passes.
#   * staging answers `Disallow: /` and X-Robots-Tag noindex whatever the storefront behind it says; production adds neither.
#   * with `server_tokens off` every check passes on both environments; the stock setting gives exactly one WARN (Server header).
#   * behind basic auth, verify-public explains the 401 and passes once SD_VERIFY_BASIC_AUTH is given.
#
# The "storefront behind nginx" is `mock-shop.mjs --upstream` on the host (reached through host.docker.internal); the
# certificates come from a throwaway CA made here. Nothing is installed on the host and the containers are removed at the end.
#
#   bash deploy/scripts/tests/test-host-nginx.sh
export MSYS2_ARG_CONV_EXCL='/CN='
# Git Bash only: docker.exe is handed Windows paths below (cygpath -w) and must not have its arguments rewritten; openssl and
# node, on the other hand, need the rewriting, so the switch is set for docker alone.
docker() { MSYS_NO_PATHCONV=1 command docker "$@"; }
here="$(cd "$(dirname "$0")" && pwd)"
tdir="$(cd "$here/../../../deploy/host-nginx" && pwd)"
for t in docker node openssl; do command -v "$t" >/dev/null 2>&1 || { echo "SKIP: this test needs $t"; exit 0; }; done
docker version --format '{{.Server.Version}}' >/dev/null 2>&1 || { echo "SKIP: the docker engine is not running"; exit 0; }

work="$(mktemp -d)"
net="sd195-hostnginx-$$"
ngx="sd195-hostnginx-nginx-$$"
pids=""
cleanup() { docker rm -f "$ngx" >/dev/null 2>&1; docker network rm "$net" >/dev/null 2>&1; for p in $pids; do kill "$p" 2>/dev/null; done; rm -rf "$work"; }
trap cleanup EXIT
winpath() { if command -v cygpath >/dev/null 2>&1; then cygpath -w "$1"; else printf '%s' "$1"; fi; }
image=nginx:1.24-alpine
fails=0
ok()   { printf 'ok    %s\n' "$*"; }
bad()  { printf 'FAIL  %s\n' "$*"; fails=$((fails + 1)); }

# ---- certificates: a throwaway CA and one certificate for the three names -------------------------------------------------
pki="$work/pki"; mkdir -p "$pki/le/live/staging.sd195.test" "$pki/le/live/shop.sd195.test"
openssl req -x509 -newkey rsa:2048 -nodes -keyout "$pki/ca.key" -out "$pki/ca.pem" -days 30 -subj "/CN=sd195 host-nginx test CA" >/dev/null 2>&1
printf 'subjectAltName=DNS:staging.sd195.test,DNS:shop.sd195.test,DNS:www.shop.sd195.test\nbasicConstraints=CA:FALSE\n' > "$pki/leaf.ext"
openssl req -newkey rsa:2048 -nodes -keyout "$pki/leaf.key" -out "$pki/leaf.csr" -subj "/CN=shop.sd195.test" >/dev/null 2>&1
openssl x509 -req -in "$pki/leaf.csr" -CA "$pki/ca.pem" -CAkey "$pki/ca.key" -CAcreateserial -out "$pki/leaf.pem" -days 30 -extfile "$pki/leaf.ext" >/dev/null 2>&1
for h in staging.sd195.test shop.sd195.test; do cp "$pki/leaf.pem" "$pki/le/live/$h/fullchain.pem"; cp "$pki/leaf.key" "$pki/le/live/$h/privkey.pem"; done
openssl dhparam -out "$pki/le/ssl-dhparams.pem" 1024 >/dev/null 2>&1
printf 'ssl_session_cache shared:le_nginx_SSL:1m;\nssl_session_timeout 1440m;\nssl_protocols TLSv1.2 TLSv1.3;\n' > "$pki/le/options-ssl-nginx.conf"
mkdir -p "$work/webroot"
printf 'uatuser:%s\n' "$(openssl passwd -apr1 'Test-Only-Pw-1')" > "$work/htpasswd"

# ---- the "application stack behind nginx": two upstream mocks on the host -------------------------------------------------
start_up() {   # name host origin extra...  -> port
  local name="$1" h="$2" o="$3"; shift 3
  node "$here/mock-shop.mjs" --upstream --host "$h" --origin "$o" --bind 0.0.0.0 --ready "$work/up-$name.json" "$@" >"$work/up-$name.log" 2>&1 &
  pids="$pids $!"
  local i=0; while [ ! -s "$work/up-$name.json" ] && [ "$i" -lt 100 ]; do sleep 0.1; i=$((i + 1)); done
  sed -n 's/.*"http":\([0-9]*\).*/\1/p' "$work/up-$name.json"
}
port_s="$(start_up staging staging.sd195.test https://staging.sd195.test)"
port_p="$(start_up production shop.sd195.test https://shop.sd195.test --www)"
[ -n "$port_s" ] && [ -n "$port_p" ] || { echo "the upstream mocks did not start"; exit 2; }

# ---- render the templates exactly as the run sheet does (docs/16 B12-e), changing only the upstream address ------------------
render() {   # template-file host port -> stdout
  sed -e "s/__SERVER_NAME__/$2/g" -e "s/__PRIMARY_HOST__/$2/g" -e "s/server 127.0.0.1:380[0-9][0-9];/server host.docker.internal:$3;/" "$1"
}
mkdir -p "$work/sites"
render "$tdir/spencer-denim-staging.conf.template" staging.sd195.test "$port_s" > "$work/sites/spencer-denim-staging.conf"
render "$tdir/spencer-denim-production.conf.template" shop.sd195.test "$port_p" > "$work/sites/spencer-denim-production.conf"
grep -n '__[A-Z_]*__' "$work/sites/"*.conf >/dev/null && bad "a placeholder is left in a rendered template"
catchall="$tdir/spencer-denim-00-default.conf.template"

mkdir -p "$work/e-plain" "$work/e-catch" "$work/e-dup" "$work/e-www" "$work/e-auth"
cp "$work/sites/"*.conf "$work/e-plain/"
cp "$work/sites/"*.conf "$work/e-catch/"; cp "$catchall" "$work/e-catch/spencer-denim-00-default.conf"
cp "$work/e-catch/"* "$work/e-dup/"; printf 'server { listen 80 default_server; server_name _; return 404; }\n' > "$work/e-dup/default"
cp "$work/e-catch/"* "$work/e-www/"
# the OPTIONAL www block, switched on the way docs/16 D3 says
render "$tdir/spencer-denim-production.conf.template" shop.sd195.test "$port_p" \
  | awk '/^# server \{/ { on = 1 } on { sub(/^# ?/, "") } { print }' | sed 's/<domain>/shop.sd195.test/g' > "$work/e-www/spencer-denim-production.conf"
cp "$work/e-catch/"* "$work/e-auth/"
sed -i -e 's/^    #   auth_basic           "UAT";/    auth_basic           "UAT";/' \
       -e 's|^    #   auth_basic_user_file /etc/nginx/spencer-denim-staging.htpasswd;|    auth_basic_user_file /etc/nginx/spencer-denim-staging.htpasswd;|' "$work/e-auth/spencer-denim-staging.conf"
grep -q '^    auth_basic ' "$work/e-auth/spencer-denim-staging.conf" || bad "the basic-auth lines could not be switched on (template text changed?)"

printf 'worker_processes 1;\nerror_log /var/log/nginx/error.log warn;\npid /tmp/nginx.pid;\nevents { worker_connections 64; }\nhttp {\n    include /etc/nginx/mime.types;\n    default_type application/octet-stream;\n    include /etc/nginx/sites-enabled/*;\n}\n' > "$work/nginx.conf"
printf 'worker_processes 1;\nerror_log /var/log/nginx/error.log warn;\npid /tmp/nginx.pid;\nevents { worker_connections 64; }\nhttp {\n    include /etc/nginx/mime.types;\n    default_type application/octet-stream;\n    server_tokens off;\n    include /etc/nginx/sites-enabled/*;\n}\n' > "$work/nginx-notokens.conf"

mounts() {   # enabled-dir main-conf
  printf -- '-v %s -v %s -v %s -v %s -v %s' \
    "$(winpath "$work/$2"):/etc/nginx/nginx.conf:ro" "$(winpath "$work/$1"):/etc/nginx/sites-enabled:ro" \
    "$(winpath "$pki/le"):/etc/letsencrypt:ro" "$(winpath "$work/webroot"):/var/www/certbot:ro" "$(winpath "$work/htpasswd"):/etc/nginx/spencer-denim-staging.htpasswd:ro"
}
nginx_t() { # enabled-dir -> exit status of nginx -t
  # shellcheck disable=SC2046
  docker run --rm --add-host host.docker.internal:host-gateway $(mounts "$1" nginx.conf) "$image" nginx -t >"$work/t.out" 2>&1
}

echo "== nginx -t"
nginx_t e-plain && ok "staging + production templates parse" || { bad "staging + production templates do not parse"; cat "$work/t.out"; }
nginx_t e-catch && ok "... plus the catch-all" || { bad "catch-all does not parse"; cat "$work/t.out"; }
nginx_t e-www   && ok "... plus the optional www block switched on" || { bad "www block does not parse"; cat "$work/t.out"; }
nginx_t e-auth  && ok "... plus the optional basic-auth lines switched on" || { bad "basic-auth lines do not parse"; cat "$work/t.out"; }
if nginx_t e-dup; then bad "a second default_server was accepted (the run sheet says it is refused)"; elif grep -q 'duplicate default server' "$work/t.out"; then ok "a second default_server is refused: 'a duplicate default server'"; else bad "e-dup failed for another reason"; cat "$work/t.out"; fi

# ---- live: nginx in a container, verify-public.sh from an alpine container on the same network ---------------------------------
docker network create "$net" >/dev/null 2>&1 || { echo "could not create a docker network"; exit 2; }
printf '#!/bin/sh\napk add --no-cache bash curl openssl >/dev/null 2>&1 || { echo "apk could not install bash/curl/openssl (no network?)"; exit 99; }\nexec bash /scripts/verify-public.sh "$@"\n' > "$work/verify.sh"
start_nginx() {   # enabled-dir main-conf -> prints the container's IP
  docker rm -f "$ngx" >/dev/null 2>&1
  # shellcheck disable=SC2046
  docker run -d --name "$ngx" --network "$net" --add-host host.docker.internal:host-gateway $(mounts "$1" "$2") "$image" >/dev/null 2>&1 || return 1
  sleep 2
  docker inspect -f '{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}' "$ngx"
}
verify() {   # extra-docker-args... -- env origin args...   (the table of verify-public.sh on stdout)
  local dargs=()
  while [ "$1" != -- ]; do dargs+=("$1"); shift; done; shift
  docker run --rm --network "$net" -e NO_COLOR=1 "${dargs[@]}" \
    -v "$(winpath "$here/..")":/scripts:ro -v "$(winpath "$pki/ca.pem")":/pki/ca.pem:ro -v "$(winpath "$work/verify.sh")":/verify.sh:ro \
    alpine:3.20 sh /verify.sh "$@" 2>&1
}
states() { printf '%s\n' "$1" | sed -n '/^RES/,$p' | grep -E '^(PASS|WARN|FAIL|SKIP) '; }
expect() {   # label output id STATE
  local got; got="$(printf '%s\n' "$2" | sed -n '/^RES/,$p' | grep -E "^(PASS|WARN|FAIL|SKIP) +$3 " | head -n 1 | awk '{print $1}')"
  [ "$got" = "$4" ] && ok "$1: $3 is $4" || bad "$1: $3 is '${got:-absent}', wanted $4"
}
all_pass_except() {   # label output [id:STATE ...]
  local label="$1" out="$2"; shift 2
  local rest; rest="$(states "$out" | grep -v '^PASS ')"
  local line extra=""
  while IFS= read -r line; do
    [ -n "$line" ] || continue
    local s i; s="$(printf '%s' "$line" | awk '{print $1}')"; i="$(printf '%s' "$line" | awk '{print $2}')"
    case " $* " in *" $i:$s "*) ;; *) extra="$extra $s/$i" ;; esac
  done <<EOF
$rest
EOF
  [ -z "$extra" ] && ok "$label: everything else PASSes" || bad "$label: unexpected non-PASS lines:$extra"
}

echo "== live nginx, WITHOUT the catch-all (stock server_tokens)"
ip="$(start_nginx e-plain nginx.conf)" || { bad "could not start nginx"; exit 1; }
out="$(verify -- staging https://staging.sd195.test --resolve "$ip" --cacert /pki/ca.pem)"
expect "no catch-all" "$out" unknown-host-https FAIL
expect "no catch-all" "$out" unknown-host-http WARN
expect "no catch-all" "$out" robots PASS
expect "no catch-all" "$out" x-robots-tag PASS
expect "no catch-all" "$out" server-header WARN
all_pass_except "no catch-all" "$out" unknown-host-https:FAIL unknown-host-http:WARN server-header:WARN

echo "== live nginx, WITH the catch-all, server_tokens off"
ip="$(start_nginx e-catch nginx-notokens.conf)" || { bad "could not start nginx"; exit 1; }
out="$(verify -- staging https://staging.sd195.test --resolve "$ip" --cacert /pki/ca.pem)"
expect "staging" "$out" unknown-host-https PASS
expect "staging" "$out" robots PASS
expect "staging" "$out" x-robots-tag PASS
all_pass_except "staging" "$out"
out="$(verify -- production https://shop.sd195.test --resolve "$ip" --cacert /pki/ca.pem)"
expect "production" "$out" robots PASS
expect "production" "$out" x-robots-tag PASS
all_pass_except "production" "$out"

echo "== production with the www block switched on"
ip="$(start_nginx e-www nginx-notokens.conf)" || { bad "could not start nginx"; exit 1; }
out="$(verify -- production https://shop.sd195.test --resolve "$ip" --cacert /pki/ca.pem --www-redirect)"
expect "www" "$out" www-redirect PASS
all_pass_except "www" "$out"

echo "== staging behind basic auth"
ip="$(start_nginx e-auth nginx-notokens.conf)" || { bad "could not start nginx"; exit 1; }
out="$(verify -- staging https://staging.sd195.test --resolve "$ip" --cacert /pki/ca.pem)"
expect "auth, no credentials" "$out" https-200 FAIL
printf '%s\n' "$out" | grep -q 'SD_VERIFY_BASIC_AUTH' && ok "the 401 is explained (names SD_VERIFY_BASIC_AUTH)" || bad "the 401 is not explained"
out="$(verify -e SD_VERIFY_BASIC_AUTH='uatuser:Test-Only-Pw-1' -- staging https://staging.sd195.test --resolve "$ip" --cacert /pki/ca.pem)"
all_pass_except "auth, with credentials" "$out"

echo
[ "$fails" -eq 0 ] && echo "host-nginx: all as expected" || echo "host-nginx: $fails problem(s)"
[ "$fails" -eq 0 ]
