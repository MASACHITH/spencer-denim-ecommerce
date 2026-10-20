#!/usr/bin/env bash
# Tests deploy/scripts/preflight.sh with stand-in commands (#195).
#
# preflight.sh is run with a PATH that holds ONLY (a) wrappers for the ordinary utilities it needs (awk, sed, grep,
# df, git ...) and (b) fake docker, ss, ip, nginx, certbot, dig, curl and id whose answers come from fixture files.
# That does three things at once:
#   * every FAIL / WARN / PASS branch can be provoked without a server;
#   * the script cannot change anything: rm, mkdir, mv, cp, touch, tee, chmod, chown, install and ln are not on the PATH
#     at all, so an attempt would print "command not found" (the test fails on that), and the fake commands refuse (and
#     log) any docker/nginx/certbot verb that is not a read;
#   * the directories the script is pointed at are compared before and after: byte for byte the same.
#
#   bash deploy/scripts/tests/test-preflight.sh             # all scenarios (TEST_JOBS=6 at a time)
#   bash deploy/scripts/tests/test-preflight.sh nginx       # only scenarios whose name contains "nginx"
here="$(cd "$(dirname "$0")" && pwd)"
repo_root="$(cd "$here/../../.." && pwd)"
only="${1:-}"
jobs_max="${TEST_JOBS:-6}"
work="$(mktemp -d)"
cleanup() { kill $(jobs -p) 2>/dev/null; rm -rf "$work"; }
trap cleanup EXIT
real_bash="$(command -v bash)"
export NO_COLOR=1

# ---- the whitelist of ordinary utilities ---------------------------------------------------------------------------------
base="$work/base"; mkdir -p "$base"
for u in awk sed grep cut tr sort head tail wc cat date df uname hostname nproc readlink dirname basename cmp stat git openssl id ls find; do
  real="$(command -v "$u" 2>/dev/null)" || continue
  printf '#!/bin/sh\nexec "%s" "$@"\n' "$real" > "$base/$u"; chmod +x "$base/$u"
done

# ---- fake commands, driven by files in $FX ----------------------------------------------------------------------------------
stubs="$work/stubs"; mkdir -p "$stubs"
mkstub() { cat > "$stubs/$1"; chmod +x "$stubs/$1"; }
mkstub docker <<'EOF'
#!/bin/sh
echo "docker $*" >> "$FX/calls.log"
case "$1" in
  version) [ -f "$FX/docker_server" ] && { cat "$FX/docker_server"; exit 0; }; echo "Cannot connect to the Docker daemon" >&2; exit 1 ;;
  compose) if [ "$2" = version ] && [ -f "$FX/compose_version" ]; then cat "$FX/compose_version"; exit 0; fi; echo "docker: 'compose' is not a docker command" >&2; exit 1 ;;
  info) [ -f "$FX/docker_server" ] && { echo /var/lib/docker; exit 0; }; exit 1 ;;
  network)
    case "$2" in
      ls) printf 'aaa\nbbb\n'; exit 0 ;;
      inspect) cat "$FX/networks"; exit 0 ;;
    esac ;;
  ps)
    for a in "$@"; do case "$a" in publish=*) p="${a#publish=}"; [ -f "$FX/ps_$p" ] && cat "$FX/ps_$p"; exit 0 ;; esac; done
    exit 0 ;;
esac
echo "docker $*" >> "$FX/violations.log"; exit 1
EOF
mkstub ss <<'EOF'
#!/bin/sh
echo "ss $*" >> "$FX/calls.log"
[ "$1" = "-ltn" ] && { cat "$FX/ss"; exit 0; }
echo "ss $*" >> "$FX/violations.log"; exit 1
EOF
mkstub ip <<'EOF'
#!/bin/sh
echo "ip $*" >> "$FX/calls.log"
case "$*" in
  "-4 route show") cat "$FX/routes"; exit 0 ;;
  "-4 -o route get 1.1.1.1") [ -f "$FX/src" ] && echo "1.1.1.1 via 10.0.0.1 dev eth0 src $(cat "$FX/src") uid 0"; exit 0 ;;
  "-4 -o addr show scope global") cat "$FX/v4addr"; exit 0 ;;
  "-6 -o addr show scope global") cat "$FX/v6addr"; exit 0 ;;
