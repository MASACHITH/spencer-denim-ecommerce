#!/usr/bin/env bash
# Usage: sudo preflight.sh [staging|production|both] [host ...] [--host NAME]... [--public-ip IP] [--skip-dns]
#
# READ-ONLY server preflight (#195, docs/16 §B step 1). Run it on the VPS BEFORE install.sh, and again after DNS has
# been changed. It creates, changes and starts nothing and prints no secret, so its whole output is safe to paste into
# the issue. (If the env files already exist it looks in them for two things only: the text CHANGE_ME, which it counts,
# and SD_PUBLIC_HOSTS, a host name.) One line per check: PASS / WARN / FAIL / INFO, then a summary. Exit status 1 if any FAIL.
#
# Hosts to check in DNS: arguments (bare words or --host), or $SD_PREFLIGHT_HOSTS (space separated), or, for an
# environment that is already installed, SD_PUBLIC_HOSTS from its config/app.env. Nothing is guessed.
#
# Other knobs (all optional): SD_PUBLIC_IP / --public-ip (this server's public IPv4, if it cannot work it out),
# SD_PREFLIGHT_NO_EXTERNAL=1 (never ask an outside service for the public IP), SD_PREFLIGHT_RESOLVER (default 1.1.1.1),
# SD_PREFLIGHT_DISK_FAIL_GB / SD_PREFLIGHT_DISK_WARN_GB (defaults 12 / 25). The only network traffic is DNS lookups and,
# when the address cannot be read from the machine itself, ONE request to api.ipify.org.
#
# What it checks is listed in docs/16 §B step 1; the numbers it compares memory with are the container limits in
# deploy/app/compose.<env>.yml and deploy/database/.env.example.
source "$(dirname "$(readlink -f "$0")")/lib.sh"
set +e +o pipefail   # lib.sh is strict; a preflight must keep going after a failed probe
export LC_ALL=C

# ---------------------------------------------------------------------------------------------------------------
# arguments
# ---------------------------------------------------------------------------------------------------------------
want=both
hosts=()
public_ip="${SD_PUBLIC_IP:-}"
skip_dns=0
first=1
while [ $# -gt 0 ]; do
  case "$1" in
    staging|production|both) if [ "$first" -eq 1 ]; then want="$1"; else hosts+=("$1"); fi ;;
    --host)       shift; hosts+=("${1:-}") ;;
    --public-ip)  shift; public_ip="${1:-}" ;;
    --skip-dns)   skip_dns=1 ;;
    -h|--help)    sed -n '2,18p' "$0"; exit 0 ;;
    -*)           die "unknown option '$1'" ;;
    *)            hosts+=("$1") ;;
  esac
  first=0
  shift
done
for h in ${SD_PREFLIGHT_HOSTS:-}; do hosts+=("$h"); done

case "$want" in
  both)       envs=(staging production) ;;
  *)          envs=("$want") ;;
esac

# ---------------------------------------------------------------------------------------------------------------
# output
# ---------------------------------------------------------------------------------------------------------------
n_pass=0 n_warn=0 n_fail=0
if [ -t 1 ] && [ -z "${NO_COLOR:-}" ]; then
  c_pass=$'\033[32m' c_warn=$'\033[33m' c_fail=$'\033[31m' c_off=$'\033[0m'
else
  c_pass="" c_warn="" c_fail="" c_off=""
fi
pass() { n_pass=$((n_pass + 1)); printf '%sPASS%s  %s\n' "$c_pass" "$c_off" "$*"; }
warnl() { n_warn=$((n_warn + 1)); printf '%sWARN%s  %s\n' "$c_warn" "$c_off" "$*"; }
fail() { n_fail=$((n_fail + 1)); printf '%sFAIL%s  %s\n' "$c_fail" "$c_off" "$*"; }
info() { printf 'INFO  %s\n' "$*"; }
head1() { printf '\n== %s\n' "$*"; }
have() { command -v "$1" >/dev/null 2>&1; }

# ver_ge A B: dotted numeric versions, A >= B.
ver_ge() {
  awk -v a="$1" -v b="$2" 'BEGIN {
    n = split(a, x, "."); m = split(b, y, ".")
    for (i = 1; i <= (n > m ? n : m); i++) { xi = x[i] + 0; yi = y[i] + 0; if (xi > yi) exit 0; if (xi < yi) exit 1 }
    exit 0 }'
}
# IPv4 / CIDR helpers (bash integer arithmetic).
is_ipv4() { printf '%s' "$1" | grep -Eq '^[0-9]{1,3}(\.[0-9]{1,3}){3}$'; }
ip2int() { local a b c d; IFS=. read -r a b c d <<EOF
$1
EOF
  echo $(( (a << 24) | (b << 16) | (c << 8) | d )); }
