using System.ComponentModel;
using ModelContextProtocol.Server;
using Microsoft.AspNetCore.Http;
using PetBox.Core.Auth;
using PetBox.Core.Contract;
using PetBox.Core.Features;
using PetBox.Core.Search;
using PetBox.Sessions.Contract;
using PetBox.Web.Mcp.Contract;

namespace PetBox.Web.Mcp;

// MCP surface for agent sessions: a per-project store of working-plan blobs keyed
// by agent-supplied sessionId. A thin adapter over ISessionService (the single door);
// it must not open the sessions context directly (a NetArchTest enforces this).
// Read-mostly. Reuses the Tasks scopes/feature.
//
// session_search is the ONE read verb (spec uniform-entity-verbs v2: list = search
// without q). Unlike tasks/memory, no ISearchService seam is implemented here — the
// query mode is a Web-composed two-stage pipeline (SessionSearchService: memory digests
// → episodic hydration) whose knobs (sessions/hitsPerSession) and nested hit rows don't
// map onto SearchRequest's axes; the envelope FORM is shared, the seam is the family's
// documented exception.
//
// Tools just THROW on a failed Assert* (or any deeper error); McpErrorEnvelopeFilter
// converts the exception into the structured {error} body centrally. Return types stay
// concrete; the success schema is advertised via [McpServerTool(OutputSchemaType)].
// TENANT DECLARATION (spec authz-scope-declaration): the `projectKey` ARGUMENT, all six verbs.
//
// Five of them take a REQUIRED projectKey and had a matching AssertProject; the sixth,
// session_search, takes an OPTIONAL one and resolves the caller's own default first
// (ModuleMcp.ResolveProject). The PEP reads the same fallback from the same place —
// McpTenantEnforcementFilter resolves an absent Argument to CallerTenant.DefaultProjectOf, which IS
// what ResolveProject uses — so one declaration covers both shapes and cannot disagree with either.
//
// ResolveProject STAYS on session_search: it is the resolver whose RETURN VALUE the body then uses,
// not a check that can be deleted. Its now-redundant AssertProject on the line below it goes.
[McpServerToolType]
[TenantFrom(TenantSource.Argument, "projectKey")]
public static class SessionTools
{
	[McpServerTool(Name = "session_upsert", Title = "Save a session blob", UseStructuredContent = true, OutputSchemaType = typeof(SessionUpsertResult))]
	[Description("""
		PUT (full snapshot replace): save an agent session's content as the latest snapshot —
		last-write-wins, no history, no field merge; always send the complete blob (it REPLACES
		whatever is stored, including a session built up by session_append). Kept for repair/import;
		incremental pushes should use session_append instead. Requires tasks:write.
		""" + "\n\t\t" + ModuleMcp.SizeGuidanceText + """

		The content is stored as a single message; the per-turn multi-message transcript is pushed
		by the Stop-hook over REST. Result: { sessionId, version, messageCount, warning? } where
		version is the last message's ordinal and `warning` is set when this call's payload was
		large enough to risk the client-side truncation described above (informational, never a
		refusal — the write always applies); omitted the rest of the time.
		""")]
	public static async Task<SessionUpsertResult> UpsertAsync(
		IHttpContextAccessor http, FeatureFlags features, ISessionService sessions,
		string projectKey, string sessionId, string agent, string content,
		[Description("Optional observed client metadata as a JSON object string (e.g. roleBinding stamp). Last-write-wins when set; omit to keep existing.")] string? meta = null,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksWrite);

