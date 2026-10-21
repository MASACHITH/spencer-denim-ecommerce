#!/usr/bin/env bash
# Usage: verify-public.sh <staging|production> <https://host[:port]> [options]
#
# Read-only check of a deployed environment from OUTSIDE (#195, docs/16 §C). Run it from any machine that can reach
# the site, including a laptop with Git Bash: it needs only bash, curl, openssl and standard utilities (no jq, no
# GNU-only flags other than `date -d`, which it can do without). It changes nothing on the server: every request is a
# GET, plus ONE POST to the page-view endpoint with an address that is not a storefront path, which the API accepts
# (204) and does not record. Every request carries a bot User-Agent, so the visits it makes are counted as bot views.
#
# Options
#   --resolve IP        connect to IP for the host (before DNS is changed), like curl --resolve. The certificate and the
#                       Host header are still those of the real name.
#   --insecure          do not verify the certificate chain (a self-signed or not-yet-issued certificate). The chain
#                       check is then reported as WARN, everything else still runs.
#   --cacert FILE       trust the CA in FILE (a private CA, or the test CA of deploy/scripts/tests)
#   --http-origin URL   where plain HTTP is answered. Default http://<host> (port 80). Needed only when the HTTPS origin
#                       has a non-standard port. "none" skips the HTTP checks.
#   --www-redirect      also check that https://www.<host>/ answers 301 to the origin (docs/16 §D, optional template block)
#   --timeout SEC       limit per request (default 20)
#   -h, --help
# Environment: SD_VERIFY_BASIC_AUTH=user:password  when UAT is behind HTTP basic auth (kept out of the process list).
#
# Exit status 1 if any check FAILs. WARN does not fail the run but names something to look at.
set -u
export LC_ALL=C

usage() { sed -n '2,23p' "$0"; exit "${1:-0}"; }

[ $# -ge 1 ] || usage 2
case "${1:-}" in -h|--help) usage 0 ;; esac
[ $# -ge 2 ] || usage 2
envname="$1"; origin="$2"; shift 2
case "$envname" in staging|production) ;; *) echo "environment must be staging or production" >&2; exit 2 ;; esac
origin="${origin%/}"
case "$origin" in
  https://*) ;;
  *) echo "the origin must be https://host[:port] (got '$origin')" >&2; exit 2 ;;
esac

resolve_ip="" insecure=0 cacert="" http_origin="" www_check=0 timeout_s=20
while [ $# -gt 0 ]; do
  case "$1" in
    --resolve)       shift; resolve_ip="${1:-}" ;;
    --insecure)      insecure=1 ;;
    --cacert)        shift; cacert="${1:-}" ;;
    --http-origin)   shift; http_origin="${1:-}" ;;
    --www-redirect)  www_check=1 ;;
    --timeout)       shift; timeout_s="${1:-20}" ;;
    -h|--help)       usage 0 ;;
    *) echo "unknown option '$1'" >&2; exit 2 ;;
  esac
  shift
done
case "$timeout_s" in ''|*[!0-9]*) echo "--timeout needs whole seconds" >&2; exit 2 ;; esac

hostport="${origin#https://}"
case "$hostport" in
  *:*) host="${hostport%%:*}"; port="${hostport##*:}" ;;
  *)   host="$hostport"; port=443 ;;
esac
if [ -z "$http_origin" ]; then
  if [ "$port" = 443 ]; then http_origin="http://$host"; else http_origin="none"; fi
fi
http_port=80
case "$http_origin" in
  http://*:*) http_port="${http_origin##*:}" ;;
esac

command -v curl >/dev/null 2>&1 || { echo "curl is required" >&2; exit 2; }
command -v openssl >/dev/null 2>&1 || { echo "openssl is required" >&2; exit 2; }

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

# A bot User-Agent on purpose: the storefront records the pages it renders, and UserAgentClassifier counts anything
# matching /bot\)/ as a bot view, which every visitor figure excludes. Do not "improve" it into a browser string.
ua='spencer-denim-verify-public/1.0 (deployment check; bot)'

curl_args=(-sS --max-time "$timeout_s" -A "$ua")
[ "$insecure" -eq 1 ] && curl_args+=(-k)
[ -n "$cacert" ] && curl_args+=(--cacert "$cacert")
# Windows curl uses Schannel, which tries to reach an OCSP/CRL server and fails offline. Other builds ignore the flag.
if curl --version 2>/dev/null | grep -qi schannel; then curl_args+=(--ssl-no-revoke); fi
if [ -n "$resolve_ip" ]; then
  curl_args+=(--resolve "$host:$port:$resolve_ip")
  [ "$http_origin" != none ] && curl_args+=(--resolve "$host:$http_port:$resolve_ip")
