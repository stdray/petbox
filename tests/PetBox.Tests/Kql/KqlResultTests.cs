using Kusto.Language;

namespace PetBox.Tests.Kql;

// Production-only KQL result-shape tests (no KustoLoco differential — these cover int-Level aggregates
// and shapes KustoLoco can't model). Converted to run production over the SHARED real-SQLite harness
// (KqlTestHost) instead of the EnumerableQuery provider, so the assertions pin the REAL SQL-translated
// behavior. See RE-PIN notes inline where the SQLite result legitimately differs from the old in-memory
// artifact.
public sealed class KqlResultTests
{
	static KustoCode Parse(string kql) => KustoCode.Parse(kql);

	// The one production run seam: seed `data` into a fresh in-memory LogDb and Execute `ast` over the
	// real linq2db IQueryable, fully materialized. Sqlite is the only Active backend today.
	static Task<(IReadOnlyList<KqlColumn> Columns, List<object?[]> Rows)> Run(LogEntryRecord[] data, KustoCode ast) =>
		KqlTestHost.ExecuteAsync(data, ast, KqlBackend.Sqlite);

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
		var (_, rows) = await Run(Rows, Parse("events | where Level == 4"));

		rows.Should().HaveCount(1);
		rows[0][0].Should().Be(2L); // Id
		rows[0][5].Should().Be("boom"); // Message
	}

	[Fact]
	public async Task Execute_Unsupported_ThrowsEagerly()
	{
		var act = async () => await Run(Rows, Parse("events | sample 3"));
		await act.Should().ThrowAsync<UnsupportedKqlException>();
	}

	[Fact]
	public async Task Project_NarrowsColumns()
	{
		var (cols, rows) = await Run(Rows, Parse("events | project Id, Message"));

		cols.Select(c => c.Name).Should().ContainInOrder("Id", "Message");
		cols.Should().HaveCount(2);

		rows.Should().HaveCount(2);
		rows[0].Should().HaveCount(2);
		rows[0][0].Should().Be(1L);
		rows[0][1].Should().Be("hello");
	}

	[Fact]
	public async Task Project_AliasRenamesColumn()
	{
		var (cols, _) = await Run(Rows, Parse("events | project EventId = Id, Text = Message"));
		cols.Select(c => c.Name).Should().ContainInOrder("EventId", "Text");
	}

	[Fact]
	public async Task Project_UnknownColumn_FallsBackToProperty()
	{
		// A projected name that is not a column reads Properties.<name> (string). Absent here → null.
		var (cols, rows) = await Run(Rows, Parse("events | project Bogus"));
		cols.Select(c => c.Name).Should().ContainInOrder("Bogus");
		cols[0].ClrType.Should().Be<string>();
		rows.Should().OnlyContain(r => r[0] == null);
	}

	[Fact]
	public async Task Count_ReturnsScalar()
	{
		var (cols, rows) = await Run(Rows, Parse("events | count"));
		cols[0].Name.Should().Be("Count");
		cols[0].ClrType.Should().Be<long>();

		rows.Single()[0].Should().Be(2L);
	}

	[Fact]
	public async Task Where_Then_Count_FiltersFirst()
	{
		var (_, rows) = await Run(Rows, Parse("events | where Level == 4 | count"));
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
		var (cols, rowData) = await Run(SummarizeRows, Parse("events | summarize count() by Level"));
		cols.Select(c => c.Name).Should().ContainInOrder("Level", "count_");

		var rows = rowData.Select(r => ((int)r[0]!, (long)r[1]!)).ToList();
		rows.Should().BeEquivalentTo([
			((int)LogLevel.Error, 3L),
			((int)LogLevel.Warning, 1L),
			((int)LogLevel.Information, 1L),
		]);
	}

	[Fact]
	public async Task Summarize_CountByMultipleColumns()
	{
		var (cols, rowData) = await Run(SummarizeRows, Parse("events | summarize count() by Level, ServiceKey"));
		cols.Select(c => c.Name).Should().ContainInOrder("Level", "ServiceKey", "count_");

		var rows = rowData.Select(r => ((int)r[0]!, (string)r[1]!, (long)r[2]!)).ToList();
		rows.Should().Contain(((int)LogLevel.Error, "svc-a", 2L));
		rows.Should().Contain(((int)LogLevel.Error, "svc-b", 1L));
	}

	[Fact]
	public async Task Summarize_CountWithoutBy_SingleRow()
	{
		var (_, rows) = await Run(SummarizeRows, Parse("events | summarize count()"));
		rows.Single()[0].Should().Be(5L);
	}

	[Fact]
	public async Task Summarize_UnsupportedAggregate_Throws()
	{
		var act = async () => await Run(SummarizeRows, Parse("events | summarize stdev(Level)"));
		await act.Should().ThrowAsync<UnsupportedKqlException>().WithMessage("*stdev*");
	}

	[Fact]
	public async Task Extend_AppendsComputedColumn()
	{
		var (cols, rows) = await Run(Rows, Parse("events | extend Doubled = Level * 2"));

		cols.Select(c => c.Name).Should().Contain("Doubled");
		cols[^1].Should().Be(new KqlColumn("Doubled", typeof(long)));

		// Original event columns preserved (Id at 0), Doubled appended last.
		rows[0][0].Should().Be(1L);
		rows[0][^1].Should().Be((long)LogLevel.Information * 2);
		rows[1][^1].Should().Be((long)LogLevel.Error * 2);
	}

	[Fact]
	public async Task Extend_ReplacesExistingColumnInPlace()
	{
		var (cols, rows) = await Run(Rows, Parse("events | extend Level = Level + 100"));

		// Replaced in place: still one Level column, now typed long.
		cols.Count(c => c.Name == "Level").Should().Be(1);
		var levelIdx = cols.ToList().FindIndex(c => c.Name == "Level");
		cols[levelIdx].ClrType.Should().Be<long>();

		rows[0][levelIdx].Should().Be((long)LogLevel.Information + 100);
	}

	[Fact]
	public async Task Extend_MultipleColumns()
	{
		var (cols, rows) = await Run(Rows, Parse("events | extend A = Level + 1, B = Id * 10"));

		var names = cols.Select(c => c.Name).ToList();
		var a = names.IndexOf("A");
		var b = names.IndexOf("B");
		rows[1][a].Should().Be((long)LogLevel.Error + 1);
		rows[1][b].Should().Be(20L);
	}

	[Fact]
	public async Task Extend_BareExpressionWithoutAlias_Throws()
	{
		var act = async () => await Run(Rows, Parse("events | extend Level + 1"));
		await act.Should().ThrowAsync<UnsupportedKqlException>().WithMessage("*name = expression*");
	}

	[Fact]
	public async Task Project_ComputedExpression()
	{
		var (cols, rows) = await Run(Rows, Parse("events | project Id, Doubled = Level * 2"));

		cols.Select(c => c.Name).Should().ContainInOrder("Id", "Doubled");
		cols[1].ClrType.Should().Be<long>();

		rows[0][0].Should().Be(1L);
		rows[0][1].Should().Be((long)LogLevel.Information * 2);
	}

	[Fact]
	public async Task Project_IffExpression_ProducesStrings()
	{
		var (_, rows) = await Run(Rows, Parse("events | project Id, Sev = iff(Level >= 4, 'high', 'low')"));

		rows[0][1].Should().Be("low");  // Information
		rows[1][1].Should().Be("high"); // Error
	}

	[Fact]
	public async Task Project_UnAliasedComputed_ThrowsPrecise()
	{
		var act = async () => await Run(Rows, Parse("events | project Level * 2"));
		await act.Should().ThrowAsync<UnsupportedKqlException>().WithMessage("*name = expression*");
	}

	[Fact]
	public async Task Compute_UnsupportedFunction_ThrowsPrecise()
	{
		var act = async () => await Run(Rows, Parse("events | project Id, X = strlen(Message)"));
		await act.Should().ThrowAsync<UnsupportedKqlException>().WithMessage("*strlen*not supported*");
	}

	// --- Task A: aggregates over the int Level column (production-only; KustoLoco can't cast
	// int->long here, so these are not in the differential suite) ---

	static async Task<Dictionary<string, object?>> SummarizeByService(string kql)
	{
		var (_, rows) = await Run(SummarizeRows, Parse(kql));
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

	// string min/max composes in SQL now (SQLite MIN/MAX = BINARY = codepoint order, the portable engine
	// promise). Production-only: KustoLoco does not implement string min/max, so there is no differential.
	// SummarizeRows messages a..e are ASCII, where BINARY == ordinal, so the pick is unambiguous.
	[Fact]
	public async Task Summarize_StringMinMax_ByService_ComposesInSql()
	{
		var (cols, rowData) = await Run(SummarizeRows,
			Parse("events | summarize Lo = min(Message), Hi = max(Message) by ServiceKey"));
		cols.Select(c => c.Name).Should().ContainInOrder("ServiceKey", "Lo", "Hi");
		cols[1].ClrType.Should().Be<string>();

		var by = rowData.ToDictionary(r => (string)r[0]!, r => ((string?)r[1], (string?)r[2]));
		by["svc-a"].Should().Be(("a", "d")); // ids 1,2,4 → messages a,b,d
		by["svc-b"].Should().Be(("c", "e")); // ids 3,5   → messages c,e
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
		var (cols, rowData) = await Run(SummarizeRows,
			Parse("events | summarize Total = count(), High = countif(Level >= 4) by Svc = ServiceKey"));
		cols.Select(c => c.Name).Should().ContainInOrder("Svc", "Total", "High");

		var by = rowData.ToDictionary(r => (string)r[0]!, r => ((long)r[1]!, (long)r[2]!));
		by["svc-a"].Should().Be((3L, 2L));
		by["svc-b"].Should().Be((2L, 1L));
	}

	[Fact]
	public async Task Summarize_CountifNonBoolean_Throws()
	{
		var act = async () => await Run(SummarizeRows, Parse("events | summarize countif(Level) by ServiceKey"));
		await act.Should().ThrowAsync<UnsupportedKqlException>().WithMessage("*boolean predicate*");
	}

	[Fact]
	public async Task Summarize_SumNonNumeric_Throws()
	{
		var act = async () => await Run(SummarizeRows, Parse("events | summarize sum(Message) by ServiceKey"));
		await act.Should().ThrowAsync<UnsupportedKqlException>().WithMessage("*sum*numeric*");
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
		var (cols, rowData) = await Run(BinRows, Parse("events | summarize Cnt = count() by Hour = bin(Timestamp, 1h)"));
		cols.Select(c => c.Name).Should().ContainInOrder("Hour", "Cnt");
		cols[0].ClrType.Should().Be<DateTime>();

		var by = rowData.ToDictionary(r => (DateTime)r[0]!, r => (long)r[1]!);
		by[new DateTime(2026, 4, 19, 10, 0, 0, DateTimeKind.Utc)].Should().Be(2);
		by[new DateTime(2026, 4, 19, 11, 0, 0, DateTimeKind.Utc)].Should().Be(1);
	}

	[Fact]
	public async Task Extend_BinTimestamp_AsPlainScalar()
	{
		var (_, rows) = await Run(BinRows, Parse("events | extend H = bin(Timestamp, 1h) | project Id, H"));
		// RE-PIN (EnumerableQuery ordering artifact): the query has no `order by`, so row order is
		// unspecified over real SQLite (was implicitly input-order under the in-memory provider). Assert
		// the bucketed value BY Id instead of by row position — value semantics unchanged.
		var byId = rows.ToDictionary(r => (long)r[0]!, r => (DateTime)r[1]!);
		byId[1L].Should().Be(new DateTime(2026, 4, 19, 10, 0, 0, DateTimeKind.Utc));
		byId[3L].Should().Be(new DateTime(2026, 4, 19, 11, 0, 0, DateTimeKind.Utc));
	}

	[Fact]
	public async Task Extend_BinNumeric_FloorsToMultiple()
	{
		var (cols, rows) = await Run(SummarizeRows, Parse("events | extend B = bin(Id, 2) | project Id, B"));
		cols[^1].ClrType.Should().Be<long>();
		// RE-PIN (EnumerableQuery ordering artifact): no `order by`, so row order is unspecified over real
		// SQLite. Pin the bin(Id,2) floor value per Id (the assertion's real intent) — not row sequence.
		var byId = rows.ToDictionary(r => (long)r[0]!, r => (long)r[1]!);
		byId.Should().BeEquivalentTo(new Dictionary<long, long>
		{
			[1L] = 0L, [2L] = 2L, [3L] = 2L, [4L] = 4L, [5L] = 4L,
		});
	}

	[Fact]
	public async Task Bin_TypeMismatch_Throws()
	{
		var act = async () => await Run(Rows, Parse("events | extend B = bin(Message, 1h) | project Id, B"));
		await act.Should().ThrowAsync<UnsupportedKqlException>().WithMessage("*bin()*");
	}

	// --- Task B: post-shape order by / take / top / distinct ---

	[Fact]
	public async Task PostShape_OrderBy_AfterSummarize()
	{
		var (_, rows) = await Run(SummarizeRows,
			Parse("events | summarize Total = count() by ServiceKey | order by Total desc, ServiceKey asc"));
		rows.Select(r => (string)r[0]!).Should().ContainInOrder("svc-a", "svc-b"); // 3 then 2
		rows.Select(r => (long)r[1]!).Should().ContainInOrder(3L, 2L);
	}

	[Fact]
	public async Task PostShape_Take_AfterProject()
	{
		var (_, rows) = await Run(SummarizeRows, Parse("events | project Id, Lvl = Level | order by Id asc | take 2"));
		rows.Should().HaveCount(2);
		rows.Select(r => (long)r[0]!).Should().ContainInOrder(1L, 2L);
	}

	[Fact]
	public async Task PostShape_Top_AfterProject_SortsAndLimits()
	{
		var (_, rows) = await Run(SummarizeRows, Parse("events | project Id, Lvl = Level | top 3 by Id desc"));
		rows.Select(r => (long)r[0]!).Should().ContainInOrder(5L, 4L, 3L);
	}

	[Fact]
	public async Task PostShape_Top_OnComputedColumn()
	{
		var (_, rows) = await Run(SummarizeRows, Parse("events | extend D = Id * 2 | project Id, D | top 2 by D desc"));
		rows.Select(r => (long)r[1]!).Should().ContainInOrder(10L, 8L);
	}

	[Fact]
	public async Task Distinct_Columns_DeDups()
	{
		var (cols, rows) = await Run(SummarizeRows, Parse("events | distinct ServiceKey"));
		cols.Select(c => c.Name).Should().ContainInOrder("ServiceKey");
		rows.Select(r => (string)r[0]!).Should().BeEquivalentTo(["svc-a", "svc-b"]);
	}

	[Fact]
	public async Task Distinct_Star_DeDupsWholeRow()
	{
		var (cols, rows) = await Run(SummarizeRows, Parse("events | where ServiceKey == 'svc-a' | distinct *"));
		cols.Select(c => c.Name).Should().ContainInOrder(
			"Id", "ServiceKey", "Timestamp", "Level", "LevelName",
			"Message", "MessageTemplate", "Exception", "PropertiesJson");
		rows.Should().HaveCount(3); // ids 1,2,4 all distinct
	}

	[Fact]
	public async Task Distinct_UnknownColumn_FallsBackToProperty()
	{
		// distinct of a name that is not a column de-dups Properties.<name>. Both rows lack the property,
		// so a single distinct null remains (bare-name fallback).
		var (cols, rows) = await Run(Rows, Parse("events | distinct Bogus"));
		cols[0].Name.Should().Be("Bogus");
		rows.Should().ContainSingle();
		rows[0][0].Should().BeNull();
	}

	// --- post-shape where: a `where` after a shape-changing op filters the computed rows ---

	[Fact]
	public async Task PostShape_Where_FiltersGroupsByAggregate()
	{
		// svc-a has 3 rows, svc-b has 2 → only svc-a passes Total > 2.
		var (cols, rows) = await Run(SummarizeRows,
			Parse("events | summarize Total = count() by ServiceKey | where Total > 2"));
		cols.Select(c => c.Name).Should().ContainInOrder("ServiceKey", "Total");
		rows.Should().ContainSingle();
		rows[0][0].Should().Be("svc-a");
		rows[0][1].Should().Be(3L);
	}

	[Fact]
	public async Task PostShape_Where_OnComputedColumn()
	{
		var (_, rows) = await Run(SummarizeRows, Parse("events | extend D = Id * 2 | where D >= 8 | project Id, D"));
		rows.Select(r => (long)r[0]!).Should().BeEquivalentTo([4L, 5L]); // ids with Id*2 >= 8
	}

	[Fact]
	public async Task PostShape_Where_ComposesWithOrderAndTake()
	{
		var (_, rows) = await Run(SummarizeRows,
			Parse("events | summarize Total = count() by ServiceKey | where Total >= 2 | order by Total desc | take 1"));
		rows.Should().ContainSingle();
		rows[0][0].Should().Be("svc-a"); // highest Total among those >= 2
	}

	[Fact]
	public async Task PostShape_Where_NonBoolean_Throws()
	{
		var act = async () => await Run(SummarizeRows,
			Parse("events | summarize Total = count() by ServiceKey | where Total + 1"));
		await act.Should().ThrowAsync<UnsupportedKqlException>().WithMessage("*boolean*");
	}

	[Fact]
	public async Task PostShape_Where_UnknownColumn_Throws()
	{
		var act = async () => await Run(SummarizeRows,
			Parse("events | summarize Total = count() by ServiceKey | where Bogus > 1"));
		await act.Should().ThrowAsync<UnsupportedKqlException>().WithMessage("*Bogus*");
	}
}
