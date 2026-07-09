using PetBox.Core.Models;
using PetBox.Log.Core.Data;
using PetBox.Log.Core.Models;
using PetBox.Log.Core.Query;
using PetBox.Log.Core.Tracing;

namespace PetBox.Log.Core.Contract;

public interface ILogService
{
	// Catalog — wraps ILogStore
	Task<IReadOnlyList<string>> ListLogNamesAsync(string projectKey, CancellationToken ct = default);
	Task<bool> ExistsAsync(string projectKey, string logName, CancellationToken ct = default);
	Task<LogMeta> CreateAsync(string projectKey, string name, string? description, CancellationToken ct = default);
	Task<bool> DeleteAsync(string projectKey, string logName, CancellationToken ct = default);

	// KQL query — delegates to ILogQueryService
	Task<LogQueryResult> QueryAsync(string projectKey, string logName, string kql, CancellationToken ct = default);

	// Trace waterfall: spans for a trace, ordered by StartUnixNs
	Task<IReadOnlyList<SpanRecord>> GetSpansByTraceIdAsync(string projectKey, string logName, string traceId, CancellationToken ct = default);

	// Trace group summaries: grouped by TraceId with min start, max end, count, worst status
	Task<IReadOnlyList<(string TraceId, long MinStartNs, long MaxEndNs, int SpanCount, int WorstStatus)>> GetTraceGroupSummariesAsync(
		string projectKey, string logName, bool errorsOnly, int offset, int limit, CancellationToken ct = default);

	// Root spans for given trace IDs (ParentSpanId is null/empty)
	Task<IReadOnlyList<SpanRecord>> GetRootSpansForTracesAsync(string projectKey, string logName, IReadOnlyList<string> traceIds, CancellationToken ct = default);

	// Distinct service keys from log entries
	Task<IReadOnlyList<string>> GetDistinctServiceKeysAsync(string projectKey, string logName, CancellationToken ct = default);

	// Plain events query for the paginated log viewer (cursor-based paging, LogEntryRecord → LogEntryViewModel)
	Task<(IReadOnlyList<LogEntryRecord> Records, bool Truncated)> ExecutePlainEventsQueryAsync(
		string projectKey, string logName, string kql, int limit, CancellationToken ct = default);

	// Full catalog list (LogMeta with name, description, timestamps — for admin page)
	Task<IReadOnlyList<LogMeta>> ListAsync(string projectKey, CancellationToken ct = default);
}
