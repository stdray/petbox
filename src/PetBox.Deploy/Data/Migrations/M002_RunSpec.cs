using FluentMigrator;

namespace PetBox.Deploy.Data.Migrations;

// Adds the declarative container run-spec (canonical JSON, "{}" = none) to deployments.
// NOTE: RunSpec is part of ConfigHash from this release on, so every existing deployment's
// hash changes once — agents recreate their containers on the first poll after rollout.
[Migration(2, "Add deploy_deployment.RunSpec (canonical-JSON container run-spec)")]
public sealed class M002_RunSpec : Migration
{
	public override void Up()
	{
		// SQLite's ADD COLUMN has no IF NOT EXISTS, and concurrent Ensure() callers (parallel
		// test hosts on one db file) can both pass the VersionInfo check — guard on the actual
		// schema, like M001's IF NOT EXISTS statements do.
		if (!Schema.Table("deploy_deployment").Column("RunSpec").Exists())
			Execute.Sql("ALTER TABLE deploy_deployment ADD COLUMN RunSpec TEXT NOT NULL DEFAULT '{}';");
	}

	public override void Down() => Execute.Sql(
		"ALTER TABLE deploy_deployment DROP COLUMN RunSpec;");
}
