using LinqToDB;
using LinqToDB.Async;
using LinqToDB.Data;
using LinqToDB.DataProvider.SQLite;
using LinqToDB.Mapping;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Client;
using PetBox.Client.Data.Linq2Db;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Data;

namespace PetBox.Tests.Data;

// End-to-end for the linq2db integration: build a query with linq2db, let the SDK extract its
// SQL and run it through petbox, materialize the result. Proves the generated SQL is valid
// SQLite the server actually executes — the full client → wire → server → client path.
public sealed class Linq2DbDataE2ETests : IAsyncLifetime
{
	const string TestProjectKey = "l2dbe2e";
	const string TestApiKey = "yb_key_l2db_e2e_xyz";
	const string TestWorkspace = "l2dbe2e-ws";
	const string TestDbName = "store";

	readonly string _baseDir;
	readonly WebApplicationFactory<Program> _factory;
	PetBoxClient _sdk = null!;

	// Columns are PascalCase to match the property names so the materializer maps by name.
	[Table("votes")]
	sealed class Vote
	{
		[Column] public int Id { get; set; }
		[Column] public string Film { get; set; } = "";
		[Column] public double Score { get; set; }
	}

	public Linq2DbDataE2ETests()
	{
		_baseDir = Path.Combine(Path.GetTempPath(), "petbox-l2db-" + Guid.NewGuid().ToString("N"));
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
						["Host:BackgroundServices"] = "false",
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
		var handler = _factory.Server.CreateHandler();

		using (var scope = _factory.Services.CreateScope())
		{
			using var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open();
			await db.ApiKeys.Where(k => k.Key == TestApiKey).DeleteAsync();
			await db.Projects.Where(p => p.Key == TestProjectKey).DeleteAsync();
			await db.Workspaces.Where(w => w.Key == TestWorkspace).DeleteAsync();

			await db.InsertAsync(new Workspace { Key = TestWorkspace, Name = "L2db E2E", CreatedAt = DateTime.UtcNow });
			await db.InsertAsync(new Project { Key = TestProjectKey, WorkspaceKey = TestWorkspace, Name = "L2db E2E" });
			await db.InsertAsync(new ApiKey { Key = TestApiKey, ProjectKey = TestProjectKey, Scopes = "data:read,data:write,data:schema", CreatedAt = DateTime.UtcNow });
		}

		_sdk = new PetBoxClient(new PetBoxClientOptions { Endpoint = "http://localhost", ApiKey = TestApiKey, Handler = handler });

		await _sdk.Data.CreateDbAsync(TestProjectKey, TestDbName);
		await _sdk.Data.ApplySchemaAsync(TestProjectKey, TestDbName, "M001",
			"CREATE TABLE votes (Id INTEGER PRIMARY KEY, Film TEXT NOT NULL, Score REAL)");
		await _sdk.Data.ExecAsync(TestProjectKey, TestDbName,
			"INSERT INTO votes (Id, Film, Score) VALUES (@id, @film, @score)",
			[new PetBoxSqlParam("@id", 1), new PetBoxSqlParam("@film", "Matrix"), new PetBoxSqlParam("@score", 8.7)]);
		await _sdk.Data.ExecAsync(TestProjectKey, TestDbName,
			"INSERT INTO votes (Id, Film, Score) VALUES (@id, @film, @score)",
			[new PetBoxSqlParam("@id", 2), new PetBoxSqlParam("@film", "Gigli"), new PetBoxSqlParam("@score", 2.4)]);
	}

	public async Task DisposeAsync()
	{
		_sdk.Dispose();
		await _factory.DisposeAsync();
		TestDirs.CleanupOrDefer(_baseDir);
	}

	[Fact]
	public async Task Linq2DbQuery_ExecutesOnServer_AndMaterializes()
	{
		using var dc = new DataConnection(new DataOptions().UseSQLite("Data Source=:memory:", SQLiteProvider.Microsoft));
		var minScore = 8.0; // captured → parameterized
		var query = dc.GetTable<Vote>().Where(v => v.Score >= minScore).OrderBy(v => v.Id);

		var rows = await _sdk.Data.QueryAsync(TestProjectKey, TestDbName, query);

		// Only Matrix (8.7) passes the >= 8.0 filter; Gigli (2.4) is excluded by the generated SQL.
		rows.Should().ContainSingle();
		rows[0].Id.Should().Be(1);
		rows[0].Film.Should().Be("Matrix");
		rows[0].Score.Should().Be(8.7);
	}
}
