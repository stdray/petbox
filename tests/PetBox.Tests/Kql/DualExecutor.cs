using Kusto.Language;
using KustoLoco.Core;

namespace PetBox.Tests.Kql;

public sealed record TestEvent(
	long Id,
	DateTime Timestamp,
	int Level,
	string Message,
	string? ServiceKey = null,
	string? MessageTemplate = null,
	string? Exception = null)
{
	public string LevelName => ((LogLevel)Level).ToString();
	public string EffectiveServiceKey => ServiceKey ?? "";

	public static TestEvent FromName(
		long id,
		DateTime ts,
		string levelName,
		string message,
		string? serviceKey = null) => new(
			id,
			ts,
			(int)(LogLevelParser.Parse(levelName) ?? throw new ArgumentException($"unknown level '{levelName}'")),
			message,
			serviceKey);
}

static class DualExecutor
{
	public static async Task AssertSameAsync(string kql, IReadOnlyList<TestEvent> dataset, bool ordered = false)
	{
		var refIds = await RunReferenceAsync(kql, dataset);
		var prodIds = RunProduction(kql, dataset);

		if (ordered)
		{
			prodIds.Should().ContainInOrder(refIds,
				$"production and reference must agree on order for {kql}");
			prodIds.Count.Should().Be(refIds.Count);
		}
		else
		{
			prodIds.Should().BeEquivalentTo(refIds,
				$"production and reference executors must agree on {kql}");
		}
	}

	// Table-shaped differential: compares the FULL projected result (column names + every cell
	// value), not just Ids. Needed for computed columns (extend / project) where the interesting
	// output is the computed value. End such queries with a `project` so both engines expose the
	// same named columns (KustoLoco's event shape differs from ours). Row order is not asserted.
	public static async Task AssertSameTableAsync(string kql, IReadOnlyList<TestEvent> dataset)
	{
		var (refCols, refRows) = await RunReferenceTableAsync(kql, dataset);
		var (prodCols, prodRows) = await RunProductionTableAsync(kql, dataset);

		prodCols.Should().BeEquivalentTo(refCols,
			$"production and reference must produce the same columns for {kql}");
		prodRows.Should().BeEquivalentTo(refRows,
			$"production and reference must produce the same rows for {kql}");
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
		RunProductionTableAsync(string kql, IReadOnlyList<TestEvent> dataset)
	{
		var records = dataset.Select(ToRecord).ToList();
		var code = KustoCode.Parse(kql);
		var result = KqlTransformer.Execute(records.AsQueryable(), code);

		var cols = result.Columns.Select(c => c.Name).ToList();
		var rows = new List<Dictionary<string, object?>>();
		await foreach (var r in result.Rows)
			rows.Add(cols.Select((c, i) => (c, Norm(r[i]))).ToDictionary(t => t.c, t => t.Item2));
		return (cols, rows);
	}

	// Normalize so trivial CLR-type differences between the two engines don't cause spurious
	// mismatches: all integers → long, all reals → rounded double, datetimes → UTC.
	static object? Norm(object? v) => v switch
	{
		null => null,
		bool b => b,
		string s => s,
		DateTime dt => dt.ToUniversalTime(),
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

	static IReadOnlyList<long> RunProduction(string kql, IReadOnlyList<TestEvent> dataset)
	{
		var records = dataset.Select(ToRecord).ToList();
		var code = KustoCode.Parse(kql);
		var result = KqlTransformer.Apply(records.AsQueryable(), code).ToList();
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
		PropertiesJson = "{}",
	};
}
