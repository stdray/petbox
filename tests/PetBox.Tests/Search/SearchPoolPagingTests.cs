using LinqToDB.Data;
using PetBox.Core.Search;

namespace PetBox.Tests.Search;

// The RANKED POOL and its declared boundary (work/search-results-pageable, spec result-set-pageable).
//
// The feature rests on one fact about the existing pipeline: the material for page 2 was ALREADY being
// computed and then thrown away — the lexical leg is enumerable (it returns everything the facet
// predicate leaves), the RRF fusion covers that whole set, and `.Take(k)` sat at the very end. So these
// tests pin the two claims that make paging over it honest rather than merely possible:
//
//   1. THE POOL IS THE WHOLE ORDER, and the legacy top-k ask is a PREFIX of it. If those two ever
//      disagree, page 1 and the unpaged answer stop matching and the feature is worse than useless.
//   2. THE BOUNDARY IS A FACT ON THE RESPONSE, not an inference. "We ranked everything that matched"
//      and "we ranked the first N of more than N" must be distinguishable — that is the one way this
//      feature can lie to a user, so it is tested directly rather than assumed from a row count.
//
// Plus the cache contract that keeps requirement 5 (one rerank per pool, not per page) true.
public sealed class SearchPoolPagingTests
{
	const string Scope = "proj";

	// ── the pool IS the order, and top-k is its prefix ────────────────────────────────────────

	[Fact]
	public async Task Pool_CarriesTheWholeRankedSet_NotJustTheLegacyTopK()
	{
		// 12 matches, and the old ask (k=3) returned 3. The pool has to hold all 12 in order — that
		// discarded tail is precisely what pages 2..n are made of.
		var svc = new SearchService([new StubIndex(Enumerable.Range(0, 12).Select(i => $"n{i:d2}"))]);

		var pool = await svc.SearchPoolAsync(Scope, "q", new SearchFilter(), legK: 3);
		var topK = await svc.SearchAsync(Scope, "q", new SearchFilter(), k: 3);

		pool.Ordered.Should().HaveCount(12);
		pool.Ordered.Select(h => h.Id).Take(3).Should().Equal(topK.Hits.Select(h => h.Id),
			"the k-row answer must be the pool's PREFIX — otherwise page 1 and the unpaged read disagree");
	}

	[Fact]
	public async Task PagingThroughThePool_CoversItExactlyOnce_NoHolesNoRepeats()
	{
		var svc = new SearchService([new StubIndex(Enumerable.Range(0, 25).Select(i => $"n{i:d2}"))]);
		var pool = await svc.SearchPoolAsync(Scope, "q", new SearchFilter(), legK: 10);

		// Walk it in pages of 4 the way a consumer would — slice, remember the last row, continue.
		var walked = new List<string>();
		for (var at = 0; at < pool.Ordered.Count; at += 4)
			walked.AddRange(pool.Ordered.Skip(at).Take(4).Select(h => h.Id));

		walked.Should().Equal(pool.Ordered.Select(h => h.Id));
		walked.Should().OnlyHaveUniqueItems();
		walked.Should().HaveCount(25);
	}

	[Fact]
	public async Task RepeatedPoolBuild_OverUnchangedData_ReproducesTheSameOrder()
	{
		// Requirement: a second pass over unchanged data yields the SAME order. This is what makes a
		// cold page (an evicted pool, unchanged data version) safe to re-materialize instead of an error.
		var reranker = new CountingReranker();
		var svc = new SearchService([new StubIndex(Enumerable.Range(0, 30).Select(i => $"n{i:d2}"))], reranker: reranker);

		var first = await svc.SearchPoolAsync(Scope, "q", new SearchFilter(), legK: 10, resolveCandidateText: IdAsText);
		var second = await svc.SearchPoolAsync(Scope, "q", new SearchFilter(), legK: 10, resolveCandidateText: IdAsText);

		second.Ordered.Select(h => h.Id).Should().Equal(first.Ordered.Select(h => h.Id));
		first.Retrievers.Ranking.Should().Be(SearchRankingOutcome.Reranked);
	}

