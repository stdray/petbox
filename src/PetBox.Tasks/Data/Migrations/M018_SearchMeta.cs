using FluentMigrator;
using PetBox.Core.Data;

namespace PetBox.Tasks.Data.Migrations;

// search_meta — the Class-A REFERENCE layer (spec search-index-authority): the single authority of a
// node's index MEMBERSHIP and its query FACETS, written in the SAME entity transaction as the
// search_fts text row (TasksService upsert seam) so a committed node's facets are never stale.
// search_fts stays pure text; the facets a query filters on live here, so a facet join lands on a
// plain table instead of scanning the FTS5 virtual table.
//
// `search_meta` is one row per indexed entity, addressed (Scope, Type, Id) exactly like search_fts /
// search_vec: Scope=projectKey, Type=board, Id=node slug. Its columns are the COMPUTED facets —
// StatusKind (open|terminalok|terminalcancel, from MethodologyRuntime.StatusKindOf) and the temporal
// Created/Updated (stored as TEXT ISO-8601 like plan_nodes, so a range predicate sorts correctly).
// `search_meta_alias` is the alias SET (one row per alias) an identity lookup resolves through — for
// a task node its slug AND its NodeId (the NodeId the lexical index does not carry). The read legs
// that consume these (facet pushdown, identity resolution) are SEPARATE work and bring their own
// secondary indexes keyed to their access pattern; this migration is the write-side storage only.
//
// Plain typed FluentMigrator DDL — no FTS5, no partial index — so nothing here is SQLite-specific
// beyond the tier already being SQLite. Forward-only in effect: a fresh empty table backfills on the
// next search, gated by the TasksCursors.Meta projection marker (no data migration needed).
[Migration(18, "search_meta + search_meta_alias: Class-A facet/alias reference layer")]
public sealed class M018_SearchMeta : SqliteMigration
{
	public override void Up()
	{
		Create.Table("search_meta")
			.WithColumn("Scope").AsString().NotNullable().PrimaryKey()
			.WithColumn("Type").AsString().NotNullable().PrimaryKey()
			.WithColumn("Id").AsString().NotNullable().PrimaryKey()
			.WithColumn("StatusKind").AsString().NotNullable()
			.WithColumn("Created").AsString().NotNullable()
			.WithColumn("Updated").AsString().NotNullable();

		Create.Table("search_meta_alias")
			.WithColumn("Scope").AsString().NotNullable().PrimaryKey()
			.WithColumn("Type").AsString().NotNullable().PrimaryKey()
			.WithColumn("Id").AsString().NotNullable().PrimaryKey()
			.WithColumn("Alias").AsString().NotNullable().PrimaryKey();
	}

	// Symmetric inverse of Up(): drop the alias set first, then the facet table (no FK between them,
	// but keep the child-before-parent order the schema reads in).
	public override void Down()
	{
		Delete.Table("search_meta_alias");
		Delete.Table("search_meta");
	}
}
