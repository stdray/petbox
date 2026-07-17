using FluentMigrator;
using PetBox.Core.Data;

namespace PetBox.Tasks.Data.Migrations;

// search-identity-leg: the identity read leg resolves an identifier by EQUALITY over
// search_meta_alias — `WHERE Scope = @scope AND Alias = @alias` (SqliteMetaIndex.ResolveIdentityAsync).
// M018 created search_meta_alias with only its PRIMARY KEY (Scope, Type, Id, Alias), on purpose: the
// index that serves the lookup was deferred to the consuming leg (this one), keyed to its access
// pattern. That access pattern is (Scope, Alias) — and it is NOT a left-prefix of the PK (Alias is the
// 4th PK column, behind Type and Id), so the PK cannot serve the lookup: SQLite would seek to the
// Scope range and then SCAN every alias row in the project, filtering Alias in memory. Identity runs on
// EVERY tasks_search query (the leg leads the ranking), so that scan is on the hot path and grows with
// the project. This composite index makes the lookup a direct seek. (Scope, Alias) also covers the
// board-narrowed variant — it seeks by (Scope, Alias) and filters the handful of matched rows by Type
// in memory, cheap. Not UNIQUE: one alias legitimately maps to several entities (a slug shared across
// boards), which is exactly the multi-hit case the leg surfaces.
//
// Additive DDL only — a new index over an existing table, no data migration. NUMBERED 19: 18 is the
// live M018_SearchMeta and 15 is BURNED (see the used-migration-numbers registry); 19 is the next free
// number above the tier's max.
[Migration(19, "search_meta_alias: (Scope, Alias) index for the identity read leg (search-identity-leg)")]
public sealed class M019_SearchMetaAliasIndex : SqliteMigration
{
	public override void Up()
	{
		Create.Index("ix_search_meta_alias_lookup").OnTable("search_meta_alias")
			.OnColumn("Scope").Ascending()
			.OnColumn("Alias").Ascending();
	}

	public override void Down() => Delete.Index("ix_search_meta_alias_lookup").OnTable("search_meta_alias");
}
