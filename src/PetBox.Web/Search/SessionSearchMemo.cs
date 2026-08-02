using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Hybrid;
using PetBox.Core.Contract;
using PetBox.Core.Search;
using PetBox.Sessions.Contract;

namespace PetBox.Web.Search;

// The session-search OUTCOME memo (card: ui-search-render-memoized) — the answer to "we search
// slowly, but coming BACK has to be fast". It keeps a whole SessionSearchOutcome, EPISODIC CONTENT
// INCLUDED, so re-opening the same search URL (or hitting Back) replays it instead of paying the
// pipeline again.
//
// ── WHY THIS IS A SECOND LAYER AND NOT A WIDENING OF SearchPoolCache ─────────────────────────────
// SearchPoolCache stores ADDRESSES ONLY, on purpose (SearchPool.cs:20-22): a stored pool may go
// stale in ORDER — which its data-version key then refuses — but never in CONTENT, because content
// is re-read live on every hydration. That invariant is load-bearing for memory and tasks and is NOT
// relaxed here; this class does not touch it, extend it, or share its storage keys.
//
// Sessions could not be fixed inside it anyway. Measured on live prod (2026-08-02, MCP session_search,
// Precision), cost is LINEAR in the number of sessions hydrated — ~5.9s of discovery plus ~2.7s per
// session (n=1/5/10/30 → 8.6/18.2/32.0/86.4s), and the UI's default `size` hydrates 30. The expensive
// product IS the episodic content, so an address-only cache structurally cannot reach the "hundreds
// of milliseconds" the card asks for. Keeping the two layers separate is what lets this one cache
// content without weakening the one that must not.
//
// ── FRESHNESS IS TTL, DELIBERATELY — THERE IS NO DATA VERSION IN THIS KEY ────────────────────────
// The owner's ruling, and the reasoning is specific to sessions rather than a shortcut. Every other
// searchable container here has a cheap scalar change stamp (ITasksService.GetBoardChangeStampAsync,
// MemoryService.ChangeStampAsync) and both pool caches key on it. Sessions have none: the discovery
// legs span the digest memory store, ISessionTermIndex (which exposes no stamp at all) and the
// session headers, and SessionSearchOutcome.DataVersion is computed FROM the finished discovery
// order (SessionSearchService.cs) — knowable only after paying the very cost this memo exists to
// avoid.
//
// A composite stamp was considered and REJECTED, not merely skipped. Sessions are appended to
// continuously, so such a stamp would move on nearly every request: entries would be invalidated
// faster than they could ever be read back, and the memo would buy nothing while costing extra
// queries. TTL also happens to be the semantics actually wanted here — a search result is a SNAPSHOT
// of a moment, and re-opening the same URL should show the moment it was taken, not silently
// re-rank underneath the reader. What this buys is bounded and stated: a session written now can go
// unseen by an identical repeat query for at most Ttl.
public sealed class SessionSearchMemo
{
	// The stored payload's SHAPE version — same role, same rules as SearchPoolCache's own. It rides
	// in the KEY (so an entry from another shape is never fetched and ages out on its own TTL) AND in
	// the PAYLOAD (so anything that does arrive is checked before it is trusted). Bump it whenever the
	// wire records at the bottom of this file change.
	const int PayloadFormatVersion = 1;

	// 15 minutes — taken from SearchPoolCache.DefaultTtl rather than chosen independently, because
	// this rides the SAME SqliteDistributedCache file and the SAME background sweep that reclaims it.
	// A second, differently-tuned expiry over one storage layer is a knob whose only reachable
	// contribution is to disagree with the first. It is a SPACE bound: correctness here is the TTL
	// itself (see the class note), so the number decides how long a repeat stays cheap and how stale a
	// repeat may be — one window, stated once.
	static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(15);

	TimeSpan Ttl { get; }

	// Null ONLY in the Disabled instance below.
	readonly HybridCache? _cache;
	readonly ILogger<SessionSearchMemo>? _log;
	readonly HybridCacheEntryOptions _entryOptions;

	// Diagnostics, and the only way the card's "a repeat runs no second search" claim is OBSERVABLE
	// rather than asserted — the tests read these instead of trusting a stopwatch.
	public long Stores => Interlocked.Read(ref _stores);
	public long Hits => Interlocked.Read(ref _hits);
	public long Misses => Interlocked.Read(ref _misses);

	long _stores, _hits, _misses;

