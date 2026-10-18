#!/usr/bin/env bash
# Proves, against REAL nginx, what the gateway and the host nginx write to their logs and that the page-view beacon
# is rate limited (issue #179, docs/15 section 8a). Needs Docker and python3; touches nothing outside its own
# containers, network and images, all named "<prefix>-...".
#
#   tools/verify-gateway-logging.sh                    # the configuration in this checkout
#   tools/verify-gateway-logging.sh --deploy-dir DIR   # DIR holds gateway/, host-nginx/ and docker/ (see below)
#   tools/verify-gateway-logging.sh --keep             # leave the containers running to look around
#
# To see the checks FAIL (they must be able to), run it against the configuration before the fix:
#
#   mkdir /tmp/sd-old && git archive 154b5e4 deploy | tar -x -C /tmp/sd-old
#   tools/verify-gateway-logging.sh --deploy-dir /tmp/sd-old/deploy
#
# What it builds, on a private network (default 10.251.179.0/24, prefix sd179: docs/14 "Scratch scripts and ports
# are shared", so override both if another session holds them: SD_VERIFY_PREFIX, SD_VERIFY_NET):
#
#   client  (.50)  the "visitor": curl, with an address that must never reach a log
#   host    (.20)  nginx 1.24, the production and staging site files from deploy/host-nginx with their two
#                  placeholders and the upstream address filled in, in front of the gateway as on the VPS
#   gw      (.10)  the gateway: the web stage of deploy/docker/frontend.Dockerfile (same base image, ENV, COPYs;
#                  only the Angular admin build is replaced by a one-line page), run read-only with a tmpfs /tmp,
#                  all capabilities dropped and 128 MB, as compose.yml runs it
#   stub    (.5)   answers as both "api" and "storefront"; counts what reaches it
#
# Exit status 0 only if every check passed.

set -u
export MSYS_NO_PATHCONV=1   # Git Bash on Windows must not rewrite /var/log/... arguments into C:/Program Files/...

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
deploy="$(cd "$here/../deploy" && pwd)"
keep=0
while [ $# -gt 0 ]; do
  case "$1" in
    --deploy-dir) deploy="$(cd "$2" && pwd)"; shift 2 ;;
    --keep) keep=1; shift ;;
    *) echo "usage: $0 [--deploy-dir DIR] [--keep]" >&2; exit 2 ;;
  esac
done

P="${SD_VERIFY_PREFIX:-sd179}"
N="${SD_VERIFY_NET:-10.251.179}"
CLIENT_IP="$N.50"; HOST_IP="$N.20"; GW_IP="$N.10"; STUB_IP="$N.5"
SPOOFED_IP="198.51.100.77"
PROD_HOST="shop.$P.test"; STAGE_HOST="staging.$P.test"
NGINX_UNPRIV_IMAGE="${SD_VERIFY_NGINX_IMAGE:-nginxinc/nginx-unprivileged:1.30.5-alpine3.24}"

W="$(mktemp -d)"
pass=0; failed=0
ok()   { pass=$((pass + 1)); printf '  PASS  %s\n' "$*"; }
bad()  { failed=$((failed + 1)); printf '  FAIL  %s\n' "$*"; }
say()  { printf '\n== %s  [%ss]\n' "$*" "$SECONDS"; }
win()  { cygpath -m "$1" 2>/dev/null || printf '%s' "$1"; }

cleanup() {
  if [ "$keep" = 1 ]; then echo "(kept: containers $P-*, network $P-net, work dir $W)"; return; fi
  docker rm -f "$P-client" "$P-host" "$P-gw" "$P-stub" >/dev/null 2>&1
  docker network rm "$P-net" >/dev/null 2>&1
  docker image rm -f "$P-gw-img" "$P-host-img" "$P-stub-img" "$P-tools-img" >/dev/null 2>&1
  rm -rf "$W"
}
trap cleanup EXIT

if [ -n "$(docker ps -a -q --filter "name=^${P}-")" ] || docker network inspect "$P-net" >/dev/null 2>&1; then
  echo "containers or a network named $P-* already exist; remove them or set SD_VERIFY_PREFIX/SD_VERIFY_NET" >&2
  trap - EXIT; exit 2
fi

