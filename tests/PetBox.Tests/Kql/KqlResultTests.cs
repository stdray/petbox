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
}
