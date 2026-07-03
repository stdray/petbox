using FluentMigrator;

namespace PetBox.Core.Data.Migrations;

// Store taxonomy (spec: memoverhaul). A system store is machine plumbing, not user
// knowledge: excluded from the default memory_search sweep and set apart in the UI.
// `session-digests` (one per project, created by the digest job) is the first such
// store, so backfill it here; new stores are tagged at creation by MemoryStore.
[Migration(30, "Add MemoryStores.IsSystem + mark session-digests system")]
public sealed class M030_MemoryStoreIsSystem : Migration
{
	public override void Up()
	{
		Create.Column("IsSystem").OnTable("MemoryStores").AsBoolean().NotNullable().WithDefaultValue(false);
		Execute.Sql("UPDATE MemoryStores SET IsSystem = 1 WHERE Name = 'session-digests';");
	}

	public override void Down() => Delete.Column("IsSystem").FromTable("MemoryStores");
}
