using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using YobaBox.Core.Models;

namespace YobaBox.Core.Auth;

public sealed class WorkspaceRoleRequirement(WorkspaceRole minRole) : IAuthorizationRequirement
{
	public WorkspaceRole MinRole { get; } = minRole;
}

public sealed class WorkspaceRoleAuthorizationHandler(IHttpContextAccessor accessor)
	: AuthorizationHandler<WorkspaceRoleRequirement>
{
	protected override Task HandleRequirementAsync(
		AuthorizationHandlerContext context,
		WorkspaceRoleRequirement requirement)
	{
		var http = accessor.HttpContext;
		if (http is null)
			return Task.CompletedTask;

		// Sysadmin implicitly satisfies any workspace role (Admin/Member/Viewer)
		// across every workspace.
		if (context.User.HasClaim(YobaBoxClaims.IsSysAdmin, "true"))
		{
			context.Succeed(requirement);
			return Task.CompletedTask;
		}

		var routeWsKey = http.GetRouteValue("workspaceKey")?.ToString()
			?? http.GetRouteValue("key")?.ToString();

		var rolesClaim = context.User.FindFirst(YobaBoxClaims.WorkspaceRoles)?.Value ?? "";
		var roles = ParseWorkspaceRoles(rolesClaim);

		var targetWs = routeWsKey
			?? context.User.FindFirst(YobaBoxClaims.ActiveWorkspace)?.Value;

		if (targetWs is null)
			return Task.CompletedTask;

		if (roles.TryGetValue(targetWs, out var actualRole) && actualRole <= requirement.MinRole)
			context.Succeed(requirement);

		return Task.CompletedTask;
	}

	static Dictionary<string, WorkspaceRole> ParseWorkspaceRoles(string claim)
	{
		var dict = new Dictionary<string, WorkspaceRole>(StringComparer.Ordinal);
		if (string.IsNullOrEmpty(claim))
			return dict;

		foreach (var pair in claim.Split(',', StringSplitOptions.RemoveEmptyEntries))
		{
			var parts = pair.Split('=');
			if (parts.Length != 2) continue;
			if (Enum.TryParse<WorkspaceRole>(parts[1], ignoreCase: true, out var role))
				dict[parts[0]] = role;
		}
		return dict;
	}

	public static string SerializeRoles(IEnumerable<(string WorkspaceKey, WorkspaceRole Role)> roles) =>
		string.Join(",", roles.Select(r => string.Create(CultureInfo.InvariantCulture, $"{r.WorkspaceKey}={r.Role}")));
}
