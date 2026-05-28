using System.Net;
using System.Net.Http.Json;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Data;

namespace PetBox.Tests.Data;

[Collection("DataModule")]
public sealed class SchemaApiTests : IAsyncLifetime
{
	const string TestProjectKey = "kpvotes";
	const string TestApiKey = "yb_key_test_schema_xyz";
	const string TestDbName = "cache";

	readonly string _baseDir;
	readonly WebApplicationFactory<Program> _factory;
	HttpClient _client = null!;

	public SchemaApiTests()
	{
		_baseDir = Path.Combine(Path.GetTempPath(), "petbox-schemaapi-test-" + Guid.NewGuid().ToString("N"));
		Environment.SetEnvironmentVariable("PETBOX_MASTER_KEY", "test-key-for-secrets");

		_factory = new WebApplicationFactory<Program>()
			.WithWebHostBuilder(b =>
			{
				b.UseEnvironment("Testing");
				b.ConfigureAppConfiguration((_, cfg) =>
				{
					cfg.AddInMemoryCollection(new Dictionary<string, string?>
					{
						["ConnectionStrings:PetBox"] = "Data Source=:memory:;Cache=Shared",
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
		_client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

		using var scope = _factory.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<PetBoxDb>();

		await db.DataDbs.DeleteAsync();
		await db.ApiKeys.Where(k => k.Key == TestApiKey).DeleteAsync();
		await db.Projects.Where(p => p.Key == TestProjectKey).DeleteAsync();
		await db.Workspaces.Where(w => w.Key == "test").DeleteAsync();

		await db.InsertAsync(new Workspace { Key = "test", Name = "Test", CreatedAt = DateTime.UtcNow });
		await db.InsertAsync(new Project { Key = TestProjectKey, WorkspaceKey = "test", Name = "KpVotes" });
		await db.InsertAsync(new ApiKey { Key = TestApiKey, ProjectKey = TestProjectKey, Scopes = "data:read,data:schema", CreatedAt = DateTime.UtcNow });

		_client.DefaultRequestHeaders.Add("X-Api-Key", TestApiKey);

		// Create a target DataDb for the schema tests.
		var resp = await _client.PostAsJsonAsync($"/api/data/{TestProjectKey}/dbs", new { name = TestDbName });
		resp.EnsureSuccessStatusCode();
	}

	public async Task DisposeAsync()
	{
		_client.Dispose();
		await _factory.DisposeAsync();
		SqliteConnection.ClearAllPools();
		if (Directory.Exists(_baseDir)) Directory.Delete(_baseDir, recursive: true);
	}

	[Fact]
	public async Task Apply_NewScript_Returns200_Applied()
	{
		var resp = await _client.PostAsJsonAsync(
			$"/api/data/{TestProjectKey}/{TestDbName}/schema",
			new { name = "M001_create_votes", sql = "CREATE TABLE votes (id INTEGER PRIMARY KEY)" });

		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var body = await resp.Content.ReadFromJsonAsync<SchemaApi.SchemaApplyResponse>();
		body!.Kind.Should().Be("Applied");
		body.Hash.Should().NotBeNullOrEmpty();
		body.ExistingHash.Should().BeNull();
	}

	[Fact]
	public async Task Apply_SameScript_Returns200_AlreadyApplied()
	{
		await _client.PostAsJsonAsync($"/api/data/{TestProjectKey}/{TestDbName}/schema",
			new { name = "M001", sql = "CREATE TABLE x (id INTEGER)" });

		var resp = await _client.PostAsJsonAsync($"/api/data/{TestProjectKey}/{TestDbName}/schema",
			new { name = "M001", sql = "CREATE TABLE x (id INTEGER)" });

		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var body = await resp.Content.ReadFromJsonAsync<SchemaApi.SchemaApplyResponse>();
		body!.Kind.Should().Be("AlreadyApplied");
	}

	[Fact]
	public async Task Apply_SameNameDifferentSql_Returns409_Conflict()
	{
		await _client.PostAsJsonAsync($"/api/data/{TestProjectKey}/{TestDbName}/schema",
			new { name = "M001", sql = "CREATE TABLE x (id INTEGER)" });

		var resp = await _client.PostAsJsonAsync($"/api/data/{TestProjectKey}/{TestDbName}/schema",
			new { name = "M001", sql = "CREATE TABLE x (id TEXT)" });

		resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
		var body = await resp.Content.ReadFromJsonAsync<SchemaApi.SchemaApplyResponse>();
		body!.Kind.Should().Be("Conflict");
		body.ExistingHash.Should().NotBeNullOrEmpty();
		body.Hash.Should().NotBe(body.ExistingHash);
	}

	[Fact]
	public async Task Apply_BogusSql_Returns400()
	{
		var resp = await _client.PostAsJsonAsync($"/api/data/{TestProjectKey}/{TestDbName}/schema",
			new { name = "M001", sql = "BOGUS NOT REAL SQL" });

		resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
	}

	[Fact]
	public async Task Apply_NoSuchDb_Returns404()
	{
		var resp = await _client.PostAsJsonAsync(
			$"/api/data/{TestProjectKey}/nope/schema",
			new { name = "M001", sql = "CREATE TABLE x (id INTEGER)" });

		resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
	}

	[Fact]
	public async Task ListMigrations_Empty_WhenNoSchemaApplied()
	{
		var resp = await _client.GetAsync($"/api/data/{TestProjectKey}/{TestDbName}/migrations");
		resp.EnsureSuccessStatusCode();
		var rows = await resp.Content.ReadFromJsonAsync<List<SchemaApi.MigrationEntry>>();
		rows.Should().NotBeNull().And.BeEmpty();
	}

	[Fact]
	public async Task ListMigrations_ReturnsAppliedScripts_InOrder()
	{
		await _client.PostAsJsonAsync($"/api/data/{TestProjectKey}/{TestDbName}/schema",
			new { name = "M001", sql = "CREATE TABLE a (id INTEGER)" });
		await _client.PostAsJsonAsync($"/api/data/{TestProjectKey}/{TestDbName}/schema",
			new { name = "M002", sql = "CREATE TABLE b (id INTEGER)" });

		var resp = await _client.GetAsync($"/api/data/{TestProjectKey}/{TestDbName}/migrations");
		var rows = await resp.Content.ReadFromJsonAsync<List<SchemaApi.MigrationEntry>>();

		rows.Should().NotBeNull();
		rows!.Select(r => r.ScriptName).Should().Equal("M001", "M002");
		rows.All(r => !string.IsNullOrEmpty(r.Hash)).Should().BeTrue();
	}

	[Fact]
	public async Task Apply_CrossProject_Forbidden()
	{
		var resp = await _client.PostAsJsonAsync(
			$"/api/data/other-project/{TestDbName}/schema",
			new { name = "M001", sql = "CREATE TABLE x (id INTEGER)" });
		resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
	}
}