cidr_range() {   # "10.251.0.0/24" -> "start end" as integers
  local ip len n mask
  case "$1" in */*) ip="${1%/*}"; len="${1#*/}" ;; *) ip="$1"; len=32 ;; esac
  n="$(ip2int "$ip")"
  mask=$(( len == 0 ? 0 : (0xFFFFFFFF << (32 - len)) & 0xFFFFFFFF ))
  echo "$(( n & mask )) $(( (n & mask) | (~mask & 0xFFFFFFFF) ))"
}
overlaps() {
  local s1 e1 s2 e2
  read -r s1 e1 <<EOF
$(cidr_range "$1")
EOF
  read -r s2 e2 <<EOF
$(cidr_range "$2")
EOF
  [ "$s1" -le "$e2" ] && [ "$s2" -le "$e1" ]
}
is_private_v4() {   # RFC1918, CGNAT, loopback, link-local
  case "$1" in
    10.*|127.*|169.254.*|192.168.*) return 0 ;;
    172.1[6-9].*|172.2[0-9].*|172.3[0-1].*) return 0 ;;
    100.6[4-9].*|100.[7-9][0-9].*|100.1[01][0-9].*|100.12[0-7].*) return 0 ;;
  esac
  return 1
}

repo="$(cd "$(dirname "$(readlink -f "$0")")/../.." 2>/dev/null && pwd)"
[ -f "$repo/deploy/app/compose.yml" ] || repo=""   # running from an installed scripts/ copy: no checkout next to it

# ---------------------------------------------------------------------------------------------------------------
head1 "This machine"
# ---------------------------------------------------------------------------------------------------------------
info "$(date -u +%Y-%m-%dT%H:%M:%SZ) on $(hostname 2>/dev/null || echo unknown), $(uname -sr 2>/dev/null)"
osrel="${SD_PREFLIGHT_OSRELEASE:-/etc/os-release}"
if [ -r "$osrel" ]; then info "OS: $(sed -n 's/^PRETTY_NAME=//p' "$osrel" | tr -d '"')"; fi
is_root=0; [ "$(id -u)" -eq 0 ] && is_root=1
if [ "$is_root" -eq 1 ]; then
  pass "running as root"
else
  warnl "not running as root ($(id -un)): nginx -T, docker and /opt may be unreadable. Re-run with sudo for a complete answer"
fi
if [ -n "$repo" ]; then
  commit="$(git -C "$repo" rev-parse --short=12 HEAD 2>/dev/null)"
  if [ -n "$commit" ]; then
    dirty=""; [ -n "$(git -C "$repo" status --porcelain --untracked-files=no 2>/dev/null)" ] && dirty=" (uncommitted changes in the working tree)"
    info "checkout: $repo at $commit$dirty"
    if git -C "$repo" merge-base --is-ancestor 8bd27a7 HEAD 2>/dev/null; then
      pass "checkout contains the RCSI fix (8bd27a7, #183), so install.sh will copy a provision-database.sql that turns READ_COMMITTED_SNAPSHOT on"
    elif git -C "$repo" cat-file -e 8bd27a7 2>/dev/null; then
      fail "checkout does NOT contain 8bd27a7 (#183): update it first (git fetch && git checkout <release commit>), or the live database silently lacks READ_COMMITTED_SNAPSHOT"
    fi
  fi
else
  info "no repository checkout next to this script (an installed copy): checkout checks skipped"
fi

# ---------------------------------------------------------------------------------------------------------------
head1 "Tools"
# ---------------------------------------------------------------------------------------------------------------
for t in git curl openssl; do
  if have "$t"; then
    case "$t" in openssl) tv="$(openssl version 2>&1 | head -n 1)" ;; *) tv="$("$t" --version 2>&1 | head -n 1)" ;; esac
    pass "$t: $tv"
  else
    fail "$t is not installed (apt install $t)"
  fi
done
if have nginx; then pass "nginx: $(nginx -v 2>&1 | head -n 1)"; else fail "nginx is not installed (the design needs the host nginx, docs/15 §10 #1)"; fi
if have certbot; then
  pass "certbot: $(certbot --version 2>&1 | head -n 1)"
elif [ -x /snap/bin/certbot ]; then
  pass "certbot: $(/snap/bin/certbot --version 2>&1 | head -n 1) (snap; add /snap/bin to sudo's PATH or call it by full path)"
else
  fail "certbot is not installed (apt install certbot, or snap install --classic certbot)"
fi
if have ss; then :; else warnl "ss is not installed (apt install iproute2): port checks below cannot run"; fi

# ---------------------------------------------------------------------------------------------------------------
head1 "Docker"
# ---------------------------------------------------------------------------------------------------------------
docker_ok=0
docker_root=""
if ! have docker; then
  fail "docker is not installed (Docker Engine with the Compose plugin >= 2.24)"
else
  engine="$(docker version --format '{{.Server.Version}}' 2>/dev/null)"
  if [ -n "$engine" ]; then
    pass "Docker Engine $engine is running and answers"
    docker_ok=1
    docker_root="$(docker info --format '{{.DockerRootDir}}' 2>/dev/null)"
  else
    fail "docker is installed but the engine does not answer (not running, or this user may not use it: run with sudo)"
  fi
  cv="$(docker compose version --short 2>/dev/null | sed 's/^v//')"
  if [ -z "$cv" ]; then
    fail "the Docker Compose plugin is missing (docker compose version fails). Install docker-compose-plugin, version >= 2.24"
  elif ver_ge "$cv" 2.24.0; then
    pass "Docker Compose $cv (>= 2.24)"
  else
    fail "Docker Compose $cv is older than 2.24, which the compose files and deploy.sh need"
  fi
