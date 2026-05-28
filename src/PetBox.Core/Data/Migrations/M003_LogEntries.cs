using FluentMigrator;

namespace PetBox.Core.Data.Migrations;

[Migration(3, "Create LogEntries table with indexes")]
public sealed class M003_LogEntries : Migration
{
	public override void Up()
	{
		Create.Table("LogEntries")
			.WithColumn("Id").AsInt64().PrimaryKey().Identity()
			.WithColumn("ServiceKey").AsString(100).NotNullable()
			.WithColumn("TimestampMs").AsInt64().NotNullable()
			.WithColumn("Level").AsInt32().NotNullable()
			.WithColumn("Message").AsString(int.MaxValue).NotNullable()
			.WithColumn("MessageTemplate").AsString(int.MaxValue).NotNullable()
			.WithColumn("Exception").AsString(int.MaxValue).Nullable()
			.WithColumn("PropertiesJson").AsString(int.MaxValue).NotNullable().WithDefaultValue("{}")
			.WithColumn("TemplateHash").AsInt64().NotNullable().WithDefaultValue(0);

		Create.Index("IX_LogEntries_ServiceKey_TimestampMs")
			.OnTable("LogEntries")
			.OnColumn("ServiceKey").Ascending()
			.OnColumn("TimestampMs").Descending();

		Create.Index("IX_LogEntries_TimestampMs")
			.OnTable("LogEntries")
			.OnColumn("TimestampMs").Descending();

		Create.Index("IX_LogEntries_Level")
			.OnTable("LogEntries")
			.OnColumn("Level").Ascending();
	}

	public override void Down()
	{
		Delete.Table("LogEntries");
	}
}
