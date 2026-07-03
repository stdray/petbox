namespace PetBox.Core.Search;

// Reciprocal Rank Fusion of several ranked key lists (e.g. lexical FTS order +
// semantic cosine order). score(key) = Σ 1/(K + rank0) over each ranking the key
// appears in (rank0 = 0-based position). RRF fuses heterogeneous score scales (FTS
// rank vs cosine) without normalizing them. Ties broken by first appearance (stable).
public static class HybridMerge
{
	const double K = 60;

	public static IReadOnlyList<string> Rrf(params IReadOnlyList<string>?[] rankings) =>
		RrfScored(rankings).Select(x => x.Key).ToList();

	// Like Rrf but keeps the fused RRF SCORE alongside each key. The score is rank-based
	// (Σ 1/(K + rank0)), so it is comparable ACROSS independent fusions — e.g. the #1 hit of
	// any store scores 1/(K+0) regardless of which store produced it. That comparability is
	// what lets a caller globally merge several per-container pools by score (a strong hit in a
	// late container beats a weak one in an early container). Order matches Rrf (score desc,
	// then first appearance).
	public static IReadOnlyList<(string Key, double Score)> RrfScored(params IReadOnlyList<string>?[] rankings)
	{
		var score = new Dictionary<string, double>();
		var firstSeen = new Dictionary<string, int>();
		var seq = 0;
		foreach (var ranking in rankings)
		{
			if (ranking is null) continue;
			for (var rank = 0; rank < ranking.Count; rank++)
			{
				var key = ranking[rank];
				score[key] = score.GetValueOrDefault(key) + 1.0 / (K + rank);
				if (!firstSeen.ContainsKey(key)) firstSeen[key] = seq++;
			}
		}
		return score.Keys
			.OrderByDescending(k => score[k])
			.ThenBy(k => firstSeen[k])
			.Select(k => (k, score[k]))
			.ToList();
	}
}