	// ── the boundary is VISIBLE and is not exhaustion ─────────────────────────────────────────

	[Fact]
	public async Task PoolWithinTheBudget_ReportsNotBounded_MeaningTrulyExhausted()
	{
		var budget = new RerankCandidateBudget { LatencyBarMs = 1000 };
		var limit = budget.Candidates();
		var svc = new SearchService([new StubIndex(Enumerable.Range(0, limit - 1).Select(i => $"n{i:d3}"))], budget: budget);

		var pool = await svc.SearchPoolAsync(Scope, "q", new SearchFilter(), legK: 10);

		pool.Count.Should().Be(limit - 1);
		pool.PoolLimit.Should().Be(limit);
		pool.PoolBounded.Should().BeFalse("every match was ranked — this really is the end of the selection");
	}

	[Fact]
	public async Task PoolAtTheBudget_ReportsBounded_SoItIsNotMistakenForExhaustion()
	{
		// THE test of this feature's honesty. Same shape of answer as above — a full page and no more
		// rows — but a completely different MEANING, and the response has to carry that difference.
		var budget = new RerankCandidateBudget { LatencyBarMs = 1000 };
		var limit = budget.Candidates();
		var svc = new SearchService([new StubIndex(Enumerable.Range(0, limit + 50).Select(i => $"n{i:d3}"))], budget: budget);

		var pool = await svc.SearchPoolAsync(Scope, "q", new SearchFilter(), legK: 10);

		pool.Count.Should().Be(limit, "ranking never looks deeper than its declared budget");
		pool.PoolLimit.Should().Be(limit);
		pool.PoolBounded.Should().BeTrue(
			"50 more entities matched behind the boundary — reporting this as exhaustion is the lie this field prevents");
	}

	[Fact]
	public async Task TheBoundary_IsTheSameNumber_WhetherRerankedOrDegradedToRrf()
	{
		// A rerank outage must not silently change how DEEP the result goes. If it did, the boundary a
		// caller was told about would depend on a degradation they cannot see, and two callers would be
		// paging pools of different sizes while reading the same PoolLimit.
		var budget = new RerankCandidateBudget { LatencyBarMs = 1000 };
		var docs = Enumerable.Range(0, budget.Candidates() + 10).Select(i => $"n{i:d3}").ToList();

		var withRerank = await new SearchService([new StubIndex(docs)], reranker: new CountingReranker(), budget: budget)
			.SearchPoolAsync(Scope, "q", new SearchFilter(), legK: 10, resolveCandidateText: IdAsText);
		var rrfOnly = await new SearchService([new StubIndex(docs)], budget: budget)
			.SearchPoolAsync(Scope, "q", new SearchFilter(), legK: 10);

		withRerank.Retrievers.Ranking.Should().Be(SearchRankingOutcome.Reranked);
		rrfOnly.Retrievers.Ranking.Should().Be(SearchRankingOutcome.DegradedRrf);
		rrfOnly.PoolLimit.Should().Be(withRerank.PoolLimit);
		rrfOnly.Count.Should().Be(withRerank.Count);
		rrfOnly.PoolBounded.Should().Be(withRerank.PoolBounded);
	}

	// ── one rerank per POOL, not per page ─────────────────────────────────────────────────────

	[Fact]
	public async Task Rerank_IsAskedForTheWholePool_NotForOnePage()
	{
		// The line that makes paging cheap: topN is the pool size, so ONE cross-encoder pass yields the
		// complete order. Asking for a page's worth would force a second pass for page 2 — the 3-4s
		// per page requirement 5 exists to prevent.
		var reranker = new CountingReranker();
		var svc = new SearchService([new StubIndex(Enumerable.Range(0, 40).Select(i => $"n{i:d2}"))], reranker: reranker);

		var pool = await svc.SearchPoolAsync(Scope, "q", new SearchFilter(), legK: 5, resolveCandidateText: IdAsText);

		reranker.Calls.Should().Be(1);
		reranker.LastTopN.Should().Be(40, "the reranker must be asked to order the POOL, not a page of it");
		pool.Ordered.Should().HaveCount(40);
	}

