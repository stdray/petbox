using LinqToDB;
using LinqToDB.Data;
using PetBox.Core.Search;

namespace PetBox.Tests.Search;

// End-to-end pass of the search contract skeleton: the SearchService facade in front of one
// trivial Class-A SQLite FTS5 index. Proves the load-bearing properties of the contract —
// entity-addressed index/search, the TRANSACTIONAL lexical floor (a rolled-back write leaves
// no trace), Russian/mixed tokenization, type filtering, delete, RRF fusion across indexes,
// and honest provenance (which retrievers ran + degraded).
public sealed class SearchServiceTests : IDisposable
{
	const string Scope = "proj/notes";
	readonly string _dir;
	readonly string _cs;

	public SearchServiceTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-search-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		_cs = $"Data Source={Path.Combine(_dir, "store.db")}";
		SearchTestSchema.Ensure(_cs);
	}

	public void Dispose()
	{
		TestDirs.CleanupOrDefer(_dir);
	}

	DataConnection Connect() => new(new DataOptions().UseSQLite(_cs));

	SearchService FtsService() => new([new SqliteFtsIndex(Connect)]);

	// Index a batch through the facade inside ONE entity transaction (the Class-A floor must
	// ride the caller's tx). commit=false exercises the transactional-rollback property.
	async Task IndexAsync(SearchService svc, bool commit, params SearchDoc[] docs)
	{
		await using var db = Connect();
		using var tx = await db.BeginTransactionAsync();
		foreach (var d in docs)
			await svc.IndexAsync(db, d);
		if (commit) await tx.CommitAsync();
		else await tx.RollbackAsync();
	}

	static SearchDoc Doc(string id, string text, string? tags = null) =>
		new(Scope, "note", id, text, tags);

	[Fact]
	public async Task IndexedEntity_IsFound_WithLexicalProvenance()
	{
		var svc = FtsService();
		await IndexAsync(svc, commit: true,
			Doc("a", "the alpha keyword appears here"),
			Doc("b", "completely unrelated content"));

		var res = await svc.SearchAsync(Scope, "alpha", new SearchFilter(), k: 10);

		res.Hits.Select(h => h.Id).Should().Equal("a");
		res.Hits[0].Type.Should().Be("note");
		res.Hits[0].Retriever.Should().Be("lexical");
		res.Retrievers.Lexical.Should().BeTrue();
		res.Retrievers.Semantic.Should().BeFalse();
		res.Retrievers.Degraded.Should().BeFalse();
	}

	[Fact]
	public async Task RolledBackWrite_LeavesNoLexicalTrace()
	{
		// The Class-A floor is transactional: a write that rolls back with its entity must not
		// be searchable (spec: search-lexical-floor). A non-transactional mirror would leak it.
		var svc = FtsService();
		await IndexAsync(svc, commit: false, Doc("ghost", "alpha keyword present"));

		var res = await svc.SearchAsync(Scope, "alpha", new SearchFilter(), k: 10);

		res.Hits.Should().BeEmpty();
	}

	[Fact]
	public async Task CyrillicQuery_Matches()
	{
		// ru/en lexical floor via the shared FtsQuery tokenizer (spec: search-lexical-multilingual).
		var svc = FtsService();
		await IndexAsync(svc, commit: true,
			Doc("ru", "разворачиваем сервер и настраиваем прокси"),
			Doc("en", "deploy the server"));

		var res = await svc.SearchAsync(Scope, "сервер", new SearchFilter(), k: 10);

		res.Hits.Select(h => h.Id).Should().Equal("ru");
	}

	[Fact]
	public async Task TypeFilter_NarrowsToEntityType()
	{
		var svc = FtsService();
		await using (var db = Connect())
		{
			using var tx = await db.BeginTransactionAsync();
			await svc.IndexAsync(db, new SearchDoc(Scope, "note", "n1", "shared keyword"));
			await svc.IndexAsync(db, new SearchDoc(Scope, "task", "t1", "shared keyword"));
			await tx.CommitAsync();
		}

		var res = await svc.SearchAsync(Scope, "keyword", new SearchFilter(Type: "task"), k: 10);

		res.Hits.Should().ContainSingle();
		res.Hits[0].Type.Should().Be("task");
		res.Hits[0].Id.Should().Be("t1");
	}

	[Fact]
	public async Task Delete_RemovesFromIndex()
	{
		var svc = FtsService();
		await IndexAsync(svc, commit: true, Doc("a", "alpha keyword"));

		await using (var db = Connect())
		{
			using var tx = await db.BeginTransactionAsync();
			await svc.DeleteAsync(db, Scope, "note", "a");
			await tx.CommitAsync();
		}

		var res = await svc.SearchAsync(Scope, "alpha", new SearchFilter(), k: 10);
		res.Hits.Should().BeEmpty();
	}

	[Fact]
	public async Task FailingEnrichmentIndex_DegradesButLexicalSurvives()
	{
		// A second (Class-B / vector) index that throws at query time must not sink the read:
		// lexical still answers, and provenance flags degraded with semantic=false (spec:
		// search-semantic-optional + search-provenance).
		var svc = new SearchService([new SqliteFtsIndex(Connect), new ThrowingVectorIndex()]);
		await IndexAsync(svc, commit: true, Doc("a", "alpha keyword"));

		var res = await svc.SearchAsync(Scope, "alpha", new SearchFilter(), k: 10);

		res.Hits.Select(h => h.Id).Should().Equal("a");
		res.Retrievers.Lexical.Should().BeTrue();
		res.Retrievers.Semantic.Should().BeFalse();
		res.Retrievers.Degraded.Should().BeTrue();
	}

	[Fact]
	public async Task EnumerableSelection_ExcludesTopKLeg_ReturnsFullSet_SemanticFalse()
	{
		// Participation rule (spec: search-leg-classification / search-selection-vs-presentation):
		// a RELEVANCE selection runs BOTH legs and the vector-only candidate enters as a peer; an
		// ENUMERABLE selection needs the full matched set, so the TopK (vector) leg is categorically
		// excluded — its candidate never appears — and provenance says `semantic:false`. The
		// enumerable leg is NOT truncated to k: every match is returned for the scan.
		var svc = new SearchService([new SqliteFtsIndex(Connect), new StubVectorIndex("v-only")]);
		await IndexAsync(svc, commit: true,
			Doc("a", "alpha keyword one"),
			Doc("b", "alpha keyword two"),
			Doc("c", "alpha keyword three"));

		// Relevance: both legs; the vector-only "v-only" enters as a peer; semantic:true.
		var rel = await svc.SearchAsync(Scope, "alpha", new SearchFilter(), k: 10, SearchSelection.Relevance);
		rel.Hits.Select(h => h.Id).Should().Contain("v-only");
		rel.Retrievers.Semantic.Should().BeTrue();

		// Enumerable: only the enumerable (lexical) leg; the vector candidate is excluded; the FULL
		// lexical set survives a tiny k (no truncation); semantic:false is the visible contract limit.
		var enumSel = await svc.SearchAsync(Scope, "alpha", new SearchFilter(), k: 1, SearchSelection.Enumerable);
		enumSel.Hits.Select(h => h.Id).Should().BeEquivalentTo(["a", "b", "c"]);
		enumSel.Hits.Select(h => h.Id).Should().NotContain("v-only");
		enumSel.Retrievers.Lexical.Should().BeTrue();
		enumSel.Retrievers.Semantic.Should().BeFalse();
	}

	[Fact]
	public async Task RelevanceWithReranker_ReordersToRerankerOrder_AndReportsReranked()
	{
		// PRECISION mode (spec: search-rerank-in-loop): a reranker reorders the deduped candidate union
		// and the result carries Reranked=true. The stub orders by document string DESC — independent of
		// the RRF/bm25 order — so the reranked output must differ from what fusion alone would produce.
		var reranker = new StubReranker(docs => docs
			.Select((d, i) => (d, i))
			.OrderByDescending(x => x.d, StringComparer.Ordinal)
			.Select(x => new RerankedHit(x.i, 1.0)).ToList());
		var svc = new SearchService([new SqliteFtsIndex(Connect)], reranker: reranker);
		await IndexAsync(svc, commit: true,
			Doc("a", "alpha one"), Doc("b", "alpha two"), Doc("c", "alpha three"));

		var res = await svc.SearchAsync(Scope, "alpha", new SearchFilter(), k: 10, resolveCandidateText: IdAsText);

		res.Hits.Select(h => h.Id).Should().Equal("c", "b", "a");
		res.Retrievers.Reranked.Should().BeTrue();
		res.Retrievers.Lexical.Should().BeTrue();
	}

	[Fact]
	public async Task RerankOutage_FallsBackToRrf_SearchStaysUp_RerankedFalse()
	{
		// A rerank outage must NEVER take search down: the pass throws SearchDegradedException, the
		// facade falls back to the honest RRF degradation (DegradedRrf), and the search still answers.
		var reranker = new StubReranker(_ =>
			throw new SearchDegradedException(SearchDegradedReason.RerankUnavailable, "rerank boom"));
		var svc = new SearchService([new SqliteFtsIndex(Connect)], reranker: reranker);
		await IndexAsync(svc, commit: true, Doc("a", "alpha keyword"), Doc("b", "completely unrelated"));

		var res = await svc.SearchAsync(Scope, "alpha", new SearchFilter(), k: 10, resolveCandidateText: IdAsText);

		res.Hits.Select(h => h.Id).Should().Equal("a");
		res.Retrievers.Reranked.Should().BeFalse();
		res.Retrievers.Degraded.Should().BeFalse(); // the LEGS answered fine; only the precision layer degraded
	}

	[Fact]
	public async Task RerankUnavailable_SkipsPrecisionPass_WithoutResolvingText()
	{
		// Fast-down (llm-fast-down): when no rerank route is live the facade skips the precision pass
		// UP FRONT — no candidate-text resolution, no thrown exception — and reports Reranked=false.
		var reranker = new StubReranker(docs => docs.Select((_, i) => new RerankedHit(i, 1.0)).ToList()) { Available = false };
		var svc = new SearchService([new SqliteFtsIndex(Connect)], reranker: reranker);
		await IndexAsync(svc, commit: true, Doc("a", "alpha keyword"));

		var resolverCalled = false;
		CandidateTextResolver resolve = (cands, _) =>
		{
			resolverCalled = true;
			return Task.FromResult<IReadOnlyList<string>>(cands.Select(h => h.Id).ToList());
		};
		var res = await svc.SearchAsync(Scope, "alpha", new SearchFilter(), k: 10, resolveCandidateText: resolve);

		res.Hits.Select(h => h.Id).Should().Equal("a");
		res.Retrievers.Reranked.Should().BeFalse();
		resolverCalled.Should().BeFalse();
	}

	[Fact]
	public async Task CandidateBudget_CapsWhatReachesTheReranker()
	{
		// The latency-derived candidate budget caps the pool reaching the cross-encoder (so the
		// enumerable lexical leg's full set can't flood it past the 5s bar): a budget of 2 lets only the
		// top 2 fused candidates through, even with 3 matches.
		var reranker = new StubReranker(docs => docs.Select((_, i) => new RerankedHit(i, 1.0)).ToList());
		var budget = new RerankCandidateBudget { LatencyBarMs = 369 };
		budget.Candidates().Should().Be(2);
		var svc = new SearchService([new SqliteFtsIndex(Connect)], reranker: reranker, budget: budget);
		await IndexAsync(svc, commit: true,
			Doc("a", "alpha one"), Doc("b", "alpha two"), Doc("c", "alpha three"));

		var res = await svc.SearchAsync(Scope, "alpha", new SearchFilter(), k: 10, resolveCandidateText: IdAsText);

		reranker.LastDocs!.Count.Should().Be(2);
		res.Hits.Count.Should().Be(2);
		res.Retrievers.Reranked.Should().BeTrue();
	}

	// The candidate-text resolver used by the rerank tests: the stub reranker only needs SOMETHING
	// deterministic to order by, so each candidate's text is just its Id.
	static Task<IReadOnlyList<string>> IdAsText(IReadOnlyList<Hit> candidates, CancellationToken ct) =>
		Task.FromResult<IReadOnlyList<string>>(candidates.Select(h => h.Id).ToList());

	// A pluggable IReranker for the precision-path tests: records the documents it was handed (to prove
	// the budget cap) and reorders them by a supplied ranking function; a function that throws models a
	// rerank outage. Available toggles the fast-down probe.
	sealed class StubReranker(Func<IReadOnlyList<string>, IReadOnlyList<RerankedHit>> rank) : IReranker
	{
		public bool Available = true;
		public List<string>? LastDocs;

		public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(Available);

		public Task<IReadOnlyList<RerankedHit>> RerankAsync(string query, IReadOnlyList<string> documents, int topN, CancellationToken ct = default)
		{
			LastDocs = documents.ToList();
			IReadOnlyList<RerankedHit> ranked = rank(documents).Take(topN).ToList();
			return Task.FromResult(ranked);
		}
	}

	// A pluggable Class-B (TopK) index that always surfaces one fixed vector-only hit — models the
	// vector leg for the participation-rule test without an embedder. LegClass defaults to TopK
	// (Capability=Vector), so an enumerable selection skips it.
	sealed class StubVectorIndex(string id) : ISearchIndex
	{
		public SearchConsistency ConsistencyClass => SearchConsistency.Eventual;
		public SearchCapability Capability => SearchCapability.Vector;
		public Task IndexAsync(DataConnection? tx, SearchDoc doc, CancellationToken ct = default) => Task.CompletedTask;
		public Task DeleteAsync(DataConnection? tx, string scope, string type, string id, CancellationToken ct = default) => Task.CompletedTask;
		public Task DeleteByTypeAsync(DataConnection? tx, string scope, string type, CancellationToken ct = default) => Task.CompletedTask;
		public Task<IReadOnlyList<Hit>> SearchAsync(string scope, string query, SearchFilter filter, int k, CancellationToken ct = default) =>
			Task.FromResult<IReadOnlyList<Hit>>([new Hit("note", id, 0.5, "semantic")]);
	}

	// A pluggable Class-B index that is unavailable at query time. Eventual consistency → the
	// facade never drives it on write; here it only models a failing read leg.
	sealed class ThrowingVectorIndex : ISearchIndex
	{
		public SearchConsistency ConsistencyClass => SearchConsistency.Eventual;
		public SearchCapability Capability => SearchCapability.Vector;
		public Task IndexAsync(DataConnection? tx, SearchDoc doc, CancellationToken ct = default) => Task.CompletedTask;
		public Task DeleteAsync(DataConnection? tx, string scope, string type, string id, CancellationToken ct = default) => Task.CompletedTask;
		public Task DeleteByTypeAsync(DataConnection? tx, string scope, string type, CancellationToken ct = default) => Task.CompletedTask;
		public Task<IReadOnlyList<Hit>> SearchAsync(string scope, string query, SearchFilter filter, int k, CancellationToken ct = default) =>
			throw new InvalidOperationException("vector index down");
	}
}
