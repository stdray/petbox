using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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

// Shared per-class host for QueryExecApiTests (xUnit news the test class per test, so
// without this fixture every test boots its own WebApplicationFactory). The "cache"
// DataDb + votes table are seeded once; per-test isolation comes from ResetAsync, which
// empties the votes table (tests insert rows with the constant id=1, and one test asserts
// the table starts EMPTY) and strips the timeout header one test adds to the shared client.
public sealed class QueryExecApiFixture : IAsyncLifetime
{
	public const string TestProjectKey = "kpvotes";
	public const string TestApiKey = "yb_key_test_query_xyz";
	public const string TestDbName = "cache";

	readonly string _baseDir;

	public WebApplicationFactory<Program> Factory { get; }
	public HttpClient Client { get; private set; } = null!;

	public QueryExecApiFixture()
	{
		_baseDir = Path.Combine(Path.GetTempPath(), "petbox-qe-test-" + Guid.NewGuid().ToString("N"));
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
		// Force MigrationRunner.Run on the test DB up front — WebApplicationFactory + static
		// Configure(app) does not always trigger migrations for tests that only touch DI.
		var __testCs = Factory.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>().GetConnectionString("PetBox")!;
		TestSchema.Core(__testCs);
		Client = Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

		using var scope = Factory.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<PetBoxDb>();
		await db.DataDbs.DeleteAsync();
		await db.ApiKeys.Where(k => k.Key == TestApiKey).DeleteAsync();
		await db.Projects.Where(p => p.Key == TestProjectKey).DeleteAsync();
		await db.Workspaces.Where(w => w.Key == "test").DeleteAsync();

		await db.InsertAsync(new Workspace { Key = "test", Name = "Test", CreatedAt = DateTime.UtcNow });
		await db.InsertAsync(new Project { Key = TestProjectKey, WorkspaceKey = "test", Name = "KpVotes" });
		await db.InsertAsync(new ApiKey { Key = TestApiKey, ProjectKey = TestProjectKey, Scopes = "data:read,data:write,data:schema", CreatedAt = DateTime.UtcNow });

		Client.DefaultRequestHeaders.Add("X-Api-Key", TestApiKey);

		// Create DataDb + schema for the query/exec scenarios.
		await Client.PostAsJsonAsync($"/api/data/{TestProjectKey}/dbs", new { name = TestDbName });
		await Client.PostAsJsonAsync($"/api/data/{TestProjectKey}/{TestDbName}/schema",
			new
			{
				name = "M001",
				sql = "CREATE TABLE votes (id INTEGER PRIMARY KEY, film TEXT NOT NULL, score REAL)",
			});
	}

	// Per-test reset under the shared host: empty the votes table (constant row ids across
	// tests; one test asserts SELECT * starts empty) and drop the per-request timeout header
	// Query_TimeoutHeader_AcceptedWithinLimit adds to the shared client.
	public async Task ResetAsync()
	{
		Client.DefaultRequestHeaders.Remove("X-PetBox-Timeout-Seconds");
		var resp = await Client.PostAsJsonAsync($"/api/data/{TestProjectKey}/{TestDbName}/exec",
			new { sql = "DELETE FROM votes" });
		resp.EnsureSuccessStatusCode();
	}

	public async Task DisposeAsync()
	{
		Client.Dispose();
		await Factory.DisposeAsync();
		TestDirs.CleanupOrDefer(_baseDir);
	}
}

public sealed class QueryExecApiTests : IClassFixture<QueryExecApiFixture>, IAsyncLifetime
{
	const string TestProjectKey = QueryExecApiFixture.TestProjectKey;
	const string TestDbName = QueryExecApiFixture.TestDbName;

	readonly QueryExecApiFixture _fx;
	readonly HttpClient _client;

	public QueryExecApiTests(QueryExecApiFixture fx)
	{
		_fx = fx;
		_client = fx.Client;
	}

	public Task InitializeAsync() => _fx.ResetAsync();

	public Task DisposeAsync() => Task.CompletedTask; // the fixture owns host teardown

	[Fact]
	public async Task Exec_Insert_Then_Query_Roundtrip()
	{
		var exec = await _client.PostAsJsonAsync($"/api/data/{TestProjectKey}/{TestDbName}/exec",
			new
			{
				sql = "INSERT INTO votes (id, film, score) VALUES (@id, @film, @score)",
				@params = new object[]
				{
					new { name = "@id", value = 1 },
					new { name = "@film", value = "Matrix" },
					new { name = "@score", value = 8.7 },
				},
			});
		exec.StatusCode.Should().Be(HttpStatusCode.OK);
		var execBody = await exec.Content.ReadFromJsonAsync<QueryExecApi.ExecResponse>();
		execBody!.Affected.Should().Be(1);

		var query = await _client.PostAsJsonAsync($"/api/data/{TestProjectKey}/{TestDbName}/query",
			new
			{
				sql = "SELECT id, film, score FROM votes WHERE id = @id",
				@params = new object[] { new { name = "@id", value = 1 } },
			});
		query.StatusCode.Should().Be(HttpStatusCode.OK);
		var rows = await query.Content.ReadFromJsonAsync<List<Dictionary<string, JsonElement>>>();
		rows.Should().NotBeNull();
		rows!.Should().ContainSingle();
		rows[0]["film"].GetString().Should().Be("Matrix");
		rows[0]["score"].GetDouble().Should().Be(8.7);
	}

