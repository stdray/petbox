using System.Diagnostics;

namespace PetBox.Core.Observability;

// Named ActivitySources for the service layer (spec: self-tracing). Spans sit at
// operation boundaries — one write, one drain pass, one tool call — never per-event
// or per-row: per-event instrumentation is too expensive and bloats the trace store
// (spec: trace-operation-granularity). Log.Core keeps its own sources
// (PetBox.Ingestion/Query/Retention) in PetBox.Log.Core.Observability.ActivitySources.
public static class PetBoxActivitySources
{
	public const string TasksSourceName = "PetBox.Tasks";
	public const string MemorySourceName = "PetBox.Memory";
	public const string SearchSourceName = "PetBox.Search";
	public const string McpSourceName = "PetBox.Mcp";

	public static readonly ActivitySource Tasks = new(TasksSourceName);
	public static readonly ActivitySource Memory = new(MemorySourceName);
	public static readonly ActivitySource Search = new(SearchSourceName);
	public static readonly ActivitySource Mcp = new(McpSourceName);
}
