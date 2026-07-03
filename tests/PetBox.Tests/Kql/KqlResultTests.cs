using Kusto.Language;

namespace PetBox.Tests.Kql;

public sealed class KqlResultTests
{
	static KustoCode Parse(string kql) => KustoCode.Parse(kql);

	static readonly LogEntryRecord[] Rows =
	[
		new() { Id = 1, TimestampMs = 100, Level = (int)LogLevel.Information, Message = "hello", ServiceKey = "svc-a" },
		new() { Id = 2, TimestampMs = 200, Level = (int)LogLevel.Error, Message = "boom", ServiceKey = "svc-b" },
	];

	[Fact]
	public void EventRecordColumns_HaveExpectedSchema()
	{
		KqlTransformer.EventRecordColumns.Select(c => c.Name).Should().ContainInOrder(
			"Id", "ServiceKey", "Timestamp", "Level", "LevelName",
			"Message", "MessageTemplate", "Exception", "PropertiesJson");
	}

	[Fact]
	public async Task Execute_Where_ReturnsFilteredRowsAsObjectArray()
	{
		var ast = Parse("events | where Level == 4");
		var result = KqlTransformer.Execute(Rows.AsQueryable(), ast);

		var rows = new List<object?[]>();
		await foreach (var r in result.Rows)
			rows.Add(r);

		rows.Should().HaveCount(1);
		rows[0][0].Should().Be(2L); // Id
		rows[0][5].Should().Be("boom"); // Message
	}

	[Fact]
	public void Execute_Unsupported_ThrowsEagerly()
	{
		var ast = Parse("events | sample 3");
		var act = () => KqlTransformer.Execute(Rows.AsQueryable(), ast);
		act.Should().Throw<UnsupportedKqlException>();
	}

	[Fact]
	public async Task Project_NarrowsColumns()
	{
		var ast = Parse("events | project Id, Message");
		var result = KqlTransformer.Execute(Rows.AsQueryable(), ast);

		result.Columns.Select(c => c.Name).Should().ContainInOrder("Id", "Message");
		result.Columns.Should().HaveCount(2);

		var rows = new List<object?[]>();
		await foreach (var r in result.Rows) rows.Add(r);
		rows.Should().HaveCount(2);
		rows[0].Should().HaveCount(2);
		rows[0][0].Should().Be(1L);
		rows[0][1].Should().Be("hello");
	}

	[Fact]
	public async Task Project_AliasRenamesColumn()
	{
		var ast = Parse("events | project EventId = Id, Text = Message");
		var result = KqlTransformer.Execute(Rows.AsQueryable(), ast);
		result.Columns.Select(c => c.Name).Should().ContainInOrder("EventId", "Text");
	}

	[Fact]
	public void Project_UnknownColumn_Throws()
	{
		var ast = Parse("events | project Bogus");
		var act = () => KqlTransformer.Execute(Rows.AsQueryable(), ast);
		act.Should().Throw<UnsupportedKqlException>().WithMessage("*Bogus*");
	}

	[Fact]
	public async Task Count_ReturnsScalar()
	{
		var ast = Parse("events | count");
		var result = KqlTransformer.Execute(Rows.AsQueryable(), ast);
		result.Columns[0].Name.Should().Be("Count");
		result.Columns[0].ClrType.Should().Be<long>();

		var rows = new List<object?[]>();
		await foreach (var r in result.Rows) rows.Add(r);
		rows.Single()[0].Should().Be(2L);
	}

	[Fact]
	public async Task Where_Then_Count_FiltersFirst()
	{
		var ast = Parse("events | where Level == 4 | count");
		var result = KqlTransformer.Execute(Rows.AsQueryable(), ast);
		var rows = new List<object?[]>();
		await foreach (var r in result.Rows) rows.Add(r);
		rows.Single()[0].Should().Be(1L);
	}