fi
if [ -n "${SD_VERIFY_BASIC_AUTH:-}" ]; then
  # Through a mode-600 curl config file, not "-u" on the command line, so the password is not in the process list.
  ( umask 077; printf 'user = "%s"\n' "$(printf '%s' "$SD_VERIFY_BASIC_AUTH" | sed 's/\\/\\\\/g; s/"/\\"/g')" > "$tmp/auth.cfg" )
  curl_args+=(-K "$tmp/auth.cfg")
fi

# ---------------------------------------------------------------------------------------------------------------
# results
# ---------------------------------------------------------------------------------------------------------------
if [ -t 1 ] && [ -z "${NO_COLOR:-}" ]; then
  c_pass=$'\033[32m' c_warn=$'\033[33m' c_fail=$'\033[31m' c_off=$'\033[0m'
else
  c_pass="" c_warn="" c_fail="" c_off=""
fi
res_id=() res_state=() res_text=()
n_pass=0 n_warn=0 n_fail=0 n_skip=0
record() {   # state id text
  local state="$1" id="$2" text="$3" col=""
  res_id+=("$id"); res_state+=("$state"); res_text+=("$text")
  case "$state" in
    PASS) n_pass=$((n_pass + 1)); col="$c_pass" ;;
    WARN) n_warn=$((n_warn + 1)); col="$c_warn" ;;
    FAIL) n_fail=$((n_fail + 1)); col="$c_fail" ;;
    SKIP) n_skip=$((n_skip + 1)) ;;
  esac
  printf '%s%-4s%s  %-18s %s\n' "$col" "$state" "$c_off" "$id" "$text"
}

# ---------------------------------------------------------------------------------------------------------------
# requests
# ---------------------------------------------------------------------------------------------------------------
seq=0
CODE="" RC=0 H="" B="" REMOTE="" LOC="" ERR=""
fetch() {   # fetch METHOD URL [curl args...] -> CODE RC H (headers file) B (body file) REMOTE LOC ERR
  local method="$1" url="$2" out; shift 2
  seq=$((seq + 1)); H="$tmp/h$seq"; B="$tmp/b$seq"
  : > "$H"; : > "$B"
  out="$(curl "${curl_args[@]}" -X "$method" -D "$H" -o "$B" \
        -w '%{http_code} %{remote_ip} %{redirect_url}' "$@" "$url" 2>"$tmp/e$seq")"
  RC=$?
  CODE="${out%% *}"; out="${out#* }"; REMOTE="${out%% *}"; LOC="${out#* }"
  ERR=""; IFS= read -r ERR < "$tmp/e$seq" || true; ERR="${ERR%$'\r'}"
  [ "$REMOTE" = "$LOC" ] && LOC=""
}
# Header lookups are plain bash (no tr/awk process per call): on Windows every process start costs ~150 ms and this
# script looks at hundreds of headers. ${var,,} needs bash 4; macOS's bash 3.2 falls back to tr.
if [ "${BASH_VERSINFO[0]:-3}" -ge 4 ]; then
  lc_set() { printf -v "$1" '%s' "${2,,}"; }
  lower() { printf '%s' "${1,,}"; }
else
  lc_set() { printf -v "$1" '%s' "$(printf '%s' "$2" | tr 'A-Z' 'a-z')"; }
  lower() { printf '%s' "$1" | tr 'A-Z' 'a-z'; }
fi
hdr() {   # hdr FILE NAME -> the last value of the header called NAME (case-insensitive), or nothing
  local want line k kl v out=""
  lc_set want "$2"
  while IFS= read -r line || [ -n "$line" ]; do
    line="${line%$'\r'}"
    case "$line" in
      *:*) k="${line%%:*}"; v="${line#*:}"; lc_set kl "$k"
           if [ "$kl" = "$want" ]; then v="${v#"${v%%[![:space:]]*}"}"; out="$v"; fi ;;
    esac
  done < "$1"
  printf '%s' "$out"
}
hdr_all() {   # hdr_all FILE NAME -> every value of that header, one per line
  local want line k kl v
  lc_set want "$2"
  while IFS= read -r line || [ -n "$line" ]; do
    line="${line%$'\r'}"
    case "$line" in
      *:*) k="${line%%:*}"; v="${line#*:}"; lc_set kl "$k"
           if [ "$kl" = "$want" ]; then v="${v#"${v%%[![:space:]]*}"}"; printf '%s\n' "$v"; fi ;;
    esac
  done < "$1"
}
body_has() { grep -q -- "$1" "$B"; }
curl_why() { if [ "$RC" -ne 0 ]; then printf 'curl error %s: %s' "$RC" "$ERR"; else printf 'HTTP %s' "$CODE"; fi; }

