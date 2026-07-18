namespace PetBox.Core.Search;

// Reusable RELEVANCE re-ranking primitives shared by every search family (memory now; tasks /
// session next) so the freshness + diversity policy lives in ONE place, above any single index.
// They operate on ALREADY-FUSED candidates (a global RRF-scored pool) — the fusion stays in
// HybridMerge; these only reshape the fused order. Both are OFF for listing mode (no query, no
// relevance to blend) — the caller applies them only in query mode.

// Time-decay knob: bleed freshness into the relevance score so, at comparable relevance, a
// newer fact ranks above a staler one. `HalfLifeDays` is the age at which the freshness weight
// halves. Conservative default (30d) + enabled: it is a gentle multiplicative tilt, never a
// freshness-dominates-relevance override.
public sealed record RecencyOptions
{
	public bool Enabled { get; init; } = true;
	public double HalfLifeDays { get; init; } = 30;
}

// MMR diversity knob: after fusion, greedily pick results that are relevant BUT novel, so the
// answer is not a wall of near-duplicates. `Lambda` trades relevance (1.0) against novelty
// (0.0); 0.7 leans relevance. Enabled by default, but it SILENTLY no-ops without vectors (no
// embedder configured) — provenance/degraded is decided elsewhere, MMR never sets it.
public sealed record DiversityOptions
{
	public bool Enabled { get; init; } = true;
	public double Lambda { get; init; } = 0.7;
}

// NOTE — there is deliberately NO semantic/cosine floor here. A `cosine >= tau` (or fused-RRF)
// membership threshold on vector-only hits is REJECTED by the pipeline contract
// (spec: search-leg-classification): it forges a boolean membership the TopK leg does not have,
// and is the SemanticFloor through the back door. The vector leg selects as a peer under a
// RELEVANCE selection (limit is the ceiling); a scan/field selection excludes it outright and
// says so via `semantic:false`. Recency + MMR reshape the fused order but never gate membership.

// The whole search re-ranking policy, bound from the `Search` config section
// (Search:Recency:*, Search:Diversity:*). Both sub-knobs default enabled with conservative
// parameters, so wiring it changes ranking but not the contract.
public sealed record SearchRerankOptions
{
	public RecencyOptions Recency { get; init; } = new();
	public DiversityOptions Diversity { get; init; } = new();
}

public static class RecencyDecay
{
	// Exponential half-life freshness weight in (0, 1]. weight = 2^(-ageDays / halfLife):
	// age 0 → 1, age == halfLife → 0.5, and it decays smoothly thereafter. A future/equal
	// timestamp or a non-positive half-life yields 1 (no decay). Multiply a relevance score by
	// this so equal-relevance ties resolve toward the fresher item.
	public static double Weight(DateTime updated, DateTime now, double halfLifeDays)
	{
		if (halfLifeDays <= 0) return 1;
		var ageDays = (now - updated).TotalDays;
		if (ageDays <= 0) return 1;
		return Math.Pow(2, -ageDays / halfLifeDays);
	}
}

public static class Mmr
{
	// Greedy Maximal Marginal Relevance re-ordering of a fused candidate pool. Each item carries
	// a relevance score and an OPTIONAL embedding; the result is EVERY item, reordered (the
	// caller then applies its limit — so near-duplicates are pushed past the cut, not dropped).
	//
	//   pick = argmax_{d ∉ S} [ λ·rel(d) − (1−λ)·max_{d'∈S} cos(d, d') ]
	//
	// Relevance is min-max normalized within the pool so it is comparable to cosine ∈ [-1, 1].
	// An item without a vector is treated as similar to nothing (sim 0) — it is never diversified
	// away, only ever ranked by relevance. When NO item has a vector the method is pure identity
	// (this is the "no embedder → silent skip" path; the caller gates on that too).
	public static List<T> Reorder<T>(IReadOnlyList<T> items, Func<T, double> relevance, Func<T, float[]?> vector, double lambda)
	{
		if (items.Count <= 2) return items.ToList();

		var vecs = items.Select(vector).ToArray();
		if (vecs.All(v => v is null)) return items.ToList(); // no vectors → nothing to diversify

		// Min-max normalize relevance into [0, 1] so λ blends against cosine on one scale.
		var rel = items.Select(relevance).ToArray();
		var min = rel.Min();
		var max = rel.Max();
		var span = max - min;
		var norm = rel.Select(r => span > 0 ? (r - min) / span : 1.0).ToArray();

		var n = items.Count;
		var picked = new List<int>(n);
		var remaining = new HashSet<int>(Enumerable.Range(0, n));

		while (remaining.Count > 0)
		{
			var bestIdx = -1;
			var bestScore = double.NegativeInfinity;
			foreach (var i in remaining)
			{
				var maxSim = 0.0;
				if (vecs[i] is { } vi)
					foreach (var j in picked)
						if (vecs[j] is { } vj)
						{
							var sim = VectorMath.Cosine(vi, vj);
							if (sim > maxSim) maxSim = sim;
						}
				var score = lambda * norm[i] - (1 - lambda) * maxSim;
				// Stable: a strict '>' keeps the earlier (already more-relevant) candidate on ties.
				if (score > bestScore)
				{
					bestScore = score;
					bestIdx = i;
				}
			}
			picked.Add(bestIdx);
			remaining.Remove(bestIdx);
		}

		return picked.Select(i => items[i]).ToList();
	}
}