esac
echo "ip $*" >> "$FX/violations.log"; exit 1
EOF
mkstub nginx <<'EOF'
#!/bin/sh
echo "nginx $*" >> "$FX/calls.log"
case "$1" in
  -v) echo "nginx version: nginx/$(cat "$FX/nginx_ver")" >&2; exit 0 ;;
  -T) if [ -f "$FX/nginx_T" ]; then cat "$FX/nginx_T"; exit 0; fi
      echo 'nginx: [alert] could not open error log file: open() "/var/log/nginx/error.log" failed (13: Permission denied)' >&2; exit 1 ;;
esac
echo "nginx $*" >> "$FX/violations.log"; exit 1
EOF
mkstub certbot <<'EOF'
#!/bin/sh
echo "certbot $*" >> "$FX/calls.log"
[ "$1" = "--version" ] && { echo "certbot 2.9.0"; exit 0; }
echo "certbot $*" >> "$FX/violations.log"; exit 1
EOF
mkstub dig <<'EOF'
#!/bin/sh
echo "dig $*" >> "$FX/calls.log"
type=""; host=""
for a in "$@"; do
  if [ -n "$type" ] && [ -z "$host" ]; then host="$a"; fi
  case "$a" in A|AAAA) [ -z "$type" ] && type="$a" ;; esac
done
[ -f "$FX/dns" ] && awk -v h="$host" -v t="$type" '$1 == h && $2 == t { print $3 }' "$FX/dns"
exit 0
EOF
mkstub curl <<'EOF'
#!/bin/sh
echo "curl $*" >> "$FX/calls.log"
[ "$1" = "--version" ] && { echo "curl 8.5.0 (stub)"; exit 0; }
case "$*" in *api.ipify.org*) [ -f "$FX/ipify" ] && { cat "$FX/ipify"; exit 0; }; exit 6 ;; esac
echo "curl $*" >> "$FX/violations.log"; exit 1
EOF
mkstub id <<'EOF'
#!/bin/sh
case "$1" in -u) cat "$FX/uid" ;; -un) echo tester ;; *) echo "uid=0" ;; esac
EOF

