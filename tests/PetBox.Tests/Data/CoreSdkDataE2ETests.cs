using System.Net;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Client;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Data;

namespace PetBox.Tests.Data;

// Shared per-class host for CoreSdkDataE2ETests (xUnit news the test class per test, so
// without this fixture both tests boot their own WebApplicationFactory). No per-test reset
// is needed: only one test creates the "store" db, the other is a read-only not-found check.
public sealed class CoreSdkDataE2EFixture : IAsyncLifetime
{
	public const string TestProjectKey = "sdke2e";
	public const string TestApiKey = "yb_key_sdk_e2e_xyz";
	public const string TestWorkspace = "sdke2e-ws";

	readonly string _baseDir;

	public WebApplicationFactory<Program> Factory { get; }
	public PetBoxClient Sdk { get; private set; } = null!;

	public CoreSdkDataE2EFixture()
	{
		_baseDir = Path.Combine(Path.GetTempPath(), "petbox-sdke2e-" + Guid.NewGuid().ToString("N"));
		Environment.SetEnvironmentVariable("PETBOX_MASTER_KEY", "test-key-for-secrets");

		Factory = new WebApplicationFactory<Program>()
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

	public async Task InitializeAsync()
	{
		var cs = Factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("PetBox")!;
		TestSchema.Core(cs);

		// Handler routed to the in-process server — the SDK builds its own HttpClient around it.
		var handler = Factory.Server.CreateHandler();

		using (var scope = Factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<PetBoxDb>();
			await db.ApiKeys.Where(k => k.Key == TestApiKey).DeleteAsync();
			await db.Projects.Where(p => p.Key == TestProjectKey).DeleteAsync();
			await db.Workspaces.Where(w => w.Key == TestWorkspace).DeleteAsync();

			await db.InsertAsync(new Workspace { Key = TestWorkspace, Name = "SDK E2E", CreatedAt = DateTime.UtcNow });
			await db.InsertAsync(new Project { Key = TestProjectKey, WorkspaceKey = TestWorkspace, Name = "SDK E2E" });
			await db.InsertAsync(new ApiKey { Key = TestApiKey, ProjectKey = TestProjectKey, Scopes = "data:read,data:write,data:schema", CreatedAt = DateTime.UtcNow });
		}

		Sdk = new PetBoxClient(new PetBoxClientOptions
		{
			Endpoint = "http://localhost",
			ApiKey = TestApiKey,
			Handler = handler,
		});
	}

	public async Task DisposeAsync()
	{
		Sdk.Dispose();
		await Factory.DisposeAsync();
		TestDirs.CleanupOrDefer(_baseDir);
	}
}

// End-to-end: drive the real PetBox.Client SDK (core transport + Data client) against an
// in-process petbox via WebApplicationFactory's handler. Proves the full round-trip
// create-db → schema → exec → query through the published client, not just raw HTTP.
public sealed class CoreSdkDataE2ETests : IClassFixture<CoreSdkDataE2EFixture>
{
	const string TestProjectKey = CoreSdkDataE2EFixture.TestProjectKey;
	const string TestDbName = "store";

	readonly PetBoxClient _sdk;

	public CoreSdkDataE2ETests(CoreSdkDataE2EFixture fx)
	{
		_sdk = fx.Sdk;
	}

	[Fact]
	public async Task Provision_Exec_Query_Roundtrip_ViaSdk()
	{
		await _sdk.Data.CreateDbAsync(TestProjectKey, TestDbName, description: "sdk e2e store");
		await _sdk.Data.ApplySchemaAsync(TestProjectKey, TestDbName, "M001",
			"CREATE TABLE votes (id INTEGER PRIMARY KEY, film TEXT NOT NULL, score REAL)");

		var affected = await _sdk.Data.ExecAsync(TestProjectKey, TestDbName,
			"INSERT INTO votes (id, film, score) VALUES (@id, @film, @score)",
			[new PetBoxSqlParam("@id", 1), new PetBoxSqlParam("@film", "Matrix"), new PetBoxSqlParam("@score", 8.7)]);
		affected.Should().Be(1);

		var rows = await _sdk.Data.QueryAsync(TestProjectKey, TestDbName,
			"SELECT id, film, score FROM votes WHERE id = @id", [new PetBoxSqlParam("@id", 1)]);

		rows.Should().ContainSingle();
		rows[0]["id"].Should().Be(1L);
		rows[0]["film"].Should().Be("Matrix");
		rows[0]["score"].Should().Be(8.7);
	}

	[Fact]
	public async Task Query_NoSuchDb_ThrowsNotFound()
	{
		var act = async () => await _sdk.Data.QueryAsync(TestProjectKey, "missing", "SELECT 1");
		var ex = (await act.Should().ThrowAsync<PetBoxClientException>()).Which;
		ex.StatusCode.Should().Be(HttpStatusCode.NotFound);
	}
}
