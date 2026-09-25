using System.ComponentModel;
using ModelContextProtocol.Server;
using PetBox.Core.Auth;
using PetBox.Web.Mcp.Contract;

namespace PetBox.Web.Mcp;

// Self-identification tool. An agent's key is project-scoped and cannot enumerate
// projects, so without this it has no way to discover which project it is bound to
// or what it is allowed to do (dogfooding finding d2). Requires no scope — any
// authenticated key may call it ([RequiresNoScope]), so tools/list shows it to every key.
// It also returns the SURFACE CATALOG (`modules`, spec mcp-whoami): tools/list hides what a key
// cannot call, and this is where an agent learns the hidden part exists and which scope opens it.
// TENANT DECLARATION (spec authz-scope-declaration): `identity` — "сведения о вызывающем и о нём
// самом". whoami answers with the caller's OWN claim and reads nothing else; there is no second
// tenant it could be aimed at, which is why the cross-tenant probe records it as having no tenant
// slot at all. The exemption suspends the TENANT axis only: /mcp still requires an authenticated
// key, and the scope axis is untouched (this tool deliberately requires no scope).
[McpServerToolType]
[TenantExempt(TenantExemption.Identity, "answers with the caller's own claim; there is no other tenant to name")]
public static class WhoAmITools
{
	[RequiresNoScope("answers with the caller's own identity; any authenticated key may ask who it is")]
	[McpServerTool(Name = "whoami", Title = "Identify the calling ApiKey", ReadOnly = true, UseStructuredContent = true)]
	[Description("Returns the calling ApiKey's identity: { project, scopes, defaultProject, host, modules }. `project` is the key's project claim — every other tool needs a projectKey that must match it ('*' = a cross-project key: any projectKey is allowed). `scopes` is the list of granted scopes (e.g. 'data:read', 'logs:query', 'tasks:write') that gate what you may do. `defaultProject` (cross-project keys only, when set) is the project the tools with an OPTIONAL projectKey fall back to when you omit it. `host` is present only on a NODE-AGENT key and names the fleet host it is bound to; such a key has an empty `project` by design — it identifies a machine, not a project. `modules` is the catalog of the WHOLE tool surface, grouped by module: each module's scopes with `granted` (does THIS key hold it) and the names of ALL its tools — including those tools/list hides because this key lacks their scope. A tool hidden from tools/list is not callable with this key (the call is refused on the scope); tool_describe still returns its full description by name. Tools that need no scope are grouped under `Core`. Call this first when you do not already know your own project key and scopes.")]
	public static WhoAmIResult WhoAmI(IHttpContextAccessor http)
	{
		var ctx = http.HttpContext ?? throw new InvalidOperationException("No HttpContext");
		var project = ctx.User.Claims
			.FirstOrDefault(c => c.Type == ApiKeyAuthenticationHandler.ProjectClaim)?.Value;
		// The catalog's tokenizer, so what whoami REPORTS as granted is exactly what the gates will
		// honour — it used to split on ',' alone and hid the space-separated half of a grant set.
		var scopes = ApiKeyScopes.Split(
			ctx.User.Claims.FirstOrDefault(c => c.Type == ApiKeyAuthenticationHandler.ScopesClaim)?.Value);
		var defaultProject = ctx.User.Claims
			.FirstOrDefault(c => c.Type == ApiKeyAuthenticationHandler.DefaultProjectClaim)?.Value;
		// The `host` claim (M050) is emitted ONLY for a key bound to a fleet host. Reading it here is
		// what makes a node key self-describing: without it whoami reported the empty project claim a
		// node key now carries and nothing else, so the one identity the key DOES have was invisible.
		var host = ctx.User.Claims
			.FirstOrDefault(c => c.Type == ApiKeyAuthenticationHandler.HostClaim)?.Value;
		// The catalog reads the SAME granted set the invocation gate checks (ApiKeyScopes.GrantedSet), so
		// `granted: true` here is exactly "the gate will let this scope through".
		return new WhoAmIResult(project, scopes, defaultProject, host,
			McpToolScopes.Catalog(ApiKeyScopes.GrantedSet(ctx.User)));
	}
}