# ---------------------------------------------------------------------------------------------------------------
say "Building (deploy dir: $deploy)"
# ---------------------------------------------------------------------------------------------------------------
mkdir -p "$W/tools" "$W/stub" "$W/gw/admin" "$W/host/conf.d" "$W/host/letsencrypt/live/$PROD_HOST" \
         "$W/host/letsencrypt/live/$STAGE_HOST"

printf 'FROM alpine:3.24\nRUN apk add --no-cache curl openssl\n' > "$W/tools/Dockerfile"
docker build -q -t "$P-tools-img" "$(win "$W/tools")" >/dev/null || { echo "cannot build the tools image" >&2; exit 2; }

# --- stub upstream ---------------------------------------------------------------------------------------------
cat > "$W/stub/nginx.conf" <<'EOF'
worker_processes 1;
pid /tmp/nginx.pid;
error_log /tmp/stub-error.log;
events { worker_connections 1024; }
http {
    log_format stub '$request_method $request_uri $status';
    access_log /tmp/stub-access.log stub;
    server {
        listen 8080;
        listen 4000;
        location = /api/traffic/page-views {
            if ($http_x_stub_fail) { return 503; }
            return 204;
        }
        location / {
            default_type application/json;
            return 200 '{"stub":true}';
        }
    }
}
EOF
printf 'FROM %s\nCOPY nginx.conf /etc/nginx/nginx.conf\n' "$NGINX_UNPRIV_IMAGE" > "$W/stub/Dockerfile"
docker build -q -t "$P-stub-img" "$(win "$W/stub")" >/dev/null || exit 2

# --- gateway: the web stage of the real Dockerfile -------------------------------------------------------------
mkdir -p "$W/gw/deploy"
cp -r "$deploy/gateway" "$W/gw/deploy/gateway"
echo "<html><body>admin stand-in</body></html>" > "$W/gw/admin/index.html"
real="$deploy/docker/frontend.Dockerfile"
{
  grep -E '^ARG NGINX_IMAGE=' "$real"
  sed -n '/^FROM \${NGINX_IMAGE} AS web/,/^EXPOSE 8080/p' "$real" \
    | sed 's#^COPY --from=build .*#COPY admin/ /usr/share/nginx/admin/#'
} > "$W/gw/Dockerfile"
docker build -q -t "$P-gw-img" "$(win "$W/gw")" >/dev/null || { echo "cannot build the gateway image" >&2; exit 2; }

# --- host nginx: the site files with their placeholders filled in -----------------------------------------------
docker run --rm "$P-tools-img" sh -c "
  set -e; mkdir /out
  openssl req -x509 -newkey rsa:2048 -nodes -days 2 -keyout /out/privkey.pem -out /out/fullchain.pem \
    -subj /CN=$PROD_HOST -addext subjectAltName=DNS:$PROD_HOST,DNS:$STAGE_HOST >/dev/null 2>&1
  openssl dhparam -dsaparam -out /out/ssl-dhparams.pem 2048 >/dev/null 2>&1
  tar -c -C /out privkey.pem fullchain.pem ssl-dhparams.pem" > "$W/tls.tar" || { echo "cannot generate the test certificate" >&2; exit 2; }
for h in "$PROD_HOST" "$STAGE_HOST"; do tar -x -C "$W/host/letsencrypt/live/$h" -f "$W/tls.tar" privkey.pem fullchain.pem; done
tar -x -C "$W/host/letsencrypt" -f "$W/tls.tar" ssl-dhparams.pem
cat > "$W/host/letsencrypt/options-ssl-nginx.conf" <<'EOF'
ssl_session_cache shared:le_nginx_SSL:10m;
ssl_session_timeout 1440m;
ssl_protocols TLSv1.2 TLSv1.3;
EOF
sed -e "s/__SERVER_NAME__/$PROD_HOST/g" -e "s/__PRIMARY_HOST__/$PROD_HOST/g" \
    -e "s/127\\.0\\.0\\.1:38080/$P-gw:8080/" "$deploy/host-nginx/spencer-denim-production.conf.template" \
    > "$W/host/conf.d/production.conf"
sed -e "s/__SERVER_NAME__/$STAGE_HOST/g" -e "s/__PRIMARY_HOST__/$STAGE_HOST/g" \
    -e "s/127\\.0\\.0\\.1:38081/$P-gw:8080/" "$deploy/host-nginx/spencer-denim-staging.conf.template" \
    > "$W/host/conf.d/staging.conf"
