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
[McpServerToolType]
public static class SessionTools
{
	[McpServerTool(Name = "session.upsert", Title = "Save a session blob", UseStructuredContent = true)]
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

	[McpServerTool(Name = "session.get", Title = "Get a session", ReadOnly = true, UseStructuredContent = true)]
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

	[McpServerTool(Name = "session.list", Title = "List sessions", ReadOnly = true, UseStructuredContent = true)]
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
