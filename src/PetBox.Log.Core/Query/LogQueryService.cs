using System.Runtime.CompilerServices;
using Kusto.Language;
using LinqToDB.Async;
using PetBox.Log.Core.Data;
using PetBox.Log.Core.Models;

namespace PetBox.Log.Core.Query;

// The shared KQL execution path for a named log. The MCP log.query tool and the REST
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
// pipeline (summarize/project/…) yields a streaming column/row table.
public abstract record LogQueryResult
{
	public sealed record Events(IReadOnlyList<LogEntry> Items) : LogQueryResult;
	public sealed record Table(KqlResult Result) : LogQueryResult;
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
		if (KqlTransformer.HasShapeChangingOps(code))
		{
			// Execute throws UnsupportedKqlException synchronously while building the
			// pipeline (user error); actual row streaming is lazy and enumerated by the
			// ADAPTER, so engine failures there must be translated at the source.
			var table = KqlTransformer.Execute(logDb.LogEntries, code);
			return new LogQueryResult.Table(table with { Rows = WrapExecutionErrors(table.Rows, kql, ct) });
		}

		try
		{
			var records = await KqlTransformer.Apply(logDb.LogEntries, code).ToListAsync(ct);
			return new LogQueryResult.Events(records.Select(r => r.ToEntry()).ToList());
		}
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
