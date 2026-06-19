using System.ComponentModel;
using ModelContextProtocol.Server;
using Microsoft.AspNetCore.Http;
using PetBox.Core.Auth;
using PetBox.Core.Features;
using PetBox.Sessions.Contract;
using PetBox.Web.Mcp.Contract;

namespace PetBox.Web.Mcp;

// MCP surface for agent sessions: a per-project store of working-plan blobs keyed
// by agent-supplied sessionId. A thin adapter over ISessionService (the single door);
// it must not open the sessions context directly (a NetArchTest enforces this).
// Read-mostly. Reuses the Tasks scopes/feature.
//
// Tools just THROW on a failed Assert* (or any deeper error); McpErrorEnvelopeFilter
// converts the exception into the structured {error} body centrally. Return types stay
// concrete; the success schema is advertised via [McpServerTool(OutputSchemaType)].
[McpServerToolType]
public static class SessionTools
{
	[McpServerTool(Name = "session.upsert", Title = "Save a session blob", UseStructuredContent = true, OutputSchemaType = typeof(SessionUpsertResult))]
	[Description("""
		Save an agent session's content as the latest snapshot (last-write-wins, no history).
		Requires tasks:write. The content is stored as a single message; the per-turn multi-message
		transcript is pushed by the Stop-hook over REST. Result: { sessionId, version, messageCount }
		where version is the last message's ordinal.
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

	[McpServerTool(Name = "session.get", Title = "Get a session", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(SessionGetResult))]
	[Description("""
		Get the active session blob by id, or null. The blob can be read INCREMENTALLY
		(spec bounded-result-sets): pass `tail` for the last N chars, or `offset`+`limit`
		for a window; with none, the full blob is returned. `length` (total chars) is
		ALWAYS returned so a caller can poll for growth and then read only the new tail.
		Requires tasks:read.
		""")]
	public static async Task<SessionGetResult?> GetAsync(
		IHttpContextAccessor http, FeatureFlags features, ISessionService sessions,
		string projectKey, string sessionId,
		[Description("Return only the last N chars of the blob (0 = off). Takes precedence over offset/limit.")] int tail = 0,
		[Description("Start reading at this char offset (default 0).")] int offset = 0,
		[Description("Max chars returned from offset (0 = to end).")] int limit = 0,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksRead);
		var s = await sessions.GetAsync(projectKey, sessionId, ct);
		if (s is null) return null;
		var full = s.Content;
		return new SessionGetResult(s.SessionId, s.Agent, Window(full, tail, offset, limit), full.Length, s.Version);
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

	[McpServerTool(Name = "session.delete", Title = "Delete a session", Destructive = true, UseStructuredContent = true, OutputSchemaType = typeof(SessionDeletedResult))]
	[Description("""
		Soft-delete a session: it disappears from session.list/session.get but the row is kept;
		a later session.upsert (or REST push) of the same sessionId resurrects it. Idempotent —
		deleting a missing or already-deleted session returns { deleted: false }. Requires tasks:write.
		""")]
	public static async Task<SessionDeletedResult> DeleteAsync(
		IHttpContextAccessor http, FeatureFlags features, ISessionService sessions,
		string projectKey, string sessionId, CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksWrite);
		return new SessionDeletedResult(await sessions.DeleteAsync(projectKey, sessionId, ct), sessionId);
	}

	[McpServerTool(Name = "session.search", Title = "Search the session archive", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(SessionSearchResultView))]
	[Description("""
		Two-stage search over the session archive. Stage 1 DISCOVERY: hybrid (lexical FTS ⊕ semantic
		vectors, RRF-fused) over per-session facts digests — the `session-digests` memory store that
		background distillation maintains. Stage 2 EPISODIC: the top `sessions` candidates are lazily
		hydrated (transient in-memory index: russian-stem FTS + vectors) and searched INSIDE, up to
		`hitsPerSession` messages each. Every hit carries the message ordinal — the provenance bridge:
		jump to the verbatim source with session.get. Both stages report retrievers
		{ lexical, semantic, degraded }. `distilled:false` means the project has no digest store yet
		(distillation runs in the background, ~minutes after a session settles) — not "no matches";
		in that case `reason` carries a machine-readable code (currently "no-digest-store").
		Requires tasks:read + memory:read.
		""")]
	public static async Task<SessionSearchResultView> SearchAsync(
		IHttpContextAccessor http, FeatureFlags features, PetBox.Web.Search.SessionSearchService search,
		string projectKey, string query,
		[Description("How many discovered sessions to hydrate and search inside (default 10, max 30).")] int sessions = 0,
		[Description("Max hits returned per session (default 5, max 20).")] int hitsPerSession = 0,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertFeature(features, Feature.Memory);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksRead);
		ModuleMcp.AssertScope(http, ApiKeyScopes.MemoryRead);

		var o = await search.SearchAsync(projectKey, query, sessions, hitsPerSession, ct);
		return new SessionSearchResultView(
			o.Distilled,
			o.Reason,
			o.Candidates.Select(c => new SessionSearchSessionView(
				c.SessionId, c.Agent, c.Description,
				c.Hits.Select(h => new SessionSearchHitView(h.Message, h.Role, h.Snippet, h.Score, h.Retriever)).ToList(),
				new RetrieverInfo(c.Retrievers.Lexical, c.Retrievers.Semantic, c.Retrievers.Degraded))).ToList(),
			new RetrieverInfo(o.Discovery.Lexical, o.Discovery.Semantic, o.Discovery.Degraded));
	}

	[McpServerTool(Name = "session.list", Title = "List sessions", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(SessionListResult))]
	[Description("List active sessions in a project. Requires tasks:read.")]
	public static async Task<SessionListResult> ListAsync(
		IHttpContextAccessor http, FeatureFlags features, ISessionService sessions,
		string projectKey, CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksRead);
		var list = await sessions.ListAsync(projectKey, ct);
		return new SessionListResult(list.Select(s => new SessionRowView(s.SessionId, s.Agent, s.Version)).ToList());
	}
}
