using System.Security.Cryptography;
using System.Text;
using LinqToDB;
using LinqToDB.Data;
using LinqToDB.DataProvider.DuckDB;
using LinqToDB.Mapping;
using Microsoft.Extensions.Logging;
using PetBox.Core.Data;
using PetBox.Core.Search;
using PetBox.LlmRouter.Contract;
using PetBox.Sessions.Contract;
using PetBox.Sessions.Data;

namespace PetBox.Sessions.Episodic;

// The episodic tier of session search: a TRANSIENT per-session index, hydrated on
// demand from the session store and aged out by idleness (spec: session-episodic-lazy).
// Measurements killed a global always-on transcript index (1-core prod box); lazy
// hydration is ~100ms per session and only for sessions a search actually points at.
//
// Each hydration builds two legs over the session's messages and fuses them through the
// standard SearchService (RRF + provenance, spec: search-provenance):
//   lexical  — in-memory DuckDB FTS with snowball stemmer='russian' + BM25: the decisive
//              recall win over SQLite prefix-FTS on Russian wordforms (m-9ea972b6);
//   semantic — brute-force cosine over MRL-1024 message embeddings (no ANN at this
//              scale), skipped silently when no embedder is available.
// Either leg failing degrades the answer honestly instead of failing the search.
//
// Message embeddings are paid ONCE, not per hydration: they materialize lazily on the
// first semantic query (whose embed call reveals the model identity) and persist in the
// session store's message_vec cache keyed by (sessionId, ordinal) with a content hash —
// re-hydrating a cold session reads vectors from disk; only changed/new ordinals (or a
// swapped embedder model) re-embed.
public sealed class DuckDbSessionEpisodicIndex : ISessionEpisodicIndex, IDisposable
{
	public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(10);
	public const int DefaultMaxHydrated = 8;

	internal const int VectorDim = 1024;
	internal const int EmbedCharCap = 2000;
	internal const int EmbedBatchSize = 64;
	internal const int SnippetLength = 240;

	readonly IScopedDbFactory<SessionsDb> _factory;
	readonly SessionStore _store;
	readonly ILlmClient? _llm;
	readonly ILogger<DuckDbSessionEpisodicIndex>? _logger;
	readonly TimeSpan _ttl;
	readonly int _maxHydrated;
	readonly TimeProvider _time;
	readonly SessionEpisodicOptions _options;

	readonly Dictionary<string, Hydrated> _cache = new(StringComparer.Ordinal);
	// One gate serializes hydration AND queries: a DuckDB connection is not thread-safe,
	// and queries are ~17ms — contention is cheaper than per-entry locking.
	readonly SemaphoreSlim _gate = new(1, 1);

	public DuckDbSessionEpisodicIndex(IScopedDbFactory<SessionsDb> factory, ILlmClient? llm = null,
		ILogger<DuckDbSessionEpisodicIndex>? logger = null, TimeSpan? ttl = null,
		int maxHydrated = DefaultMaxHydrated, TimeProvider? time = null,
		SessionEpisodicOptions? options = null)
	{
		_factory = factory;
		_store = new SessionStore(factory);
		_llm = llm;
		_logger = logger;
		_ttl = ttl ?? DefaultTtl;
		_maxHydrated = maxHydrated;
		_time = time ?? TimeProvider.System;
		_options = options ?? new SessionEpisodicOptions();
	}