	static readonly LogEntryRecord[] SummarizeRows =
	[
		new() { Id = 1, Level = (int)LogLevel.Error, Message = "a", ServiceKey = "svc-a" },
		new() { Id = 2, Level = (int)LogLevel.Error, Message = "b", ServiceKey = "svc-a" },
		new() { Id = 3, Level = (int)LogLevel.Warning, Message = "c", ServiceKey = "svc-b" },
		new() { Id = 4, Level = (int)LogLevel.Information, Message = "d", ServiceKey = "svc-a" },
		new() { Id = 5, Level = (int)LogLevel.Error, Message = "e", ServiceKey = "svc-b" },
	];

	[Fact]
	public async Task Summarize_CountByLevel_GroupsCorrectly()
	{
		var ast = Parse("events | summarize count() by Level");
		var result = KqlTransformer.Execute(SummarizeRows.AsQueryable(), ast);
		result.Columns.Select(c => c.Name).Should().ContainInOrder("Level", "count_");

		var rows = new List<(int Level, long Count)>();
		await foreach (var r in result.Rows)
			rows.Add(((int)r[0]!, (long)r[1]!));

		rows.Should().BeEquivalentTo([
			((int)LogLevel.Error, 3L),
			((int)LogLevel.Warning, 1L),
			((int)LogLevel.Information, 1L),
		]);
	}

	[Fact]
	public async Task Summarize_CountByMultipleColumns()
	{
		var ast = Parse("events | summarize count() by Level, ServiceKey");
		var result = KqlTransformer.Execute(SummarizeRows.AsQueryable(), ast);
		result.Columns.Select(c => c.Name).Should().ContainInOrder("Level", "ServiceKey", "count_");

		var rows = new List<(int Level, string ServiceKey, long Count)>();
		await foreach (var r in result.Rows)
			rows.Add(((int)r[0]!, (string)r[1]!, (long)r[2]!));

		rows.Should().Contain(((int)LogLevel.Error, "svc-a", 2L));
		rows.Should().Contain(((int)LogLevel.Error, "svc-b", 1L));
	}

	[Fact]
	public async Task Summarize_CountWithoutBy_SingleRow()
	{
		var ast = Parse("events | summarize count()");
		var result = KqlTransformer.Execute(SummarizeRows.AsQueryable(), ast);
		var rows = new List<object?[]>();
		await foreach (var r in result.Rows) rows.Add(r);
		rows.Single()[0].Should().Be(5L);
	}

	[Fact]
	public void Summarize_UnsupportedAggregate_Throws()
	{
		var ast = Parse("events | summarize stdev(Level)");
		var act = () => KqlTransformer.Execute(SummarizeRows.AsQueryable(), ast);
		act.Should().Throw<UnsupportedKqlException>().WithMessage("*stdev*");
	}

	static async Task<List<object?[]>> Materialize(KqlResult result)
	{
		var rows = new List<object?[]>();
		await foreach (var r in result.Rows) rows.Add(r);
		return rows;
	}

	[Fact]
	public async Task Extend_AppendsComputedColumn()
	{
		var ast = Parse("events | extend Doubled = Level * 2");
		var result = KqlTransformer.Execute(Rows.AsQueryable(), ast);

		result.Columns.Select(c => c.Name).Should().Contain("Doubled");
		result.Columns[^1].Should().Be(new KqlColumn("Doubled", typeof(long)));

		var rows = await Materialize(result);
		// Original event columns preserved (Id at 0), Doubled appended last.
		rows[0][0].Should().Be(1L);
		rows[0][^1].Should().Be((long)LogLevel.Information * 2);
		rows[1][^1].Should().Be((long)LogLevel.Error * 2);
	}

	[Fact]
	public async Task Extend_ReplacesExistingColumnInPlace()
	{
		var ast = Parse("events | extend Level = Level + 100");
		var result = KqlTransformer.Execute(Rows.AsQueryable(), ast);

		// Replaced in place: still one Level column, now typed long.
		result.Columns.Count(c => c.Name == "Level").Should().Be(1);
		var levelIdx = result.Columns.ToList().FindIndex(c => c.Name == "Level");
		result.Columns[levelIdx].ClrType.Should().Be<long>();

		var rows = await Materialize(result);
		rows[0][levelIdx].Should().Be((long)LogLevel.Information + 100);
	}

