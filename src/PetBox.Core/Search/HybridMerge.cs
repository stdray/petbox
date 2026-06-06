namespace PetBox.Core.Search;

// Reciprocal Rank Fusion of several ranked key lists (e.g. lexical FTS order +
// semantic cosine order). score(key) = Σ 1/(K + rank0) over each ranking the key
// appears in (rank0 = 0-based position). RRF fuses heterogeneous score scales (FTS
// rank vs cosine) without normalizing them. Ties broken by first appearance (stable).
public static class HybridMerge
{
	const double K = 60;

	public static IReadOnlyList<string> Rrf(params IReadOnlyList<string>?[] rankings)
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
			.ToList();
	}
}