fi

# ---------------------------------------------------------------------------------------------------------------
head1 "Ports (loopback, one per environment) and subnets"
# ---------------------------------------------------------------------------------------------------------------
listen_lines=""
if have ss; then listen_lines="$(ss -ltn 2>/dev/null | grep -E '^LISTEN')"; fi

port_in_use() { printf '%s\n' "$listen_lines" | awk -v p="$1" '$4 ~ ("[:.]" p "$") { f = 1 } END { exit f ? 0 : 1 }'; }

if have ss; then
  for e in "${envs[@]}"; do
    p="$(http_port "$e")"
    if port_in_use "$p"; then
      owner=""
      [ "$docker_ok" -eq 1 ] && owner="$(docker ps --filter "publish=$p" --format '{{.Names}}' 2>/dev/null | head -n 1)"
      case "$owner" in
        "sd-$e"-*) pass "port $p ($e): already published by this environment's own gateway ($owner)" ;;
        *)         fail "port $p ($e) is already in use${owner:+ by container $owner}: choose another port, or find the owner with: ss -ltnp | grep :$p  (docs/15 §10 #3 lists what to change together)" ;;
      esac
    else
      pass "port $p ($e) is free"
    fi
  done
fi

# Both environments' edge subnets matter whichever environment is being installed first.
targets=("10.251.0.0/24:production" "10.251.1.0/24:staging")
conflicts=0
checked_nets=0
own_bridges=""
if [ "$docker_ok" -eq 1 ]; then
  net_ids="$(docker network ls -q 2>/dev/null)"
  if [ -n "$net_ids" ]; then
    # shellcheck disable=SC2086
    net_lines="$(docker network inspect $net_ids --format '{{.Name}} {{slice .Id 0 12}} {{range .IPAM.Config}}{{.Subnet}} {{end}}' 2>/dev/null)"
    while read -r nname nid subnets; do
      [ -n "$nname" ] || continue
      checked_nets=$((checked_nets + 1))
      for sn in $subnets; do
        printf '%s' "$sn" | grep -Eq '^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+/[0-9]+$' || continue
        for t in "${targets[@]}"; do
          tcidr="${t%%:*}"; tenv="${t##*:}"
          if overlaps "$sn" "$tcidr"; then
            if [ "$nname" = "sd-${tenv}_edge" ] && [ "$sn" = "$tcidr" ]; then
              own_bridges="$own_bridges br-$nid"
              info "docker network $nname already holds $tcidr (this environment's own edge network)"
            else
              fail "docker network '$nname' uses $sn, which overlaps $tcidr (the $tenv edge subnet). Change the subnet in deploy/app/compose.$tenv.yml together with lib.sh/the nginx site (docs/15 §10 #3)"
              conflicts=$((conflicts + 1))
            fi
          fi
        done
      done
    done <<EOF
$net_lines
EOF
  fi
fi
route_count=0
if have ip; then
  while read -r dest rest; do
    [ -n "$dest" ] || continue
    [ "$dest" = default ] && continue
    printf '%s' "$dest" | grep -Eq '^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+(/[0-9]+)?$' || continue
    route_count=$((route_count + 1))
    dev="$(printf ' %s' "$rest" | sed -n 's/.*[[:space:]]dev[[:space:]]\{1,\}\([^[:space:]]*\).*/\1/p')"
    skip=0
    for ob in $own_bridges; do [ "$dev" = "$ob" ] && skip=1; done
    [ "$skip" -eq 1 ] && continue
    for t in "${targets[@]}"; do
      tcidr="${t%%:*}"; tenv="${t##*:}"
      if overlaps "$dest" "$tcidr"; then
        fail "host route '$dest dev ${dev:-?}' overlaps $tcidr (the $tenv edge subnet). Something else already owns that range"
        conflicts=$((conflicts + 1))
      fi
    done
  done <<EOF
$(ip -4 route show 2>/dev/null)
EOF
fi
if [ "$conflicts" -eq 0 ]; then
  if [ "$docker_ok" -eq 1 ] || have ip; then
    pass "no docker network or host route overlaps 10.251.0.0/24 or 10.251.1.0/24 (looked at $checked_nets docker networks, $route_count routes)"
  else
    warnl "could not look at docker networks or routes (no docker, no ip): subnets unchecked"
  fi
fi

