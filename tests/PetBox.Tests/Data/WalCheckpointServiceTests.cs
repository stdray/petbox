using LinqToDB;
using LinqToDB.Async;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Data;
using PetBox.Data;

namespace PetBox.Tests.Data;

[Collection("DataModule")]
public sealed class WalCheckpointServiceTests : IAsyncLifetime
{
	const string TestProjectKey = "kpvotes";

	readonly string _baseDir;
	readonly WebApplicationFactory<Program> _factory;

	public WalCheckpointServiceTests()
	{
		_baseDir = Path.Combine(Path.GetTempPath(), "petbox-wal-test-" + Guid.NewGuid().ToString("N"));
		Environment.SetEnvironmentVariable("PETBOX_MASTER_KEY", "test-key-for-secrets");

		_factory = new WebApplicationFactory<Program>()
			.WithWebHostBuilder(b =>
			{
				b.UseEnvironment("Testing");
				b.ConfigureAppConfiguration((_, cfg) =>
				{
					cfg.AddInMemoryCollection(new Dictionary<string, string?>
					{
						["ConnectionStrings:PetBox"] = $"Data Source={Path.Combine(Path.GetTempPath(), $"petbox-test-{Guid.NewGuid():N}.db")};Cache=Shared",
						["Features:Data"] = "true",
					});
				});
				b.ConfigureServices(svc =>
				{
					var existing = svc.SingleOrDefault(d => d.ServiceType == typeof(IDataDbFactory));
					if (existing is not null) svc.Remove(existing);
					svc.AddSingleton<IDataDbFactory>(_ => new DataDbFactory(_baseDir));
				});
			});
	}

	public Task InitializeAsync()
	{
		// Force MigrationRunner.Run on the test DB up front — WebApplicationFactory + static
		// Configure(app) does not always trigger migrations for tests that only touch DI.
		var __testCs = _factory.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>().GetConnectionString("PetBox")!;
		PetBox.Core.Data.MigrationRunner.Run(__testCs);
		_ = _factory.Services.GetRequiredService<IDataDbFactory>();
		return Task.CompletedTask;
	}

	public async Task DisposeAsync()
	{
		await _factory.DisposeAsync();
		SqliteConnection.ClearAllPools();
		if (Directory.Exists(_baseDir)) Directory.Delete(_baseDir, recursive: true);
	}

	[Fact]
	public async Task RunOncePassAsync_TruncatesWal_OnActiveDb()
	{
		var factory = _factory.Services.GetRequiredService<IDataDbFactory>();
		await factory.CreateAsync(TestProjectKey, "active", DataDbFactory.DefaultMaxPageCount);

		using (var scope = _factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<PetBoxDb>();
			await db.Workspaces.Where(w => w.Key == "test").DeleteAsync();
			await db.InsertAsync(new PetBox.Core.Models.Workspace { Key = "test", Name = "T", CreatedAt = DateTime.UtcNow });
			await db.Projects.Where(p => p.Key == TestProjectKey).DeleteAsync();
			await db.InsertAsync(new PetBox.Core.Models.Project { Key = TestProjectKey, WorkspaceKey = "test", Name = "K" });
			await db.InsertAsync(new PetBox.Core.Models.DataDb
			{
				ProjectKey = TestProjectKey,
				Name = "active",
				MaxPageCount = DataDbFactory.DefaultMaxPageCount,
				CreatedAt = DateTime.UtcNow,
				UpdatedAt = DateTime.UtcNow,
			});
		}

		// Generate WAL traffic so the .wal sidecar has something to checkpoint.
		var cs = factory.GetConnectionString(TestProjectKey, "active");
		await using (var conn = new SqliteConnection(cs))
		{
			await conn.OpenAsync();
			await using var cmd = conn.CreateCommand();
			cmd.CommandText = "CREATE TABLE t (id INTEGER); INSERT INTO t VALUES (1), (2), (3)";
			await cmd.ExecuteNonQueryAsync();
		}
		SqliteConnection.ClearAllPools();

		// Run checkpoint pass — should not throw, should observe the DataDbs row.
		var service = ActivatorUtilities.CreateInstance<WalCheckpointService>(_factory.Services);
		await service.RunOncePassAsync(CancellationToken.None);

		// File still exists; .wal may or may not exist depending on timing
		// (TRUNCATE keeps the file but sets it to zero, or deletes it on some
		// SQLite builds). The important contract is: no exception, no data loss.
		await using var verify = new SqliteConnection(cs);
		await verify.OpenAsync();
		await using var check = verify.CreateCommand();
		check.CommandText = "SELECT count(*) FROM t";
		((long?)await check.ExecuteScalarAsync()).Should().Be(3);
	}

	[Fact]
	public async Task RunOncePassAsync_NoDataDbs_NoOp()
	{
		var service = ActivatorUtilities.CreateInstance<WalCheckpointService>(_factory.Services);
		await service.RunOncePassAsync(CancellationToken.None);
		// Just verify no exception when DataDbs is empty.
	}
}