	[Fact]
	public async Task PoolCache_ServesAStoredPool_SoASecondPageNeedsNoRerank()
	{
		using var harness = new PoolCacheHarness();
		var pool = new SearchPool([new Hit("t", "a", 1.0)], PoolLimit: 495, PoolBounded: false, new SearchRetrievers(true, false, false));

		var first = await harness.Cache.GetOrComputeAsync("fp-1", _ => Computed(pool));
		first.FromCache.Should().BeFalse("nothing was stored yet — this call is the one that materializes the pool");

		var second = await harness.Cache.GetOrComputeAsync("fp-1", _ => throw new InvalidOperationException(
			"page 2 must SERVE the stored pool — running the computation again is the 3-4s rerank this cache exists to avoid"));
		second.FromCache.Should().BeTrue();
		second.Pool.Ordered.Select(h => h.Id).Should().Equal("a");

		var other = await harness.Cache.GetOrComputeAsync("fp-2", _ => Computed(pool));
		other.FromCache.Should().BeFalse("a different fingerprint is a different pool, never a shared one");
	}

	[Fact]
	public async Task PoolCache_RoundTripsEveryFactThePoolCarries_NotJustItsOrder()
	{
		// The pool is SERIALIZED now, not handed back by reference, so every field is a chance to
		// lose something in transit. Two of them are load-bearing beyond the row order: PoolBounded
		// is what stops a truncated pool being reported as an exhausted search, and Annotations is
		// what makes a hydrated page 2 reproduce page 1 exactly.
		using var harness = new PoolCacheHarness();
		var pool = new SearchPool(
			[new Hit("board", "n1", 0.75, "lexical"), new Hit("board", "n2", 0.5, "semantic")],
			PoolLimit: 495,
			PoolBounded: true,
			new SearchRetrievers(true, true, false, null, SemanticLag: 7, SearchRankingOutcome.Reranked),
			Annotations: ["comment", null]);

		await harness.Cache.GetOrComputeAsync("fp", _ => Computed(pool));
		var got = (await harness.Cache.GetOrComputeAsync("fp", _ => Computed(pool))).Pool;

		got.Ordered.Should().Equal(pool.Ordered);
		got.PoolLimit.Should().Be(495);
		got.PoolBounded.Should().BeTrue("a truncated pool reported as exhausted is the lie this feature exists to prevent");
		got.Retrievers.Should().Be(pool.Retrievers);
		got.AnnotationAt(0).Should().Be("comment");
		got.AnnotationAt(1).Should().BeNull();
		got.OrderHash.Should().Be(pool.OrderHash, "a cursor minted against the stored pool has to seek in the same list");
	}

	[Fact]
	public async Task PoolCache_HoldsFarMoreThanTheOldCapacity_BecauseTheBoundIsNowTtlNotACount()
	{
		// THE MEANING OF THIS TEST CHANGED WITH THE STORAGE, and deliberately so. It used to assert a
		// 64-entry ceiling with oldest-first eviction, which existed to bound RAM — pools no longer
		// live in RAM. The bound is now TTL plus the cache's sweep, so the honest claim is the
		// opposite one: storing many pools evicts none of them, and in particular the FIRST walk is
		// still resumable after a hundred later ones, which under the old ceiling it would not have
		// been.
		using var harness = new PoolCacheHarness();
		var pool = new SearchPool([new Hit("t", "a", 1.0)], 495, false, new SearchRetrievers(true, false, false));

		for (var i = 0; i < 100; i++)
			await harness.Cache.GetOrComputeAsync($"fp-{i}", _ => Computed(pool));

		(await harness.Cache.GetOrComputeAsync("fp-0", _ => Computed(pool))).FromCache
			.Should().BeTrue("the oldest walk is still in flight as far as anyone knows — nothing evicted it");
		(await harness.Cache.GetOrComputeAsync("fp-99", _ => Computed(pool))).FromCache.Should().BeTrue();
		harness.Cache.Stores.Should().Be(100);
	}