# ---------------------------------------------------------------------------------------------------------------
head1 "Memory, disk, CPU against the budget"
# ---------------------------------------------------------------------------------------------------------------
meminfo="${SD_PREFLIGHT_MEMINFO:-/proc/meminfo}"
mem_total=0 mem_avail=0 swap_total=0
if [ -r "$meminfo" ]; then
  mem_total=$(awk '/^MemTotal:/ { printf "%d", $2 / 1024 }' "$meminfo")
  mem_avail=$(awk '/^MemAvailable:/ { printf "%d", $2 / 1024 }' "$meminfo")
  swap_total=$(awk '/^SwapTotal:/ { printf "%d", $2 / 1024 }' "$meminfo")
  [ -n "$mem_avail" ] && [ "$mem_avail" -gt 0 ] || mem_avail=$(awk '/^MemFree:/ { printf "%d", $2 / 1024 }' "$meminfo")
fi
# Container ceilings from the compose files / db.env template, in MB.
sql_prod=2560 sql_stg=1536                  # SD_MSSQL_CONTAINER_MEMORY (Express caps its buffer pool at 1,410 MB anyway)
app_prod=$((768 + 512 + 128))               # api + storefront + web, compose.production.yml
app_stg=$((512 + 384 + 96))                 # compose.staging.yml
migrator=384                                # one-shot, only while a deploy runs
need_two=$((sql_prod + sql_stg + app_prod + app_stg + migrator))
need_shared=$((sql_prod + app_prod + app_stg + migrator))
need_stg=$((sql_stg + app_stg + migrator))
need_prod=$((sql_prod + app_prod + migrator))
# What this run is for decides the budget: asking about staging alone must not be told it needs a production SQL
# Server that is not going to exist yet (the first UAT on a server that already hosts other things).
case "$want" in
  staging)    need_here=$need_stg;  plan="the staging environment alone (its own SQL Server ${sql_stg} + app ${app_stg} + migrator ${migrator})" ;;
  production) need_here=$need_prod; plan="the production environment alone (its own SQL Server ${sql_prod} + app ${app_prod} + migrator ${migrator})" ;;
  *)          need_here=$need_two;  plan="both environments with their own SQL Server" ;;
esac
info "budget (container ceilings): production SQL ${sql_prod} + staging SQL ${sql_stg} + production app ${app_prod} + staging app ${app_stg} + migrator ${migrator} = ${need_two} MB with two SQL Server instances; ${need_shared} MB if UAT shares the production instance; ${need_stg} MB for staging alone, ${need_prod} MB for production alone"
if [ "$mem_total" -gt 0 ]; then
  info "this machine: ${mem_total} MB RAM, ${mem_avail} MB available now (what the sites already running here leave free), ${swap_total} MB swap"
  if [ "$mem_avail" -ge "$need_here" ]; then
    pass "memory: ${mem_avail} MB available covers ${plan} (${need_here} MB)"
  elif [ "$want" = both ] && [ "$mem_avail" -ge "$need_shared" ]; then
    warnl "memory: ${mem_avail} MB available is below ${need_two} MB (two SQL Server instances) but covers ${need_shared} MB. Use the docs/15 §10 #2 fallback: point UAT's connection strings at the PRODUCTION SQL instance (database SpencerDenim_Staging, its own logins) and do not start the staging database stack. Decide before step B3"
  elif [ "$want" = both ]; then
    fail "memory: only ${mem_avail} MB available; even the shared-instance plan needs ${need_shared} MB. Free memory (stop something), resize the VPS, or run only one environment for now"
  else
    fail "memory: only ${mem_avail} MB available; ${plan} needs ${need_here} MB. Free memory (stop something) or resize the VPS"
  fi
  if [ "$swap_total" -eq 0 ]; then
    warnl "no swap is configured: a spike during an image build (Angular + .NET) is then an out-of-memory kill. Watch 'free -m' during the first build-images.sh, or build elsewhere (docs/15 §10 #4)"
  fi
else
  warnl "could not read memory from $meminfo"
fi

disk_fail="${SD_PREFLIGHT_DISK_FAIL_GB:-12}" disk_warn="${SD_PREFLIGHT_DISK_WARN_GB:-25}"
check_disk() {   # label path
  local label="$1" path="$2" kb gb mount
  while [ -n "$path" ] && [ ! -e "$path" ] && [ "$path" != / ]; do path="$(dirname "$path")"; done
  # Counted from the right, so a file system name with a space in it does not shift the columns.
  kb="$(df -Pk "$path" 2>/dev/null | awk 'NR == 2 { print $(NF - 2) }')"
  mount="$(df -Pk "$path" 2>/dev/null | awk 'NR == 2 { print $NF }')"
  [ -n "$kb" ] || { warnl "disk: could not read free space for $label ($path)"; return; }
  gb=$((kb / 1024 / 1024))
  if [ "$gb" -lt "$disk_fail" ]; then
    fail "disk: ${gb} GB free on $mount ($label). Below ${disk_fail} GB the images, two databases and their backups will not fit"
  elif [ "$gb" -lt "$disk_warn" ]; then
    warnl "disk: ${gb} GB free on $mount ($label); under ${disk_warn} GB gets tight once images, build cache, databases and 35 days of backups pile up"
  else
    pass "disk: ${gb} GB free on $mount ($label)"
  fi
}
check_disk "/opt, databases and uploads" /opt
if [ -n "$docker_root" ]; then check_disk "Docker images and build cache, $docker_root" "$docker_root"; fi
info "disk thresholds are this script's own working figures (FAIL under ${disk_fail} GB, WARN under ${disk_warn} GB), not from the docs: change them with SD_PREFLIGHT_DISK_FAIL_GB / _WARN_GB"

