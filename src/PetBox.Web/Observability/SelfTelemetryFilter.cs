namespace PetBox.Web.Observability;

// Outgoing-HTTP trace filter: telemetry self-export must not be traced. The OTLP
// exporter POSTs span batches to /v1/traces (PETBOX_OTEL_ENDPOINT=self points back at
// this same app), so tracing that call would emit a new span per export batch, forever
// (spec: trace-outgoing-http). /v1/logs (self-log shipping) is excluded for the same
// reason. Matches by path, not host, so it also holds when telemetry targets a remote
// collector.
public static class SelfTelemetryFilter
{
	public static bool ShouldTrace(Uri? uri) =>
		uri is null
		|| !(uri.AbsolutePath.StartsWith("/v1/traces", StringComparison.Ordinal)
			|| uri.AbsolutePath.StartsWith("/v1/logs", StringComparison.Ordinal));
}
