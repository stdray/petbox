namespace PetBox.Core.Search.Eval;

// The lab bench: an eval harness that runs a LABELED corpus of queries through the search
// contract and scores retrieval quality, so digest/fusion strategies are decided EMPIRICALLY
// rather than on intuition (design: memory m-1a5c37fe; spec: session-discovery-digest).
//
// Deliberately contract-only and READ-side: a strategy under test is just a SearchService
// configured with some set of ISearchIndex implementations; the harness only calls SearchAsync
// and scores the result. That is the validation — if evaluation needs nothing but the read
// contract, the contract is the right seam for plugging/comparing strategies (spec:
// search-pluggable-location). Indexing the corpus is the caller's job (it owns the store + the
// Class-A transaction); the harness never touches a DataConnection.
//
// Corpus loaders (LoCoMo / LongMemEval / synthetic-from-transcripts) and the local-Qwen digest
// runs produce EvalQuery lists that feed this engine — those are research executions ON the
// bench, separate from the bench itself.

// One relevance judgment: the entity (type, id) a query SHOULD retrieve.
public readonly record struct EvalJudgment(string Type, string Id);

// A labeled query: the text to search, the scope/filter to search under, and the entities that
// count as correct answers.
public readonly record struct EvalQuery(
	string Scope,
	string Query,
	SearchFilter Filter,
	IReadOnlyList<EvalJudgment> Relevant);

// Per-query outcome: what came back (entity addresses, in rank order), the 1-based rank of the
// first relevant hit (0 = none in top-k), whether it hit at all, and the run's provenance.
public sealed record EvalQueryResult(
	string Query,
	IReadOnlyList<EvalJudgment> Returned,
	int FirstRelevantRank,
	bool Hit,
	SearchRetrievers Retrievers);

// Aggregate score for a strategy over the corpus. HitRate@k = share of queries with ≥1 relevant
// in top-k; Recall@k = mean per-query |relevant∩topK|/|relevant|; MRR@k = mean reciprocal rank
// of the first relevant hit. The provenance counters say how often each retriever actually ran
// and how often a result was degraded — the honest signal behind a quality number.
public sealed record EvalReport(
	int Queries,
	int K,
	double HitRateAtK,
	double RecallAtK,
	double MrrAtK,
	int LexicalQueries,
	int SemanticQueries,
	int DegradedQueries,
	IReadOnlyList<EvalQueryResult> PerQuery);

// Side-by-side of two strategies over the SAME corpus, plus the per-query divergence (queries
// where the returned entity sets differ) — the shadow-diff comparison the contract is meant to
// make cheap. (search-shadow-diff builds on this; here it is just the trivial A/B of two reports.)
public sealed record EvalComparison(EvalReport A, EvalReport B, IReadOnlyList<string> DivergedQueries);

public static class SearchEvalHarness
{
	public static async Task<EvalReport> EvaluateAsync(
		SearchService svc, IReadOnlyList<EvalQuery> queries, int k, CancellationToken ct = default)
	{
		var per = new List<EvalQueryResult>(queries.Count);
		double recallSum = 0, rrSum = 0;
		int hits = 0, lexical = 0, semantic = 0, degraded = 0;

		foreach (var q in queries)
		{
			var res = await svc.SearchAsync(q.Scope, q.Query, q.Filter, k, ct: ct);
			var returned = res.Hits.Select(h => new EvalJudgment(h.Type, h.Id)).ToList();
			var relevant = q.Relevant.ToHashSet();

			var firstRank = 0;
			for (var i = 0; i < returned.Count; i++)
				if (relevant.Contains(returned[i])) { firstRank = i + 1; break; }

			var found = returned.Count(relevant.Contains);
			var hit = firstRank > 0;

			if (hit) hits++;
			if (relevant.Count > 0) recallSum += (double)found / relevant.Count;
			if (firstRank > 0) rrSum += 1.0 / firstRank;
			if (res.Retrievers.Lexical) lexical++;
			if (res.Retrievers.Semantic) semantic++;
			if (res.Retrievers.Degraded) degraded++;

			per.Add(new EvalQueryResult(q.Query, returned, firstRank, hit, res.Retrievers));
		}

		var n = queries.Count;
		return new EvalReport(
			Queries: n,
			K: k,
			HitRateAtK: n == 0 ? 0 : (double)hits / n,
			RecallAtK: n == 0 ? 0 : recallSum / n,
			MrrAtK: n == 0 ? 0 : rrSum / n,
			LexicalQueries: lexical,
			SemanticQueries: semantic,
			DegradedQueries: degraded,
			PerQuery: per);
	}

	// Run two strategies over the same corpus and report which queries returned different entity
	// sets — the bench for "run two impls and diff" (pluggable-location).
	public static async Task<EvalComparison> CompareAsync(
		SearchService a, SearchService b, IReadOnlyList<EvalQuery> queries, int k, CancellationToken ct = default)
	{
		var ra = await EvaluateAsync(a, queries, k, ct);
		var rb = await EvaluateAsync(b, queries, k, ct);

		var diverged = new List<string>();
		for (var i = 0; i < queries.Count; i++)
		{
			var sa = ra.PerQuery[i].Returned.ToHashSet();
			var sb = rb.PerQuery[i].Returned.ToHashSet();
			if (!sa.SetEquals(sb)) diverged.Add(queries[i].Query);
		}
		return new EvalComparison(ra, rb, diverged);
	}
}
