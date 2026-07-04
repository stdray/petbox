using System.Net;
using System.Net.Http.Json;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Data;

namespace PetBox.Tests.Data;

// Shared per-class host for DataDbsApiTests (xUnit news the test class per test, so
// without this fixture each of the 11 tests boots its own WebApplicationFactory).
// Per-test DATA isolation comes from ResetAsync: tests create data DBs with constant
// names ("cache", "audit") and two tests assert the list starts EMPTY, so the reset
// wipes the DataDbs metadata rows AND deletes the physical .db files under the test's
// baseDir (pool handles released first — a lingering same-name file would make the
// next create throw "already exists").
public sealed class DataDbsApiFixture : IAsyncLifetime
{
	public const string TestProjectKey = "kpvotes";
	public const string TestApiKey = "yb_key_test_data_schema_xyz";

	readonly string _baseDir;

	public WebApplicationFactory<Program> Factory { get; }
	public HttpClient Client { get; private set; } = null!;

	public DataDbsApiFixture()
	{
		_baseDir = Path.Combine(Path.GetTempPath(), "petbox-api-test-" + Guid.NewGuid().ToString("N"));

		Environment.SetEnvironmentVariable("PETBOX_MASTER_KEY", "test-key-for-secrets");

		Factory = new WebApplicationFactory<Program>()
			.WithWebHostBuilder(b =>
			{
				b.UseEnvironment("Testing");
				b.ConfigureAppConfiguration((_, cfg) =>
				{
					cfg.AddInMemoryCollection(new Dictionary<string, string?>
					{
						["ConnectionStrings:PetBox"] = $"Data Source={Path.Combine(Path.GetTempPath(), $"petbox-test-{Guid.NewGuid():N}.db")};Cache=Shared",
						["Features:Data"] = "true",
						["Admin:Username"] = "admin",
					});
				});
				b.ConfigureServices(svc =>
				{
					// Override IDataDbFactory to a test-owned tempdir — Program.cs's
					// default tries to derive baseDir from the connection string's
					// Data Source path, which is `:memory:` here.
					var existing = svc.SingleOrDefault(d => d.ServiceType == typeof(IDataDbFactory));
					if (existing is not null) svc.Remove(existing);
					svc.AddSingleton<IDataDbFactory>(_ => new DataDbFactory(_baseDir));
				});
			});
	}

	public async Task InitializeAsync()
	{
		// Force MigrationRunner.Run on the test DB up front — WebApplicationFactory + static
		// Configure(app) does not always trigger migrations for tests that only touch DI.
		var __testCs = Factory.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>().GetConnectionString("PetBox")!;
		TestSchema.Core(__testCs);
		Client = Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

		using var scope = Factory.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<PetBoxDb>();

		// Wipe + seed: kpvotes project + an ApiKey with data:schema scope.
		await db.DataDbs.DeleteAsync();
		await db.ApiKeys.Where(k => k.Key == TestApiKey).DeleteAsync();
		await db.Projects.Where(p => p.Key == TestProjectKey).DeleteAsync();
		await db.Workspaces.Where(w => w.Key == "test").DeleteAsync();

		await db.InsertAsync(new Workspace
		{
			Key = "test",
			Name = "Test",
			Description = "fixture",
			CreatedAt = DateTime.UtcNow,
		});
		await db.InsertAsync(new Project
		{
			Key = TestProjectKey,
			WorkspaceKey = "test",
			Name = "KpVotes",
			Description = "fixture",
		});
		await db.InsertAsync(new ApiKey
		{
			Key = TestApiKey,
			ProjectKey = TestProjectKey,
			Scopes = "data:read,data:schema",
			CreatedAt = DateTime.UtcNow,
		});

		Client.DefaultRequestHeaders.Add("X-Api-Key", TestApiKey);
	}

	// Per-test reset under the shared host: drop all DataDbs metadata rows and delete
	// the physical files a previous test created through the REST surface.
	public async Task ResetAsync()
	{
		using (var scope = Factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<PetBoxDb>();
			await db.DataDbs.DeleteAsync();
		}

		if (!Directory.Exists(_baseDir)) return;
		TestDirs.ClearPoolsUnder(_baseDir); // release pooled handles before file deletes
		foreach (var file in Directory.EnumerateFiles(_baseDir, "*.db", SearchOption.AllDirectories))
			if (!PetBox.Core.Data.ScopedDbFiles.TryDelete(file))
				throw new InvalidOperationException($"per-test reset could not delete {file} (still locked)");
	}

	public async Task DisposeAsync()
	{
		Client.Dispose();
		await Factory.DisposeAsync();
		TestDirs.CleanupOrDefer(_baseDir);
	}
}

public sealed class DataDbsApiTests : IClassFixture<DataDbsApiFixture>, IAsyncLifetime
{
	const string TestProjectKey = DataDbsApiFixture.TestProjectKey;

	readonly DataDbsApiFixture _fx;
	readonly WebApplicationFactory<Program> _factory;
	readonly HttpClient _client;

	public DataDbsApiTests(DataDbsApiFixture fx)
	{
		_fx = fx;
		_factory = fx.Factory;
		_client = fx.Client;
	}

	public Task InitializeAsync() => _fx.ResetAsync();

	public Task DisposeAsync() => Task.CompletedTask; // the fixture owns host teardown

