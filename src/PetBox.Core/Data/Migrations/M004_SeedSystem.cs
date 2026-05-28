using FluentMigrator;

namespace PetBox.Core.Data.Migrations;

[Migration(4, "Seed $system internal API key for self-logging")]
public sealed class M004_SeedSystem : Migration
{
	public override void Up()
	{
		Execute.Sql("INSERT OR IGNORE INTO ApiKeys (Key, ProjectKey, Scopes, CreatedAt) " +
			"VALUES ('yb_key_system_internal', '$system', 'logs:ingest,logs:query,config:read', datetime('now'))");
	}

	public override void Down()
	{
		Execute.Sql("DELETE FROM ApiKeys WHERE Key = 'yb_key_system_internal'");
	}
}
