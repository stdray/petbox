using System.ComponentModel;
using ModelContextProtocol.Server;
using Microsoft.AspNetCore.Http;
using PetBox.Core.Auth;
using PetBox.Core.Contract;
using PetBox.Core.Features;
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
[McpServerToolType]
public static class SessionTools
{
	[McpServerTool(Name = "session_upsert", Title = "Save a session blob", UseStructuredContent = true, OutputSchemaType = typeof(SessionUpsertResult))]
	[Description("""
		PUT (full snapshot replace): save an agent session's content as the latest snapshot —
		last-write-wins, no history, no field merge; always send the complete blob (it REPLACES
		whatever is stored, including a session built up by session_append). Kept for repair/import;
		incremental pushes should use session_append instead. Requires tasks:write.
		The content is stored as a single message; the per-turn multi-message transcript is pushed
		by the Stop-hook over REST. Result: { sessionId, version, messageCount } where version is
		the last message's ordinal.
		""")]
	public static async Task<SessionUpsertResult> UpsertAsync(
		IHttpContextAccessor http, FeatureFlags features, ISessionService sessions,
		string projectKey, string sessionId, string agent, string content,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksWrite);

		// MCP is the degenerate single-blob writer; store it as one message. Latest-snapshot
		// replaces any prior content for this sessionId.
		var messages = new[] { new SessionMessageInput("session", content) };
		var o = await sessions.UpsertAsync(projectKey, sessionId, agent, messages, ct);
		return new SessionUpsertResult(o.SessionId, o.Version, o.MessageCount);
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
		Result: { sessionId, applied, lastOrdinal, appended, reason }.
		""")]
	public static async Task<SessionAppendResult> AppendAsync(
		IHttpContextAccessor http, FeatureFlags features, ISessionService sessions,
		string projectKey, string sessionId, string agent,
		[Description("Ordinal (1-based) of the first message in this batch.")] long fromOrdinal,
		[Description("Array of {role, content} messages, in transcript order — the same shape session_get returns.")] SessionMessageDto[] messages,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksWrite);

		var inputs = messages
			.Where(m => !string.IsNullOrEmpty(m.Content))
			.Select(m => new SessionMessageInput(m.Role ?? "", m.Content!))
			.ToList();

		var o = await sessions.AppendAsync(projectKey, sessionId, agent, fromOrdinal, inputs, ct);
		return new SessionAppendResult(o.SessionId, o.Applied, o.LastOrdinal, o.Appended, o.Applied ? null : "gap");
	}

	[McpServerTool(Name = "session_get", Title = "Get a session", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(SessionGetResult))]
	[Description("""
		Get the active session blob by id. `sessionId` may be the full id OR a unique PREFIX of
		one (the short form digests and session_search snippets use — e.g. the first block of the
		UUID); the exact id wins when both match, and a prefix that collides with 2+ sessions is
		an ERROR that lists the candidates rather than guessing. A missing/unknown id is a
		not-found ERROR (never a bare null: a declared outputSchema demands structured content, so
		a null result is rejected by strict MCP clients — the error rides the isError channel
		instead). The returned `sessionId` is always the resolved full id. The blob can be read
		INCREMENTALLY (spec bounded-result-sets): pass `tail` for the last N chars, or
		`offset`+`limit` for a window; with none, the full blob is returned. `length` (total
		chars) is ALWAYS returned so a caller can poll for growth and then read only the new tail.
		Requires tasks:read.
		""")]
	public static async Task<SessionGetResult> GetAsync(
		IHttpContextAccessor http, FeatureFlags features, ISessionService sessions,
		string projectKey,
		[Description("Full session id or a unique prefix of one (e.g. the first UUID block).")] string sessionId,
		[Description("Return only the last N chars of the blob (0 = off). Takes precedence over offset/limit.")] int tail = 0,
		[Description("Start reading at this char offset (default 0).")] int offset = 0,
		[Description("Max chars returned from offset (0 = to end).")] int limit = 0,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksRead);
		var resolvedId = await ResolveOrThrowAsync(sessions, projectKey, sessionId, ct);
		var s = resolvedId is null ? null : await sessions.GetAsync(projectKey, resolvedId, ct);
		if (s is null) throw new InvalidOperationException($"session '{sessionId}' not found in project '{projectKey}'");
		var full = s.Content;
		return new SessionGetResult(s.SessionId, s.Agent, Window(full, tail, offset, limit), full.Length, s.Version);
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

	// Incremental read of a plan blob: `tail` (last N chars) wins; else the [offset, offset+limit)
	// window (limit 0 = to end). All bounds are clamped so out-of-range args can't throw.
	static string Window(string s, int tail, int offset, int limit)
	{
		if (tail > 0) return tail >= s.Length ? s : s[^tail..];
		var start = Math.Clamp(offset, 0, s.Length);
		var rest = s.Length - start;
		var count = limit <= 0 ? rest : Math.Min(limit, rest);
		return s.Substring(start, count);
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
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksWrite);
		// Resolve (throws on ambiguity) so a prefix can never delete the wrong session; a miss
		// stays the idempotent { deleted: false }.
		var resolvedId = await ResolveOrThrowAsync(sessions, projectKey, sessionId, ct);
		var deleted = resolvedId is not null && await sessions.DeleteAsync(projectKey, resolvedId, ct);
		return new SessionDeletedResult(deleted, resolvedId ?? sessionId);
	}

	[McpServerTool(Name = "session_delta", Title = "Session archive delta since cursor", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(SessionDeltaResult))]
	[Description("Return sessions changed since `sinceVersion` (no writes) — the sessions family's catch-up surface, completing the uniform-entity-verbs matrix. Sessions are last-write-wins blobs with NO store-wide version watermark (each session's `version` is only its own message ordinal), so the cursor is the newest session's Updated time as Unix epoch MILLISECONDS: this returns active sessions with Updated-ms > sinceVersion (rows { sessionId, agent, version }, freshest first) and `currentVersion` = the max Updated-ms — pass it back as the next `sinceVersion` (0 = the whole archive). LIMITATIONS (documented): a soft-delete is NOT surfaced (a removed session just drops out — use session_search for the current set), and same-millisecond writes after your read may be missed until their next change (timestamp-cursor granularity). Hard ~30k-char output budget. Requires tasks:read.")]
	public static async Task<SessionDeltaResult> DeltaAsync(
		IHttpContextAccessor http, FeatureFlags features, ISessionService sessions,
		string projectKey,
		[Description("The Unix-epoch-millisecond cursor from a prior session_delta `currentVersion` (0 = the whole archive).")] long sinceVersion,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksRead);

		// Sessions have no store-wide monotonic version — `Updated` (bumped to UtcNow on every
		// upsert/append) is the real monotonic field, so the cursor is Updated-as-unix-ms. A pure
		// adapter over ListAsync (all active headers): compute the max cursor, keep the ones that
		// moved past `sinceVersion`, freshest first, budget-enveloped.
		var headers = await sessions.ListAsync(projectKey, ct);
		var current = headers.Count == 0 ? 0L : headers.Max(h => UpdatedMs(h.Updated));
		var changed = headers
			.Where(h => UpdatedMs(h.Updated) > sinceVersion)
			.OrderByDescending(h => UpdatedMs(h.Updated))
			.Select(h => new SessionSearchItemView(h.SessionId, h.Agent, h.Version))
			.ToList();
		var (kept, omitted) = new ResponseBudget().Take(changed);
		return omitted == 0
			? new SessionDeltaResult(current, kept)
			: new SessionDeltaResult(current, kept, Truncated: true, Omitted: omitted, Hint: DeltaBudgetHint);
	}

	// The session-archive cursor: a session's `Updated` time as Unix epoch milliseconds. Stored
	// UtcNow reads back with an unspecified Kind, so pin it to UTC before converting.
	static long UpdatedMs(DateTime updated) =>
		new DateTimeOffset(DateTime.SpecifyKind(updated, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

	// Surfaced on SessionDeltaResult.Hint when the changed rows were cut by the response budget.
	const string DeltaBudgetHint =
		"Output budget exceeded: changed-session rows were truncated (see truncated/omitted). Advance " +
		"`sinceVersion` toward `currentVersion` and page, or read one session with session_get.";

	[McpServerTool(Name = "session_search", Title = "Read the session archive (list + search)", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(SessionSearchResultView))]
	[Description("""
		THE session read verb — LISTING (no `q`) of the project's sessions, or a two-stage
		SEARCH (`q`) over the archive (digest ⊕ verbatim-term discovery, then in-session episodic
		hits with message ordinals for session_get). `fullScan` is gated by deployment permission
		(never automatic). Listing needs tasks:read; search also needs memory:read. Hard ~30k-char
		output budget.
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
		with session_get. Items then carry { sessionId, agent, description, hits[],
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
		[Description("Search query. Omit for a deterministic listing of the project's sessions (list = search without q).")] string? q = null,
		[Description("With q: how many discovered sessions to hydrate and search inside (default 10, max 30).")] int sessions = 0,
		[Description("With q: max hits returned per session (default 5, max 20).")] int hitsPerSession = 0,
		[Description("With q: opt into the full-scan escape hatch (raw substring scan over every session). Only actually runs if the deployment's permission setting also allows it — see fullScanRan/fullScanReason in the response. Default false: never on automatically.")] bool fullScan = false,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		projectKey = ModuleMcp.ResolveProject(http, projectKey);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksRead);

