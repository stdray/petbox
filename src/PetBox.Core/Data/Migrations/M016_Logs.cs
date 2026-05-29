using FluentMigrator;

namespace PetBox.Core.Data.Migrations;

// Metadata about per-project named log SQLite databases. The actual events live
// in `data/logs/{ProjectKey}/{Name}.db` files; this table tracks which logs
// exist and who created them. Mirrors DataDbs (M013) but without a size quota —
// retention, not page-count, bounds log growth.
[Migration(16, "Create Logs metadata table for the Log module")]
public sealed class M016_Logs : Migration
{
	public override void Up()
	{
		Create.Table("Logs")
			.WithColumn("ProjectKey").AsString(100).NotNullable().PrimaryKey("PK_Logs")
			.WithColumn("Name").AsString(100).NotNullable().PrimaryKey("PK_Logs")
			.WithColumn("Description").AsString(1000).Nullable()
			.WithColumn("CreatedAt").AsDateTime().NotNullable()
			.WithColumn("UpdatedAt").AsDateTime().NotNullable();
	}

	public override void Down() => Delete.Table("Logs");
}
