#!/usr/bin/env bash
# Smoke-test a built router binary against tools/ci/fake-upstream.py.
#
#   tools/ci/smoke.sh <path-to-binary>
#
# Runs on Linux, macOS and Git-Bash (Windows). Exits non-zero on the first failure.
# Every release binary goes through this script on its own operating system before a
# GitHub Release is created, so it must stay dependency-free (curl + python3 only).
set -uo pipefail

BIN="${1:?usage: smoke.sh <path-to-router-binary>}"
ROUTER_PORT="${SMOKE_ROUTER_PORT:-18321}"
FIXTURE_PORT="${SMOKE_FIXTURE_PORT:-18123}"
CONFIG="samples/router.ci-smoke.toml"
ROUTER="http://127.0.0.1:${ROUTER_PORT}"
UPSTREAM="http://127.0.0.1:${FIXTURE_PORT}"

PYBIN="$(command -v python3 || command -v python)"
[ -n "$PYBIN" ] || fail "python3 (or python) is required to run the fake upstream"

fail() { echo "SMOKE FAIL: $*" >&2; exit 1; }
step() { echo "--- smoke: $*"; }

# wait_for <description> <pid> <logfile> <command...> — retries until the command succeeds,
# but stops early if the process we depend on has already died.
wait_for() {
  local what="$1" pid="$2" log="$3"; shift 3
  for _ in $(seq 1 60); do
    if "$@" >/dev/null 2>&1; then return 0; fi
    kill -0 "$pid" 2>/dev/null || fail "$what died before it was ready (see $log): $(tail -5 "$log")"
    sleep 0.25
  done
  fail "$what never became ready (see $log)"
}

[ -x "$BIN" ] || [ -f "$BIN" ] || fail "binary not found: $BIN"

FIXTURE_PID=""
ROUTER_PID=""
cleanup() {
  [ -n "$ROUTER_PID" ] && kill "$ROUTER_PID" 2>/dev/null
  [ -n "$FIXTURE_PID" ] && kill "$FIXTURE_PID" 2>/dev/null
  return 0
}
trap cleanup EXIT

step "help output"
"$BIN" help >/dev/null || fail "help exited non-zero"

step "config validation (no sockets, no lifecycle)"
"$BIN" config --config "$CONFIG" >/dev/null || fail "config validation failed for $CONFIG"

step "check the smoke ports are free"
curl -fsS "${UPSTREAM}/health" >/dev/null 2>&1 && fail "port ${FIXTURE_PORT} is already in use"
curl -fsS "${ROUTER}/healthz" >/dev/null 2>&1 && fail "port ${ROUTER_PORT} is already in use"

step "start fake upstream on :${FIXTURE_PORT}"
FIXTURE_PORT="$FIXTURE_PORT" "$PYBIN" tools/ci/fake-upstream.py >/tmp/air-fixture.log 2>&1 &
FIXTURE_PID=$!
wait_for "fake upstream" "$FIXTURE_PID" /tmp/air-fixture.log curl -fsS "${UPSTREAM}/health"

step "start router on :${ROUTER_PORT}"
"$BIN" serve --config "$CONFIG" --listen "$ROUTER" >/tmp/air-router.log 2>&1 &
ROUTER_PID=$!
wait_for "router" "$ROUTER_PID" /tmp/air-router.log curl -fsS "${ROUTER}/healthz"

step "/v1/models lists the configured model (health probe must land first)"
wait_for "model catalogue" "$ROUTER_PID" /tmp/air-router.log sh -c "curl -fsS '${ROUTER}/v1/models' | grep -q fixture-model"

step "admin status reports the engine as healthy"
wait_for "engine health" "$ROUTER_PID" /tmp/air-router.log sh -c "curl -fsS '${ROUTER}/admin/status' | grep -q Healthy.:true"

step "non-streaming chat through HTTP"
resp=$(curl -fsS --max-time 30 -X POST "${ROUTER}/v1/chat/completions" -H 'content-type: application/json' \
  -d '{"model":"fixture-model","messages":[{"role":"user","content":"smoke"}]}')
echo "$resp" | grep -q "fixture-ok" || fail "chat response missing fixture-ok: $resp"

step "streaming chat through HTTP relays every frame"
sse=$(curl -fsS --max-time 30 -N -X POST "${ROUTER}/v1/chat/completions" -H 'content-type: application/json' \
  -d '{"model":"fixture-model","stream":true,"messages":[{"role":"user","content":"smoke"}]}') || fail "streaming request did not complete"
echo "$sse" | grep -q 'data: ' || fail "stream produced no data frames"
echo "$sse" | grep -q '\[DONE\]' || fail "stream was not terminated"
printf '%s' "$sse" | grep -o '"content":[[:space:]]*"[^"]*"' | sed 's/.*:[[:space:]]*"//;s/"$//' | tr -d '\n' > /tmp/air-stream.txt
grep -q "fixture-ok" /tmp/air-stream.txt || fail "streamed deltas do not reassemble to fixture-ok"

step "chat through the CLI client"
cli_out=$("$BIN" chat --server "$ROUTER" --no-stream smoke-me 2>&1)
echo "$cli_out" | grep -q "fixture-ok" || fail "CLI chat did not print fixture-ok: $cli_out"

step "engine failure maps to 502 engine_error"
curl -fsS -X POST "${UPSTREAM}/__fail" >/dev/null || fail "could not arm the failing upstream"
code=$(curl -s -o /tmp/air-error.json -w '%{http_code}' -X POST "${ROUTER}/v1/chat/completions" \
  -H 'content-type: application/json' -d '{"model":"fixture-model","messages":[{"role":"user","content":"smoke"}]}')
[ "$code" = "502" ] || fail "expected 502 from a failing engine, got $code"
grep -q "engine" /tmp/air-error.json || fail "error body missing an engine error code"

step "router logged no unhandled failures"
if grep -qiE "unhandled exception|fatal" /tmp/air-router.log; then
  fail "router log contains an unhandled failure: $(tail -5 /tmp/air-router.log)"
fi

echo "SMOKE OK: $BIN"
