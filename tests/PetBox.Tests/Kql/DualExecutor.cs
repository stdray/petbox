using System.Text.Json.Nodes;
using Kusto.Language;
using KustoLoco.Core;
using PetBox.Log.Core;

namespace PetBox.Tests.Kql;

public sealed record TestEvent(
	long Id,
	DateTime Timestamp,
	int Level,
	string Message,
	string? ServiceKey = null,
	string? MessageTemplate = null,
	string? Exception = null,
	IReadOnlyDictionary<string, object?>? Props = null)
{
	public string LevelName => ((LogLevel)Level).ToString();
	public string EffectiveServiceKey => ServiceKey ?? "";

	// A dynamic column so KustoLoco can resolve `Properties.<key>` the same way production does over
	// PropertiesJson (see ToRecord). KustoLoco maps a JsonNode property to a dynamic-typed column.
	public JsonNode? Properties
	{
		get
		{
			if (Props is null)
				return null;
			var o = new JsonObject();
			foreach (var (k, v) in Props)
				o[k] = v is null ? null : JsonValue.Create(v);
			return o;
		}
	}

	public static TestEvent FromName(
		long id,
		DateTime ts,
		string levelName,
		string message,
		string? serviceKey = null,
		IReadOnlyDictionary<string, object?>? props = null) => new(
			id,
			ts,
			(int)(LogLevelParser.Parse(levelName) ?? throw new ArgumentException($"unknown level '{levelName}'")),
			message,
			serviceKey,
			Props: props);
}

static class DualExecutor
{
	// The differential compares each ACTIVE backend's production result against the ONE KustoLoco
	// reference run (per KqlBackendConfig.Active — Sqlite today; DuckDb once its dialect is live). A
	// per-call `exclude` drops a backend that genuinely can't serve the query (none needed while the
	// suite is Sqlite-only); the harness runs the production side over real SQL, not EnumerableQuery.
	public static async Task AssertSameAsync(string kql, IReadOnlyList<TestEvent> dataset, bool ordered = false,
		params KqlBackend[] exclude)
	{
		var refIds = await RunReferenceAsync(kql, dataset);
		foreach (var backend in KqlBackendConfig.Active.Except(exclude))
		{
			var prodIds = RunProduction(kql, dataset, backend);
			if (ordered)
			{
				prodIds.Should().ContainInOrder(refIds,
					$"production ({backend}) and reference must agree on order for {kql}");
				prodIds.Count.Should().Be(refIds.Count);
			}
			else
			{
				prodIds.Should().BeEquivalentTo(refIds,
					$"production ({backend}) and reference executors must agree on {kql}");
			}
		}
	}

	// Table-shaped differential: compares the FULL projected result (column names + every cell
	// value), not just Ids. Needed for computed columns (extend / project) where the interesting
	// output is the computed value. End such queries with a `project` so both engines expose the
	// same named columns (KustoLoco's event shape differs from ours). Row order is not asserted.
	public static async Task AssertSameTableAsync(string kql, IReadOnlyList<TestEvent> dataset, bool ordered = false,
		params KqlBackend[] exclude)
	{
		var (refCols, refRows) = await RunReferenceTableAsync(kql, dataset);
		foreach (var backend in KqlBackendConfig.Active.Except(exclude))
		{
			var (prodCols, prodRows) = await RunProductionTableAsync(kql, dataset, backend);

			prodCols.Should().BeEquivalentTo(refCols,
				$"production ({backend}) and reference must produce the same columns for {kql}");
			if (ordered)
				prodRows.Should().BeEquivalentTo(refRows, o => o.WithStrictOrdering(),
					$"production ({backend}) and reference must produce the same rows in order for {kql}");
			else
				prodRows.Should().BeEquivalentTo(refRows,
					$"production ({backend}) and reference must produce the same rows for {kql}");
		}
	}

	static async Task<(IReadOnlyList<string> Columns, IReadOnlyList<Dictionary<string, object?>> Rows)>
		RunReferenceTableAsync(string kql, IReadOnlyList<TestEvent> dataset)
	{
		var ctx = new KustoQueryContext();
		ctx.CopyDataIntoTable("events", dataset);

		var result = await ctx.RunQuery(kql);
		result.Error.Should().BeNullOrEmpty("reference executor error: " + result.Error);

		var cols = result.ColumnNames().ToList();
		var rows = result.EnumerateRows()
			.Select(r => cols.Select((c, i) => (c, Norm(r[i]))).ToDictionary(t => t.c, t => t.Item2))
			.ToList();
		return (cols, rows);
	}

	static async Task<(IReadOnlyList<string> Columns, IReadOnlyList<Dictionary<string, object?>> Rows)>
		RunProductionTableAsync(string kql, IReadOnlyList<TestEvent> dataset, KqlBackend backend)
	{
		var records = dataset.Select(ToRecord).ToList();
		var code = KustoCode.Parse(kql);
		var (resultCols, resultRows) = await KqlTestHost.ExecuteAsync(records, code, backend);

		var cols = resultCols.Select(c => c.Name).ToList();
		var rows = resultRows
			.Select(r => cols.Select((c, i) => (c, Norm(r[i]))).ToDictionary(t => t.c, t => t.Item2))
			.ToList();
		return (cols, rows);
	}

	// Normalize so trivial CLR-type differences between the two engines don't cause spurious
	// mismatches: all integers → long, all reals → rounded double, datetimes → UTC wall-clock.
	// Both engines carry UTC data, but KustoLoco returns computed datetimes (e.g. bin()) with
	// Kind=Unspecified; ToUniversalTime() would then wrongly assume local time and shift them,
	// so we reinterpret the wall-clock as UTC instead of converting.
	static object? Norm(object? v) => v switch
	{
		null => null,
		bool b => b,
		string s => s,
		DateTime dt => DateTime.SpecifyKind(dt, DateTimeKind.Utc),
		sbyte or byte or short or ushort or int or uint or long or ulong => Convert.ToInt64(v),
		float or double or decimal => Math.Round(Convert.ToDouble(v), 9),
		_ => v.ToString(),
	};

	static async Task<IReadOnlyList<long>> RunReferenceAsync(string kql, IReadOnlyList<TestEvent> dataset)
	{
		var ctx = new KustoQueryContext();
		ctx.CopyDataIntoTable("events", dataset);

		var result = await ctx.RunQuery(kql);
		result.Error.Should().BeNullOrEmpty("reference executor error: " + result.Error);

		var idCol = Array.IndexOf(result.ColumnNames(), nameof(TestEvent.Id));
		return result.EnumerateRows().Select(r => Convert.ToInt64(r[idCol])).ToList();
	}

	static IReadOnlyList<long> RunProduction(string kql, IReadOnlyList<TestEvent> dataset, KqlBackend backend)
	{
		var records = dataset.Select(ToRecord).ToList();
		var code = KustoCode.Parse(kql);
		var result = KqlTestHost.Apply(records, code, backend);
		return result.Select(r => r.Id).ToList();
	}

	static LogEntryRecord ToRecord(TestEvent t) => new()
	{
		Id = t.Id,
		TimestampMs = new DateTimeOffset(t.Timestamp, TimeSpan.Zero).ToUnixTimeMilliseconds(),
		Level = t.Level,
		Message = t.Message,
		MessageTemplate = t.MessageTemplate ?? t.Message,
		Exception = t.Exception,
		ServiceKey = t.EffectiveServiceKey,
		PropertiesJson = t.Props is null ? "{}" : PropertiesJsonSerializer.Serialize(t.Props),
	};
}
