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
		var ast = Parse("events | distinct Level");
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
		var ast = Parse("events | summarize avg(Level)");
		var act = () => KqlTransformer.Execute(SummarizeRows.AsQueryable(), ast);
		act.Should().Throw<UnsupportedKqlException>().WithMessage("*avg*");
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
}