		// MCP is the degenerate single-blob writer; store it as one message. Latest-snapshot
		// replaces any prior content for this sessionId.
		var messages = new[] { new SessionMessageInput("session", content) };
		var o = await sessions.UpsertAsync(projectKey, sessionId, agent, messages, meta, ct);
		// card size-warning-not-wired-to-write-verbs: session_upsert always writes (no
		// conflict/reject path), so — mirroring memory_remember — the warning is unconditional.
		return new SessionUpsertResult(o.SessionId, o.Version, o.MessageCount, ModuleMcp.SizeWarningOrNull(http));
	}

	[McpServerTool(Name = "session_append", Title = "Append messages to a session", UseStructuredContent = true, OutputSchemaType = typeof(SessionAppendResult))]
	[Description("""
		Incrementally append transcript messages against the SERVER-authoritative cursor
		(spec session-append-wire) — the client keeps no durable state and sends only the
		increment. `fromOrdinal` is the ordinal of the FIRST message in the batch; the server's
		cursor (lastOrdinal) is the current message count (0 for a new session, so a new session
		starts with fromOrdinal=1). Contiguous batches (fromOrdinal == lastOrdinal+1) append;
		OVERLAPPING batches apply idempotently (ordinals the server already holds are ignored,
		the new tail appends); a GAP (fromOrdinal > lastOrdinal+1) writes nothing and returns the
		structured reject { applied:false, reason:"gap", lastOrdinal } — resend from
		lastOrdinal+1. Requires tasks:write.
		""" + "\n\t\t" + ModuleMcp.SizeGuidanceText + """

		Result: { sessionId, applied, lastOrdinal, appended, reason, warning? } — `warning` is set
		when an APPLIED call's request payload was large enough to risk the client-side truncation
		described above (informational, never a refusal — the write already landed); omitted the
		rest of the time (including on a gap reject, where `reason` is already the signal to act on).
		""")]
	public static async Task<SessionAppendResult> AppendAsync(
		IHttpContextAccessor http, FeatureFlags features, ISessionService sessions,
		string projectKey, string sessionId, string agent,
		[Description("Ordinal (1-based) of the first message in this batch.")] long fromOrdinal,
		[Description("Array of {role, content} messages, in transcript order. session_get does NOT return this array shape — it returns the transcript joined into ONE string (`content`), not a per-message list. The bridge between the two verbs is the ORDINAL, not the wire shape: this call's `fromOrdinal` and session_get's/session_search's `lastOrdinal` share the same 1-based, dense message-count cursor.")] SessionMessageDto[] messages,
		[Description("Optional observed client metadata as a JSON object string (e.g. roleBinding stamp). Last-write-wins when set; omit to keep existing; not written on a gap reject.")] string? meta = null,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksWrite);

		var inputs = messages
			.Where(m => !string.IsNullOrEmpty(m.Content))
			.Select(m => new SessionMessageInput(m.Role ?? "", m.Content!))
			.ToList();

		var o = await sessions.AppendAsync(projectKey, sessionId, agent, fromOrdinal, inputs, meta, ct);
		// card size-warning-not-wired-to-write-verbs, mirroring MemoryTools.UpsertAsync point 4:
		// only warn about size on a write that actually landed — a gap reject already has its own
		// signal (reason:"gap").
		var warning = o.Applied ? ModuleMcp.SizeWarningOrNull(http) : null;
		return new SessionAppendResult(o.SessionId, o.Applied, o.LastOrdinal, o.Appended, o.Applied ? null : "gap", warning);
	}

	[McpServerTool(Name = "session_get", Title = "Get a session", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(SessionGetResult))]
	[Description("""
		Get the active session blob by id. `sessionId` may be the full id OR a unique PREFIX of
		one (the short form digests and session_search snippets use — e.g. the first block of the
		UUID); the exact id wins when both match, and a prefix that collides with 2+ sessions is
		an ERROR that lists the candidates rather than guessing. A missing/unknown id is a
		not-found ERROR (never a bare null: a declared outputSchema demands structured content, so
		a null result is rejected by strict MCP clients — the error rides the isError channel
		instead). The returned `sessionId` is always the resolved full id. The blob is COMPLETE by
		default (this is the pointed full read — the uniform bodyLen knob still applies: 0 = no
		body, N>0 = the first N chars, -1 = full). `length` (total chars, always the FULL blob's
		length regardless of bodyLen AND of fromOrdinal) is ALWAYS returned so a caller can poll
		for growth.

		INCREMENTAL READ — `fromOrdinal` (1-based, default 1) starts the window at that MESSAGE,
		so a growing session is re-read by what is new, not re-read whole. The unit is the message
		ordinal, the same cursor `session_append` writes against (fromOrdinal/lastOrdinal) and the
		same ordinal `session_search` hits carry — messages are immutable and dense 1..N, so this
		cursor can never go stale. The response's `lastOrdinal` (= the message count) is the cursor
		to pass back as `fromOrdinal+1` next time. `fromOrdinal` PAST `lastOrdinal` is NOT an error:
		it is the normal poll for growth and returns an empty body plus the current `lastOrdinal`.
		The window is exactly a suffix of the full blob. `bodyLen` and `fromOrdinal` compose
		predictably — "from here, this many chars"; neither takes precedence over the other.
		Requires tasks:read.
		""")]
	public static async Task<SessionGetResult> GetAsync(
		IHttpContextAccessor http, FeatureFlags features, ISessionService sessions,
		string projectKey,
		[Description("Full session id or a unique prefix of one (e.g. the first UUID block).")] string sessionId,
		[LogArg][Description("Body length knob (uniform contract): omitted = the FULL body (this is the pointed full read); 0 = no body; N>0 = the first N chars (\"…\" when cut); -1 = the full body.")] int? bodyLen = null,
		[LogArg][Description("Ordinal (1-based) of the first message to return — the incremental-read cursor (default 1 = the whole transcript). Pass the previous response's lastOrdinal+1 to read only what was appended since; past the last ordinal is not an error but an empty body + the current lastOrdinal.")] long fromOrdinal = 1,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksRead);
		// Reject 0/negative rather than leniently reading it as "the whole thing": ordinals are
		// 1-based everywhere on this surface (session_append rejects the same way), and a caller
		// holding a 0-based mental model must find out here, not by silently re-reading the
		// transcript it thought it was skipping.
		if (fromOrdinal < 1)
			throw new ArgumentOutOfRangeException(nameof(fromOrdinal), fromOrdinal, "fromOrdinal must be >= 1");
		var resolvedId = await ResolveOrThrowAsync(sessions, projectKey, sessionId, ct);
		var s = resolvedId is null ? null : await sessions.GetAsync(projectKey, resolvedId, ct);
		if (s is null) throw new InvalidOperationException($"session '{sessionId}' not found in project '{projectKey}'");
		// Two independent axes, applied in order and without precedence: fromOrdinal picks the
		// WINDOW (a suffix of the transcript), bodyLen then cuts LENGTH inside it. `length` keeps
		// reporting the full transcript so the char-axis growth signal survives both knobs.
		var window = s.ContentFrom(fromOrdinal);
		return new SessionGetResult(
			s.SessionId, s.Agent, ModuleMcp.Body(window, bodyLen, ModuleMcp.FullBody) ?? "",
			s.Length, s.Version, s.Messages.Count, s.MetaJson);
	}

	// Resolve a full-or-prefix session id to its stored full id. Returns null on a miss (the
	// caller renders its own not-found / idempotent-false), and THROWS on an ambiguous prefix so
	// the collision surfaces as a clear error listing the candidates — never a silent wrong pick.
	static async Task<string?> ResolveOrThrowAsync(
		ISessionService sessions, string projectKey, string sessionId, CancellationToken ct)
	{
		var r = await sessions.ResolveIdAsync(projectKey, sessionId, ct);
		if (r.Ambiguous.Count > 0)
			throw new InvalidOperationException(
				$"session id '{sessionId}' is ambiguous — it prefixes {r.Ambiguous.Count} sessions " +
				$"({string.Join(", ", r.Ambiguous)}). Pass more characters, or the full id.");
		return r.Match;
	}

	[McpServerTool(Name = "session_delete", Title = "Delete a session", Destructive = true, UseStructuredContent = true, OutputSchemaType = typeof(SessionDeletedResult))]
	[Description("""
		Soft-delete a session: it disappears from session_search/session_get but the row is kept;
		a later session_upsert (or REST push) of the same sessionId resurrects it. `sessionId` may
		be the full id or a unique PREFIX of one; an ambiguous prefix (2+ matches) is an ERROR that
		lists the candidates rather than deleting the wrong session. Idempotent — deleting a
		missing or already-deleted session returns { deleted: false }; on success `sessionId`
		echoes the resolved full id. Requires tasks:write.
		""")]
	public static async Task<SessionDeletedResult> DeleteAsync(
		IHttpContextAccessor http, FeatureFlags features, ISessionService sessions,
		string projectKey,
		[Description("Full session id or a unique prefix of one.")] string sessionId,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksWrite);
		// Resolve (throws on ambiguity) so a prefix can never delete the wrong session; a miss
		// stays the idempotent { deleted: false }.
		var resolvedId = await ResolveOrThrowAsync(sessions, projectKey, sessionId, ct);
		var deleted = resolvedId is not null && await sessions.DeleteAsync(projectKey, resolvedId, ct);
		return new SessionDeletedResult(deleted, resolvedId ?? sessionId);
	}

	[McpServerTool(Name = "session_search", Title = "Read the session archive (list + search)", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(SessionSearchResultView))]
	[Description("""
		THE session read verb — LISTING (no `q`) of the project's sessions, or a two-stage
		SEARCH (`q`) over the archive (digest ⊕ verbatim-term discovery, then in-session episodic
		hits with message ordinals for session_get). `q` is a relevance SELECTION over discovered
		sessions, not an enumeration — there is no session delta; for the complete session
		inventory list without `q`. `fullScan` is gated by deployment permission
		(never automatic). Listing needs tasks:read; search also needs memory:read. Hard ~30k-char
		output budget. Each hit's snippet follows the uniform `bodyLen` knob (omitted = a
		query-centered ~240-char preview; 0 = no snippet; N>0 = a wider/narrower preview; -1 =
		the full raw message).

		Cost — your context pays it. Hits carry verbatim transcript text, so widening how many
		sessions are hydrated and how many hits each returns multiplies the response fast —
		a single wide scan can add thousands of chars of raw messages, and `fullScan` is the
		widest of all.
		Cheap path: search with the defaults, read the descriptions and hits, then session_get
		the 1-3 sessions (at the hit ordinals) you actually need.
		Widening the scan "just in case" is the most expensive habit available here: it
		routinely spends a third of the response budget on text you will not read.
		[[full]]
		THE session read verb — one tool for both LISTING and SEARCH (list = search without
		`q`; replaces the former session.list).

		Without `q`: a deterministic LISTING of the project's active sessions — compact rows
		{ sessionId, agent, version }. Requires tasks:read.

		With `q`: a two-stage search over the session archive. Stage 1 DISCOVERY fuses up to
		THREE legs (RRF) over per-session state, no hydration: the `session-digests` memory
		store (an LLM-composed summary, hybrid lexical FTS ⊕ semantic vectors), a VERBATIM
		term index (full-text BM25 over the raw transcript — a distinctive term the digest
		summary dropped still surfaces the session through this leg alone), and — ONLY when
		`fullScan:true` is passed AND the deployment's two-key permission setting allows it
		(never on by default, never automatic) — a raw substring/phrase scan over every
		session (capped; see `fullScanCapped`). Stage 2 EPISODIC: the top `sessions`
		candidates are lazily hydrated (transient in-memory index: russian-stem FTS +
		vectors) and searched INSIDE, up to `hitsPerSession` messages each. Every hit
		carries the message ordinal — the provenance bridge: jump to the verbatim source
		with `session_get {fromOrdinal: <the hit's message ordinal>}`, which starts the
		read AT that message instead of returning the whole transcript. Each hit's
		`snippet` follows the uniform bodyLen contract: omitted
		= a query-centered ~240-char preview (the compact default — width-only reading of N,
		since a hit's natural anchor is the query match, not the message head); 0 = no
		snippet text; N>0 = a query-centered preview N chars wide; -1 = the FULL raw message
		(the same full body session_get returns at that ordinal). Items then carry
		{ sessionId, agent, description, hits[],
		retrievers, sources } — `sources` names which stage-1 leg(s) raised the session
		("digest"/"term"/"fullscan") — and the response the stage-1 `retrievers`;
		`distilled:false` means the project has no digest store yet (distillation runs in
		the background, ~minutes after a session settles) — not "no matches"; `reason`
		then carries a machine-readable code (currently "no-digest-store"). Requesting
		`fullScan:true` when it is not allowed does NOT silently ignore the flag: the
		response carries `fullScanRequested:true`, `fullScanRan:false` and
		`fullScanReason:"not-allowed"`. The two-stage pipeline needs memory too: requires
		tasks:read + memory:read.

		Both modes share one envelope: `items` plus the HARD OUTPUT BUDGET markers (~30k
		serialized chars; overflowing items are prefix-cut and flagged `truncated:true` +
		`omitted` + a narrowing `hint`; no markers = the complete answer).
		""")]
	public static async Task<SessionSearchResultView> SearchAsync(
		IHttpContextAccessor http, FeatureFlags features, ISessionService sessionSvc, PetBox.Web.Search.SessionSearchService search,
		string? projectKey = null,
		[LogArg(LogArgMode.Presence)][Description("Search query. Omit for a deterministic listing of the project's sessions (list = search without q).")] string? q = null,
		[LogArg][Description("With q: how many discovered sessions to hydrate and search inside (default 10, max 30).")] int sessions = 0,
		[LogArg][Description("With q: max hits returned per session (default 5, max 20).")] int hitsPerSession = 0,
		[LogArg][Description("With q: opt into the full-scan escape hatch (raw substring scan over every session). Only actually runs if the deployment's permission setting also allows it — see fullScanRan/fullScanReason in the response. Default false: never on automatically.")] bool fullScan = false,
		[LogArg][Description("With q: body length knob (uniform contract) for each hit's snippet — omitted = a query-centered ~240-char preview (the compact default); 0 = no snippet text; N>0 = a query-centered preview N chars wide; -1 = the full raw message (or jump there directly with session_get {fromOrdinal: the hit's `message` ordinal}).")] int? bodyLen = null,
		[LogArg(LogArgMode.Presence)][Description("With q: pagination — the opaque `nextCursor` from the previous page, passed back verbatim to continue after it. Keep every other argument identical while paging; a cursor from a different query is an ERROR, not a silent restart. It is bound to the discovery ORDER it was issued for, so a new session or a fresh digest mid-walk also errors — drop the cursor to start over.")] string? cursor = null,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		projectKey = await ModuleMcp.ResolveProject(http, projectKey, ct);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksRead);

		if (string.IsNullOrWhiteSpace(q))
		{
			// A cursor belongs to the walk that issued it. The listing branch has no ranked pool and no
			// resume token of its own (that is listing-keyset-memory-sessions' work), so a token arriving
			// here came from a QUERY walk — refuse it rather than ignoring it and serving an unrelated
			// first page that looks like a continuation.
			if (!string.IsNullOrWhiteSpace(cursor))
				throw new ArgumentException(
					"session_search: this cursor was issued for a `q` walk — dropping `q` changes both the "
					+ "selection and the ordering basis, so continuing would splice two orderings. Keep the "
					+ "query while paging, or drop the cursor.");
			// LISTING (the former session.list): compact rows, budget-enveloped.
			var list = await sessionSvc.ListAsync(projectKey, ct);
			var rows = list.Select(s => new SessionSearchItemView(s.SessionId, s.Agent, s.Version)).ToList();
			var (keptRows, omittedRows) = new ResponseBudget().Take(rows);
			return omittedRows == 0
				? new SessionSearchResultView(rows)
				: new SessionSearchResultView(keptRows, Truncated: true, Omitted: omittedRows, Hint: ListBudgetHint);
		}

		// QUERY: the two-stage pipeline (digest discovery → episodic hydration) leans on
		// the Memory module, so the extra feature/scope guards apply only here.
		ModuleMcp.AssertFeature(features, Feature.Memory);
		ModuleMcp.AssertScope(http, ApiKeyScopes.MemoryRead);

		// The token's POSITION is read up front (structure, format version and shape are all refused here);
		// its FINGERPRINT is checked below, the moment the discovery order — which the stamp is made of —
		// exists. Nothing is returned to the caller between the two, so the guarantee is unchanged: an
		// invalid cursor is an error, never a plausible-looking wrong page.
		var token = string.IsNullOrWhiteSpace(cursor) ? (KeysetCursor?)null : KeysetCursor.Peek(cursor, "session_search");
		// EDGE default (search-ranking-mode-is-caller-choice): an MCP verb is an agent acting on the
		// answer, where a ranking mistake costs more than latency — Precision, same as
		// memory_search/tasks_search. Not a caller-exposed argument (mirrors those two verbs).
		var o = await search.SearchAsync(projectKey, q, sessions, hitsPerSession, fullScan, bodyLen,
			afterSessionId: token?.Key, mode: SearchRankingMode.Precision, ct: ct);
		// The discovery ORDER moved out of the fingerprint and into the order commitment, where the other
		// two surfaces now carry it. Same guarantee, better words: a fingerprint mismatch tells the caller
		// to keep their arguments identical, which is wrong advice when their arguments were identical and
		// the server's ranking is what changed.
		var fingerprint = SearchFingerprint(projectKey, q, fullScan);
		token?.AssertFingerprint(fingerprint, "session_search");
		token?.AssertPoolOrder(o.DataVersion ?? "", "session_search");
		var items = o.Candidates.Select(c => new SessionSearchItemView(
			c.SessionId, c.Agent,
			Description: c.Description,
			Hits: c.Hits.Select(h => new SessionSearchHitView(h.Message, h.Role, h.Snippet, h.Score, h.Retriever)).ToList(),
			Retrievers: new RetrieverInfo(c.Retrievers.Lexical, c.Retrievers.Semantic, c.Retrievers.Degraded, c.Retrievers.DegradedReason,
				c.Retrievers.SemanticLag, c.Retrievers.Ranking),
			Sources: c.Sources)).ToList();
		var (kept, omitted) = new ResponseBudget().Take(items);
		// Rows remain if the pool has more behind this page OR the budget cut rows off this one.
		var more = o.MoreInPool || omitted > 0;
		// RESUME FROM THE LAST ROW ACTUALLY SENT, not from the end of the slice we considered. When the
		// response budget cut candidates off this page (omitted > 0), those candidates never reached the
		// caller — resuming past them would skip them for good, which is the same lost-rows defect the
		// budget seek in tasks_search and memory_search exists to avoid. Only when NOTHING was kept (the
		// whole slice went stale and hydrated to nothing) do we fall back to the slice end, because there
		// is no delivered row to resume from and stopping there would strand the rest of the pool.
		var resumeAfter = kept.Count > 0 ? kept[^1].SessionId : o.LastPoolKey;
		var nextCursor = more && resumeAfter is not null
			? new KeysetCursor(fingerprint, "", resumeAfter, projectKey, o.DataVersion ?? "").Encode()
			: null;
		// WHY THE WALK STOPPED — the SAME three words tasks_search and memory_search use. Always present
		// with `q`, so a caller never has to read "nextCursor is absent" and guess whether it reached the
		// end of the matches or the end of what discovery ever looked at.
		var stop = more ? SearchPoolStop.More
			: o.PoolBounded ? SearchPoolStop.PoolBoundary
			: SearchPoolStop.Exhausted;
		return new SessionSearchResultView(
			kept,
			Distilled: o.Distilled,
			Reason: o.Reason,
			Retrievers: new RetrieverInfo(o.Discovery.Lexical, o.Discovery.Semantic, o.Discovery.Degraded, o.Discovery.DegradedReason,
				o.Discovery.SemanticLag, o.Discovery.Ranking),
			Truncated: omitted > 0 ? true : null,
			Omitted: omitted > 0 ? omitted : null,
			Hint: omitted > 0 ? SearchBudgetHint : null,
			FullScanRequested: o.FullScanRequested,
			FullScanRan: o.FullScanRan,
			FullScanReason: o.FullScanReason,
			FullScanCapped: o.FullScanCapped,
			NextCursor: nextCursor,
			Stop: MemoryTools.StopWire(stop),
			PoolLimit: o.PoolLimit,
			PoolBoundaryHint: stop == SearchPoolStop.PoolBoundary ? PoolBoundaryHintText : null);
	}

	// Surfaced ONLY on stop:"pool-boundary" — the case a caller must not read as "that was everything".
	const string PoolBoundaryHintText =
		"Discovery depth reached (see poolLimit): more sessions were discovered than the pool walks, so "
		+ "this is a PREFIX of what matched, NOT all of it — and there is no further page to fetch. "
		+ "Narrow the read (a more specific `q`), or list without `q` to enumerate the archive.";

	// The query identity a cursor is bound to — the QUESTION only. The discovery ORDER lives in the
	// token's order commitment instead (see AssertPoolOrder at the call site), so the two failures stay
	// tellable apart: "you changed the query" versus "the ranking moved under you".
	// `sessions`/`hitsPerSession`/`bodyLen` are deliberately EXCLUDED: they shape a page, not the
	// sequence, so a caller may vary them mid-walk.
	static string SearchFingerprint(string projectKey, string? query, bool fullScan) =>
		KeysetCursor.FingerprintOf("session_search", projectKey, query, fullScan ? "1" : "0");

	// Surfaced on SessionSearchResultView.Hint when listing rows were cut by the budget.
	const string ListBudgetHint =
		"Output budget exceeded: session rows were truncated (see truncated/omitted). Find a " +
		"session by content by passing `q` (session_search), or read one directly with session_get.";

	// Surfaced when a query answer was cut by the budget.
	const string SearchBudgetHint =
		"Output budget exceeded: session items were truncated (see truncated/omitted). Narrow " +
		"the read: fewer `sessions`, a lower `hitsPerSession`, or jump to one source with session_get.";
}
