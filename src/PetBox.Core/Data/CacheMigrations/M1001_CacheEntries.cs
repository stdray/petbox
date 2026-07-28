using FluentMigrator;

namespace PetBox.Core.Data.CacheMigrations;

// Baseline schema for the disk cache file (data/cache/cache.db).
//
// ── WHY THIS SET IS FENCED OFF TWICE ──────────────────────────────────────────────────────────────
// It is the SECOND migration set in the PetBox.Core assembly, and FluentMigrator's default unit of
// isolation is the assembly. Both fences below are load-bearing, and each covers what the other
// cannot:
//
//  1. ITS OWN NAMESPACE, which MigrationRunner scans by. This is the fence that makes the two sets
//     CORRECT: without it core.db would grow a cache_entries table, and — far worse — the cache file
//     would be built with the entire core schema. The namespace is a SIBLING of
//     PetBox.Core.Data.Migrations, not a child: the runner enables NestedNamespaces, so a child would
//     be swept straight back into the core set.
//
//  2. ITS OWN VERSION RANGE (1000+). This is the fence that keeps everything which does NOT know
//     about the namespace filter working. Versions are per-scan, so a second `M001` in the assembly
//     is a duplicate to any scan that sees both — and such scans exist: eighteen migration tests
//     build their own runner over the Core assembly to drive partial MigrateUp(version) calls, and
//     every one of them died with `DuplicateMigrationException: Duplicate migration version 1` while
//     this file was numbered 1. Found by running the gate, not by reading it. The separation also
//     reads as intent at a glance: a 1000-series number says "not the core.db timeline".
//
// ── VERSIONED, EVEN THOUGH THE DATA IS DISPOSABLE ─────────────────────────────────────────────────
// The two version axes here are not interchangeable. This one tracks the SCHEMA;
// SearchPoolCache.PayloadFormatVersion tracks the format of a stored VALUE. A payload-shape change
// needs no migration at all (old rows read as a miss and age out on TTL); a column change needs one
// and cannot be expressed by the other.
[Migration(1001, "Create the disk cache entries table")]
public sealed class M1001_CacheEntries : Migration
{
	public override void Up()
	{
		Create.Table("cache_entries")
			.WithColumn("key").AsString().NotNullable().PrimaryKey()
			.WithColumn("value").AsBinary().NotNullable()
			.WithColumn("expires_at_ticks").AsInt64().NotNullable();

		// The sweep's access path: `WHERE expires_at_ticks <> never AND expires_at_ticks < now`.
		// Without it every cleanup pass is a full scan of the whole cache.
		Create.Index("ix_cache_entries_expires").OnTable("cache_entries")
			.OnColumn("expires_at_ticks").Ascending();
	}

	public override void Down()
	{
		Delete.Index("ix_cache_entries_expires").OnTable("cache_entries");
		Delete.Table("cache_entries");
	}
}
