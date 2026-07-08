// Regression tests for the --telemetry OTLP export env (work telemetry-otlp-auth-401): the auth
// header MUST carry the RESOLVED api key and the exact header NAME the server validates, never a
// literal `${VAR}` placeholder (Claude Code does not expand `${VAR}` in settings.json `env`, so a
// placeholder is sent verbatim and the OTLP ingest returns 401).
//
// Run: node --test src/telemetry-settings.test.ts

import assert from "node:assert/strict";
import { test } from "node:test";
import {
  buildTelemetryOtlpEnv,
  OTLP_API_KEY_HEADER,
  OTLP_SERVICE_KEY_HEADER,
  OTLP_SERVICE_KEY_VALUE,
} from "./telemetry-settings.ts";

const BASE = "https://petbox.example";
const PROJECT = "acme";
const LOG = "cc-telemetry";
const KEY = "pk_live_RESOLVED_SECRET_123";

test("OTLP headers carry the RESOLVED key value, never a ${...} placeholder", () => {
  const { secretEnv } = buildTelemetryOtlpEnv(BASE, PROJECT, KEY, LOG);
  const headers = secretEnv.OTEL_EXPORTER_OTLP_HEADERS;
  assert.ok(headers, "OTEL_EXPORTER_OTLP_HEADERS must be present");
  assert.ok(headers.includes(KEY), "header must contain the resolved key value");
  assert.ok(!headers.includes("${"), `header must not contain a \${...} placeholder — got: ${headers}`);
  assert.ok(!/\{env:/.test(headers), "header must not contain an {env:...} reference");
});

test("auth header uses the exact name the server's ApiKey scheme validates (X-Api-Key)", () => {
  const { secretEnv } = buildTelemetryOtlpEnv(BASE, PROJECT, KEY, LOG);
  const headers = secretEnv.OTEL_EXPORTER_OTLP_HEADERS;
  assert.equal(OTLP_API_KEY_HEADER, "X-Api-Key");
  assert.ok(
    headers.includes(`${OTLP_API_KEY_HEADER}=${KEY}`),
    `expected "X-Api-Key=<key>" pair — got: ${headers}`,
  );
  // Must NOT use the bare self-export routes' shared-secret header (those routes aren't targeted).
  assert.ok(!/X-Seq-ApiKey/i.test(headers), "must not use X-Seq-ApiKey (wrong route's auth header)");
});

test("service-key header pair is present (IngestLogs 400s without X-Service-Key)", () => {
  const { secretEnv } = buildTelemetryOtlpEnv(BASE, PROJECT, KEY, LOG);
  assert.ok(
    secretEnv.OTEL_EXPORTER_OTLP_HEADERS.includes(`${OTLP_SERVICE_KEY_HEADER}=${OTLP_SERVICE_KEY_VALUE}`),
    "X-Service-Key=claude-code pair must be present",
  );
});

test("the secret (key-bearing) env is split out from the committable public env", () => {
  const { publicEnv, secretEnv } = buildTelemetryOtlpEnv(BASE, PROJECT, KEY, LOG);
  // The key must live only in the secret env (destined for gitignored settings.local.json).
  assert.ok(!JSON.stringify(publicEnv).includes(KEY), "public env must never contain the api key");
  assert.ok(Object.keys(secretEnv).length === 1, "secret env carries only the auth header");
});

test("endpoints are the path-based routes (project + log in the URL path)", () => {
  const { publicEnv } = buildTelemetryOtlpEnv(BASE, PROJECT, KEY, LOG);
  assert.equal(publicEnv.OTEL_EXPORTER_OTLP_METRICS_ENDPOINT, `${BASE}/v1/metrics/${PROJECT}/${LOG}`);
  assert.equal(publicEnv.OTEL_EXPORTER_OTLP_LOGS_ENDPOINT, `${BASE}/v1/logs/${PROJECT}/${LOG}`);
});
