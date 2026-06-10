using FluentMigrator;

namespace PetBox.Tasks.Data.Migrations;

// Retrofit board search behind the PetBox.Core.Search contract (mirrors memory M006). Replaces
// plan_nodes_fts / plan_node_vec (keyed by NodeId) with the contract's entity-addressed tables:
// search_fts (Class-A lexical floor, written INSIDE the entity tx) + search_vec (Class-B vectors,
// dim 1024, materialized by the async-vectorization worker) + the worker's durable cursor/
// dead-letter state. Entity address: Scope=projectKey, Type=Board, Id=node slug (the temporal Key)
// — so the temporal log's slugs map straight through and the worker's per-board cursor uses
// IndexName=Board. The file is shared by all of a project's boards. Lexical content backfills on
// first search; vectors re-embed from cursor 0.
[Migration(9, "Replace plan_nodes_fts/plan_node_vec with contract search tables")]
public sealed class M009_SearchTables : Migration
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
		DROP TABLE IF EXISTS plan_nodes_fts;
		DROP TABLE IF EXISTS plan_node_vec;
		""");

	public override void Down() => Execute.Sql("""
		DROP TABLE IF EXISTS search_fts;
		DROP TABLE IF EXISTS search_vec;
		DROP TABLE IF EXISTS search_cursor;
		DROP TABLE IF EXISTS search_deadletter;
		CREATE VIRTUAL TABLE IF NOT EXISTS plan_nodes_fts USING fts5(
			NodeId UNINDEXED, Board UNINDEXED, Name, Body, Tags, tokenize='unicode61'
		);
		CREATE TABLE IF NOT EXISTS plan_node_vec (
			NodeId TEXT PRIMARY KEY, Board TEXT NOT NULL, Model TEXT NOT NULL,
			Dim INTEGER NOT NULL, Vec BLOB NOT NULL
		);
		""");
}
