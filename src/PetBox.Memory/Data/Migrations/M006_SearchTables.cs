using FluentMigrator;

namespace PetBox.Memory.Data.Migrations;

// Retrofit memory search behind the PetBox.Core.Search contract. Replaces the bespoke
// memory_fts / memory_vec (keyed only by entry Key, since the file is already per-store) with
// the contract's entity-addressed tables (Scope, Type, Id): search_fts (Class-A lexical floor,
// written INSIDE the entity tx) + search_vec (Class-B vectors, dim 1024, materialized by the
// async-vectorization worker) + the worker's durable cursor/dead-letter state. DDL mirrors
// SqliteFtsIndex/VectorSearchIndex/SqliteIndexCursorStore.EnsureSchema. Whole-assembly scan
// applies it to every memory store file. Lexical content is rebuilt cheaply on first search;
// vectors are re-embedded by the worker (cursor starts at 0 = full backfill).
[Migration(6, "Replace memory_fts/memory_vec with contract search tables (search_fts/vec/cursor/deadletter)")]
public sealed class M006_SearchTables : Migration
{
	public override void Up() => Execute.Sql("""
		CREATE VIRTUAL TABLE IF NOT EXISTS search_fts USING fts5(
			Scope UNINDEXED, Type UNINDEXED, Id UNINDEXED, Text, Tags, tokenize='unicode61'
		);
		CREATE TABLE IF NOT EXISTS search_vec (
			Scope TEXT NOT NULL, Type TEXT NOT NULL, Id TEXT NOT NULL,
			Model TEXT NOT NULL, Dim INTEGER NOT NULL, Vec BLOB NOT NULL,
			PRIMARY KEY (Scope, Type, Id)
		);
		CREATE TABLE IF NOT EXISTS search_cursor (
			IndexName TEXT PRIMARY KEY, Version INTEGER NOT NULL
		);
		CREATE TABLE IF NOT EXISTS search_deadletter (
			IndexName TEXT NOT NULL, Type TEXT NOT NULL, Id TEXT NOT NULL,
			Attempts INTEGER NOT NULL, Dead INTEGER NOT NULL,
			PRIMARY KEY (IndexName, Type, Id)
		);
		DROP TABLE IF EXISTS memory_fts;
		DROP TABLE IF EXISTS memory_vec;
		""");

	public override void Down() => Execute.Sql("""
		DROP TABLE IF EXISTS search_fts;
		DROP TABLE IF EXISTS search_vec;
		DROP TABLE IF EXISTS search_cursor;
		DROP TABLE IF EXISTS search_deadletter;
		CREATE VIRTUAL TABLE IF NOT EXISTS memory_fts USING fts5(
			Key UNINDEXED, Description, Body, Tags, tokenize='unicode61'
		);
		CREATE TABLE IF NOT EXISTS memory_vec (
			Key TEXT PRIMARY KEY, Model TEXT NOT NULL, Dim INTEGER NOT NULL, Vec BLOB NOT NULL
		);
		""");
}
