using System.Security.Claims;
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

	static IReadOnlySet<string> ScopesOf(ClaimsPrincipal? user) => ApiKeyScopes.GrantedSet(user);

	// The scope-module a tool belongs to. Returns the scope prefix the key needs ANY
	// of ("tasks" → any tasks:* scope), or the literal "admin:provision" for tools
	// gated on that single scope, or null for tools we don't classify (→ fail open).
	//
	// THIS TABLE MUST NAME EVERY TOOL FAMILY THAT CALLS ModuleMcp.AssertScope (or an equivalent local
	// AssertScope wrapper — DataTools/DataDbTools/HealthTools each have one). A family missing here
	// falls through to `null` — fail-open, so invocation still enforces the scope correctly, but the
	// listing lies about it (work `mcp-tools-list-ignores-key-scopes`: apikey_*/project_*/llm_*/
	// comments_*/relations_*/health_* all escaped this way — apikey_*/project_* by an explicit
	// "leave unclassified" comment that stopped matching what AssertScope actually required, and
	// llm_*/comments_*/relations_*/health_* by never being added when those families were).
	// McpToolScopeFilterTests.EveryVisibleTool_InvokesPastTheScopeCheck guards the whole table
	// generically — it invokes every tool tools/list shows a restricted key and fails if any of them
	// throws ModuleMcp.AssertScope's "lacks required scope" — so a NEW family added here without a
	// matching entry (or a wrong one) is caught in CI rather than rediscovered by a card like this one.
	static string? ModuleOf(string tool) =>
		tool.StartsWith("tasks_", StringComparison.Ordinal) || tool.StartsWith("session_", StringComparison.Ordinal)
			// comments_*/relations_* act on task nodes and are gated on tasks:read/tasks:write exactly
			// like tasks_*/session_* — see CommentTools/RelationTools' AssertScope calls.
			|| tool.StartsWith("comments_", StringComparison.Ordinal) || tool.StartsWith("relations_", StringComparison.Ordinal) ? "tasks"
		: tool.StartsWith("memory_", StringComparison.Ordinal) ? "memory"
		: tool.StartsWith("log_", StringComparison.Ordinal) ? "logs"
		: tool.StartsWith("data_", StringComparison.Ordinal) || tool.StartsWith("db_", StringComparison.Ordinal) ? "data"
		: tool.StartsWith("deploy_", StringComparison.Ordinal) ? "deploy"
		// config_binding_* are ordinary TENANT verbs (config:read / config:write over the workspace named
		// by `workspaceKey`), so they get a module of their own like every other family. They used to map
		// to the literal admin:provision, which matched the gate they had then — and hid them from a key
		// holding config:read/config:write, i.e. from exactly the keys that may now call them.
		: tool.StartsWith("config_", StringComparison.Ordinal) ? "config"
		// llm_config_get/_upsert (llm:admin) and llm_embed/_rerank/_chat (llm:invoke) — both scopes live
		// under the "llm" module, same module-not-read/write looseness as every other family here.
		: tool.StartsWith("llm_", StringComparison.Ordinal) ? "llm"
		// health_search — health:read. HealthTools has its own local AssertScope wrapper, same catalog.
		: tool.StartsWith("health_", StringComparison.Ordinal) ? "health"
		// apikey_*/project_* all gate on the single literal admin:provision (ApiKeyTools/ProjectTools'
		// AssertScope calls) — not a module of read/write scopes, so route them through the exact-match
		// branch in Allowed() below instead of leaving them unclassified.
		: tool.StartsWith("apikey_", StringComparison.Ordinal) || tool.StartsWith("project_", StringComparison.Ordinal)
			? ApiKeyScopes.AdminProvision
		// share_revoke / whoami / tool_describe / petbox_report_issue* / search_reindex genuinely require
		// no FIXED scope (share_revoke by design — see ShareTools' header; search_reindex's requirement
		// is computed per-call from its `tier` argument) — correctly left unclassified so every key sees
		// them and invocation itself decides.
		: null;

	static bool Allowed(string tool, IReadOnlySet<string> granted)
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
