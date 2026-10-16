#!/usr/bin/env bash
# End-to-end acceptance checks for a running BookMarker test container.
#
# Usage: scripts/acceptance.sh [container-name] [base-url]
# Defaults: bookmarker-test / http://localhost:8888
#
# Requires: bash, curl, docker, and tests/assets/sample-3page.pdf in the repo.
# Start the container first with ./build-and-run.sh.

set -uo pipefail

NAME="${1:-bookmarker-test}"
URL="${2:-http://localhost:8888}"
PASS=0
FAIL=0
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SAMPLE="$ROOT/tests/assets/sample-3page.pdf"
WORK=$(mktemp -d /tmp/bookmarker-accept.XXXXXX)

say() { printf '%s\n' "$*"; }
ok()  { PASS=$((PASS + 1)); say "  PASS: $*"; }
bad() { FAIL=$((FAIL + 1)); say "  FAIL: $*"; }

say "== BookMarker acceptance =="
say "container=$NAME url=$URL work=$WORK"

# ---------- preflight ----------
if ! docker inspect "$NAME" >/dev/null 2>&1; then
  say "container $NAME not found - run ./build-and-run.sh first"
  exit 2
fi
RP=$(docker inspect -f '{{.HostConfig.RestartPolicy.Name}}' "$NAME")
if [ "$RP" = "unless-stopped" ]; then ok "restart policy = unless-stopped"; else bad "restart policy = '$RP' (expected unless-stopped)"; fi
if [ -f "$SAMPLE" ]; then ok "sample pdf present"; else bad "sample pdf missing: $SAMPLE"; exit 2; fi

# ---------- E1: health + kill -9 recovery ----------
say "-- E1: health + restart recovery"
CODE=$(curl -sS -o "$WORK/health.json" -w '%{http_code}' "$URL/health" || echo 000)
if [ "$CODE" = "200" ] && grep -q '"status":"healthy"' "$WORK/health.json"; then
  ok "health is 200/healthy"
else
  bad "health returned $CODE: $(head -c 200 "$WORK/health.json" 2>/dev/null)"
fi

# Simulate an unexpected crash: SIGKILL PID 1 from *inside* the container, which the
# restart policy recovers. (docker kill/stop are daemon-level explicit stops and do
# NOT trigger the policy - that is by design.)
docker exec "$NAME" bash -c 'kill -9 1' >/dev/null 2>&1
RECOVERED=""
for i in $(seq 1 24); do
  sleep 5
  code=$(curl -s -o /dev/null -w '%{http_code}' "$URL/health" || true)
  if [ "$code" = "200" ]; then RECOVERED=$i; break; fi
done
if [ -n "$RECOVERED" ]; then ok "recovered after kill -9 PID 1 (poll $RECOVERED)"; else bad "did not recover within 120s"; fi

# ---------- E2: convert sample (nollm) + CLI comparison ----------
say "-- E2: convert sample (llm=nollm) vs direct CLI run"
CODE=$(curl -sS -o "$WORK/out.md" -w '%{http_code}' -D "$WORK/headers.txt" \
  -X POST "$URL/convert" -F "llm=nollm" -F "file=@$SAMPLE")
PAGES=$(awk 'tolower($1)=="x-pages:"{print $2}' "$WORK/headers.txt" | tr -d '\r')
SPP=$(awk 'tolower($1)=="x-sec-per-page:"{print $2}' "$WORK/headers.txt" | tr -d '\r')
if [ "$CODE" = "200" ] && [ -s "$WORK/out.md" ]; then
  ok "convert 200 ($(wc -c <"$WORK/out.md") bytes, pages=${PAGES:-?}, s/page=${SPP:-?})"
else
  bad "convert returned $CODE: $(head -c 300 "$WORK/out.md" 2>/dev/null)"
fi

docker exec "$NAME" bash -c 'rm -rf /tmp/cli-cmp && mkdir -p /tmp/cli-cmp/in /tmp/cli-cmp/out' >/dev/null 2>&1
docker cp "$SAMPLE" "$NAME:/tmp/cli-cmp/in/sample-3page.pdf" >/dev/null 2>&1
docker exec "$NAME" bash -c 'marker /tmp/cli-cmp/in --output_dir /tmp/cli-cmp/out --workers 1 --disable_multiprocessing --disable_image_extraction > /tmp/cli-cmp/run.log 2>&1; echo CLI_EXIT=$?'
CLIMD=$(docker exec "$NAME" bash -c 'find /tmp/cli-cmp/out -name "*.md" | head -1')
if [ -n "$CLIMD" ]; then
  docker cp "$NAME:$CLIMD" "$WORK/cli.md" >/dev/null 2>&1
fi
if [ -s "$WORK/cli.md" ] && [ -s "$WORK/out.md" ]; then
  NDIFF=$(diff "$WORK/cli.md" "$WORK/out.md" | grep -c '^[<>]' || true)
  say "   service md vs CLI md: $NDIFF differing lines"
  if [ "$NDIFF" -le 20 ]; then ok "service md matches CLI md"; else bad "service md diverges from CLI md ($NDIFF lines)"; fi
