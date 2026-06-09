namespace PetBox.Core.Search;

// Brute-force vector similarity for v1 semantic search: cosine + TopK over candidates
// already filtered to one (model,dim) by the caller. Chosen on merit — simplest and
// correct at the current scale (thousands of rows); an ANN index is a later
// optimization decided on merit when scale demands it.
public static class VectorMath
{
	public static double Cosine(float[] a, float[] b)
	{
		if (a.Length != b.Length || a.Length == 0) return 0;
		double dot = 0, na = 0, nb = 0;
		for (var i = 0; i < a.Length; i++)
		{
			dot += (double)a[i] * b[i];
			na += (double)a[i] * a[i];
			nb += (double)b[i] * b[i];
		}
		if (na == 0 || nb == 0) return 0;
		return dot / (Math.Sqrt(na) * Math.Sqrt(nb));
	}

	// Top-k by cosine, descending. Candidates whose length differs from the query are
	// skipped (defensive — callers should already model/dim-guard before TopK).
	public static IReadOnlyList<(string Key, double Score)> TopK(
		float[] query, IEnumerable<(string Key, float[] Vec)> candidates, int k)
	{
		var scored = new List<(string Key, double Score)>();
		foreach (var (key, vec) in candidates)
		{
			if (vec.Length != query.Length) continue;
			scored.Add((key, Cosine(query, vec)));
		}
		return scored.OrderByDescending(x => x.Score).Take(k).ToList();
	}
}
