using Kusto.Language;
using LinqToDB;
using PetBox.Core.Models;
using PetBox.Log.Core.Contract;
using PetBox.Log.Core.Data;
using PetBox.Log.Core.Query;
using PetBox.Log.Core.Tracing;

namespace PetBox.Log.Core.Services;

public sealed class LogService(ILogStore store, ILogQueryService queryService) : ILogService
{
	public async Task<IReadOnlyList<string>> ListLogNamesAsync(string projectKey, CancellationToken ct = default)
	{
		var list = await store.ListAsync(projectKey, ct);
		return list.Select(l => l.Name).ToList().AsReadOnly();
	}

	public Task<bool> ExistsAsync(string projectKey, string logName, CancellationToken ct = default) =>
		store.ExistsAsync(projectKey, logName, ct);

	public Task<LogMeta> CreateAsync(string projectKey, string name, string? description, CancellationToken ct = default) =>
		store.CreateAsync(projectKey, name, description, ct);

	public Task<bool> DeleteAsync(string projectKey, string logName, CancellationToken ct = default) =>
		store.DeleteAsync(projectKey, logName, ct);

	public Task<LogQueryResult> QueryAsync(string projectKey, string logName, string kql, CancellationToken ct = default) =>
		queryService.QueryAsync(projectKey, logName, kql, ct);

	public async Task<IReadOnlyList<SpanRecord>> GetSpansByTraceIdAsync(string projectKey, string logName, string traceId, CancellationToken ct = default)
	{
		using var logDb = store.NewEnsuredContext(projectKey, logName);
		return await logDb.Spans
			.Where(s => s.TraceId == traceId)
			.OrderBy(s => s.StartUnixNs)
			.ToListAsync(ct);
	}

	public async Task<IReadOnlyList<(string TraceId, long MinStartNs, long MaxEndNs, int SpanCount, int WorstStatus)>> GetTraceGroupSummariesAsync(
		string projectKey, string logName, bool errorsOnly, int offset, int limit, CancellationToken ct = default)
	{
		using var logDb = store.NewEnsuredContext(projectKey, logName);
		var query = logDb.Spans.GroupBy(s => s.TraceId);
		if (errorsOnly)
			query = query.Where(g => g.Any(s => s.StatusCode == (int)SpanStatusCode.Error));
		var result = await query
			.Select(g => new
			{
				g.Key,
				MinStartNs = g.Min(s => s.StartUnixNs),
				MaxEndNs = g.Max(s => s.EndUnixNs),
				SpanCount = g.Count(),
				WorstStatus = g.Max(s => s.StatusCode)
			})
			.OrderByDescending(x => x.MinStartNs)
			.Skip(offset)
			.Take(limit)
			.ToListAsync(ct);
		return result.Select(x => (x.Key, x.MinStartNs, x.MaxEndNs, x.SpanCount, x.WorstStatus)).ToList().AsReadOnly();
	}

	public async Task<IReadOnlyList<SpanRecord>> GetRootSpansForTracesAsync(string projectKey, string logName, IReadOnlyList<string> traceIds, CancellationToken ct = default)
	{
		using var logDb = store.NewEnsuredContext(projectKey, logName);
		return await logDb.Spans
			.Where(s => traceIds.Contains(s.TraceId) && (s.ParentSpanId == null || s.ParentSpanId == string.Empty))
			.ToListAsync(ct);
	}

	public async Task<IReadOnlyList<string>> GetDistinctServiceKeysAsync(string projectKey, string logName, CancellationToken ct = default)
	{
		using var logDb = store.NewEnsuredContext(projectKey, logName);
		return await logDb.LogEntries
			.Where(e => e.ServiceKey.Length > 0)
			.Select(e => e.ServiceKey)
			.Distinct()
			.OrderBy(s => s)
			.ToListAsync(ct);
	}

	public async Task<(IReadOnlyList<LogEntryRecord> Records, bool Truncated)> ExecutePlainEventsQueryAsync(
		string projectKey, string logName, string kql, int limit, CancellationToken ct)
	{
		using var logDb = store.NewEnsuredContext(projectKey, logName);
		var code = KustoCode.Parse(kql);
		var records = await KqlTransformer.Apply(logDb.LogEntries, code).Take(limit + 1).ToListAsync(ct);
		var truncated = records.Count > limit;
		if (truncated) records.RemoveAt(records.Count - 1);
		return (records, truncated);
	}

	public async Task<IReadOnlyList<LogMeta>> ListAsync(string projectKey, CancellationToken ct = default)
	{
		var list = await store.ListAsync(projectKey, ct);
		return list.ToList().AsReadOnly();
	}
}
