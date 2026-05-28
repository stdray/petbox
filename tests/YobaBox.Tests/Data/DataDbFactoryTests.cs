using Microsoft.Data.Sqlite;
using YobaBox.Data;

namespace YobaBox.Tests.Data;

// Tests share the global SqliteConnection pool — serialize across this file
// and SchemaRunnerTests to avoid one Dispose's ClearAllPools yanking a
// connection out of the other's in-flight test.
[Collection("DataModule")]
public sealed class DataDbFactoryTests : IDisposable
{
	readonly string _baseDir;
	readonly DataDbFactory _factory;

	public DataDbFactoryTests()
	{
		_baseDir = Path.Combine(Path.GetTempPath(), "yobabox-test-" + Guid.NewGuid().ToString("N"));
		_factory = new DataDbFactory(_baseDir);
	}

	public void Dispose()
	{
		// SqliteConnection.ClearAllPools releases any pooled file handles so the
		// tempdir can be deleted on Windows.
		SqliteConnection.ClearAllPools();
		if (Directory.Exists(_baseDir)) Directory.Delete(_baseDir, recursive: true);
	}

	[Fact]
	public void GetDbPath_Returns_Project_Subdirectory_File()
	{
		var path = _factory.GetDbPath("myproj", "cache");
		path.Should().EndWith(Path.Combine("myproj", "cache.db"));
	}

	[Fact]
	public async Task CreateAsync_Creates_File_With_WAL_And_Quota()
	{
		await _factory.CreateAsync("myproj", "cache", 1000);

		var path = _factory.GetDbPath("myproj", "cache");
		File.Exists(path).Should().BeTrue();

		var cs = _factory.GetConnectionString("myproj", "cache");
		await using var conn = new SqliteConnection(cs);
		await conn.OpenAsync();

		await using var jcmd = conn.CreateCommand();
		jcmd.CommandText = "PRAGMA journal_mode";
		var mode = (string?)await jcmd.ExecuteScalarAsync();
		mode.Should().Be("wal");

		await using var qcmd = conn.CreateCommand();
		qcmd.CommandText = "PRAGMA max_page_count";
		var quota = (long?)await qcmd.ExecuteScalarAsync();
		quota.Should().Be(1000);
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
		SqliteConnection.ClearAllPools();

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
		SqliteConnection.ClearAllPools();

		await using var connB = new SqliteConnection(csB);
		await connB.OpenAsync();
		await using var check = connB.CreateCommand();
		check.CommandText = "SELECT count(*) FROM sqlite_master WHERE name = 'only_in_a'";
		var count = (long?)await check.ExecuteScalarAsync();
		count.Should().Be(0);
	}
}
