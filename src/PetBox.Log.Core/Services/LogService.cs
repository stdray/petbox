using Kusto.Language;
using PetBox.Core.Models;
using PetBox.Log.Core.Contract;
using PetBox.Log.Core.Data;
using PetBox.Log.Core.Query;
using PetBox.Log.Core.Tracing;

namespace PetBox.Log.Core.Services;

// The one implementation of ILogService. See ILogService for why this door exists.
//
// Every method here follows the same shape: open a context, read, dispose before returning. The
// `using` is deliberately inside each method rather than around a shared field — a LogDb is a live
// SQLite connection, and holding one for the lifetime of a scoped service is the exact thing
// conn-safety forbids.
public sealed class LogService(ILogStore store, ILogQueryService queries) : ILogService
{
	// SQLite's "no such table" — the young-log case, not a failure. Translated at this boundary
	// so no caller above it needs to know the provider or the code.
	const int SqliteNoSuchTable = 1;

	public Task<IReadOnlyList<LogMeta>> ListAsync(string projectKey, CancellationToken ct = default) =>
		store.ListAsync(projectKey, ct);

	public Task<bool> ExistsAsync(string projectKey, string logName, CancellationToken ct = default) =>
		store.ExistsAsync(projectKey, logName, ct);

	public Task<LogMeta> CreateAsync(string projectKey, string logName, string? description, CancellationToken ct = default) =>
		store.CreateAsync(projectKey, logName, description, ct: ct);

	public Task<bool> DeleteAsync(string projectKey, string logName, CancellationToken ct = default) =>
		store.DeleteAsync(projectKey, logName, ct);

	public Task<LogQueryResult> QueryAsync(
		string projectKey, string logName, string kql, CancellationToken ct = default) =>
		queries.QueryAsync(projectKey, logName, kql, ct);

	public async Task<LogEntryRecord?> GetEventAsync(
		string projectKey, string logName, long id, CancellationToken ct = default)
	{
		using var logDb = store.NewEnsuredContext(projectKey, logName);
		return await logDb.LogEntries.FirstOrDefaultAsync(e => e.Id == id, ct);
	}

	public async Task<IReadOnlyList<LogEntryRecord>> QueryEventsAsync(
		string projectKey, string logName, KustoCode code, int? take = null, CancellationToken ct = default)
	{
		using var logDb = store.NewEnsuredContext(projectKey, logName);
		var query = KqlTransformer.Apply(logDb.LogEntries, code);
		if (take is { } cap) query = query.Take(cap);

		try
		{
			return await query.ToListAsync(ct);
		}
		catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == SqliteNoSuchTable)
		{
			throw new LogSchemaMissingException($"log '{logName}' has no events table yet", ex);
		}
	}

	public async Task<LogTableResult> QueryTableAsync(
		string projectKey, string logName, KustoCode code, string root, int maxRows, CancellationToken ct = default)
	{
		using var logDb = store.NewEnsuredContext(projectKey, logName);

		try
		{
			var result = string.Equals(root, KqlTransformer.SpansTable, StringComparison.Ordinal)
				? KqlTransformer.ExecuteSpans(logDb.Spans, code)
				: string.Equals(root, KqlTransformer.MetricsTable, StringComparison.Ordinal)
					? KqlTransformer.ExecuteMetrics(logDb.MetricPoints, code)
					: KqlTransformer.Execute(logDb.LogEntries, code);

			// Materialized HERE, while the context is still open — the caller gets a list, never a
			// lazy sequence that would fault on a disposed connection after this method returns.
			var rows = new List<object?[]>();
			var truncated = false;
			await foreach (var row in result.Rows.WithCancellation(ct))
			{
				if (rows.Count >= maxRows)
				{
					truncated = true;
					break;
				}
				rows.Add(row);
			}

			return new LogTableResult(result.Columns, rows, truncated);
		}
		catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == SqliteNoSuchTable)
		{
			throw new LogSchemaMissingException($"log '{logName}' has no '{root}' table yet", ex);
		}
	}

	public async Task<IReadOnlyList<string>> ListServiceKeysAsync(
		string projectKey, string logName, CancellationToken ct = default)
	{
		using var logDb = store.NewEnsuredContext(projectKey, logName);
		try
		{
			return await logDb.LogEntries
				.Select(e => e.ServiceKey)
				.Distinct()
				.OrderBy(s => s)
				.ToListAsync(ct);
		}
		catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == SqliteNoSuchTable)
		{
			throw new LogSchemaMissingException($"log '{logName}' has no events table yet", ex);
		}
	}

	public async Task<IReadOnlyList<SpanRecord>> ListTraceSpansAsync(
		string projectKey, string logName, string traceId, CancellationToken ct = default)
	{
		using var logDb = store.NewEnsuredContext(projectKey, logName);
		try
		{
			return await logDb.Spans
				.Where(s => s.TraceId == traceId)
				.OrderBy(s => s.StartUnixNs)
				.ToListAsync(ct);
		}
		catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == SqliteNoSuchTable)
		{
			throw new LogSchemaMissingException($"log '{logName}' has no spans table yet", ex);
		}
	}

	public async Task<TraceGroupPage> ListTraceGroupsAsync(
		string projectKey, string logName, bool errorsOnly, int offset, int limit, CancellationToken ct = default)
	{
		using var logDb = store.NewEnsuredContext(projectKey, logName);

		try
		{
			var q = logDb.Spans
				.GroupBy(s => s.TraceId)
				.Select(g => new
				{
					TraceId = g.Key,
					MinStart = g.Min(s => s.StartUnixNs),
					MaxEnd = g.Max(s => s.EndUnixNs),
					Count = g.Count(),
					WorstStatus = g.Max(s => s.StatusCode),
				});
			// The error filter runs at the query (a HAVING over the per-trace worst status), so
			// paging counts filtered traces — never a client-side cull of a full page.
			if (errorsOnly) q = q.Where(g => g.WorstStatus == 2);

			var grouped = await q
				.OrderByDescending(g => g.MinStart)
				.Skip(offset)
				.Take(limit + 1)
				.ToListAsync(ct);

			var hasNext = grouped.Count > limit;
			if (hasNext) grouped.RemoveAt(grouped.Count - 1);

			var traceIds = grouped.Select(g => g.TraceId).ToList();
			var roots = await logDb.Spans
				.Where(s => traceIds.Contains(s.TraceId) && s.ParentSpanId == null)
				.ToListAsync(ct);

			// A trace is NOT guaranteed to have exactly one root span: an ingester can be handed
			// several parentless spans under one TraceId (the smoke fixtures reuse a constant id,
			// and a partially-exported trace looks the same). ToDictionary threw a 500 on that —
			// group and take the earliest root instead, so the name is deterministic either way.
			var rootByTrace = roots
				.GroupBy(s => s.TraceId)
				.ToDictionary(g => g.Key, g => g.OrderBy(s => s.StartUnixNs).First().Name);

			var rows = grouped
				.Select(g => new TraceGroupRow(
					g.TraceId,
					rootByTrace.GetValueOrDefault(g.TraceId, "(no root)"),
					g.MinStart,
					g.MaxEnd,
					g.Count,
					g.WorstStatus))
				.ToList();

			return new TraceGroupPage(rows, hasNext);
		}
		catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == SqliteNoSuchTable)
		{
			throw new LogSchemaMissingException($"log '{logName}' has no spans table yet", ex);
		}
	}
}
