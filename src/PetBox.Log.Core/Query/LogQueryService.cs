using System.Runtime.CompilerServices;
using Kusto.Language;
using LinqToDB.Async;
using PetBox.Log.Core.Data;
using PetBox.Log.Core.Models;

namespace PetBox.Log.Core.Query;

// The shared KQL execution path for a named log. The MCP log_query tool and the REST
// /api/logs/{p}/{log} endpoint used to re-implement the same orchestration (exists-check,
// KQL parse + diagnostics, shape-changing branch, transform) around the already-shared
// KqlTransformer. This collapses that orchestration into one place; each adapter keeps
// its own auth, wire shape and error mapping. Enumeration of a shape-changing result is
// left to the caller (the Table case streams rows), so UnsupportedKqlException — and
// KqlExecutionException for engine faults — can still surface in the adapter's
// await-foreach; both adapters handle those.
public interface ILogQueryService
{
	Task<LogQueryResult> QueryAsync(string projectKey, string logName, string kql, CancellationToken ct = default);
}

// Discriminated result: a plain query yields materialized events; a shape-changing
// pipeline (summarize/project/…) yields a streaming column/row table. Both arms carry a
// truncation signal (KqlLimits response capping): Events is materialized, so a plain bool;
// Table streams, so the flag lives in a box the limiter sets WHEN it cuts the stream —
// adapters read it only after enumerating Rows (both buffer before writing the response).
public abstract record LogQueryResult
{
	public sealed record Events(IReadOnlyList<LogEntry> Items, bool Truncated) : LogQueryResult;
	public sealed record Table(KqlResult Result, TruncationSignal Truncation) : LogQueryResult;
}

// Mutable by design: set during row enumeration, read after it.
public sealed class TruncationSignal
{
	public bool Truncated { get; internal set; }
}

// The named log does not exist for this project. Adapters map it (REST -> 404).
public sealed class LogNotFoundException(string projectKey, string logName)
	: Exception($"log '{logName}' not found in project '{projectKey}'");

// KQL failed to parse. Details carries the per-diagnostic messages. Adapters map it
// (REST -> 400 with details; MCP -> ArgumentException).
public sealed class KqlParseException(IReadOnlyList<string> details)
	: Exception("KQL parse error: " + string.Join("; ", details))
{
	public IReadOnlyList<string> Details { get; } = details;
}

// A syntactically valid query failed while EXECUTING — linq2db SQL translation, the
// SQLite engine, or row streaming. Distinct from the USER errors above (parse /
// unsupported construct / unknown column → 400): this is an internal fault the adapters
// render structurally (REST -> JSON 500 with type + message, MCP -> the {error}
// envelope) instead of leaking an HTML error page / opaque tool failure.
public sealed class KqlExecutionException(string kql, Exception inner)
	: Exception($"KQL execution failed for '{kql}': {inner.Message}", inner);

