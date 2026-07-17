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
		_store = new TaskBoardStore(_db.Factory(), _factory);
		_relations = new RelationStore(_factory);
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
	static PetBox.Core.Contract.SearchRequest<TaskNodeFilter, TaskSortBy> Query(string q, string? board = null, bool includeClosed = false) =>
		new() { Query = q, Filter = new TaskNodeFilter(board, IncludeClosed: includeClosed) };

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
	public async Task TerminalCancelNode_LeavesTheDefaultQuery()
	{
		// search-hides-terminal-nodes (defect 2, owner decision): a default query-mode search
		// hides ONLY terminal-CANCEL (rejected/cancelled) — moving a node to Cancelled must drop
		// it from a plain content query's result. TerminalOk (Done) does NOT leave the default
		// query — see TerminalOkNode_StaysInTheDefaultQuery.
		var tasks = Service(llm: null);
		await tasks.CreateBoardAsync(Proj, "b", "simple", null, null);
		await tasks.UpsertAsync(Proj, "b", [Node("keepme", "alpha note", "alpha keyword")]);
		(await tasks.SearchNodesAsync(Proj, Query("alpha"))).Hits.Select(h => h.Node.Key).Should().Equal("keepme");

		var view = await tasks.GetAsync(Proj, "b", includeClosed: false);
		var version = view.Nodes.First(n => n.Key == "keepme").Version;
		await tasks.UpsertAsync(Proj, "b", [new NodePatch { Key = "keepme", Version = version, Status = "Cancelled" }]);

		(await tasks.SearchNodesAsync(Proj, Query("alpha"))).Hits.Should().BeEmpty();
	}

	[Fact]
	public async Task TerminalOkNode_StaysInTheDefaultQuery()
	{
		// search-hides-terminal-nodes (defect 2, owner decision): terminal-OK (Done on work,
		// accepted on ideas) is a SUCCESS state, not "closed" — it is the anchor
		// search-before-rework needs to reach, so a default query-mode search must still find
		// it after the transition, with no includeClosed needed.
		var tasks = Service(llm: null);
		await tasks.CreateBoardAsync(Proj, "b", "simple", null, null);
		await tasks.UpsertAsync(Proj, "b", [Node("keepme", "alpha note", "alpha keyword")]);
		var version = (await tasks.GetAsync(Proj, "b", includeClosed: false)).Nodes.First(n => n.Key == "keepme").Version;
		await tasks.UpsertAsync(Proj, "b", [new NodePatch { Key = "keepme", Version = version, Status = "Done" }]);

		var res = await tasks.SearchNodesAsync(Proj, Query("alpha"));
		res.Hits.Select(h => h.Node.Key).Should().Equal("keepme");
		res.Hits[0].Node.Status.Should().Be("Done");
	}

	[Fact]
	public async Task ExactSlug_SurfacesTerminalCancelNode_EvenThoughHiddenByDefault()
	{
		// exact-slug-lookup-terminal-nodes: a q that IS an existing node's slug must return the
		// node even after it goes terminal-CANCEL — hidden from the default query-mode pool per
		// search-hides-terminal-nodes, but the exact escape hatch (GetNodeAsync, includeClosed
		// internally) ignores that filter entirely and leads the hits with no includeClosed
		// needed on the request.
		var tasks = Service(llm: null);
		await tasks.CreateBoardAsync(Proj, "b", "simple", null, null);
		await tasks.UpsertAsync(Proj, "b", [Node("kql-spans-query", "spans note", "some body")]);
		var version = (await tasks.GetAsync(Proj, "b", includeClosed: false)).Nodes.First(n => n.Key == "kql-spans-query").Version;
		await tasks.UpsertAsync(Proj, "b", [new NodePatch { Key = "kql-spans-query", Version = version, Status = "Cancelled" }]);

		// Sanity: hidden from the default relevance pool (a plain content query finds nothing).
		(await tasks.SearchNodesAsync(Proj, Query("spans"))).Hits.Should().BeEmpty();

		// The exact-slug q surfaces it regardless of terminality — no includeClosed needed.
		var res = await tasks.SearchNodesAsync(Proj, Query("kql-spans-query"));
		res.Hits.Select(h => h.Node.Key).Should().Equal("kql-spans-query");
		res.Hits[0].Node.Status.Should().Be("Cancelled");
		res.Hits[0].Retriever.Should().Be("exact");
	}

	[Fact]
	public async Task ExactNodeId_SurfacesTerminalCancelNode_IgnoringTheFilter()
	{
		// Regression: an exact 32-hex NodeId query must still resolve via the exact escape
		// hatch — rank first, retriever "exact" — and ignore the terminal-CANCEL filter
		// entirely, same as an exact slug does.
		var tasks = Service(llm: null);
		await tasks.CreateBoardAsync(Proj, "b", "simple", null, null);
		await tasks.UpsertAsync(Proj, "b", [Node("target-node", "target note", "some body")]);
		var before = (await tasks.GetAsync(Proj, "b", includeClosed: false)).Nodes.First(n => n.Key == "target-node");
		await tasks.UpsertAsync(Proj, "b", [new NodePatch { Key = "target-node", Version = before.Version, Status = "Cancelled" }]);

		var res = await tasks.SearchNodesAsync(Proj, Query(before.NodeId));

		res.Hits.Select(h => h.Node.Key).Should().Equal("target-node");
		res.Hits[0].Retriever.Should().Be("exact");
		res.Hits[0].Score.Should().BeNull();
		res.Hits[0].Node.Status.Should().Be("Cancelled");
	}

	[Fact]
	public async Task IncludeClosed_SurfacesTerminalCancelNode_InRankedQuery()
	{
		// search-hides-terminal-nodes (defect 3): includeClosed:true with q is no longer a
		// silent no-op — it widens the RANKED candidate pool to terminal-CANCEL nodes too,
		// symmetric with listing mode's includeClosed. Uses a content match (not the slug) so
		// the exact escape hatch can't be the one doing the work.
		var tasks = Service(llm: null);
		await tasks.CreateBoardAsync(Proj, "b", "simple", null, null);
		await tasks.UpsertAsync(Proj, "b", [Node("scrapped-widget", "scrapped widget note", "the marmot keyword lives here")]);
		var version = (await tasks.GetAsync(Proj, "b", includeClosed: false)).Nodes.First(n => n.Key == "scrapped-widget").Version;
		await tasks.UpsertAsync(Proj, "b", [new NodePatch { Key = "scrapped-widget", Version = version, Status = "Cancelled" }]);

		// Regression: hidden by default.
		(await tasks.SearchNodesAsync(Proj, Query("marmot"))).Hits.Should().BeEmpty();

		// includeClosed:true actually widens the search now.
		var widened = await tasks.SearchNodesAsync(Proj, Query("marmot", "b", includeClosed: true));
		widened.Hits.Select(h => h.Node.Key).Should().Equal("scrapped-widget");
		widened.Hits[0].Node.Status.Should().Be("Cancelled");
		widened.Hits[0].Retriever.Should().Be("lexical");
	}

	[Fact]
	public async Task SlugWords_SpacedQuery_FindsTheKebabSlug_ViaNormalizedExact()
	{
		// search-hides-terminal-nodes (defect 1): a q that is the slug's WORDS separated by
		// spaces — not the verbatim kebab slug — must still hit the exact escape hatch via a
		// normalized kebab candidate (trim, collapse whitespace/underscores to one hyphen,
		// casefold), tried as a SECOND candidate behind the literal identifier. The node's
		// prose carries no Latin tokens, so a fused lexical/semantic hit can't be the one
		// doing the work here — only the normalized exact path can find it.
		var tasks = Service(llm: null);
		await tasks.CreateBoardAsync(Proj, "b", "simple", null, null);
		await tasks.UpsertAsync(Proj, "b",
			[Node("methodology-redesign", "Редизайн методологии", "Полное описание передела процесса.")]);

		var res = await tasks.SearchNodesAsync(Proj, Query("methodology redesign"));

		res.Hits.Select(h => h.Node.Key).Should().Equal("methodology-redesign");
		res.Hits[0].Retriever.Should().Be("exact");
		res.Hits[0].Score.Should().BeNull();
	}

	[Fact]
	public async Task ExactSlug_OpenNodeMatchingBothWays_AppearsOnce()
	{
		// An open node whose slug equals the q also matches the relevance leg — the dedup guard
		// must keep it single. Since search-slug-words-gap the OVERLAP is the normal case (the
		// slug's own words are lexical terms, so a verbatim-slug q always matches lexically too),
		// and the dedup resolves toward the ADDRESSED hit: one row, labelled `exact`, not the
		// fused copy labelled `lexical`.
		var tasks = Service(llm: null);
		await tasks.CreateBoardAsync(Proj, "b", "simple", null, null);
		await tasks.UpsertAsync(Proj, "b", [Node("alpha", "alpha note", "alpha keyword")]);

		var res = await tasks.SearchNodesAsync(Proj, Query("alpha"));
		res.Hits.Select(h => h.Node.Key).Should().Equal("alpha"); // exactly one, not duplicated
		res.Hits[0].Retriever.Should().Be("exact");
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

	// ---- the slug is part of the lexicon (search-slug-words-gap) ----

	[Fact]
	public async Task SlugWords_SpacedQuery_FindsRussianTitledNode()
	{
		// THE repro (owner, 16.07.2026). Slugs are English kebab, titles/bodies are often Russian —
		// so an English-speaking query has exactly two ways in: the slug verbatim (exact) or
		// semantics. Type the slug's own words with SPACES and both miss: too fuzzy to be an
		// addressed ask, and (before the fix) `methodology`/`lifecycle`/`ux` were in no document.
		// The slug now leads the indexed text, so the lexical leg carries the query across the
		// English-identifier / Russian-prose divide. llm: null → no semantic leg can rescue this;
		// what passes here passes lexically or not at all.
		//
		// Word order is SHUFFLED on purpose (search-hides-terminal-nodes, defect 1): the slug's
		// words IN ORDER now also normalize into the verbatim slug and resolve via the exact
		// escape hatch (see SlugWords_SpacedQuery_FindsTheKebabSlug_ViaNormalizedExact) — a
		// stronger, addressed result. Reordering keeps this test proving the INDEPENDENT lexical
		// bridge (order-agnostic FTS matching), which the exact/kebab candidate does not cover.
		var tasks = Service(llm: null);
		await tasks.CreateBoardAsync(Proj, "b", "simple", null, null);
		await tasks.UpsertAsync(Proj, "b",
		[
			Node("methodology-lifecycle-ux", "Жизненный цикл методологии: удобство работы",
				"Как выглядит путь от идеи до спека глазами агента."),
		]);

		var res = await tasks.SearchNodesAsync(Proj, Query("ux lifecycle methodology"));

		res.Retrievers!.Value.Lexical.Should().BeTrue();
		res.Hits.Select(h => h.Node.Key).Should().Equal("methodology-lifecycle-ux");
		res.Hits[0].Retriever.Should().Be("lexical"); // lexicon, not the exact escape hatch
	}

	[Fact]
	public async Task SlugWords_SingleWordOfSlug_IsEnough()
	{
		// The bridge is per-WORD, not all-or-nothing: one word of the slug reaches the node, and a
		// word of no document reaches nothing (the terms come from the slug, not from thin air).
		var tasks = Service(llm: null);
		await tasks.CreateBoardAsync(Proj, "b", "simple", null, null);
		await tasks.UpsertAsync(Proj, "b", [Node("kql-spans-query", "Запрос по спанам", "Тело без латиницы.")]);

		(await tasks.SearchNodesAsync(Proj, Query("spans"))).Hits
			.Select(h => h.Node.Key).Should().Equal("kql-spans-query");
		(await tasks.SearchNodesAsync(Proj, Query("wombat"))).Hits.Should().BeEmpty();
	}

	[Fact]
	public async Task SlugWords_DoNotOutrankATopicalMatch()
	{
		// The ranking guard. The new channel must ADD reach, not reorder what already worked: a node
		// that merely SHARES one word in its slug must not climb over the node the query is actually
		// about. Both match `report`, but only `report-deploy-pipeline` matches through its slug
		// alone — its prose is about something else entirely — while the topical node carries the
		// term in title AND body. The topical node stays first.
		//
		// The query is LATIN on purpose: a slug is `[a-z0-9_-]*`, so a Cyrillic query can never
		// reach one, and a decoy built on a transliterated slug (`otchet-…` vs `отчёт`) would not
		// match at all — the test would pass on an empty decoy and prove nothing.
		var tasks = Service(llm: null);
		await tasks.CreateBoardAsync(Proj, "b", "simple", null, null);
		await tasks.UpsertAsync(Proj, "b",
		[
			Node("weekly-summary", "Weekly report of the team", "The report is assembled from member reports."),
			Node("report-deploy-pipeline", "Конвейер выкатки", "Как устроен деплой в проде."),
		]);

		var res = await tasks.SearchNodesAsync(Proj, Query("report"));

		res.Hits.Select(h => h.Node.Key).Should().Equal("weekly-summary", "report-deploy-pipeline");
	}

	[Fact]
	public async Task VerbatimSlug_StaysExactAndFirst_EvenThoughItNowMatchesLexically()
	{
		// The regression the fix could plausibly cause. A verbatim-slug query now ALSO matches the
		// node lexically, so the old "skip an exact hit the fused ranking already produced" rule
		// would have handed the addressed node back at its FUSED rank, labelled `lexical` — behind
		// a node that merely shares the slug's words. exact-identifier-search-surfacing says the
		// exact match leads and is confirmed by identity; the decoy must not get in front of it.
		var tasks = Service(llm: null);
		await tasks.CreateBoardAsync(Proj, "b", "simple", null, null);
		await tasks.UpsertAsync(Proj, "b",
		[
			Node("decoy", "alpha beta gamma", "alpha beta gamma alpha beta gamma"),
			Node("alpha-beta-gamma", "Русский заголовок", "Русское тело."),
		]);

		var res = await tasks.SearchNodesAsync(Proj, Query("alpha-beta-gamma"));

		res.Hits[0].Node.Key.Should().Be("alpha-beta-gamma");
		res.Hits[0].Retriever.Should().Be("exact");
		res.Hits[0].Score.Should().BeNull();
		res.Hits.Count(h => h.Node.Key == "alpha-beta-gamma").Should().Be(1); // not duplicated
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
	public async Task CommentRowsWiped_DoNotSelfHeal_UntilTheLexicalMarkerIsRewound()
	{
		// The old per-signal guards ("no c:% row yet") are gone (reindex-as-first-class-mechanism):
		// EnsureLexicalBackfillAsync is now VERSION-gated, ONE marker for the whole file — nodes and
		// comments both come out of the single TasksSearchDocs projection. So once a search has
		// stamped the marker at the current version, wiping just the comment rows does NOT self-heal
		// on the next plain search (that would mean re-verifying the whole file on every query).
		// Recovery from row-level corruption like this is what search_reindex's Class-A half is
		// for: it rewinds TasksCursors.Lexical, and the very next search rebuilds the file.
		var tasks = Service(llm: null);
		await tasks.CreateBoardAsync(Proj, "b", "simple", null, null);
		await tasks.UpsertAsync(Proj, "b", [Node("host", "host note", "unrelated body")]);
		await AddComment(tasks, "b", "host", "narwhal note in a comment");

		// Write path already indexed the comment; this search also stamps the lexical marker.
		(await tasks.SearchNodesAsync(Proj, Query("narwhal"))).Hits.Select(h => h.Node.Key).Should().Equal("host");

		var ctx = _store.GetContext(Proj);
		ctx.Execute("DELETE FROM search_fts WHERE Id LIKE 'c:%'");
		ctx.Execute<long>("SELECT count(*) FROM search_fts WHERE Id LIKE 'c:%'").Should().Be(0);

		// The marker is current → a plain search does NOT rebuild the wiped comment row.
		(await tasks.SearchNodesAsync(Proj, Query("narwhal"))).Hits.Should().BeEmpty();

		// Rewinding the marker (what search_reindex's Class-A reset does) heals it on the NEXT search.
		ctx.Execute($"UPDATE search_cursor SET Version = 0 WHERE IndexName = '{TasksCursors.Lexical}'");
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
