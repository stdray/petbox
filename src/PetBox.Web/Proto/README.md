# Vendored OpenTelemetry Protobuf definitions

These `.proto` files are copied verbatim from [open-telemetry/opentelemetry-proto][repo] tag `v1.5.0`. They're compiled to C# by `Grpc.Tools` at build time (see `YobaLog.Web.csproj` `<Protobuf Include="Proto/**/*.proto" />`).

## Why vendor?

Decision-log 2026-04-21 originally targeted the `OpenTelemetry.Proto` NuGet package — that package doesn't exist on nuget.org. Options evaluated:

- **`OpenTelemetry.Exporter.OpenTelemetryProtocol`** — no public proto types (confirmed via reflection on 1.15.1: 0 types in `OpenTelemetry.Proto.*` namespaces).
- **Hand-written decoder** — ~200 LOC of protobuf wire-format parsing; delicate around varints/field-skip.
- **Vendor + compile** (this file) — 424 lines of `.proto`, `Grpc.Tools` handles C# generation, zero runtime footprint changes. Picked.

## Which proto files?

OTLP Logs + Traces ingestion (Phase F + H.2):

- `common/v1/common.proto` — `AnyValue`, `KeyValue`, `InstrumentationScope`.
- `resource/v1/resource.proto` — `Resource`.
- `logs/v1/logs.proto` — `LogRecord`, `ResourceLogs`, `ScopeLogs`, `LogsData`, `SeverityNumber`.
- `collector/logs/v1/logs_service.proto` — `ExportLogsServiceRequest`, `ExportLogsServiceResponse`.
- `trace/v1/trace.proto` — `Span` (OTLP wire shape; maps to our `YobaLog.Core.Tracing.Span`), `ResourceSpans`, `ScopeSpans`, `Status`.
- `collector/trace/v1/trace_service.proto` — `ExportTraceServiceRequest`, `ExportTraceServiceResponse`.

OTLP Metrics is explicitly out of scope (decision-log 2026-04-21 — yobalog is logs + traces, metrics are Prometheus territory).

## How to update

When bumping to a newer OTel proto version:

```bash
V=1.6.0  # target version
B=https://raw.githubusercontent.com/open-telemetry/opentelemetry-proto/v$V/opentelemetry/proto
for p in common/v1/common resource/v1/resource \
         logs/v1/logs collector/logs/v1/logs_service \
         trace/v1/trace collector/trace/v1/trace_service; do
  curl -sSfo "src/YobaLog.Web/Proto/opentelemetry/proto/$p.proto" "$B/$p.proto"
done
```

Rebuild, run the OTLP compat tests, verify generated types still have the fields the parsers pull.

## Generated namespace

Proto `package opentelemetry.proto.<group>.v1` → C# namespace `OpenTelemetry.Proto.<Group>.V1` (via `csharp_namespace` option inside each `.proto`). Types are `public sealed class`; `Grpc.Tools` puts them into `$(IntermediateOutputPath)/protos/` during build.

## Invariant

Proto-generated types are **wire-boundary only**. They stay in `OtlpLogsParser` and never leak into `ILogStore` / `ISpanStore` contracts (decision-log 2026-04-21 Rule 1). The parser converts them to `LogEventCandidate` (immutable domain record) before data flows deeper.
[repo]: https://github.com/open-telemetry/opentelemetry-proto
