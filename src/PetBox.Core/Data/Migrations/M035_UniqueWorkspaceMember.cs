using FluentMigrator;

namespace PetBox.Core.Data.Migrations;

// Bug fix (adminbootstrapper-seed-race): AdminBootstrapper.EnsureAdminUser used to do a plain
// check-then-insert with no DB-level backstop, so two parallel first-boot callers (e.g. two
// processes racing the same fresh petbox.db) could both pass the "no $system admin yet" check
// and each insert a WorkspaceMembers row for the same user+workspace — a duplicate admin
// membership. A user is only ever meant to hold ONE role per workspace (every add-member path —
// WorkspaceAdmin.cshtml.cs, WorkspaceUsers.cshtml.cs — already does a redundant "already a
// member?" check before inserting), so this unique index just makes that invariant durable at
// the DB level and gives concurrent writers something to collide on instead of silently
// duplicating rows.
[Migration(35, "Unique (UserId, WorkspaceKey) index on WorkspaceMembers")]
public sealed class M035_UniqueWorkspaceMember : Migration
{
	public override void Up()
	{
		// Dedupe first: an existing DB may already carry duplicate rows from the very race this
		// migration guards against (or from the pre-existing, still-unfixed race in the manual
		// add-member pages). Keep the lowest Id per (UserId, WorkspaceKey) — same "earliest wins"
		// rule the app itself now enforces — so CREATE UNIQUE INDEX below doesn't fail on
		// pre-existing dupes.
		Execute.Sql(
			"DELETE FROM WorkspaceMembers WHERE Id NOT IN (" +
			"SELECT MIN(Id) FROM WorkspaceMembers GROUP BY UserId, WorkspaceKey)");

		Create.Index("IX_WorkspaceMembers_UserId_WorkspaceKey")
			.OnTable("WorkspaceMembers")
			.OnColumn("UserId").Ascending()
			.OnColumn("WorkspaceKey").Ascending()
			.WithOptions().Unique();
	}

	public override void Down() =>
		Delete.Index("IX_WorkspaceMembers_UserId_WorkspaceKey").OnTable("WorkspaceMembers");
}
