using System.Security.Claims;
using PetBox.Core.Auth;

namespace PetBox.Web.Mcp;

// The two readers of a tool's scope declaration (McpToolScopes — spec mcp-scope-declared-once) on
// the MCP request pipeline:
//
//   * RegisterListFilter — tools/list shows a key only the tools whose declared requirement its
//     scopes satisfy (spec mcp-tool-visibility-by-scope). PER TOOL, not per module: a tasks:read key
//     does not see tasks_upsert, a tasks:write key without methodology:write does not see the
//     governance verbs. NOT a security boundary — hiding is token economy, and the call gate below
//     enforces independently of what was listed; tool_describe still describes any tool by name and
//     whoami's catalog still names every tool. FAIL-OPEN: no scopes claim, an admin:provision key,
//     an undeclared tool, or any error → shown.
//
//   * RegisterCallGate — THE scope check at invocation, replacing the unconditional
//     ModuleMcp.AssertScope each tool body used to open with. Refuses with the same
//     UnauthorizedAccessException("ApiKey lacks required scope '<scope>'") those calls threw, so the
//     wire refusal is unchanged. DEFAULT-DENY: a tool with no declaration is refused (the ratchet
//     McpToolScopeDeclarationTests keeps that set empty; this is the belt).
static class McpToolScopeFilter
{
	public static void RegisterListFilter(IMcpRequestFilterBuilder filters) =>
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
				result.Tools = result.Tools.Where(t => Visible(t.Name, granted)).ToList();
			}
			catch
			{
				// fail open — never break tools/list because of filtering
			}
			return result;
		});

	// Registered in Program.cs directly INSIDE McpTenantEnforcementFilter: the tenant refusal keeps
	// precedence (it did before, when this check lived in the tool body), and the scope refusal now
	// precedes McpProjectExistsFilter, McpUnknownParameterFilter, argument binding, the feature gate
	// and the body — a key without the scope learns nothing about what exists or what a tool accepts.
	public static void RegisterCallGate(IMcpRequestFilterBuilder filters) =>
		filters.AddCallToolFilter(next => async (request, ct) =>
		{
			AssertScope(request.User, request.Params?.Name);
			return await next(request, ct);
		});

	internal static void AssertScope(ClaimsPrincipal? user, string? tool)
	{
		if (string.IsNullOrEmpty(tool)) return; // the SDK answers a nameless call itself
		if (!McpToolScopes.Declared.TryGetValue(tool, out var requirement))
			throw new UnauthorizedAccessException(
				$"'{tool}' declares no required scope. Every MCP tool states its scope in one machine-readable "
				+ "place ([RequiresScope]/[RequiresAnyScope]/[RequiresNoScope]); until it does, it is refused.");
		var granted = ScopesOf(user);
		if (!requirement.IsSatisfiedBy(granted))
			throw new UnauthorizedAccessException(requirement.RefusalMessage(granted));
	}

	static IReadOnlySet<string> ScopesOf(ClaimsPrincipal? user) => ApiKeyScopes.GrantedSet(user);

	static bool Visible(string tool, IReadOnlySet<string> granted) =>
		!McpToolScopes.Declared.TryGetValue(tool, out var requirement) // undeclared → show (fail open)
		|| requirement.IsSatisfiedBy(granted);
}
