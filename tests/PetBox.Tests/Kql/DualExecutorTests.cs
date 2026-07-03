namespace PetBox.Tests.Kql;

public sealed class DualExecutorTests
{
	static readonly IReadOnlyList<TestEvent> Dataset =
	[
		TestEvent.FromName(1, new DateTime(2026, 4, 19, 10, 0, 0, DateTimeKind.Utc), "Information", "hello world", "svc-a"),
		TestEvent.FromName(2, new DateTime(2026, 4, 19, 10, 1, 0, DateTimeKind.Utc), "Error", "boom", "svc-b"),
		TestEvent.FromName(3, new DateTime(2026, 4, 19, 10, 2, 0, DateTimeKind.Utc), "Warning", "meh", "svc-a"),
		TestEvent.FromName(4, new DateTime(2026, 4, 19, 10, 3, 0, DateTimeKind.Utc), "Error", "crash on Earth"),
		TestEvent.FromName(5, new DateTime(2026, 4, 19, 10, 4, 0, DateTimeKind.Utc), "Debug", "starting", "svc-c"),
		TestEvent.FromName(6, new DateTime(2026, 4, 19, 10, 5, 0, DateTimeKind.Utc), "Information", "BOOM normalized", "svc-b"),
	];

	[Theory]
	[InlineData("events | where Level == 4")]
	[InlineData("events | where Level != 4")]
	[InlineData("events | where Level >= 3")]
	[InlineData("events | where Level > 3")]
	[InlineData("events | where Level <= 2")]
	[InlineData("events | where Level < 2")]
	[InlineData("events | where LevelName == 'Error'")]
	[InlineData("events | where LevelName != 'Information'")]
	[InlineData("events | where Message == 'boom'")]
	[InlineData("events | where Message != 'boom'")]
	[InlineData("events | where Id == 3")]
	[InlineData("events | where Id != 3")]
	[InlineData("events | where Id > 3")]
	[InlineData("events | where Id >= 3")]
	[InlineData("events | where Id < 3")]
	[InlineData("events | where Id <= 3")]
	[InlineData("events | where ServiceKey == 'svc-a'")]
	public async Task ScalarComparisons_MatchReference(string kql)
	{
		await DualExecutor.AssertSameAsync(kql, Dataset);
	}

	[Theory]
	[InlineData("events | where Message contains 'boom'")]
	[InlineData("events | where Message contains 'BOOM'")]
	[InlineData("events | where Message contains 'earth'")]
	[InlineData("events | where Message contains 'no-such-thing'")]
	public async Task Contains_CaseInsensitive_MatchesReference(string kql)
	{
		await DualExecutor.AssertSameAsync(kql, Dataset);
	}

	[Theory]
	[InlineData("events | where Level >= 4 and ServiceKey == 'svc-b'")]
	[InlineData("events | where Level == 4 or Level == 3")]
	[InlineData("events | where Id > 1 and Id <= 4")]
	[InlineData("events | where not(Level == 4)")]
	[InlineData("events | where LevelName == 'Information' and Message contains 'hello'")]
	public async Task LogicalCombinators_MatchReference(string kql)
	{
		await DualExecutor.AssertSameAsync(kql, Dataset);
	}

	[Fact]
	public async Task EmptyResult_BothSidesEmpty()
	{
		await DualExecutor.AssertSameAsync("events | where Level == 5", Dataset);
	}

	[Theory]
	[InlineData("events | order by Id")]
	[InlineData("events | order by Id asc")]
	[InlineData("events | order by Id desc")]
	[InlineData("events | order by Level asc, Id desc")]
	[InlineData("events | where Level >= 3 | order by Id")]
	public async Task OrderBy_PreservesOrdering(string kql)
	{
		await DualExecutor.AssertSameAsync(kql, Dataset, ordered: true);
	}

