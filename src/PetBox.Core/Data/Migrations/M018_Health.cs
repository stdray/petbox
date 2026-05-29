using FluentMigrator;

namespace PetBox.Core.Data.Migrations;

// Health/status subsystem (replaces the Service entity, dropped in a later
// migration). HealthReports — append-only history of pushed/pulled status
// structures, identified by (Svc, Tags); HealthEndpoints — pull-mode URLs.
[Migration(18, "Create HealthReports + HealthEndpoints")]
public sealed class M018_Health : Migration
{
	public override void Up()
	{
		Create.Table("HealthReports")
			.WithColumn("Id").AsInt64().PrimaryKey().Identity()
			.WithColumn("Svc").AsString(200).NotNullable()
			.WithColumn("Name").AsString(400).Nullable()
			.WithColumn("Tags").AsString(2000).NotNullable()
			.WithColumn("Version").AsString(100).Nullable()
			.WithColumn("Sha").AsString(100).Nullable()
			.WithColumn("BuildDate").AsString(100).Nullable()
			.WithColumn("Status").AsString(50).NotNullable()
			.WithColumn("ReceivedAt").AsDateTime().NotNullable()
			.WithColumn("Source").AsString(20).NotNullable();

		// Serves both latest-per-(svc,tags) lookups and the retention sweep tail.
		Create.Index("IX_HealthReports_Svc_Tags_ReceivedAt")
			.OnTable("HealthReports")
			.OnColumn("Svc").Ascending()
			.OnColumn("Tags").Ascending()
			.OnColumn("ReceivedAt").Descending();
		Create.Index("IX_HealthReports_ReceivedAt")
			.OnTable("HealthReports")
			.OnColumn("ReceivedAt").Ascending();

		Create.Table("HealthEndpoints")
			.WithColumn("Id").AsInt64().PrimaryKey().Identity()
			.WithColumn("ProjectKey").AsString(100).NotNullable()
			.WithColumn("Url").AsString(1000).NotNullable()
			.WithColumn("Enabled").AsBoolean().NotNullable()
			.WithColumn("IntervalSeconds").AsInt32().NotNullable()
			.WithColumn("CreatedAt").AsDateTime().NotNullable()
			.WithColumn("CreatedBy").AsString(100).Nullable();
	}

	public override void Down()
	{
		Delete.Table("HealthEndpoints");
		Delete.Table("HealthReports");
	}
}
