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

// W5 relevance overhaul (spec memoverhaul-global-fusion): GLOBAL cross-store RRF fusion (the best
// hit wins regardless of which store holds it), freshness time-decay (fresher wins at comparable
// relevance), and MMR diversification (near-duplicates don't crowd the head) — plus honest
// degradation without an embedder. Rerank knobs are toggled per-test so each property is isolated.
public sealed class MemoryFusionRerankTests : IDisposable
{
	const string Proj = "proj";
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<MemoryDb> _factory;
	readonly MemoryStore _store;

	public MemoryFusionRerankTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-memfuse-" + Guid.NewGuid().ToString("N"));
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

	static SearchRerankOptions Off => new()
	{
		Recency = new RecencyOptions { Enabled = false },
		Diversity = new DiversityOptions { Enabled = false },
	};

	static MemoryEntryInput Entry(string key, string body) =>
		new() { Key = key, Version = 0, Type = "Project", Description = key, Body = body };

	static SearchRequest<MemoryEntryFilter, MemorySortBy> Query(string q, int limit = 20) =>
		new() { Query = q, Filter = new MemoryEntryFilter(), Limit = limit, BodyLen = 0 };

	static async Task<List<string>> SearchKeys(MemoryService memory, string q, int limit = 20) =>
		(await memory.SearchEntriesAsync(Proj, Query(q, limit))).Hits.Select(h => h.Entry.Key).ToList();

	// ---- W5.1 global cross-store fusion ----

	[Fact]
	public async Task GlobalFusion_StrongHitInLateStore_OutranksWeakHitInEarlyStore()
	{
		// Lexical-only, decay+MMR off — pure fusion. The "early" store buries its target below
		// two stronger decoys (rank 2); the "late" store holds a single top-ranked hit (rank 0).
		// With per-store CONCAT (the old behaviour) the late hit would trail every early row; with
		// GLOBAL RRF fusion the rank-0 late hit outranks the rank-2 early "weak" one.
		var memory = new MemoryService(_store, llm: null, Off);
		await memory.CreateStoreAsync(Proj, "aaa_early", null);
		await memory.CreateStoreAsync(Proj, "zzz_late", null);
		await memory.UpsertAsync(Proj, "aaa_early",
		[
			Entry("e-decoy1", "kubernetes kubernetes kubernetes rollout"),
			Entry("e-decoy2", "kubernetes kubernetes upgrade"),
			Entry("e-weak", "a short note that mentions kubernetes once"),
		], []);
		await memory.UpsertAsync(Proj, "zzz_late", [Entry("l-strong", "kubernetes")], []);

		var keys = await SearchKeys(memory, "kubernetes");

		keys.Should().Contain("l-strong").And.Contain("e-weak");
		keys.IndexOf("l-strong").Should().BeLessThan(keys.IndexOf("e-weak"),
			"a rank-0 hit in a later store must beat a rank-2 hit in an earlier store under global fusion");
	}

	// ---- W5.3 freshness time-decay ----

	[Fact]
	public async Task Decay_FresherRanksAboveStalerAtComparableMatch()
	{
		// One store, two hits: "old-strong" matches more strongly (rank 0) but is 120 days old;
		// "new-weak" is rank 1 but fresh. With decay OFF the stronger lexical hit leads; with
		// decay ON the stale hit's score is halved four times and the fresher hit rises above it.
		async Task Seed(MemoryService m)
		{
			await m.CreateStoreAsync(Proj, "notes", null);
			await m.UpsertAsync(Proj, "notes",
			[
				Entry("old-strong", "alpha alpha alpha"),
				Entry("new-weak", "alpha only mentioned once here"),
			], []);
			// Backdate the strong hit far past the 30-day half-life.
			var ctx = _store.GetContext(Proj);
			ctx.Execute("UPDATE memory_entries SET Updated = @u WHERE Key = @k",
				new DataParameter("u", DateTime.UtcNow.AddDays(-120)),
				new DataParameter("k", "old-strong"));
		}

		var noDecay = new MemoryService(_store, llm: null, Off);
		await Seed(noDecay);
		var without = await SearchKeys(noDecay, "alpha");
		without.IndexOf("old-strong").Should().BeLessThan(without.IndexOf("new-weak"),
			"without decay the stronger lexical match leads");

		// Fresh store dir for the decay-on run so the backdate is re-applied cleanly.
		var withDecay = new MemoryService(_store, llm: null, new SearchRerankOptions
		{
			Recency = new RecencyOptions { Enabled = true, HalfLifeDays = 30 },
			Diversity = new DiversityOptions { Enabled = false },
		});
		await _store.DeleteAsync(Proj, "notes");
		await Seed(withDecay);
		var with = await SearchKeys(withDecay, "alpha");
		with.IndexOf("new-weak").Should().BeLessThan(with.IndexOf("old-strong"),
			"with decay the fresher hit overtakes the stale-but-stronger one");
	}

