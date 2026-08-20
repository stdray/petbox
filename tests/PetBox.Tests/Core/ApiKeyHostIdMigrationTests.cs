using FluentMigrator.Runner;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Data;
using PetBox.Core.Data.Migrations;

namespace PetBox.Tests.Migrations;

// M050 (spec node-grant-own-carrier) moves the node id off ApiKeys.ProjectKey onto its own column.
// Staged migration test: park the DB at v49 — the shape the LIVE database is in — seed the node key
// exactly as the pre-M050 mint path wrote it, run M050, and check the row actually moved rather than
// being left half-migrated.
//
// The control rows are the point of the test, not padding: alongside the node key `node:local-pc`
// (whose ProjectKey holds the node id) sits an ordinary project key whose ProjectKey holds the SAME
// STRING. Before M050 those two rows were indistinguishable to the deploy plane's reader — that IS
// the collision the card is about. After M050 the migration must move exactly one of them and leave
// the other alone, which is the first half of the proof; DeployApiTests proves the second half (the
// reader no longer accepts the project key as a node).
public sealed class ApiKeyHostIdMigrationTests : IDisposable
{
	// The live fleet host on petbox.3po.su — same id, so this test rehearses the production UPDATE.
	const string NodeId = "local-pc";
	const string NodeKey = "yb_key_node_deadbeefdeadbeefdeadbeefdeadbeef";
	const string NodeKeyRef = "node:local-pc";

	// A project key that is named EXACTLY like the node. Pre-M050 this row and the node key carried
	// the same value in the same column; the migration must not touch it.
	const string TwinProjectKey = "yb_key_twin_project";

	readonly string _dir;
	readonly string _cs;

	public ApiKeyHostIdMigrationTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-m050-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		// A COPY of the once-per-process v49 template, not forty-nine migrations per test.
		_cs = PreM050CoreDb.CopyTo(Path.Combine(_dir, "petbox.db"));
	}

	public void Dispose()
	{
		SqliteConnection.ClearPool(new SqliteConnection(_cs));
		TestDirs.CleanupOrDefer(_dir);
	}

	[Fact]
	public void M050_MovesNodeIdToHostId_AndLeavesTheSameNamedProjectKeyAlone()
	{
		SeedPreM050Rows();

		MigrateTo(50);

		var rows = Read();

		// The live node key: its id moved to the carrier of its own type, and the tenant column it
		// was squatting in is now empty — a blank project claim authorizes no project at all.
		rows[NodeKey].HostId.Should().Be(NodeId);
		rows[NodeKey].ProjectKey.Should().BeEmpty();

		// The project that shares the node's name is NOT a machine and must not acquire a host.
		rows[TwinProjectKey].HostId.Should().BeNull();
		rows[TwinProjectKey].ProjectKey.Should().Be(NodeId);

		// An unrelated key is untouched in both columns.
		rows["yb_key_ordinary"].HostId.Should().BeNull();
		rows["yb_key_ordinary"].ProjectKey.Should().Be("$system");
	}

	[Fact]
	public void M050_Down_PutsTheNodeIdBackWhereThePreM050ReaderLooksForIt()
	{
		SeedPreM050Rows();
		MigrateTo(50);

		MigrateDownTo(49);

		// Reversible: the column is gone and the node id is back in ProjectKey, which is exactly the
		// state the pre-M050 code reads. A rollback of the deployment therefore leaves a WORKING
		// node key rather than an orphaned one.
		Columns("ApiKeys").Should().NotContain("HostId");

		using var db = new PetBoxDb(PetBoxDb.CreateOptions(_cs));
		var byKey = db.ApiKeys.Select(k => new { k.Key, k.ProjectKey }).ToDictionary(k => k.Key, k => k.ProjectKey);
		byKey[NodeKey].Should().Be(NodeId);
		byKey[TwinProjectKey].Should().Be(NodeId);
		byKey["yb_key_ordinary"].Should().Be("$system");
	}

	[Fact]
	public void M050_IsIdempotent_ReRunningTheBackfillCannotBlankAnAlreadyMovedRow()
	{
		SeedPreM050Rows();
		MigrateTo(50);

		// Replaying the UPDATE (what a re-run of the statement would do) must be a no-op, not a
		// second move that overwrites HostId with the now-empty ProjectKey.
		Exec(
			"UPDATE ApiKeys SET HostId = ProjectKey, ProjectKey = '' " +
			"WHERE Name LIKE 'node:%' AND Key LIKE 'yb_key_node_%' AND HostId IS NULL AND ProjectKey <> '';");

		var rows = Read();
		rows[NodeKey].HostId.Should().Be(NodeId);
		rows[NodeKey].ProjectKey.Should().BeEmpty();
	}

	// The three rows as the PRE-M050 code would have written them. Columns are listed explicitly
	// because the DB is parked at v49 and a full-entity insert would name HostId, which is the very
	// column that does not exist yet.
	void SeedPreM050Rows() => Exec($"""
		INSERT INTO ApiKeys (Key, ProjectKey, Scopes, Name, CreatedAt) VALUES
			('{NodeKey}', '{NodeId}', 'agent:poll,agent:heartbeat,logs:ingest', '{NodeKeyRef}', '2026-01-01'),
			('{TwinProjectKey}', '{NodeId}', 'config:read', 'twin project key', '2026-01-01'),
			('yb_key_ordinary', '$system', 'config:read', 'ordinary', '2026-01-01');
		""");

	Dictionary<string, (string ProjectKey, string? HostId)> Read()
	{
		var result = new Dictionary<string, (string, string?)>(StringComparer.Ordinal);
		using var conn = new SqliteConnection(_cs);
		conn.Open();
		using var cmd = conn.CreateCommand();
		cmd.CommandText = "SELECT Key, ProjectKey, HostId FROM ApiKeys;";
		using var reader = cmd.ExecuteReader();
		while (reader.Read())
			result[reader.GetString(0)] = (reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2));
		return result;
	}

	List<string> Columns(string table)
	{
		var cols = new List<string>();
		using var conn = new SqliteConnection(_cs);
		conn.Open();
		using var cmd = conn.CreateCommand();
		cmd.CommandText = $"PRAGMA table_info({table});";
		using var reader = cmd.ExecuteReader();
		while (reader.Read()) cols.Add(reader.GetString(1));
		return cols;
	}

	void Exec(string sql)
	{
		using var conn = new SqliteConnection(_cs);
		conn.Open();
		using var cmd = conn.CreateCommand();
		cmd.CommandText = sql;
		cmd.ExecuteNonQuery();
	}

	void MigrateTo(long version) => WithRunner(r => r.MigrateUp(version));

	void MigrateDownTo(long version) => WithRunner(r => r.MigrateDown(version));

	void WithRunner(Action<IMigrationRunner> act)
	{
		using var services = new ServiceCollection()
			.AddFluentMigratorCore()
			.ConfigureRunner(rb => rb
				.AddSQLite()
				.WithGlobalConnectionString(_cs)
				.ScanIn(typeof(M001_Initial).Assembly).For.Migrations())
			.BuildServiceProvider();
		using var scope = services.CreateScope();
		act(scope.ServiceProvider.GetRequiredService<IMigrationRunner>());
	}
}