	public async Task<SessionEpisodicResult?> SearchAsync(string projectKey, string sessionId, string query, int k, CancellationToken ct = default)
	{
		if (k <= 0) k = 10;
		await _gate.WaitAsync(ct);
		try
		{
			EvictIdleLocked();

			// Header-only staleness check — never decompress the blob for a warm hit.
			var header = (await _store.ListAsync(projectKey, ct)).FirstOrDefault(h => h.SessionId == sessionId);
			var key = projectKey + "\x1f" + sessionId;
			if (header is null)
			{
				if (_cache.Remove(key, out var gone)) gone.Dispose();
				return null;
			}

			if (!_cache.TryGetValue(key, out var entry) || entry.SessionVersion != header.Version)
			{
				if (_cache.Remove(key, out var stale)) stale.Dispose();
				var snap = await _store.GetAsync(projectKey, sessionId, ct);
				if (snap is null) return null;
				entry = Hydrate(projectKey, snap);
				_cache[key] = entry;
				// Honor the cap immediately — the fresh entry is the most recent, so the
				// LRU trim hits an older hydration, never this one.
				EvictIdleLocked();
			}

			entry.LastAccess = _time.GetUtcNow().UtcDateTime;
			return await QueryAsync(entry, projectKey, query, k, ct);
		}
		finally
		{
			_gate.Release();
		}
	}

	public int EvictIdle()
	{
		_gate.Wait();
		try { return EvictIdleLocked(); }
		finally { _gate.Release(); }
	}

	public void Dispose()
	{
		_gate.Wait();
		try
		{
			foreach (var entry in _cache.Values) entry.Dispose();
			_cache.Clear();
		}
		finally { _gate.Release(); }
		_gate.Dispose();
	}

	int EvictIdleLocked()
	{
		var now = _time.GetUtcNow().UtcDateTime;
		var victims = _cache.Where(p => now - p.Value.LastAccess > _ttl).Select(p => p.Key).ToList();
		// Over capacity → drop the least-recently used beyond the cap (RAM is the budget).
		if (_cache.Count - victims.Count > _maxHydrated)
			victims.AddRange(_cache.Where(p => !victims.Contains(p.Key))
				.OrderBy(p => p.Value.LastAccess)
				.Take(_cache.Count - victims.Count - _maxHydrated)
				.Select(p => p.Key));
		foreach (var key in victims)
			if (_cache.Remove(key, out var entry))
				entry.Dispose();
		return victims.Count;
	}

	// Hydration is network-free: only the DuckDB FTS is built here. Vectors come from the
	// persistent cache (or a lazy embed) on the first semantic query.
	Hydrated Hydrate(string projectKey, SessionSnapshot snap)
	{
		var entry = new Hydrated(snap.SessionId, snap.Version, snap.Messages)
		{
			LastAccess = _time.GetUtcNow().UtcDateTime,
		};

		try
		{
			entry.Fts = BuildFts(snap.Messages);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			// The fts extension may be unloadable (offline box, missing native lib) — the
			// semantic leg and a degraded answer beat a failed search.
			_logger?.LogWarning(ex, "episodic FTS hydration failed for {Project}/{Session}; lexical leg off",
				projectKey, snap.SessionId);
		}
		return entry;
	}

	[Table("messages")]
	sealed class MessageRow
	{
		[Column] public long Version { get; set; }
		[Column] public string Content { get; set; } = "";
	}

	static DataConnection BuildFts(IReadOnlyList<SessionMessage> messages)
	{
		// DuckDBTools.CreateDataConnection opens the underlying connection lazily and keeps
		// it open until Dispose — the ":memory:" DB's content survives across the separate
		// Execute/Insert/FromSql calls below exactly like the raw DuckDBConnection field did
		// (verified by DuckDbLinq2DbFtsProbeTests, the migration spike).
		var db = DuckDBTools.CreateDataConnection("DataSource=:memory:");
		try
		{
			db.Execute("INSTALL fts; LOAD fts;");
			db.Execute("CREATE TABLE messages (version BIGINT PRIMARY KEY, content VARCHAR)");
			using (db.BeginTransaction())
			{
				foreach (var m in messages)
					db.Insert(new MessageRow { Version = m.Version, Content = m.Content });
				db.CommitTransaction();
			}
			// The whole point of DuckDB here: snowball stemming makes Russian wordforms
			// match (запустили ~ запустила); SQLite FTS5 has no russian stemmer.
			db.Execute("PRAGMA create_fts_index('messages', 'version', 'content', stemmer='russian')");
			return db;
		}
		catch
		{
			db.Dispose();
			throw;
		}
	}

