using FluentMigrator;

namespace YobaBox.Core.Data.Migrations;

// Adds a Name column to ApiKeys so the create form can give each issued
// key a human-readable label ("ci pipeline", "claude code dev", etc.).
// Backfills empty string for existing rows — the column is NotNullable.
[Migration(14, "Add Name column to ApiKeys")]
public sealed class M014_ApiKeyName : Migration
{
	public override void Up()
	{
		Alter.Table("ApiKeys")
			.AddColumn("Name").AsString(200).NotNullable().WithDefaultValue(string.Empty);
	}

	public override void Down()
	{
		Delete.Column("Name").FromTable("ApiKeys");
	}
}