	// Bumped by Invalidate(); prefixed into every key. Same test seam, and same honesty about being
	// one, as SearchPoolCache._generation: several tests need the state "the entry is gone, so this
	// call must recompute", and on a disk cache that survives a restart there is no other way to reach
	// it. Waiting out a TTL is not a test.
	int _generation;

	public SessionSearchMemo(HybridCache cache, ILogger<SessionSearchMemo>? log = null, TimeSpan? ttl = null)
	{
		_cache = cache ?? throw new ArgumentNullException(nameof(cache));
		_log = log;
		Ttl = ttl ?? DefaultTtl;
		_entryOptions = new HybridCacheEntryOptions
		{
			Expiration = Ttl,
			// L1 OFF in both directions, for the same reason SearchPoolCache disables it and with more
			// at stake: an entry here carries episodic SNIPPETS, not addresses, so a hot query under
			// many tabs would otherwise pin megabytes of transcript text in process memory for a full
			// TTL. What HybridCache is wanted for is the OTHER thing it does — single-flighting
			// concurrent misses, so N simultaneous cold renders run ONE search rather than N.
			Flags = HybridCacheEntryFlags.DisableLocalCacheRead | HybridCacheEntryFlags.DisableLocalCacheWrite,
		};
	}

	SessionSearchMemo()
	{
		Ttl = DefaultTtl;
		_entryOptions = new HybridCacheEntryOptions();
	}

	// The explicit "no memo is wired here" instance — every search runs, nothing is stored. Mirrors
	// SearchPoolCache.Disabled and exists for the same reason: a host that forgot the registration
	// should be visibly uncached BY NAME at the call site, never quietly slow behind a private
	// per-request cache that nobody can ever read back.
	public static SessionSearchMemo Disabled { get; } = new();

	public void Invalidate() => Interlocked.Increment(ref _generation);

	// EVERY argument SessionSearchService.SearchAsync takes, minus the CancellationToken — deliberately
	// one field per parameter, in the same order, so "the key covers everything that can change the
	// answer" is checkable by reading the two side by side rather than by trusting a comment.
	// SessionSearchMemoKeyCoversEveryEngineArgument pins it mechanically.
	//
	// WHAT IS ABSENT IS THE POINT. The sessions page's `agent` filter, `sortBy`/`sortDesc` and `pos`
	// never reach the engine — they reshape the already-fetched pool inside OnGetAsync — so they are
	// not here, and `?q=X`, `?q=X&agent=` and `?q=X&sortBy=updated` are ONE entry rather than three
	// misses. That falls out of keying on engine arguments; it is not a string-normalization pass over
	// the query string, and there is none anywhere in this file.
	//
	// NO USER AXIS, and that is a checked claim rather than an omission. SessionSearchService takes no
	// ClaimsPrincipal, and every leg it drives is scoped to projectKey alone (the digest store, the
	// term index, the full-scan index, the episodic index, the session list) — visibility for this
	// surface is decided by the page's [Authorize(Policy = "WorkspaceViewer")] + [TenantFrom(Route,
	// "projectKey")] BEFORE the search is reached, exactly the container discipline MemorySearchScope
	// uses (rights decide WHAT is searched; the cache keys on the container; the user never enters the
	// key). RankingMode IS here — it is a per-user preference that changes the ANSWER, so it separates
	// entries even though it grants no one visibility of anything new.
	public readonly record struct MemoKey(
		string ProjectKey,
		string Query,
		int Sessions,
		int HitsPerSession,
		bool FullScan,
		int? BodyLen,
		string? AfterSessionId,
		SearchRankingMode Mode)
	{
		// Builds the key from EFFECTIVE values: the counts run through the engine's own clamps, so
		// `size=40` and `size=100` (both → 30 sessions) are one entry, and an absent count normalizes
		// onto the same default the engine would have picked. An empty/whitespace cursor is the ABSENT
		// cursor — `?cursor=` is the first page, not a fourth distinct pool.
		public static MemoKey For(
			string projectKey, string query, int sessions, SearchRankingMode mode,
			string? afterSessionId = null, int hitsPerSession = 0, bool fullScan = false, int? bodyLen = null) =>
			new(projectKey,
				query,
				SessionSearchService.ClampSessions(sessions),
				SessionSearchService.ClampHitsPerSession(hitsPerSession),
				fullScan,
				bodyLen,
				string.IsNullOrWhiteSpace(afterSessionId) ? null : afterSessionId,
				mode);

		// FingerprintOf keeps null and "" distinguishable and joins on control characters no project
		// key, query or session id can contain, so no field can impersonate a boundary between two.
		public string Fingerprint() => KeysetCursor.FingerprintOf(
			"sessions-ui-outcome",
			ProjectKey,
			Query,
			Sessions.ToString(CultureInfo.InvariantCulture),
			HitsPerSession.ToString(CultureInfo.InvariantCulture),
			FullScan ? "1" : "0",
			BodyLen?.ToString(CultureInfo.InvariantCulture),
			AfterSessionId,
			Mode.ToString());
	}

