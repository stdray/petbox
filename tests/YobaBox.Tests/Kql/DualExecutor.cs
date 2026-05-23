using Kusto.Language;
using KustoLoco.Core;

namespace YobaBox.Tests.Kql;

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
