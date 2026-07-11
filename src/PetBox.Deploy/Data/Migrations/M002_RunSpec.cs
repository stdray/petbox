using FluentMigrator;

namespace PetBox.Deploy.Data.Migrations;

// Adds the declarative container run-spec (canonical JSON, "{}" = none) to deployments.
// NOTE: RunSpec is part of ConfigHash from this release on, so every existing deployment's
// hash changes once — agents recreate their containers on the first poll after rollout.
//
// WHY THE `Schema...Column(...).Exists()` GUARD THAT USED TO WRAP THIS ADD COLUMN IS GONE:
// its stated reason was "concurrent Ensure() callers (parallel test hosts on one db file) can both
// pass the VersionInfo check". Two answers. (1) They cannot: MigrationRunner.Run serializes
// MigrateUp per connection string precisely because FluentMigrator's own VersionInfo bootstrap is
// not concurrency-safe (prod is a single process). (2) Even if they could, the guard would not
// have saved the run — both racers would go on to INSERT the same Version into VersionInfo, whose
// UC_Version unique index rejects the second. The guard could only ever move the failure, never
// prevent it. What it did do reliably was hide a divergent column. So: plain ADD COLUMN. The
// table is M001's, and VersionInfo guarantees M001 ran.
// (Contrast PetBox.Sessions M007, where a Schema.Table().Exists() guard IS justified: there the
// tables were created at RUNTIME, behind VersionInfo's back, and must be adopted. Deploy's schema
// has been born in migrations from day one — there is nothing to adopt.)
[Migration(2, "Add deploy_deployment.RunSpec (canonical-JSON container run-spec)")]
public sealed class M002_RunSpec : Migration
{
	public override void Up() =>
		Alter.Table("deploy_deployment")
			.AddColumn("RunSpec").AsString().NotNullable().WithDefaultValue("{}");

	public override void Down() =>
		Delete.Column("RunSpec").FromTable("deploy_deployment");
}
