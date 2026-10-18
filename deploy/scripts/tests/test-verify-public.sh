#!/usr/bin/env bash
# Tests deploy/scripts/verify-public.sh against deploy/scripts/tests/mock-shop.mjs (#195).
#
# For every check it starts a mock environment that is right (all PASS, exit 0) and one that is deliberately wrong in
# exactly one way (that check goes FAIL or WARN, exit 1 for FAIL), then a few certificate cases. Needs bash, curl,
# openssl and node (the same as the repository's own tooling). Nothing leaves this machine: the mock binds 127.0.0.1
# on ports the operating system picks, and the "domain" shop.test is pinned to 127.0.0.1 with curl --resolve.
#
#   bash deploy/scripts/tests/test-verify-public.sh            # all cases (cases run TEST_JOBS=6 at a time)
#   bash deploy/scripts/tests/test-verify-public.sh hsts       # only the cases whose name contains "hsts"
export MSYS2_ARG_CONV_EXCL='/CN='   # Git Bash only: keep openssl's -subj "/CN=..." from being turned into a path
here="$(cd "$(dirname "$0")" && pwd)"
script="$here/../verify-public.sh"
only="${1:-}"
jobs_max="${TEST_JOBS:-6}"
work="$(mktemp -d)"
cleanup() { kill $(jobs -p) 2>/dev/null; rm -rf "$work"; }
trap cleanup EXIT

for t in node curl openssl; do command -v "$t" >/dev/null 2>&1 || { echo "this test needs $t" >&2; exit 2; }; done
export NO_COLOR=1

# ---- a throwaway CA and the certificates the cases need ---------------------------------------------------------------
pki="$work/pki"; mkdir -p "$pki"
openssl req -x509 -newkey rsa:2048 -nodes -keyout "$pki/ca.key" -out "$pki/ca.pem" -days 30 -subj "/CN=sd195 test CA" >/dev/null 2>&1 \
  || { echo "could not create the test CA" >&2; exit 2; }
make_leaf() {   # name cn days sans
  local name="$1" cn="$2" days="$3" sans="$4"
  openssl req -newkey rsa:2048 -nodes -keyout "$pki/$name.key" -out "$pki/$name.csr" -subj "/CN=$cn" >/dev/null 2>&1
  printf 'subjectAltName=%s\nbasicConstraints=CA:FALSE\n' "$sans" > "$pki/$name.ext"
  openssl x509 -req -in "$pki/$name.csr" -CA "$pki/ca.pem" -CAkey "$pki/ca.key" -CAcreateserial -out "$pki/$name.pem" \
    -days "$days" -extfile "$pki/$name.ext" >/dev/null 2>&1
}
make_leaf good shop.test 60 "DNS:shop.test,DNS:www.shop.test"
make_leaf soon shop.test 10 "DNS:shop.test,DNS:www.shop.test"
make_leaf wronghost other.test 60 "DNS:other.test"
# Expired: valid for no time at all. (If this openssl refuses -days 0 the case reports SKIP.)
make_leaf expired shop.test 0 "DNS:shop.test"
# Self-signed: not issued by the CA the test trusts.
openssl req -x509 -newkey rsa:2048 -nodes -keyout "$pki/self.key" -out "$pki/self.pem" -days 60 -subj "/CN=shop.test" \
  -addext "subjectAltName=DNS:shop.test" >/dev/null 2>&1

report() { printf '%s\n' "$*"; }

# case_body N NAME ENV CERT "FAULTS" "EXPECTED" [extra verify-public args]
#   EXPECTED is a space-separated list of id:STATE (the state that check must have), ALL (every check PASS) and exit:N.
case_body() {
  local n="$1" name="$2" envname="$3" cert="$4" faults="$5" expected="$6"; shift 6
  local extra=("$@") mock_pid="" ready="$work/ready.$n.json"
  [ -s "$pki/$cert.pem" ] || { report "SKIP  $name (certificate '$cert' could not be made with this openssl)"; return 0; }
  local mockargs=(--env "$envname" --host shop.test --cert "$pki/$cert.pem" --key "$pki/$cert.key" --ready "$ready")
  [ -n "$faults" ] && mockargs+=(--faults "$faults")
  case " ${extra[*]} " in *" --www-redirect "*) mockargs+=(--www) ;; esac
  node "$here/mock-shop.mjs" "${mockargs[@]}" >"$work/mock.$n.log" 2>&1 &
  mock_pid=$!
  local i=0
  while [ ! -s "$ready" ] && [ "$i" -lt 100 ]; do sleep 0.1; i=$((i + 1)); done
  if [ ! -s "$ready" ]; then report "FAIL  $name: the mock did not start: $(head -n 3 "$work/mock.$n.log")"; kill "$mock_pid" 2>/dev/null; return 1; fi
  local hp pp
  hp="$(sed -n 's/.*"https":\([0-9]*\).*/\1/p' "$ready")"; pp="$(sed -n 's/.*"http":\([0-9]*\).*/\1/p' "$ready")"
  grep -q sd195-mock-shop "$ready" || { report "FAIL  $name: that is not my mock on port $hp"; kill "$mock_pid" 2>/dev/null; return 1; }

  local out rc
  out="$(bash "$script" "$envname" "https://shop.test:$hp" --resolve 127.0.0.1 --cacert "$pki/ca.pem" \
        --http-origin "http://shop.test:$pp" --timeout 8 "${extra[@]}" 2>&1)"
  rc=$?
  kill "$mock_pid" 2>/dev/null; wait "$mock_pid" 2>/dev/null

  local bad="" want id state got
  for want in $expected; do
    id="${want%%:*}"; state="${want##*:}"
    if [ "$id" = exit ]; then
      [ "$rc" = "$state" ] || bad="$bad exit=$rc(wanted $state)"
    elif [ "$id" = ALL ]; then
      if printf '%s\n' "$out" | grep -E '^(FAIL|WARN|SKIP) ' >/dev/null; then
        bad="$bad not-all-PASS($(printf '%s\n' "$out" | grep -E '^(FAIL|WARN|SKIP) ' | awk '{print $2}' | sort -u | tr '\n' ','))"
      fi
    else
      got="$(printf '%s\n' "$out" | grep -E "^(PASS|WARN|FAIL|SKIP) +$id " | head -n 1 | awk '{print $1}')"
      [ "$got" = "$state" ] || bad="$bad $id=${got:-absent}(wanted $state)"
    fi
  done
  if [ -z "$bad" ]; then
    report "ok    $name  [$expected]"
    return 0
  fi
  report "FAIL  $name:$bad"
  printf '%s\n' "$out" | sed 's/^/        | /' | head -n 60
  return 1
}

