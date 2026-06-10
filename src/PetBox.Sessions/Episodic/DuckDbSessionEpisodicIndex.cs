using DuckDB.NET.Data;
using LinqToDB.Data;
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

	readonly Dictionary<string, Hydrated> _cache = new(StringComparer.Ordinal);
	// One gate serializes hydration AND queries: a DuckDB connection is not thread-safe,
	// and queries are ~17ms — contention is cheaper than per-entry locking.
	readonly SemaphoreSlim _gate = new(1, 1);

	public DuckDbSessionEpisodicIndex(IScopedDbFactory<SessionsDb> factory, ILlmClient? llm = null,
		ILogger<DuckDbSessionEpisodicIndex>? logger = null, TimeSpan? ttl = null,
		int maxHydrated = DefaultMaxHydrated, TimeProvider? time = null)
	{
		_factory = factory;
		_store = new SessionStore(factory);
		_llm = llm;
		_logger = logger;
		_ttl = ttl ?? DefaultTtl;
		_maxHydrated = maxHydrated;
		_time = time ?? TimeProvider.System;
	}

	public async Task<SessionEpisodicResult?> SearchAsync(string projectKey, string sessionId, string query, int k, CancellationToken ct = default)
	{
		if (k <= 0) k = 10;
		await _gate.WaitAsync(ct);
		try
		{
			EvictIdleLocked();

			// GetDb first: the store reads must see a migrated schema even if this file
			// was last opened before a migration (reference: NewConnection ≠ migrations).
			_factory.GetDb(projectKey);

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
				entry = await HydrateAsync(projectKey, snap, ct);
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

	async Task<Hydrated> HydrateAsync(string projectKey, SessionSnapshot snap, CancellationToken ct)
	{
		var entry = new Hydrated(snap.Version, snap.Messages)
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

		if (_llm is not null && await _llm.IsAvailableAsync(projectKey, LlmCapability.Embed, ct))
		{
			try
			{
				entry.Vectors = await EmbedMessagesAsync(projectKey, snap.Messages, ct);
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				entry.EmbedFailed = true;
				_logger?.LogWarning(ex, "episodic embed hydration failed for {Project}/{Session}; semantic leg off",
					projectKey, snap.SessionId);
			}
		}
		return entry;
	}

	static DuckDBConnection BuildFts(IReadOnlyList<SessionMessage> messages)
	{
		var db = new DuckDBConnection("DataSource=:memory:");
		try
		{
			db.Open();
			Execute(db, "INSTALL fts; LOAD fts;");
			Execute(db, "CREATE TABLE messages (version BIGINT PRIMARY KEY, content VARCHAR)");
			using (var tx = db.BeginTransaction())
			{
				using var insert = db.CreateCommand();
				insert.Transaction = tx;
				insert.CommandText = "INSERT INTO messages VALUES (?, ?)";
				var pVersion = new DuckDBParameter();
				var pContent = new DuckDBParameter();
				insert.Parameters.Add(pVersion);
				insert.Parameters.Add(pContent);
				foreach (var m in messages)
				{
					pVersion.Value = m.Version;
					pContent.Value = m.Content;
					insert.ExecuteNonQuery();
				}
				tx.Commit();
			}
			// The whole point of DuckDB here: snowball stemming makes Russian wordforms
			// match (запустили ~ запустила); SQLite FTS5 has no russian stemmer.
			Execute(db, "PRAGMA create_fts_index('messages', 'version', 'content', stemmer='russian')");
			return db;
		}
		catch
		{
			db.Dispose();
			throw;
		}
	}

	async Task<float[][]> EmbedMessagesAsync(string projectKey, IReadOnlyList<SessionMessage> messages, CancellationToken ct)
	{
		var embedder = new LlmClientEmbedder(_llm!, projectKey);
		var vectors = new float[messages.Count][];
		for (var i = 0; i < messages.Count; i += EmbedBatchSize)
		{
			var batch = messages.Skip(i).Take(EmbedBatchSize)
				.Select(m => m.Content.Length > EmbedCharCap ? m.Content[..EmbedCharCap] : m.Content)
				.ToList();
			var res = await embedder.EmbedAsync(batch, ct);
			for (var j = 0; j < batch.Count; j++)
				vectors[i + j] = Truncate(res.Vectors[j], VectorDim);
		}
		return vectors;
	}

	async Task<SessionEpisodicResult> QueryAsync(Hydrated entry, string projectKey, string query, int k, CancellationToken ct)
	{
		var indexes = new List<ISearchIndex>();
		if (entry.Fts is not null)
			indexes.Add(new FtsLeg(entry.Fts));
		if (entry.Vectors is not null)
			indexes.Add(new VectorLeg(entry, q => EmbedQueryAsync(projectKey, q, ct)));

		var resp = await new SearchService(indexes).SearchAsync(projectKey, query, new SearchFilter(null), k, ct);

		var byVersion = entry.Messages.ToDictionary(m => m.Version);
		var hits = new List<SessionEpisodicHit>(resp.Hits.Count);
		foreach (var h in resp.Hits)
		{
			if (!long.TryParse(h.Id, out var ordinal) || !byVersion.TryGetValue(ordinal, out var msg)) continue;
			hits.Add(new SessionEpisodicHit(ordinal, msg.Role, Snippet(msg.Content, query), h.Score, h.Retriever));
		}

		// A leg that could not even hydrate never reached SearchService — fold it into the
		// degraded flag so the caller can tell a full hybrid answer from a partial one.
		var degraded = resp.Retrievers.Degraded || entry.Fts is null || entry.EmbedFailed;
		return new SessionEpisodicResult(hits, resp.Retrievers with { Degraded = degraded });
	}

	async Task<float[]> EmbedQueryAsync(string projectKey, string query, CancellationToken ct)
	{
		var res = await new LlmClientEmbedder(_llm!, projectKey).EmbedAsync([query], ct);
		return Truncate(res.Vectors[0], VectorDim);
	}

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

	static void Execute(DuckDBConnection db, string sql)
	{
		using var cmd = db.CreateCommand();
		cmd.CommandText = sql;
		cmd.ExecuteNonQuery();
	}

	sealed class Hydrated(long sessionVersion, IReadOnlyList<SessionMessage> messages) : IDisposable
	{
		public long SessionVersion { get; } = sessionVersion;
		public IReadOnlyList<SessionMessage> Messages { get; } = messages;
		public DuckDBConnection? Fts { get; set; }
		public float[][]? Vectors { get; set; }
		public bool EmbedFailed { get; set; }
		public DateTime LastAccess { get; set; }

		public void Dispose() => Fts?.Dispose();
	}

	// The lexical leg as a standard ISearchIndex so SearchService fuses + reports
	// provenance exactly like every other index. Read-only: writes never come through
	// the facade for an Eventual index.
	sealed class FtsLeg(DuckDBConnection db) : ISearchIndex
	{
		public SearchConsistency ConsistencyClass => SearchConsistency.Eventual;
		public SearchCapability Capability => SearchCapability.Lexical;

		public Task IndexAsync(DataConnection? tx, SearchDoc doc, CancellationToken ct = default) =>
			throw new NotSupportedException("episodic index is hydrated, not written");
		public Task DeleteAsync(DataConnection? tx, string scope, string type, string id, CancellationToken ct = default) =>
			throw new NotSupportedException("episodic index is hydrated, not written");

		public Task<IReadOnlyList<Hit>> SearchAsync(string scope, string query, SearchFilter filter, int k, CancellationToken ct = default)
		{
			using var cmd = db.CreateCommand();
			cmd.CommandText = """
				SELECT version, score FROM (
					SELECT version, fts_main_messages.match_bm25(version, ?) AS score FROM messages
				) WHERE score IS NOT NULL ORDER BY score DESC LIMIT ?
				""";
			cmd.Parameters.Add(new DuckDBParameter { Value = query });
			cmd.Parameters.Add(new DuckDBParameter { Value = k });
			var hits = new List<Hit>();
			using var reader = cmd.ExecuteReader();
			while (reader.Read())
				hits.Add(new Hit("message", reader.GetInt64(0).ToString(), reader.GetDouble(1), "lexical"));
			return Task.FromResult<IReadOnlyList<Hit>>(hits);
		}
	}

	// The semantic leg: brute-force cosine over the hydrated message vectors.
	sealed class VectorLeg(Hydrated entry, Func<string, Task<float[]>> embedQuery) : ISearchIndex
	{
		public SearchConsistency ConsistencyClass => SearchConsistency.Eventual;
		public SearchCapability Capability => SearchCapability.Vector;

		public Task IndexAsync(DataConnection? tx, SearchDoc doc, CancellationToken ct = default) =>
			throw new NotSupportedException("episodic index is hydrated, not written");
		public Task DeleteAsync(DataConnection? tx, string scope, string type, string id, CancellationToken ct = default) =>
			throw new NotSupportedException("episodic index is hydrated, not written");

		public async Task<IReadOnlyList<Hit>> SearchAsync(string scope, string query, SearchFilter filter, int k, CancellationToken ct = default)
		{
			var q = await embedQuery(query);
			var candidates = entry.Messages.Select((m, i) => (Key: m.Version.ToString(), Vec: entry.Vectors![i]));
			return VectorMath.TopK(q, candidates, k)
				.Select(t => new Hit("message", t.Key, t.Score, "semantic"))
				.ToList();
		}
	}
}