else
  bad "missing markdown output for diff (cli='$CLIMD')"
fi

# page_range cursor
CODE=$(curl -sS -o "$WORK/out-0-1.md" -w '%{http_code}' -D "$WORK/h2.txt" \
  -X POST "$URL/convert" -F "llm=nollm" -F "page_range=0-1" -F "file=@$SAMPLE")
P2=$(awk 'tolower($1)=="x-pages:"{print $2}' "$WORK/h2.txt" | tr -d '\r')
if [ "$CODE" = "200" ] && [ "$P2" = "2" ] && [ -s "$WORK/out-0-1.md" ]; then
  ok "page_range=0-1 cursor -> 200, pages=2"
else
  bad "page_range run: code=$CODE pages=${P2:-?}"
fi

# ---------- E3: 503 refusal while busy ----------
say "-- E3: 503 refusal while busy"
curl -sS -o "$WORK/bg.md" -X POST "$URL/convert" -F "llm=nollm" -F "file=@$SAMPLE" &
BGPID=$!
BUSY=0
for i in $(seq 1 30); do
  sleep 1
  st=$(curl -s "$URL/api/status" | grep -o '"state":"[a-z]*"' | head -1)
  if [ "$st" = '"state":"busy"' ]; then BUSY=1; break; fi
done
if [ "$BUSY" = 1 ]; then
  CODE=$(curl -sS -o "$WORK/busy.json" -w '%{http_code}' -D "$WORK/busy.headers" \
    -X POST "$URL/convert" -F "llm=nollm" -F "file=@$SAMPLE")
  RA=$(awk 'tolower($1)=="retry-after:"{print $2}' "$WORK/busy.headers" | tr -d '\r')
  if [ "$CODE" = "503" ] && [ -n "$RA" ]; then
    ok "busy -> 503 with Retry-After: $RA"
  else
    bad "busy test: code=$CODE retry-after='$RA'"
  fi
  H=$(curl -s -o /dev/null -w '%{http_code}' "$URL/health")
  if [ "$H" = "200" ]; then ok "health stays 200 while busy"; else bad "health during busy: $H"; fi
else
  bad "never observed busy state (conversion too fast?)"
fi
wait "$BGPID" || true
if [ -s "$WORK/bg.md" ]; then ok "background conversion completed after refusal window"; else bad "background conversion produced nothing"; fi

# ---------- E4: failure paths keep the service up ----------
say "-- E4: failure paths"
printf 'this is not a pdf at all\n' > "$WORK/notpdf.bin"
CODE=$(curl -sS -o "$WORK/notpdf.json" -w '%{http_code}' -X POST "$URL/convert" -F "file=@$WORK/notpdf.bin")
if [ "$CODE" = "415" ]; then ok "non-PDF -> 415"; else bad "non-PDF -> $CODE"; fi

printf '%%PDF-1.4\nthis is not a real pdf body\n%%%%EOF\n' > "$WORK/broken.pdf"
CODE=$(curl -sS -o "$WORK/broken.json" -w '%{http_code}' -X POST "$URL/convert" -F "file=@$WORK/broken.pdf")
if [ "$CODE" = "500" ] && grep -q '"logTail"' "$WORK/broken.json"; then
  ok "broken PDF -> 500 with logTail"
else
  bad "broken PDF -> $CODE ($(head -c 200 "$WORK/broken.json" 2>/dev/null))"
fi

H=$(curl -s -o /dev/null -w '%{http_code}' "$URL/health")
if [ "$H" = "200" ]; then ok "health still 200 after failures"; else bad "health after failure: $H"; fi

CODE=$(curl -sS -o "$WORK/after.md" -w '%{http_code}' -X POST "$URL/convert" -F "file=@$SAMPLE")
if [ "$CODE" = "200" ] && [ -s "$WORK/after.md" ]; then ok "next request serves normally"; else bad "post-failure convert -> $CODE"; fi

# ---------- E5: no caller data retained ----------
say "-- E5: no caller data retained"
LEFT=$(docker exec "$NAME" bash -c 'find /tmp/bookmarker -mindepth 1 2>/dev/null | head -5')
if [ -z "$LEFT" ]; then ok "scratch is empty after all jobs"; else bad "scratch leftovers: $LEFT"; fi
docker exec "$NAME" bash -c 'rm -rf /tmp/cli-cmp' >/dev/null 2>&1

# ---------- E6: measured throughput ----------
say "-- E6: measured throughput"
if [ -n "${SPP:-}" ]; then
  ok "s/page recorded on the service: $SPP (pages=${PAGES:-?})"
else
  bad "no X-Sec-Per-Page header on the 200 response"
fi

# ---------- summary ----------
say "== summary: PASS=$PASS FAIL=$FAIL =="
[ "$FAIL" = 0 ]