	// ---- W5.4 MMR diversification ----

	[Fact]
	public async Task Mmr_CollapsesNearDuplicates_OutOfTheHead()
	{
		// Scripted vectors give exact control: dup-a/dup-b embed to the SAME vector (near-dups),
		// distinct/filler to an orthogonal one. Lexical strength grades dup-a > dup-b > distinct >
		// filler so, by pure score, the two near-dups sit at the top-2. MMR must push the second
		// near-dup out of the head in favour of the novel "distinct".
		var llm = new ScriptedEmbedder();

		async Task<List<string>> Run(bool mmr)
		{
			await _store.DeleteAsync(Proj, "notes");
			var memory = new MemoryService(_store, llm, new SearchRerankOptions
			{
				Recency = new RecencyOptions { Enabled = false },
				Diversity = new DiversityOptions { Enabled = mmr, Lambda = 0.7 },
			});
			await memory.CreateStoreAsync(Proj, "notes", null);
			await memory.UpsertAsync(Proj, "notes",
			[
				Entry("dup-a", "portal portal portal AAA"),
				Entry("dup-b", "portal portal portal AAA"),
				Entry("distinct", "portal portal BBB"),
				Entry("filler", "portal BBB"),
			], []);
			await DrainVectors(llm, "notes");
			return await SearchKeys(memory, "portal");
		}

		var off = await Run(mmr: false);
		off.Take(2).Should().BeEquivalentTo(["dup-a", "dup-b"],
			"by pure score the two identical-vector hits occupy the head");

		var on = await Run(mmr: true);
		on.Take(2).Should().NotBeEquivalentTo(["dup-a", "dup-b"]);
		on.Take(2).Should().Contain("distinct", "MMR promotes the novel hit into the head");
	}

	[Fact]
	public async Task NoEmbedder_StillAnswers_AndMmrSilentlySkips()
	{
		// Without an embedder the semantic leg never runs (retrievers.Semantic false, not
		// degraded — it was never attempted) and MMR silently no-ops (no vectors to diversify);
		// the lexical answer still comes back in fused order.
		var memory = new MemoryService(_store, llm: null); // default rerank (MMR/decay enabled)
		await memory.CreateStoreAsync(Proj, "notes", null);
		await memory.UpsertAsync(Proj, "notes",
		[
			Entry("a", "alpha keyword one"),
			Entry("b", "alpha keyword two"),
		], []);

		var res = await memory.SearchEntriesAsync(Proj, Query("alpha"));

		res.Hits.Select(h => h.Entry.Key).Should().BeEquivalentTo(["a", "b"]);
		res.Retrievers!.Value.Lexical.Should().BeTrue();
		res.Retrievers!.Value.Semantic.Should().BeFalse();
		res.Retrievers!.Value.Degraded.Should().BeFalse();
	}

	// ---- per-row retriever provenance + vector-as-peer selection (search-leg-classification) ----

