using Microsoft.AspNetCore.Authorization;
using PetBox.Core.Data;

namespace PetBox.Core.Auth;

// The "CanCreateWorkspace" policy (spec workspace-create-permission): may THIS account create one
// more workspace right now?
//
// Not a claim. A quota is a number compared against live state (how many workspaces the account
// already owns), and both halves change without the cookie changing — so baking it into the
// identity at sign-in would answer with a stale number, in the direction that grants access. It is
// read from the DB, at the moment the question is asked.
//
// COST: this handler runs on EVERY page that renders the sidebar (_WorkspaceSelector asks it to
// decide whether to offer "+ New workspace"), plus the self-service create page and the
// no-workspace empty state. Two indexed core-db reads per run: one Users row, one
// WorkspaceMembers count — and a sysadmin short-circuits before either. Cheap, but no longer off
// the hot path: if it ever shows up in a profile, cache it per-request on NavigationContext
// rather than baking the answer into the cookie (see above for why the cookie is wrong).
public sealed class WorkspaceCreateRequirement : IAuthorizationRequirement;

public sealed class WorkspaceCreateAuthorizationHandler(WorkspaceProvisioning provisioning)
	: AuthorizationHandler<WorkspaceCreateRequirement>
{
	protected override async Task HandleRequirementAsync(
		AuthorizationHandlerContext context,
		WorkspaceCreateRequirement requirement)
	{
		// A sysadmin may create workspaces without limit — the quota is a grant to regular accounts,
		// not a leash on the operator.
		if (context.User.HasClaim(PetBoxClaims.IsSysAdmin, "true"))
		{
			context.Succeed(requirement);
			return;
		}

		if (!long.TryParse(context.User.FindFirst(PetBoxClaims.UserId)?.Value, out var userId))
			return;

		if (await provisioning.CanCreateAsync(userId))
			context.Succeed(requirement);
	}
}
