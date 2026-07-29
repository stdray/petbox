using Kusto.Language;
using PetBox.Core.Models;
using PetBox.Log.Core.Data;
using PetBox.Log.Core.Query;
using PetBox.Log.Core.Tracing;

namespace PetBox.Log.Core.Contract;

// THE page-facing door for PetBox.Log.Core — the presentation layer's equivalent of what
// IConfigDirectory is for PetBox.Config (AGENTS.md § "Database connections — a hard invariant").
//
// ILogStore is a CONNECTION door: it hands out a live LogDb (`NewEnsuredContext`) that the caller
// owns and must dispose. That is the right shape for module-internal code (ingestion, retention,
// LogApi) but the wrong shape for a Razor page — a page that holds it opens log SQLite files
// inline, and eight of them had drifted into doing exactly that with eight slightly different
// takes on paging, root-span grouping and the missing-table case.
//
// So this interface returns MATERIALIZED results and never leaks a LogDb. Every method opens its
// own context, reads, and disposes before returning — which also means a page can no longer hold
// a connection open across a render (the bug class conn-safety was about).
//
// Where a page still needs to PARSE KQL it keeps doing so: KustoCode/KqlTransformer are
// query-language types, not database types, and the page's parse-error UX depends on the
// diagnostics. What moves here is only the part that touches a file.
//
// ILogQueryService (the shared KQL execution path behind the log_query MCP tool and the REST
// endpoint) is NOT replaced by this interface — callers that want the raw KQL path (log_query,
// the REST log endpoint) inject ILogQueryService directly instead of going through here. This
// interface used to also expose a QueryAsync wrapper delegating to it, but nothing ever called
// the wrapper (every caller already injected ILogQueryService directly) — removed as dead code
// (resharper-clt-step5b-dto-contract-and-minimal-api), not suppressed.
public interface ILogService
{
	// --- Catalog (core.db Logs table) -----------------------------------------

	// The project's named logs. Backs the log picker on every log page and the admin list.
	Task<IReadOnlyList<LogMeta>> ListAsync(string projectKey, CancellationToken ct = default);

	Task<bool> ExistsAsync(string projectKey, string logName, CancellationToken ct = default);

	Task<LogMeta> CreateAsync(string projectKey, string logName, string? description, CancellationToken ct = default);

	Task<bool> DeleteAsync(string projectKey, string logName, CancellationToken ct = default);

	// --- Events ---------------------------------------------------------------

	// One event row by id, scoped to project+log. Null when the row does not exist.
	Task<LogEntryRecord?> GetEventAsync(string projectKey, string logName, long id, CancellationToken ct = default);

	// Runs an already-parsed events query and materializes the rows. `take` is a memory guard
	// applied ON TOP of whatever limit the KQL itself carries — pass null when the caller has
	// already appended its own `| take` (the paged log viewer does).
	// Throws LogSchemaMissingException when the log file has no events table yet.
	Task<IReadOnlyList<LogEntryRecord>> QueryEventsAsync(
		string projectKey, string logName, KustoCode code, int? take = null, CancellationToken ct = default);

	// Runs a shape-changing query (a projection/summarize over events, or any spans/metrics
	// query) and materializes the column-shaped result, capped at `maxRows`.
	// `root` selects the table — KqlTransformer.EventsTable/SpansTable/MetricsTable.
	// Throws LogSchemaMissingException when the log file has no such table yet.
	Task<LogTableResult> QueryTableAsync(
		string projectKey, string logName, KustoCode code, string root, int maxRows, CancellationToken ct = default);

	// Distinct ServiceKey values across the log's events, ordered — the log page's service filter.
	Task<IReadOnlyList<string>> ListServiceKeysAsync(string projectKey, string logName, CancellationToken ct = default);

	// --- Tracing --------------------------------------------------------------

	// Every span of one trace, ordered by start time — the waterfall.
	Task<IReadOnlyList<SpanRecord>> ListTraceSpansAsync(
		string projectKey, string logName, string traceId, CancellationToken ct = default);

	// One page of trace summaries, newest-first, each already carrying its root span's name.
	// `errorsOnly` filters at the query (a HAVING over the per-trace worst status) so paging
	// counts filtered traces rather than culling a full page client-side.
	// Throws LogSchemaMissingException when the log file has no spans table yet.
	Task<TraceGroupPage> ListTraceGroupsAsync(
		string projectKey, string logName, bool errorsOnly, int offset, int limit, CancellationToken ct = default);
}

// A column-shaped query result, fully materialized. `Truncated` means the row cap was hit and the
// result is a prefix, not the whole answer.
public sealed record LogTableResult(
	IReadOnlyList<KqlColumn> Columns,
	IReadOnlyList<object?[]> Rows,
	bool Truncated);

// One trace, summarized across its spans.
public sealed record TraceGroupRow(
	string TraceId,
	string RootName,
	long MinStartNs,
	long MaxEndNs,
	int SpanCount,
	int WorstStatus);

// `HasNext` is probed by reading one row past the page, so it is exact rather than a guess from
// a separate COUNT that could race the read.
public sealed record TraceGroupPage(IReadOnlyList<TraceGroupRow> Rows, bool HasNext);

// The log file exists but the table the query targets has not been created yet (a log that has
// received events but no spans, say). Pages render this as an empty-state, NOT an error — it is
// the normal shape of a young log.
//
// This exists so the presentation layer does not have to catch Microsoft.Data.Sqlite.SqliteException
// and test `SqliteErrorCode == 1` — a provider detail that has no business in a page.
public sealed class LogSchemaMissingException : Exception
{
	public LogSchemaMissingException(string message, Exception innerException)
		: base(message, innerException) { }
}
