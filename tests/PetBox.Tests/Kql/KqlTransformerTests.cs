namespace PetBox.Tests.Kql;

public sealed class KqlTransformerTests
{
	static Kusto.Language.KustoCode Parse(string kql) => Kusto.Language.KustoCode.Parse(kql);

	static readonly LogEntryRecord[] Rows =
	[
		new() { Id = 1, TimestampMs = 100, Level = (int)LogLevel.Information, Message = "hello", ServiceKey = "svc-a" },
		new() { Id = 2, TimestampMs = 200, Level = (int)LogLevel.Error, Message = "boom", ServiceKey = "svc-b" },
		new() { Id = 3, TimestampMs = 300, Level = (int)LogLevel.Warning, Message = "meh", ServiceKey = "svc-a" },
		new() { Id = 4, TimestampMs = 400, Level = (int)LogLevel.Error, Message = "crash", ServiceKey = "svc-c" },
	];

	static IQueryable<LogEntryRecord> Src() => Rows.AsQueryable();

	[Fact]
	public void Where_LevelEqualsInt_FiltersByRank()
	{
		var ast = Parse("events | where Level == 4");
		var result = KqlTransformer.Apply(Src(), ast).ToList();
		result.Select(r => r.Id).Should().BeEquivalentTo([2L, 4L]);
	}

	[Fact]
	public void Where_LevelOrdering_Works()
	{
		var ast = Parse("events | where Level >= 3");
		var result = KqlTransformer.Apply(Src(), ast).ToList();
		result.Select(r => r.Id).Should().BeEquivalentTo([2L, 3L, 4L]);
	}

	[Fact]
	public void Where_LevelNameEquals_FiltersByName()
	{
		var ast = Parse("events | where LevelName == 'Error'");
		var result = KqlTransformer.Apply(Src(), ast).ToList();
		result.Select(r => r.Id).Should().BeEquivalentTo([2L, 4L]);
	}

	[Fact]
	public void Where_ServiceKeyEquals_Filters()
	{
		var ast = Parse("events | where ServiceKey == 'svc-a'");
		var result = KqlTransformer.Apply(Src(), ast).ToList();
		result.Select(r => r.Id).Should().BeEquivalentTo([1L, 3L]);
	}

	[Fact]
	public void Where_MessageEquals_Filters()
	{
		var ast = Parse("events | where Message == 'boom'");
		var result = KqlTransformer.Apply(Src(), ast).ToList();
		result.Single().Id.Should().Be(2);
	}

	[Fact]
	public void Take_LimitsRows()
	{
		var ast = Parse("events | take 2");
		var result = KqlTransformer.Apply(Src(), ast).ToList();
		result.Should().HaveCount(2);
	}

	[Fact]
	public void Where_Then_Take_Composes()
	{
		var ast = Parse("events | where Level == 4 | take 1");
		var result = KqlTransformer.Apply(Src(), ast).ToList();
		result.Should().HaveCount(1);
		result.Single().Level.Should().Be((int)LogLevel.Error);
	}

	[Fact]
	public void UnknownTable_Throws()
	{
		var ast = Parse("WrongTable | where Level == 4");
		var act = () => KqlTransformer.Apply(Src(), ast).ToList();
		act.Should().Throw<UnsupportedKqlException>().WithMessage("*WrongTable*");
	}

	[Fact]
	public void UnknownOperator_Throws()
	{
		var ast = Parse("events | summarize count()");
		var act = () => KqlTransformer.Apply(Src(), ast).ToList();
		act.Should().Throw<UnsupportedKqlException>().WithMessage("*not supported*");
	}

	[Fact]
	public void UnknownColumn_Throws()
	{
		var ast = Parse("events | where Nonexistent == 'x'");
		var act = () => KqlTransformer.Apply(Src(), ast).ToList();
		act.Should().Throw<UnsupportedKqlException>().WithMessage("*Nonexistent*");
	}

	[Fact]
	public void LevelWithStringLiteral_Throws()
	{
		var ast = Parse("events | where Level == 'Error'");
		var act = () => KqlTransformer.Apply(Src(), ast).ToList();
		act.Should().Throw<UnsupportedKqlException>().WithMessage("*integer literal*");
	}