trace_re='(Exception|[Ss]tack ?[Tt]race|^[[:space:]]+at [A-Za-z0-9_.<>$]+ ?\(|node_modules|[A-Za-z]\.ts:[0-9]+|System\.[A-Z][a-z]+|Microsoft\.AspNetCore|ENOENT|ECONNREFUSED|TypeError|ReferenceError)'

printf 'verify-public: %s  %s%s%s\n\n' "$envname" "$origin" "${resolve_ip:+  (connecting to $resolve_ip)}" "$([ "$insecure" -eq 1 ] && echo '  (--insecure)')"

# ---------------------------------------------------------------------------------------------------------------
# 1. plain HTTP -> HTTPS
# ---------------------------------------------------------------------------------------------------------------
if [ "$http_origin" = none ]; then
  record SKIP http-redirect "plain HTTP not checked (no --http-origin for a non-standard HTTPS port)"
else
  fetch GET "$http_origin/verify-public?x=1"
  if [ "$RC" -ne 0 ]; then
    record FAIL http-redirect "$http_origin did not answer ($(curl_why)). Port 80 must answer with a 301 to HTTPS (and certbot needs it)"
  elif [ "$CODE" != 301 ]; then
    record FAIL http-redirect "HTTP answered $CODE, expected 301 (a permanent redirect; 302/307 are not cached by browsers or crawlers)"
  elif [ "$LOC" != "$origin/verify-public?x=1" ]; then
    record FAIL http-redirect "301, but Location is '$LOC', expected '$origin/verify-public?x=1' (scheme, host, path and query must survive)"
  else
    record PASS http-redirect "http -> https: 301 to $LOC"
  fi
fi

# ---------------------------------------------------------------------------------------------------------------
# 2. HTTPS answers
# ---------------------------------------------------------------------------------------------------------------
fetch GET "$origin/"
home_h="$H"; home_b="$B"; home_code="$CODE"; home_rc="$RC"; home_remote="$REMOTE"
https_usable=1
if [ "$home_rc" -ne 0 ]; then
  https_usable=0
  hint=""
  case "$home_rc" in 35|51|58|59|60|77|83) hint=". The certificate is not trusted or does not match: fix it, or re-run with --insecure to test the rest meanwhile" ;; esac
  record FAIL https-200 "GET $origin/ failed ($(curl_why))$hint"
elif [ "$home_code" = 401 ]; then
  https_usable=0
  record FAIL https-200 "GET / answered 401: this host asks for a user name and password (HTTP basic auth, docs/16 B13). Pass it with SD_VERIFY_BASIC_AUTH=user:password"
elif [ "$home_code" != 200 ]; then
  record FAIL https-200 "GET / answered $home_code, expected 200"
else
  record PASS https-200 "GET / -> 200"
fi
ip_for_probes="${resolve_ip:-$home_remote}"

# ---------------------------------------------------------------------------------------------------------------
# 3. certificate
# ---------------------------------------------------------------------------------------------------------------
connect="${resolve_ip:-$host}"
sclient_args=(-connect "$connect:$port" -servername "$host")
[ -n "$cacert" ] && sclient_args+=(-CAfile "$cacert")
if [ "$insecure" -eq 0 ]; then sclient_args+=(-verify_hostname "$host"); fi
tmo=""; command -v timeout >/dev/null 2>&1 && tmo="timeout $((timeout_s + 5))"
sc_out="$(printf '' | $tmo openssl s_client "${sclient_args[@]}" 2>&1)"
host_note=""
case "$sc_out" in
  *"unknown option"*|*"Usage: s_client"*|*"usage: s_client"*)
    # An openssl without -verify_hostname (LibreSSL on macOS, very old OpenSSL). The chain is still checked here, and curl
    # (the https-200 check) checks the host name, so ask again without it.
    sclient_args=(-connect "$connect:$port" -servername "$host")
    [ -n "$cacert" ] && sclient_args+=(-CAfile "$cacert")
    sc_out="$(printf '' | $tmo openssl s_client "${sclient_args[@]}" 2>&1)"
    host_note=" (this openssl cannot check the host name; curl did, in https-200)" ;;
