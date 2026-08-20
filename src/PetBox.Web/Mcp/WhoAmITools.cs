using System.ComponentModel;
using ModelContextProtocol.Server;
using PetBox.Core.Auth;
using PetBox.Web.Mcp.Contract;

namespace PetBox.Web.Mcp;

// Self-identification tool. An agent's key is project-scoped and cannot enumerate
// projects, so without this it has no way to discover which project it is bound to
// or what it is allowed to do (dogfooding finding d2). Requires no scope — any
// authenticated key may call it — and the A7b scope filter leaves it shown to every
// key (unclassified tool → fail-open).
// TENANT DECLARATION (spec authz-scope-declaration): `identity` — "сведения о вызывающем и о нём
// самом". whoami answers with the caller's OWN claim and reads nothing else; there is no second
// tenant it could be aimed at, which is why the cross-tenant probe records it as having no tenant
// slot at all. The exemption suspends the TENANT axis only: /mcp still requires an authenticated
// key, and the scope axis is untouched (this tool deliberately requires no scope).
[McpServerToolType]
[TenantExempt(TenantExemption.Identity, "answers with the caller's own claim; there is no other tenant to name")]
public static class WhoAmITools
{
	[McpServerTool(Name = "whoami", Title = "Identify the calling ApiKey", ReadOnly = true, UseStructuredContent = true)]
	[Description("Returns the calling ApiKey's identity: { project, scopes, defaultProject, host }. `project` is the key's project claim — every other tool needs a projectKey that must match it ('*' = a cross-project key: any projectKey is allowed). `scopes` is the list of granted scopes (e.g. 'data:read', 'logs:query', 'tasks:write') that gate what you may do. `defaultProject` (cross-project keys only, when set) is the project the tools with an OPTIONAL projectKey fall back to when you omit it. `host` is present only on a NODE-AGENT key and names the fleet host it is bound to; such a key has an empty `project` by design — it identifies a machine, not a project. Call this first when you do not already know your own project key and scopes.")]
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
		return new WhoAmIResult(project, scopes, defaultProject, host);
	}
}
