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
		tool.StartsWith("tasks.", StringComparison.Ordinal) || tool.StartsWith("session.", StringComparison.Ordinal) ? "tasks"
		: tool.StartsWith("memory.", StringComparison.Ordinal) ? "memory"
		: tool.StartsWith("log.", StringComparison.Ordinal) ? "logs"
		: tool.StartsWith("data.", StringComparison.Ordinal) || tool.StartsWith("db.", StringComparison.Ordinal) ? "data"
		: tool.StartsWith("deploy.", StringComparison.Ordinal) ? "deploy"
		: tool.StartsWith("config.", StringComparison.Ordinal) ? ApiKeyScopes.AdminProvision
		: null; // project.* / apikey.* — provisioning-mixed (admin:provision shows ALL anyway), leave shown

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
