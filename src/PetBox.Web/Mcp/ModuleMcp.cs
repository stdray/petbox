using System.Text.Json;
using Microsoft.AspNetCore.Http;
using PetBox.Core.Auth;
using PetBox.Core.Features;

namespace PetBox.Web.Mcp;

// Shared guards + JSON helpers for the tasks.*/memory.*/session.* MCP tools.
// Mirrors the private AssertProject/AssertScope helpers in DataTools/LogTools,
// factored out so the three new tool classes don't each copy them. Claims
// ("project", "scopes") are set by ApiKeyAuthenticationHandler.
static class ModuleMcp
{
	public static void AssertProject(IHttpContextAccessor http, string projectKey)
	{
		var ctx = http.HttpContext ?? throw new InvalidOperationException("No HttpContext");
		var claim = ctx.User.Claims.FirstOrDefault(c => c.Type == "project")?.Value;
		if (!ProjectScope.Authorizes(claim, projectKey))
			throw new UnauthorizedAccessException($"ApiKey is not scoped to project '{projectKey}'");
	}

	// Reserved cross-cutting containers shared across projects (parallels the memory
	// module's "$workspace"): a workspace-level methodology quartet lives here. Reachable
	// by ANY key holding the module scope — the single-project claim is not required, since
	// the point is to be shared. Per-workspace isolation is future work.
	public static readonly string[] SharedProjects = ["$workspace"];

	// Like AssertProject, but a reserved shared container is allowed regardless of the
	// key's project claim (the module scope still gates the call).
	public static void AssertProjectOrShared(IHttpContextAccessor http, string projectKey)
	{
		if (SharedProjects.Contains(projectKey, StringComparer.Ordinal)) return;
		AssertProject(http, projectKey);
	}

	// Resolve the effective projectKey for tools where it is OPTIONAL: when omitted,
	// default to the key's single-project claim so a project-scoped key need not repeat
	// it. A cross-project ("*") key has no single project to default to, so an explicit
	// projectKey is required there. Always authorizes the result against the claim.
	public static string ResolveProject(IHttpContextAccessor http, string? projectKey)
	{
		var ctx = http.HttpContext ?? throw new InvalidOperationException("No HttpContext");
		var claim = ctx.User.Claims.FirstOrDefault(c => c.Type == "project")?.Value;
		var effective = projectKey;
		if (string.IsNullOrWhiteSpace(effective))
		{
			if (string.IsNullOrEmpty(claim) || claim == ProjectScope.AllProjects)
				throw new ArgumentException("projectKey is required (the API key is not scoped to a single project)");
			effective = claim;
		}
		if (!ProjectScope.Authorizes(claim, effective))
			throw new UnauthorizedAccessException($"ApiKey is not scoped to project '{effective}'");
		return effective;
	}

	public static void AssertScope(IHttpContextAccessor http, string required)
	{
		var ctx = http.HttpContext ?? throw new InvalidOperationException("No HttpContext");
		var scopes = ctx.User.Claims.FirstOrDefault(c => c.Type == "scopes")?.Value ?? "";
		var parts = scopes.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		if (!parts.Contains(required, StringComparer.Ordinal))
			throw new UnauthorizedAccessException($"ApiKey lacks required scope '{required}'");
	}

	public static void AssertFeature(FeatureFlags features, Feature feature)
	{
		if (!features.IsEnabled(feature))
			throw new InvalidOperationException($"feature '{feature}' is disabled");
	}

	// Runs a tool body, converting any thrown exception into a structured, agent-
	// readable error result instead of the MCP framework's opaque "An error occurred
	// invoking 'X'". Surfaces the cause (scope/feature/project assert, or a deeper
	// server-side failure with message + stack) to the caller. Request-level logging
	// (incl. MCP, into the self-log) is handled centrally by RequestLoggingMiddleware.
	public static async Task<object> GuardAsync(Func<Task<object>> body)
	{
		try
		{
			return await body();
		}
		catch (Exception ex)
		{
			return new { error = new { type = ex.GetType().Name, message = ex.Message, detail = ex.ToString() } };
		}
	}

	public static string? OptStr(JsonElement o, string name) =>
		o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String
			? e.GetString()
			: null;

	public static string ReqStr(JsonElement o, string name)
	{
		var v = OptStr(o, name);
		if (string.IsNullOrWhiteSpace(v)) throw new ArgumentException($"{name} is required");
		return v!;
	}

	public static long OptLong(JsonElement o, string name, long dflt) =>
		o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var e)
			&& e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var v)
			? v
			: dflt;
}
