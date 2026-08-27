using System.Text.Json;
using PetBox.Core.Contract;
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
	private const int DefaultSessions = 10;
	// The hydration cap per query. Recall saturates by K≈20-30 (eval m-dcbc8d51);
	// hydrations are sequential, so RAM stays bounded by the episodic cache cap.
	public const int MaxSessions = 30;
	private const int DefaultHitsPerSession = 5;
	private const int MaxHitsPerSession = 20;

	// Term-leg over-fetch pool: mirrors the memory contract's own convention for a store's
	// hybrid pool (max(3×limit, 50), see IMemoryService.SearchEntriesAsync) so neither leg
	// starves the fusion of candidates the session cut would otherwise keep.
	private const int TermPoolFloor = 50;

	readonly IMemoryService _memory;
	readonly ISessionEpisodicIndex _episodic;
	readonly ISessionTermIndex _termIndex;
	readonly ISessionFullScanIndex _fullScanIndex;
	readonly ISettingsResolver _settings;
	readonly ISessionService _sessionsSvc;
	// Discovery re-ranking policy. `_ordering` is the SHARED freshness+diversity policy (config
	// `Search:Recency`/`Search:Diversity`) — session discovery has the same semantics as memory
	// ("fresher wins at comparable relevance", "no near-duplicate sessions crowd the head"), so it
	// reuses the exact primitives. There is NO semantic floor: a vector-only digest hit enters as a
	// peer (spec: search-leg-classification — the tau membership threshold is gone).
	readonly SearchOrderingPolicies _ordering;
	// THE MATERIALIZED DISCOVERY POOL, and the reason this surface can be paged at all — the SAME cache
	// memory and tasks page against (spec: result-set-pageable, requirement 5).
	//
	// Sessions were the one ranked surface wired WITHOUT it, and that was not a missing optimization: it
	// was the defect. The digest leg carries a cross-encoder rerank (spec: search-rerank-for-sessions),
	// the live rerank route is not order-stable across two identical calls (measured: adjacent ranks swap
	// between back-to-back requests on the same eight documents), and an uncached pool is REBUILT on every
	// page. So every page re-ranked, came out ordered differently, minted a different order stamp, and the
	// cursor guard refused the walk it had itself issued one call earlier — page 2 did not exist.
	// Computing the order ONCE and keeping it is what makes the stamp reproducible; the guard is untouched.
	// The fallback is SearchPoolCache.Disabled, which stores nothing and says so by name.
	readonly SearchPoolCache _poolCache;

	public SessionSearchService(IMemoryService memory, ISessionEpisodicIndex episodic,
		ISessionTermIndex termIndex, ISessionFullScanIndex fullScanIndex, ISettingsResolver settings,
		ISessionService sessionsSvc, SearchOrderingPolicies? rerank = null, SearchPoolCache? poolCache = null)
	{
		_memory = memory;
		_episodic = episodic;
		_termIndex = termIndex;
		_fullScanIndex = fullScanIndex;
		_settings = settings;
		_sessionsSvc = sessionsSvc;
		_ordering = rerank ?? new SearchOrderingPolicies();
		_poolCache = poolCache ?? SearchPoolCache.Disabled;
	}

	// How deep the DISCOVERY pool is allowed to be paged (spec: result-set-pageable) — sessions' analogue
	// of the rerank candidate budget. It bounds the ORDER a caller may walk, never the cost of one call:
	// each page still hydrates only `sessions` (≤ MaxSessions) candidates, so paging cannot turn a
	// sublinear discovery into a full archive scan. Today's callers never see it bite — the legs' own
	// pools (termPool, the digest store's) are far smaller — but it is what `poolLimit` names, so it is a
	// declared constant rather than an emergent accident.
	public const int DiscoveryPoolLimit = 200;

	// `afterSessionId` is the RESUME POINT: hydrate the candidates that come strictly after that session
	// in the discovery order. The adapter still owns the cursor token (encode/decode/fingerprint, exactly
	// as tasks_search and memory_search do); this parameter is only the position it decoded, kept here
	// because the discovery order lives here and re-deriving it in the adapter would mean hydrating the
	// whole pool just to find one row.
	// `mode` (spec: search-ranking-mode-is-caller-choice) is threaded through BOTH discovery-leg-1
	// (the digest store's hybrid search) and episodic hydration — this service never picks a
	// default of its own, it simply propagates whatever the caller supplies (the same posture
	// MemoryService/TasksService take for SearchRequest.RankingMode). The EDGE decides: the MCP
	// verb (session_search) hardcodes Precision, the UI page reads the human's
	// ui-search-ranking-mode-preference override of the Speed default.
	// The two ARGUMENT NORMALIZATIONS SearchAsync applies before it does anything else, lifted out of
	// the method body so they are callable WITHOUT running a search. SessionSearchMemo's key is built
	// from the EFFECTIVE values the engine actually runs with, not from the raw ones a caller passed
	// (ui-search-render-memoized) — and the only way that claim can stay true is for both to read the
	// same function. Inlined clamps would drift the first time a bound moved: the key would keep
	// separating two asks the engine had already collapsed into one, and the memo would quietly stop
	// hitting with nothing failing.
	public static int ClampSessions(int sessions) =>
		Math.Clamp(sessions <= 0 ? DefaultSessions : sessions, 1, MaxSessions);

	public static int ClampHitsPerSession(int hitsPerSession) =>
		Math.Clamp(hitsPerSession <= 0 ? DefaultHitsPerSession : hitsPerSession, 1, MaxHitsPerSession);

	public async Task<SessionSearchOutcome> SearchAsync(string projectKey, string query,
		int sessions = 0, int hitsPerSession = 0, bool fullScan = false, int? bodyLen = null,
		string? afterSessionId = null, SearchRankingMode mode = SearchRankingMode.Precision, CancellationToken ct = default)
	{
		sessions = ClampSessions(sessions);
		hitsPerSession = ClampHitsPerSession(hitsPerSession);

		// No digest store yet = distillation hasn't reached this project. We report that honestly
		// (Distilled=false + Reason) but do NOT bail: the verbatim term leg is the DECLARED lower bound of
		// recall (spec: session-discovery-verbatim) and must run even with no digest — "distillation
		// hasn't run" is not "nothing to find". The digest leg is simply skipped (empty ranking);
		// SearchScoredAsync THROWS on a missing store, so it is gated behind this check.
		//
		// Probed OUTSIDE the pool computation because it is also an OUTCOME field: a page served from a
		// cached pool must still answer "is this project distilled" about TODAY, not about whenever the
		// pool was built. It is one scalar catalog lookup, so paying it per page costs nothing.
		var distilled = await _memory.StoreExistsAsync(projectKey, SessionDigestJob.Store, ct);

		// The full-scan PERMISSION (spec: session-fullscan-optin) is decided per call for the same reason:
		// two settings reads, and a deployment that revokes the permission mid-walk must be obeyed on the
		// NEXT page rather than inherited from a pool built while it was still granted. Only the scan
		// ITSELF is expensive, and only it lives inside the pool below.
		bool? fullScanRequested = null, fullScanRan = null;
		string? fullScanReason = null;
		if (fullScan)
		{
			fullScanRequested = true;
			fullScanRan = await FullScanAllowedAsync(projectKey, ct);
			if (fullScanRan == false) fullScanReason = "not-allowed";
		}
		var scanLeg = fullScanRan == true;

		// The term leg's over-fetch depth. It decides WHICH sessions are candidates, so it is part of the
		// pool's identity below — lifted up here from the leg itself for exactly that reason.
		var termPool = Math.Max(3 * sessions, TermPoolFloor);

		// THE POOL KEY — everything that decides the pool's MEMBERSHIP and ORDER, and nothing that decides
		// only how one page is rendered (`hitsPerSession`, `bodyLen`, and `sessions` except through the
		// candidate depth it implies, which IS included).
		//
		// NO DATA-VERSION COMPONENT, unlike tasks/memory, and that is a consequence rather than an
		// oversight: this surface's data version IS the discovery order (see `dataVersion` below), which
		// does not exist until the pool has been built — it cannot key the thing it is derived from.
		// What follows, stated plainly: inside the TTL a walk pages a SNAPSHOT, so a session appended
		// mid-walk joins the NEXT walk instead of refusing this one. That is strictly safer than what it
		// replaces (a walk spliced across two rerank orderings), it is one coherent ordering throughout,
		// and the refusal that matters is untouched — once the pool is gone (TTL, restart, eviction) the
		// rebuild's order hash is compared as before and a genuinely different order is still refused.
		var poolKey = KeysetCursor.FingerprintOf(
			"sessions-pool", projectKey, query,
			distilled ? "digest" : "no-digest",
			scanLeg ? "scan" : "no-scan",
			termPool.ToString(System.Globalization.CultureInfo.InvariantCulture),
			mode.ToString());

		var lookup = await _poolCache.GetOrComputeAsync(poolKey, async innerCt =>
		{
			var (rows, discoveryRetrievers, bounded, scanCapped) =
				await DiscoverAsync(projectKey, query, distilled, scanLeg, termPool, mode, innerCt);
			// A DEGRADED pool is never KEPT — the rule tasks/memory already follow. It is cheap to
			// recompute (the reranker did not run anyway) and expensive to keep: storing it pins a
			// half-answer plus its now-stale provenance for the whole TTL, so every repeat of the query
			// keeps hitting an outage that has already healed.
			var cacheable = !discoveryRetrievers.Degraded && discoveryRetrievers.Ranking != SearchRankingOutcome.DegradedRrf;
			return new SearchPoolCache.PoolComputation(
				ToPool(rows, discoveryRetrievers, bounded, scanCapped), cacheable);
		}, ct);

		// From here on there is ONE representation of the discovery order — the pool — whether this call
		// built it or read it. That is the point: the stamp, the seek and the page all read the same
		// object, so no two of them can disagree about what the order was.
		var pool = lookup.Pool;
		var ranked = FromPool(pool);
		var retrievers = pool.Retrievers;
		var poolBounded = pool.PoolBounded;
		// Whether the opt-in scan hit its cap belongs to the PASS that built this pool, so it rides with
		// the pool rather than being recomputed (page 2 never re-runs the scan). Null unless the scan ran,
		// which is the wire meaning of "never requested".
		bool? fullScanCapped = scanLeg ? pool.PoolAnnotation == ScanCappedNote : null;

		// The DATA VERSION of this walk is a hash of the discovery order ITSELF (session address + fused
		// score, in order). That is exact rather than approximate: the basis of the ordering is precisely
		// what a cursor must stay bound to, and every input that could move a row — a new session, a fresh
		// digest, a term-index update, a changed leg — shows up here by construction.
		//
		// It now comes off SearchPool.OrderHash, i.e. off the SAME object the cache stored, and THAT is
		// the fix. Minted from a freshly recomputed pool it was not reproducible at all: the digest leg
		// reranks through a cross-encoder route that returns the same documents in a different order on
		// two back-to-back calls, so every page stamped a new value and the cursor guard refused a walk in
		// which nothing whatsoever had changed. Reading page 2's stamp off the pool page 1 was issued
		// against makes it reproducible for the only reason that can ever make it reproducible — it is the
		// same list, not a second attempt at the same list.
		var dataVersion = pool.OrderHash;

		// KEYSET SEEK by IDENTITY: resume strictly after the session the token names, wherever it now
		// sits. Identity is the whole key — a session id is unique in the pool, so a repeated score cannot
		// make the boundary ambiguous. A resume point that is no longer in the pool cannot occur inside a
		// valid walk (the stamp above would have refused the token first); if it somehow does, we refuse
		// rather than silently restarting at the top, which is the failure this design exists to prevent.
		var start = 0;
		if (afterSessionId is not null)
		{
			var at = ranked.FindIndex(h => string.Equals(h.SessionId, afterSessionId, StringComparison.Ordinal));
			if (at < 0)
				throw new ArgumentException(
					"session_search: the session this cursor names is no longer in the discovery pool — "
					+ "drop the cursor and start the query over.");
			start = at + 1;
		}

		var candidates = new List<SessionSearchCandidate>();
		var pageSlice = ranked.Skip(start).Take(sessions).ToList();
		// Rows remaining in the pool AFTER this page — the "more" signal, computed on the POOL (before
		// hydration can drop a stale candidate) so it describes the walk, not this page's luck.
		var moreInPool = start + pageSlice.Count < ranked.Count;
		foreach (var row in pageSlice)
		{
			ct.ThrowIfCancellationRequested();
			var inner = await _episodic.SearchAsync(projectKey, row.SessionId, query, hitsPerSession, bodyLen, mode: mode, ct: ct);
			if (inner is null) continue; // session deleted after distillation — stale digest
			candidates.Add(new SessionSearchCandidate(row.SessionId, row.Agent, row.Description, inner.Hits, inner.Retrievers, row.Sources));
		}

		// Distilled/Reason stay an HONEST informational signal — but candidates are no longer
		// gated on it: the term (and opt-in fullscan) legs answer regardless of the digest store.
		return new SessionSearchOutcome(distilled, distilled ? null : "no-digest-store", candidates, retrievers,
			fullScanRequested, fullScanRan, fullScanReason, fullScanCapped,
			DiscoveryPoolLimit, poolBounded, moreInPool, dataVersion,
			// The resume point is the last candidate this page CONSIDERED, not the last it managed to
			// hydrate. A stale candidate (session deleted after distillation) is skipped, and resuming
			// before it would re-consider it forever; resuming after the slice also keeps a page whose
			// every candidate went stale from ending the walk while the pool still has rows.
			pageSlice.Count > 0 ? pageSlice[^1].SessionId : null);
	}

	// ONE ROW of the discovery pool: the session a caller navigates, plus the three facts a page needs
	// that its ADDRESS alone no longer says (the agent and the digest description it displays, and which
	// discovery leg raised it). Everything else the legs produced — the digest entry, its vector, its
	// lexical-confirmation flag — is spent by the time RankDiscovery has run and is deliberately not kept.
	readonly record struct DiscoveredSession(
		string SessionId, string Agent, string Description, IReadOnlyList<string> Sources, double Score);

	// The pool's ADDRESS type. Tasks address a board, memory a store; a session is its own container, so
	// this is a constant — it is here because SearchPool.OrderHash hashes (Type, Id) and an address needs
	// both halves, not because sessions have a second axis.
	const string PoolRowType = "session";
	// The per-row annotation packs the two DISPLAY facts into the one opaque slot SearchPool gives a
	// consumer, on the US control character FingerprintOf joins its parts with and for the same reason:
	// no agent name and no digest description contains it, so neither field can impersonate the boundary.
	const char RowFieldSeparator = '\u001f';
	// The pool-level annotation: whether the opt-in full scan hit its cap. Two spellings rather than one
	// plus null, so "the scan ran and was not capped" stays distinguishable from "no note stored".
	const string ScanCappedNote = "scan:capped";
	const string ScanUncappedNote = "scan:uncapped";

	static SearchPool ToPool(IReadOnlyList<DiscoveredSession> rows, SearchRetrievers retrievers, bool bounded, bool? scanCapped) =>
		new([.. rows.Select(r => new Hit(PoolRowType, r.SessionId, r.Score, string.Join(',', r.Sources)))],
			DiscoveryPoolLimit, bounded, retrievers,
			[.. rows.Select(r => (string?)(r.Agent + RowFieldSeparator + r.Description))],
			scanCapped is null ? null : scanCapped.Value ? ScanCappedNote : ScanUncappedNote);

	static List<DiscoveredSession> FromPool(SearchPool pool)
	{
		var rows = new List<DiscoveredSession>(pool.Count);
		for (var i = 0; i < pool.Count; i++)
		{
			var hit = pool.Ordered[i];
			var packed = pool.AnnotationAt(i) ?? "";
			var cut = packed.IndexOf(RowFieldSeparator);
			IReadOnlyList<string> sources = string.IsNullOrEmpty(hit.Retriever)
				? []
				: hit.Retriever!.Split(',', StringSplitOptions.RemoveEmptyEntries);
			rows.Add(new DiscoveredSession(hit.Id,
				cut < 0 ? "" : packed[..cut],
				cut < 0 ? packed : packed[(cut + 1)..],
				sources, hit.Score));
		}
		return rows;
	}

	// THE POOL COMPUTATION: the three discovery legs, their RRF fusion, the presentation reshape and the
	// depth cut — everything whose result a cursor is bound to, and nothing else. It runs ONCE per pool
	// (see _poolCache); a later page of the same walk never reaches it.
	async Task<(List<DiscoveredSession> Rows, SearchRetrievers Retrievers, bool PoolBounded, bool? ScanCapped)> DiscoverAsync(
		string projectKey, string query, bool distilled, bool scanLeg, int termPool, SearchRankingMode mode,
		CancellationToken ct)
	{
		// DISCOVERY leg 1: the digest store's own hybrid (lexical + semantic, RRF-fused) search,
		// keeping the raw re-ranking signals (per-hit fused score, freshness, lexical-confirmation
		// provenance, vector) — the outer fusion below treats this leg's ORDER as one ranking. This is
		// also the ONLY leg that is not order-stable: it is the one that reranks.
		var digestRanking = new List<string>();
		var bySession = new Dictionary<string, MemoryScoredHit>(StringComparer.Ordinal);
		var digestRetrievers = new SearchRetrievers(false, false, false);
		if (distilled)
		{
			var discovery = await _memory.SearchScoredAsync(projectKey, SessionDigestJob.Store, query, type: null, mode: mode, ct: ct);
			digestRetrievers = discovery.Retrievers;
			foreach (var hit in discovery.Hits)
			{
				var (sessionId, _) = Provenance(hit.Entry);
				digestRanking.Add(sessionId);
				bySession.TryAdd(sessionId, hit); // the best (first) digest hit per session wins the metadata
			}
		}

		// DISCOVERY leg 2: verbatim term-FTS over the raw transcript (spec: session-discovery-verbatim).
		var termRanking = await _termIndex.SearchAsync(projectKey, query, termPool, ct);
		var termSet = new HashSet<string>(termRanking, StringComparer.Ordinal);

		// DISCOVERY leg 3: the full-scan escape hatch (spec: session-fullscan-optin) — OPT-IN ONLY, and
		// already permission-checked by the caller: `scanLeg` is "asked AND allowed". `Capped` rides back
		// out because it is a fact about THIS pool that no later page can re-derive.
		var scanRanking = (IReadOnlyList<string>)[];
		bool? scanCapped = null;
		if (scanLeg)
		{
			var scan = await _fullScanIndex.ScanAsync(projectKey, query, ct);
			scanRanking = scan.SessionIds;
			scanCapped = scan.Capped;
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

		var rankedAll = RankDiscovery(pool, _ordering);

		// THE PAGEABLE POOL is the discovery order — the sessions that WILL BE SHOWN, ranked. Not the
		// intermediate digest entries: a digest is how a session was found, not the thing the caller pages
		// through, and several digests can point at one session. Truncated to the declared depth so the
		// boundary a caller is told about is a constant, and recorded as PoolBounded when it actually bit.
		var poolBounded = rankedAll.Count > DiscoveryPoolLimit;
		var ranked = rankedAll.Count > DiscoveryPoolLimit ? rankedAll.Take(DiscoveryPoolLimit).ToList() : rankedAll;

		// Discovery retrievers: OR the term/fullscan legs' lexical confirmation into the digest
		// leg's provenance — a verbatim-only match is still a LEXICAL discovery signal, just from
		// a different index (and the whole digest provenance is off when distillation never ran).
		// Folded in HERE, inside the pool, because it describes the pass that DECIDED this order and must
		// be reported identically by every page served from it.
		var retrievers = digestRetrievers with { Lexical = digestRetrievers.Lexical || termRanking.Count > 0 || scanRanking.Count > 0 };

		var rows = new List<DiscoveredSession>(ranked.Count);
		foreach (var hit in ranked)
		{
			var (sessionId, agent) = Provenance(hit.Entry);
			if (agent.Length == 0 && headers is not null && headers.TryGetValue(sessionId, out var hdr))
				agent = hdr.Agent; // term/fullscan-only candidate — the digest metadata never carried an agent
			rows.Add(new DiscoveredSession(sessionId, agent, hit.Entry.Description,
				sourcesBySession.GetValueOrDefault(sessionId, (IReadOnlyList<string>)["digest"]), hit.Score));
		}
		return (rows, retrievers, poolBounded, scanCapped);
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
		SearchOrderingPolicies rerank)
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
//
// PAGING (spec: result-set-pageable). `PoolLimit` is how deep the discovery order may be walked;
// `PoolBounded` says that depth was actually reached, so the pool is a PREFIX of what discovery found;
// `MoreInPool` says rows remain after this page. `DataVersion` stamps the discovery ORDER a cursor is
// bound to, and `LastPoolKey` is the session this page ended on — the position a resume token names.
//
// NOTE on ranking modes (spec: search-ranking-modes-uniform-across-entities, search-rerank-for-sessions):
// sessions DO carry a cross-encoder rerank, on BOTH stages — `Discovery` (this envelope) carries the
// digest leg's `Ranking` (Reranked/DegradedRrf/ChosenRrf), and each `SessionSearchCandidate.Retrievers`
// carries the episodic-hydration leg's own `Ranking`, same three-way contract memory/tasks use. Neither
// is hardcoded: `SearchAsync`'s `mode` parameter is the caller's choice, threaded through both legs — the
// fused-legs' PRESENTATION reshape (SearchOrderingPolicies: freshness decay, MMR) is a SEPARATE axis from
// this ranking-mode choice and runs regardless of it.
public sealed record SessionSearchOutcome(
	bool Distilled,
	string? Reason,
	IReadOnlyList<SessionSearchCandidate> Candidates,
	SearchRetrievers Discovery,
	bool? FullScanRequested = null,
	bool? FullScanRan = null,
	string? FullScanReason = null,
	bool? FullScanCapped = null,
	int? PoolLimit = null,
	bool PoolBounded = false,
	bool MoreInPool = false,
	string? DataVersion = null,
	string? LastPoolKey = null);
