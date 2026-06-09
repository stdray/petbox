using PetBox.Core.Search;

namespace PetBox.Tests.SearchCore;

public sealed class HybridMergeTests
{
	[Fact]
	public void Rrf_KeyRankedHighInBothLists_WinsOverlySingleListLeader()
	{
		// "b" is rank 1 in both → its summed reciprocal-rank beats "a" (rank 0 in only one).
		var lexical = new[] { "a", "b", "c" };
		var semantic = new[] { "x", "b", "y" };
		var fused = HybridMerge.Rrf(lexical, semantic);
		fused[0].Should().Be("b");
	}

	[Fact]
	public void Rrf_UnionsAllKeys()
	{
		var fused = HybridMerge.Rrf(new[] { "a", "b" }, new[] { "b", "c" });
		fused.Should().BeEquivalentTo(new[] { "a", "b", "c" });
	}

	[Fact]
	public void Rrf_NullRankingsIgnored()
	{
		var fused = HybridMerge.Rrf(new[] { "a", "b" }, null);
		fused.Should().Equal("a", "b");
	}

	[Fact]
	public void Rrf_SingleRanking_PreservesOrder()
	{
		HybridMerge.Rrf(new[] { "a", "b", "c" }).Should().Equal("a", "b", "c");
	}

	[Fact]
	public void Rrf_TiesBrokenByFirstAppearance()
	{
		// Disjoint single-entry lists: both score 1/(60+0); stable by first-seen order.
		var fused = HybridMerge.Rrf(new[] { "first" }, new[] { "second" });
		fused.Should().Equal("first", "second");
	}
}