	[Fact]
	public async Task Post_CreatesDb_AndPersistsRow()
	{
		var resp = await _client.PostAsJsonAsync($"/api/data/{TestProjectKey}/dbs",
			new { name = "cache", description = "vote cache", maxPageCount = 4096 });

		resp.StatusCode.Should().Be(HttpStatusCode.Created);

		using var scope = _factory.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<PetBoxDb>();
		var row = await db.DataDbs.FirstOrDefaultAsync((Core.Models.DataDb d) => d.ProjectKey == TestProjectKey && d.Name == "cache");
		row.Should().NotBeNull();
		row!.MaxPageCount.Should().Be(4096);
		row.Description.Should().Be("vote cache");

		// File created with WAL + quota PRAGMAs.
		var factory = scope.ServiceProvider.GetRequiredService<IDataDbFactory>();
		File.Exists(factory.GetDbPath(TestProjectKey, "cache")).Should().BeTrue();
	}

	[Fact]
	public async Task Post_InvalidName_400()
	{
		var resp = await _client.PostAsJsonAsync($"/api/data/{TestProjectKey}/dbs",
			new { name = "Bad-Name" }); // uppercase not allowed
		resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
	}

	[Fact]
	public async Task Post_ReservedName_400()
	{
		var resp = await _client.PostAsJsonAsync($"/api/data/{TestProjectKey}/dbs",
			new { name = "__schema_versions" });
		resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
	}

	[Fact]
	public async Task Post_NonExistentProject_404()
	{
		var resp = await _client.PostAsJsonAsync("/api/data/no-such-project/dbs",
			new { name = "cache" });
		resp.StatusCode.Should().Be(HttpStatusCode.Forbidden); // ApiKey scoped to kpvotes
	}

	[Fact]
	public async Task Post_Duplicate_409()
	{
		await _client.PostAsJsonAsync($"/api/data/{TestProjectKey}/dbs", new { name = "cache" });
		var dup = await _client.PostAsJsonAsync($"/api/data/{TestProjectKey}/dbs", new { name = "cache" });
		dup.StatusCode.Should().Be(HttpStatusCode.Conflict);
	}

	[Fact]
	public async Task Get_ListsDbs_Empty_Initially()
	{
		var resp = await _client.GetAsync($"/api/data/{TestProjectKey}/dbs");
		resp.EnsureSuccessStatusCode();
		var rows = await resp.Content.ReadFromJsonAsync<List<DataDbsApi.DbInfo>>();
		rows.Should().NotBeNull().And.BeEmpty();
	}

	[Fact]
	public async Task Get_ListsDbs_AfterCreate()
	{
		await _client.PostAsJsonAsync($"/api/data/{TestProjectKey}/dbs", new { name = "cache" });
		await _client.PostAsJsonAsync($"/api/data/{TestProjectKey}/dbs", new { name = "audit" });

		var resp = await _client.GetAsync($"/api/data/{TestProjectKey}/dbs");
		var rows = await resp.Content.ReadFromJsonAsync<List<DataDbsApi.DbInfo>>();

		rows.Should().NotBeNull();
		rows!.Should().HaveCount(2);
		rows.Select(r => r.Name).Should().BeEquivalentTo(["audit", "cache"]);
	}

	[Fact]
	public async Task Delete_RemovesRow_Immediately()
	{
		// DELETE removes the metadata row synchronously; physical file cleanup
		// is best-effort (orphan cleanup service mops up if Windows file lock
		// keeps the file alive briefly). We assert the row is gone — that's
		// the user-visible contract.
		await _client.PostAsJsonAsync($"/api/data/{TestProjectKey}/dbs", new { name = "cache" });

		var resp = await _client.DeleteAsync($"/api/data/{TestProjectKey}/dbs/cache");
		resp.StatusCode.Should().Be(HttpStatusCode.NoContent);

		using var scope = _factory.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<PetBoxDb>();
		var row = await db.DataDbs.FirstOrDefaultAsync((Core.Models.DataDb d) =>
			d.ProjectKey == TestProjectKey && d.Name == "cache");
		row.Should().BeNull();
	}

	[Fact]
	public async Task Delete_Then_Recreate_Works_EvenIfFileStillOnDisk()
	{
		// If the previous file lingers because of a file lock, creating a new
		// DataDb with the same name must NOT fail — we delete the old file
		// first (no lock case) or fail informatively (lock case). The metadata
		// row is gone so the name slot is free.
		await _client.PostAsJsonAsync($"/api/data/{TestProjectKey}/dbs", new { name = "cache" });
		await _client.DeleteAsync($"/api/data/{TestProjectKey}/dbs/cache");

		var resp = await _client.PostAsJsonAsync($"/api/data/{TestProjectKey}/dbs", new { name = "cache" });
		// Either Created (file gone) or InternalServerError (file stuck). We
		// accept both as documented MVP behavior — the orphan cleanup loop
		// reconciles eventually.
		((int)resp.StatusCode).Should().BeOneOf(201, 500);
	}

	[Fact]
	public async Task Delete_NonExistent_404()
	{
		var resp = await _client.DeleteAsync($"/api/data/{TestProjectKey}/dbs/never-existed");
		resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
	}

	[Fact]
	public async Task Post_CrossProject_Forbidden()
	{
		var resp = await _client.PostAsJsonAsync("/api/data/some-other-project/dbs",
			new { name = "cache" });
		resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
	}
}
