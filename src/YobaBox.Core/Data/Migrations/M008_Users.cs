using FluentMigrator;

namespace YobaBox.Core.Data.Migrations;

[Migration(8, "Create Users and WorkspaceMembers tables")]
public sealed class M008_Users : Migration
{
	public override void Up()
	{
		Create.Table("Users")
			.WithColumn("Id").AsInt64().PrimaryKey().Identity().NotNullable()
			.WithColumn("Username").AsString(100).NotNullable().Unique()
			.WithColumn("PasswordHash").AsString(200).NotNullable()
			.WithColumn("CreatedAt").AsString().NotNullable().WithDefaultValue("");

		Create.Table("WorkspaceMembers")
			.WithColumn("Id").AsInt64().PrimaryKey().Identity().NotNullable()
			.WithColumn("UserId").AsInt64().NotNullable()
			.WithColumn("WorkspaceKey").AsString(100).NotNullable()
			.WithColumn("Role").AsInt32().NotNullable();

		Execute.Sql("INSERT INTO Users (Username, PasswordHash, CreatedAt) VALUES ('admin', '', datetime('now'))");
		Execute.Sql("INSERT INTO WorkspaceMembers (UserId, WorkspaceKey, Role) VALUES (1, '$system', 0)");
	}

	public override void Down()
	{
		Delete.Table("WorkspaceMembers");
		Delete.Table("Users");
	}
}
