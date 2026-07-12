using FluentMigrator;

namespace PetBox.Core.Data.Migrations;

// Sandbox containment (spec work/smoke-writes-into-real-projects), the key-side half of M041.
// A SandboxOnly key is authorized against a projectKey only when BOTH the existing identity
// check (ProjectScope.Authorizes: claim vs. projectKey) AND this containment check
// (Projects.Sandbox = true for that projectKey) pass — see ProjectScope.AuthorizesAsync. Additive
// and defaulted false: every existing key keeps writing wherever its claim already authorized it.
[Migration(42, "Add ApiKeys.SandboxOnly (containment source for the sandbox write gate)")]
public sealed class M042_ApiKeySandboxOnly : Migration
{
	public override void Up() =>
		Create.Column("SandboxOnly").OnTable("ApiKeys").AsBoolean().NotNullable().WithDefaultValue(false);

	public override void Down() => Delete.Column("SandboxOnly").FromTable("ApiKeys");
}
