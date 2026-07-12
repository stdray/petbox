using Microsoft.Data.Sqlite;
using PetBox.Data;
using PetBox.Data.Schema;

namespace PetBox.Tests.Data;

public sealed class SchemaRunnerTests : IDisposable
{
	readonly string _baseDir;
	readonly DataDbFactory _factory;
	readonly SchemaRunner _runner;

	public SchemaRunnerTests()
	{
		_baseDir = Path.Combine(Path.GetTempPath(), "petbox-schema-test-" + Guid.NewGuid().ToString("N"));
		_factory = new DataDbFactory(_baseDir);
		_runner = new SchemaRunner();
	}

	public void Dispose() => TestDirs.CleanupOrDefer(_baseDir);

	async Task<string> NewDbAsync()
	{
		var name = "db-" + Guid.NewGuid().ToString("N")[..6];
		await _factory.CreateAsync("test", name, DataDbFactory.DefaultMaxPageCount);
		return name;
	}

	// Mirrors production: every Apply runs on its own freshly-opened, quota'd connection.
	async Task<SchemaApplyResult> ApplyAsync(string dbName, string scriptName, string sql)
	{
		await using var conn = await _factory.OpenAsync("test", dbName, DataDbFactory.DefaultMaxPageCount);
		return _runner.Apply(conn, scriptName, sql);
	}

	string Cs(string dbName) => _factory.GetConnectionString("test", dbName);

	[Fact]
	public async Task FirstApplication_RunsScript_ReturnsApplied()
	{
		var db = await NewDbAsync();

		var result = await ApplyAsync(db, "M001_create_votes",
			"CREATE TABLE votes (id INTEGER PRIMARY KEY, film TEXT NOT NULL)");

		result.Kind.Should().Be(SchemaApplyKind.Applied, "error: " + result.Error);
		result.Hash.Should().NotBeNullOrEmpty();
		result.Error.Should().BeNull();

		await using var conn = new SqliteConnection(Cs(db));
		await conn.OpenAsync();
		await using var cmd = conn.CreateCommand();
		cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE name = 'votes'";
		((long?)await cmd.ExecuteScalarAsync()).Should().Be(1);
	}

	[Fact]
	public async Task SecondApplication_SameNameSameSql_NoOp_AlreadyApplied()
	{
		var db = await NewDbAsync();
		var sql = "CREATE TABLE votes (id INTEGER PRIMARY KEY)";

		(await ApplyAsync(db, "M001", sql)).Kind.Should().Be(SchemaApplyKind.Applied);

		var second = await ApplyAsync(db, "M001", sql);
		second.Kind.Should().Be(SchemaApplyKind.AlreadyApplied);
	}

	[Fact]
	public async Task SecondApplication_SameNameCosmeticChange_StillAlreadyApplied()
	{
		// Whitespace/case differences must normalize to the same hash.
		var db = await NewDbAsync();

		(await ApplyAsync(db, "M001", "CREATE TABLE votes (id INTEGER PRIMARY KEY)"))
			.Kind.Should().Be(SchemaApplyKind.Applied);

		var second = await ApplyAsync(db, "M001", "create  table  votes  (id  integer  primary  key);");
		second.Kind.Should().Be(SchemaApplyKind.AlreadyApplied);
	}

	[Fact]
	public async Task SecondApplication_SameNameDifferentSql_Conflict()
	{
		var db = await NewDbAsync();
		(await ApplyAsync(db, "M001", "CREATE TABLE votes (id INTEGER)")).Kind.Should().Be(SchemaApplyKind.Applied);

		var conflict = await ApplyAsync(db, "M001", "CREATE TABLE votes (id TEXT)");

		conflict.Kind.Should().Be(SchemaApplyKind.Conflict);
		conflict.ExistingHash.Should().NotBeNullOrEmpty();
		conflict.Hash.Should().NotBe(conflict.ExistingHash);
	}

	[Fact]
	public async Task DifferentName_AppliesIndependently()
	{
		var db = await NewDbAsync();

		(await ApplyAsync(db, "M001", "CREATE TABLE a (id INTEGER)")).Kind.Should().Be(SchemaApplyKind.Applied);
		(await ApplyAsync(db, "M002", "CREATE TABLE b (id INTEGER)")).Kind.Should().Be(SchemaApplyKind.Applied);

		await using var conn = new SqliteConnection(Cs(db));
		await conn.OpenAsync();
		await using var cmd = conn.CreateCommand();
		cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE name IN ('a', 'b')";
		((long?)await cmd.ExecuteScalarAsync()).Should().Be(2);
	}

	[Fact]
	public async Task FailedScript_ReturnsFailed_HashNotRecorded()
	{
		var db = await NewDbAsync();

		var result = await ApplyAsync(db, "M001_bad", "CREATE TABLE valid (id INTEGER); BOGUS SYNTAX HERE;");

		result.Kind.Should().Be(SchemaApplyKind.Failed);
		result.Error.Should().NotBeNullOrEmpty();

		// Re-applying the bad script must NOT report AlreadyApplied — DbUp does
		// not insert the journal row on failure.
		var retry = await ApplyAsync(db, "M001_bad", "CREATE TABLE valid (id INTEGER); BOGUS SYNTAX HERE;");
		retry.Kind.Should().Be(SchemaApplyKind.Failed);
	}

	[Fact]
	public async Task FailedScript_FixedAndRetried_AppliesSuccessfully()
	{
		var db = await NewDbAsync();

		(await ApplyAsync(db, "M001", "CREATE TABLE x (id INTEGER); BOGUS;"))
			.Kind.Should().Be(SchemaApplyKind.Failed);

		var fixed_ = await ApplyAsync(db, "M001", "CREATE TABLE x (id INTEGER)");
		fixed_.Kind.Should().Be(SchemaApplyKind.Applied);
	}

	[Fact]
	public async Task JournalTable_Has_Hash_Column()
	{
		var db = await NewDbAsync();
		(await ApplyAsync(db, "M001", "CREATE TABLE x (id INTEGER)")).Kind.Should().Be(SchemaApplyKind.Applied);

		await using var conn = new SqliteConnection(Cs(db));
		await conn.OpenAsync();
		await using var cmd = conn.CreateCommand();
		cmd.CommandText = $"SELECT ScriptName, Hash FROM {SchemaRunner.JournalTableName}";
		await using var reader = await cmd.ExecuteReaderAsync();

		reader.Read().Should().BeTrue();
		reader.GetString(0).Should().Be("M001");
		reader.GetString(1).Should().NotBeNullOrEmpty();
	}

	[Fact]
	public async Task Migration_Over_Quota_Fails_With_Full_Not_Applied()
	{
		// Schema migrations write too: a migration that blows past the DataDb quota
		// must fail with SQLITE_FULL, not silently balloon the file. Regression guard
		// for DbUp opening its own (unquota'd) connection from the connection string.
		var name = "db-" + Guid.NewGuid().ToString("N")[..6];
		await _factory.CreateAsync("test", name, 64); // ~256 KB at 4 KB pages
		TestDirs.ClearPoolsUnder(_baseDir);           // no create-time connection to inherit

		await using var conn = await _factory.OpenAsync("test", name, 64);
		var result = _runner.Apply(conn, "M001_fat",
			"""
			CREATE TABLE blob_rows (id INTEGER PRIMARY KEY, payload BLOB);
			INSERT INTO blob_rows (payload)
			  WITH RECURSIVE c(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM c WHERE i < 500)
			  SELECT randomblob(4096) FROM c;
			""");

		result.Kind.Should().Be(SchemaApplyKind.Failed);
		result.Error.Should().Contain("full");
	}
}
