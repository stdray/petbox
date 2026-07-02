using LinqToDB;
using LinqToDB.Data;
using Microsoft.Data.Sqlite;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Core.Search;
using PetBox.Core.Settings;
using PetBox.LlmRouter.Contract;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Data;
using PetBox.Tasks.Services;

namespace PetBox.Tests.Tasks;

// Hybrid (lexical FTS ⊕ semantic vectors, RRF-fused) board search and its honest
// provenance, mirroring the memory hybrid-search suite: a deterministic fake embedder
// makes the semantic leg reproducible so we can assert (a) the fused union, (b) graceful
// degrade to lexical-only when embedding is absent, (c) the model/dim guard that ignores
// incomparable stored vectors, (d) Cyrillic lexical match, and (e) board-filter scoping.
[Collection("DataModule")]
public sealed class TasksHybridSearchTests : IDisposable
{
	const string Proj = "proj";
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<TasksDb> _factory;
	readonly TaskBoardStore _store;
	readonly RelationStore _relations;
	readonly CommentService _commentSvc;
	readonly TagStore _tags;

	public TasksHybridSearchTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-taskshybrid-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });
		_factory = new ScopedDbFactory<TasksDb>(Path.Combine(_dir, "tasks"), Scope.Project,
			c => new TasksDb(TasksDb.CreateOptions(c)), TasksSchema.Ensure);
		_store = new TaskBoardStore(_db, _factory);
		_relations = new RelationStore(_db);
		_commentSvc = new CommentService(_factory);
		_tags = new TagStore(_factory);
	}

	public void Dispose()
	{
		_db.Dispose();
		_factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		SqliteConnection.ClearAllPools();
		if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
	}

	TasksService Service(ILlmClient? llm) => new(_store, _relations, _tags, _commentSvc, llm);

	// Vectors are materialized OFF the write path by the async-vectorization worker (per board), so
	// a test needing the semantic leg drains first (same embedder the query path uses → model/dim
	// guard matches). Mirrors TasksVectorizationJob for one board.
	async Task<DrainResult> DrainVectors(ILlmClient llm, string board)
	{
		DataConnection Connect() => _factory.NewConnection(Proj);
		var target = new VectorSearchIndex(Connect, new LlmClientEmbedder(llm, Proj));
		var source = new TasksSearchSource(Connect, Proj, board);
		var cursor = new SqliteIndexCursorStore(Connect);
		var worker = new AsyncVectorizationWorker(board, source, target, cursor);
		return await worker.DrainAsync();
	}

	// A free board accepts a bare node with a generated status — no spec/idea gate.
	static NodePatch Node(string key, string title, string body) =>
		new() { Key = key, Version = 0, Title = title, Body = body };

	// Query-mode request for the unified read verb (list = search without a query).
	static PetBox.Core.Contract.SearchRequest<TaskNodeFilter, TaskSortBy> Query(string q, string? board = null) =>
		new() { Query = q, Filter = new TaskNodeFilter(board) };

	[Fact]
	public async Task Hybrid_FusesLexicalAndSemanticUnion_AndReportsBothRan()
	{
		var tasks = Service(new FakeLlmClient());
		await tasks.CreateBoardAsync(Proj, "b", "simple", null, null);
		// "alpha" hits lexically on the query token; "beta" does NOT contain the token but its
		// embedding is steered to sit near the query vector, so only semantic finds it.
		await tasks.UpsertAsync(Proj, "b",
		[
			Node("alpha", "alpha note", "the alpha keyword appears here"),
			Node("beta", "beta note", FakeLlmClient.NearQueryMarker + " unrelated words"),
		]);
		await DrainVectors(new FakeLlmClient(), "b"); // materialize Class-B vectors for the semantic leg

		var res = await tasks.SearchNodesAsync(Proj, Query("alpha"));

		res.Retrievers!.Value.Lexical.Should().BeTrue();
		res.Retrievers!.Value.Semantic.Should().BeTrue();
		res.Retrievers!.Value.Degraded.Should().BeFalse();
		res.Hits.Select(h => h.Node.Key).Should().BeEquivalentTo(["alpha", "beta"]);
	}

	[Fact]
	public async Task NoLlm_DegradesToLexicalOnly()
	{
		var tasks = Service(llm: null);
		await tasks.CreateBoardAsync(Proj, "b", "simple", null, null);
		await tasks.UpsertAsync(Proj, "b", [Node("alpha", "alpha note", "alpha keyword")]);

		var res = await tasks.SearchNodesAsync(Proj, Query("alpha"));

		res.Retrievers!.Value.Lexical.Should().BeTrue();
		res.Retrievers!.Value.Semantic.Should().BeFalse();
		// _llm is null → semantic was never attempted, so this is not "degraded".
		res.Retrievers!.Value.Degraded.Should().BeFalse();
		res.Hits.Select(h => h.Node.Key).Should().Equal("alpha");
	}

	[Fact]
	public async Task ThrowingEmbedder_AtQueryTime_DegradesAndFlags()
	{
		// Embedder that throws: the write never embeds (Class-B is off the write path), so the
		// upsert succeeds regardless; at query time the semantic leg fails → lexical-only, degraded.
		var tasks = Service(new ThrowingLlmClient());
		await tasks.CreateBoardAsync(Proj, "b", "simple", null, null);
		await tasks.UpsertAsync(Proj, "b", [Node("alpha", "alpha note", "alpha keyword")]);

		var res = await tasks.SearchNodesAsync(Proj, Query("alpha"));

		res.Retrievers!.Value.Lexical.Should().BeTrue();
		res.Retrievers!.Value.Semantic.Should().BeFalse();
		res.Retrievers!.Value.Degraded.Should().BeTrue();
		res.Hits.Select(h => h.Node.Key).Should().Equal("alpha");
	}

	[Fact]
	public async Task SemanticOnly_WithModelDimMismatchRow_IgnoresIncomparableVector()
	{
		var tasks = Service(new FakeLlmClient());
		await tasks.CreateBoardAsync(Proj, "b", "simple", null, null);
		await tasks.UpsertAsync(Proj, "b",
		[
			Node("good", "good note", FakeLlmClient.NearQueryMarker + " body"),
			Node("bad", "bad note", FakeLlmClient.NearQueryMarker + " body"),
		]);
		await DrainVectors(new FakeLlmClient(), "b");

		// Corrupt "bad"'s stored vector to a different model — the query embedding's (model,dim)
		// guard must exclude it from the semantic candidate set. (Id = slug, Type = board.)
		var ctx = _store.GetContext(Proj);
		ctx.Execute("UPDATE search_vec SET Model = 'other-model' WHERE Type = 'b' AND Id = 'bad'");

		// The query token matches nothing lexically, so only the semantic leg contributes hits
		// (the per-retriever toggles are gone from the unified verb — both always run).
		var res = await tasks.SearchNodesAsync(Proj, Query("anything"));

		res.Retrievers!.Value.Semantic.Should().BeTrue();
		res.Hits.Select(h => h.Node.Key).Should().Equal("good"); // "bad" guarded out
	}

	[Fact]
	public async Task Lexical_MatchesCyrillicQuery()
	{
		// User content is partly Russian — a Cyrillic query MUST tokenize and match. An
		// ASCII-only tokenizer would drop the Cyrillic query entirely; the Unicode-aware
		// tokenizer + unicode61 FTS must hit.
		var tasks = Service(llm: null);
		await tasks.CreateBoardAsync(Proj, "b", "simple", null, null);
		await tasks.UpsertAsync(Proj, "b",
		[
			Node("ru", "заметка про деплой", "разворачиваем сервер и настраиваем прокси"),
			Node("en", "english note", "deploy the server"),
		]);

		var res = await tasks.SearchNodesAsync(Proj, Query("сервер"));

		res.Retrievers!.Value.Lexical.Should().BeTrue();
		res.Hits.Select(h => h.Node.Key).Should().Equal("ru"); // only the Cyrillic doc matches "сервер*"
	}

	[Fact]
	public async Task BoardFilter_ScopesToOneBoard()
	{
		// A node on board A must NOT be returned when searching board B.
		var tasks = Service(new FakeLlmClient());
		await tasks.CreateBoardAsync(Proj, "a", "simple", null, null);
		await tasks.CreateBoardAsync(Proj, "b", "simple", null, null);
		await tasks.UpsertAsync(Proj, "a", [Node("widget", "widget on a", "the gizmo keyword lives here")]);
		await tasks.UpsertAsync(Proj, "b", [Node("gadget", "gadget on b", "another gizmo keyword here")]);
		await DrainVectors(new FakeLlmClient(), "a");
		await DrainVectors(new FakeLlmClient(), "b");

		// Project-wide: both boards' "gizmo" nodes are found.
		var all = await tasks.SearchNodesAsync(Proj, Query("gizmo"));
		all.Hits.Select(h => h.Node.Key).Should().BeEquivalentTo(["widget", "gadget"]);

		// Scoped to board "b": only that board's node, and it carries board="b".
		var scoped = await tasks.SearchNodesAsync(Proj, Query("gizmo", board: "b"));
		scoped.Hits.Should().ContainSingle();
		scoped.Hits[0].Node.Key.Should().Be("gadget");
		scoped.Hits[0].Board.Should().Be("b");
		scoped.Board.Should().Be("b"); // board-scoped read carries the board context
		scoped.Kind.Should().Be("simple");
	}

	[Fact]
	public async Task TerminalNode_LeavesTheOpenSetIndex()
	{
		// Tasks search covers only the OPEN set. Moving a node to a terminal status must drop it
		// from the index — and that drop rides the entity transaction (onWithinTx), not a separate
		// post-commit rebuild.
		var tasks = Service(llm: null);
		await tasks.CreateBoardAsync(Proj, "b", "simple", null, null);
		await tasks.UpsertAsync(Proj, "b", [Node("keepme", "alpha note", "alpha keyword")]);
		(await tasks.SearchNodesAsync(Proj, Query("alpha"))).Hits.Select(h => h.Node.Key).Should().Equal("keepme");

		var view = await tasks.GetAsync(Proj, "b", includeClosed: false);
		var version = view.Nodes.First(n => n.Key == "keepme").Version;
		await tasks.UpsertAsync(Proj, "b", [new NodePatch { Key = "keepme", Version = version, Status = "Done" }]);

		(await tasks.SearchNodesAsync(Proj, Query("alpha"))).Hits.Should().BeEmpty();
	}

	// ---- deterministic fakes (same shape as the memory hybrid-search fakes) ----

	sealed class FakeLlmClient : ILlmClient
	{
		public const int Dim = 8;
		public const string Model = "fake-embed-v1";
		public const string NearQueryMarker = "__NEARQUERY__";

		public Task<EmbedResult> EmbedAsync(string projectKey, EmbedRequest request, CancellationToken ct = default)
		{
			var vectors = request.Inputs.Select(Vector).ToList();
			return Task.FromResult(new EmbedResult(vectors, new ModelIdentity(Model, Dim),
				new ServedBy("fake", Model, 1, Degraded: false)));
		}

		static float[] Vector(string text)
		{
			// Any text carrying the marker (and any query) collapses to the same unit vector,
			// so marked documents sit adjacent to the query embedding.
			if (text.Contains(NearQueryMarker) || !text.Contains(' ') || IsQueryLike(text))
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

		public Task<RerankResult> RerankAsync(string projectKey, RerankRequest request, CancellationToken ct = default) =>
			throw new NotSupportedException();
		public Task<ChatResult> ChatAsync(string projectKey, ChatRequest request, CancellationToken ct = default) =>
			throw new NotSupportedException();
		public Task<bool> IsAvailableAsync(string projectKey, LlmCapability capability, CancellationToken ct = default) =>
			Task.FromResult(true);
	}

	sealed class ThrowingLlmClient : ILlmClient
	{
		public Task<EmbedResult> EmbedAsync(string projectKey, EmbedRequest request, CancellationToken ct = default) =>
			throw new InvalidOperationException("embed down");
		public Task<RerankResult> RerankAsync(string projectKey, RerankRequest request, CancellationToken ct = default) =>
			throw new NotSupportedException();
		public Task<ChatResult> ChatAsync(string projectKey, ChatRequest request, CancellationToken ct = default) =>
			throw new NotSupportedException();
		public Task<bool> IsAvailableAsync(string projectKey, LlmCapability capability, CancellationToken ct = default) =>
			Task.FromResult(true);
	}
}