ncpu="$(nproc 2>/dev/null || echo 0)"
if [ "$ncpu" -ge 2 ]; then pass "cpu: $ncpu vCPUs"; elif [ "$ncpu" -gt 0 ]; then warnl "cpu: $ncpu vCPU; the compose files allow the production API 1.0 and staging 0.5 CPU, builds will be slow"; fi
cpuinfo="${SD_PREFLIGHT_CPUINFO:-/proc/cpuinfo}"
if [ -r "$cpuinfo" ] && [ "$(uname -m 2>/dev/null)" = x86_64 ]; then
  flags="$(grep -m1 '^flags' "$cpuinfo" 2>/dev/null)"
  missing=""
  for f in sse4_2 popcnt ssse3; do case " $flags " in *" $f "*) ;; *) missing="$missing $f" ;; esac; done
  if [ -n "$flags" ] && [ -n "$missing" ]; then
    warnl "cpu: the virtual CPU does not advertise${missing}. SQL Server 2022 and later need an x86-64-v2 CPU; if the sqlserver container exits at start, this is why (ask Hostinger for a different CPU model)"
  fi
fi
ntp="$(timedatectl show -p NTPSynchronized --value 2>/dev/null)"
if [ "$ntp" = no ]; then warnl "the system clock is not synchronised (timedatectl): TLS validation and certificate dates depend on it"; fi

# ---------------------------------------------------------------------------------------------------------------
head1 "Layout on disk, and whether install.sh has been run"
# ---------------------------------------------------------------------------------------------------------------
for d in "$SD_APPS_BASE" "$SD_DB_BASE"; do
  if [ -d "$d" ]; then pass "$d exists"; else warnl "$d does not exist. The server's convention is /opt/apps and /opt/server (docs/15 §2); install.sh creates the environment directories below it, as root"; fi
done
for e in "${envs[@]}"; do
  a="$(app_root "$e")"; d="$(db_root "$e")"
  if [ -f "$a/scripts/deploy.sh" ] || [ -f "$a/config/app.env" ] || [ -f "$d/config/db.env" ]; then
    info "$e: install.sh HAS been run (found under $a / $d)"
    for f in "$a/config/app.env" "$d/config/db.env"; do
      if [ -f "$f" ]; then
        if grep -E '^[A-Za-z_][A-Za-z0-9_]*=' "$f" | grep -q 'CHANGE_ME'; then
          warnl "$f still contains CHANGE_ME placeholders (fill them in; then check-env.sh $e)"
        else
          pass "$f has no CHANGE_ME placeholders"
        fi
      else
        warnl "$f is missing (install.sh creates it from the template)"
      fi
    done
    # The RCSI trap (#183): db-provision.sh runs the COPY in scripts/, which install.sh refreshes from the checkout.
    inst="$d/scripts/provision-database.sql"
    if [ -f "$inst" ]; then
      if ! grep -q 'READ_COMMITTED_SNAPSHOT' "$inst"; then
        fail "$e: the installed $inst has no READ_COMMITTED_SNAPSHOT. Run install.sh $e from a checkout at 8bd27a7 or later BEFORE db-provision.sh"
      elif [ -n "$repo" ] && [ -f "$repo/tools/db/provision-database.sql" ] && ! cmp -s "$inst" "$repo/tools/db/provision-database.sql"; then
        warnl "$e: the installed provision-database.sql differs from this checkout's. Run install.sh $e again before db-provision.sh"
      else
        pass "$e: the installed provision-database.sql sets READ_COMMITTED_SNAPSHOT"
      fi
    fi
  else
    info "$e: install.sh has NOT been run yet (nothing under $a or $d): expected before step B4"
    # install.sh sets owner and mode on the directories below $d and $a. If something else already lives there (another
    # project's data, a SQL Server's files), changing them can break it. An empty skeleton is fine.
    for x in "$a" "$d"; do
      if [ -d "$x" ]; then
        found="$(find "$x" -type f 2>/dev/null | head -n 5)"
        if [ -n "$found" ]; then
          warnl "$e: $x already holds files ($(printf '%s' "$found" | tr '
' ' ')) although install.sh has not been run. Look with 'ls -laR $x' and make sure nothing there belongs to another project or a running container before step B4: install.sh sets the owner and mode of the directories it uses. (Empty sub-directories are fine: that is an unused skeleton.)"
        fi
      fi
    done
  fi
done
if [ -n "$repo" ]; then
  if grep -q 'READ_COMMITTED_SNAPSHOT' "$repo/tools/db/provision-database.sql" 2>/dev/null; then
    pass "this checkout's tools/db/provision-database.sql sets READ_COMMITTED_SNAPSHOT"
  else
    fail "this checkout's tools/db/provision-database.sql does not mention READ_COMMITTED_SNAPSHOT: it is older than #183"
  fi
