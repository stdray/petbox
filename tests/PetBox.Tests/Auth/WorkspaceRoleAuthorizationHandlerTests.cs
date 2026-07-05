using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using PetBox.Core.Auth;
using PetBox.Core.Models;

namespace PetBox.Tests.Auth;

// authz-bypass-project-create: WorkspaceRoleAuthorizationHandler is the enforcement that
// Admin/Projects.cshtml.cs (and every sibling Admin/Project*.cshtml.cs page) relies on via
// [Authorize(Policy = "WorkspaceAdmin")]. It resolves the workspace being ACTED ON from the
// {workspaceKey}/{key} ROUTE value — never the caller's own ActiveWorkspace claim, which is
// only a fallback when no route value is present — and requires the caller's WorkspaceRoles
// claim to grant a role at-or-above the requirement FOR THAT WORKSPACE. Admin/Projects.cshtml.cs
// used to carry only a bare [Authorize] (authenticated-only), so any logged-in user could
// create a Project in a workspace they don't administer — these tests exercise the handler
// directly (no MVC pipeline needed) to pin down the cross-tenant check the fix now depends on.
public sealed class WorkspaceRoleAuthorizationHandlerTests
{
	static WorkspaceRoleAuthorizationHandler Handler(string? routeWorkspaceKey, ClaimsPrincipal user)
	{
		var httpContext = new DefaultHttpContext { User = user };
		if (routeWorkspaceKey is not null)
			httpContext.Request.RouteValues["workspaceKey"] = routeWorkspaceKey;
		return new WorkspaceRoleAuthorizationHandler(new HttpContextAccessor { HttpContext = httpContext });
	}

	static ClaimsPrincipal UserWithRoles(string rolesClaim, bool sysAdmin = false, string? activeWorkspace = null)
	{
		var claims = new List<Claim> { new(PetBoxClaims.WorkspaceRoles, rolesClaim) };
		if (sysAdmin) claims.Add(new Claim(PetBoxClaims.IsSysAdmin, "true"));
		if (activeWorkspace is not null) claims.Add(new Claim(PetBoxClaims.ActiveWorkspace, activeWorkspace));
		return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
	}

	static async Task<bool> SucceedsAsync(WorkspaceRoleAuthorizationHandler handler, ClaimsPrincipal user, WorkspaceRole minRole)
	{
		var requirement = new WorkspaceRoleRequirement(minRole);
		var context = new AuthorizationHandlerContext([requirement], user, resource: null);
		await handler.HandleAsync(context);
		return context.HasSucceeded;
	}

	[Fact]
	public async Task Admin_in_wsA_is_denied_creating_in_wsB()
	{
		// This is exactly the exploit: the caller administers wsA only, but the route
		// (the workspace the POST would mutate) is wsB.
		var user = UserWithRoles("wsA=Admin");
		var handler = Handler("wsB", user);

		(await SucceedsAsync(handler, user, WorkspaceRole.Admin)).Should().BeFalse(
			"an Admin of wsA must not be authorized to act as Admin on wsB — cross-tenant must be denied");
	}

	[Fact]
	public async Task Admin_in_wsB_is_permitted_for_wsB()
	{
		var user = UserWithRoles("wsB=Admin");
		var handler = Handler("wsB", user);

		(await SucceedsAsync(handler, user, WorkspaceRole.Admin)).Should().BeTrue(
			"an Admin of wsB must be authorized to act on wsB");
	}

	[Fact]
	public async Task Sysadmin_bypasses_for_any_workspace()
	{
		var user = UserWithRoles(rolesClaim: "", sysAdmin: true);
		var handler = Handler("wsB", user);

		(await SucceedsAsync(handler, user, WorkspaceRole.Admin)).Should().BeTrue(
			"sysadmin implicitly administers every workspace");
	}

	[Fact]
	public async Task Member_role_does_not_satisfy_Admin_requirement()
	{
		var user = UserWithRoles("wsB=Member");
		var handler = Handler("wsB", user);

		(await SucceedsAsync(handler, user, WorkspaceRole.Admin)).Should().BeFalse(
			"Member is a weaker role than Admin and must not satisfy an Admin requirement");
	}

	[Fact]
	public async Task ActiveWorkspace_claim_is_only_a_fallback_not_a_substitute_for_the_route()
	{
		// The caller is Admin of their OWN active workspace (wsA) but the route targets wsB —
		// the route value must win; the ActiveWorkspace claim must never let a user smuggle
		// their own workspace's role into a mutation on someone else's.
		var user = UserWithRoles("wsA=Admin", activeWorkspace: "wsA");
		var handler = Handler("wsB", user);

		(await SucceedsAsync(handler, user, WorkspaceRole.Admin)).Should().BeFalse(
			"the route's target workspace must be checked, not the caller's active workspace");
	}

	[Fact]
	public async Task No_route_workspace_and_no_active_workspace_claim_fails_closed()
	{
		var user = UserWithRoles("wsB=Admin");
		var handler = Handler(routeWorkspaceKey: null, user);

		(await SucceedsAsync(handler, user, WorkspaceRole.Admin)).Should().BeFalse(
			"with no resolvable target workspace, the requirement must fail closed");
	}
}
