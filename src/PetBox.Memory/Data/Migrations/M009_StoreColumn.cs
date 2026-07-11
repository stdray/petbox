using FluentMigrator;
using PetBox.Core.Data;

namespace PetBox.Memory.Data.Migrations;

// Memory moves from one-file-per-(project,store) to one-file-per-PROJECT: a project's stores now
// share a single memory_entries table, partitioned by a Store column (the exact idiom the tasks
// tier already runs with plan_nodes.Board — M005_BoardColumn). Two stores each run an independent
// per-store version cursor, so (Key, Version) collides across stores: the PRIMARY KEY must become
// (Store, Key, Version) and active-key uniqueness must be per-store. Same for entry_usage, whose
// counter key becomes (Store, Key). SQLite cannot alter a PK in place → rebuild the tables,
// defaulting existing rows' Store to '' (a fresh per-project file is empty here; M010 stamps Store
// as it copies rows in from the legacy per-store files).
//
// The four SEARCH tables (search_fts / search_vec / search_cursor / search_deadletter) keep their
// shape on purpose: they are already ENTITY-addressed (Scope, Type, Id), and the store rides the
// leading `Type` column — exactly how tasks addresses a board (Type = board name). So the FTS5
// virtual table already carries the store as a leading UNINDEXED column, one lexical index and one
// vector index now cover every store of the project, and a store filter is a `Type IN (...)`
// predicate (SearchFilter.Types). Adding a second, redundant Store column there would fork the
// shared PetBox.Core.Search index implementations for no gain.
//
// Typed FluentMigrator DDL (no `IF NOT EXISTS`): a migration runs once per VersionInfo, so a
// tolerant CREATE would only swallow schema drift. Two things the typed API cannot express go
// through the NAMED, guarded SqliteDdl helpers instead of an anonymous Execute.Sql: the data
// moves (INSERT…SELECT → SqliteDdl.Raw, reason recorded) and the PARTIAL unique index
// (SqliteDdl.PartialIndex — a filtered index has no typed form, and .Filter() from the SqlServer
// extension would silently drop the WHERE here, turning it into a TOTAL unique index).
//
// Forward-only.
[Migration(9, "memory_entries/entry_usage: add Store, repoint PKs to (Store, …), per-store indexes")]
public sealed class M009_StoreColumn : SqliteMigration
{
	public override void Up()
	{
		Create.Table("memory_entries_new")
			.WithColumn("Store").AsString().NotNullable().WithDefaultValue("").PrimaryKey()
			.WithColumn("Key").AsString().NotNullable().PrimaryKey()
			.WithColumn("Version").AsInt64().NotNullable().PrimaryKey()
			.WithColumn("Type").AsInt32().NotNullable().WithDefaultValue(2)
			.WithColumn("Description").AsString().NotNullable()
			.WithColumn("Body").AsString().NotNullable()
			.WithColumn("Tags").AsString().NotNullable()
			.WithColumn("Metadata").AsString().NotNullable().WithDefaultValue("")
			.WithColumn("PrevKey").AsString().Nullable()
			.WithColumn("ActiveFrom").AsInt64().NotNullable()
			.WithColumn("ActiveTo").AsInt64().Nullable()
			.WithColumn("Created").AsString().NotNullable()
			.WithColumn("Updated").AsString().NotNullable();

		SqliteDdl.Raw(
			"table rebuild: move the memory_entries rows into the new (Store, Key, Version) shape — SQLite cannot alter a PK in place, and an INSERT..SELECT has no typed form. A fresh per-project file is empty here; a legacy per-store file adopted in place carries its rows over with an empty Store",
			"""
			INSERT INTO memory_entries_new (Store,Key,Version,Type,Description,Body,Tags,Metadata,PrevKey,ActiveFrom,ActiveTo,Created,Updated)
			SELECT '', Key, Version, Type, Description, Body, Tags, Metadata, PrevKey, ActiveFrom, ActiveTo, Created, Updated
			FROM memory_entries;
			""");
		Delete.Table("memory_entries");
		Rename.Table("memory_entries_new").To("memory_entries");

		Create.Index("ix_memory_entries_active").OnTable("memory_entries")
			.OnColumn("Store").Ascending()
			.OnColumn("ActiveTo").Ascending()
			.OnColumn("Key").Ascending();
		// PARTIAL unique index: one ACTIVE revision per key, per store.
		SqliteDdl.PartialIndex(
			name: "ux_memory_entries_active_key",
			table: "memory_entries",
			columns: ["Store", "Key"],
			where: "ActiveTo IS NULL",
			unique: true);

		Create.Table("entry_usage_new")
			.WithColumn("Store").AsString().NotNullable().WithDefaultValue("").PrimaryKey()
			.WithColumn("Key").AsString().NotNullable().PrimaryKey()
			.WithColumn("SurfacedCount").AsInt64().NotNullable().WithDefaultValue(0)
			.WithColumn("DeliberateCount").AsInt64().NotNullable().WithDefaultValue(0)
			.WithColumn("OpenedCount").AsInt64().NotNullable().WithDefaultValue(0)
			.WithColumn("LastHitAt").AsString().Nullable();

		SqliteDdl.Raw(
			"table rebuild: move the entry_usage counters into the new (Store, Key) shape — same INSERT..SELECT constraint as memory_entries above",
			"""
			INSERT INTO entry_usage_new (Store,Key,SurfacedCount,DeliberateCount,OpenedCount,LastHitAt)
			SELECT '', Key, SurfacedCount, DeliberateCount, OpenedCount, LastHitAt FROM entry_usage;
			""");
		Delete.Table("entry_usage");
		Rename.Table("entry_usage_new").To("entry_usage");

		// Progress log of the one-time legacy merge (M010) — one row per store actually copied in,
		// with the VERIFIED row counts. Also the resume marker: a re-run skips a logged store.
		Create.Table("memory_store_merge")
			.WithColumn("Store").AsString().NotNullable().PrimaryKey()
			.WithColumn("EntryRows").AsInt64().NotNullable()
			.WithColumn("UsageRows").AsInt64().NotNullable()
			.WithColumn("MergedAt").AsString().NotNullable();
	}

	public override void Down() { } // forward-only
}