	// NOTE: `in` / `between` / unary-minus are exercised over the long `Id` column here.
	// The reference engine (KustoLoco) throws an int→long column-cast error when these
	// specific operators run over the int `Level` column; that is a KustoLoco limitation,
	// not ours — production support for Level in/between is covered in KqlTransformerTests.
	[Theory]
	[InlineData("events | where Id in (2, 3, 4)")]
	[InlineData("events | where Id in (4)")]
	[InlineData("events | where Id !in (2, 3)")]
	[InlineData("events | where Id !in (2, 3, 4)")]
	[InlineData("events | where ServiceKey in ('svc-a', 'svc-c')")]
	[InlineData("events | where ServiceKey !in ('svc-a')")]
	public async Task InOperator_MatchesReference(string kql)
	{
		await DualExecutor.AssertSameAsync(kql, Dataset);
	}

	[Theory]
	[InlineData("events | where Id between (2 .. 4)")]
	[InlineData("events | where Id between (4 .. 4)")]
	[InlineData("events | where Id !between (2 .. 3)")]
	public async Task BetweenOperator_MatchesReference(string kql)
	{
		await DualExecutor.AssertSameAsync(kql, Dataset);
	}

	[Theory]
	[InlineData("events | where Level + 1 == 5")]
	[InlineData("events | where Level * 2 >= 8")]
	[InlineData("events | where Level - 1 < 2")]
	[InlineData("events | where Id % 2 == 0")]
	[InlineData("events | where Id / 2 == 2")]
	[InlineData("events | where -Id < -3")]
	[InlineData("events | where (Level + 1) * 2 == 10")]
	public async Task Arithmetic_InWhere_MatchesReference(string kql)
	{
		await DualExecutor.AssertSameAsync(kql, Dataset);
	}

	[Theory]
	[InlineData("events | where iff(Level == 4, 1, 0) == 1")]
	[InlineData("events | where iff(Level >= 3, Level, 0) >= 3")]
	[InlineData("events | where case(Level == 4, 'hi', Level == 3, 'mid', 'lo') == 'hi'")]
	[InlineData("events | where case(Level >= 4, 2, Level == 3, 1, 0) >= 1")]
	public async Task IffCase_InWhere_MatchesReference(string kql)
	{
		await DualExecutor.AssertSameAsync(kql, Dataset);
	}

	[Theory]
	[InlineData("events | project Id, Doubled = Level * 2")]
	[InlineData("events | project Id, Plus = Level + 10")]
	[InlineData("events | project Id, Mod = Id % 2")]
	[InlineData("events | project Id, Half = Id / 2")]
	[InlineData("events | project Id, Combo = (Level + 1) * 3")]
	public async Task ComputedProject_MatchesReference(string kql)
	{
		await DualExecutor.AssertSameTableAsync(kql, Dataset);
	}

	[Theory]
	[InlineData("events | extend Doubled = Level * 2 | project Id, Doubled")]
	[InlineData("events | extend Plus = Level + 10 | project Id, Plus")]
	[InlineData("events | extend Mod = Id % 2 | project Id, Mod")]
	[InlineData("events | extend Level = Level + 100 | project Id, Level")]
	[InlineData("events | where Level >= 3 | extend D = Level * 2 | project Id, D")]
	public async Task ComputedExtend_MatchesReference(string kql)
	{
		await DualExecutor.AssertSameTableAsync(kql, Dataset);
	}

	[Theory]
	[InlineData("events | project Id, Sev = iff(Level >= 4, 'high', 'low')")]
	[InlineData("events | project Id, Bucket = case(Level >= 4, 'err', Level == 3, 'warn', 'info')")]
	[InlineData("events | extend Flag = iff(Id in (2, 4), 1, 0) | project Id, Flag")]
	[InlineData("events | extend Ok = iff(Id between (2 .. 3), 1, 0) | project Id, Ok")]
	public async Task ComputedConditionals_MatchReference(string kql)
	{
		await DualExecutor.AssertSameTableAsync(kql, Dataset);
	}
}
