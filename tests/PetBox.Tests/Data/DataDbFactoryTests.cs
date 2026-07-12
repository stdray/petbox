using Microsoft.Data.Sqlite;
using PetBox.Data;

namespace PetBox.Tests.Data;

public sealed class DataDbFactoryTests : IDisposable
{
	readonly string _baseDir;
	readonly DataDbFactory _factory;

	public DataDbFactoryTests()
	{
		_baseDir = Path.Combine(Path.GetTempPath(), "petbox-test-" + Guid.NewGuid().ToString("N"));
		_factory = new DataDbFactory(_baseDir);
	}

	public void Dispose() => TestDirs.CleanupOrDefer(_baseDir);

	[Fact]
	public void GetDbPath_Returns_Project_Subdirectory_File()
	{
		var path = _factory.GetDbPath("myproj", "cache");
		path.Should().EndWith(Path.Combine("myproj", "cache.db"));
	}

	[Fact]
	public async Task CreateAsync_Persists_WAL_But_Not_The_Quota()
	{
		// Pins the asymmetry that used to be a production bug: journal_mode lives in the
		// file header and survives any reopen; max_page_count is per-CONNECTION and does
		// NOT. The pool is cleared first so this is a genuinely fresh connection — reading
		// back over a warm pool would show the create-time connection's quota and prove
		// nothing.
		await _factory.CreateAsync("myproj", "cache", 1000);

		var path = _factory.GetDbPath("myproj", "cache");
		File.Exists(path).Should().BeTrue();

		TestDirs.ClearPoolsUnder(_baseDir);

		var cs = _factory.GetConnectionString("myproj", "cache");
		await using var conn = new SqliteConnection(cs);
		await conn.OpenAsync();

		await using var jcmd = conn.CreateCommand();
		jcmd.CommandText = "PRAGMA journal_mode";
		var mode = (string?)await jcmd.ExecuteScalarAsync();
		mode.Should().Be("wal", "journal_mode is written into the DB file header");

		await using var qcmd = conn.CreateCommand();
		qcmd.CommandText = "PRAGMA max_page_count";
		var quota = (long?)await qcmd.ExecuteScalarAsync();
		quota.Should().NotBe(1000, "max_page_count is per-connection — a raw open carries NO quota");
	}

	[Fact]
	public async Task OpenAsync_Applies_Quota_On_A_Fresh_Connection()
	{
		await _factory.CreateAsync("myproj", "cache", 1000);
		TestDirs.ClearPoolsUnder(_baseDir); // drop the create-time connection

		await using var conn = await _factory.OpenAsync("myproj", "cache", 1000);
		await using var qcmd = conn.CreateCommand();
		qcmd.CommandText = "PRAGMA max_page_count";
		((long?)await qcmd.ExecuteScalarAsync()).Should().Be(1000);
	}

	[Fact]
	public async Task OpenAsync_Quota_Is_Enforced_On_A_Non_CreateTime_Connection()
	{
		// The regression this whole change exists for: a write past the quota on a
		// connection that is NOT the one CreateAsync used must fail with SQLITE_FULL.
		await _factory.CreateAsync("myproj", "cache", 64); // ~256 KB at 4 KB pages
		TestDirs.ClearPoolsUnder(_baseDir);

		await using var conn = await _factory.OpenAsync("myproj", "cache", 64);
		await using var create = conn.CreateCommand();
		create.CommandText = "CREATE TABLE blobs (id INTEGER PRIMARY KEY, data BLOB)";
		await create.ExecuteNonQueryAsync();

		await using var insert = conn.CreateCommand();
		insert.CommandText = "INSERT INTO blobs (data) VALUES (randomblob(1048576))"; // 1 MB >> 256 KB
		var act = async () => await insert.ExecuteNonQueryAsync();

		var ex = await act.Should().ThrowAsync<SqliteException>();
		ex.Which.SqliteErrorCode.Should().Be(13, "SQLITE_FULL — the quota is real");
	}

	[Fact]
	public async Task CreateAsync_Throws_If_File_Already_Exists()
	{
		await _factory.CreateAsync("myproj", "cache", DataDbFactory.DefaultMaxPageCount);

		Func<Task> act = () => _factory.CreateAsync("myproj", "cache", DataDbFactory.DefaultMaxPageCount);
		await act.Should().ThrowAsync<InvalidOperationException>();
	}

	[Fact]
	public void GetConnectionString_Throws_If_File_Missing()
	{
		Action act = () => _factory.GetConnectionString("nope", "missing");
		act.Should().Throw<FileNotFoundException>();
	}

	[Fact]
	public async Task TryDelete_Removes_File_And_Sidecars()
	{
		await _factory.CreateAsync("myproj", "cache", DataDbFactory.DefaultMaxPageCount);

		// Open + close a transaction to create the -wal / -shm sidecar files.
		var cs = _factory.GetConnectionString("myproj", "cache");
		await using (var conn = new SqliteConnection(cs))
		{
			await conn.OpenAsync();
			await using var cmd = conn.CreateCommand();
			cmd.CommandText = "CREATE TABLE x (id INTEGER); INSERT INTO x VALUES (1);";
			await cmd.ExecuteNonQueryAsync();
		}
		TestDirs.ClearPoolsUnder(_baseDir);

		var deleted = _factory.TryDelete("myproj", "cache");

		deleted.Should().BeTrue();
		File.Exists(_factory.GetDbPath("myproj", "cache")).Should().BeFalse();
	}

	[Fact]
	public async Task ListPhysicalDbs_Returns_DbNames_Without_Extension()
	{
		await _factory.CreateAsync("myproj", "cache", DataDbFactory.DefaultMaxPageCount);
		await _factory.CreateAsync("myproj", "audit", DataDbFactory.DefaultMaxPageCount);
		await _factory.CreateAsync("other", "metrics", DataDbFactory.DefaultMaxPageCount);

		var myproj = _factory.ListPhysicalDbs("myproj").OrderBy(s => s).ToList();
		myproj.Should().Equal("audit", "cache");

		var other = _factory.ListPhysicalDbs("other");
		other.Should().Equal("metrics");
	}

	[Fact]
	public void ListPhysicalDbs_Returns_Empty_For_Unknown_Project()
	{
		_factory.ListPhysicalDbs("nope").Should().BeEmpty();
	}

	[Fact]
	public async Task Different_Projects_With_Same_DbName_Are_Isolated()
	{
		await _factory.CreateAsync("a", "cache", DataDbFactory.DefaultMaxPageCount);
		await _factory.CreateAsync("b", "cache", DataDbFactory.DefaultMaxPageCount);

		var csA = _factory.GetConnectionString("a", "cache");
		var csB = _factory.GetConnectionString("b", "cache");

		await using (var connA = new SqliteConnection(csA))
		{
			await connA.OpenAsync();
			await using var cmd = connA.CreateCommand();
			cmd.CommandText = "CREATE TABLE only_in_a (id INTEGER)";
			await cmd.ExecuteNonQueryAsync();
		}
		TestDirs.ClearPoolsUnder(_baseDir);

		await using var connB = new SqliteConnection(csB);
		await connB.OpenAsync();
		await using var check = connB.CreateCommand();
		check.CommandText = "SELECT count(*) FROM sqlite_master WHERE name = 'only_in_a'";
		var count = (long?)await check.ExecuteScalarAsync();
		count.Should().Be(0);
	}
}
