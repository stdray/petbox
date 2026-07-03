using FluentMigrator;

namespace PetBox.Core.Data.Migrations;

// Revive the built-in "$workspace" project as the REAL cross-project memory container
// (the `workspace` scope of memory_remember/search). M028 first seeded it, but on prod the
// row was manually deleted after the 2026-06-03 consolidation that briefly routed the
// workspace scope into "$system"; that consolidation is now reverted, so the row must exist
// again. Re-ensure it idempotently: INSERT OR IGNORE inserts on prod (row gone) and is a
// no-op on fresh DBs (M028 already seeded it). MemoryStore.CreateAsync guards that the
// project row exists, so this Projects row is what lets the workspace container be written.
[Migration(31, "Ensure $workspace project exists for cross-project memory")]
public sealed class M031_EnsureWorkspaceMemory : Migration
{
	public override void Up()
	{
		Execute.Sql("INSERT OR IGNORE INTO Projects (Key, WorkspaceKey, Name, Description) " +
			"VALUES ('$workspace', '$system', 'Workspace', 'Built-in container for cross-project shared memory')");
	}

	// No Down: removing the row would orphan any workspace memory written against it. The
	// revival is a floor, not a reversible schema change.
	public override void Down() { }
}