n=0
run_case() {
  local name="$1"
  [ -z "$only" ] || case "$name" in *"$only"*) ;; *) return 0 ;; esac
  n=$((n + 1))
  while [ "$(jobs -rp | wc -l)" -ge "$jobs_max" ]; do sleep 0.3; done
  ( case_body "$n" "$@" > "$work/out.$n" 2>&1; echo $? > "$work/status.$n" ) &
}

# ---- GREEN: a right environment passes every check ------------------------------------------------------------------
run_case "green-production" production good "" "ALL exit:0 tls-cert:PASS"
run_case "green-staging"    staging    good "" "ALL exit:0 tls-cert:PASS"
run_case "green-www-redirect" production good "" "www-redirect:PASS ALL exit:0" --www-redirect

# ---- RED: one fault, one check -------------------------------------------------------------------------------------------
run_case "http-302"            production good "http-302"            "http-redirect:FAIL exit:1"
run_case "http-wrong-location" production good "http-wrong-location" "http-redirect:FAIL exit:1"
run_case "no-http"             production good "no-http"             "http-redirect:FAIL exit:1"
run_case "no-hsts"             production good "no-hsts"             "hsts:FAIL exit:1"
run_case "hsts-short"          production good "hsts-short"          "hsts:WARN exit:0"
run_case "no-ssr"              production good "no-ssr"              "ssr:FAIL exit:1"
run_case "ssr-twice"           production good "ssr-twice"           "ssr:FAIL exit:1"
run_case "robots-prod-disallow" production good "robots-prod-disallow" "robots:FAIL exit:1"
run_case "robots-prod-wrong-sitemap" production good "robots-prod-wrong-sitemap" "robots:FAIL exit:1"
run_case "robots-staging-allows" staging good "robots-staging-allows" "robots:FAIL exit:1"
run_case "sitemap-wrong-host"  production good "sitemap-wrong-host"  "sitemap:FAIL exit:1"
run_case "sitemap-404"         production good "sitemap-404"         "sitemap:FAIL exit:1"
run_case "no-noindex-staging"  staging    good "no-noindex"          "x-robots-tag:FAIL exit:1"
run_case "prod-noindex"        production good "prod-noindex"        "x-robots-tag:FAIL exit:1"
run_case "admin-500"           production good "admin-500"           "admin:FAIL exit:1"
run_case "departments-html"    production good "departments-html"    "api-departments:FAIL exit:1"
run_case "departments-500"     production good "departments-500"     "api-departments:FAIL exit:1"
run_case "unknown-host-200"    production good "unknown-host-200"    "unknown-host-https:FAIL unknown-host-http:FAIL exit:1"
run_case "order-500"           production good "order-500"           "order-token:FAIL exit:1"
run_case "reset-500"           production good "reset-500"           "reset-password:FAIL exit:1"
run_case "pageview-200"        production good "pageview-200"        "page-view:FAIL exit:1"
run_case "server-version"      production good "server-version"      "server-header:WARN exit:0"
run_case "powered-by"          production good "powered-by"          "server-header:FAIL exit:1"
run_case "trace-404"           production good "trace-404"           "not-found:FAIL exit:1"
run_case "soft-404"            production good "soft-404"            "not-found:FAIL exit:1"
run_case "internal-open"       production good "internal-open"       "not-exposed:FAIL exit:1"
run_case "ready-open"          production good "ready-open"          "not-exposed:FAIL exit:1"
run_case "cookie-insecure"     production good "cookie-insecure"     "cookies:FAIL exit:1"
run_case "cookie-nohttponly"   production good "cookie-nohttponly"   "cookies:WARN exit:0"
run_case "www-no-redirect"     production good "www-no-redirect"     "www-redirect:FAIL exit:1" --www-redirect

# ---- certificates ----------------------------------------------------------------------------------------------------
run_case "cert-expiring-soon"  production soon      "" "tls-cert:WARN exit:0"
run_case "cert-wrong-host"     production wronghost "" "tls-cert:FAIL https-200:FAIL hsts:SKIP exit:1"
run_case "cert-self-signed"    production self      "" "tls-cert:FAIL https-200:FAIL exit:1"
run_case "cert-expired"        production expired   "" "tls-cert:FAIL exit:1"
run_case "cert-wrong-host-insecure" production wronghost "" "tls-cert:WARN https-200:PASS hsts:PASS ssr:PASS exit:0" --insecure

wait
passed=0 failed=0 i=1
while [ "$i" -le "$n" ]; do
  cat "$work/out.$i"
  if [ "$(cat "$work/status.$i" 2>/dev/null)" = 0 ]; then passed=$((passed + 1)); else failed=$((failed + 1)); fi
  i=$((i + 1))
done
report ""
report "$passed case(s) as expected, $failed not"
[ "$failed" -eq 0 ]