	[Fact]
	public void OrderBy_DefaultDescending()
	{
		var ast = Parse("events | order by Id");
		var result = KqlTransformer.Apply(Src(), ast).ToList();
		result.Select(r => r.Id).Should().ContainInOrder(4L, 3L, 2L, 1L);
	}

	[Fact]
	public void OrderBy_AscendingExplicit()
	{
		var ast = Parse("events | order by Id asc");
		var result = KqlTransformer.Apply(Src(), ast).ToList();
		result.Select(r => r.Id).Should().ContainInOrder(1L, 2L, 3L, 4L);
	}

	[Fact]
	public void OrderBy_MultipleColumns_ThenBy()
	{
		var ast = Parse("events | order by Level asc, Id desc");
		var result = KqlTransformer.Apply(Src(), ast).ToList();
		result.Select(r => r.Id).Should().ContainInOrder(1L, 3L, 4L, 2L);
	}

	[Fact]
	public void Top_OrdersDescendingAndLimits()
	{
		var ast = Parse("events | top 2 by Id desc");
		var result = KqlTransformer.Apply(Src(), ast).ToList();
		result.Select(r => r.Id).Should().ContainInOrder(4L, 3L);
	}

	[Fact]
	public void Top_Ascending()
	{
		var ast = Parse("events | top 2 by Id asc");
		var result = KqlTransformer.Apply(Src(), ast).ToList();
		result.Select(r => r.Id).Should().ContainInOrder(1L, 2L);
	}

	[Fact]
	public void Top_DefaultDescending()
	{
		var ast = Parse("events | top 1 by Id");
		var result = KqlTransformer.Apply(Src(), ast).ToList();
		result.Single().Id.Should().Be(4);
	}

	[Fact]
	public void WhereThenTop_Composes()
	{
		var ast = Parse("events | where Level == 4 | top 1 by Id asc");
		var result = KqlTransformer.Apply(Src(), ast).ToList();
		result.Single().Id.Should().Be(2);
	}

	[Fact]
	public void ParseError_Throws()
	{
		var ast = Parse("events | where Level ==");
		var act = () => KqlTransformer.Apply(Src(), ast).ToList();
		act.Should().Throw<UnsupportedKqlException>().WithMessage("*parse error*");
	}

	[Fact]
	public void Where_In_FiltersBySet()
	{
		var ast = Parse("events | where Level in (3, 4)");
		var result = KqlTransformer.Apply(Src(), ast).ToList();
		result.Select(r => r.Id).Should().BeEquivalentTo([2L, 3L, 4L]);
	}

	[Fact]
	public void Where_NotIn_FiltersBySet()
	{
		var ast = Parse("events | where Level !in (4)");
		var result = KqlTransformer.Apply(Src(), ast).ToList();
		result.Select(r => r.Id).Should().BeEquivalentTo([1L, 3L]);
	}

	[Fact]
	public void Where_Between_FiltersRange()
	{
		var ast = Parse("events | where Level between (3 .. 4)");
		var result = KqlTransformer.Apply(Src(), ast).ToList();
		result.Select(r => r.Id).Should().BeEquivalentTo([2L, 3L, 4L]);
	}

	[Fact]
	public void Where_Arithmetic_Filters()
	{
		var ast = Parse("events | where Level + 1 == 5");
		var result = KqlTransformer.Apply(Src(), ast).ToList();
		result.Select(r => r.Id).Should().BeEquivalentTo([2L, 4L]);
	}

	[Fact]
	public void Where_Modulo_Filters()
	{
		var ast = Parse("events | where Id % 2 == 0");
		var result = KqlTransformer.Apply(Src(), ast).ToList();
		result.Select(r => r.Id).Should().BeEquivalentTo([2L, 4L]);
	}

	[Fact]
	public void Where_Iff_Filters()
	{
		var ast = Parse("events | where iff(Level == 4, 1, 0) == 1");
		var result = KqlTransformer.Apply(Src(), ast).ToList();
		result.Select(r => r.Id).Should().BeEquivalentTo([2L, 4L]);
	}

	[Fact]
	public void Where_UnsupportedFunction_ThrowsPrecise()
	{
		var ast = Parse("events | where strlen(Message) == 5");
		var act = () => KqlTransformer.Apply(Src(), ast).ToList();
		act.Should().Throw<UnsupportedKqlException>().WithMessage("*strlen*not supported*");
	}
}
