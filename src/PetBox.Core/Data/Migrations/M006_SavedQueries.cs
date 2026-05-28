using FluentMigrator;

namespace PetBox.Core.Data.Migrations;

[Migration(6, "Create SavedQueries table")]
public sealed class M006_SavedQueries : Migration
{
	public override void Up()
	{
		Create.Table("SavedQueries")
			.WithColumn("Id").AsInt64().PrimaryKey().Identity()
			.WithColumn("Name").AsString(200).NotNullable()
			.WithColumn("Kql").AsString(int.MaxValue).NotNullable()
			.WithColumn("ProjectKey").AsString(100).NotNullable()
			.WithColumn("CreatedAt").AsDateTime().NotNullable()
			.WithColumn("UpdatedAt").AsDateTime().NotNullable();

		Create.Index("IX_SavedQueries_ProjectKey")
			.OnTable("SavedQueries")
			.OnColumn("ProjectKey").Ascending();
	}

	public override void Down()
	{
		Delete.Table("SavedQueries");
	}
}
