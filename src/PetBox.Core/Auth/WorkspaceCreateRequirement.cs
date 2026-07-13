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
// COST: this handler is NOT on the hot path. It runs only where the policy is declared (the
// self-service create page) and where the CTA is rendered (the no-workspace empty state, which a
// user with a workspace never sees) — not on every request. Two indexed core-db reads when it does
// run: one Users row, one WorkspaceMembers count.
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
