using System.Diagnostics;

namespace PetBox.Log.Core.Observability;

// Named ActivitySources per subsystem. Spans are emitted at batch boundaries only —
// per-event instrumentation is too expensive at scale (yobalog measured 5-20% CPU at
// 100k events/sec). $system project is never instrumented because it's the destination
// of self-logging — tracing its writes would recurse through the export loop.
public static class ActivitySources
{
	public const string IngestionSourceName = "PetBox.Ingestion";
	public const string QuerySourceName = "PetBox.Query";
	public const string RetentionSourceName = "PetBox.Retention";

	public static readonly ActivitySource Ingestion = new(IngestionSourceName);
	public static readonly ActivitySource Query = new(QuerySourceName);
	public static readonly ActivitySource Retention = new(RetentionSourceName);
}
