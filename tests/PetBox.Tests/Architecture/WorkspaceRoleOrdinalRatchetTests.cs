using PetBox.Core.Models;

namespace PetBox.Tests.Architecture;

// THE RATCHET for work `workspacerole-explicit-ordinals`: WorkspaceRole is declared with explicit
// numeric values (Admin = 0, Member = 1, Viewer = 2) precisely so a future insertion in the MIDDLE
// of the list cannot silently renumber the roles that already exist. Declaring the numbers is only
// half the defense — nothing stops someone from editing `= 1` back out again, or reordering the
// members (which recomputes the implicit values right back to the bug this exists to prevent) while
// leaving the explicit annotations on the two that didn't move. This test pins the ACTUAL RUNTIME
// VALUES, not the source text, so either kind of regression fails the build instead of shipping.
//
// WHY THE NUMBERS MATTER: WorkspaceRoleRequirement.HandleRequirementAsync ("actualRole <=
// requirement.MinRole") and WorkspaceRoleClaims.HasWorkspaceRoleAtLeast ("role <= minRole") both
// compare WorkspaceRole by ORDINAL — "lower ordinal = stronger role" is the entire access-control
// semantics for the workspace role axis. If Admin/Member/Viewer's numbers ever shift relative to
// each other, both of those comparisons flip meaning with no compiler error.
//
// WHAT THIS DOES NOT COVER: the `yb:ws_roles` claim (WorkspaceRoleRequirement.SerializeRoles)
// serializes by ROLE NAME, not by number (see the "ws=Role,ws=Role" wire format), so a claim already
// issued survives a number change unaffected until the next sign-in refreshes it — this ratchet only
// guards the numbers the DB column and the ordinal comparisons above depend on, not that
// name/number duality.
public sealed class WorkspaceRoleOrdinalRatchetTests
{
	[Fact]
	public void WorkspaceRole_ValuesAreExplicitlyPinned()
	{
		((int)WorkspaceRole.Admin).Should().Be(0,
			"WorkspaceRole.Admin is the STRONGEST role — every ordinal-based comparison in "
			+ "WorkspaceRoleRequirement/WorkspaceRoleClaims treats the lowest number as the strongest role. "
			+ "If this value moves, every '<= MinRole' check in the codebase silently changes what it grants.");

		((int)WorkspaceRole.Member).Should().Be(1,
			"WorkspaceRole.Member sits between Admin and Viewer in strength. A future role inserted before "
			+ "this one (or a reorder) must fail here rather than silently renumber it.");

		((int)WorkspaceRole.Viewer).Should().Be(2,
			"WorkspaceRole.Viewer is the WEAKEST role — the ceiling every '<= MinRole' comparison in the "
			+ "codebase is measured against.");
	}

	// Guard the guard, and guard against a new role slipping in unnoticed: the set of DEFINED role
	// names must be exactly these three. Adding a fourth role is a legitimate future change, but it is
	// a decision about role semantics (where does it rank?), not a one-line enum edit that should pass
	// silently through this ratchet — extending this list is the deliberate part of making that change.
	[Fact]
	public void WorkspaceRole_HasExactlyTheseThreeRoles()
	{
		Enum.GetNames<WorkspaceRole>().Order(StringComparer.Ordinal).Should().Equal(
			["Admin", "Member", "Viewer"],
			"a fourth role (or a rename) is a role-semantics decision — where does it rank relative to the "
			+ "other three? — not a change that should pass through this ratchet unnoticed. Update this test "
			+ "deliberately, alongside the ordinal it should get, when that decision is actually made.");
	}
}
