using FluentMigrator;

namespace YobaBox.Core.Data.Migrations;

[Migration(1)]
public sealed class M001_Initial : Migration
{
	public override void Up()
	{
		Create.Table("Projects")
			.WithColumn("Key").AsString(100).PrimaryKey().NotNullable()
			.WithColumn("Name").AsString(200).NotNullable()
			.WithColumn("Description").AsString(1000).Nullable();

		Create.Table("Services")
			.WithColumn("Key").AsString(100).PrimaryKey().NotNullable()
			.WithColumn("ProjectKey").AsString(100).NotNullable()
			.WithColumn("Kind").AsInt32().NotNullable()
			.WithColumn("Url").AsString(500).Nullable()
			.WithColumn("Version").AsString(50).Nullable()
			.WithColumn("ShortSha").AsString(8).Nullable()
			.WithColumn("Health").AsInt32().NotNullable().WithDefaultValue(3)
			.WithColumn("CheckedAt").AsDateTime().Nullable();

		Create.Table("ApiKeys")
			.WithColumn("Key").AsString(100).PrimaryKey().NotNullable()
			.WithColumn("ProjectKey").AsString(100).NotNullable()
			.WithColumn("Scopes").AsString(int.MaxValue).NotNullable()
			.WithColumn("CreatedAt").AsDateTime().NotNullable();

		Insert.IntoTable("Projects").Row(new { Key = "$system", Name = "System", Description = "Built-in system project" });
	}

	public override void Down()
	{
		Delete.Table("ApiKeys");
		Delete.Table("Services");
		Delete.Table("Projects");
	}
}
