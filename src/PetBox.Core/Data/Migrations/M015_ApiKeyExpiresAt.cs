using FluentMigrator;

namespace PetBox.Core.Data.Migrations;

// Adds a nullable ExpiresAt column to ApiKeys for temporary agent/onboarding keys.
// NULL (the default for existing rows) means the key never expires.
[Migration(15, "Add ExpiresAt column to ApiKeys")]
public sealed class M015_ApiKeyExpiresAt : Migration
{
	public override void Up()
	{
		Alter.Table("ApiKeys")
			.AddColumn("ExpiresAt").AsDateTime().Nullable();
	}

	public override void Down()
	{
		Delete.Column("ExpiresAt").FromTable("ApiKeys");
	}
}
