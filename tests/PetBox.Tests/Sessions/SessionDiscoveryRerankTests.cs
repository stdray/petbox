using PetBox.Core.Search;
using PetBox.Memory.Contract;
using PetBox.Web.Search;

namespace PetBox.Tests.Sessions;

// W5 discovery re-ranking for session_search (spec search-fair-fusion): the raw digest pool the
// store hybrid returns is reshaped by SessionSearchService.RankDiscovery — a semantic-noise FLOOR
// (drop semantic-ONLY hits below the floor; lexically-confirmed hits always survive), then freshness
// DECAY and MMR diversity via the SHARED Reranking primitives. Without an embedder every hit is
// lexically confirmed and carries no vector, so the floor no-ops and MMR is identity — quiet
// degradation. The policy is a pure function of (hits, rerank, floor), unit-tested directly.
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

	static SessionSearchOptions Floor(double f) => new() { SemanticFloor = f };

	static List<string> Keys(IReadOnlyList<MemoryScoredHit> hits) => hits.Select(h => h.Entry.Key).ToList();

	// ---- semantic floor ----

	[Fact]
	public void Floor_SemanticOnlyLowScore_IsCut_LexicalLowScorePasses()
	{
		// Three hits below the floor: a semantic-ONLY one (unconfirmed) is noise → cut; the two
		// LEXICALLY-CONFIRMED ones are vouched for by the lexical leg → kept regardless of score.
		var hits = new[]
		{
			Hit("sem-noise", score: 0.010, lexicalConfirmed: false),
			Hit("lex-weak", score: 0.010, lexicalConfirmed: true),
			Hit("lex-strong", score: 0.030, lexicalConfirmed: true),
		};

		var ranked = SessionSearchService.RankDiscovery(hits, RerankOff, Floor(0.013));

		Keys(ranked).Should().NotContain("sem-noise", "a semantic-only hit under the floor is noise");
		Keys(ranked).Should().Contain("lex-weak", "a lexically-confirmed hit is never floored");
		Keys(ranked).Should().Contain("lex-strong");
	}

	[Fact]
	public void Floor_SemanticOnlyAboveFloor_Survives()
	{
		// A semantic-only hit that clears the floor is a genuine paraphrase match, not noise — kept.
		var hits = new[]
		{
			Hit("sem-good", score: 0.016, lexicalConfirmed: false),
			Hit("sem-bad", score: 0.011, lexicalConfirmed: false),
		};

		var ranked = SessionSearchService.RankDiscovery(hits, RerankOff, Floor(0.013));

		Keys(ranked).Should().Contain("sem-good").And.NotContain("sem-bad");
	}

	[Fact]
	public void Floor_Zero_DisablesTheCut()
	{
		// SemanticFloor = 0 keeps every semantic-only hit (the escape hatch).
		var hits = new[] { Hit("sem-any", score: 0.0001, lexicalConfirmed: false) };

		var ranked = SessionSearchService.RankDiscovery(hits, RerankOff, Floor(0));

		Keys(ranked).Should().ContainSingle().Which.Should().Be("sem-any");
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

		var off = Keys(SessionSearchService.RankDiscovery(hits, RerankOff, Floor(0)));
		off.IndexOf("stale-strong").Should().BeLessThan(off.IndexOf("fresh-weak"),
			"without decay the stronger score leads");

		var decay = new SearchRerankOptions
		{
			Recency = new RecencyOptions { Enabled = true, HalfLifeDays = 30 },
			Diversity = new DiversityOptions { Enabled = false },
		};
		var on = Keys(SessionSearchService.RankDiscovery(hits, decay, Floor(0)));
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
		Keys(SessionSearchService.RankDiscovery(hits, mmrOff, Floor(0))).Take(2)
			.Should().BeEquivalentTo(["dup-a", "dup-b"], "by pure score the two near-dups occupy the head");

		var mmrOn = new SearchRerankOptions
		{
			Recency = new RecencyOptions { Enabled = false },
			Diversity = new DiversityOptions { Enabled = true, Lambda = 0.7 },
		};
		var on = Keys(SessionSearchService.RankDiscovery(hits, mmrOn, Floor(0)));
		on.Take(2).Should().Contain("distinct", "MMR promotes the novel hit into the head");
	}

	// ---- degradation without an embedder ----

	[Fact]
	public void NoEmbedder_FloorNoOps_MmrIdentity_OrderedByScore()
	{
		// Without an embedder the store search yields lexically-confirmed hits with NO vectors: the
		// floor cannot cut anything (every hit confirmed) and MMR silently degrades to identity, so
		// the answer is the plain fused order by (decayed) score — nothing is lost.
		var hits = new[]
		{
			Hit("a", score: 0.030, lexicalConfirmed: true, vector: null),
			Hit("b", score: 0.020, lexicalConfirmed: true, vector: null),
			Hit("c", score: 0.010, lexicalConfirmed: true, vector: null),
		};

		// Default policy: decay + MMR enabled, conservative floor. All hits are recent, so decay is
		// a no-op multiplier and the score order survives.
		var ranked = SessionSearchService.RankDiscovery(hits, new SearchRerankOptions(), new SessionSearchOptions());

		Keys(ranked).Should().Equal(["a", "b", "c"], "no hit dropped; MMR identity; ordered by score");
	}
}