	[Fact]
	public async Task Extend_MultipleColumns()
	{
		var ast = Parse("events | extend A = Level + 1, B = Id * 10");
		var result = KqlTransformer.Execute(Rows.AsQueryable(), ast);

		var rows = await Materialize(result);
		var cols = result.Columns.Select(c => c.Name).ToList();
		var a = cols.IndexOf("A");
		var b = cols.IndexOf("B");
		rows[1][a].Should().Be((long)LogLevel.Error + 1);
		rows[1][b].Should().Be(20L);
	}

	[Fact]
	public void Extend_BareExpressionWithoutAlias_Throws()
	{
		var ast = Parse("events | extend Level + 1");
		var act = () => KqlTransformer.Execute(Rows.AsQueryable(), ast);
		act.Should().Throw<UnsupportedKqlException>().WithMessage("*name = expression*");
	}

	[Fact]
	public async Task Project_ComputedExpression()
	{
		var ast = Parse("events | project Id, Doubled = Level * 2");
		var result = KqlTransformer.Execute(Rows.AsQueryable(), ast);

		result.Columns.Select(c => c.Name).Should().ContainInOrder("Id", "Doubled");
		result.Columns[1].ClrType.Should().Be<long>();

		var rows = await Materialize(result);
		rows[0][0].Should().Be(1L);
		rows[0][1].Should().Be((long)LogLevel.Information * 2);
	}

	[Fact]
	public async Task Project_IffExpression_ProducesStrings()
	{
		var ast = Parse("events | project Id, Sev = iff(Level >= 4, 'high', 'low')");
		var result = KqlTransformer.Execute(Rows.AsQueryable(), ast);

		var rows = await Materialize(result);
		rows[0][1].Should().Be("low");  // Information
		rows[1][1].Should().Be("high"); // Error
	}

	[Fact]
	public void Project_UnAliasedComputed_ThrowsPrecise()
	{
		var ast = Parse("events | project Level * 2");
		var act = () => KqlTransformer.Execute(Rows.AsQueryable(), ast);
		act.Should().Throw<UnsupportedKqlException>().WithMessage("*name = expression*");
	}

	[Fact]
	public void Compute_UnsupportedFunction_ThrowsPrecise()
	{
		var ast = Parse("events | project Id, X = tolower(Message)");
		var act = () => KqlTransformer.Execute(Rows.AsQueryable(), ast);
		act.Should().Throw<UnsupportedKqlException>().WithMessage("*tolower*not supported*");
	}

	// --- Task A: aggregates over the int Level column (production-only; KustoLoco can't cast
	// int->long here, so these are not in the differential suite) ---

	static async Task<Dictionary<string, object?>> SummarizeByService(string kql)
	{
		var result = KqlTransformer.Execute(SummarizeRows.AsQueryable(), Parse(kql));
		var rows = await Materialize(result);
		// column 0 = ServiceKey key, column 1 = the single aggregate
		return rows.ToDictionary(r => (string)r[0]!, r => r[1]);
	}

	[Fact]
	public async Task Summarize_Sum_ByService()
	{
		var by = await SummarizeByService("events | summarize sum(Level) by ServiceKey");
		by["svc-a"].Should().Be((long)LogLevel.Error * 2 + (long)LogLevel.Information); // 4+4+2
		by["svc-b"].Should().Be((long)LogLevel.Warning + (long)LogLevel.Error); // 3+4
	}

	[Fact]
	public async Task Summarize_Min_Max_ByService()
	{
		var min = await SummarizeByService("events | summarize min(Level) by ServiceKey");
		min["svc-a"].Should().Be((int)LogLevel.Information); // 2
		min["svc-b"].Should().Be((int)LogLevel.Warning); // 3

		var max = await SummarizeByService("events | summarize max(Level) by ServiceKey");
		max["svc-a"].Should().Be((int)LogLevel.Error); // 4
		max["svc-b"].Should().Be((int)LogLevel.Error); // 4
	}

	[Fact]
	public async Task Summarize_Avg_ByService()
	{
		var by = await SummarizeByService("events | summarize avg(Level) by ServiceKey");
		by["svc-a"].Should().Be(((double)((int)LogLevel.Error * 2 + (int)LogLevel.Information)) / 3);
		by["svc-b"].Should().Be(((double)((int)LogLevel.Warning + (int)LogLevel.Error)) / 2);
	}

