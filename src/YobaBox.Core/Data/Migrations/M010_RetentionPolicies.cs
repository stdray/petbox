using FluentMigrator;

namespace YobaBox.Core.Data.Migrations;

[Migration(10, "Create RetentionPolicies table")]
public sealed class M010_RetentionPolicies : Migration
{
	public override void Up()
	{
		Create.Table("RetentionPolicies")
			.WithColumn("Id").AsInt64().PrimaryKey().Identity().NotNullable()
			.WithColumn("ProjectKey").AsString(100).NotNullable().Unique()
			.WithColumn("RetainDays").AsInt32().NotNullable().WithDefaultValue(7)
			.WithColumn("CreatedAt").AsDateTime().NotNullable()
			.WithColumn("UpdatedAt").AsDateTime().NotNullable();
	}

	public override void Down() => Delete.Table("RetentionPolicies");
}