	// The outcome, and whether it came from storage rather than from `compute`. FromCache is NOT the
	// same question as "did I miss": under concurrent misses HybridCache single-flights, so a caller
	// that lost the race never ran its own factory and must read the stored branch like any other
	// reader. Only this class can tell those apart.
	public readonly record struct MemoLookup(SessionSearchOutcome Outcome, bool FromCache);

	public async ValueTask<MemoLookup> GetOrComputeAsync(
		MemoKey key,
		Func<CancellationToken, ValueTask<SessionSearchOutcome>> compute,
		CancellationToken ct = default)
	{
		ArgumentNullException.ThrowIfNull(compute);

		if (_cache is null)
		{
			Interlocked.Increment(ref _misses);
			return new MemoLookup(await compute(ct), FromCache: false);
		}

		SessionSearchOutcome? computed = null;
		var cacheable = true;

		var bytes = await _cache.GetOrCreateAsync(
			VersionedKey(key),
			async innerCt =>
			{
				var result = await compute(innerCt);
				computed = result;
				// A DEGRADED answer is never KEPT — the same rule SearchPoolCache applies to a degraded
				// pool, and it matters more here because this layer has no data version to expire it
				// early. Storing one pins a half-answer for the WHOLE Ttl, so every repeat keeps hitting
				// an outage (a dead embed route, a failed FTS hydration) that has already healed.
				cacheable = !result.Discovery.Degraded;
				if (cacheable) Interlocked.Increment(ref _stores);
				return Serialize(result);
			},
			_entryOptions,
			tags: null,
			ct);

		if (computed is not null)
		{
			Interlocked.Increment(ref _misses);
			if (!cacheable)
			{
				// HybridCache fixes its write flags BEFORE the factory runs, so "do not keep this one"
				// cannot be said up front — the entry is written and then withdrawn. The window is
				// microseconds and all a reader can catch inside it is the degraded answer they would
				// have computed themselves, so the property that matters holds: the outage is not pinned.
				await _cache.RemoveAsync(VersionedKey(key), ct);
			}
			return new MemoLookup(computed, FromCache: false);
		}

		var outcome = Deserialize(bytes);
		if (outcome is null)
		{
			// A payload this build cannot read is a MISS, never garbage handed to a caller and never an
			// error: a routine deploy that changes the shape must cost a recomputation, not failing
			// searches for the length of one TTL.
			Interlocked.Increment(ref _misses);
			return new MemoLookup(await compute(ct), FromCache: false);
		}

		Interlocked.Increment(ref _hits);
		return new MemoLookup(outcome, FromCache: true);
	}

	string VersionedKey(MemoKey key) =>
		$"session-outcome:v{PayloadFormatVersion}:{Volatile.Read(ref _generation)}:{key.Fingerprint()}";

	static byte[] Serialize(SessionSearchOutcome o) =>
		JsonSerializer.SerializeToUtf8Bytes(
			new OutcomePayload(
				PayloadFormatVersion,
				o.Distilled, o.Reason,
				[.. o.Candidates.Select(c => new CandidatePayload(
					c.SessionId, c.Agent, c.Description,
					[.. c.Hits.Select(h => new HitPayload(h.Message, h.Role, h.Snippet, h.Score, h.Retriever))],
					Retrievers(c.Retrievers),
					[.. c.Sources]))],
				Retrievers(o.Discovery),
				o.FullScanRequested, o.FullScanRan, o.FullScanReason, o.FullScanCapped,
				o.PoolLimit, o.PoolBounded, o.MoreInPool, o.DataVersion, o.LastPoolKey),
			PayloadJson);