	[Fact]
	public async Task Summarize_Dcount_ByService()
	{
		var by = await SummarizeByService("events | summarize dcount(Level) by ServiceKey");
		by["svc-a"].Should().Be(2L); // {Error, Information}
		by["svc-b"].Should().Be(2L); // {Warning, Error}
	}

	[Fact]
	public async Task Summarize_Countif_ByService()
	{
		var by = await SummarizeByService("events | summarize countif(Level >= 4) by ServiceKey");
		by["svc-a"].Should().Be(2L); // ids 1,2
		by["svc-b"].Should().Be(1L); // id 5
	}

	[Fact]
	public async Task Summarize_AliasesBothAggregateAndKey()
	{
		var ast = Parse("events | summarize Total = count(), High = countif(Level >= 4) by Svc = ServiceKey");
		var result = KqlTransformer.Execute(SummarizeRows.AsQueryable(), ast);
		result.Columns.Select(c => c.Name).Should().ContainInOrder("Svc", "Total", "High");

		var rows = await Materialize(result);
		var by = rows.ToDictionary(r => (string)r[0]!, r => ((long)r[1]!, (long)r[2]!));
		by["svc-a"].Should().Be((3L, 2L));
		by["svc-b"].Should().Be((2L, 1L));
	}

	[Fact]
	public void Summarize_CountifNonBoolean_Throws()
	{
		var ast = Parse("events | summarize countif(Level) by ServiceKey");
		var act = () => KqlTransformer.Execute(SummarizeRows.AsQueryable(), ast);
		act.Should().Throw<UnsupportedKqlException>().WithMessage("*boolean predicate*");
	}

	[Fact]
	public void Summarize_SumNonNumeric_Throws()
	{
		var ast = Parse("events | summarize sum(Message) by ServiceKey");
		var act = () => KqlTransformer.Execute(SummarizeRows.AsQueryable(), ast);
		act.Should().Throw<UnsupportedKqlException>().WithMessage("*sum*numeric*");
	}

	// --- Task A: bin() as time / numeric bucket ---

	static readonly LogEntryRecord[] BinRows =
	[
		MkTs(1, "2026-04-19T10:15:00Z", LogLevel.Information),
		MkTs(2, "2026-04-19T10:45:00Z", LogLevel.Information),
		MkTs(3, "2026-04-19T11:05:00Z", LogLevel.Error),
	];

	static LogEntryRecord MkTs(long id, string iso, LogLevel level) => new()
	{
		Id = id,
		TimestampMs = new DateTimeOffset(DateTime.Parse(iso, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal)).ToUnixTimeMilliseconds(),
		Level = (int)level,
		Message = "m",
		ServiceKey = "svc",
	};

	[Fact]
	public async Task Summarize_ByHourBin_BucketsByHour()
	{
		var ast = Parse("events | summarize Cnt = count() by Hour = bin(Timestamp, 1h)");
		var result = KqlTransformer.Execute(BinRows.AsQueryable(), ast);
		result.Columns.Select(c => c.Name).Should().ContainInOrder("Hour", "Cnt");
		result.Columns[0].ClrType.Should().Be<DateTime>();

		var rows = await Materialize(result);
		var by = rows.ToDictionary(r => (DateTime)r[0]!, r => (long)r[1]!);
		by[new DateTime(2026, 4, 19, 10, 0, 0, DateTimeKind.Utc)].Should().Be(2);
		by[new DateTime(2026, 4, 19, 11, 0, 0, DateTimeKind.Utc)].Should().Be(1);
	}

	[Fact]
	public async Task Extend_BinTimestamp_AsPlainScalar()
	{
		var ast = Parse("events | extend H = bin(Timestamp, 1h) | project Id, H");
		var result = KqlTransformer.Execute(BinRows.AsQueryable(), ast);
		var rows = await Materialize(result);
		rows[0][1].Should().Be(new DateTime(2026, 4, 19, 10, 0, 0, DateTimeKind.Utc));
		rows[2][1].Should().Be(new DateTime(2026, 4, 19, 11, 0, 0, DateTimeKind.Utc));
	}