esac
verify_line="$(printf '%s\n' "$sc_out" | tr -d '\r' | grep -m1 'Verify return code' | sed 's/^[[:space:]]*//')"
end_raw="$(printf '%s\n' "$sc_out" | openssl x509 -noout -enddate 2>/dev/null | sed 's/^notAfter=//')"
issuer="$(printf '%s\n' "$sc_out" | openssl x509 -noout -issuer 2>/dev/null | sed 's/^issuer= *//')"
if [ -z "$end_raw" ]; then
  record FAIL tls-cert "no certificate received from $connect:$port for $host ($(printf '%s\n' "$sc_out" | tr -d '\r' | grep -m1 -iE 'error|refused|failure' | cut -c1-120))"
else
  days=""
  now_s="$(date -u +%s)"
  exp_s="$(date -u -d "$end_raw" +%s 2>/dev/null || date -ju -f '%b %e %T %Y %Z' "$end_raw" +%s 2>/dev/null)"
  [ -n "$exp_s" ] && days=$(( (exp_s - now_s) / 86400 ))
  if [ -z "$days" ]; then
    # No usable `date`: bracket the expiry with -checkend instead.
    days="?"
    for d in 90 60 30 21 14 7 1; do
      if printf '%s\n' "$sc_out" | openssl x509 -noout -checkend $((d * 86400)) >/dev/null 2>&1; then days="more than $d"; break; fi
    done
  fi
  detail="expires $end_raw (in $days days), issuer: ${issuer:-?}"
  if [ "$insecure" -eq 1 ]; then
    record WARN tls-cert "chain NOT verified (--insecure). $detail"
  else
    case "$verify_line" in
      *"code: 0 "*|*"code: 0("*)
        expired=0; case "$days" in ''|'?'|more*) ;; *) [ "$days" -le 0 ] && expired=1 ;; esac
        if [ "$expired" -eq 1 ]; then
          record FAIL tls-cert "certificate has EXPIRED. $detail"
        else
          case "$days" in
            ''|'?'|more*) record PASS tls-cert "chain and host name verify. $detail$host_note" ;;
            *) if [ "$days" -lt 21 ]; then record WARN tls-cert "chain verifies but renew now: $detail"; else record PASS tls-cert "chain and host name verify. $detail$host_note"; fi ;;
          esac
        fi ;;
      *) record FAIL tls-cert "certificate does not verify for $host: ${verify_line:-no verification result}. $detail (serve fullchain.pem, not cert.pem, so the intermediate is sent)" ;;
    esac
  fi
fi

# ---------------------------------------------------------------------------------------------------------------
# From here on the checks need working HTTPS.
# ---------------------------------------------------------------------------------------------------------------
skip_all() { record SKIP "$1" "not checked: HTTPS is unusable (see https-200)"; }

# 4. HSTS
if [ "$https_usable" -eq 1 ]; then
  hsts="$(hdr "$home_h" strict-transport-security)"
  if [ -z "$hsts" ]; then
    record FAIL hsts "no Strict-Transport-Security header on /"
  else
    age="$(printf '%s' "$hsts" | sed -n 's/.*max-age=\([0-9]*\).*/\1/p')"
    if [ -z "$age" ]; then record FAIL hsts "Strict-Transport-Security has no max-age ('$hsts')"
    elif [ "$age" -lt 15552000 ]; then record WARN hsts "max-age=$age is under 180 days ('$hsts')"
    else record PASS hsts "$hsts"; fi
  fi
else skip_all hsts; fi

# 5. server-side rendering (docs/15 §3 step 5)
if [ "$https_usable" -eq 1 ]; then
  n_ssr="$(grep -o 'ng-server-context="ssr"' "$home_b" | wc -l | tr -d ' ')"
  if [ "$n_ssr" = 1 ]; then
    record PASS ssr "the home page is server-rendered (ng-server-context=\"ssr\" once)"
  else
    record FAIL ssr "ng-server-context=\"ssr\" appears $n_ssr times (must be exactly 1): the storefront served the empty client-side shell. Check NG_TRUST_PROXY_HEADERS against the gateway, and SD_PUBLIC_HOSTS (docs/15 §3 step 5)"
  fi
else skip_all ssr; fi

