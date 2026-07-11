using FluentMigrator;

namespace PetBox.Config.Data.Migrations;

// BASELINE of the per-workspace config file (config/{workspace}.db): the binding store, its
// audit history, and the tag vocabulary. Enums (Kind) stored as INTEGER; secrets live in the
// Ciphertext/Iv/AuthTag triple (Value stays empty for them), so these rows are NOT reproducible
// from anywhere else — losing or rebuilding this table is data loss, not an inconvenience.
//
// WHY EVERY OBJECT IS UNDER A `Schema...Exists()` GUARD — read before copying this pattern:
// this tier had NO migrations at all until now. Its schema was created at runtime by hand-written
// idempotent DDL (ConfigSchema.Ensure: `CREATE TABLE IF NOT EXISTS` + an `AddColumnIfMissing`
// ladder), so every config file on a live deployment ALREADY has these tables and NO VersionInfo.
// A bare `Create.Table` would blow up with "table already exists" on all of them. The guards let
// this migration ADOPT the existing objects: what the file already has, it keeps (rows and all);
// what it lacks, this migration creates — so an adopted file and a fresh file converge on the same
// schema. Same pardon, same reasoning as PetBox.Sessions M007_SearchCursorTables.
//
// THE COLUMN LADDER IS PART OF THE ADOPTION, NOT DECORATION. ConfigBindings grew by
// `ALTER TABLE ADD COLUMN` over time (Kind -> Ciphertext/Iv/AuthTag -> Version/ContentHash ->
// IsDeleted/DeletedAt, with IX_ConfigBindings_IsDeleted last). A file created before any given
// step and never reopened by a newer build could have STOPPED at that step, so "the table exists"
// does not imply "the table is complete". Hence: when the table is absent it is created whole;
// when it is present each late column is added only if missing, in the historical order, with the
// same type/default the old ladder used — so ANY historical stage converges here. (Checked against
// all 10 live workspace files on 2026-07-11: all of them already carry the full column set, i.e.
// the ladder is a no-op there. It stays because the guard costs nothing and a stale file is
// exactly the case a baseline exists to survive.)
//
// THIS GUARD IS NOT A LICENCE. It is legal in a BASELINE that adopts pre-migration files. Every
// later migration in this tier is written with a plain typed `Create.Table` / `Alter.Table`: from
// M002 on, VersionInfo is authoritative and a tolerant DDL could only hide drift.
[Migration(1, "Adopt/create the config binding, history and tag-vocabulary tables")]
public sealed class M001_ConfigBaseline : Migration
{
	public override void Up()
	{
		if (!Schema.Table("ConfigBindings").Exists())
			Create.Table("ConfigBindings")
				// `.Nullable()` on the identity PK looks wrong and is deliberate (same trick as
				// Sessions M007): without it FluentMigrator emits `INTEGER NOT NULL PRIMARY KEY
				// AUTOINCREMENT`, while the live files — and SQLite's own rowid-alias idiom — carry
				// `INTEGER PRIMARY KEY AUTOINCREMENT`. The NOT NULL is a no-op on a rowid alias, but
				// it shows up in PRAGMA table_info, which would leave a FRESH file permanently
				// different in shape from an ADOPTED one. Same for the two tables below.
				.WithColumn("Id").AsInt64().Nullable().PrimaryKey().Identity()
				.WithColumn("Path").AsString().NotNullable()
				.WithColumn("Value").AsString().NotNullable()
				.WithColumn("Tags").AsString().NotNullable()
				.WithColumn("Kind").AsInt32().NotNullable().WithDefaultValue(0)
				// The secret triple: AES-GCM ciphertext + IV + auth tag, NULL for plain bindings.
				.WithColumn("Ciphertext").AsString().Nullable()
				.WithColumn("Iv").AsString().Nullable()
				.WithColumn("AuthTag").AsString().Nullable()
				.WithColumn("Version").AsInt64().NotNullable().WithDefaultValue(1)
				.WithColumn("ContentHash").AsString().NotNullable().WithDefaultValue("")
				.WithColumn("IsDeleted").AsInt32().NotNullable().WithDefaultValue(0)
				.WithColumn("DeletedAt").AsString().Nullable()
				.WithColumn("CreatedAt").AsString().NotNullable()
				.WithColumn("UpdatedAt").AsString().NotNullable();
		else
			AdoptBindingColumns();

		if (!Schema.Table("ConfigBindingHistory").Exists())
			Create.Table("ConfigBindingHistory")
				.WithColumn("Id").AsInt64().Nullable().PrimaryKey().Identity()
				.WithColumn("BindingId").AsInt64().NotNullable()
				.WithColumn("Action").AsString().NotNullable()
				.WithColumn("Path").AsString().NotNullable()
				.WithColumn("Tags").AsString().NotNullable()
				.WithColumn("Kind").AsInt32().NotNullable().WithDefaultValue(0)
				.WithColumn("OldValue").AsString().Nullable()
				.WithColumn("NewValue").AsString().Nullable()
				.WithColumn("Actor").AsString().NotNullable().WithDefaultValue("system")
				.WithColumn("At").AsString().NotNullable();

		// `.Unique()` on TagKey emits BOTH the inline `UNIQUE` column constraint (which is all the
		// live files have, backed by sqlite_autoindex_TagVocabulary_1) AND a companion unique index
		// IX_TagVocabulary_TagKey — that is FluentMigrator's SQLite generator, not a choice made
		// here. So a FRESH file carries one redundant index that an ADOPTED file does not; both
		// enforce the same invariant (one row per tag key), and the alternative — rebuilding a live
		// table to add an index nothing queries — would be risk for zero gain. It is the only
		// schema difference left between an adopted and a fresh config file.
		if (!Schema.Table("TagVocabulary").Exists())
			Create.Table("TagVocabulary")
				.WithColumn("Id").AsInt64().Nullable().PrimaryKey().Identity()
				.WithColumn("TagKey").AsString().NotNullable().Unique()
				.WithColumn("Description").AsString().Nullable()
				.WithColumn("CreatedAt").AsString().NotNullable();

		CreateIndexIfMissing("IX_ConfigBindings_Path", "ConfigBindings",
			() => Create.Index("IX_ConfigBindings_Path").OnTable("ConfigBindings")
				.OnColumn("Path").Ascending());

		// Last rung of the ladder: added together with IsDeleted, so a file can carry the column
		// and still lack the index (or the whole table and lack both).
		CreateIndexIfMissing("IX_ConfigBindings_IsDeleted", "ConfigBindings",
			() => Create.Index("IX_ConfigBindings_IsDeleted").OnTable("ConfigBindings")
				.OnColumn("IsDeleted").Ascending());

		CreateIndexIfMissing("IX_ConfigBindingHistory_At", "ConfigBindingHistory",
			() => Create.Index("IX_ConfigBindingHistory_At").OnTable("ConfigBindingHistory")
				.OnColumn("At").Descending());

		CreateIndexIfMissing("IX_ConfigBindingHistory_Path", "ConfigBindingHistory",
			() => Create.Index("IX_ConfigBindingHistory_Path").OnTable("ConfigBindingHistory")
				.OnColumn("Path").Ascending());
	}