fi

# ---------------------------------------------------------------------------------------------------------------
head1 "Host nginx"
# ---------------------------------------------------------------------------------------------------------------
nginx_dump=""
if have nginx; then
  nv="$(nginx -v 2>&1 | sed -n 's|.*nginx/\([0-9.]*\).*|\1|p')"
  if [ -n "$nv" ]; then
    if ver_ge "$nv" 1.19.4; then pass "nginx $nv supports ssl_reject_handshake (needed by the catch-all template)"; else warnl "nginx $nv is older than 1.19.4: the catch-all template's ssl_reject_handshake will not work; use a self-signed default certificate instead"; fi
  fi
  nginx_dump="$(nginx -T 2>&1)"
  if ! printf '%s\n' "$nginx_dump" | grep -q '^# configuration file '; then
    warnl "could not read nginx's configuration with 'nginx -T' ($(printf '%s\n' "$nginx_dump" | grep -m1 -E 'emerg|alert|denied|error' | cut -c1-140)). Re-run with sudo; the default_server and server_name checks below were skipped"
    nginx_dump=""
  elif printf '%s\n' "$nginx_dump" | grep -qE '\[(emerg|alert)\]'; then
    warnl "nginx -T reports a problem with the CURRENT configuration (fix that first): $(printf '%s\n' "$nginx_dump" | grep -m1 -E '\[(emerg|alert)\]' | cut -c1-160)"
  else
    pass "nginx -T reads the current configuration without errors"
  fi
fi

if [ -n "$nginx_dump" ]; then
  # Every "listen ... default_server" with the file it is in, the port and the address family.
  defaults="$(printf '%s\n' "$nginx_dump" | awk '
    /^# configuration file / { file = $4; sub(/:$/, "", file); next }
    { line = $0; sub(/#.*/, "", line) }
    line ~ /listen[ \t]/ && line ~ /default_server/ {
      # one statement at a time: a compact config can hold several listen directives on one line
      ns = split(line, stmt, /;/)
      for (k = 1; k <= ns; k++) {
        if (stmt[k] !~ /default_server/) continue
        n = split(stmt[k], t, /[ \t]+/); arg = ""
        for (i = 1; i <= n; i++) if (t[i] == "listen") { arg = t[i + 1]; break }
        if (arg == "" || arg ~ /^unix:/) continue
        fam = "v4"; port = 80
        if (arg ~ /^\[/) { fam = "v6"; if (arg ~ /\]:[0-9]+$/) { port = arg; sub(/.*\]:/, "", port) } }
        else if (arg ~ /^[0-9]+$/) { port = arg }
        else if (arg ~ /:[0-9]+$/) { port = arg; sub(/.*:/, "", port) }
        print port "|" fam "|" arg "|" file
      }
    }')"
  slot() { printf '%s\n' "$defaults" | awk -F'|' -v p="$1" -v f="$2" '$1 == p && $2 == f { print $4 " (listen " $3 ")" }' | head -n 1; }
  s80_4="$(slot 80 v4)"; s80_6="$(slot 80 v6)"; s443_4="$(slot 443 v4)"; s443_6="$(slot 443 v6)"
  found=0
  for pair in "80 IPv4:$s80_4" "80 IPv6:$s80_6" "443 IPv4:$s443_4" "443 IPv6:$s443_6"; do
    label="${pair%%:*}"; where="${pair#*:}"
    if [ -n "$where" ]; then info "default_server for port $label: $where"; found=$((found + 1)); else info "default_server for port $label: none"; fi
  done
  ours="$(printf '%s\n' "$defaults" | awk -F'|' '$4 ~ /spencer-denim-00-default/ { c++ } END { print c + 0 }')"
  if [ "$found" -eq 0 ]; then
    warnl "NO default_server on 80 or 443. Unknown hosts would be answered by whichever server block nginx reads first (possibly our shop, which answers 400). DECISION: install deploy/host-nginx/spencer-denim-00-default.conf.template (docs/16 §B step 9)"
  elif [ "$ours" -gt 0 ] && [ "$ours" -eq "$found" ]; then
    pass "the catch-all from deploy/host-nginx/spencer-denim-00-default.conf.template is already installed and is the only default_server"
  elif [ "$found" -eq 4 ]; then
    pass "default_server already exists on 80 and 443 (IPv4 and IPv6). DECISION: do NOT install spencer-denim-00-default.conf.template (a second default_server makes nginx -t fail). Unknown hosts will be answered by the server block listed above; confirm that is acceptable"
  else
    warnl "default_server exists for some of 80/443 x IPv4/IPv6 but not all (see INFO lines). DECISION: do NOT install the catch-all file as it is. Either add 'default_server' to the missing listens of the existing catch-all, or install a copy of the template that keeps only the missing listen lines"
  fi

  if [ -n "$listen_lines" ]; then
    if port_in_use 80 && port_in_use 443; then pass "something is listening on 80 and 443 (the host nginx, as the design assumes)"; else warnl "nothing is listening on 80 and/or 443; is nginx running (systemctl status nginx)?"; fi
  fi
  if printf '%s\n' "$nginx_dump" | grep -q 'sites-enabled'; then
    pass "nginx.conf includes sites-enabled (the templates are installed there)"
  else
    warnl "nginx.conf does not seem to include /etc/nginx/sites-enabled/*; the install commands in docs/16 assume it does. Use conf.d if that is this server's convention"
  fi
  existing_sd="$(printf '%s\n' "$nginx_dump" | grep -E '^# configuration file .*spencer-denim' | sed 's/^# configuration file //; s/:$//')"
  if [ -n "$existing_sd" ]; then info "already enabled here: $(printf '%s' "$existing_sd" | tr '\n' ' ')"; fi
fi

webroot="${SD_PREFLIGHT_CERTBOT_WEBROOT:-/var/www/certbot}"; le_dir="${SD_PREFLIGHT_LETSENCRYPT_DIR:-/etc/letsencrypt}"   # overridable for the test
if [ -d "$webroot" ]; then
  if [ -r "$webroot" ] && [ -x "$webroot" ]; then pass "$webroot exists (certbot --webroot target)"; else warnl "$webroot exists but nginx may not be able to read it (needs mode 755)"; fi
else
  warnl "$webroot does not exist (the templates' ACME location uses it). Create it: sudo install -d -m 755 $webroot"
fi
for f in "$le_dir/options-ssl-nginx.conf" "$le_dir/ssl-dhparams.pem"; do
  if [ -f "$f" ]; then
    pass "$f exists (the site templates include it)"
  else
    warnl "$f does not exist. The site templates include it, so 'nginx -t' fails until it does; 'certbot certonly --webroot' does not create it. docs/16 §B step 9 says how to provide it"
  fi
done

# ---------------------------------------------------------------------------------------------------------------
head1 "DNS"
# ---------------------------------------------------------------------------------------------------------------
if [ "${#hosts[@]}" -eq 0 ]; then
  for e in "${envs[@]}"; do
    f="$(app_root "$e")/config/app.env"
    if [ -f "$f" ]; then
      for h in $(env_value "$f" SD_PUBLIC_HOSTS | tr ',' ' '); do
        case "$h" in CHANGE_ME*) ;; *) hosts+=("$h") ;; esac
      done
    fi
  done