if grep -n '__[A-Z_]*__' "$W"/host/conf.d/*.conf >/dev/null; then echo "a placeholder was left in a site file" >&2; exit 2; fi
# Like Ubuntu's nginx.conf: one shared access log in the default combined format, and an error log at the default level.
cat > "$W/host/nginx.conf" <<'EOF'
worker_processes 1;
pid /run/nginx.pid;
error_log /var/log/nginx/error.log;
events { worker_connections 256; }
http {
    include /etc/nginx/mime.types;
    access_log /var/log/nginx/access.log;
    include /etc/nginx/conf.d/*.conf;
}
EOF
cat > "$W/host/Dockerfile" <<'EOF'
FROM nginx:1.24-alpine
RUN rm -f /etc/nginx/conf.d/default.conf /var/log/nginx/*.log \
 && mkdir -p /var/log/nginx/spencer-denim /var/www/certbot \
 && touch /var/log/nginx/access.log /var/log/nginx/error.log
COPY nginx.conf /etc/nginx/nginx.conf
COPY letsencrypt/ /etc/letsencrypt/
COPY conf.d/ /etc/nginx/conf.d/
EOF
docker build -q -t "$P-host-img" "$(win "$W/host")" >/dev/null || { echo "cannot build the host image" >&2; exit 2; }

# ---------------------------------------------------------------------------------------------------------------
say "Starting"
# ---------------------------------------------------------------------------------------------------------------
docker network create --subnet "$N.0/24" "$P-net" >/dev/null || { echo "cannot create network $P-net on $N.0/24" >&2; exit 2; }
docker run -d --name "$P-stub" --network "$P-net" --ip "$STUB_IP" --network-alias api --network-alias storefront \
  "$P-stub-img" >/dev/null
docker run -d --name "$P-gw" --network "$P-net" --ip "$GW_IP" --read-only --tmpfs /tmp --cap-drop ALL \
  --security-opt no-new-privileges --memory 128m -e "SD_REAL_IP_FROM=$HOST_IP" "$P-gw-img" >/dev/null
for _ in $(seq 1 30); do
  docker exec "$P-gw" wget -q -O /dev/null http://127.0.0.1:8080/gateway-health 2>/dev/null && break; sleep 1
done
docker exec "$P-gw" wget -q -O /dev/null http://127.0.0.1:8080/gateway-health 2>/dev/null \
  || { echo "the gateway did not start:" >&2; docker logs "$P-gw" 2>&1 | tail -20 >&2; exit 2; }
docker run -d --name "$P-host" --network "$P-net" --ip "$HOST_IP" "$P-host-img" >/dev/null
sleep 2
docker run -d --name "$P-client" --network "$P-net" --ip "$CLIENT_IP" \
  --add-host "$PROD_HOST:$HOST_IP" --add-host "$STAGE_HOST:$HOST_IP" "$P-tools-img" sleep 3600 >/dev/null

# every request below comes from the client container, through the host tier, over TLS
cx() { docker exec "$P-client" curl -sk --path-as-is "$@"; }
# A fresh random GUID that cannot contain the probe strings by accident.
secret_guid()   { python3 -c 'import uuid
while True:
    g = str(uuid.uuid4())
    if "abc" not in g and "xyz" not in g: print(g); break'; }
TOKEN="$(secret_guid)"; TOKEN2="$(secret_guid)"
LOWER_TOKEN="k3j9x0a8s7d6f5g4h3j2k1l0"

# ---------------------------------------------------------------------------------------------------------------
say "Configuration is valid (nginx -t)"
# ---------------------------------------------------------------------------------------------------------------
if out="$(docker exec "$P-gw" nginx -t 2>&1)"; then ok "gateway: nginx -t"; else bad "gateway: nginx -t"; echo "$out" | sed 's/^/        /'; fi
if out="$(docker exec "$P-host" nginx -t 2>&1)"; then ok "host nginx, production and staging site files: nginx -t"; else bad "host nginx: nginx -t"; echo "$out" | sed 's/^/        /'; fi

# ---------------------------------------------------------------------------------------------------------------
say "Requests that carry credentials, an address and a Referer"
# ---------------------------------------------------------------------------------------------------------------
REF_SECRET="https://$PROD_HOST/order/$TOKEN2?token=refsecret"
cx -o /dev/null -A "$P-ua-order"   -H "Referer: $REF_SECRET" -H "X-Forwarded-For: $SPOOFED_IP" "https://$PROD_HOST/order/$TOKEN"
cx -o /dev/null -A "$P-ua-reset"   -H "Referer: $REF_SECRET" "https://$PROD_HOST/account/reset-password?token=abc"
cx -o /dev/null -A "$P-ua-confirm" -H "Referer: $REF_SECRET" "https://$PROD_HOST/account/confirm-email?token=xyz"
cx -o /dev/null -A "$P-ua-page"    -H "Referer: $REF_SECRET" "https://$PROD_HOST/women/jeans?q=abc"
cx -o /dev/null -A "$P-ua-paypal"  "https://$PROD_HOST/api/payments/paypal/return/$TOKEN?token=abc&PayerID=xyz"
cx -o /dev/null -A "$P-ua-orderapi" "https://$PROD_HOST/api/orders/$TOKEN"
cx -o /dev/null -A "$P-ua-staging" -H "Referer: $REF_SECRET" "https://$STAGE_HOST/order/$TOKEN?token=abc"
cx -o /dev/null -A "$P-ua-http"    -H "Referer: $REF_SECRET" "http://$PROD_HOST/order/$TOKEN?token=abc"   # port 80: the redirect

# ---------------------------------------------------------------------------------------------------------------
say "The path written to the gateway log, case by case"
# ---------------------------------------------------------------------------------------------------------------
G="$TOKEN"
A15="$(python3 -c "print('a' * 15)")"; A16="$(python3 -c "print('a' * 16)")"
S100="$(python3 -c "print('ab-' * 33 + 'a')")"; S101="$(python3 -c "print('ab-' * 33 + 'ab')")"
cat > "$W/cases.txt" <<EOF
/order/$G|/order/*
/order/$LOWER_TOKEN|/order/*
/Order/$LOWER_TOKEN|/order/*
/order;m=1/$LOWER_TOKEN|/order/*
/%6frder/$LOWER_TOKEN|/order/*
//order//$LOWER_TOKEN/|/order/*
/foo/../order/$LOWER_TOKEN|/order/*
/order/$LOWER_TOKEN?token=abc|/order/*
/api/orders/$G|/api/orders/*
/api/orders/$G/payment-slip|/api/orders/*
/api/ORDERS/$G/cancel|/api/orders/*
/api/payments/paypal/return/$G?token=abc&PayerID=xyz|/api/payments/paypal/return/*
/api/payments/paypal/start/$LOWER_TOKEN|/api/payments/paypal/start/*
/api/payments/paypal/webhook|/api/payments/paypal/webhook
/api/admin/orders/$G/invoice|/api/admin/orders/*
/api/admin/orders/bank-transfers/$G/approve|/api/admin/orders/bank-transfers/*
/api/admin/orders/bank-transfers|/api/admin/orders/bank-transfers
/api/admin/orders|/api/admin/orders
/api/admin/orders/$LOWER_TOKEN|/api/admin/orders/*
/|/
/women/jeans|/women/jeans
/women/jeans/|/women/jeans
/kids/girls/girls-dresses|/kids/girls/girls-dresses
/product/slim-fit-jeans?color=abc|/product/slim-fit-jeans
/account/reset-password?token=abc|/account/reset-password
/account/confirm-email?token=xyz|/account/confirm-email
/api/catalog/products?pageSize=2|/api/catalog/products
/api/admin/customers/42/orders|/api/admin/customers/42/orders
/api/admin/customers/1234567/orders|/api/admin/customers/*
/product/jane.doe@example.com|/product/*
/something/0771234567|/something/*
/something/077-123-4567|/something/*
/gift/$LOWER_TOKEN|/gift/*
/gift/3F2504E0-4F89-41D3-9A0C-0305E82C3301|/gift/*
/gift/3f2504e04f8941d39a0c0305e82c3301|/gift/*
/uppercase/Slug|/uppercase/*
/x/$A15|/x/$A15
/x/$A16|/x/*
/a/b/c/d/e/f/g|/a/b/c/d/e/f/g
/a/b/c/d/e/f/g/h|/a/b/c/d/e/f/g/*
/s/$S100|/s/$S100
/s/$S101|/s/*
/main-AB12CD34.js|/main-AB12CD34.js
/media/public/products/main-image.webp|/media/public/products/main-image.webp
/media/public/products/$G/main.webp|/media/public/products/*
/.well-known/security.txt|/*
/ab%0Acd/x|/*
/admin/orders/$G|/admin/index.html
/admin/|/admin/index.html
EOF
n=0
: > "$W/expected.tsv"
while IFS='|' read -r reqpath expected; do
  n=$((n + 1))
  printf '%s\t%s\t%s\n' "$P-case-$n" "$expected" "$reqpath" >> "$W/expected.tsv"
done < "$W/cases.txt"
# one exec for the whole table (a docker exec per request is slow on Windows); the user agent names the case
cat > "$W/run-cases.sh" <<'EOS'
#!/bin/sh
# usage: run-cases.sh <prefix> <host>; reads /cases.txt (path|expected), one request per line
n=0
while IFS='|' read -r reqpath expected; do
  n=$((n + 1))
  curl -sk --path-as-is -o /dev/null -A "$1-case-$n" "https://$2$reqpath"
done < /cases.txt
EOS
docker cp "$(win "$W/cases.txt")" "$P-client:/cases.txt"
docker cp "$(win "$W/run-cases.sh")" "$P-client:/run-cases.sh"
docker exec "$P-client" sh /run-cases.sh "$P" "$PROD_HOST"

# Referer handling, each with its own marker
cx -o /dev/null -A "$P-ref-1" -H "Referer: https://www.example.org/some/path?utm=abc" "https://$PROD_HOST/"
cx -o /dev/null -A "$P-ref-2" -H "Referer: http://$CLIENT_IP/x" "https://$PROD_HOST/"
cx -o /dev/null -A "$P-ref-3" -H "Referer: https://user:pw@evil.example.org/x" "https://$PROD_HOST/"
cx -o /dev/null -A "$P-ref-4" "https://$PROD_HOST/"

# ---------------------------------------------------------------------------------------------------------------
say "Page-view beacon rate limit"
# ---------------------------------------------------------------------------------------------------------------
stub_hits() { docker exec "$P-stub" grep -cF -- "$1" /tmp/stub-access.log 2>/dev/null || true; }
rate=20; burst=40
sleep 3
base_hits="$(stub_hits ' /api/traffic/page-views?')"
t0=$(date +%s)
cx -Z --parallel-max 100 -X POST -H 'Content-Type: application/json' -d '{}' -o /dev/null -w '%{http_code}\n' \
   "https://$PROD_HOST/api/traffic/page-views?[1-2000]" > "$W/flood-codes.txt"
t1=$(date +%s)
elapsed=$((t1 - t0 + 1))
codes="$(sort "$W/flood-codes.txt" | uniq -c | tr -s ' ' | tr '\n' ';')"
hits=$(( $(stub_hits ' /api/traffic/page-views?') - base_hits ))
allowed=$(( burst + 1 + rate * elapsed + 10 ))
echo "        2000 POSTs in ~${elapsed}s -> responses: $codes; reached the API: $hits (limit allows about $allowed)"
if [ "$(grep -vc '^204$' "$W/flood-codes.txt")" = 0 ] && [ "$(wc -l < "$W/flood-codes.txt")" = 2000 ]; then
  ok "every flooded request is answered 204 (the browser sees nothing wrong)"
else bad "some flooded requests were not answered 204: $codes"; fi
if [ "$hits" -le "$allowed" ]; then ok "the flood is throttled: $hits of 2000 reached the API"; else bad "the flood was not throttled: $hits of 2000 reached the API"; fi

for variant in '/api/traffic/page-views/' '/API/Traffic/Page-Views' '/api/traffic/page%2Dviews' '//api//traffic//page-views'; do
  before="$(stub_hits " $variant?")"
  cx -Z --parallel-max 100 -X POST -d '{}' -o /dev/null "https://$PROD_HOST${variant}?[1-300]" >/dev/null
  after="$(stub_hits " $variant?")"
  if [ $((after - before)) -le $((rate * 3 + 10)) ]; then ok "the limit also covers the spelling $variant ($((after - before)) of 300 got through)"
  else bad "the spelling $variant bypasses the limit ($((after - before)) of 300 got through)"; fi
done

sleep 4   # refill the bucket
code="$(cx -o /dev/null -w '%{http_code}' -X POST -H 'X-Stub-Fail: 1' -d '{}' "https://$PROD_HOST/api/traffic/page-views")"
if [ "$code" = 503 ]; then ok "a real 503 from the API is passed on, not turned into a 204"; else bad "an API 503 came back as $code"; fi
code="$(cx -o /dev/null -w '%{http_code}' -X POST -d '{}' "https://$PROD_HOST/api/traffic/page-views")"
if [ "$code" = 204 ]; then ok "an ordinary beacon is answered 204 by the API"; else bad "an ordinary beacon came back as $code"; fi

# ---------------------------------------------------------------------------------------------------------------
say "Dependencies fail: what do the error logs say?"
# ---------------------------------------------------------------------------------------------------------------
OUT_REF="https://$PROD_HOST/order/$TOKEN2?token=refsecret"
docker stop "$P-stub" >/dev/null
c1="$(cx -m 20 -o /dev/null -w '%{http_code}' -A "$P-ua-down1" -H "Referer: $OUT_REF" "https://$PROD_HOST/order/$TOKEN?token=abc")"
sleep 12   # past the gateway's 10 s DNS cache: the next failure is "could not be resolved"
c2="$(cx -m 20 -o /dev/null -w '%{http_code}' -A "$P-ua-down2" -H "Referer: $OUT_REF" "https://$PROD_HOST/account/reset-password?token=abc")"
docker stop "$P-gw" >/dev/null
c3="$(cx -m 30 -o /dev/null -w '%{http_code}' -A "$P-ua-down3" -H "Referer: $OUT_REF" "https://$PROD_HOST/order/$TOKEN?token=abc")"
echo "        with the stub down: $c1, $c2; with the gateway down too: $c3"
case "$c1$c2$c3" in
  *5[0-9][0-9]*5[0-9][0-9]*5[0-9][0-9]*) ok "the three outages really happened (5xx each), so the absences below are meaningful" ;;
  *) bad "an outage did not produce a 5xx ($c1 $c2 $c3), so the error-log checks prove nothing" ;;
esac

# ---------------------------------------------------------------------------------------------------------------
say "Reading the logs"
# ---------------------------------------------------------------------------------------------------------------
docker logs "$P-gw" > "$W/gw.log" 2>&1
docker exec "$P-host" sh -c 'for f in /var/log/nginx/*.log /var/log/nginx/spencer-denim/*.log; do [ -f "$f" ] && { echo "=== $f"; cat "$f"; }; done' > "$W/host.log" 2>&1
docker logs "$P-host" >> "$W/host.log" 2>&1
echo "        gateway: $(grep -c . "$W/gw.log") lines; host nginx files: $(grep -c . "$W/host.log") lines"
grep -q '"path"\|"uri"' "$W/gw.log" && ok "the gateway wrote access-log lines (so the absences below are not an empty log)" \
  || bad "the gateway log has no access-log lines at all"

needles="$TOKEN
$TOKEN2
$LOWER_TOKEN
abc
xyz
refsecret
$CLIENT_IP
$SPOOFED_IP"
for pair in "gateway log:$W/gw.log" "host nginx logs (access, error, shared access.log):$W/host.log"; do
  label="${pair%%:*}"; file="${pair#*:}"
  found=""
  while IFS= read -r needle; do
    [ -z "$needle" ] && continue
    if grep -qF -- "$needle" "$file"; then found="$found '$needle'"; fi
  done <<< "$needles"
  if [ -z "$found" ]; then ok "$label: none of token, abc, xyz, the referring order link, or the visitor's address appears"
  else bad "$label contains:$found"; grep -F -m3 -e "$TOKEN" -e "abc" -e "$CLIENT_IP" "$file" | cut -c1-230 | sed 's/^/        > /'; fi
done

grep -qE '"status":50[24]' "$W/gw.log" && ok "the gateway's access log recorded the failures (status 502/504)"   || bad "the gateway's access log shows no 502/504 although the stub was stopped"

sampled="$(grep -cE '"path":"/api/traffic/page-views","status":204,[^}]*"upstream":""' "$W/gw.log")"
if [ "$sampled" -ge 1 ] && [ "$sampled" -le 60 ]; then ok "throttled beacons are sampled into the access log, not logged one by one ($sampled lines for about 3,000 throttled requests)"
else bad "expected a few sampled throttle lines (status 204, empty upstream) in the gateway log, found $sampled"; fi

# --- gateway JSON: structure, path table, no address in any field -------------------------------------------------
cat > "$W/check.py" <<'EOF'
import json, re, sys
log, expected_tsv = sys.argv[1], sys.argv[2]
ipv4 = re.compile(r"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b")
ipv6 = re.compile(r"(?:[0-9a-fA-F]{1,4}:){2,}[0-9a-fA-F]{0,4}")
lines = []
for raw in open(log, encoding="utf-8", errors="replace"):
    raw = raw.strip()
    if raw.startswith("{"):
        try: lines.append(json.loads(raw))
        except ValueError: pass
by_ua = {}
for entry in lines: by_ua.setdefault(entry.get("user_agent", ""), []).append(entry)
bad = 0
def report(ok, text):
    global bad
    print(("  PASS  " if ok else "  FAIL  ") + text)
    if not ok: bad += 1

# 1. no field but `upstream` (a container address, never a visitor's) holds anything address-shaped, and the keys
keys_seen = set()
leaks = []
for entry in lines:
    keys_seen.update(entry.keys())
    for key, value in entry.items():
        if key in ("upstream", "time"): continue
        if isinstance(value, str) and (ipv4.search(value) or ipv6.search(value)): leaks.append((key, value))
report(not leaks, "no gateway log field except `upstream` holds anything shaped like an IP address" + ("" if not leaks else f" -- {leaks[:3]}"))
forbidden = {"remote_addr", "peer", "uri", "referer", "request_uri", "x_forwarded_for"} & keys_seen
report(not forbidden, "the gateway log has no remote_addr / peer / uri / referer field" + ("" if not forbidden else f" -- found {sorted(forbidden)}"))

# 2. path table
checked = failures = 0
for row in open(expected_tsv, encoding="utf-8"):
    ua, expected, requested = row.rstrip("\n").split("\t", 2)
    got = [e.get("path") for e in by_ua.get(ua, [])]
    checked += 1
    if got != [expected]:
        failures += 1
        print(f"        {requested[:90]!r}\n            expected {expected!r}, logged {got!r}")
report(failures == 0, f"the logged path is right for all {checked} table cases ({failures} wrong)")

# 3. referer host
want = {"1": "www.example.org", "2": "", "3": "", "4": ""}
for suffix, host in want.items():
    got = [e.get("referer_host") for e in by_ua.get(sys.argv[3] + "-ref-" + suffix, [])]
    report(got == [host], f"Referer case {suffix} is logged as host {host!r} (got {got!r})")
sys.exit(1 if bad else 0)
EOF
python3 "$(win "$W/check.py")" "$(win "$W/gw.log")" "$(win "$W/expected.tsv")" "$P" > "$W/check.out" 2>&1; chk=$?
cat "$W/check.out"
p2=$(grep -c '^  PASS' "$W/check.out"); f2=$(grep -c '^  FAIL' "$W/check.out")
pass=$((pass + p2)); failed=$((failed + f2))
if [ $((p2 + f2)) -lt 1 ] || { [ "$chk" != 0 ] && [ "$chk" != 1 ]; }; then
  bad "the gateway-log checks did not run to completion (python exit $chk, $p2 passes): see above"
fi

# --- the error logs: nginx quotes the whole request (client address, request line, Referer) on an error line ----
# These came from the outages above (stub down, gateway down); in the old configuration they are at error level.
for pair in "gateway error log (stderr):$W/gw.log" "host nginx error files:$W/host.log"; do
  label="${pair%%:*}"; file="${pair#*:}"
  if grep -qE 'request: "(GET|POST|PUT|HEAD)' "$file"; then
    bad "$label quotes requests (client, request line, upstream, referrer)"
    grep -E -m2 'request: "' "$file" | cut -c1-260 | sed 's/^/        > /'
  else ok "$label quotes no request"; fi
done

# --- what the host nginx wrote to its files --------------------------------------------------------------------------
if grep -qE 'GET /|POST /|HTTP/[12]' "$W/host.log"; then
  bad "the host nginx files hold request lines (an access log or an error line that quotes the request)"
  grep -E -m3 'GET /|POST /|HTTP/[12]' "$W/host.log" | cut -c1-230 | sed 's/^/        > /'
else ok "the host nginx files hold no request line at all"; fi

say "Result"
echo "  $pass passed, $failed failed"
[ "$failed" = 0 ]
