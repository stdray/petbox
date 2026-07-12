using FluentMigrator;

namespace PetBox.Core.Data.Migrations;

// A cross-project key's claim ("*") authorizes every project but supplies none, so every MCP
// call from such a key that omits the optional projectKey fails. Give it an explicit fallback:
// ApiKeys.DefaultProjectKey. Additive and nullable — existing keys keep working with it NULL
// (no default ⇒ exactly the old behavior).
[Migration(40, "Add ApiKeys.DefaultProjectKey (fallback project for cross-project keys)")]
public sealed class M040_ApiKeyDefaultProject : Migration
{
	public override void Up() =>
		Create.Column("DefaultProjectKey").OnTable("ApiKeys").AsString(100).Nullable();

	public override void Down() => Delete.Column("DefaultProjectKey").FromTable("ApiKeys");
}