# 6. robots.txt
if [ "$https_usable" -eq 1 ]; then
  fetch GET "$origin/robots.txt"
  if [ "$RC" -ne 0 ] || [ "$CODE" != 200 ]; then
    record FAIL robots "GET /robots.txt: $(curl_why), expected 200"
  else
    lines="$(tr -d '\r' < "$B" | sed 's/^[[:space:]]*//; s/[[:space:]]*$//')"
    has_ua=0; printf '%s\n' "$lines" | grep -qx 'User-agent: \*' && has_ua=1
    has_disallow_all=0; printf '%s\n' "$lines" | grep -qx 'Disallow: /' && has_disallow_all=1
    has_allow=0; printf '%s\n' "$lines" | grep -qi '^Allow:' && has_allow=1
    sitemap_line="$(printf '%s\n' "$lines" | sed -n 's/^[Ss]itemap:[[:space:]]*//p' | head -n 1)"
    if [ "$envname" = staging ]; then
      if [ "$has_ua" -eq 1 ] && [ "$has_disallow_all" -eq 1 ] && [ "$has_allow" -eq 0 ] && [ -z "$sitemap_line" ]; then
        record PASS robots "staging robots.txt is 'User-agent: *' / 'Disallow: /' (nothing allowed, no Sitemap)"
      else
        record FAIL robots "staging robots.txt must disallow everything and name no sitemap. Got: $(printf '%s\n' "$lines" | head -n 6 | tr '\n' '|')"
      fi
    else
      if [ "$has_ua" -eq 0 ]; then
        record FAIL robots "production robots.txt has no 'User-agent: *' group"
      elif [ "$has_disallow_all" -eq 1 ]; then
        record FAIL robots "production robots.txt contains 'Disallow: /': it blocks the whole shop from search engines (staging's file on production?)"
      elif [ "$has_allow" -eq 0 ]; then
        record FAIL robots "production robots.txt has no 'Allow: /'"
      elif [ "$sitemap_line" != "$origin/sitemap.xml" ]; then
        record FAIL robots "production robots.txt names Sitemap '${sitemap_line:-nothing}', expected '$origin/sitemap.xml' (SD_PUBLIC_ORIGIN, https)"
      else
        record PASS robots "production robots.txt allows crawling and names Sitemap: $sitemap_line"
      fi
    fi
  fi
else skip_all robots; fi

# 7. sitemap.xml
if [ "$https_usable" -eq 1 ]; then
  fetch GET "$origin/sitemap.xml" --max-time $((timeout_s * 3))
  if [ "$RC" -ne 0 ] || [ "$CODE" != 200 ]; then
    if [ "$envname" = production ]; then record FAIL sitemap "GET /sitemap.xml: $(curl_why), expected 200"
    else record WARN sitemap "GET /sitemap.xml: $(curl_why) (not required on staging)"; fi
  else
    locs="$(grep -o '<loc>[^<]*</loc>' "$B" | sed 's/<[^>]*>//g')"
    n_loc="$(printf '%s\n' "$locs" | grep -c .)"
    stray="$(printf '%s\n' "$locs" | grep -v -F "$origin/" | grep -v -x -F "$origin" | head -n 3 | tr '\n' ' ')"
    if [ -n "$stray" ]; then
      record FAIL sitemap "sitemap.xml lists addresses outside $origin: $stray (SD_PUBLIC_ORIGIN wrong?)"
    elif [ "$n_loc" -eq 0 ]; then
      record WARN sitemap "sitemap.xml answers 200 but lists no <loc> (empty catalogue?)"
    else
      record PASS sitemap "sitemap.xml: 200, $n_loc addresses, all under $origin"
    fi
  fi
else skip_all sitemap; fi

# 8. X-Robots-Tag
if [ "$https_usable" -eq 1 ]; then
  bad=""; seen_any=""
  for path in / /robots.txt /api/catalog/departments; do
    if [ "$path" = / ]; then h_file="$home_h"; else fetch GET "$origin$path"; h_file="$H"; fi
    xr="$(hdr "$h_file" x-robots-tag)"
    if [ "$envname" = staging ]; then
      case "$(lower "$xr")" in *noindex*) ;; *) bad="$bad $path" ;; esac
    else
      case "$(lower "$xr")" in
        *noindex*|*none*) bad="$bad $path($xr)" ;;
        '') ;;
        *) seen_any="$seen_any $path($xr)" ;;
      esac
    fi
  done
  if [ "$envname" = staging ]; then
    if [ -z "$bad" ]; then record PASS x-robots-tag "staging sends X-Robots-Tag: noindex on /, /robots.txt and the API"
    else record FAIL x-robots-tag "staging is INDEXABLE: no 'X-Robots-Tag: noindex' on:$bad (the staging host-nginx template adds it)"; fi
  else
    if [ -n "$bad" ]; then record FAIL x-robots-tag "production sends noindex:$bad. The shop would drop out of search results (staging's nginx file on production?)"
    elif [ -n "$seen_any" ]; then record WARN x-robots-tag "production sends an X-Robots-Tag you did not expect:$seen_any"
    else record PASS x-robots-tag "production sends no X-Robots-Tag on /, /robots.txt, the API"; fi
  fi
else skip_all x-robots-tag; fi

