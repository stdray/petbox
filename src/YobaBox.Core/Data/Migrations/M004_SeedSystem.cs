using FluentMigrator;

namespace YobaBox.Core.Data.Migrations;

[Migration(4, "Seed $system project and services for self-logging")]
public sealed class M004_SeedSystem : Migration
{
	public override void Up()
	{
		Insert.IntoTable("Projects").Row(new
		{
			Key = "$system",
			Name = "System",
			Description = "YobaBox internal services and self-logging",
		});

		Insert.IntoTable("ApiKeys").Row(new
		{
			Key = "yb_key_system_internal",
			ProjectKey = "$system",
			Scopes = "logs:ingest,logs:query,config:read",
			CreatedAt = DateTime.UtcNow,
		});
	}

	public override void Down()
	{
		Delete.FromTable("ApiKeys").Row(new { Key = "yb_key_system_internal" });
		Delete.FromTable("Projects").Row(new { Key = "$system" });
	}
}
