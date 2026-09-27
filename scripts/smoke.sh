#!/usr/bin/env bash
# Smoke test a Tickwire deployment end to end.
#   scripts/smoke.sh http://localhost:8080 localhost:9878
#   scripts/smoke.sh https://tickwire-api.fly.dev tickwire-api.fly.dev:9878
set -euo pipefail

API="${1:?usage: smoke.sh <httpBase> <fixHost:port>}"
FIX="${2:?usage: smoke.sh <httpBase> <fixHost:port>}"
PY="${PYTHON:-python3}"
here="$(cd "$(dirname "$0")" && pwd)"

step() { printf '\n== %s\n' "$*"; }
fail() { printf 'FAIL: %s\n' "$*" >&2; exit 1; }

step "health"
[ "$(curl -fsS --max-time 10 "$API/health")" = "Healthy" ] || fail "/health is not Healthy"

step "instruments"
curl -fsS --max-time 10 "$API/api/instruments" | grep -q '"symbol":"SPY"' || fail "SPY missing from /api/instruments"

step "guest provisioning"
guest="$(curl -fsS --max-time 10 -X POST "$API/api/guest")"
echo "$guest" | grep -q '"token"' || fail "no token from POST /api/guest"

step "FIX over TCP ($FIX)"
if ! "$PY" -c 'import simplefix' 2>/dev/null; then
  venv="$here/../clients/python/.venv"
  [ -x "$venv/bin/python" ] || python3 -m venv "$venv"
  "$venv/bin/python" -m pip install --quiet -r "$here/../clients/python/requirements.txt"
  PY="$venv/bin/python"
fi
"$PY" "$here/../clients/python/smoke_fix.py" --api "$API" --fix "$FIX" --quiet || fail "FIX round trip"

step "analyzer"
curl -fsS --max-time 20 -X POST "$API/api/analyzer" -H 'content-type: application/json' \
  --data "{\"text\":\"8=FIX.4.4|9=45|35=0|49=A|56=B|34=1|52=20250314-13:30:00.000|10=064|\"}" | grep -q '"messageCount":1' \
  || fail "analyzer"

printf '\nAll smoke checks passed for %s\n' "$API"