# 9. admin
if [ "$https_usable" -eq 1 ]; then
  fetch GET "$origin/admin/"
  if [ "$RC" -ne 0 ] || [ "$CODE" != 200 ]; then
    record FAIL admin "GET /admin/: $(curl_why), expected 200"
  elif ! grep -qi '<html' "$B"; then
    record FAIL admin "GET /admin/ answered 200 but not an HTML page"
  else
    xfo="$(hdr "$H" x-frame-options)"
    if [ -z "$xfo" ]; then record WARN admin "/admin/ is 200 HTML but sends no X-Frame-Options (the gateway should send DENY)"
    else record PASS admin "/admin/ -> 200 HTML, X-Frame-Options: $xfo"; fi
  fi
else skip_all admin; fi

# 10. API
if [ "$https_usable" -eq 1 ]; then
  fetch GET "$origin/api/catalog/departments"
  ctype="$(lower "$(hdr "$H" content-type)")"
  first="$(tr -d ' \t\r\n' < "$B" | cut -c1-1)"
  if [ "$RC" -ne 0 ] || [ "$CODE" != 200 ]; then
    record FAIL api-departments "GET /api/catalog/departments: $(curl_why), expected 200"
  else
    case "$ctype" in
      application/json*|*+json*) ;;
      *) record FAIL api-departments "answered 200 but Content-Type is '${ctype:-none}', not JSON (the storefront or an error page answered instead of the API)"; ctype="" ;;
    esac
    if [ -n "$ctype" ]; then
      case "$first" in
        '['|'{')
          if [ "$(tr -d ' \t\r\n' < "$B")" = '[]' ]; then record WARN api-departments "200 JSON but an empty list: has the reference data been seeded (check the API log)?"
          else record PASS api-departments "200 application/json"; fi ;;
        *) record FAIL api-departments "Content-Type says JSON but the body does not start with [ or {" ;;
      esac
    fi
  fi
else skip_all api-departments; fi

# 11 + 12. an unknown Host header must NOT reach the shop
unk="unknown-host-$(date +%s)-$$.invalid"
looks_like_shop() {   # headers file, body file: did our stack answer?
  # Two of our own fingerprints together (the gateway's Referrer-Policy and the site file's HSTS), the staging
  # file's noindex header, or the storefront's server-rendered markup. One header alone could be another tenant's
  # default site. Not `<app-root`: every Angular app has it, and the other project on this server is one (found on
  # the first UAT run, where its front end answered the unknown name and was reported as ours).
  local h="$1" b="$2" rp
  rp="$(lower "$(hdr "$h" referrer-policy)")"
  [ -n "$(hdr "$h" strict-transport-security)" ] && [ "$rp" = strict-origin-when-cross-origin ] && return 0
  [ "$envname" = staging ] && [ "$(lower "$(hdr "$h" x-robots-tag)")" = "noindex, nofollow" ] && return 0
  grep -q -e 'ng-version' -e 'ng-server-context' "$b" && return 0
  return 1
}
if [ -z "$ip_for_probes" ]; then
  record SKIP unknown-host-https "no IP address to probe (use --resolve IP)"
  record SKIP unknown-host-http "no IP address to probe (use --resolve IP)"
else
  save_args=("${curl_args[@]}")
  # Only this probe differs: the unknown name is pinned to the same IP, and the certificate cannot match it.
  curl_args=(-sS --max-time "$timeout_s" -A "$ua" -k --resolve "$unk:$port:$ip_for_probes")
  fetch GET "https://$unk:$port/"
  case "$RC" in
    35|60|51|58|59) record PASS unknown-host-https "TLS handshake refused for an unknown name (no certificate shown): the catch-all works" ;;
    52|56)          record PASS unknown-host-https "connection closed without an answer for an unknown name" ;;
    0)
      if looks_like_shop "$H" "$B"; then
        record FAIL unknown-host-https "an UNKNOWN host name was answered by the shop (HTTP $CODE). Add the catch-all default_server (spencer-denim-00-default.conf.template) or fix server_name"
      elif [ "$CODE" -ge 200 ] && [ "$CODE" -lt 300 ]; then
        record WARN unknown-host-https "an unknown host name got a 200 from something that is not the shop (another site's default?). Confirm it is intended"
      else
        record PASS unknown-host-https "an unknown host name got HTTP $CODE from something that is not the shop"
      fi ;;
    *) record WARN unknown-host-https "unexpected curl result for the unknown-host probe: $(curl_why)" ;;
  esac
  if [ "$http_origin" = none ]; then
    record SKIP unknown-host-http "plain HTTP not checked (no --http-origin)"
  else
    curl_args=(-sS --max-time "$timeout_s" -A "$ua" --resolve "$unk:$http_port:$ip_for_probes")
    fetch GET "http://$unk:$http_port/"
    case "$RC" in
      52|56) record PASS unknown-host-http "unknown name on port 80: connection closed without an answer (444)" ;;
      0)
        if looks_like_shop "$H" "$B"; then
          record FAIL unknown-host-http "an unknown host name on port 80 reached the shop"
        elif [ "$CODE" = 301 ] || [ "$CODE" = 302 ] || [ "$CODE" = 308 ]; then
          record WARN unknown-host-http "an unknown name on port 80 is redirected to '$LOC': our port-80 block is acting as the default server (no default_server, or it sorts first). Harmless to visitors, but install the catch-all"
        elif [ "$CODE" -ge 200 ] && [ "$CODE" -lt 300 ]; then
          record PASS unknown-host-http "an unknown name on port 80 got the server's default page ($CODE), not the shop"
        else
          record PASS unknown-host-http "an unknown name on port 80 got HTTP $CODE from something that is not the shop"
        fi ;;
      *) record WARN unknown-host-http "unexpected curl result for the unknown-host probe on port 80: $(curl_why)" ;;
    esac
  fi
  curl_args=("${save_args[@]}")