	[Fact]
	public async Task PoolCache_SinglesFlightsConcurrentMisses_SoFiftyColdPagesRerankOnce()
	{
		// The reason HybridCache is in the stack at all (its in-memory tier is switched OFF). Fifty
		// callers arriving on the same cold key must produce ONE cross-encoder pass, not fifty — and
		// the forty-nine that did not run it must be told so, because their branch is "hydrate the
		// stored addresses", not "use the rows I just computed".
		using var harness = new PoolCacheHarness();
		var pool = new SearchPool([new Hit("t", "a", 1.0)], 495, false, new SearchRetrievers(true, false, false));
		var runs = 0;
		var gate = new TaskCompletionSource();

		var callers = Enumerable.Range(0, 50).Select(_ => Task.Run(async () =>
		{
			await gate.Task;
			return await harness.Cache.GetOrComputeAsync("hot", async _ =>
			{
				Interlocked.Increment(ref runs);
				await Task.Delay(50, CancellationToken.None);
				return new SearchPoolCache.PoolComputation(pool, Cacheable: true);
			});
		})).ToArray();

		gate.SetResult();
		var results = await Task.WhenAll(callers);

		runs.Should().Be(1, "fifty simultaneous misses must cost ONE computation");
		results.Count(r => !r.FromCache).Should().Be(1,
			"exactly the caller that ran the factory owns the fresh rows; everyone else hydrates");
		results.Should().OnlyContain(r => r.Pool.Ordered.Count == 1);
	}

	static ValueTask<SearchPoolCache.PoolComputation> Computed(SearchPool pool) =>
		ValueTask.FromResult(new SearchPoolCache.PoolComputation(pool, Cacheable: true));

	// ── fixtures ──────────────────────────────────────────────────────────────────────────────

	static Task<IReadOnlyList<string>> IdAsText(IReadOnlyList<Hit> candidates, CancellationToken ct) =>
		Task.FromResult<IReadOnlyList<string>>(candidates.Select(h => h.Id).ToList());

	// An ENUMERABLE lexical leg that returns its whole matched set in a fixed order, ignoring k — which
	// is exactly how the real lexical leg behaves (it has no top-K) and why a wide pool exists at all.
	sealed class StubIndex(IEnumerable<string> ids) : ISearchIndex
	{
		readonly List<string> _ids = ids.ToList();
		public SearchConsistency ConsistencyClass => SearchConsistency.Synchronous;
		public SearchCapability Capability => SearchCapability.Lexical;
		public Task IndexAsync(DataConnection? tx, SearchDoc doc, CancellationToken ct = default) => Task.CompletedTask;
		public Task DeleteAsync(DataConnection? tx, string scope, string type, string id, CancellationToken ct = default) => Task.CompletedTask;
		public Task DeleteByTypeAsync(DataConnection? tx, string scope, string type, CancellationToken ct = default) => Task.CompletedTask;
		public Task<IReadOnlyList<Hit>> SearchAsync(string scope, string query, SearchFilter filter, int k, CancellationToken ct = default) =>
			Task.FromResult<IReadOnlyList<Hit>>([.. _ids.Select((id, i) => new Hit("note", id, 1.0 / (i + 1), "lexical"))]);
	}

	// Counts rerank passes and records the topN it was asked for — the two facts requirement 5 is about.
	// Order-preserving, so the pool's order stays predictable for the repeatability assertions.
	sealed class CountingReranker : IReranker
	{
		public int Calls;
		public int LastTopN;
		public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

		public Task<IReadOnlyList<RerankedHit>> RerankAsync(string query, IReadOnlyList<string> documents, int topN, CancellationToken ct = default)
		{
			Calls++;
			LastTopN = topN;
			IReadOnlyList<RerankedHit> ranked = [.. documents.Select((_, i) => new RerankedHit(i, 1.0 / (i + 1))).Take(topN)];
			return Task.FromResult(ranked);
		}
	}

	sealed class FakeClock(DateTimeOffset now) : TimeProvider
	{
		public DateTimeOffset Now = now;
		public override DateTimeOffset GetUtcNow() => Now;
	}
}
