using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;

namespace PetBox.Core.Auth;

public sealed class ScopeRequirement : IAuthorizationRequirement
{
	public string RequiredScope { get; }

	public ScopeRequirement(string scope) => RequiredScope = scope;
}

public sealed class ScopeAuthorizationHandler : AuthorizationHandler<ScopeRequirement>
{
	protected override Task HandleRequirementAsync(
		AuthorizationHandlerContext context,
		ScopeRequirement requirement)
	{
		var scopesClaim = context.User.FindFirstValue("scopes");
		if (scopesClaim is not null && HasScope(scopesClaim, requirement.RequiredScope))
			context.Succeed(requirement);

		return Task.CompletedTask;
	}

	static bool HasScope(string scopes, string required)
	{
		if (string.IsNullOrWhiteSpace(scopes))
			return false;
		return scopes
			.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Any(s => string.Equals(s, required, StringComparison.OrdinalIgnoreCase));
	}
}
