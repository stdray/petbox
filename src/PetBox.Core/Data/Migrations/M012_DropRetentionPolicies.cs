using FluentMigrator;

namespace PetBox.Core.Data.Migrations;

// RetentionPolicies migrated to L2 Settings (scope=project, path=log.retention.days).
// No data is carried over — existing per-project policies start from system defaults
// after this migration. Acceptable per Phase 23.3 (small data, sole user).
[Migration(12, "Drop RetentionPolicies — moved into L2 Settings store")]
public sealed class M012_DropRetentionPolicies : Migration
{
	public override void Up() => Delete.Table("RetentionPolicies");

	public override void Down() => Create.Table("RetentionPolicies")
		.WithColumn("Id").AsInt64().PrimaryKey().Identity().NotNullable()
		.WithColumn("ProjectKey").AsString(100).NotNullable().Unique()
		.WithColumn("RetainDays").AsInt32().NotNullable().WithDefaultValue(7)
		.WithColumn("CreatedAt").AsDateTime().NotNullable()
		.WithColumn("UpdatedAt").AsDateTime().NotNullable();
}
