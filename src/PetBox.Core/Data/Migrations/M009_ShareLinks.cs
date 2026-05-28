using FluentMigrator;

namespace PetBox.Core.Data.Migrations;

[Migration(9, "Create ShareLinks table for shared KQL queries")]
public sealed class M009_ShareLinks : Migration
{
	public override void Up()
	{
		Create.Table("ShareLinks")
			.WithColumn("Id").AsString(40).PrimaryKey().NotNullable()
			.WithColumn("ProjectKey").AsString(100).NotNullable()
			.WithColumn("Kql").AsString(int.MaxValue).NotNullable()
			.WithColumn("CreatedAt").AsDateTime().NotNullable()
			.WithColumn("ExpiresAt").AsDateTime().NotNullable()
			.WithColumn("SaltBase64").AsString(64).NotNullable()
			.WithColumn("ColumnsJson").AsString(int.MaxValue).NotNullable().WithDefaultValue("[]")
			.WithColumn("ModesJson").AsString(int.MaxValue).NotNullable().WithDefaultValue("{}")
			.WithColumn("CreatedBy").AsString(100).NotNullable().WithDefaultValue("system");

		Create.Index("IX_ShareLinks_ExpiresAt").OnTable("ShareLinks").OnColumn("ExpiresAt").Ascending();
	}

	public override void Down() => Delete.Table("ShareLinks");
}
