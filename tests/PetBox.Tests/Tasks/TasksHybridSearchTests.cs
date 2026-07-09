using LinqToDB;
using LinqToDB.Data;
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
		TestDirs.CleanupOrDefer(_dir);
	}

	TasksService Service(ILlmClient? llm) => new(_store, _relations, _tags, _commentSvc, llm);

	// Vectors are materialized OFF the write path by the async-vectorization worker (per board), so
	// a test needing the semantic leg drains first (same embedder the query path uses → model/dim
	// guard matches). Mirrors TasksVectorizationJob for one board.
	async Task<DrainResult> DrainVectors(ILlmClient llm, string board)
	{
		DataConnection Connect() => _factory.NewEnsuredConnection(Proj);
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

	[Fact]
	public async Task ExactSlug_SurfacesTerminalNode_EvenThoughNotIndexed()
	{
		// exact-slug-lookup-terminal-nodes: a q that IS an existing node's slug must return the
		// node even after it goes terminal (dropped from the open-set index). Before the escape
		// hatch this returned empty; now it rides GetNodeAsync (includeClosed) and leads the hits.
		var tasks = Service(llm: null);
		await tasks.CreateBoardAsync(Proj, "b", "simple", null, null);
		await tasks.UpsertAsync(Proj, "b", [Node("kql-spans-query", "spans note", "some body")]);
		var version = (await tasks.GetAsync(Proj, "b", includeClosed: false)).Nodes.First(n => n.Key == "kql-spans-query").Version;
		await tasks.UpsertAsync(Proj, "b", [new NodePatch { Key = "kql-spans-query", Version = version, Status = "Done" }]);

		// Sanity: it's out of the relevance index (a plain content query no longer finds it).
		(await tasks.SearchNodesAsync(Proj, Query("spans"))).Hits.Should().BeEmpty();

		// The exact-slug q surfaces it regardless of terminality.
		var res = await tasks.SearchNodesAsync(Proj, Query("kql-spans-query"));
		res.Hits.Select(h => h.Node.Key).Should().Equal("kql-spans-query");
		res.Hits[0].Node.Status.Should().Be("Done");
	}

	[Fact]
	public async Task ExactSlug_OpenNodeMatchingBothWays_AppearsOnce()
	{
		// An open node whose slug equals the q also matches the relevance leg — the dedup guard
		// must keep it single (escape hatch skips a hit the fused ranking already produced).
		var tasks = Service(llm: null);
		await tasks.CreateBoardAsync(Proj, "b", "simple", null, null);
		await tasks.UpsertAsync(Proj, "b", [Node("alpha", "alpha note", "alpha keyword")]);

		var res = await tasks.SearchNodesAsync(Proj, Query("alpha"));
		res.Hits.Select(h => h.Node.Key).Should().Equal("alpha"); // exactly one, not duplicated
	}

	[Fact]
	public async Task ExactSlug_AmbiguousAcrossBoards_ReturnsAllMatches()
	{
		// exact-identifier-search-surfacing: the same slug on two boards, both terminal (so the
		// relevance index can't surface them), must ALL come back on a project-wide q — ambiguity
		// is not an error in search; each hit carries its board so the caller disambiguates.
		var tasks = Service(llm: null);
		await tasks.CreateBoardAsync(Proj, "a", "simple", null, null);
		await tasks.CreateBoardAsync(Proj, "b", "simple", null, null);
		foreach (var board in new[] { "a", "b" })
		{
			await tasks.UpsertAsync(Proj, board, [Node("dup", "dup note", "body")]);
			var v = (await tasks.GetAsync(Proj, board, includeClosed: false)).Nodes.First(n => n.Key == "dup").Version;
			await tasks.UpsertAsync(Proj, board, [new NodePatch { Key = "dup", Version = v, Status = "Done" }]);
		}

		// Project-wide (no board filter) → BOTH terminal matches, labelled by board, ordered by board.
		var all = await tasks.SearchNodesAsync(Proj, Query("dup"));
		all.Hits.Select(h => h.Board).Should().Equal("a", "b");
		all.Hits.Should().OnlyContain(h => h.Node.Key == "dup" && h.Node.Status == "Done");

		// Board-scoped narrows to that one board's terminal node.
		var scoped = await tasks.SearchNodesAsync(Proj, Query("dup", board: "a"));
		scoped.Hits.Select(h => h.Node.Key).Should().Equal("dup");
		scoped.Hits[0].Board.Should().Be("a");
	}

	// ---- per-row score/retriever + semantic floor (search-fusion-floor-impl) ----

	[Fact]
	public async Task Query_RowsCarryScoreAndRetriever_ListingLeavesThemNull()
	{
		var tasks = Service(new FakeLlmClient());
		await tasks.CreateBoardAsync(Proj, "b", "simple", null, null);
		await tasks.UpsertAsync(Proj, "b",
		[
			Node("marmot-1", "marmot note", "the marmot keyword appears here"),
			Node("marmot-2", "marmot too", "another marmot keyword lives here"),
		]);
		await DrainVectors(new FakeLlmClient(), "b");

		// Query mode: both nodes match lexically → retriever "lexical" and each carries a fused
		// RRF score, descending down the result order.
		var query = await tasks.SearchNodesAsync(Proj, Query("marmot"));
		query.Hits.Should().OnlyContain(h => h.Retriever == "lexical" && h.Score != null);
		query.Hits.Select(h => h.Score!.Value).Should().BeInDescendingOrder();

		// Listing mode runs no relevance leg → score/retriever stay null (omitted on the wire).
		var listing = await tasks.SearchNodesAsync(Proj,
			new PetBox.Core.Contract.SearchRequest<TaskNodeFilter, TaskSortBy> { Filter = new TaskNodeFilter("b") });
		listing.Hits.Should().OnlyContain(h => h.Score == null && h.Retriever == null);
	}

	[Fact]
	public async Task Floor_DropsSemanticOnlyTail_LimitIsCeilingNotPlan()
	{
		var tasks = Service(new FakeLlmClient());
		await tasks.CreateBoardAsync(Proj, "b", "simple", null, null);
		// Eight nodes that match ONLY semantically: each body carries the near-query marker (its
		// embedding collapses onto the query vector) but shares no lexical token with the query.
		var nodes = Enumerable.Range(0, 8)
			.Select(i => Node($"sem-{i}", $"note {i}", FakeLlmClient.NearQueryMarker + $" filler{i}"))
			.ToArray();
		await tasks.UpsertAsync(Proj, "b", nodes);
		await DrainVectors(new FakeLlmClient(), "b");

		// A token that appears in NO body: the lexical leg confirms nothing, the vector leg alone
		// surfaces all eight. Even with a generous limit the answer is NOT padded to eight — the
		// sub-floor semantic-only tail is dropped, so only the ~top-5 candidates survive.
		var res = await tasks.SearchNodesAsync(Proj,
			new PetBox.Core.Contract.SearchRequest<TaskNodeFilter, TaskSortBy>
			{
				Query = "zqxjkw",
				Filter = new TaskNodeFilter("b"),
				Limit = 50,
			});

		res.Hits.Should().OnlyContain(h => h.Retriever == "semantic");
		res.Hits.Count.Should().BeInRange(1, 5,
			"sub-floor semantic-only hits are cut, so limit is a ceiling not a plan");
	}

	[Fact]
	public async Task ExactSlug_Hit_CarriesExactRetriever_AndNullScore()
	{
		var tasks = Service(llm: null);
		await tasks.CreateBoardAsync(Proj, "b", "simple", null, null);
		await tasks.UpsertAsync(Proj, "b", [Node("wombat-burrow", "wombat note", "some body")]);
		// Terminal → out of the relevance index; only the exact-slug escape hatch can surface it.
		var version = (await tasks.GetAsync(Proj, "b", includeClosed: false)).Nodes.First(n => n.Key == "wombat-burrow").Version;
		await tasks.UpsertAsync(Proj, "b", [new NodePatch { Key = "wombat-burrow", Version = version, Status = "Done" }]);

		var res = await tasks.SearchNodesAsync(Proj, Query("wombat-burrow"));
		res.Hits.Should().ContainSingle();
		res.Hits[0].Retriever.Should().Be("exact");
		res.Hits[0].Score.Should().BeNull(); // an addressed match has no fused score
	}

	// ---- comments in the lexical corpus (tasks-search-comments) ----

	// Add a root comment under a node (resolves the owner's stable NodeId from the open view).
	async Task<CommentUpsertResult> AddComment(TasksService tasks, string board, string nodeKey, string body)
	{
		var view = await tasks.GetAsync(Proj, board, includeClosed: false);
		var nodeId = view.Nodes.First(n => n.Key == nodeKey).NodeId;
		return await _commentSvc.AddAsync(Proj, board, nodeId, null, "author", body, null);
	}

	[Fact]
	public async Task CommentMatch_ReturnsOwnerNode_MarkedMatchedInComment()
	{
		// The node body does NOT contain the token — only the comment does. The hit must still
		// come back, pointing at the OWNER node, marked matchedIn="comment" with lexical provenance.
		var tasks = Service(llm: null);
		await tasks.CreateBoardAsync(Proj, "b", "simple", null, null);
		await tasks.UpsertAsync(Proj, "b", [Node("host", "host note", "unrelated node body")]);
		await AddComment(tasks, "b", "host", "the platypus insight lives in this comment");

		var res = await tasks.SearchNodesAsync(Proj, Query("platypus"));

		res.Hits.Should().ContainSingle();
		res.Hits[0].Node.Key.Should().Be("host");
		res.Hits[0].MatchedIn.Should().Be("comment");
		res.Hits[0].Retriever.Should().Be("lexical");
		res.Hits[0].Score.Should().NotBeNull();
	}

	[Fact]
	public async Task NodeAndCommentBothMatch_SingleRow_NodeWins_MatchedInNull()
	{
		// Both the node (directly) and its comment match the query. The direct node hit — the token
		// packed into a short doc — outranks the single mention buried in a longer comment, so the
		// node wins the fused rank; the comment hit is a duplicate and is dropped (matchedIn stays null).
		var tasks = Service(llm: null);
		await tasks.CreateBoardAsync(Proj, "b", "simple", null, null);
		await tasks.UpsertAsync(Proj, "b", [Node("beaver", "beaver beaver", "beaver beaver beaver")]);
		await AddComment(tasks, "b", "beaver", "a long passing note that mentions beaver once amid much other filler text here");

		var res = await tasks.SearchNodesAsync(Proj, Query("beaver"));

		res.Hits.Should().ContainSingle(); // no duplicate row
		res.Hits[0].Node.Key.Should().Be("beaver");
		res.Hits[0].MatchedIn.Should().BeNull(); // the node won the better rank
	}

	[Fact]
	public async Task CommentEdit_ReindexesText_Delete_RemovesFromIndex()
	{
		var tasks = Service(llm: null);
		await tasks.CreateBoardAsync(Proj, "b", "simple", null, null);
		await tasks.UpsertAsync(Proj, "b", [Node("host", "host note", "unrelated body")]);
		var add = await AddComment(tasks, "b", "host", "quokka feedback goes here");

		(await tasks.SearchNodesAsync(Proj, Query("quokka"))).Hits.Select(h => h.Node.Key).Should().Equal("host");

		// Edit swaps the token: the OLD text stops matching, the NEW text finds the owner node.
		await _commentSvc.EditAsync(Proj, "b", add.Id!, "wombat feedback goes here", null, add.CurrentVersion);
		(await tasks.SearchNodesAsync(Proj, Query("quokka"))).Hits.Should().BeEmpty();
		(await tasks.SearchNodesAsync(Proj, Query("wombat"))).Hits.Select(h => h.Node.Key).Should().Equal("host");

		// Delete drops the comment's FTS row entirely.
		(await _commentSvc.DeleteAsync(Proj, "b", add.Id!)).Should().BeTrue();
		(await tasks.SearchNodesAsync(Proj, Query("wombat"))).Hits.Should().BeEmpty();
	}

	[Fact]
	public async Task CommentBackfill_ReindexesAfterFtsRowsWiped()
	{
		var tasks = Service(llm: null);
		await tasks.CreateBoardAsync(Proj, "b", "simple", null, null);
		await tasks.UpsertAsync(Proj, "b", [Node("host", "host note", "unrelated body")]);
		await AddComment(tasks, "b", "host", "narwhal note in a comment");

		// Write path already indexed the comment.
		(await tasks.SearchNodesAsync(Proj, Query("narwhal"))).Hits.Select(h => h.Node.Key).Should().Equal("host");

		// Simulate a file predating tasks-search-comments: wipe ONLY the comment FTS rows (node rows
		// stay), so the node-backfill guard is satisfied and only the comment backfill re-runs.
		var ctx = _store.GetContext(Proj);
		ctx.Execute("DELETE FROM search_fts WHERE Id LIKE 'c:%'");
		ctx.Execute<long>("SELECT count(*) FROM search_fts WHERE Id LIKE 'c:%'").Should().Be(0);

		// The next query runs the comment backfill (guard: no c:% rows) → the comment is found again.
		(await tasks.SearchNodesAsync(Proj, Query("narwhal"))).Hits.Select(h => h.Node.Key).Should().Equal("host");
	}

	[Fact]
	public async Task CommentOnBoardA_DoesNotLeakIntoBoardBQuery()
	{
		// Type=board scoping: a comment under a board-A node must not surface in a board-B query.
		var tasks = Service(llm: null);
		await tasks.CreateBoardAsync(Proj, "a", "simple", null, null);
		await tasks.CreateBoardAsync(Proj, "b", "simple", null, null);
		await tasks.UpsertAsync(Proj, "a", [Node("anode", "a node", "body a")]);
		await tasks.UpsertAsync(Proj, "b", [Node("bnode", "b node", "body b")]);
		await AddComment(tasks, "a", "anode", "axolotl only in a board-a comment");

		(await tasks.SearchNodesAsync(Proj, Query("axolotl", board: "b"))).Hits.Should().BeEmpty();
		(await tasks.SearchNodesAsync(Proj, Query("axolotl", board: "a"))).Hits.Select(h => h.Node.Key).Should().Equal("anode");
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