	[Fact]
	public async Task Query_EmptyResult_ReturnsEmptyArray()
	{
		var resp = await _client.PostAsJsonAsync($"/api/data/{TestProjectKey}/{TestDbName}/query",
			new { sql = "SELECT * FROM votes" });
		resp.EnsureSuccessStatusCode();
		var rows = await resp.Content.ReadFromJsonAsync<List<Dictionary<string, JsonElement>>>();
		rows.Should().NotBeNull().And.BeEmpty();
	}

	[Fact]
	public async Task Query_NullValueInColumn_ReturnsJsonNull()
	{
		await _client.PostAsJsonAsync($"/api/data/{TestProjectKey}/{TestDbName}/exec",
			new
			{
				sql = "INSERT INTO votes (id, film, score) VALUES (1, 'X', NULL)",
			});

		var resp = await _client.PostAsJsonAsync($"/api/data/{TestProjectKey}/{TestDbName}/query",
			new { sql = "SELECT score FROM votes" });
		var rows = await resp.Content.ReadFromJsonAsync<List<Dictionary<string, JsonElement>>>();
		rows![0]["score"].ValueKind.Should().Be(JsonValueKind.Null);
	}

	[Fact]
	public async Task Exec_BadSql_Returns400_WithSqliteMessage()
	{
		var resp = await _client.PostAsJsonAsync($"/api/data/{TestProjectKey}/{TestDbName}/exec",
			new { sql = "INSERT INTO not_a_real_table VALUES (1)" });
		resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
	}

	[Fact]
	public async Task Exec_PRAGMA_writable_schema_Forbidden()
	{
		var resp = await _client.PostAsJsonAsync($"/api/data/{TestProjectKey}/{TestDbName}/exec",
			new { sql = "PRAGMA writable_schema = 1" });
		resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
		var body = await resp.Content.ReadAsStringAsync();
		body.Should().Contain("writable_schema");
	}

	[Fact]
	public async Task Exec_PRAGMA_safe_OK()
	{
		var resp = await _client.PostAsJsonAsync($"/api/data/{TestProjectKey}/{TestDbName}/exec",
			new { sql = "PRAGMA cache_size = 1000" });
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
	}

	[Fact]
	public async Task Query_NoSuchDb_Returns404()
	{
		var resp = await _client.PostAsJsonAsync($"/api/data/{TestProjectKey}/nope/query",
			new { sql = "SELECT 1" });
		resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
	}

	[Fact]
	public async Task Quota_Exceeded_Returns507()
	{
		// Fresh DataDb with a tiny quota (1024 pages × 4KB = 4 MB).
		// Insert a 5 MB blob via parameter binding → exceeds quota → SQLITE_FULL.
		const string tinyDb = "tiny";
		var create = await _client.PostAsJsonAsync($"/api/data/{TestProjectKey}/dbs",
			new { name = tinyDb, maxPageCount = 1024 });
		create.EnsureSuccessStatusCode();
		await _client.PostAsJsonAsync($"/api/data/{TestProjectKey}/{tinyDb}/schema",
			new { name = "M001", sql = "CREATE TABLE blobs (id INTEGER PRIMARY KEY, data BLOB)" });

		var hugePayload = new string('x', 5 * 1024 * 1024);
		var resp = await _client.PostAsJsonAsync($"/api/data/{TestProjectKey}/{tinyDb}/exec",
			new
			{
				sql = "INSERT INTO blobs (id, data) VALUES (@id, @data)",
				@params = new object[]
				{
					new { name = "@id", value = 1 },
					new { name = "@data", value = hugePayload },
				},
			});

		// Body is ~5 MB, well under /exec's 10 MB body limit → so this is a
		// genuine quota hit, not a 413.
		resp.StatusCode.Should().Be(HttpStatusCode.InsufficientStorage);
	}

	// (The per-endpoint body-size guard can't be exercised here: WebApplicationFactory's
	// in-memory transport doesn't set Content-Length, so CheckBodySize sees null and passes.
	// Kestrel's server-level limit covers it in production.)

	[Fact]
	public async Task Query_TimeoutHeader_AcceptedWithinLimit()
	{
		// Just verify the header is accepted (handler reads it without 400).
		_client.DefaultRequestHeaders.Remove("X-PetBox-Timeout-Seconds");
		_client.DefaultRequestHeaders.Add("X-PetBox-Timeout-Seconds", "60");
		var resp = await _client.PostAsJsonAsync($"/api/data/{TestProjectKey}/{TestDbName}/query",
			new { sql = "SELECT 1 AS one" });
		resp.EnsureSuccessStatusCode();
	}
}