	async Task<SessionEpisodicResult> QueryAsync(Hydrated entry, string projectKey, string query, int k, CancellationToken ct)
	{
		var indexes = new List<ISearchIndex>();
		if (entry.Fts is not null)
			indexes.Add(new FtsLeg(entry.Fts));
		if (_llm is not null && await _llm.IsAvailableAsync(projectKey, LlmCapability.Embed, ct))
			indexes.Add(new VectorLeg(entry, q => EnsureVectorsAndEmbedQueryAsync(projectKey, entry, q, ct)));

		// OVER-FETCH, then floor, then cut to k: fetching only k and then dropping semantic
		// noise would return SHORT. A pool of max(3k, 20) (naturally bounded by the session's
		// message count) leaves substantive hits to refill the quota after the floor trims the
		// junk. The floor mirrors stage-1 discovery (RankDiscovery, spec: search-fair-fusion).
		var pool = Math.Max(3 * k, 20);
		var resp = await new SearchService(indexes).SearchAsync(projectKey, query, new SearchFilter(null), pool, ct);

		var byVersion = entry.Messages.ToDictionary(m => m.Version);
		var hits = new List<SessionEpisodicHit>(k);
		foreach (var h in resp.Hits)
		{
			if (hits.Count >= k) break;
			// W5 semantic floor: drop a hit surfaced by the semantic leg ALONE whose fused RRF
			// relevance is below the floor. The lexical index is registered FIRST, so — exactly
			// as MemoryService.SearchStoreAsync documents — Retriever=="lexical" ⟺ lexically
			// confirmed (never floored, the lexical leg vouched for it), "semantic" ⟺ vector-only
			// (unconfirmed, a caller may floor it). This only trims the weak tail: rank-0 junk is
			// already kept OUT of the semantic candidate set upstream (MinSemanticChars), because
			// its 1/60 ≈ 0.0167 fused score would clear any sane floor.
			if (_options.SemanticFloor > 0 && h.Retriever != "lexical" && h.Score < _options.SemanticFloor) continue;
			if (!long.TryParse(h.Id, out var ordinal) || !byVersion.TryGetValue(ordinal, out var msg)) continue;
			hits.Add(new SessionEpisodicHit(ordinal, msg.Role, Snippet(msg.Content, query), h.Score, h.Retriever));
		}

		// A leg that could not even hydrate never reached SearchService — fold it into the
		// degraded flag so the caller can tell a full hybrid answer from a partial one.
		var degraded = resp.Retrievers.Degraded || entry.Fts is null;
		return new SessionEpisodicResult(hits, resp.Retrievers with { Degraded = degraded });
	}

	// The semantic leg's one network path: embed the query (which reveals the embedder's
	// model identity), then make sure the entry's message vectors exist FOR THAT MODEL —
	// from the persistent message_vec cache where the content hash still matches, embedding
	// and persisting only the misses. A failure here surfaces to SearchService, which
	// degrades the answer instead of failing the search.
	async Task<float[]> EnsureVectorsAndEmbedQueryAsync(string projectKey, Hydrated entry, string query, CancellationToken ct)
	{
		var embedder = new LlmClientEmbedder(_llm!, projectKey);
		var qb = await embedder.EmbedAsync([query], ct);
		var queryVec = Truncate(qb.Vectors[0], VectorDim);
		// Comparability guard = the query's (model, truncated dim) — message vectors must
		// match BOTH, or cosine compares apples to oranges.
		await EnsureMessageVectorsAsync(projectKey, entry, qb.Model, queryVec.Length, embedder, ct);
		return queryVec;
	}