# ---- the baseline: a healthy, untouched VPS ---------------------------------------------------------------------------------
nginx_conf_header='nginx: the configuration file /etc/nginx/nginx.conf syntax is ok
nginx: configuration file /etc/nginx/nginx.conf test is successful
# configuration file /etc/nginx/nginx.conf:
http { include /etc/nginx/sites-enabled/*; }'
nginx_all_defaults="$nginx_conf_header
# configuration file /etc/nginx/sites-enabled/default:
server {
    listen 80 default_server;
    listen [::]:80 default_server;
    listen 443 ssl default_server;
    listen [::]:443 ssl default_server;
    server_name _;
}"
fx_baseline() {   # dir
  local fx="$1"; mkdir -p "$fx/le" "$fx/webroot" "$fx/apps" "$fx/db"
  echo 0 > "$fx/uid"
  echo "28.4.0" > "$fx/docker_server"
  echo "v2.29.1" > "$fx/compose_version"
  printf 'bridge bbbbbbbbbbbb 172.17.0.0/16 \nhost hhhhhhhhhhhh \nnone nnnnnnnnnnnn \nother-site_default cccccccccccc 172.18.0.0/16 \n' > "$fx/networks"
  printf 'State Recv-Q Send-Q Local Address:Port Peer Address:Port\nLISTEN 0 511 0.0.0.0:80 0.0.0.0:*\nLISTEN 0 511 0.0.0.0:443 0.0.0.0:*\nLISTEN 0 128 0.0.0.0:22 0.0.0.0:*\nLISTEN 0 511 [::]:80 [::]:*\nLISTEN 0 511 [::]:443 [::]:*\n' > "$fx/ss"
  printf 'default via 10.0.0.1 dev eth0\n172.17.0.0/16 dev docker0 proto kernel scope link src 172.17.0.1\n203.0.113.0/24 dev eth0 proto kernel scope link src 203.0.113.10\n' > "$fx/routes"
  echo 203.0.113.10 > "$fx/src"
  echo "2: eth0 inet 203.0.113.10/24 brd 203.0.113.255 scope global eth0" > "$fx/v4addr"
  echo "2: eth0 inet6 2001:db8::10/64 scope global" > "$fx/v6addr"
  echo "1.24.0 (Ubuntu)" > "$fx/nginx_ver"
  printf '%s\n' "$nginx_all_defaults" > "$fx/nginx_T"
  printf 'MemTotal: 8192000 kB\nMemAvailable: 7680000 kB\nSwapTotal: 2097152 kB\nMemFree: 1000000 kB\n' > "$fx/meminfo"
  printf 'processor : 0\nflags : fpu sse3 ssse3 sse4_1 sse4_2 popcnt\n' > "$fx/cpuinfo"
  printf 'PRETTY_NAME="Ubuntu 24.04 LTS"\n' > "$fx/os-release"
  printf 'staging.example.test A 203.0.113.10\nshop.example.test A 203.0.113.10\n' > "$fx/dns"
  : > "$fx/le/options-ssl-nginx.conf"; : > "$fx/le/ssl-dhparams.pem"
}

# run_pf FX [args...] -> $PF_OUT, $PF_RC. Always the same hardened environment.
run_pf() {
  local fx="$1"; shift
  PF_OUT="$(cd "$fx" && env -i HOME="$fx" PATH="$stubs:$base" FX="$fx" \
      SD_APPS_BASE="${SD_APPS_BASE_OVERRIDE:-$fx/apps}" SD_DB_BASE="${SD_DB_BASE_OVERRIDE:-$fx/db}" \
      SD_PREFLIGHT_MEMINFO="$fx/meminfo" SD_PREFLIGHT_CPUINFO="$fx/cpuinfo" SD_PREFLIGHT_OSRELEASE="$fx/os-release" \
      SD_PREFLIGHT_CERTBOT_WEBROOT="$fx/webroot" SD_PREFLIGHT_LETSENCRYPT_DIR="$fx/le" \
      SD_PREFLIGHT_DISK_FAIL_GB="${DISK_FAIL:-1}" SD_PREFLIGHT_DISK_WARN_GB="${DISK_WARN:-2}" SD_PREFLIGHT_NO_EXTERNAL="${NO_EXTERNAL:-0}" \
      NO_COLOR=1 "$real_bash" "${PF_SCRIPT:-$repo_root/deploy/scripts/preflight.sh}" "$@" 2>&1)"
  PF_RC=$?
}

fails=""
check() {   # check STATE "substring"  -> ok if some line starts with STATE and contains the substring
  local state="$1" needle="$2"
  if ! printf '%s\n' "$PF_OUT" | grep -E "^$state " | grep -qF -- "$needle"; then fails="$fails\n    missing $state line containing: $needle"; fi
}
refute() {   # refute STATE "substring" -> no such line
  local state="$1" needle="$2"
  if printf '%s\n' "$PF_OUT" | grep -E "^$state " | grep -qF -- "$needle"; then fails="$fails\n    unexpected $state line containing: $needle"; fi
}
rc_is() { [ "$PF_RC" = "$1" ] || fails="$fails\n    exit status $PF_RC, wanted $1"; }
no_failures() { if printf '%s\n' "$PF_OUT" | grep -q '^FAIL '; then fails="$fails\n    a FAIL line appeared: $(printf '%s\n' "$PF_OUT" | grep -m1 '^FAIL ' | cut -c1-120)"; fi; }
no_cmd_not_found() { if printf '%s\n' "$PF_OUT" | grep -qiE 'command not found|not found$'; then fails="$fails\n    a command was missing: $(printf '%s\n' "$PF_OUT" | grep -iE -m1 'not found')"; fi; }

# ---- scenarios -----------------------------------------------------------------------------------------------------------------
sc_good() {
  local fx="$1"; fx_baseline "$fx"
  run_pf "$fx" both staging.example.test shop.example.test
  rc_is 0; no_failures; no_cmd_not_found
  check PASS "running as root"
  check PASS "checkout contains the RCSI fix"
  check PASS "Docker Engine 28.4.0"
  check PASS "Docker Compose 2.29.1 (>= 2.24)"
  check PASS "port 38081 (staging) is free"
  check PASS "port 38080 (production) is free"
  check PASS "no docker network or host route overlaps"
  check PASS "memory: 7500 MB available covers both environments"
  check PASS "default_server already exists on 80 and 443"
  check PASS "nginx 1.24.0 supports ssl_reject_handshake"
  check PASS "DNS: staging.example.test -> 203.0.113.10 (via dig @1.1.1.1) points at this server"
  check PASS "DNS: shop.example.test -> 203.0.113.10"
  check PASS "$fx/webroot exists"
  check PASS "ssl-dhparams.pem exists"
  check INFO "staging: install.sh has NOT been run yet"
  refute WARN "memory"
}
sc_everything_wrong() {
  local fx="$1"; fx_baseline "$fx"
  echo "v2.20.3" > "$fx/compose_version"
  printf 'bridge bbbbbbbbbbbb 172.17.0.0/16 \nvpn vvvvvvvvvvvv 10.251.0.0/16 \nlab llllllllllll 10.251.1.0/25 \n' > "$fx/networks"
  printf 'State Recv-Q Send-Q Local Address:Port Peer Address:Port\nLISTEN 0 511 0.0.0.0:80 0.0.0.0:*\nLISTEN 0 511 0.0.0.0:443 0.0.0.0:*\nLISTEN 0 4096 127.0.0.1:38080 0.0.0.0:*\nLISTEN 0 4096 0.0.0.0:38081 0.0.0.0:*\n' > "$fx/ss"
  printf 'default via 10.0.0.1 dev eth0\n10.251.0.0/20 dev wg0 proto kernel scope link\n' > "$fx/routes"
  printf 'MemTotal: 4096000 kB\nMemAvailable: 3072000 kB\nSwapTotal: 0 kB\nMemFree: 100000 kB\n' > "$fx/meminfo"
  printf 'processor : 0\nflags : fpu sse3\n' > "$fx/cpuinfo"
  printf '%s\n' "$nginx_conf_header
# configuration file /etc/nginx/sites-enabled/other:
server { listen 80; server_name other.example; }" > "$fx/nginx_T"
  rm -f "$fx/le/options-ssl-nginx.conf" "$fx/le/ssl-dhparams.pem"; rmdir "$fx/webroot"
  printf 'shop.example.test A 198.51.100.7\nv6.example.test A 203.0.113.10\nv6.example.test AAAA 2001:db8:ffff::1\n' > "$fx/dns"
  DISK_FAIL=99999999 run_pf "$fx" both staging.example.test shop.example.test v6.example.test
  rc_is 1; no_cmd_not_found
  check FAIL "Docker Compose 2.20.3 is older than 2.24"
  check FAIL "port 38081 (staging) is already in use"
  check FAIL "port 38080 (production) is already in use"
  check FAIL "docker network 'vpn' uses 10.251.0.0/16, which overlaps 10.251.0.0/24"
  check FAIL "docker network 'lab' uses 10.251.1.0/25, which overlaps 10.251.1.0/24"
  check FAIL "host route '10.251.0.0/20 dev wg0' overlaps 10.251.0.0/24"
  check FAIL "memory: only 3000 MB available"
  check WARN "no swap is configured"
  check WARN "does not advertise sse4_2"
  check FAIL "disk:"
  check WARN "NO default_server on 80 or 443"
  check WARN "DECISION: install deploy/host-nginx/spencer-denim-00-default.conf.template"
  check WARN "$fx/webroot does not exist"
  check WARN "options-ssl-nginx.conf does not exist"
  check FAIL "DNS: staging.example.test has no A record"
  check FAIL "DNS: shop.example.test -> 198.51.100.7 (via dig @1.1.1.1) but this server is 203.0.113.10"
  check PASS "DNS: v6.example.test -> 203.0.113.10"
  check WARN "DNS: v6.example.test has an AAAA record (2001:db8:ffff::1) that is not an IPv6 address of this server"
}
sc_missing_tools() {
  local fx="$1"; fx_baseline "$fx"
  rm -f "$stubs.none" 2>/dev/null
  mkdir -p "$fx/emptybin"
  # Only the whitelist: no docker, nginx, certbot, ss, ip, dig, curl.
  PF_OUT="$(cd "$fx" && env -i HOME="$fx" PATH="$base" FX="$fx" SD_APPS_BASE="$fx/apps" SD_DB_BASE="$fx/db" \
      SD_PREFLIGHT_MEMINFO="$fx/meminfo" SD_PREFLIGHT_CPUINFO="$fx/cpuinfo" SD_PREFLIGHT_OSRELEASE="$fx/os-release" \
      SD_PREFLIGHT_CERTBOT_WEBROOT="$fx/webroot" SD_PREFLIGHT_LETSENCRYPT_DIR="$fx/le" SD_PREFLIGHT_DISK_FAIL_GB=1 SD_PREFLIGHT_DISK_WARN_GB=2 \
      NO_COLOR=1 "$real_bash" "$repo_root/deploy/scripts/preflight.sh" staging staging.example.test 2>&1)"; PF_RC=$?
  rc_is 1
  check FAIL "curl is not installed"
  check FAIL "nginx is not installed"
  check FAIL "certbot is not installed"
  check FAIL "docker is not installed"
  check WARN "ss is not installed"
  check WARN "staging.example.test: no dig, host or getent here"
}
sc_engine_down() {
  local fx="$1"; fx_baseline "$fx"; rm -f "$fx/docker_server" "$fx/compose_version"
  run_pf "$fx" staging --skip-dns
  rc_is 1
  check FAIL "docker is installed but the engine does not answer"
  check FAIL "the Docker Compose plugin is missing"
}
sc_own_resources() {
  local fx="$1"; fx_baseline "$fx"
  printf 'bridge bbbbbbbbbbbb 172.17.0.0/16 \nsd-staging_edge eeeeeeeeeeee 10.251.1.0/24 \nsd-production_edge ffffffffffff 10.251.0.0/24 \n' > "$fx/networks"
  printf 'State Recv-Q Send-Q Local Address:Port Peer Address:Port\nLISTEN 0 4096 127.0.0.1:38080 0.0.0.0:*\nLISTEN 0 4096 127.0.0.1:38081 0.0.0.0:*\n' > "$fx/ss"
  echo "sd-staging-web-1" > "$fx/ps_38081"; echo "sd-production-web-1" > "$fx/ps_38080"
  printf 'default via 10.0.0.1 dev eth0\n10.251.1.0/24 dev br-eeeeeeeeeeee proto kernel scope link src 10.251.1.1\n10.251.0.0/24 dev br-ffffffffffff proto kernel scope link src 10.251.0.1\n' > "$fx/routes"
  run_pf "$fx" both --skip-dns
  rc_is 0; no_failures
  check PASS "port 38081 (staging): already published by this environment's own gateway (sd-staging-web-1)"
  check PASS "port 38080 (production): already published by this environment's own gateway (sd-production-web-1)"
  check INFO "docker network sd-staging_edge already holds 10.251.1.0/24"
  check PASS "no docker network or host route overlaps"
}
sc_foreign_container_on_port() {
  local fx="$1"; fx_baseline "$fx"
  printf 'State Recv-Q Send-Q Local Address:Port Peer Address:Port\nLISTEN 0 4096 127.0.0.1:38081 0.0.0.0:*\n' > "$fx/ss"
  echo "someone-elses-app" > "$fx/ps_38081"
  run_pf "$fx" staging --skip-dns
  rc_is 1
  check FAIL "port 38081 (staging) is already in use by container someone-elses-app"
}
sc_memory_fallback() {
  local fx="$1"; fx_baseline "$fx"
  printf 'MemTotal: 8192000 kB\nMemAvailable: 5632000 kB\nSwapTotal: 2097152 kB\n' > "$fx/meminfo"
  run_pf "$fx" both --skip-dns
  rc_is 0
  check WARN "memory: 5500 MB available is below 6880 MB (two SQL Server instances) but covers 5344 MB"
  check WARN "docs/15 §10 #2 fallback"
}
sc_installed_stale() {
  local fx="$1"; fx_baseline "$fx"
  local a="$fx/apps/staging/spencer-denim-app" d="$fx/db/staging"
  mkdir -p "$a/scripts" "$a/config" "$d/config" "$d/scripts"
  : > "$a/scripts/deploy.sh"
  printf 'SD_PUBLIC_HOSTS=CHANGE_ME.example.com\n' > "$a/config/app.env"
  printf 'SD_ENV=staging\n' > "$d/config/db.env"
  printf -- '-- an older script\nCREATE DATABASE [$(DbName)];\n-- READ_COMMITTED_SNAPSHOT ON (different text)\n' > "$d/scripts/provision-database.sql"
  run_pf "$fx" staging --skip-dns
  check INFO "staging: install.sh HAS been run"
  check WARN "app.env still contains CHANGE_ME placeholders"
  check PASS "db.env has no CHANGE_ME placeholders"
  check WARN "staging: the installed provision-database.sql differs from this checkout's. Run install.sh staging again before db-provision.sh"
}
sc_installed_without_rcsi() {
  local fx="$1"; fx_baseline "$fx"
  local a="$fx/apps/staging/spencer-denim-app" d="$fx/db/staging"
  mkdir -p "$a/scripts" "$a/config" "$d/config" "$d/scripts"
  : > "$a/scripts/deploy.sh"; printf 'SD_PUBLIC_HOSTS=staging.example.test\n' > "$a/config/app.env"; printf 'SD_ENV=staging\n' > "$d/config/db.env"
  printf 'CREATE DATABASE [$(DbName)];\n' > "$d/scripts/provision-database.sql"
  run_pf "$fx" staging --skip-dns
  rc_is 1
  check FAIL "staging: the installed $d/scripts/provision-database.sql has no READ_COMMITTED_SNAPSHOT. Run install.sh staging from a checkout at 8bd27a7 or later BEFORE db-provision.sh"
}
sc_installed_current() {
  local fx="$1"; fx_baseline "$fx"
  local a="$fx/apps/staging/spencer-denim-app" d="$fx/db/staging"
  mkdir -p "$a/scripts" "$a/config" "$d/config" "$d/scripts"
  : > "$a/scripts/deploy.sh"; printf 'SD_PUBLIC_HOSTS=staging.example.test\n' > "$a/config/app.env"; printf 'SD_ENV=staging\n' > "$d/config/db.env"
  cat "$repo_root/tools/db/provision-database.sql" > "$d/scripts/provision-database.sql"
  run_pf "$fx" staging
  rc_is 0; no_failures
  check PASS "staging: the installed provision-database.sql sets READ_COMMITTED_SNAPSHOT"
  check PASS "DNS: staging.example.test -> 203.0.113.10"
  refute WARN "differs from this checkout"
}
sc_nginx_none() {
  local fx="$1"; fx_baseline "$fx"
  printf '%s\n' "$nginx_conf_header
# configuration file /etc/nginx/sites-enabled/other:
server {
    # listen 80 default_server;
    listen 80;
    listen 443 ssl;
    server_name other.example;
}" > "$fx/nginx_T"
  run_pf "$fx" staging --skip-dns
  rc_is 0
  check INFO "default_server for port 80 IPv4: none"
  check INFO "default_server for port 443 IPv6: none"
  check WARN "NO default_server on 80 or 443"
  check WARN "DECISION: install deploy/host-nginx/spencer-denim-00-default.conf.template"
}
sc_nginx_partial() {
  local fx="$1"; fx_baseline "$fx"
  printf '%s\n' "$nginx_conf_header
# configuration file /etc/nginx/sites-enabled/default:
server { listen 80 default_server; listen 127.0.0.1:443 ssl default_server; server_name _; }" > "$fx/nginx_T"
  run_pf "$fx" staging --skip-dns
  check INFO "default_server for port 80 IPv4: /etc/nginx/sites-enabled/default (listen 80)"
  check INFO "default_server for port 443 IPv4: /etc/nginx/sites-enabled/default (listen 127.0.0.1:443)"
  check INFO "default_server for port 80 IPv6: none"
  check WARN "DECISION: do NOT install the catch-all file as it is"
}
sc_nginx_ours() {
  local fx="$1"; fx_baseline "$fx"
  printf '%s\n' "$nginx_conf_header
# configuration file /etc/nginx/sites-enabled/spencer-denim-00-default.conf:
server { listen 80 default_server; listen [::]:80 default_server; server_name _; return 444; }
server { listen 443 ssl http2 default_server; listen [::]:443 ssl http2 default_server; server_name _; ssl_reject_handshake on; }" > "$fx/nginx_T"
  run_pf "$fx" staging --skip-dns
  check PASS "the catch-all from deploy/host-nginx/spencer-denim-00-default.conf.template is already installed"
}
sc_nginx_unreadable() {
  local fx="$1"; fx_baseline "$fx"; rm -f "$fx/nginx_T"
  run_pf "$fx" staging --skip-dns
  check WARN "could not read nginx's configuration with 'nginx -T'"
  refute WARN "NO default_server"
}
sc_server_name_conflict() {
  local fx="$1"; fx_baseline "$fx"
  printf '%s\n' "$nginx_all_defaults
# configuration file /etc/nginx/sites-enabled/old-site.conf:
server { listen 80; server_name staging.example.test www.old.example; }
# configuration file /etc/nginx/sites-enabled/spencer-denim-production.conf:
server { listen 80; server_name shop.example.test; }" > "$fx/nginx_T"
  run_pf "$fx" both staging.example.test shop.example.test
  check WARN "staging.example.test is already a server_name in /etc/nginx/sites-enabled/old-site.conf (not ours)"
  check INFO "shop.example.test: already named in /etc/nginx/sites-enabled/spencer-denim-production.conf (this project's own staging/production site file)"
}
# The first UAT on a server that already runs an earlier stack of this project under its own site file.
sc_server_name_earlier_stack() {
  local fx="$1"; fx_baseline "$fx"
  printf '%s
' "$nginx_all_defaults
# configuration file /etc/nginx/sites-enabled/spencer-denim-development:
server { listen 443 ssl; server_name shop.example.test www.shop.example.test; }" > "$fx/nginx_T"
  run_pf "$fx" production shop.example.test
  check WARN "shop.example.test is already a server_name in /etc/nginx/sites-enabled/spencer-denim-development: a site file of THIS project but not the staging/production one"
  refute INFO "shop.example.test: already named in"
}
# Asking about staging alone must budget for staging alone, not for a production SQL Server that does not exist yet.
sc_memory_staging_alone() {
  local fx="$1"; fx_baseline "$fx"
  printf 'MemTotal: 8192000 kB
MemAvailable: 3072000 kB
SwapTotal: 4194304 kB
' > "$fx/meminfo"
  run_pf "$fx" staging --skip-dns
  rc_is 0; no_failures
  check PASS "memory: 3000 MB available covers the staging environment alone"
  refute WARN "UAT on the production"
  printf 'MemTotal: 8192000 kB
MemAvailable: 2048000 kB
SwapTotal: 4194304 kB
' > "$fx/meminfo"
  run_pf "$fx" staging --skip-dns
  rc_is 1
  check FAIL "memory: only 2000 MB available; the staging environment alone"
}
# A directory below the install base that already holds files, before install.sh has been run, may be another project's.
sc_install_dir_not_empty() {
  local fx="$1"; fx_baseline "$fx"
  mkdir -p "$fx/db/staging/data" "$fx/db/staging/config"; echo x > "$fx/db/staging/data/foreign.mdf"
  run_pf "$fx" staging --skip-dns
  check WARN "staging: $fx/db/staging already holds files"
  rm -rf "$fx/db/staging"; mkdir -p "$fx/db/staging/data" "$fx/db/staging/config"
  run_pf "$fx" staging --skip-dns
  refute WARN "already holds files"
}
sc_dns_public_ip() {
  local fx="$1"; fx_baseline "$fx"
  # A private address on the default route: the public one must come from the single outside lookup.
  echo 10.0.0.5 > "$fx/src"; echo 203.0.113.10 > "$fx/ipify"
  run_pf "$fx" staging staging.example.test
  check INFO "this server's public IPv4: 203.0.113.10 (from api.ipify.org"
  check PASS "DNS: staging.example.test -> 203.0.113.10"
  grep -q 'api.ipify.org' "$fx/calls.log" || fails="$fails\n    the ipify lookup was not made"
}
sc_dns_public_ip_unknown() {
  local fx="$1"; fx_baseline "$fx"
  echo 10.0.0.5 > "$fx/src"; rm -f "$fx/ipify"
  echo "2: eth0 inet 10.0.0.5/24 brd 10.0.0.255 scope global eth0" > "$fx/v4addr"
  NO_EXTERNAL=1 run_pf "$fx" staging staging.example.test
  rc_is 0
  check WARN "could not work out this server's public IPv4"
  check WARN "DNS: staging.example.test -> 203.0.113.10 (via dig @1.1.1.1), but this server's public address is unknown"
  if grep -q 'api.ipify.org' "$fx/calls.log"; then fails="$fails\n    an outside lookup was made although SD_PREFLIGHT_NO_EXTERNAL=1"; fi
}
sc_dns_given_ip() {
  local fx="$1"; fx_baseline "$fx"
  echo 10.0.0.5 > "$fx/src"
  run_pf "$fx" staging staging.example.test --public-ip 203.0.113.10
  check INFO "this server's public IPv4: 203.0.113.10 (from given)"
  check PASS "DNS: staging.example.test -> 203.0.113.10"
}
sc_old_checkout() {
  local fx="$1"; fx_baseline "$fx"
  local r="$fx/oldrepo"; mkdir -p "$r/deploy/scripts" "$r/deploy/app" "$r/tools/db"
  cp "$repo_root/deploy/scripts/preflight.sh" "$repo_root/deploy/scripts/lib.sh" "$r/deploy/scripts/"
  : > "$r/deploy/app/compose.yml"; echo "CREATE DATABASE [x];" > "$r/tools/db/provision-database.sql"
  ( cd "$r" && git init -q . && git -c user.name=t -c user.email=t@t add -A && git -c user.name=t -c user.email=t@t commit -q -m old ) >/dev/null 2>&1
  PF_SCRIPT="$r/deploy/scripts/preflight.sh" run_pf "$fx" staging --skip-dns
  rc_is 1
  check FAIL "this checkout's tools/db/provision-database.sql does not mention READ_COMMITTED_SNAPSHOT"
}
sc_not_root() {
  local fx="$1"; fx_baseline "$fx"; echo 1000 > "$fx/uid"
  run_pf "$fx" staging --skip-dns
  check WARN "not running as root (tester)"
}
sc_read_only() {
  # The healthy scenario again, with the directories it looks at snapshotted before and after, and the fakes' call log read.
  local fx="$1"; fx_baseline "$fx"
  mkdir -p "$fx/apps/staging/spencer-denim-app/config" "$fx/db/staging/config"
  printf 'SD_PUBLIC_HOSTS=staging.example.test\n' > "$fx/apps/staging/spencer-denim-app/config/app.env"
  snap() { ( cd "$fx" && find apps db le webroot -type f -o -type d | sort | while read -r p; do if [ -f "$p" ]; then printf '%s %s\n' "$p" "$(cksum < "$p")"; else echo "$p/"; fi; done ); }
  local before after; before="$(snap)"
  run_pf "$fx" both staging.example.test shop.example.test
  after="$(snap)"
  [ "$before" = "$after" ] || fails="$fails\n    the directories it inspects changed"
  no_cmd_not_found
  [ -s "$fx/violations.log" ] && fails="$fails\n    a fake command was asked for something other than a read: $(head -n 3 "$fx/violations.log" | tr '\n' ';')"
  # Every docker call must be one of the read-only verbs.
  if grep '^docker ' "$fx/calls.log" | grep -vE '^docker (version|compose version|info|network (ls|inspect)|ps) ' >/dev/null; then
    fails="$fails\n    docker was asked to do something that is not a read: $(grep '^docker ' "$fx/calls.log" | grep -vE '^docker (version|compose version|info|network (ls|inspect)|ps) ' | head -n 2 | tr '\n' ';')"
  fi
  if grep '^nginx ' "$fx/calls.log" | grep -vE '^nginx (-v|-T)$' >/dev/null; then fails="$fails\n    nginx was called with something other than -v / -T"; fi
  if grep '^certbot ' "$fx/calls.log" | grep -v '^certbot --version$' >/dev/null; then fails="$fails\n    certbot was called with something other than --version"; fi
  [ -s "$fx/calls.log" ] || fails="$fails\n    the fakes were never called (is the PATH wrong?)"
}

# ---- runner ----------------------------------------------------------------------------------------------------------------------
n=0
run_sc() {
  local name="$1"
  [ -z "$only" ] || case "$name" in *"$only"*) ;; *) return 0 ;; esac
  n=$((n + 1))
  while [ "$(jobs -rp | wc -l)" -ge "$jobs_max" ]; do sleep 0.3; done
  (
    fails=""
    "sc_${name}" "$work/fx.$n"
    if [ -z "$fails" ]; then echo "ok    $name"; echo 0 > "$work/status.$n"
    else
      printf 'FAIL  %s%b\n' "$name" "$fails"
      printf '%s\n' "$PF_OUT" | sed 's/^/        | /' | head -n 70
      echo 1 > "$work/status.$n"
    fi
  ) > "$work/out.$n" 2>&1 &
}
for s in good everything_wrong missing_tools engine_down own_resources foreign_container_on_port memory_fallback \
         installed_stale installed_without_rcsi installed_current nginx_none nginx_partial nginx_ours nginx_unreadable \
         server_name_conflict server_name_earlier_stack memory_staging_alone install_dir_not_empty dns_public_ip dns_public_ip_unknown dns_given_ip old_checkout not_root read_only; do
  run_sc "$s"
done
wait
passed=0 failed=0 i=1
while [ "$i" -le "$n" ]; do
  cat "$work/out.$i"
  if [ "$(cat "$work/status.$i" 2>/dev/null)" = 0 ]; then passed=$((passed + 1)); else failed=$((failed + 1)); fi
  i=$((i + 1))
done
printf '\n%s scenario(s) as expected, %s not\n' "$passed" "$failed"
[ "$failed" -eq 0 ]
