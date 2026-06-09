using System.Net;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Client;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Data;

namespace PetBox.Tests.Data;

// End-to-end: drive the real PetBox.Client SDK (core transport + Data client) against an
// in-process petbox via WebApplicationFactory's handler. Proves the full round-trip
// create-db → schema → exec → query through the published client, not just raw HTTP.
[Collection("DataModule")]
public sealed class CoreSdkDataE2ETests : IAsyncLifetime
{
	const string TestProjectKey = "sdke2e";
	const string TestApiKey = "yb_key_sdk_e2e_xyz";
	const string TestWorkspace = "sdke2e-ws";
	const string TestDbName = "store";

	readonly string _baseDir;
	readonly WebApplicationFactory<Program> _factory;
	PetBoxClient _sdk = null!;

	public CoreSdkDataE2ETests()
	{
		_baseDir = Path.Combine(Path.GetTempPath(), "petbox-sdke2e-" + Guid.NewGuid().ToString("N"));
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

	public async Task InitializeAsync()
	{
		var cs = _factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("PetBox")!;
		TestSchema.Core(cs);

		// Handler routed to the in-process server — the SDK builds its own HttpClient around it.
		var handler = _factory.Server.CreateHandler();

		using (var scope = _factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<PetBoxDb>();
			await db.ApiKeys.Where(k => k.Key == TestApiKey).DeleteAsync();
			await db.Projects.Where(p => p.Key == TestProjectKey).DeleteAsync();
			await db.Workspaces.Where(w => w.Key == TestWorkspace).DeleteAsync();

			await db.InsertAsync(new Workspace { Key = TestWorkspace, Name = "SDK E2E", CreatedAt = DateTime.UtcNow });
			await db.InsertAsync(new Project { Key = TestProjectKey, WorkspaceKey = TestWorkspace, Name = "SDK E2E" });
			await db.InsertAsync(new ApiKey { Key = TestApiKey, ProjectKey = TestProjectKey, Scopes = "data:read,data:write,data:schema", CreatedAt = DateTime.UtcNow });
		}

		_sdk = new PetBoxClient(new PetBoxClientOptions
		{
			Endpoint = "http://localhost",
			ApiKey = TestApiKey,
			Handler = handler,
		});
	}

	public async Task DisposeAsync()
	{
		_sdk.Dispose();
		await _factory.DisposeAsync();
		SqliteConnection.ClearAllPools();
		if (Directory.Exists(_baseDir)) Directory.Delete(_baseDir, recursive: true);
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
