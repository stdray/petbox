using FluentMigrator;

namespace PetBox.Core.Data.Migrations;

// Store taxonomy (spec: memoverhaul), widening the system-store set. M030 marked
// `session-digests` IsSystem; `autocaptured` and `canon` are agent plumbing too (the
// autocapture sink and the curated canon index pulled into every session) and must be
// protected from casual deletion. New stores with those names are tagged at creation by
// MemoryStore.SystemStoreNames; backfill any pre-existing rows here. Case-insensitive to
// match how the flag is computed at creation (OrdinalIgnoreCase set membership).
[Migration(33, "Mark autocaptured + canon memory stores system (widen SystemStoreNames)")]
public sealed class M033_WidenSystemStores : Migration
{
	public override void Up() =>
		Execute.Sql(
			"UPDATE MemoryStores SET IsSystem = 1 " +
			"WHERE LOWER(Name) IN ('autocaptured', 'canon');");

	// Revert only the rows this migration could have flipped, and only if they still carry
	// the system-name (an operator could not have renamed a store — Name is the PK — so this
	// is exact). Symmetric with Up; safe because nothing else sets IsSystem for these names.
	public override void Down() =>
		Execute.Sql(
			"UPDATE MemoryStores SET IsSystem = 0 " +
			"WHERE LOWER(Name) IN ('autocaptured', 'canon');");
}