fi

# 13. order page and reset-password with fake tokens
if [ "$https_usable" -eq 1 ]; then
  worst=""
  for path in /order/00000000-0000-0000-0000-000000000000 /order/not-a-real-token; do
    fetch GET "$origin$path"
    if [ "$RC" -ne 0 ] || [ "$CODE" -ge 500 ]; then worst="$worst $path:$(curl_why)"
    elif grep -qE "$trace_re" "$B"; then worst="$worst $path:stack-trace-in-body"; fi
  done
  if [ -z "$worst" ]; then record PASS order-token "a made-up order token answers without a server error (no 5xx, no trace)"
  else record FAIL order-token "made-up order token misbehaves:$worst"; fi

  fetch GET "$origin/account/reset-password?token=x"
  if [ "$RC" -ne 0 ] || [ "$CODE" -ge 500 ]; then record FAIL reset-password "GET /account/reset-password?token=x: $(curl_why)"
  elif grep -qE "$trace_re" "$B"; then record FAIL reset-password "a stack trace is visible in the reset-password page"
  elif [ "$CODE" != 200 ]; then record WARN reset-password "answers $CODE, expected the page (200)"
  else record PASS reset-password "reset-password with a junk token -> 200, no server error"; fi
else skip_all order-token; skip_all reset-password; fi

# 14. page-view endpoint (the address is not a storefront path, so the API ignores it: 204, nothing recorded)
if [ "$https_usable" -eq 1 ]; then
  fetch POST "$origin/api/traffic/page-views" -H 'Content-Type: application/json' --data '{"path":"verify-public-probe"}'
  if [ "$RC" -ne 0 ]; then record FAIL page-view "POST /api/traffic/page-views: $(curl_why)"
  elif [ "$CODE" != 204 ]; then record FAIL page-view "POST /api/traffic/page-views answered $CODE, expected 204"
  else record PASS page-view "POST /api/traffic/page-views -> 204"; fi
else skip_all page-view; fi

# 15. information leaks
if [ "$https_usable" -eq 1 ]; then
  srv="$(hdr "$home_h" server)"
  pw="$(hdr "$home_h" x-powered-by)"; asp="$(hdr "$home_h" x-aspnet-version)"
  if [ -n "$pw" ] || [ -n "$asp" ]; then
    record FAIL server-header "framework headers are exposed: X-Powered-By='$pw' X-AspNet-Version='$asp'"
  elif printf '%s' "$srv" | grep -Eq '[0-9]+\.[0-9]+'; then
    record WARN server-header "Server: '$srv' reveals the nginx version. Add 'server_tokens off;' (http-level snippet or the site files; docs/16 §C)"
  else
    record PASS server-header "no version in the Server header (${srv:-none}), no X-Powered-By"
  fi
  if [ "$(lower "$(hdr "$home_h" x-content-type-options)")" = nosniff ]; then
    record PASS nosniff "X-Content-Type-Options: nosniff (the gateway is in the path)"
  else
    record WARN nosniff "no X-Content-Type-Options: nosniff on / (is the request really passing through the gateway?)"
  fi

  tr_bad=""
  rnd="$(date +%s)$$"
  for path in "/verify-public-404-$rnd" "/api/verify-public-404-$rnd"; do
    fetch GET "$origin$path"
    if [ "$RC" -ne 0 ] || [ "$CODE" -ge 500 ]; then tr_bad="$tr_bad $path:$(curl_why)"
    elif grep -qE "$trace_re" "$B"; then tr_bad="$tr_bad $path:stack-trace"
    elif [ "$CODE" = 200 ]; then tr_bad="$tr_bad $path:200(soft-404)"; fi
  done
  if [ -z "$tr_bad" ]; then record PASS not-found "unknown pages and API routes answer 404 with no stack trace"
  else record FAIL not-found "404 handling is wrong:$tr_bad"; fi