	[Fact]
	public async Task Extend_BinNumeric_FloorsToMultiple()
	{
		var ast = Parse("events | extend B = bin(Id, 2) | project Id, B");
		var result = KqlTransformer.Execute(SummarizeRows.AsQueryable(), ast);
		result.Columns[^1].ClrType.Should().Be<long>();
		var rows = await Materialize(result);
		// Id 1->0, 2->2, 3->2, 4->4, 5->4
		rows.Select(r => (long)r[1]!).Should().ContainInOrder(0L, 2L, 2L, 4L, 4L);
	}

	[Fact]
	public void Bin_TypeMismatch_Throws()
	{
		var ast = Parse("events | extend B = bin(Message, 1h) | project Id, B");
		var act = () => KqlTransformer.Execute(Rows.AsQueryable(), ast);
		act.Should().Throw<UnsupportedKqlException>().WithMessage("*bin()*");
	}

	// --- Task B: post-shape order by / take / top / distinct ---

	[Fact]
	public async Task PostShape_OrderBy_AfterSummarize()
	{
		var ast = Parse("events | summarize Total = count() by ServiceKey | order by Total desc, ServiceKey asc");
		var result = KqlTransformer.Execute(SummarizeRows.AsQueryable(), ast);
		var rows = await Materialize(result);
		rows.Select(r => (string)r[0]!).Should().ContainInOrder("svc-a", "svc-b"); // 3 then 2
		rows.Select(r => (long)r[1]!).Should().ContainInOrder(3L, 2L);
	}

	[Fact]
	public async Task PostShape_Take_AfterProject()
	{
		var ast = Parse("events | project Id, Lvl = Level | order by Id asc | take 2");
		var result = KqlTransformer.Execute(SummarizeRows.AsQueryable(), ast);
		var rows = await Materialize(result);
		rows.Should().HaveCount(2);
		rows.Select(r => (long)r[0]!).Should().ContainInOrder(1L, 2L);
	}

	[Fact]
	public async Task PostShape_Top_AfterProject_SortsAndLimits()
	{
		var ast = Parse("events | project Id, Lvl = Level | top 3 by Id desc");
		var result = KqlTransformer.Execute(SummarizeRows.AsQueryable(), ast);
		var rows = await Materialize(result);
		rows.Select(r => (long)r[0]!).Should().ContainInOrder(5L, 4L, 3L);
	}

	[Fact]
	public async Task PostShape_Top_OnComputedColumn()
	{
		var ast = Parse("events | extend D = Id * 2 | project Id, D | top 2 by D desc");
		var result = KqlTransformer.Execute(SummarizeRows.AsQueryable(), ast);
		var rows = await Materialize(result);
		rows.Select(r => (long)r[1]!).Should().ContainInOrder(10L, 8L);
	}

	[Fact]
	public async Task Distinct_Columns_DeDups()
	{
		var ast = Parse("events | distinct ServiceKey");
		var result = KqlTransformer.Execute(SummarizeRows.AsQueryable(), ast);
		result.Columns.Select(c => c.Name).Should().ContainInOrder("ServiceKey");
		var rows = await Materialize(result);
		rows.Select(r => (string)r[0]!).Should().BeEquivalentTo(["svc-a", "svc-b"]);
	}

	[Fact]
	public async Task Distinct_Star_DeDupsWholeRow()
	{
		var ast = Parse("events | where ServiceKey == 'svc-a' | distinct *");
		var result = KqlTransformer.Execute(SummarizeRows.AsQueryable(), ast);
		result.Columns.Select(c => c.Name).Should().ContainInOrder(
			"Id", "ServiceKey", "Timestamp", "Level", "LevelName",
			"Message", "MessageTemplate", "Exception", "PropertiesJson");
		var rows = await Materialize(result);
		rows.Should().HaveCount(3); // ids 1,2,4 all distinct
	}

	[Fact]
	public void Distinct_UnknownColumn_Throws()
	{
		var ast = Parse("events | distinct Bogus");
		var act = () => KqlTransformer.Execute(Rows.AsQueryable(), ast);
		act.Should().Throw<UnsupportedKqlException>().WithMessage("*Bogus*");
	}
}
