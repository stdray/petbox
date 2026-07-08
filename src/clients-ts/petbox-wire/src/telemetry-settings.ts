// The OTLP export env that `--telemetry` writes into Claude Code's settings, extracted into its
// own importable module (wire.ts runs main() at module top level and must never be imported by a
// test — same reason posix-env.ts exists). The env-string building here is the load-bearing,
// regression-prone part: it must emit a RESOLVED api key and the exact auth header the server
// validates. wire.ts owns the file-merge glue (mergeEnvIntoSettings) and calls this.

// Auth header the PetBox path-based OTLP routes (/v1/{signal}/{project}/{log}) validate. Those
// routes use RequireAuthorization("ApiKey"), and the ApiKey scheme reads X-Api-Key
// (PetBox.Core/Auth/ApiKeyAuthenticationHandler.cs: ApiKeyHeader = "X-Api-Key"). This is NOT the
// bare self-export routes' X-Seq-ApiKey (those are AllowAnonymous + a shared self-log secret and
// are not what telemetry targets), so X-Api-Key is the correct name here.
export const OTLP_API_KEY_HEADER = "X-Api-Key";
// IngestLogs additionally requires a non-empty X-Service-Key header (400 otherwise); it only tags
// the emitter (free string, no Service entity).
export const OTLP_SERVICE_KEY_HEADER = "X-Service-Key";
export const OTLP_SERVICE_KEY_VALUE = "claude-code";

export interface TelemetryOtlpEnv {
  // Non-secret export config → committable .claude/settings.json.
  publicEnv: Record<string, string>;
  // API-key-bearing header → gitignored .claude/settings.local.json.
  secretEnv: Record<string, string>;
}

// Build the OTLP export env split by secrecy.
//
// The api key is written RESOLVED (the literal key value), NOT as a `${VAR}` reference: Claude Code
// does NOT expand `${VAR}` inside settings.json `env` values (unlike .mcp.json) — empirically
// verified 2026-07-06 — so a reference form would send the literal string `${VAR}` and the OTLP
// ingest returns 401. Tradeoff: a literal key PINS the value, so if the project api key rotates the
// header goes stale — re-run wire (--telemetry) to re-provision. The key already lives plaintext in
// ~/.petbox/keys.json; settings.local.json (gitignored) is the same trust boundary, per-project.
export function buildTelemetryOtlpEnv(
  baseUrl: string,
  project: string,
  key: string,
  logName: string,
): TelemetryOtlpEnv {
  const metricsEndpoint = `${baseUrl}/v1/metrics/${project}/${logName}`;
  const logsEndpoint = `${baseUrl}/v1/logs/${project}/${logName}`;
  const publicEnv: Record<string, string> = {
    CLAUDE_CODE_ENABLE_TELEMETRY: "1",
    OTEL_METRICS_EXPORTER: "otlp",
    OTEL_LOGS_EXPORTER: "otlp",
    OTEL_EXPORTER_OTLP_PROTOCOL: "http/protobuf",
    OTEL_EXPORTER_OTLP_METRICS_ENDPOINT: metricsEndpoint,
    OTEL_EXPORTER_OTLP_LOGS_ENDPOINT: logsEndpoint,
    OTEL_METRIC_EXPORT_INTERVAL: "5000",
  };
  const secretEnv: Record<string, string> = {
    // OTLP header list format: comma-separated key=value pairs. Resolved key, correct header name.
    OTEL_EXPORTER_OTLP_HEADERS: `${OTLP_API_KEY_HEADER}=${key},${OTLP_SERVICE_KEY_HEADER}=${OTLP_SERVICE_KEY_VALUE}`,
  };
  return { publicEnv, secretEnv };
}