public sealed class LogQueryService(ILogStore store) : ILogQueryService
{
	public async Task<LogQueryResult> QueryAsync(string projectKey, string logName, string kql, CancellationToken ct = default)
	{
		if (string.IsNullOrWhiteSpace(kql)) throw new ArgumentException("kql is required");
		if (!await store.ExistsAsync(projectKey, logName, ct))
			throw new LogNotFoundException(projectKey, logName);

		KustoCode code;
		try
		{
			code = KustoCode.Parse(kql);
			var parseErrors = code.GetDiagnostics()
				.Where(d => d.Severity == "Error")
				.Select(d => d.Message)
				.ToList();
			if (parseErrors.Count > 0) throw new KqlParseException(parseErrors);
		}
		catch (KqlParseException) { throw; }
		catch (Exception ex) { throw new KqlParseException([ex.Message]); }

		var logDb = store.GetContext(projectKey, logName);
		var root = KqlTransformer.GetRootTableName(code);

		// Response capping (KqlLimits): no explicit take/top → DefaultTake, an explicit one is
		// bounded by MaxTake. Applied to every arm below; the extra probed row is how truncation
		// is detected without a separate count query.
		var limit = KqlLimits.EffectiveRowLimit(code);

		// The `spans` root runs the SAME KQL subset over the log's Spans table. A plain spans query has no
		// LogEntry-shaped result, so spans ALWAYS yield the streamed span column shape (a Table); the
		// events root keeps its Events/Table split. Row streaming stays lazy, so an engine fault during
		// enumeration is wrapped just like the events Table path.
		if (string.Equals(root, KqlTransformer.SpansTable, StringComparison.Ordinal))
		{
			var spanTable = BuildTable(() => KqlTransformer.ExecuteSpans(logDb.Spans, code), kql);
			var spanSignal = new TruncationSignal();
			return new LogQueryResult.Table(
				spanTable with { Rows = LimitRows(WrapExecutionErrors(spanTable.Rows, kql, ct), limit, spanSignal, ct) },
				spanSignal);
		}

		// The `metrics` root: same treatment as spans — a plain metrics query has no LogEntry-shaped
		// result, so it ALWAYS yields the streamed metric column shape (a Table).
		if (string.Equals(root, KqlTransformer.MetricsTable, StringComparison.Ordinal))
		{
			var metricTable = BuildTable(() => KqlTransformer.ExecuteMetrics(logDb.MetricPoints, code), kql);
			var metricSignal = new TruncationSignal();
			return new LogQueryResult.Table(
				metricTable with { Rows = LimitRows(WrapExecutionErrors(metricTable.Rows, kql, ct), limit, metricSignal, ct) },
				metricSignal);
		}

		// An unknown root fails HERE with the full supported-table list: the events-only engine entries
		// below (Execute/Apply) are entitled to claim only 'events', which would be wrong on this
		// surface, where `spans` and `metrics` work too.
		if (root is not null && !string.Equals(root, KqlTransformer.EventsTable, StringComparison.Ordinal))
			throw new UnsupportedKqlException(KqlTransformer.UnknownTableMessage(root));

		if (KqlTransformer.HasShapeChangingOps(code))
		{
			// Execute throws UnsupportedKqlException synchronously while building the
			// pipeline (user error); actual row streaming is lazy and enumerated by the
			// ADAPTER, so engine failures there must be translated at the source.
			var table = BuildTable(() => KqlTransformer.Execute(logDb.LogEntries, code), kql);
			var signal = new TruncationSignal();
			return new LogQueryResult.Table(
				table with { Rows = LimitRows(WrapExecutionErrors(table.Rows, kql, ct), limit, signal, ct) },
				signal);
		}

		try
		{
			// Take(limit + 1) rides the SQL LIMIT (composing with any explicit take as a min), so a
			// plain `events` never materializes the whole table — the OOM vector this capping closes.
			var records = await KqlTransformer.Apply(logDb.LogEntries, code).Take(limit + 1).ToListAsync(ct);
			var truncated = records.Count > limit;
			if (truncated)
				records.RemoveAt(records.Count - 1);
			return new LogQueryResult.Events(records.Select(r => r.ToEntry()).ToList(), truncated);
		}
		catch (UnsupportedKqlException) { throw; }
		catch (OperationCanceledException) { throw; }
		catch (Exception ex) { throw new KqlExecutionException(kql, ex); }
	}

	// Cuts the streamed rows at `limit` and flags the signal when at least one more row existed.
	static async IAsyncEnumerable<object?[]> LimitRows(
		IAsyncEnumerable<object?[]> rows, int limit, TruncationSignal signal,
		[EnumeratorCancellation] CancellationToken ct = default)
	{
		var emitted = 0;
		await foreach (var row in rows.WithCancellation(ct).ConfigureAwait(false))
		{
			if (emitted >= limit)
			{
				signal.Truncated = true;
				yield break;
			}
			emitted++;
			yield return row;
		}
	}

	// Classify SYNCHRONOUS pipeline-build failures identically for both roots: UnsupportedKqlException
	// is the user's error and propagates as-is; anything else is an engine fault → the typed execution
	// error (streamed failures get the same treatment in WrapExecutionErrors below).
	static KqlResult BuildTable(Func<KqlResult> build, string kql)
	{
		try { return build(); }
		catch (UnsupportedKqlException) { throw; }
		catch (OperationCanceledException) { throw; }
		catch (Exception ex) { throw new KqlExecutionException(kql, ex); }
	}

	// A shape-changing result streams: MoveNextAsync is where linq2db translation and
	// SQLite failures actually surface (well after QueryAsync returned). Wrap them into
	// the typed execution error so both adapters classify streamed failures the same
	// way as materialized ones.
	static async IAsyncEnumerable<object?[]> WrapExecutionErrors(
		IAsyncEnumerable<object?[]> rows, string kql, [EnumeratorCancellation] CancellationToken ct = default)
	{
		await using var e = rows.GetAsyncEnumerator(ct);
		while (true)
		{
			bool moved;
			try
			{
				moved = await e.MoveNextAsync();
			}
			catch (UnsupportedKqlException) { throw; }
			catch (OperationCanceledException) { throw; }
			catch (Exception ex) { throw new KqlExecutionException(kql, ex); }
			if (!moved) yield break;
			yield return e.Current;
		}
	}
}
