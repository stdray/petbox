using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PetBox.Core.Models;

namespace PetBox.Core.Auth;

public sealed class WorkspaceRoleRequirement(WorkspaceRole minRole) : IAuthorizationRequirement
{
	public WorkspaceRole MinRole { get; } = minRole;
}

// THE ROLE AXIS, and it is NOT the tenant axis — after the Razor wave that distinction is the only
// reason this type still exists, so it is written down here rather than rediscovered.
//
// Every workspace-scoped page now declares [TenantFrom(Route, "workspaceKey"|"key", Workspace)] and
// ITenantAuthorizer answers "may this caller touch this workspace at all" centrally, at Viewer or better.
// This handler answers a different question — "does it hold at least MinRole THERE" — and it is the only
// enforcement of MinRole anywhere. Deleting it would silently demote every Admin-gated page to Viewer.
//
// AND ITS ROUTE READ MUST STAY. Removing `routeWsKey` looks like the tidy half of that (the tenant PEP
// reads the same route value, after all) and is a PRIVILEGE ESCALATION: MinRole would then be measured
// against the ActiveWorkspace CLAIM. A user who is Admin of wsA and Viewer of wsB, active on wsA, asking
// for /ui/admin/ws/wsB/members would pass the tenant axis (they are a member of wsB) AND pass this
// requirement (they are Admin of wsA) — and land on wsB's membership editor. The role has to be demanded
// of the workspace the REQUEST names, which means reading it from the route, which means this handler
// keeps its own reading of it. The claim fallback below is for the routeless pages only.
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
		if (context.User.HasClaim(PetBoxClaims.IsSysAdmin, "true"))
		{
			context.Succeed(requirement);
			return Task.CompletedTask;
		}

		var routeWsKey = http.GetRouteValue("workspaceKey")?.ToString()
			?? http.GetRouteValue("key")?.ToString();

		var rolesClaim = context.User.FindFirst(PetBoxClaims.WorkspaceRoles)?.Value ?? "";
		var roles = ParseWorkspaceRoles(rolesClaim);

		var targetWs = routeWsKey
			?? context.User.FindFirst(PetBoxClaims.ActiveWorkspace)?.Value;

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