else skip_all server-header; skip_all nosniff; skip_all not-found; fi

# 16. things that must not be reachable from the internet
if [ "$https_usable" -eq 1 ]; then
  bad=""
  fetch GET "$origin/internal/catalog/sitemap"; [ "$RC" -eq 0 ] && [ "$CODE" = 200 ] && bad="$bad /internal/catalog/sitemap"
  fetch GET "$origin/health/ready";             [ "$RC" -eq 0 ] && [ "$CODE" = 200 ] && bad="$bad /health/ready"
  if [ -z "$bad" ]; then record PASS not-exposed "/internal/... and /health/ready are not served on the public origin"
  else record FAIL not-exposed "reachable from the internet but must not be:$bad (docs/03 §4 '/internal is not routed'; the gateway returns 404 for /health/)"; fi
else skip_all not-exposed; fi

# 17. cookies seen so far
if [ "$https_usable" -eq 1 ]; then
  cookies="$(for f in "$tmp"/h*; do hdr_all "$f" set-cookie; done)"
  if [ -z "$cookies" ]; then
    record WARN cookies "no Set-Cookie header was seen at all (the page-view probe normally sets sd_vs), so cookie flags were not checked"
  else
    n_c=0; bad_secure=""; bad_http=""; bad_same=""
    while IFS= read -r c; do
      [ -n "$c" ] || continue
      n_c=$((n_c + 1)); name="${c%%=*}"; low="$(lower "$c")"
      case "$low" in *"; secure"*|*";secure"*) ;; *) bad_secure="$bad_secure $name" ;; esac
      case "$low" in *"; httponly"*|*";httponly"*) ;; *) bad_http="$bad_http $name" ;; esac
      case "$low" in *"samesite="*) ;; *) bad_same="$bad_same $name" ;; esac
    done <<EOF
$cookies
EOF
    if [ -n "$bad_secure" ]; then record FAIL cookies "cookie(s) without Secure over HTTPS:$bad_secure"
    elif [ -n "$bad_http" ]; then record WARN cookies "cookie(s) without HttpOnly:$bad_http (fine only if a script must read them)"
    elif [ -n "$bad_same" ]; then record WARN cookies "cookie(s) without SameSite:$bad_same"
    else record PASS cookies "$n_c Set-Cookie header(s) seen, all Secure + HttpOnly + SameSite"; fi
  fi
else skip_all cookies; fi

# 18. optional: www -> apex
if [ "$www_check" -eq 1 ]; then
  case "$host" in
    www.*) record SKIP www-redirect "the origin already is a www name" ;;
    *)
      save_args=("${curl_args[@]}")
      [ -n "$resolve_ip" ] && curl_args+=(--resolve "www.$host:$port:$resolve_ip")
      fetch GET "https://www.$host${port:+:$port}/x?y=1"
      if [ "$RC" -ne 0 ]; then record FAIL www-redirect "https://www.$host did not answer ($(curl_why)); does its certificate cover www?"
      elif [ "$CODE" != 301 ] || [ "$LOC" != "$origin/x?y=1" ]; then record FAIL www-redirect "https://www.$host/x?y=1 answered $CODE Location '$LOC', expected 301 to '$origin/x?y=1'"
      else record PASS www-redirect "www.$host -> 301 $LOC"; fi
      curl_args=("${save_args[@]}") ;;
  esac
fi

# ---------------------------------------------------------------------------------------------------------------
printf '\n%-4s  %-18s %s\n' "RES" "CHECK" "WHAT"
printf '%-4s  %-18s %s\n' "----" "------------------" "----"
i=0
while [ "$i" -lt "${#res_id[@]}" ]; do
  printf '%-4s  %-18s %s\n' "${res_state[$i]}" "${res_id[$i]}" "$(printf '%s' "${res_text[$i]}" | cut -c1-90)"
  i=$((i + 1))
done
printf '\n%s PASS, %s WARN, %s FAIL, %s SKIP  (%s, %s)\n' "$n_pass" "$n_warn" "$n_fail" "$n_skip" "$envname" "$origin"
if [ "$n_fail" -gt 0 ]; then printf 'RESULT: FAIL\n'; exit 1; fi
printf 'RESULT: no FAIL%s\n' "$([ "$n_warn" -gt 0 ] && echo ' (read the WARN lines)')"
exit 0
