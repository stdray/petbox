using LinqToDB;
using LinqToDB.Data;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Core.Search;
using PetBox.Core.Settings;
using PetBox.Core.Contract;
using PetBox.LlmRouter.Contract;
using PetBox.Memory.Contract;
using PetBox.Memory.Data;
using PetBox.Memory.Services;

namespace PetBox.Tests.Memory;

// The B6 boundary check (spec memory-search-visibility-parity): the GENERIC search-visibility floor
// (опорный слой + лексическая нога in PetBox.Core.Search) ports onto memory WITHOUT editing a single
// generic node. These tests exercise memory THROUGH the generic mechanism and assert the pieces the
// parity spec names — the two-mode precision + provenance, and memory's DECLARED specializations
// (Description IS the title, a single `key` alias, updated-desc listing, and NO status facet because
// memory has no lifecycle). If any of these needed a generic-node change to pass, the boundary would
// be drawn wrong; they pass on the generic mechanism as-is.
public sealed class MemoryVisibilityParityTests : IDisposable
{
	const string Proj = "proj";
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<MemoryDb> _factory;
	readonly MemoryStore _store;

	public MemoryVisibilityParityTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-memparity-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });
		_factory = new ScopedDbFactory<MemoryDb>(Path.Combine(_dir, "memory"), Scope.Project,
			c => new MemoryDb(MemoryDb.CreateOptions(c)), MemorySchema.Ensure);
		_store = new MemoryStore(_db.Factory(), _factory);
	}

	public void Dispose()
	{
		_db.Dispose();
		_factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		TestDirs.CleanupOrDefer(_dir);
	}

	static MemoryEntryInput Entry(string key, string body) =>
		new() { Key = key, Version = 0, Type = "Project", Description = key, Body = body };

	static MemoryEntryInput Entry(string key, string description, string body) =>
		new() { Key = key, Version = 0, Type = "Project", Description = description, Body = body };

	// ---- two-mode precision + provenance on memory (spec: search-rerank-in-loop) ----
	//
	// Same seed, two runs: the lexical leg surfaces "decoy" ABOVE "winner" (decoy repeats the query
	// token, winner mentions it once). The штатный precision path reranks that candidate union with a
	// cross-encoder and promotes "winner" (its body carries the reranker's sentinel), reporting
	// Reranked=true. When the rerank route is unavailable the SAME search degrades HONESTLY to RRF —
	// Reranked=false, no cross-encoder promotion — and still answers. That is the two-mode precision:
	// rerank is штатный, RRF is the honest degradation, NEVER the default dressed up as precision.

	[Fact]
	public async Task Precision_LiveReranker_ReportsRerankedTrue_AndHonorsCrossEncoderOrder()
	{
		var memory = new MemoryService(_store, new RerankingLlmClient(rerankAvailable: true));
		await memory.CreateStoreAsync(Proj, "notes", null);
		await memory.UpsertAsync(Proj, "notes",
		[
			Entry("decoy", "alpha alpha alpha alpha note"),
			Entry("winner", "alpha WINNER note"),
		], []);

		// semantic:false isolates the lexical leg + the precision pass over its candidate union.
		var res = await memory.SearchAsync(Proj, "notes", "alpha", type: null, semantic: false);

		res.Retrievers.Reranked.Should().BeTrue("the cross-encoder rescored the candidate union — the штатный precision path");
		res.Retrievers.Degraded.Should().BeFalse();
		res.Hits.Select(h => h.Key).Should().Contain("winner").And.Contain("decoy");
		res.Hits[0].Key.Should().Be("winner",
			"the reranker promoted the sentinel-bearing candidate above the stronger LEXICAL hit — precision order, not RRF");
	}

	[Fact]
	public async Task Precision_NoRerankRoute_DegradesToRrfHonestly_RerankedFalse_StillAnswers()
	{
		var memory = new MemoryService(_store, new RerankingLlmClient(rerankAvailable: false));
		await memory.CreateStoreAsync(Proj, "notes", null);
		await memory.UpsertAsync(Proj, "notes",
		[
			Entry("decoy", "alpha alpha alpha alpha note"),
			Entry("winner", "alpha WINNER note"),
		], []);

		var res = await memory.SearchAsync(Proj, "notes", "alpha", type: null, semantic: false);

		res.Retrievers.Reranked.Should().BeFalse("no rerank route → the honest RRF degradation, never dressed up as precision");
		res.Hits.Select(h => h.Key).Should().Contain("winner").And.Contain("decoy");
		res.Hits[0].Key.Should().Be("decoy",
			"with no reranker the pure lexical/RRF order stands — the stronger lexical hit leads, no cross-encoder promotion");
	}

	// ---- specialization WITH REASON: Description IS the title, a free port to the weighted Title
	// column (search-doc-model-title-weights). Not laziness — a fact about the entity: a memory entry's
	// Description is its title, so it rides the SAME generic Title column tasks' Name does. ----

	[Fact]
	public async Task DescriptionIsTitle_WeightedAboveBody_OnTheGenericTitleColumn()
	{
		var memory = new MemoryService(_store); // llm:null → pure лексическая нога, no rerank noise
		await memory.CreateStoreAsync(Proj, "notes", null);
		await memory.UpsertAsync(Proj, "notes",
		[
			Entry("titled", description: "portalix", body: "an unrelated filler body with no query token"),
			Entry("bodied", description: "an unrelated description", body: "portalix shows up only here in the body"),
		], []);

		var res = await memory.SearchAsync(Proj, "notes", "portalix", type: null, semantic: false);

		var keys = res.Hits.Select(h => h.Key).ToList();
		keys.Should().Contain("titled").And.Contain("bodied");
		keys.IndexOf("titled").Should().BeLessThan(keys.IndexOf("bodied"),
			"a Description(=Title)-column hit is weighted above a Body-column hit — Description ported onto the generic Title column");
	}

	// ---- specialization WITH REASON: the alias set is a SINGLE identifier `key`, projected into the
	// generic indexed Key column (search-key-column-everywhere). Memory has no NodeId twin, so no
	// search_meta alias table is needed — the one key rides the лексическая нога's Key column. ----

	[Fact]
	public async Task SingleKeyAlias_RidesTheGenericLexicalKeyColumn()
	{
		var memory = new MemoryService(_store);
		await memory.CreateStoreAsync(Proj, "notes", null);
		// English kebab key, Russian body sharing NO word with the key: the only bridge from an
		// English key-word query to this entry is the indexed Key column.
		await memory.UpsertAsync(Proj, "notes",
		[
			Entry("model-tiers-roster", description: "памятка о ролях", body: "некоторый текст без английских слов"),
		], []);

		var res = await memory.SearchAsync(Proj, "notes", "model tiers roster", type: null, semantic: false);

		res.Hits.Select(h => h.Key).Should().Contain("model-tiers-roster",
			"the single `key` alias is searchable through the generic Key column — no separate alias table");
	}

	// ---- specialization WITH REASON: default listing order is updated-desc (memory-list-default-order).
	// A listing runs no relevance leg, so the presentation axis owns the order: newest-updated first. ----

	[Fact]
	public async Task DefaultListing_Order_IsUpdatedDesc()
	{
		var memory = new MemoryService(_store);
		await memory.CreateStoreAsync(Proj, "notes", null);
		await memory.UpsertAsync(Proj, "notes",
		[
			Entry("a", "one"),
			Entry("b", "two"),
			Entry("c", "three"),
		], []);
		// Backdate to distinct Updated instants so the order is deterministic: b newest, then a, then c.
		using (var ctx = _factory.NewEnsuredConnection(Proj))
		{
			void SetUpdated(string key, int daysAgo) => ctx.Execute(
				"UPDATE memory_entries SET Updated = @u WHERE Key = @k AND ActiveTo IS NULL",
				new DataParameter("u", DateTime.UtcNow.AddDays(-daysAgo)),
				new DataParameter("k", key));
			SetUpdated("b", 1);
			SetUpdated("a", 5);
			SetUpdated("c", 9);
		}

		var res = await memory.SearchEntriesAsync(Proj,
			new SearchRequest<MemoryEntryFilter, MemorySortBy> { Filter = new MemoryEntryFilter(), Limit = 20, BodyLen = 0 });

		res.Hits.Select(h => h.Entry.Key).Should().Equal("b", "a", "c");
		res.Retrievers.Should().BeNull("a listing ran no retriever leg — provenance is null, not a degraded search");
	}

	// ---- specialization WITH REASON: memory has NO lifecycle, so there is NO status facet to fill —
	// and therefore NO search_meta reference layer. The generic FACET MECHANISM (FacetFilter pushdown in
	// SqliteFtsIndex) still ports: memory passes a NEUTRAL facet (null), so the pushdown emits no join.
	// The mechanism is present and unedited; the VALUE-SET is the declared memory specialization. ----

	[Fact]
	public async Task NoLifecycle_NoStatusFacet_NoSearchMetaReferenceLayer()
	{
		var memory = new MemoryService(_store);
		await memory.CreateStoreAsync(Proj, "notes", null);
		await memory.UpsertAsync(Proj, "notes", [Entry("alpha", "alpha keyword")], []);
		// Touch the search path so the file is fully materialized (lexical backfill etc.).
		await memory.SearchAsync(Proj, "notes", "alpha", type: null, semantic: false);

		using var ctx = _factory.NewEnsuredConnection(Proj);
		var tables = ctx.Query<string>("SELECT name FROM sqlite_master WHERE type='table'").ToList();

		// The опорный слой's lexical half IS here (the generic mechanism ports)…
		tables.Should().Contain("search_fts", "memory rides the generic лексическая нога");
		// …but the facet/alias reference layer is NOT — memory has no lifecycle to classify and a
		// single key alias already lives in the Key column, so there is nothing for search_meta to hold.
		tables.Should().NotContain("search_meta",
			"memory declares no status facet (no lifecycle) — the facet MECHANISM ports as a neutral no-op, not a forked node");
	}

	// A deterministic ILlmClient with a REAL cross-encoder rerank: it promotes any candidate whose
	// resolved text carries the WINNER sentinel to the top, keeping the rest in fused order. `rerank
	// Available` toggles the fast-down probe so the same client exercises both precision modes. Embed
	// is a trivial stub (the precision tests run semantic:false, so it is never called).
	sealed class RerankingLlmClient(bool rerankAvailable) : ILlmClient
	{
		public Task<EmbedResult> EmbedAsync(string projectKey, EmbedRequest request, CancellationToken ct = default) =>
			Task.FromResult(new EmbedResult(
				request.Inputs.Select(_ => new float[] { 1f }).ToList(),
				new ModelIdentity("rr-embed", 1), new ServedBy("fake", "rr-embed", 1, Degraded: false)));

		public Task<RerankResult> RerankAsync(string projectKey, RerankRequest request, CancellationToken ct = default)
		{
			var hits = request.Documents
				.Select((doc, i) => new RerankHit(i, doc.Contains("WINNER") ? 100.0 : -i))
				.OrderByDescending(h => h.Score)
				.Take(request.TopN ?? request.Documents.Count)
				.ToList();
			return Task.FromResult(new RerankResult(hits, new ModelIdentity("rr-rerank"), new ServedBy("fake", "rr-rerank", 1, Degraded: false)));
		}

		public Task<ChatResult> ChatAsync(string projectKey, ChatRequest request, CancellationToken ct = default) =>
			throw new NotSupportedException();

		public Task<bool> IsAvailableAsync(string projectKey, LlmCapability capability, CancellationToken ct = default) =>
			Task.FromResult(capability != LlmCapability.Rerank || rerankAvailable);
	}
}
