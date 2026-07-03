using PetBox.Core.Search;

namespace PetBox.Tests.SearchCore;

// Unit cover for the reusable relevance re-ranking primitives (spec memoverhaul): the fused
// RRF SCORE that makes cross-container merge possible, exponential freshness decay, and MMR
// diversification (incl. the "no embedder → silent identity" contract).
public sealed class RerankingTests
{
	// ---- HybridMerge.RrfScored: rank-based, cross-fusion comparable ----

	[Fact]
	public void RrfScored_TopHitScore_IsRankZeroReciprocal_AndComparableAcrossFusions()
	{
		// The #1 of ANY ranking scores 1/(60+0), independent of which list produced it — the
		// property that lets a caller merge separate per-store pools by score.
		var a = HybridMerge.RrfScored(new[] { "a", "b" });
		var b = HybridMerge.RrfScored(new[] { "x", "y" });
		a[0].Key.Should().Be("a");
		a[0].Score.Should().BeApproximately(1.0 / 60, 1e-9);
		b[0].Score.Should().BeApproximately(a[0].Score, 1e-12); // top-of-pool scores match across fusions
	}

	[Fact]
	public void RrfScored_KeyInBothLists_OutscoresSingleListLeader()
	{
		var fused = HybridMerge.RrfScored(new[] { "a", "b", "c" }, new[] { "x", "b", "y" });
		fused[0].Key.Should().Be("b");
		fused[0].Score.Should().BeGreaterThan(fused[1].Score);
	}

	// ---- RecencyDecay ----

	[Fact]
	public void Decay_AtHalfLife_IsOneHalf_AndFreshIsOne()
	{
		var now = new DateTime(2026, 07, 03, 0, 0, 0, DateTimeKind.Utc);
		RecencyDecay.Weight(now, now, 30).Should().BeApproximately(1.0, 1e-12);          // age 0
		RecencyDecay.Weight(now.AddDays(-30), now, 30).Should().BeApproximately(0.5, 1e-9); // age == half-life
		RecencyDecay.Weight(now.AddDays(-60), now, 30).Should().BeApproximately(0.25, 1e-9);
	}

	[Fact]
	public void Decay_FutureTimestampOrNonPositiveHalfLife_IsOne()
	{
		var now = new DateTime(2026, 07, 03, 0, 0, 0, DateTimeKind.Utc);
		RecencyDecay.Weight(now.AddDays(5), now, 30).Should().Be(1);  // future → no penalty
		RecencyDecay.Weight(now.AddDays(-100), now, 0).Should().Be(1); // decay disabled by half-life
	}

	[Fact]
	public void Decay_AppliedToEqualScores_RanksFresherFirst()
	{
		// The spec promise "at equal relevance, fresher ranks higher": equal fused score, the
		// newer Updated wins after the multiplicative decay blend.
		var now = DateTime.UtcNow;
		var items = new[]
		{
			(Key: "old", Score: 0.5, Updated: now.AddDays(-90)),
			(Key: "new", Score: 0.5, Updated: now.AddDays(-1)),
		};
		var ranked = items
			.Select(i => (i.Key, Blended: i.Score * RecencyDecay.Weight(i.Updated, now, 30)))
			.OrderByDescending(x => x.Blended)
			.Select(x => x.Key)
			.ToList();
		ranked.Should().Equal("new", "old");
	}

	// ---- MMR ----

	[Fact]
	public void Mmr_CollapsesNearDuplicates_PushingThemPastTheHead()
	{
		// a1 and a2 share (near-)identical vectors — near-dups; b and c are distinct. Scores are
		// close (a2 barely above b) and a floor item (c) keeps b's normalized relevance high, so
		// the diversity penalty on a2 (a near-dup of the already-picked a1) tips b into second —
		// a top-2 cut is then not two copies of the same fact.
		var unit = new float[] { 1f, 0f };
		var almost = new float[] { 0.999f, 0.045f };
		var other = new float[] { 0f, 1f };
		var items = new[]
		{
			(Key: "a1", Score: 1.00, Vec: (float[]?)unit),
			(Key: "a2", Score: 0.95, Vec: (float[]?)almost), // near-dup of a1
			(Key: "b",  Score: 0.90, Vec: (float[]?)other),  // distinct, nearly as relevant
			(Key: "c",  Score: 0.50, Vec: (float[]?)other),  // relevance floor
		};
		var ordered = Mmr.Reorder(items, x => x.Score, x => x.Vec, lambda: 0.7).Select(x => x.Key).ToList();
		ordered[0].Should().Be("a1");
		ordered[1].Should().Be("b");   // novelty beats the near-dup
		ordered.IndexOf("b").Should().BeLessThan(ordered.IndexOf("a2"));
	}

	[Fact]
	public void Mmr_WithoutVectors_IsIdentity()
	{
		// The "no embedder → silent skip" contract: every vector null → the fused order is kept
		// verbatim (no diversification, no error).
		var items = new[]
		{
			(Key: "a", Score: 1.0, Vec: (float[]?)null),
			(Key: "b", Score: 0.9, Vec: (float[]?)null),
			(Key: "c", Score: 0.8, Vec: (float[]?)null),
		};
		Mmr.Reorder(items, x => x.Score, x => x.Vec, lambda: 0.7)
			.Select(x => x.Key).Should().Equal("a", "b", "c");
	}

	[Fact]
	public void Mmr_HeadIsAlwaysTheMostRelevant()
	{
		// The first MMR pick has an empty selected set, so it is pure relevance — the strongest
		// hit always leads (guards "best result wins" against diversification).
		var items = new[]
		{
			(Key: "weak",   Score: 0.10, Vec: (float[]?)new float[] { 1f, 0f }),
			(Key: "strong", Score: 0.99, Vec: (float[]?)new float[] { 0f, 1f }),
			(Key: "mid",    Score: 0.50, Vec: (float[]?)new float[] { 1f, 1f }),
		};
		Mmr.Reorder(items, x => x.Score, x => x.Vec, lambda: 0.7).First().Key.Should().Be("strong");
	}
}
