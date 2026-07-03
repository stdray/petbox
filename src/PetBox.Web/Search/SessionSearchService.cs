using System.Text.Json;
using PetBox.Core.Search;
using PetBox.Memory.Contract;
using PetBox.Sessions.Contract;

namespace PetBox.Web.Search;

// The two-stage session search (spec: session-provenance-bridge):
//   1. DISCOVERY — hybrid (lexical ⊕ semantic, RRF) over the always-on session digests
//      (the `session-digests` memory store SessionDigestJob maintains): cheap, no
//      hydration, sublinear to archive size — the K it returns is constant.
//   2. EPISODIC — the top-K candidate sessions are lazily hydrated and searched INSIDE
//      (ISessionEpisodicIndex), each hit carrying the message ordinal: the provenance
//      bridge from found-by-meaning to the verbatim source (session_get).
// Candidates keep their discovery order; a session that vanished under a stale digest
// is skipped, not an error.
public sealed class SessionSearchService
{
	public const int DefaultSessions = 10;
	// The hydration cap per query. Recall saturates by K≈20-30 (eval m-dcbc8d51);
	// hydrations are sequential, so RAM stays bounded by the episodic cache cap.
	public const int MaxSessions = 30;
	public const int DefaultHitsPerSession = 5;
	public const int MaxHitsPerSession = 20;

	readonly IMemoryService _memory;
	readonly ISessionEpisodicIndex _episodic;
	// Discovery re-ranking policy. `_rerank` is the SHARED freshness+diversity policy (config
	// `Search:Recency`/`Search:Diversity`) — session discovery has the same semantics as memory
	// ("fresher wins at comparable relevance", "no near-duplicate sessions crowd the head"), so it
	// reuses the exact primitives. `_floor` is the session-specific semantic-noise guard.
	readonly SearchRerankOptions _rerank;
	readonly SessionSearchOptions _floor;

	public SessionSearchService(IMemoryService memory, ISessionEpisodicIndex episodic,
		SearchRerankOptions? rerank = null, SessionSearchOptions? options = null)
	{
		_memory = memory;
		_episodic = episodic;
		_rerank = rerank ?? new SearchRerankOptions();
		_floor = options ?? new SessionSearchOptions();
	}

	public async Task<SessionSearchOutcome> SearchAsync(string projectKey, string query,
		int sessions = 0, int hitsPerSession = 0, CancellationToken ct = default)
	{
		sessions = Math.Clamp(sessions <= 0 ? DefaultSessions : sessions, 1, MaxSessions);
		hitsPerSession = Math.Clamp(hitsPerSession <= 0 ? DefaultHitsPerSession : hitsPerSession, 1, MaxHitsPerSession);

		// No digest store yet = distillation hasn't reached this project; say so instead
		// of failing (the store auto-vivifies on the first distilled session). `reason`
		// gives callers a machine-readable code, not just a bare bool.
		if (!await _memory.StoreExistsAsync(projectKey, SessionDigestJob.Store, ct))
			return new SessionSearchOutcome(false, "no-digest-store", [], new SearchRetrievers(false, false, false));

		// DISCOVERY over the digest store, keeping the raw re-ranking signals (per-hit fused score,
		// freshness, lexical-confirmation provenance, vector) so the session-search policy runs here,
		// not inside the store search: a semantic-noise FLOOR, then freshness decay + MMR diversity.
		var discovery = await _memory.SearchScoredAsync(projectKey, SessionDigestJob.Store, query, type: null, ct: ct);
		var ranked = RankDiscovery(discovery.Hits, _rerank, _floor);

		var candidates = new List<SessionSearchCandidate>();
		foreach (var digest in ranked.Take(sessions))
		{
			ct.ThrowIfCancellationRequested();
			var (sessionId, agent) = Provenance(digest.Entry);
			var inner = await _episodic.SearchAsync(projectKey, sessionId, query, hitsPerSession, ct);
			if (inner is null) continue; // session deleted after distillation — stale digest
			candidates.Add(new SessionSearchCandidate(sessionId, agent, digest.Entry.Description, inner.Hits, inner.Retrievers));
		}

		// Discovery provenance is passed through UNTOUCHED — the floor/decay/MMR never re-decide
		// which retrievers ran or whether the answer degraded (that stays the store search's word).
		return new SessionSearchOutcome(true, null, candidates, discovery.Retrievers);
	}

