using FluentMigrator;

namespace PetBox.Core.Data.Migrations;

// spec access-attribution, via work `workspaceadmin-self-issue-admin-provision-root`: WHO issued a
// key. ApiKeys was the one credential table with no attribution at all — ShareLink and
// HealthEndpoint both already carry a CreatedBy — so an escalation performed by minting a key left
// nothing to reconstruct it from, and the keys of a departed account could not be found.
//
// This is also what makes the new grant gate CHECKABLE rather than merely present: a privileged
// scope on a key now comes with a claim about who put it there, and that claim can be read back.
//
// NULLABLE, no backfill, no default. Every pre-existing key genuinely has no recorded issuer, and
// NULL is the only value that says so. Defaulting to 'system' would assert that the operator minted
// rows nobody can account for — inventing attribution is strictly worse than admitting its absence,
// because the invented value is indistinguishable from a real one. The admin table renders the NULL
// as "unknown" in words for the same reason.
[Migration(49, "Add ApiKeys.CreatedBy (the actor that issued the key)")]
public sealed class M049_ApiKeyCreatedBy : Migration
{
	public override void Up() =>
		Create.Column("CreatedBy").OnTable("ApiKeys").AsString().Nullable();

	public override void Down() => Delete.Column("CreatedBy").FromTable("ApiKeys");
}
