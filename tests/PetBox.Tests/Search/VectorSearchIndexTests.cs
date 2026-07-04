using LinqToDB;
using LinqToDB.Data;
using PetBox.Core.Search;

namespace PetBox.Tests.Search;

// The Class-B vector index and its hybrid fusion through the facade. A deterministic fake
// embedder makes the semantic leg reproducible: a sentinel marker steers a document's embedding
// next to the query vector, so we can assert (a) lexical ⊕ semantic union via RRF, (b) the
// model/dim guard, and (c) MRL truncation to the configured dim. The vector index is Eventual,
// so the facade does NOT drive it on write — these tests materialize it directly (standing in
// for the async-vectorization worker).
public sealed class VectorSearchIndexTests : IDisposable
{
	const string Scope = "proj/notes";
	readonly string _dir;
	readonly string _cs;

	public VectorSearchIndexTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-vec-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		_cs = $"Data Source={Path.Combine(_dir, "store.db")}";
		using var db = Connect();
		SqliteFtsIndex.EnsureSchema(db);
		VectorSearchIndex.EnsureSchema(db);
	}

	public void Dispose()
	{
		TestDirs.CleanupOrDefer(_dir);
	}

	DataConnection Connect() => new(new DataOptions().UseSQLite(_cs));

	static SearchDoc Doc(string id, string text) => new(Scope, "note", id, text);

	[Fact]
	public async Task Hybrid_FusesLexicalAndSemanticUnion_AndReportsBothRan()
	{
		var fts = new SqliteFtsIndex(Connect);
		var vec = new VectorSearchIndex(Connect, new FakeEmbedder(), dim: 8);
		var svc = new SearchService([fts, vec]);

		// "alpha" matches the query lexically; "beta" does not contain the token but its embedding
		// is steered next to the query vector, so only the semantic leg finds it.
		await using (var db = Connect())
		{
			using var tx = await db.BeginTransactionAsync();
			await svc.IndexAsync(db, Doc("alpha", "the alpha keyword appears here"));
			await svc.IndexAsync(db, Doc("beta", FakeEmbedder.NearQueryMarker + " unrelated words"));
			await tx.CommitAsync();
		}
		// Eventual index isn't driven by the facade — materialize it (worker stand-in).
		await vec.IndexAsync(null, Doc("alpha", "the alpha keyword appears here"));
		await vec.IndexAsync(null, Doc("beta", FakeEmbedder.NearQueryMarker + " unrelated words"));

		var res = await svc.SearchAsync(Scope, "alpha", new SearchFilter(), k: 10);

		res.Retrievers.Lexical.Should().BeTrue();
		res.Retrievers.Semantic.Should().BeTrue();
		res.Retrievers.Degraded.Should().BeFalse();
		res.Hits.Select(h => h.Id).Should().BeEquivalentTo(["alpha", "beta"]);
	}

	[Fact]
	public async Task VectorOnly_FindsSemanticNeighbour()
	{
		var vec = new VectorSearchIndex(Connect, new FakeEmbedder(), dim: 8);
		var svc = new SearchService([vec]);

		await vec.IndexAsync(null, Doc("near", FakeEmbedder.NearQueryMarker + " words"));
		await vec.IndexAsync(null, Doc("far", "totally different content here"));

		var res = await svc.SearchAsync(Scope, "whatever query", new SearchFilter(), k: 1);

		res.Retrievers.Lexical.Should().BeFalse();
		res.Retrievers.Semantic.Should().BeTrue();
		res.Hits.Select(h => h.Id).Should().Equal("near");
	}

	[Fact]
	public async Task ModelDimGuard_ExcludesIncomparableVectors()
	{
		var vec = new VectorSearchIndex(Connect, new FakeEmbedder(), dim: 8);

		await vec.IndexAsync(null, Doc("good", FakeEmbedder.NearQueryMarker + " body"));
		await vec.IndexAsync(null, Doc("bad", FakeEmbedder.NearQueryMarker + " body"));

		// Corrupt "bad"'s stored model so the query's (model,dim) guard must exclude it.
		await using (var db = Connect())
			await db.ExecuteAsync("UPDATE search_vec SET Model = 'other-model' WHERE Id = 'bad'");

		var svc = new SearchService([vec]);
		var res = await svc.SearchAsync(Scope, "query", new SearchFilter(), k: 10);

		res.Hits.Select(h => h.Id).Should().Equal("good"); // "bad" guarded out
	}

	[Fact]
	public async Task TruncatesStoredVectorToConfiguredDim()
	{
		var vec = new VectorSearchIndex(Connect, new FakeEmbedder(), dim: 4); // < FakeEmbedder.Dim (8)
		await vec.IndexAsync(null, Doc("a", "some text"));

		await using var db = Connect();
		var dim = db.GetTable<StoredVec>().Where(r => r.Id == "a").Select(r => r.Dim).ToList().First();
		dim.Should().Be(4);
	}

	[LinqToDB.Mapping.Table("search_vec")]
	sealed class StoredVec
	{
		[LinqToDB.Mapping.Column] public string Id { get; set; } = string.Empty;
		[LinqToDB.Mapping.Column] public int Dim { get; set; }
	}

	// Deterministic embedder: a stable text hash → fixed-dim vector, so the same text always
	// embeds identically. The marker (and any query-like input) collapses to the same unit
	// vector, putting marked documents adjacent to the query embedding.
	sealed class FakeEmbedder : IEmbedder
	{
		public const int Dim = 8;
		public const string Model = "fake-embed-v1";
		public const string NearQueryMarker = "__NEARQUERY__";

		public Task<EmbedBatch> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct = default) =>
			Task.FromResult(new EmbedBatch(inputs.Select(Vector).ToList(), Model, Dim));

		static float[] Vector(string text)
		{
			if (text.Contains(NearQueryMarker) || IsQueryLike(text))
			{
				var q = new float[Dim];
				q[0] = 1f;
				return q;
			}
			var v = new float[Dim];
			var h = unchecked((uint)text.GetHashCode());
			for (var i = 0; i < Dim; i++)
			{
				v[i] = ((h >> i) & 1) == 1 ? 1f : -1f;
				h = h * 2654435761u + 1u;
			}
			return v;
		}

		static bool IsQueryLike(string text) => !text.Contains('\n') && text.Split(' ').Length <= 2;
	}
}
