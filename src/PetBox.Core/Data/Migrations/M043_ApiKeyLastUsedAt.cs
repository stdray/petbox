using FluentMigrator;

namespace PetBox.Core.Data.Migrations;

// spec apikey-last-used: the moment a key was last authenticated with, so an unused key is
// identifiable. Nullable on purpose — NULL means "never seen since this column existed", which is
// exactly what every pre-existing key is, and it stays distinguishable from "used long ago".
// The value is COARSE: the auth hot path stamps memory only (IKeyStatService) and a background
// flusher folds the marks into this column roughly every 5 minutes.
[Migration(43, "Add ApiKeys.LastUsedAt (last successful authentication with the key)")]
public sealed class M043_ApiKeyLastUsedAt : Migration
{
	public override void Up() =>
		Create.Column("LastUsedAt").OnTable("ApiKeys").AsDateTime().Nullable();

	public override void Down() => Delete.Column("LastUsedAt").FromTable("ApiKeys");
}