	async Task EnsureMessageVectorsAsync(string projectKey, Hydrated entry, string model, int dim,
		LlmClientEmbedder embedder, CancellationToken ct)
	{
		if (entry.Vectors is not null && entry.VecModel == model) return;

		using var db = _factory.NewConnection(projectKey);
		var cached = db.MessageVectors
			.Where(v => v.SessionId == entry.SessionId)
			.ToDictionary(v => v.Version);

		var vectors = new float[entry.Messages.Count][];
		var missing = new List<int>();
		for (var i = 0; i < entry.Messages.Count; i++)
		{
			var message = entry.Messages[i];
			// Junk exclusion from the SEMANTIC leg only: a message too short to carry meaning
			// ("```", "Записано.", "No response requested.") is left as a null vector — not
			// embedded (no wasted embed call / message_vec cache row) and filtered from the
			// cosine candidates. These nulls are STABLE across the entry.Vectors memoization
			// (the same indices are skipped every time). The FTS leg keeps indexing it — a
			// lexical token match on a short message is legitimate and must never be dropped.
			if (_options.MinSemanticChars > 0 && message.Content.Trim().Length < _options.MinSemanticChars)
				continue;
			if (cached.TryGetValue(message.Version, out var row)
				&& row.Model == model && row.Dim == dim && row.Hash == ContentHash(message.Content))
				vectors[i] = VectorCodec.Decode(row.Vec);
			else
				missing.Add(i);
		}

		for (var at = 0; at < missing.Count; at += EmbedBatchSize)
		{
			var batch = missing.Skip(at).Take(EmbedBatchSize).ToList();
			var texts = batch.Select(i =>
			{
				var content = entry.Messages[i].Content;
				return content.Length > EmbedCharCap ? content[..EmbedCharCap] : content;
			}).ToList();
			var res = await embedder.EmbedAsync(texts, ct);
			for (var j = 0; j < batch.Count; j++)
			{
				var i = batch[j];
				var vec = Truncate(res.Vectors[j], VectorDim);
				vectors[i] = vec;
				await db.InsertOrReplaceAsync(new MessageVec
				{
					SessionId = entry.SessionId,
					Version = entry.Messages[i].Version,
					Hash = ContentHash(entry.Messages[i].Content),
					Model = res.Model,
					Dim = vec.Length,
					Vec = VectorCodec.Encode(vec),
				}, token: ct);
			}
		}

		entry.Vectors = vectors;
		entry.VecModel = model;
	}

	static string ContentHash(string content) =>
		Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

	// MRL truncation, same rule as VectorSearchIndex: the leading components of a
	// Matryoshka embedding are a valid lower-dim embedding; cosine renormalizes.
	static float[] Truncate(float[] v, int dim) => dim > 0 && dim < v.Length ? v[..dim] : v;

	// A display snippet around the first query-token occurrence (or the head of the
	// message for purely semantic hits).
	internal static string Snippet(string content, string query)
	{
		var at = -1;
		foreach (var token in query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			at = content.IndexOf(token, StringComparison.OrdinalIgnoreCase);
			if (at >= 0) break;
		}
		var start = at < 0 ? 0 : Math.Max(0, at - SnippetLength / 3);
		var len = Math.Min(SnippetLength, content.Length - start);
		var snippet = content.Substring(start, len);
		if (start > 0) snippet = "…" + snippet;
		if (start + len < content.Length) snippet += "…";
		return snippet;
	}

	sealed class Hydrated(string sessionId, long sessionVersion, IReadOnlyList<SessionMessage> messages) : IDisposable
	{
		public string SessionId { get; } = sessionId;
		public long SessionVersion { get; } = sessionVersion;
		public IReadOnlyList<SessionMessage> Messages { get; } = messages;
		public DataConnection? Fts { get; set; }
		// Materialized lazily by the first semantic query (per embedder model); backed by
		// the persistent message_vec cache, so a re-hydration rarely re-embeds.
		public float[][]? Vectors { get; set; }
		public string? VecModel { get; set; }
		public DateTime LastAccess { get; set; }

