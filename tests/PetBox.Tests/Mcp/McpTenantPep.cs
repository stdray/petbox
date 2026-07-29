using System.Security.Claims;
using System.Text.Json;
using PetBox.Core.Auth;
using PetBox.Core.Data;
using PetBox.Web.Mcp;

namespace PetBox.Tests.Mcp;

// WHERE THE MCP TENANT REFUSAL LIVES NOW — work `authz-default-deny-delivery`, step 5.
//
// Until this wave, every *Tools.cs verb opened with ModuleMcp.AssertProject(http, projectKey), so a
// UNIT test could call the tool method directly and observe the refusal as a thrown
// UnauthorizedAccessException. The declaration wave moved that decision OUT of the tool bodies and
// into McpTenantEnforcementFilter, which the SDK runs before a tool is entered at all — deliberately,
// because a check inside the body is a check that ran after the request was bound, and because two
// copies of one question are two answers waiting to disagree.
//
// A direct method call therefore no longer refuses anything: there is nothing left in the body to
// refuse with. That is the intended end state, NOT a hole — but a test that kept asserting on the
// direct call would have quietly become a test of nothing, so the assertions move to where the
// decision moved. This helper is that move: it asks the REAL PEP entry point
// (McpTenantEnforcementFilter.DecideAsync) with the REAL declarations scanned off PetBox.Web, so it
// exercises the allowlist check, the declaration lookup and ITenantAuthorizer exactly as a live call
// does. If a family's declaration is wrong or missing, these tests fail.
//
// The principal is built as an API-KEY identity because that is the only kind that reaches /mcp:
// Program.cs maps it with RequireAuthorization over the ApiKey scheme alone. A test principal with
// some other AuthenticationType would take TenantAuthorizer's cookie branch and measure a path this
// transport cannot produce.
static class McpTenantPep
{
	// Scanned ONCE off the production assembly — the same reflection the filter does at startup.
	static readonly IReadOnlyDictionary<string, IReadOnlyList<TenantDeclarationAttribute>> Declarations =
		McpTenantEnforcementFilter.Scan(typeof(Program).Assembly);

	// The verdict the MCP plane would reach for `tool` called with `projectKey` by a key whose project
	// claim is `claim`. Pass projectKey: null to model the caller omitting the argument.
	static ValueTask<TenantGateResult> DecideAsync(
		IProjectCatalog catalog, string tool, string? projectKey, string claim, bool sandboxOnly = false)
	{
		var arguments = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
		if (projectKey is not null)
			arguments["projectKey"] = JsonSerializer.SerializeToElement(projectKey);

		return McpTenantEnforcementFilter.DecideAsync(
			new TenantAuthorizer(catalog), Principal(claim, sandboxOnly), tool, arguments, Declarations);
	}

	// Sugar for the common assertion: this tool, aimed at this project, by this key, is REFUSED.
	public static async Task RefusesAsync(
		IProjectCatalog catalog, string tool, string projectKey, string claim, bool sandboxOnly = false)
	{
		var verdict = await DecideAsync(catalog, tool, projectKey, claim, sandboxOnly);
		verdict.Allowed.Should().BeFalse(
			$"'{tool}' aimed at '{projectKey}' by a key claiming '{claim}' must be refused on the tenant axis "
			+ "before the tool is entered at all");
	}

	public static async Task AllowsAsync(
		IProjectCatalog catalog, string tool, string? projectKey, string claim, bool sandboxOnly = false)
	{
		var verdict = await DecideAsync(catalog, tool, projectKey, claim, sandboxOnly);
		verdict.Allowed.Should().BeTrue(
			$"'{tool}' aimed at '{projectKey ?? "(omitted)"}' by a key claiming '{claim}' must be allowed — "
			+ $"the PEP said: {verdict.Message}");
	}

	static ClaimsPrincipal Principal(string claim, bool sandboxOnly)
	{
		var claims = new List<Claim> { new(ApiKeyAuthenticationHandler.ProjectClaim, claim) };
		if (sandboxOnly) claims.Add(new Claim(ApiKeyAuthenticationHandler.SandboxOnlyClaim, "true"));
		return new ClaimsPrincipal(new ClaimsIdentity(claims, ApiKeyAuthenticationHandler.SchemeName));
	}
}
