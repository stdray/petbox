using System.ComponentModel;
using ModelContextProtocol.Server;
using Microsoft.AspNetCore.Http;
using PetBox.Core.Auth;
using PetBox.Core.Features;
using PetBox.Sessions.Contract;

namespace PetBox.Web.Mcp;

// MCP surface for agent sessions: a per-project store of working-plan blobs keyed
// by agent-supplied sessionId. A thin adapter over ISessionService (the single door);
// it must not open the sessions context directly (a NetArchTest enforces this).
// Read-mostly. Reuses the Tasks scopes/feature.
[McpServerToolType]
public static class SessionTools
{
	[McpServerTool(Name = "session.append", Title = "Save a session plan blob")]
	[Description("""
		Create or update an agent session's plan blob. Requires tasks:write.
		`version` is the baseline you last saw (0 = new session). Two writers on the
		same sessionId with a stale baseline get a conflict. Result:
		{ applied, currentVersion, conflicts[] }.
		""")]
	public static async Task<object> AppendAsync(
		IHttpContextAccessor http, FeatureFlags features, ISessionService sessions,
		string projectKey, string sessionId, string agent, string content,
		long version = 0, CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksWrite);

		var r = (await sessions.AppendAsync(projectKey, sessionId, agent, content, version, ct)).Result;
		return new
		{
			applied = r.Applied,
			currentVersion = r.CurrentVersion,
			conflicts = r.Conflicts.Select(c => new { key = c.Key, kind = c.Kind.ToString() }).ToList(),
		};
	}

	[McpServerTool(Name = "session.get", Title = "Get a session", ReadOnly = true)]
	[Description("""
		Get the active session blob by id, or null. The blob can be read INCREMENTALLY
		(spec bounded-result-sets): pass `tail` for the last N chars, or `offset`+`limit`
		for a window; with none, the full blob is returned. `length` (total chars) is
		ALWAYS returned so a caller can poll for growth and then read only the new tail.
		Requires tasks:read.
		""")]
	public static async Task<object?> GetAsync(
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
		var full = s.Content ?? "";
		return new { sessionId = s.Key, agent = s.Agent, content = Window(full, tail, offset, limit), length = full.Length, version = s.Version };
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

	[McpServerTool(Name = "session.list", Title = "List sessions", ReadOnly = true)]
	[Description("List active sessions in a project. Requires tasks:read.")]
	public static async Task<object> ListAsync(
		IHttpContextAccessor http, FeatureFlags features, ISessionService sessions,
		string projectKey, CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksRead);
		var list = await sessions.ListAsync(projectKey, ct);
		return new { sessions = list.Select(s => new { sessionId = s.Key, agent = s.Agent, version = s.Version }).ToList() };
	}
}
