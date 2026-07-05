using FluentMigrator;

namespace PetBox.Core.Data.Migrations;

// M004 seeds the $system self-log key (`yb_key_system_internal`, referenced by
// Seq:SelfLog:ApiKey — PetBox ingests its own logs/traces with it) but predates the
// Name column (added by M014), so on existing DBs it carries an empty Name and shows
// blank in the admin key list. Backfill a human-readable label WITHOUT touching the
// live key's value/scopes. Idempotent + guarded: only the seeded key, and only while
// still unnamed — re-running or an operator-renamed row is left alone.
[Migration(34, "Name the seeded $system self-log API key")]
public sealed class M034_NameSeededSystemKey : Migration
{
	public override void Up() =>
		Execute.Sql(
			"UPDATE ApiKeys SET Name = 'system-internal' " +
			"WHERE Key = 'yb_key_system_internal' AND (Name IS NULL OR Name = '');");

	// Symmetric with Up: revert only the row this migration could have named, and only
	// while it still carries the exact label we set (an operator relabel is preserved).
	// Reverting to empty restores the pre-M034 state rather than being a no-op, so a
	// down-then-up round-trip is faithful.
	public override void Down() =>
		Execute.Sql(
			"UPDATE ApiKeys SET Name = '' " +
			"WHERE Key = 'yb_key_system_internal' AND Name = 'system-internal';");
}
