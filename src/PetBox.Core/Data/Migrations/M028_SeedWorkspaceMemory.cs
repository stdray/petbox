using FluentMigrator;

namespace PetBox.Core.Data.Migrations;

// Seed the built-in "$workspace" project — the reserved container for cross-project
// shared memory (the `workspace` scope of memory.remember/recall). Memory stores are
// addressed by projectKey on disk but MemoryStore.CreateAsync guards that the project
// row exists, so the reserved container needs a Projects row like "$system" does. It
// lives under the "$system" workspace; it is a memory container, not a user project.
[Migration(28, "Seed $workspace project for cross-project memory")]
public sealed class M028_SeedWorkspaceMemory : Migration
{
	public override void Up()
	{
		Execute.Sql("INSERT OR IGNORE INTO Projects (Key, WorkspaceKey, Name, Description) " +
			"VALUES ('$workspace', '$system', 'Workspace', 'Built-in container for cross-project shared memory')");
	}

	public override void Down()
	{
		Execute.Sql("DELETE FROM Projects WHERE Key = '$workspace'");
	}
}
