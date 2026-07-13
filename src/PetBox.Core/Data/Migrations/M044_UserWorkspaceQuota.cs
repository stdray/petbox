using FluentMigrator;

namespace PetBox.Core.Data.Migrations;

// spec workspace-create-permission: the right to create a workspace becomes an explicit NUMBER on
// the account (Users.WorkspaceQuota), replacing "sysadmin or nobody".
//
// The column default is 0 (may not create) — that is the safe value for any row inserted without
// the app naming it. New accounts do NOT get a silent default from the system: the admin form makes
// the field mandatory and empty, so the number is always a decision someone made.
//
// The backfill below is a ONE-TIME grant, not a system default: every account that existed BEFORE
// this migration gets 1, so the humans already using the instance can provision themselves a
// workspace without the maintainer touching the DB by hand. Accounts created after this migration
// get whatever the admin typed and nothing more.
[Migration(44, "Add Users.WorkspaceQuota; backfill existing accounts with 1")]
public sealed class M044_UserWorkspaceQuota : Migration
{
	public override void Up()
	{
		Create.Column("WorkspaceQuota").OnTable("Users").AsInt32().NotNullable().WithDefaultValue(0);

		// One-time backfill of the accounts that predate the column. AllRows() is exactly right here:
		// at this instant every row IS a pre-existing account.
		Update.Table("Users").Set(new { WorkspaceQuota = 1 }).AllRows();
	}

	public override void Down() => Delete.Column("WorkspaceQuota").FromTable("Users");
}