		if (string.IsNullOrWhiteSpace(q))
		{
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

		var o = await search.SearchAsync(projectKey, q, sessions, hitsPerSession, fullScan, ct);
		var items = o.Candidates.Select(c => new SessionSearchItemView(
			c.SessionId, c.Agent,
			Description: c.Description,
			Hits: c.Hits.Select(h => new SessionSearchHitView(h.Message, h.Role, h.Snippet, h.Score, h.Retriever)).ToList(),
			Retrievers: new RetrieverInfo(c.Retrievers.Lexical, c.Retrievers.Semantic, c.Retrievers.Degraded),
			Sources: c.Sources)).ToList();
		var (kept, omitted) = new ResponseBudget().Take(items);
		return new SessionSearchResultView(
			kept,
			Distilled: o.Distilled,
			Reason: o.Reason,
			Retrievers: new RetrieverInfo(o.Discovery.Lexical, o.Discovery.Semantic, o.Discovery.Degraded),
			Truncated: omitted > 0 ? true : null,
			Omitted: omitted > 0 ? omitted : null,
			Hint: omitted > 0 ? SearchBudgetHint : null,
			FullScanRequested: o.FullScanRequested,
			FullScanRan: o.FullScanRan,
			FullScanReason: o.FullScanReason,
			FullScanCapped: o.FullScanCapped);
	}

	// Surfaced on SessionSearchResultView.Hint when listing rows were cut by the budget.
	const string ListBudgetHint =
		"Output budget exceeded: session rows were truncated (see truncated/omitted). Find a " +
		"session by content by passing `q` (session_search), or read one directly with session_get.";

	// Surfaced when a query answer was cut by the budget.
	const string SearchBudgetHint =
		"Output budget exceeded: session items were truncated (see truncated/omitted). Narrow " +
		"the read: fewer `sessions`, a lower `hitsPerSession`, or jump to one source with session_get.";
}