fi
if [ "$skip_dns" -eq 1 ]; then
  info "DNS checks skipped (--skip-dns)"
elif [ "${#hosts[@]}" -eq 0 ]; then
  warnl "no host name to check: pass them (preflight.sh staging staging.<domain> ...) or set SD_PREFLIGHT_HOSTS. The DNS records are the thing certbot needs first"
else
  # This server's public IPv4: given, else the source address of the default route if it is public, else ONE outside lookup.
  pub="$public_ip"; pub_src="given"
  if [ -z "$pub" ] && have ip; then
    cand="$(ip -4 -o route get 1.1.1.1 2>/dev/null | sed -n 's/.* src \([0-9.]*\).*/\1/p' | head -n 1)"
    if is_ipv4 "$cand" && ! is_private_v4 "$cand"; then pub="$cand"; pub_src="the address of the default route"; fi
  fi
  if [ -z "$pub" ] && [ "${SD_PREFLIGHT_NO_EXTERNAL:-0}" != 1 ] && have curl; then
    cand="$(curl -4 -fsS --max-time 6 https://api.ipify.org 2>/dev/null | tr -d '[:space:]')"
    if is_ipv4 "$cand"; then pub="$cand"; pub_src="api.ipify.org (the only outside request this script makes)"; fi
  fi
  local_v4=""; local_v6=""
  if have ip; then
    local_v4="$(ip -4 -o addr show scope global 2>/dev/null | awk '{ sub(/\/.*/, "", $4); print $4 }')"
    local_v6="$(ip -6 -o addr show scope global 2>/dev/null | awk '{ sub(/\/.*/, "", $4); print $4 }')"
  fi
  if [ -n "$pub" ]; then info "this server's public IPv4: $pub (from $pub_src)"; else warnl "could not work out this server's public IPv4 (no public address on the default route, and no outside lookup). Pass it: preflight.sh --public-ip <ip> ..."; fi

  resolver="${SD_PREFLIGHT_RESOLVER:-1.1.1.1}"
  # dns_lookup <host> <A|AAAA>: first line "via <method>", then the addresses one per line; nothing at all if none.
  dns_lookup() {
    local h="$1" t="$2" out="" keep
    if [ "$t" = A ]; then keep='^[0-9]+[.][0-9]+[.][0-9]+[.][0-9]+$'; else keep=':'; fi
    if have dig; then
      out="$(dig +short +time=3 +tries=1 "$t" "$h" "@$resolver" 2>/dev/null | grep -E "$keep")"
      if [ -n "$out" ]; then printf 'via dig @%s\n%s\n' "$resolver" "$out"; return; fi
      out="$(dig +short +time=3 +tries=1 "$t" "$h" 2>/dev/null | grep -E "$keep")"
      if [ -n "$out" ]; then printf 'via dig (system resolver)\n%s\n' "$out"; return; fi
    elif have host; then
      out="$(host -t "$t" "$h" 2>/dev/null | awk '/has (IPv6 )?address/ { print $NF }' | grep -E "$keep")"
      if [ -n "$out" ]; then printf 'via host\n%s\n' "$out"; return; fi
    elif have getent; then
      if [ "$t" = A ]; then out="$(getent ahostsv4 "$h" 2>/dev/null | awk '{ print $1 }' | sort -u)"; else out="$(getent ahostsv6 "$h" 2>/dev/null | awk '{ print $1 }' | sort -u)"; fi
      if [ -n "$out" ]; then printf 'via getent (system resolver, /etc/hosts included)\n%s\n' "$out"; return; fi
    fi
  }
  seen=" "
  for h in "${hosts[@]}"; do
    [ -n "$h" ] || continue
    case "$seen" in *" $h "*) continue ;; esac; seen="$seen$h "
    if ! have dig && ! have host && ! have getent; then warnl "$h: no dig, host or getent here, so DNS cannot be checked (apt install dnsutils)"; continue; fi
    a_raw="$(dns_lookup "$h" A)"
    via="$(printf '%s\n' "$a_raw" | sed -n '1s/^via //p')"
    a_list="$(printf '%s\n' "$a_raw" | sed 1d | tr '\n' ' ' | sed 's/ $//')"
    if [ -z "$a_list" ]; then
      fail "DNS: $h has no A record. certbot cannot issue, and nobody can reach the site, until it points at this server${pub:+ ($pub)}"
      continue
    fi
    ok=0
    for ip in $a_list; do
      if { [ -n "$pub" ] && [ "$ip" = "$pub" ]; } || printf '%s\n' "$local_v4" | grep -qx "$ip"; then ok=1; fi
    done
    if [ -z "$pub" ] && [ "$ok" -eq 0 ]; then
      warnl "DNS: $h -> $a_list (via $via), but this server's public address is unknown, so it cannot be compared"
    elif [ "$ok" -eq 1 ]; then
      pass "DNS: $h -> $a_list (via $via) points at this server${pub:+ ($pub)}"
    else
      fail "DNS: $h -> $a_list (via $via) but this server is ${pub}. Fix the A record at the DNS provider (and wait for the TTL), then re-run"
    fi
    aaaa_list="$(dns_lookup "$h" AAAA | sed 1d | tr '\n' ' ' | sed 's/ $//')"
    if [ -n "$aaaa_list" ]; then
      ok6=0
      for ip in $aaaa_list; do printf '%s\n' "$local_v6" | grep -qix "$ip" && ok6=1; done
      if [ "$ok6" -eq 1 ]; then
        pass "DNS: $h has an AAAA record ($aaaa_list) that matches an IPv6 address of this server"
      else
        warnl "DNS: $h has an AAAA record ($aaaa_list) that is not an IPv6 address of this server: visitors and Let's Encrypt using IPv6 would reach the wrong place. Remove or correct it"
      fi
    fi
    if [ -d "$le_dir/live/$h" ]; then info "$h: a certificate directory already exists under $le_dir/live/ (certbot has issued for it before)"; fi
    if [ -n "$nginx_dump" ]; then
      dup="$(printf '%s\n' "$nginx_dump" | awk -v h="$h" '
        /^# configuration file / { file = $4; sub(/:$/, "", file); next }
        { line = $0; sub(/#.*/, "", line) }
        line ~ /server_name/ { n = split(line, t, /[ \t;]+/); for (i = 1; i <= n; i++) if (t[i] == h) { print file; exit } }')"
      if [ -n "$dup" ]; then
        case "$dup" in
          */spencer-denim-staging|*/spencer-denim-staging.conf|*/spencer-denim-production|*/spencer-denim-production.conf)
            info "$h: already named in $dup (this project's own staging/production site file)" ;;
          *spencer-denim*) warnl "$h is already a server_name in $dup: a site file of THIS project but not the staging/production one (an earlier stack, e.g. spencer-denim-development, that is serving this name today). Enabling the staging/production site for the same name gives 'conflicting server name', nginx ignores the later block and the earlier stack keeps the traffic. Plan the cut-over (docs/16 §D) before enabling it" ;;
          *) warnl "$h is already a server_name in $dup (not ours): two server blocks for one name; nginx warns and the first wins. Decide who owns it" ;;
        esac
      fi
    fi
  done
fi

# ---------------------------------------------------------------------------------------------------------------
printf '\n== Summary\n'
printf '%s PASS, %s WARN, %s FAIL\n' "$n_pass" "$n_warn" "$n_fail"
if [ "$n_fail" -gt 0 ]; then
  printf 'RESULT: FAIL: fix the FAIL lines above before install.sh. WARN lines need a decision, not necessarily a fix (docs/16 §B).\n'
  exit 1
fi
printf 'RESULT: no FAIL. Read every WARN line: each names the decision it needs.\n'
exit 0
