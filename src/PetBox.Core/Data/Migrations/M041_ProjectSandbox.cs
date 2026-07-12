using FluentMigrator;

namespace PetBox.Core.Data.Migrations;

// Sandbox containment (spec work/smoke-writes-into-real-projects): smoke/background-job traffic
// keeps running with enrichment ON (no separate "smoke mode" to drift from prod), but the write
// GATE — ProjectScope.AuthorizesAsync — only lets a SandboxOnly key (M042) land in a project
// flagged here. Additive and defaulted false: every existing project stays a normal, non-sandbox
// project until an operator opts one in via project_create(sandbox:true) or a direct flip.
[Migration(41, "Add Projects.Sandbox (containment target for sandbox-only API keys)")]
public sealed class M041_ProjectSandbox : Migration
{
	public override void Up() =>
		Create.Column("Sandbox").OnTable("Projects").AsBoolean().NotNullable().WithDefaultValue(false);

	public override void Down() => Delete.Column("Sandbox").FromTable("Projects");
}
