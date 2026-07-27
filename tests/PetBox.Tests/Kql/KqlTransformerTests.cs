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

	// The one production run seam: seed Rows into a fresh in-memory LogDb and Apply `ast` over the real
	// linq2db IQueryable. Sqlite is the only Active backend today.
	static List<LogEntryRecord> Apply(Kusto.Language.KustoCode ast) =>
		KqlTestHost.Apply(Rows, ast, KqlBackend.Sqlite);

	// One method per shape, driven by TheoryData<string, long[]> instead of 15 copy-pasted Facts
	// (work/test-suite-improvements-2607 #5 — KQL expected results are id ARRAYS, which InlineData
	// cannot carry; the KQL string itself doubles as the case's readable label in the runner output,
	// same convention as WriteVerbOmissionProseTests' TheoryData<string, string[]>). Where-clause
	// results keep BeEquivalentTo (order-insensitive — a `where` doesn't promise a sequence); a
	// single-row expectation ([2L], []) folds the former Single()/BeEmpty() Facts into the same shape.
	public static TheoryData<string, long[]> FilterCases() => new()
	{
		{ "events | where Level == 4", [2L, 4L] },
		{ "events | where Level >= 3", [2L, 3L, 4L] },
		{ "events | where LevelName == 'Error'", [2L, 4L] },
		{ "events | where ServiceKey == 'svc-a'", [1L, 3L] },
		{ "events | where Message == 'boom'", [2L] },
		// A name that is not a known event column resolves as a Properties.<name> lookup (bare-name
		// fallback). The property is absent here, so the predicate simply matches nothing — rather
		// than raising "column not supported". Known columns still win (the Level/Message cases above).
		{ "events | where Nonexistent == 'x'", [] },
		{ "events | where Level in (3, 4)", [2L, 3L, 4L] },
		{ "events | where Level !in (4)", [1L, 3L] },
		{ "events | where Level between (3 .. 4)", [2L, 3L, 4L] },
		{ "events | where Level + 1 == 5", [2L, 4L] },
		{ "events | where Id % 2 == 0", [2L, 4L] },
		{ "events | where iff(Level == 4, 1, 0) == 1", [2L, 4L] },
	};

	[Theory]
	[MemberData(nameof(FilterCases))]
	public void Where_FiltersToExpectedIds(string kql, long[] expectedIds)
	{
		var result = Apply(Parse(kql));
		result.Select(r => r.Id).Should().BeEquivalentTo(expectedIds);
	}

	// Ordering results, unlike filters, promise a SEQUENCE, so this second theory asserts the
	// exact sequence rather than reusing FilterCases' order-insensitive BeEquivalentTo. Equal,
	// not ContainInOrder: `top N` must also be held to the ROW COUNT — ContainInOrder tolerates
	// extra rows, so `top 1` returning the whole table would slip through it.
	public static TheoryData<string, long[]> OrderingCases() => new()
	{
		{ "events | order by Id", [4L, 3L, 2L, 1L] },
		{ "events | order by Id asc", [1L, 2L, 3L, 4L] },
		{ "events | order by Level asc, Id desc", [1L, 3L, 4L, 2L] },
		{ "events | top 2 by Id desc", [4L, 3L] },
		{ "events | top 2 by Id asc", [1L, 2L] },
		{ "events | top 1 by Id", [4L] },
	};

	[Theory]
	[MemberData(nameof(OrderingCases))]
	public void Where_OrdersToExpectedSequence(string kql, long[] expectedIds)
	{
		var result = Apply(Parse(kql));
		result.Select(r => r.Id).Should().Equal(expectedIds);
	}

	[Fact]
	public void Take_LimitsRows()
	{
		var ast = Parse("events | take 2");
		var result = Apply(ast);
		result.Should().HaveCount(2);
	}

	[Fact]
	public void Where_Then_Take_Composes()
	{
		var ast = Parse("events | where Level == 4 | take 1");
		var result = Apply(ast);
		result.Should().HaveCount(1);
		result.Single().Level.Should().Be((int)LogLevel.Error);
	}

	[Fact]
	public void UnknownTable_Throws()
	{
		var ast = Parse("WrongTable | where Level == 4");
		var act = () => Apply(ast);
		act.Should().Throw<UnsupportedKqlException>().WithMessage("*WrongTable*");
	}

	[Fact]
	public void UnknownOperator_Throws()
	{
		var ast = Parse("events | summarize count()");
		var act = () => Apply(ast);
		act.Should().Throw<UnsupportedKqlException>().WithMessage("*not supported*");
	}

	[Fact]
	public void LevelWithStringLiteral_Throws()
	{
		var ast = Parse("events | where Level == 'Error'");
		var act = () => Apply(ast);
		act.Should().Throw<UnsupportedKqlException>().WithMessage("*integer literal*");
	}

	[Fact]
	public void WhereThenTop_Composes()
	{
		var ast = Parse("events | where Level == 4 | top 1 by Id asc");
		var result = Apply(ast);
		result.Single().Id.Should().Be(2);
	}

	[Fact]
	public void ParseError_Throws()
	{
		var ast = Parse("events | where Level ==");
		var act = () => Apply(ast);
		act.Should().Throw<UnsupportedKqlException>().WithMessage("*parse error*");
	}

	[Fact]
	public void Where_UnsupportedFunction_ThrowsPrecise()
	{
		var ast = Parse("events | where strlen(Message) == 5");
		var act = () => Apply(ast);
		act.Should().Throw<UnsupportedKqlException>().WithMessage("*strlen*not supported*");
	}
}