	// Returns null for ANY payload this build cannot honour — a foreign version, a truncated blob,
	// something that is not JSON at all. Never throws: a storage problem costs a recomputation.
	SessionSearchOutcome? Deserialize(byte[]? bytes)
	{
		if (bytes is null || bytes.Length == 0) return null;
		try
		{
			var p = JsonSerializer.Deserialize<OutcomePayload>(bytes, PayloadJson);
			if (p is null) return null;
			if (p.Version != PayloadFormatVersion)
			{
				if (_log?.IsEnabled(LogLevel.Debug) == true)
					_log.LogDebug(
						"Ignoring a memoized session search written in payload format v{Found} (this build reads v{Expected}) — recomputing.",
						p.Version, PayloadFormatVersion);
				return null;
			}

			return new SessionSearchOutcome(
				p.Distilled, p.Reason,
				[.. p.Candidates.Select(c => new SessionSearchCandidate(
					c.SessionId, c.Agent, c.Description,
					[.. c.Hits.Select(h => new SessionEpisodicHit(h.Message, h.Role, h.Snippet, h.Score, h.Retriever))],
					Retrievers(c.Retrievers),
					[.. c.Sources]))],
				Retrievers(p.Discovery),
				p.FullScanRequested, p.FullScanRan, p.FullScanReason, p.FullScanCapped,
				p.PoolLimit, p.PoolBounded, p.MoreInPool, p.DataVersion, p.LastPoolKey);
		}
		catch (JsonException ex)
		{
			_log?.LogWarning(ex, "A memoized session search could not be read back — treating it as a miss.");
			return null;
		}
	}

	static RetrieversPayload Retrievers(SearchRetrievers r) =>
		new(r.Lexical, r.Semantic, r.Degraded, r.DegradedReason, r.SemanticLag, r.Ranking);

	static SearchRetrievers Retrievers(RetrieversPayload r) =>
		new(r.Lexical, r.Semantic, r.Degraded, r.DegradedReason, r.SemanticLag, r.Ranking);

	static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web);

	// The stored SHAPE, declared here rather than by serializing the domain records directly — the
	// same call SearchPoolCache makes, for the same load-bearing reason: a refactor of
	// SessionSearchOutcome / SessionSearchCandidate / SessionEpisodicHit must not be able to change
	// the on-disk format by accident. Declared separately, such a change breaks THIS file's
	// compilation, which is where someone can decide whether PayloadFormatVersion needs bumping.
	// Serializing the domain types directly would instead deserialize an OLD blob into a NEW shape
	// with defaults for the added fields, under a key asserting it is current — wrong content served
	// silently, which is the one failure this cache must not have.
	sealed record OutcomePayload(
		[property: JsonPropertyName("v")] int Version,
		[property: JsonPropertyName("d")] bool Distilled,
		[property: JsonPropertyName("rs")] string? Reason,
		[property: JsonPropertyName("c")] IReadOnlyList<CandidatePayload> Candidates,
		[property: JsonPropertyName("dr")] RetrieversPayload Discovery,
		[property: JsonPropertyName("fq")] bool? FullScanRequested,
		[property: JsonPropertyName("fr")] bool? FullScanRan,
		[property: JsonPropertyName("fs")] string? FullScanReason,
		[property: JsonPropertyName("fc")] bool? FullScanCapped,
		[property: JsonPropertyName("pl")] int? PoolLimit,
		[property: JsonPropertyName("pb")] bool PoolBounded,
		[property: JsonPropertyName("m")] bool MoreInPool,
		[property: JsonPropertyName("dv")] string? DataVersion,
		[property: JsonPropertyName("lk")] string? LastPoolKey);

	sealed record CandidatePayload(
		[property: JsonPropertyName("s")] string SessionId,
		[property: JsonPropertyName("a")] string Agent,
		[property: JsonPropertyName("d")] string Description,
		[property: JsonPropertyName("h")] IReadOnlyList<HitPayload> Hits,
		[property: JsonPropertyName("r")] RetrieversPayload Retrievers,
		[property: JsonPropertyName("src")] IReadOnlyList<string> Sources);

	sealed record HitPayload(
		[property: JsonPropertyName("m")] long Message,
		[property: JsonPropertyName("r")] string Role,
		[property: JsonPropertyName("s")] string Snippet,
		[property: JsonPropertyName("sc")] double Score,
		[property: JsonPropertyName("rt")] string? Retriever);

	sealed record RetrieversPayload(
		[property: JsonPropertyName("l")] bool Lexical,
		[property: JsonPropertyName("s")] bool Semantic,
		[property: JsonPropertyName("d")] bool Degraded,
		[property: JsonPropertyName("dr")] string? DegradedReason,
		[property: JsonPropertyName("sl")] long? SemanticLag,
		[property: JsonPropertyName("rk")] SearchRankingOutcome? Ranking);
}
