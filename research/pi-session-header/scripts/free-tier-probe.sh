#!/usr/bin/env bash
# Can pi reach OpenCode's FREE models by sending OpenCode's own headers?
#
# Upstream evidence: earendil-works/pi#9690 — 403 FreeTierError
# "OpenCode's free tier can only be used from within OpenCode"; the reporter also tried
# OpenCode's headers with a pi-generated session and still got 403, while a server-issued
# `ses_...` session returned 200.
#
# Needs the OpenCode (Zen) API key in $OPENCODE_API_KEY — NOT the opencode-go key.
# Read-only probes: free models, max_tokens 4, nothing is created.
#
#   OPENCODE_API_KEY=... ./free-tier-probe.sh [model] [real-session-id]
set -u

MODEL="${1:-deepseek-v4.1-flash-free}"
REAL_SES="${2:-}"
BASE="https://opencode.ai/inference/openai/v1/chat/completions"
KEY="${OPENCODE_API_KEY:?OPENCODE_API_KEY is not set (the provider is 'opencode', not 'opencode-go')}"

PI_UA="pi/1.5.0 (win32; node/v24.11.0; x64)"
OC_UA="opencode/prod/2.2.5/opencode"
UUID_PI="01a19c7f-c534-7262-94c2-2a465dbca25f"
SES_SYNTH="ses_00000000ffff$(printf '%s' "$UUID_PI" | tr -d '-' | cut -c1-15)"

probe() {
  local label="$1"; shift
  local out status body
  out=$(curl -sS --max-time 60 -o /tmp/free-probe-body.txt -w '%{http_code}' -X POST "$BASE" \
    -H "authorization: Bearer $KEY" \
    -H 'content-type: application/json' \
    "$@" \
    --data "{\"model\":\"$MODEL\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}],\"max_tokens\":4}")
  status=$out
  body=$(head -c 300 /tmp/free-probe-body.txt | tr -d '\n')
  printf '%-34s %s  %s\n' "$label" "$status" "$body"
}

hdr() { printf '%s' "$1"; }

echo "model: $MODEL"
probe "1 no session headers (control)" \
  -H "user-agent: $PI_UA"
probe "2 pi headers + pi session" \
  -H "user-agent: $PI_UA" -H "x-opencode-client: pi" -H "x-opencode-session: $UUID_PI"
probe "3 opencode UA/headers + pi session" \
  -H "user-agent: $OC_UA" -H "x-opencode-client: opencode" -H "x-opencode-session: $UUID_PI"
probe "4 opencode UA/headers + synthetic ses_" \
  -H "user-agent: $OC_UA" -H "x-opencode-client: opencode" -H "x-opencode-session: $SES_SYNTH"
if [ -n "$REAL_SES" ]; then
  probe "5 opencode UA/headers + real ses_ (from db)" \
    -H "user-agent: $OC_UA" -H "x-opencode-client: opencode" -H "x-opencode-session: $REAL_SES"
  probe "6 full OpenCode header set + real ses_" \
    -H "user-agent: $OC_UA" -H "x-opencode-client: opencode" \
    -H "x-opencode-session: $REAL_SES" -H "x-opencode-session-id: $REAL_SES" \
    -H "x-session-affinity: $REAL_SES" -H "X-Session-Id: $REAL_SES"
  probe "7 pi UA/headers + real ses_" \
    -H "user-agent: $PI_UA" -H "x-opencode-client: pi" -H "x-opencode-session: $REAL_SES"
fi
