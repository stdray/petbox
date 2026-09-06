using PetBox.Core.Contract;

namespace PetBox.Tests.Search;

// Regression coverage for SortKeyComparisons (work/slopo-found-real-dups): the two
// Comparison<string> delegates KeysetCursor.Advance takes as its sortComparison. A4 in
// PagePoolRegressionTests drives Advance with an inline int.Parse delegate, so the
// PRODUCTION delegates were the one paging comparison nothing pinned down.
public sealed class SortKeyComparisonsTests
{
	[Fact]
	public void CompareLong_ComparesNumerically_NotAsText()
	{
		// The trap: lexically "12" < "9", but these are priority/length numbers.
		SortKeyComparisons.CompareLong("12", "9").Should().BePositive();
		SortKeyComparisons.CompareLong("9", "12").Should().BeNegative();
		SortKeyComparisons.CompareLong("7", "7").Should().Be(0);
	}

	[Fact]
	public void CompareInstant_ComparesAsInstants_NotAsText()
	{
		// The trap: lexically "2026-07-02" sorts AFTER "2026-07-10"; as instants it is BEFORE.
		SortKeyComparisons.CompareInstant("2026-07-02T00:00:00.0000000Z", "2026-07-10T00:00:00.0000000Z").Should().BeNegative();
		SortKeyComparisons.CompareInstant("2026-07-10T00:00:00.0000000Z", "2026-07-02T00:00:00.0000000Z").Should().BePositive();
		SortKeyComparisons.CompareInstant("2026-07-10T00:00:00.0000000Z", "2026-07-10T00:00:00.0000000Z").Should().Be(0);
	}

	[Fact]
	public void AdvanceFallback_ResumesAtTokenPosition_UsingCompareInstant()
	{
		// Mirrors PagePoolRegressionTests.A4: a boundary row that MOVED along a timestamp axis must resume
		// at the token's described position, not the row's new one — driven by the PRODUCTION delegate.
		var rows = new[]
		{
			("2026-07-01T00:00:00.0000000Z", "a"),
			("2026-07-06T00:00:00.0000000Z", "c"),
			("2026-07-09T00:00:00.0000000Z", "b"),
		};
		var cursor = new KeysetCursor("fp", "2026-07-05T00:00:00.0000000Z", "b", "board");

		var rest = KeysetCursor.Advance(rows, cursor,
			r => (r.Item1, r.Item2, "board"),
			SortKeyComparisons.CompareInstant, desc: false, "test");

		rest.Select(r => r.Item2).Should().Equal(["c", "b"]);
	}

	[Fact]
	public void AdvanceFallback_ResumesAtTokenPosition_UsingCompareLong()
	{
		var rows = new[] { ("10", "a"), ("30", "c"), ("99", "b") };
		var cursor = new KeysetCursor("fp", "20", "b", "board");

		var rest = KeysetCursor.Advance(rows, cursor,
			r => (r.Item1, r.Item2, "board"),
			SortKeyComparisons.CompareLong, desc: false, "test");

		rest.Select(r => r.Item2).Should().Equal(["c", "b"]);
	}
}
