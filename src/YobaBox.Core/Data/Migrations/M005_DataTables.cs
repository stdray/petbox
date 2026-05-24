using FluentMigrator;

namespace YobaBox.Core.Data.Migrations;

[Migration(5, "Create DataTables table for yobadata")]
public sealed class M005_DataTables : Migration
{
	public override void Up()
	{
		Create.Table("DataTables")
			.WithColumn("Name").AsString(200).PrimaryKey().NotNullable()
			.WithColumn("ProjectKey").AsString(100).NotNullable()
			.WithColumn("Columns").AsString(int.MaxValue).NotNullable().WithDefaultValue("[]")
			.WithColumn("Read").AsBoolean().NotNullable().WithDefaultValue(true)
			.WithColumn("Write").AsBoolean().NotNullable().WithDefaultValue(false)
			.WithColumn("Delete").AsBoolean().NotNullable().WithDefaultValue(false);

		Create.Index("IX_DataTables_ProjectKey")
			.OnTable("DataTables")
			.OnColumn("ProjectKey").Ascending();
	}

	public override void Down()
	{
		Delete.Table("DataTables");
	}
}
