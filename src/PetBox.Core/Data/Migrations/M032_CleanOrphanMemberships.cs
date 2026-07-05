using FluentMigrator;

namespace PetBox.Core.Data.Migrations;

// Backfill: remove pre-existing orphaned WorkspaceMember rows whose workspace no longer
// exists. Before ui-workspace-delete-cascade, deleting a workspace dropped the Workspaces
// row but left its memberships behind (e.g. an "admin → infra Admin" grant surviving a long
// deleted "infra" workspace). Going forward the delete-workspace handler cleans memberships
// itself; this one-shot migration reconciles rows that were orphaned before that fix landed.
[Migration(32, "Delete WorkspaceMembers whose workspace no longer exists")]
public sealed class M032_CleanOrphanMemberships : Migration
{
	public override void Up() =>
		Execute.Sql("DELETE FROM WorkspaceMembers " +
			"WHERE WorkspaceKey NOT IN (SELECT Key FROM Workspaces)");

	// No Down: the removed rows were already orphaned (their workspace is gone), so there is
	// nothing coherent to restore.
	public override void Down() { }
}
