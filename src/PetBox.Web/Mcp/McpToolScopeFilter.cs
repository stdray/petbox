using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using PetBox.Core.Auth;

namespace PetBox.Web.Mcp;

// A7b — trim tools/list to the modules the caller's key scopes grant, so an agent
// sees only its relevant tools (token economy on a growing tool surface).
//
// NOT a security boundary: every tool still enforces its exact scope at call time
// via ModuleMcp.AssertScope/AssertProject — this only shortens the listing.
//
// Deliberately MODULE-level, not read/write: if the key holds ANY scope in a
// module we show all that module's tools and let call-time enforce read-vs-write,
// so we never hide a tool the key could actually use. FAIL-OPEN throughout —
// unknown tools, no scopes claim, or any error → the tool is shown.
static class McpToolScopeFilter
{
	public static void Register(IMcpRequestFilterBuilder filters) =>
		filters.AddListToolsFilter(next => async (request, ct) =>
		{
			var result = await next(request, ct);
			try
			{
				// spec tool-description-economy — serve the COMPACT HEAD for tools that opted in
				// with a sentinel (full prose stays fetchable via tool_describe). Runs at this same
				// tools/list layer, BEFORE the scope trim, so every early-return path below still
				// hands back compacted descriptions. Clones (never mutates) sentinel tools, so the
				// server's canonical ToolCollection keeps the full text.
				result.Tools = result.Tools.Select(McpToolDescriptions.Compact).ToList();
			}
			catch
			{
				// fail open — never break tools/list because of compaction
			}
			try
			{
				var granted = ScopesOf(request.User);
				if (granted.Count == 0) return result;                          // no claim → show all
				if (granted.Contains(ApiKeyScopes.AdminProvision)) return result; // provision key → show all
				result.Tools = result.Tools.Where(t => Allowed(t.Name, granted)).ToList();
			}
			catch
			{
				// fail open — never break tools/list because of filtering
			}
			return result;
		});

	static HashSet<string> ScopesOf(ClaimsPrincipal? user)
	{
		var raw = user?.FindFirst("scopes")?.Value ?? string.Empty;
		return new HashSet<string>(
			raw.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
			StringComparer.Ordinal);
	}

	// The scope-module a tool belongs to. Returns the scope prefix the key needs ANY
	// of ("tasks" → any tasks:* scope), or the literal "admin:provision" for tools
	// gated on that single scope, or null for tools we don't classify (→ fail open).
	static string? ModuleOf(string tool) =>
		tool.StartsWith("tasks_", StringComparison.Ordinal) || tool.StartsWith("session_", StringComparison.Ordinal) ? "tasks"
		: tool.StartsWith("memory_", StringComparison.Ordinal) ? "memory"
		: tool.StartsWith("log_", StringComparison.Ordinal) ? "logs"
		: tool.StartsWith("data_", StringComparison.Ordinal) || tool.StartsWith("db_", StringComparison.Ordinal) ? "data"
		: tool.StartsWith("deploy_", StringComparison.Ordinal) ? "deploy"
		: tool.StartsWith("agent_def_", StringComparison.Ordinal) ? "agents"
		: tool.StartsWith("config_", StringComparison.Ordinal) ? ApiKeyScopes.AdminProvision
		: null; // project_* / apikey_* — provisioning-mixed (admin:provision shows ALL anyway), leave shown

	static bool Allowed(string tool, HashSet<string> granted)
	{
		var module = ModuleOf(tool);
		if (module is null) return true;                              // unclassified → show
		if (module == ApiKeyScopes.AdminProvision) return granted.Contains(ApiKeyScopes.AdminProvision);
		var prefix = module + ":";
		foreach (var s in granted)
			if (s.StartsWith(prefix, StringComparison.Ordinal)) return true;
		return false;
	}
}
