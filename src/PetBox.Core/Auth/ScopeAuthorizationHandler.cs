using Microsoft.AspNetCore.Authorization;

namespace PetBox.Core.Auth;

public sealed class ScopeRequirement : IAuthorizationRequirement
{
	public string RequiredScope { get; }

	public ScopeRequirement(string scope) => RequiredScope = scope;
}

public sealed class ScopeAuthorizationHandler : AuthorizationHandler<ScopeRequirement>
{
	// THE REST HALF of spec `access-permission-uniform`. It used to carry its own reading of the
	// `scopes` claim and was the ONLY one of sixteen that compared OrdinalIgnoreCase and split on ','
	// alone — so this policy, and nothing else in the system, could Allow a permission the MCP guard
	// Denied (a casing difference) and Deny one the MCP guard Allowed (a space-separated grant set).
	// Both readings now come from the catalog, which is what makes the two transports agree by
	// construction rather than by coincidence.
	protected override Task HandleRequirementAsync(
		AuthorizationHandlerContext context,
		ScopeRequirement requirement)
	{
		if (ApiKeyScopes.Granted(context.User, requirement.RequiredScope))
			context.Succeed(requirement);

		return Task.CompletedTask;
	}
}