		public void Dispose() => Fts?.Dispose();
	}

	// The lexical leg as a standard ISearchIndex so SearchService fuses + reports
	// provenance exactly like every other index. Read-only: writes never come through
	// the facade for an Eventual index.
	sealed class FtsLeg(DataConnection db) : ISearchIndex
	{
		public SearchConsistency ConsistencyClass => SearchConsistency.Eventual;
		public SearchCapability Capability => SearchCapability.Lexical;

		public Task IndexAsync(DataConnection? tx, SearchDoc doc, CancellationToken ct = default) =>
			throw new NotSupportedException("episodic index is hydrated, not written");
		public Task DeleteAsync(DataConnection? tx, string scope, string type, string id, CancellationToken ct = default) =>
			throw new NotSupportedException("episodic index is hydrated, not written");
		public Task DeleteByTypeAsync(DataConnection? tx, string scope, string type, CancellationToken ct = default) =>
			throw new NotSupportedException("episodic index is hydrated, not written");

		sealed class BmRow
		{
			public long Version { get; set; }
			public double Score { get; set; }
		}

		public Task<IReadOnlyList<Hit>> SearchAsync(string scope, string query, SearchFilter filter, int k, CancellationToken ct = default)
		{
			// match_bm25/create_fts_index are DuckDB-specific SQL the linq2db expression tree
			// cannot model — hand-written SQL text run THROUGH linq2db (FromSql, interpolated
			// values become DbParameters), not raw ADO.
			var rows = db.FromSql<BmRow>($"""
				SELECT version, score FROM (
					SELECT version, fts_main_messages.match_bm25(version, {query}) AS score FROM messages
				) WHERE score IS NOT NULL ORDER BY score DESC LIMIT {k}
				""").ToList();
			var hits = rows.Select(r => new Hit("message", r.Version.ToString(), r.Score, "lexical")).ToList();
			return Task.FromResult<IReadOnlyList<Hit>>(hits);
		}
	}

	// The semantic leg: brute-force cosine over the (lazily materialized) message vectors.
	// `ensureAndEmbedQuery` embeds the query AND guarantees entry.Vectors exist for the
	// query's model before returning.
	sealed class VectorLeg(Hydrated entry, Func<string, Task<float[]>> ensureAndEmbedQuery) : ISearchIndex
	{
		public SearchConsistency ConsistencyClass => SearchConsistency.Eventual;
		public SearchCapability Capability => SearchCapability.Vector;

		public Task IndexAsync(DataConnection? tx, SearchDoc doc, CancellationToken ct = default) =>
			throw new NotSupportedException("episodic index is hydrated, not written");
		public Task DeleteAsync(DataConnection? tx, string scope, string type, string id, CancellationToken ct = default) =>
			throw new NotSupportedException("episodic index is hydrated, not written");
		public Task DeleteByTypeAsync(DataConnection? tx, string scope, string type, CancellationToken ct = default) =>
			throw new NotSupportedException("episodic index is hydrated, not written");

		public async Task<IReadOnlyList<Hit>> SearchAsync(string scope, string query, SearchFilter filter, int k, CancellationToken ct = default)
		{
			var q = await ensureAndEmbedQuery(query);
			// Skip messages excluded from the semantic leg (null vector = junk kept OUT by
			// EnsureMessageVectorsAsync) before cosine — they were never embedded.
			var candidates = entry.Messages
				.Select((m, i) => (Key: m.Version.ToString(), Vec: entry.Vectors![i]))
				.Where(c => c.Vec is not null)
				.Select(c => (c.Key, Vec: c.Vec!));
			return VectorMath.TopK(q, candidates, k)
				.Select(t => new Hit("message", t.Key, t.Score, "semantic"))
				.ToList();
		}
	}
}