	// The `AddColumnIfMissing` ladder of the pre-migration ConfigSchema, in its historical order and
	// with its exact types/defaults. `Alter.Table.AddColumn` is the typed form of the ALTER the old
	// code emitted, so an adopted column is indistinguishable from one born in Create.Table above.
	void AdoptBindingColumns()
	{
		AddColumnIfMissing("Kind", c => c.AsInt32().NotNullable().WithDefaultValue(0));
		AddColumnIfMissing("Ciphertext", c => c.AsString().Nullable());
		AddColumnIfMissing("Iv", c => c.AsString().Nullable());
		AddColumnIfMissing("AuthTag", c => c.AsString().Nullable());
		AddColumnIfMissing("Version", c => c.AsInt64().NotNullable().WithDefaultValue(1));
		AddColumnIfMissing("ContentHash", c => c.AsString().NotNullable().WithDefaultValue(""));
		AddColumnIfMissing("IsDeleted", c => c.AsInt32().NotNullable().WithDefaultValue(0));
		AddColumnIfMissing("DeletedAt", c => c.AsString().Nullable());
	}

	void AddColumnIfMissing(
		string column,
		Action<FluentMigrator.Builders.Alter.Table.IAlterTableColumnAsTypeSyntax> define)
	{
		if (Schema.Table("ConfigBindings").Column(column).Exists()) return;
		define(Alter.Table("ConfigBindings").AddColumn(column));
	}

	void CreateIndexIfMissing(string index, string table, Action create)
	{
		if (!Schema.Table(table).Index(index).Exists()) create();
	}

	public override void Down()
	{
		Delete.Table("TagVocabulary");
		Delete.Table("ConfigBindingHistory");
		Delete.Table("ConfigBindings");
	}
}
