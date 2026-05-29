using FluentMigrator;

namespace PetBox.Core.Data.Migrations;

// The Service entity is obsolete: its only per-service data (logs) is now
// per-project named logs, and health monitoring moved to tag-identified
// HealthReports (M018). Drop the table. Log entries keep ServiceKey as a free
// emitter tag (no FK).
[Migration(19, "Drop Services (replaced by HealthReports)")]
public sealed class M019_DropServices : Migration
{
	public override void Up()
	{
		Delete.Table("Services");
	}

	public override void Down()
	{
		Create.Table("Services")
			.WithColumn("Key").AsString(100).NotNullable().PrimaryKey()
			.WithColumn("ProjectKey").AsString(100).NotNullable()
			.WithColumn("HealthModel").AsInt32().NotNullable()
			.WithColumn("Url").AsString(500).Nullable()
			.WithColumn("Version").AsString(50).Nullable()
			.WithColumn("ShortSha").AsString(8).Nullable()
			.WithColumn("Health").AsInt32().NotNullable()
			.WithColumn("CheckedAt").AsDateTime().Nullable();
	}
}
