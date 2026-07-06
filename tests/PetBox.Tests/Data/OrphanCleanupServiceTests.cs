using LinqToDB;
using LinqToDB.Async;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Data;
using PetBox.Data;

namespace PetBox.Tests.Data;

public sealed class OrphanCleanupServiceTests : IAsyncLifetime
{
	const string TestProjectKey = "kpvotes";

	readonly string _baseDir;
	readonly WebApplicationFactory<Program> _factory;

	public OrphanCleanupServiceTests()
	{
		_baseDir = Path.Combine(Path.GetTempPath(), "petbox-orphan-test-" + Guid.NewGuid().ToString("N"));
		Environment.SetEnvironmentVariable("PETBOX_MASTER_KEY", "test-key-for-secrets");

		_factory = new WebApplicationFactory<Program>()
			.WithWebHostBuilder(b =>
			{
				b.UseEnvironment("Testing");
				b.ConfigureAppConfiguration((_, cfg) =>
				{
					cfg.AddInMemoryCollection(new Dictionary<string, string?>
					{
						["ConnectionStrings:PetBox"] = TestSchema.NewTempConnectionString(),
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
		TestSchema.Core(__testCs);
		// Force singletons to materialize.
		_ = _factory.Services.GetRequiredService<IDataDbFactory>();
		return Task.CompletedTask;
	}

	public async Task DisposeAsync()
	{
		await _factory.DisposeAsync();
		TestDirs.CleanupOrDefer(_baseDir);
	}

	[Fact]
	public async Task RunOncePassAsync_RemovesFiles_WithoutMetadataRow()
	{
		var factory = _factory.Services.GetRequiredService<IDataDbFactory>();
		await factory.CreateAsync(TestProjectKey, "orphan1", DataDbFactory.DefaultMaxPageCount);
		await factory.CreateAsync(TestProjectKey, "orphan2", DataDbFactory.DefaultMaxPageCount);

		// No DataDbs rows inserted — both files are orphans by definition.
		TestDirs.ClearPoolsUnder(_baseDir);

		var service = ActivatorUtilities.CreateInstance<OrphanCleanupService>(_factory.Services);
		await service.RunOncePassAsync(CancellationToken.None);

		File.Exists(factory.GetDbPath(TestProjectKey, "orphan1")).Should().BeFalse();
		File.Exists(factory.GetDbPath(TestProjectKey, "orphan2")).Should().BeFalse();
	}

	[Fact]
	public async Task RunOncePassAsync_LeavesFiles_WithMetadataRow()
	{
		var factory = _factory.Services.GetRequiredService<IDataDbFactory>();
		await factory.CreateAsync(TestProjectKey, "kept", DataDbFactory.DefaultMaxPageCount);

		using (var scope = _factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<PetBoxDb>();
			var now = DateTime.UtcNow;
			await db.InsertAsync(new PetBox.Core.Models.DataDb
			{
				ProjectKey = TestProjectKey,
				Name = "kept",
				MaxPageCount = DataDbFactory.DefaultMaxPageCount,
				CreatedAt = now,
				UpdatedAt = now,
			});
		}
		TestDirs.ClearPoolsUnder(_baseDir);

		var service = ActivatorUtilities.CreateInstance<OrphanCleanupService>(_factory.Services);
		await service.RunOncePassAsync(CancellationToken.None);

		File.Exists(factory.GetDbPath(TestProjectKey, "kept")).Should().BeTrue();
	}

	[Fact]
	public async Task RunOncePassAsync_HandlesProject_WithOnlyOrphans_NotInDataDbs()
	{
		// Project directory exists, no metadata rows for it. Files should be
		// cleaned up just as for known projects.
		var factory = _factory.Services.GetRequiredService<IDataDbFactory>();
		await factory.CreateAsync("vanished-project", "lonely", DataDbFactory.DefaultMaxPageCount);
		TestDirs.ClearPoolsUnder(_baseDir);

		var service = ActivatorUtilities.CreateInstance<OrphanCleanupService>(_factory.Services);
		await service.RunOncePassAsync(CancellationToken.None);

		File.Exists(factory.GetDbPath("vanished-project", "lonely")).Should().BeFalse();
	}
}
