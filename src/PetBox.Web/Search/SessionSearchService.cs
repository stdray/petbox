using System.Text.Json;
using PetBox.Core.Search;
using PetBox.Core.Settings;
using PetBox.Memory.Contract;
using PetBox.Sessions.Contract;
using PetBox.Sessions.Search;

namespace PetBox.Web.Search;

// The two-stage session search (spec: session-provenance-bridge):
//   1. DISCOVERY — UP TO THREE fused legs over the always-on per-session state, no
//      hydration, sublinear to archive size (the K each leg returns is constant):
//        - digest   — hybrid (lexical ⊕ semantic, RRF) over the `session-digests` memory
//          store SessionDigestJob maintains (an LLM-composed summary);
//        - term     — verbatim BM25 over the FULL stemmed token stream of the session's raw
//          content (ISessionTermIndex, spec: session-discovery-verbatim). A distinctive term
//          the digest's LLM summary dropped still surfaces a session through this leg alone;
//        - fullscan — OPT-IN ONLY (spec: session-fullscan-optin): a raw, untokenized
//          substring/phrase scan over every session's content, gated behind an explicit
//          per-call `fullScan:true` AND a two-key permission setting
//          (SessionFullScanSettings: system AND project must both allow it). Never runs by
//          default, never automatically. Catches what term-FTS structurally cannot (a
//          substring straddling token boundaries) at the cost of a full hydration scan —
//          capped, and the cap is reported, never silent.
//      Every leg's ranked session-id list is fused by the SAME RRF primitive (HybridMerge)
//      the rest of the system uses, one level up (session identity instead of entity
//      identity) — a session found by only one leg gets a fair RRF score, not a last-place
//      tack-on. The fused pool then runs through the SHARED re-ranking policy (semantic
//      floor, freshness decay, MMR diversity) exactly as before.
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

	// Term-leg over-fetch pool: mirrors the memory contract's own convention for a store's
	// hybrid pool (max(3×limit, 50), see IMemoryService.SearchEntriesAsync) so neither leg
	// starves the fusion of candidates the session cut would otherwise keep.
	internal const int TermPoolFloor = 50;

	readonly IMemoryService _memory;
	readonly ISessionEpisodicIndex _episodic;
	readonly ISessionTermIndex _termIndex;
	readonly ISessionFullScanIndex _fullScanIndex;
	readonly ISettingsResolver _settings;
	readonly ISessionService _sessionsSvc;
	// Discovery re-ranking policy. `_rerank` is the SHARED freshness+diversity policy (config
	// `Search:Recency`/`Search:Diversity`) — session discovery has the same semantics as memory
	// ("fresher wins at comparable relevance", "no near-duplicate sessions crowd the head"), so it
	// reuses the exact primitives. There is NO semantic floor: a vector-only digest hit enters as a
	// peer (spec: search-leg-classification — the tau membership threshold is gone).
	readonly SearchRerankOptions _rerank;

	public SessionSearchService(IMemoryService memory, ISessionEpisodicIndex episodic,
		ISessionTermIndex termIndex, ISessionFullScanIndex fullScanIndex, ISettingsResolver settings,
		ISessionService sessionsSvc, SearchRerankOptions? rerank = null)
	{
		_memory = memory;
		_episodic = episodic;
		_termIndex = termIndex;
		_fullScanIndex = fullScanIndex;
		_settings = settings;
		_sessionsSvc = sessionsSvc;
		_rerank = rerank ?? new SearchRerankOptions();
	}

	public async Task<SessionSearchOutcome> SearchAsync(string projectKey, string query,
		int sessions = 0, int hitsPerSession = 0, bool fullScan = false, CancellationToken ct = default)
	{
		sessions = Math.Clamp(sessions <= 0 ? DefaultSessions : sessions, 1, MaxSessions);
		hitsPerSession = Math.Clamp(hitsPerSession <= 0 ? DefaultHitsPerSession : hitsPerSession, 1, MaxHitsPerSession);

		// DISCOVERY leg 1: the digest store's own hybrid (lexical ⊕ semantic, RRF-fused) search,
		// keeping the raw re-ranking signals (per-hit fused score, freshness, lexical-confirmation
		// provenance, vector) — the outer fusion below treats this leg's ORDER as one ranking.
		//
		// No digest store yet = distillation hasn't reached this project. We report that honestly
		// (Distilled=false + Reason) but do NOT bail: the verbatim term leg is the DECLARED lower
		// bound of recall (spec: session-discovery-verbatim) and must run even with no digest —
		// "distillation hasn't run" ≠ "nothing to find". The digest leg is simply skipped (empty
		// ranking); SearchScoredAsync THROWS on a missing store, so it is gated behind the check.
		var distilled = await _memory.StoreExistsAsync(projectKey, SessionDigestJob.Store, ct);
		var digestRanking = new List<string>();
		var bySession = new Dictionary<string, MemoryScoredHit>(StringComparer.Ordinal);
		var digestRetrievers = new SearchRetrievers(false, false, false);
		if (distilled)
		{
			var discovery = await _memory.SearchScoredAsync(projectKey, SessionDigestJob.Store, query, type: null, ct: ct);
			digestRetrievers = discovery.Retrievers;
			foreach (var hit in discovery.Hits)
			{
				var (sessionId, _) = Provenance(hit.Entry);
				digestRanking.Add(sessionId);
				bySession.TryAdd(sessionId, hit); // the best (first) digest hit per session wins the metadata
			}
		}

		// DISCOVERY leg 2: verbatim term-FTS over the raw transcript (spec: session-discovery-verbatim).
		var termPool = Math.Max(3 * sessions, TermPoolFloor);
		var termRanking = await _termIndex.SearchAsync(projectKey, query, termPool, ct);
		var termSet = new HashSet<string>(termRanking, StringComparer.Ordinal);

		// DISCOVERY leg 3: the full-scan escape hatch (spec: session-fullscan-optin) — OPT-IN
		// ONLY. Requested is honest about what the caller asked; Ran/Reason/Capped report what
		// actually happened (denied ≠ silently ignored).
		var scanRanking = (IReadOnlyList<string>)[];
		bool? fullScanRequested = null, fullScanRan = null, fullScanCapped = null;
		string? fullScanReason = null;
		if (fullScan)
		{
			fullScanRequested = true;
			var allowed = await FullScanAllowedAsync(projectKey, ct);
			fullScanRan = allowed;
			if (!allowed)
			{
				fullScanReason = "not-allowed";
			}
			else
			{
				var scan = await _fullScanIndex.ScanAsync(projectKey, query, ct);
				scanRanking = scan.SessionIds;
				fullScanCapped = scan.Capped;
			}
		}
		var scanSet = new HashSet<string>(scanRanking, StringComparer.Ordinal);

		// Fuse every leg's session-id ranking by the SAME RRF primitive the rest of the system
		// uses, one level up (session identity, not entity identity) — a session found by only
		// ONE leg gets a fair rank-based score, not a last-place tack-on.
		var fused = HybridMerge.RrfScored(digestRanking, termRanking, scanRanking);

		// A session found ONLY by term/fullscan has no digest entry yet — its freshness/agent
		// come from the session header instead. Looked up once, only if such a session exists.
		Dictionary<string, SessionHeader>? headers = null;
		if (fused.Any(f => !bySession.ContainsKey(f.Key)))
			headers = (await _sessionsSvc.ListAsync(projectKey, ct)).ToDictionary(h => h.SessionId, StringComparer.Ordinal);

		var pool = new List<MemoryScoredHit>(fused.Count);
		var sourcesBySession = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
		foreach (var (sessionId, score) in fused)
		{
			var inDigest = bySession.TryGetValue(sessionId, out var digestHit);
			var inTerm = termSet.Contains(sessionId);
			var inScan = scanSet.Contains(sessionId);
			var sources = new List<string>(3);
			if (inDigest) sources.Add("digest");
			if (inTerm) sources.Add("term");
			if (inScan) sources.Add("fullscan");
			sourcesBySession[sessionId] = sources;

			if (inDigest)
			{
				// A term/fullscan confirmation is ALSO a lexical (verbatim) confirmation — it
				// must never be floored as semantic-only noise, even if the digest's own hybrid
				// search only found it through the vector leg.
				pool.Add(digestHit! with { Score = score, LexicalConfirmed = digestHit.LexicalConfirmed || inTerm || inScan });
			}
			else
			{
				headers!.TryGetValue(sessionId, out var header);
				var entry = new MemoryEntryView(sessionId, "Reference", "", "", [], 0, "");
				// Term-FTS and full-scan are both lexical (verbatim) by construction — never floored.
				pool.Add(new MemoryScoredHit(entry, header?.Updated ?? DateTime.UtcNow, score, LexicalConfirmed: true, Vector: null));
			}
		}

		var ranked = RankDiscovery(pool, _rerank);

		var candidates = new List<SessionSearchCandidate>();
		foreach (var digest in ranked.Take(sessions))
		{
			ct.ThrowIfCancellationRequested();
			var (sessionId, agent) = Provenance(digest.Entry);
			if (agent.Length == 0 && headers is not null && headers.TryGetValue(sessionId, out var hdr))
				agent = hdr.Agent; // term/fullscan-only candidate — the digest metadata never carried an agent
			var inner = await _episodic.SearchAsync(projectKey, sessionId, query, hitsPerSession, ct);
			if (inner is null) continue; // session deleted after distillation — stale digest
			var sources = sourcesBySession.GetValueOrDefault(sessionId, (IReadOnlyList<string>)["digest"]);
			candidates.Add(new SessionSearchCandidate(sessionId, agent, digest.Entry.Description, inner.Hits, inner.Retrievers, sources));
		}

		// Discovery retrievers: OR the term/fullscan legs' lexical confirmation into the digest
		// leg's provenance — a verbatim-only match is still a LEXICAL discovery signal, just from
		// a different index (and the whole digest provenance is off when distillation never ran).
		var retrievers = digestRetrievers with { Lexical = digestRetrievers.Lexical || termRanking.Count > 0 || scanRanking.Count > 0 };
		// Distilled/Reason stay an HONEST informational signal — but candidates are no longer
		// gated on it: the term (and opt-in fullscan) legs answer regardless of the digest store.
		return new SessionSearchOutcome(distilled, distilled ? null : "no-digest-store", candidates, retrievers,
			fullScanRequested, fullScanRan, fullScanReason, fullScanCapped);
	}

	// allowed = system.SystemEnabled AND project.ProjectEnabled — TWO independent switches
	// (spec: session-fullscan-optin), read via two separate resolver calls so each property
	// resolves against its own TopLevel scope (mirrors LogSettings' System/Project pair).
	async Task<bool> FullScanAllowedAsync(string projectKey, CancellationToken ct)
	{
		var system = await _settings.GetAsync<SessionFullScanSettings>(Scope.System, "$", ct);
		if (!system.SystemEnabled) return false;
		var project = await _settings.GetAsync<SessionFullScanSettings>(Scope.Project, projectKey, ct);
		return project.ProjectEnabled;
	}

	// The discovery re-ranking policy, applied to the raw digest pool BEFORE the session cut. This
	// is the PRESENTATION reshape of an already-selected pool — it reorders, it never gates
	// membership (spec: search-selection-vs-presentation):
	//   1. Freshness DECAY — multiply the fused score by an exp half-life weight on the digest's
	//      Updated, so at comparable relevance the fresher session ranks higher.
	//   2. MMR DIVERSITY — reorder so near-duplicate sessions don't crowd the head; silently
	//      identity without digest vectors (no embedder / unvectorized store).
	// There is NO semantic floor (spec: search-leg-classification — the tau membership threshold is
	// gone): a vector-only digest hit ENTERS as a peer, bounded only by the pool and the session cut.
	internal static List<MemoryScoredHit> RankDiscovery(IReadOnlyList<MemoryScoredHit> hits,
		SearchRerankOptions rerank)
	{
		if (hits.Count == 0) return hits.ToList();

		var now = DateTime.UtcNow;
		var recency = rerank.Recency;
		double Blended(MemoryScoredHit h) => recency.Enabled
			? h.Score * RecencyDecay.Weight(h.Updated, now, recency.HalfLifeDays)
			: h.Score;

		var blended = hits
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
// episodic hits inside it (message ordinal = the session_get bridge), the inner retriever
// provenance, and `Sources` — which STAGE-1 DISCOVERY leg(s) raised this session ("digest",
// "term", or both; "fullscan" joins the set once opted in — spec session-fullscan-optin).
// A session with Sources == ["term"] alone has no digest entry (yet): Description is empty.
public sealed record SessionSearchCandidate(
	string SessionId,
	string Agent,
	string Description,
	IReadOnlyList<SessionEpisodicHit> Hits,
	SearchRetrievers Retrievers,
	IReadOnlyList<string> Sources);

// Distilled=false → the project has no digest store yet (background distillation
// hasn't run); an honest "not indexed yet", distinct from "nothing matched". `Reason`
// is a machine-readable code for that state (e.g. "no-digest-store"), null when distilled.
//
// FullScan* (spec: session-fullscan-optin) are all null when `fullScan` was never passed
// (not requested — the common case). Once requested, `FullScanRequested=true` always, and:
//   FullScanRan=false, FullScanReason="not-allowed" — asked, but the two-key permission
//     setting denies it (system and/or project switch off). The scan never ran — honestly
//     reported, not silently ignored.
//   FullScanRan=true  — the scan ran; `FullScanCapped=true` means the project holds more
//     sessions than the scan cap, so some were never looked at (also logged, never silent).
public sealed record SessionSearchOutcome(
	bool Distilled,
	string? Reason,
	IReadOnlyList<SessionSearchCandidate> Candidates,
	SearchRetrievers Discovery,
	bool? FullScanRequested = null,
	bool? FullScanRan = null,
	string? FullScanReason = null,
	bool? FullScanCapped = null);