	// The discovery re-ranking policy, applied to the raw digest pool BEFORE the session cut:
	//   1. Semantic FLOOR — drop a hit surfaced by the semantic leg ALONE (no lexical confirmation)
	//      whose RAW fused relevance (the RRF score, before decay) is below the floor: the low-score
	//      semantic-only false hits the plain hybrid otherwise floats up. A lexically-confirmed hit
	//      is NEVER floored (the lexical leg vouched for it), and without an embedder every hit is
	//      confirmed, so the floor silently no-ops — degradation stays quiet.
	//   2. Freshness DECAY — multiply the fused score by an exp half-life weight on the digest's
	//      Updated, so at comparable relevance the fresher session ranks higher.
	//   3. MMR DIVERSITY — reorder so near-duplicate sessions don't crowd the head; silently
	//      identity without digest vectors (no embedder / unvectorized store).
	internal static List<MemoryScoredHit> RankDiscovery(IReadOnlyList<MemoryScoredHit> hits,
		SearchRerankOptions rerank, SessionSearchOptions floor)
	{
		if (hits.Count == 0) return hits.ToList();

		var kept = hits.Where(h => h.LexicalConfirmed || h.Score >= floor.SemanticFloor).ToList();

		var now = DateTime.UtcNow;
		var recency = rerank.Recency;
		double Blended(MemoryScoredHit h) => recency.Enabled
			? h.Score * RecencyDecay.Weight(h.Updated, now, recency.HalfLifeDays)
			: h.Score;

		var blended = kept
			.OrderByDescending(Blended)
			.ThenByDescending(h => h.Updated)
			.ThenBy(h => h.Entry.Key, StringComparer.Ordinal)
			.ToList();

		var diversity = rerank.Diversity;
		if (diversity.Enabled)
			blended = Mmr.Reorder(blended, Blended, h => h.Vector, diversity.Lambda);
		return blended;
	}

	// The digest entry's metadata carries the provenance (sessionId + agent) the
	// distiller stamped; the entry key doubles as the sessionId fallback.
	static (string SessionId, string Agent) Provenance(MemoryEntryView digest)
	{
		if (!string.IsNullOrWhiteSpace(digest.Metadata))
		{
			try
			{
				using var doc = JsonDocument.Parse(digest.Metadata);
				var sessionId = doc.RootElement.TryGetProperty("sessionId", out var s) ? s.GetString() : null;
				var agent = doc.RootElement.TryGetProperty("agent", out var a) ? a.GetString() : null;
				return (sessionId ?? digest.Key, agent ?? "");
			}
			catch (JsonException) { /* fall through to the key */ }
		}
		return (digest.Key, "");
	}
}

// One discovered session: its digest description (what the session is about), the
// episodic hits inside it (message ordinal = the session_get bridge) and the inner
// retriever provenance.
public sealed record SessionSearchCandidate(
	string SessionId,
	string Agent,
	string Description,
	IReadOnlyList<SessionEpisodicHit> Hits,
	SearchRetrievers Retrievers);

// Session-discovery re-ranking knobs bound from config `Search:Sessions:*` (sibling of the shared
// `Search:Recency`/`Search:Diversity` that drive decay + MMR). `SemanticFloor` is the minimum RAW
// fused RRF relevance a SEMANTIC-ONLY digest hit must clear to survive — a lexically-confirmed hit
// is never floored. The default is conservative on the RRF scale: a single-leg hit scores
// 1/(K+rank) with K=60, so a lone semantic hit tops out at 1/60 ≈ 0.0167 (rank 0) and thins toward
// 0.010 by rank ~40; 0.013 keeps the strong semantic-only head (≈ top-17 ranks) and trims the weak
// deep tail. Raise it to cut more aggressively, 0 to disable the floor entirely.
public sealed record SessionSearchOptions
{
	public double SemanticFloor { get; init; } = 0.013;
}

// Distilled=false → the project has no digest store yet (background distillation
// hasn't run); an honest "not indexed yet", distinct from "nothing matched". `Reason`
// is a machine-readable code for that state (e.g. "no-digest-store"), null when distilled.
public sealed record SessionSearchOutcome(
	bool Distilled,
	string? Reason,
	IReadOnlyList<SessionSearchCandidate> Candidates,
	SearchRetrievers Discovery);
