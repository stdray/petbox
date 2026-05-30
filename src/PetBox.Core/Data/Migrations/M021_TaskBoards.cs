using FluentMigrator;

namespace PetBox.Core.Data.Migrations;

// Metadata about per-project named task boards. The actual plan nodes live in
// `data/tasks/{ProjectKey}/{Name}.db` temporal files; this table tracks which
// boards exist. Mirrors Logs (M016) — explicit creation, no quota.
[Migration(21, "Create TaskBoards metadata table for the Tasks module")]
public sealed class M021_TaskBoards : Migration
{
	public override void Up()
	{
		Create.Table("TaskBoards")
			.WithColumn("ProjectKey").AsString(100).NotNullable().PrimaryKey("PK_TaskBoards")
			.WithColumn("Name").AsString(100).NotNullable().PrimaryKey("PK_TaskBoards")
			.WithColumn("Description").AsString(1000).Nullable()
			.WithColumn("CreatedAt").AsDateTime().NotNullable()
			.WithColumn("UpdatedAt").AsDateTime().NotNullable();
	}

	public override void Down() => Delete.Table("TaskBoards");
}
