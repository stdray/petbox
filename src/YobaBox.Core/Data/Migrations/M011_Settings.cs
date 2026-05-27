using FluentMigrator;

namespace YobaBox.Core.Data.Migrations;

[Migration(11, "Create Settings table for L2 generic key-value store")]
public sealed class M011_Settings : Migration
{
	public override void Up()
	{
		// SQLite needs the composite primary key declared inline (separate
		// ADD CONSTRAINT after CREATE TABLE is not supported).
		Create.Table("Settings")
			.WithColumn("Scope").AsString(20).NotNullable().PrimaryKey("PK_Settings")
			.WithColumn("ScopeKey").AsString(200).NotNullable().PrimaryKey("PK_Settings")
			.WithColumn("Path").AsString(200).NotNullable().PrimaryKey("PK_Settings")
			.WithColumn("Type").AsString(20).NotNullable()
			.WithColumn("Value").AsString(int.MaxValue).NotNullable()
			.WithColumn("UpdatedAt").AsDateTime().NotNullable()
			.WithColumn("UpdatedBy").AsInt64().Nullable();

		Create.Index("IX_Settings_ScopeKey")
			.OnTable("Settings")
			.OnColumn("Scope").Ascending()
			.OnColumn("ScopeKey").Ascending();
	}

	public override void Down() => Delete.Table("Settings");
}
