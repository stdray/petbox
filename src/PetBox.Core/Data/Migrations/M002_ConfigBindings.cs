using FluentMigrator;

namespace PetBox.Core.Data.Migrations;

[Migration(2)]
public sealed class M002_ConfigBindings : Migration
{
	public override void Up()
	{
		Create.Table("ConfigBindings")
			.WithColumn("Id").AsInt64().PrimaryKey().Identity()
			.WithColumn("Path").AsString(500).NotNullable()
			.WithColumn("Value").AsString(int.MaxValue).NotNullable()
			.WithColumn("Tags").AsString(1000).NotNullable()
			.WithColumn("CreatedAt").AsDateTime().NotNullable()
			.WithColumn("UpdatedAt").AsDateTime().NotNullable();

		Create.Index("IX_ConfigBindings_Path_Tags")
			.OnTable("ConfigBindings")
			.OnColumn("Path").Ascending()
			.OnColumn("Tags").Ascending();
	}

	public override void Down() => Delete.Table("ConfigBindings");
}
