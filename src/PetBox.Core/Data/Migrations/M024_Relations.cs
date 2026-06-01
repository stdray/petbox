using FluentMigrator;

namespace PetBox.Core.Data.Migrations;

[Migration(24, "Create Relation table (typed directed edges between node ids)")]
public sealed class M024_Relations : Migration
{
	public override void Up()
	{
		Create.Table("Relation")
			.WithColumn("Id").AsString(100).NotNullable().PrimaryKey("PK_Relation")
			.WithColumn("ProjectKey").AsString(100).NotNullable()
			.WithColumn("Kind").AsString(40).NotNullable()
			.WithColumn("FromNodeId").AsString(100).NotNullable()
			.WithColumn("ToNodeId").AsString(100).NotNullable()
			.WithColumn("CreatedAt").AsDateTime().NotNullable();

		Create.Index("ix_relation_from").OnTable("Relation")
			.OnColumn("ProjectKey").Ascending().OnColumn("FromNodeId").Ascending();
		Create.Index("ix_relation_to").OnTable("Relation")
			.OnColumn("ProjectKey").Ascending().OnColumn("ToNodeId").Ascending();
	}

	public override void Down() => Delete.Table("Relation");
}
