using PetBox.Core.Search;
using PetBox.Memory.Contract;
using PetBox.Web.Search;

namespace PetBox.Tests.Sessions;

// Discovery re-ranking for session_search: the raw digest pool the store hybrid returns is
// reshaped by SessionSearchService.RankDiscovery — freshness DECAY and MMR diversity via the SHARED
// Reranking primitives. There is NO semantic floor (spec: search-leg-classification — the tau
// membership threshold is gone): a vector-only digest hit ENTERS as a peer; RankDiscovery only
// reorders, it never gates membership. Without an embedder every hit is lexically confirmed and
// carries no vector, so MMR is identity — quiet degradation. The policy is a pure function of
// (hits, rerank), unit-tested directly.
public sealed class SessionDiscoveryRerankTests
{
	static readonly DateTime Now = DateTime.UtcNow;

	static MemoryScoredHit Hit(string key, double score, bool lexicalConfirmed,
		double ageDays = 0, float[]? vector = null) =>
		new(new MemoryEntryView(key, "Project", key, "", [], 1, ""),
			Now.AddDays(-ageDays), score, lexicalConfirmed, vector);

	static SearchRerankOptions RerankOff => new()
	{
		Recency = new RecencyOptions { Enabled = false },
		Diversity = new DiversityOptions { Enabled = false },
	};

	static List<string> Keys(IReadOnlyList<MemoryScoredHit> hits) => hits.Select(h => h.Entry.Key).ToList();

	// ---- vector-only enters as a peer (no floor) ----

	[Fact]
	public void SemanticOnlyLowScore_Enters_NotFloored()
	{
		// A low-scoring semantic-ONLY (unconfirmed) hit is NOT cut — with the tau threshold gone it
		// selects as a peer alongside the lexically-confirmed hits; RankDiscovery only reorders.
		var hits = new[]
		{
			Hit("sem-low", score: 0.0001, lexicalConfirmed: false),
			Hit("lex-weak", score: 0.010, lexicalConfirmed: true),
			Hit("lex-strong", score: 0.030, lexicalConfirmed: true),
		};

		var ranked = SessionSearchService.RankDiscovery(hits, RerankOff);

		Keys(ranked).Should().Contain("sem-low", "no floor: a vector-only hit enters as a peer");
		Keys(ranked).Should().Contain("lex-weak").And.Contain("lex-strong");
	}

	// ---- freshness decay ----

	[Fact]
	public void Decay_FresherOutranksStaler_AtComparableScore()
	{
		// "stale-strong" matches a touch stronger but is 120 days old (four+ 30-day half-lives);
		// "fresh-weak" is today. With decay OFF the stronger score leads; with decay ON the stale
		// score is quartered away and the fresh hit rises above it.
		var hits = new[]
		{
			Hit("stale-strong", score: 0.030, lexicalConfirmed: true, ageDays: 120),
			Hit("fresh-weak", score: 0.026, lexicalConfirmed: true, ageDays: 0),
		};

		var off = Keys(SessionSearchService.RankDiscovery(hits, RerankOff));
		off.IndexOf("stale-strong").Should().BeLessThan(off.IndexOf("fresh-weak"),
			"without decay the stronger score leads");

		var decay = new SearchRerankOptions
		{
			Recency = new RecencyOptions { Enabled = true, HalfLifeDays = 30 },
			Diversity = new DiversityOptions { Enabled = false },
		};
		var on = Keys(SessionSearchService.RankDiscovery(hits, decay));
		on.IndexOf("fresh-weak").Should().BeLessThan(on.IndexOf("stale-strong"),
			"with decay the fresher hit overtakes the stale-but-stronger one");
	}

	// ---- MMR diversity ----

	[Fact]
	public void Mmr_PushesNearDuplicateOutOfTheHead()
	{
		// dup-a/dup-b share a vector (near-dups); distinct/filler are the orthogonal cluster, filler
		// last by score so distinct is not the relevance floor. Pure score puts the two near-dups at
		// the top-2; MMR must promote the novel "distinct" into the head.
		float[] a = [1f, 0f];
		float[] b = [0f, 1f];
		var hits = new[]
		{
			Hit("dup-a", score: 0.030, lexicalConfirmed: true, vector: a),
			Hit("dup-b", score: 0.028, lexicalConfirmed: true, vector: a),
			Hit("distinct", score: 0.026, lexicalConfirmed: true, vector: b),
			Hit("filler", score: 0.020, lexicalConfirmed: true, vector: b),
		};

		var mmrOff = new SearchRerankOptions
		{
			Recency = new RecencyOptions { Enabled = false },
			Diversity = new DiversityOptions { Enabled = false },
		};
		Keys(SessionSearchService.RankDiscovery(hits, mmrOff)).Take(2)
			.Should().BeEquivalentTo(["dup-a", "dup-b"], "by pure score the two near-dups occupy the head");

		var mmrOn = new SearchRerankOptions
		{
			Recency = new RecencyOptions { Enabled = false },
			Diversity = new DiversityOptions { Enabled = true, Lambda = 0.7 },
		};
		var on = Keys(SessionSearchService.RankDiscovery(hits, mmrOn));
		on.Take(2).Should().Contain("distinct", "MMR promotes the novel hit into the head");
	}

	// ---- degradation without an embedder ----

	[Fact]
	public void NoEmbedder_MmrIdentity_OrderedByScore()
	{
		// Without an embedder the store search yields lexically-confirmed hits with NO vectors: MMR
		// silently degrades to identity, so the answer is the plain fused order by (decayed) score —
		// nothing is lost.
		var hits = new[]
		{
			Hit("a", score: 0.030, lexicalConfirmed: true, vector: null),
			Hit("b", score: 0.020, lexicalConfirmed: true, vector: null),
			Hit("c", score: 0.010, lexicalConfirmed: true, vector: null),
		};

		// Default policy: decay + MMR enabled. All hits are recent, so decay is a no-op multiplier
		// and the score order survives.
		var ranked = SessionSearchService.RankDiscovery(hits, new SearchRerankOptions());

		Keys(ranked).Should().Equal(["a", "b", "c"], "no hit dropped; MMR identity; ordered by score");
	}
}
