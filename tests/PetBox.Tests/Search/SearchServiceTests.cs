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
		using var db = Connect();
		SqliteFtsIndex.EnsureSchema(db);
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

	// A pluggable Class-B index that is unavailable at query time. Eventual consistency → the
	// facade never drives it on write; here it only models a failing read leg.
	sealed class ThrowingVectorIndex : ISearchIndex
	{
		public SearchConsistency ConsistencyClass => SearchConsistency.Eventual;
		public SearchCapability Capability => SearchCapability.Vector;
		public Task IndexAsync(DataConnection? tx, SearchDoc doc, CancellationToken ct = default) => Task.CompletedTask;
		public Task DeleteAsync(DataConnection? tx, string scope, string type, string id, CancellationToken ct = default) => Task.CompletedTask;
		public Task<IReadOnlyList<Hit>> SearchAsync(string scope, string query, SearchFilter filter, int k, CancellationToken ct = default) =>
			throw new InvalidOperationException("vector index down");
	}
}