	[Fact]
	public async Task Retriever_LabelsLexicalConfirmedVsSemanticOnly()
	{
		// "lex" carries the query token (lexically confirmed); "sem" only sits on the query vector
		// (AAA → [1,0], the query embeds to [1,0] too) without the token, so the vector leg alone
		// surfaces it. No floor: the vector-only hit enters as a peer (decay/MMR off).
		var llm = new ScriptedEmbedder();
		var memory = new MemoryService(_store, llm, Off);
		await memory.CreateStoreAsync(Proj, "notes", null);
		await memory.UpsertAsync(Proj, "notes",
		[
			Entry("lex", "portal keyword here"),
			Entry("sem", "AAA unrelated words"),
		], []);
		await DrainVectors(llm, "notes");

		var hits = (await memory.SearchEntriesAsync(Proj, Query("portal"))).Hits;
		hits.Single(h => h.Entry.Key == "lex").Retriever.Should().Be("lexical");
		hits.Single(h => h.Entry.Key == "sem").Retriever.Should().Be("semantic");
	}

	[Fact]
	public async Task VectorOnlyCandidates_EnterAsPeers_NoSemanticFloor()
	{
		// One lexically-confirmed entry + eight semantic-only entries (all embed to [1,0], on the
		// query vector, but only "lex" carries the token). The tau/SemanticFloor membership
		// threshold is REJECTED (spec: search-leg-classification): under a relevance selection the
		// vector leg selects as a PEER, so EVERY vector-only candidate enters — bounded only by the
		// limit, never cut by a cosine/RRF floor.
		var llm = new ScriptedEmbedder();
		var memory = new MemoryService(_store, llm, Off);
		await memory.CreateStoreAsync(Proj, "notes", null);
		var entries = new List<MemoryEntryInput> { Entry("lex", "portal lexical anchor") };
		for (var i = 0; i < 8; i++) entries.Add(Entry($"sem-{i}", $"AAA filler {i}"));
		await memory.UpsertAsync(Proj, "notes", entries, []);
		await DrainVectors(llm, "notes");

		var hits = (await memory.SearchEntriesAsync(Proj, Query("portal", limit: 50))).Hits;
		var keys = hits.Select(h => h.Entry.Key).ToList();

		keys.Should().Contain("lex");
		// No floor: all eight vector-only peers enter (the limit, not a threshold, is the only bound).
		keys.Count(k => k.StartsWith("sem-")).Should().Be(8);
		hits.Where(h => h.Entry.Key.StartsWith("sem-")).Should().OnlyContain(h => h.Retriever == "semantic");
	}

	// Vectors are materialized OFF the write path — drain them with the SAME embedder the query
	// path uses (mirrors MemoryHybridSearchTests / MemoryVectorizationJob for one store).
	async Task DrainVectors(ILlmClient llm, string store)
	{
		DataConnection Connect() => _factory.NewEnsuredConnection(Proj);
		var target = new VectorSearchIndex(Connect, new LlmClientEmbedder(llm, Proj));
		var source = new MemorySearchSource(Connect, Proj, store);
		var cursor = new SqliteIndexCursorStore(Connect);
		var worker = new AsyncVectorizationWorker(MemoryCursors.Vector(store), source, target, cursor);
		await worker.DrainAsync();
	}

	// Deterministic scripted embedder: a body carrying "BBB" embeds orthogonally to one carrying
	// "AAA" (and to the query), so identical-body hits share a vector (near-dups) while the "BBB"
	// hits are the novel cluster. Dim 2, stable model — no hash randomness (unlike FakeLlmClient).
	sealed class ScriptedEmbedder : ILlmClient
	{
		public Task<EmbedResult> EmbedAsync(string projectKey, EmbedRequest request, CancellationToken ct = default)
		{
			var vectors = request.Inputs.Select(Vec).ToList();
			return Task.FromResult(new EmbedResult(vectors, new ModelIdentity("scripted-v1", 2),
				new ServedBy("scripted", "scripted-v1", 1, Degraded: false)));
		}

		static float[] Vec(string text) => text.Contains("BBB") ? [0f, 1f] : [1f, 0f];

		public Task<RerankResult> RerankAsync(string projectKey, RerankRequest request, CancellationToken ct = default) =>
			throw new NotSupportedException();
		public Task<ChatResult> ChatAsync(string projectKey, ChatRequest request, CancellationToken ct = default) =>
			throw new NotSupportedException();
		public Task<bool> IsAvailableAsync(string projectKey, LlmCapability capability, CancellationToken ct = default) =>
			Task.FromResult(true);
	}
}
