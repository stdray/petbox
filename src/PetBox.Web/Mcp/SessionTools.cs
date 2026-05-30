using System.ComponentModel;
using ModelContextProtocol.Server;
using Microsoft.AspNetCore.Http;
using PetBox.Core.Auth;
using PetBox.Core.Data.Temporal;
using PetBox.Core.Features;
using PetBox.Sessions.Data;

namespace PetBox.Web.Mcp;

// MCP surface for agent sessions: a per-project store of working-plan blobs keyed
// by agent-supplied sessionId. No named container — the project's sessions file is
// materialized on first append. Read-mostly. Reuses the Tasks scopes/feature.
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
		IHttpContextAccessor http, FeatureFlags features, ISessionStore sessions,
		string projectKey, string sessionId, string agent, string content,
		long version = 0, CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksWrite);

		var ctx = sessions.GetContext(projectKey);
		var r = await TemporalStore.UpsertAsync(ctx, new[]
		{
			new SessionRow { Key = sessionId, Version = version, Agent = agent, Content = content },
		}, ct: ct);
		return new
		{
			applied = r.Applied,
			currentVersion = r.CurrentVersion,
			conflicts = r.Conflicts.Select(c => new { key = c.Key, kind = c.Kind.ToString() }).ToList(),
		};
	}

	[McpServerTool(Name = "session.get", Title = "Get a session", ReadOnly = true)]
	[Description("Get the active session blob by id, or null. Requires tasks:read.")]
	public static async Task<object?> GetAsync(
		IHttpContextAccessor http, FeatureFlags features, ISessionStore sessions,
		string projectKey, string sessionId, CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksRead);
		var s = await sessions.GetAsync(projectKey, sessionId, ct);
		return s is null ? null : new { sessionId = s.Key, agent = s.Agent, content = s.Content, version = s.Version };
	}

	[McpServerTool(Name = "session.list", Title = "List sessions", ReadOnly = true)]
	[Description("List active sessions in a project. Requires tasks:read.")]
	public static async Task<object> ListAsync(
		IHttpContextAccessor http, FeatureFlags features, ISessionStore sessions,
		string projectKey, CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksRead);
		var list = await sessions.ListAsync(projectKey, ct);
		return new { sessions = list.Select(s => new { sessionId = s.Key, agent = s.Agent, version = s.Version }).ToList() };
	}
}
