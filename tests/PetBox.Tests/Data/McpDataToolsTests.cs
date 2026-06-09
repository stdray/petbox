using System.Net.Http.Json;
using System.Text.Json;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Data;

namespace PetBox.Tests.Data;

// Sanity tests for the MCP server: tools/list discovery + a couple of
// round-trips that exercise the most-trafficked Data tools (create_db,
// schema_apply, exec, query). Deep behavior is already covered by the REST
// QueryExecApiTests / SchemaApiTests — the MCP layer reuses the same code
// paths, so we just verify the surface works end-to-end through the protocol.
[Collection("DataModule")]
public sealed class McpDataToolsTests : IAsyncLifetime
{
	const string TestProjectKey = "kpvotes";
	const string TestApiKey = "yb_key_test_mcp_xyz";
	const string TestDbName = "mcptest";

	readonly string _baseDir;
	readonly WebApplicationFactory<Program> _factory;
	McpClient _mcp = null!;
	HttpClient _http = null!;

	public McpDataToolsTests()
	{
		_baseDir = Path.Combine(Path.GetTempPath(), "petbox-mcp-test-" + Guid.NewGuid().ToString("N"));
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
		// Force MigrationRunner.Run on the test DB up front — WebApplicationFactory + static
		// Configure(app) does not always trigger migrations for tests that only touch DI.
		var __testCs = _factory.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>().GetConnectionString("PetBox")!;
		TestSchema.Core(__testCs);
		_http = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

		using (var scope = _factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<PetBoxDb>();
			await db.DataDbs.DeleteAsync();
			await db.ApiKeys.Where(k => k.Key == TestApiKey).DeleteAsync();
			await db.Projects.Where(p => p.Key == TestProjectKey).DeleteAsync();
			await db.Workspaces.Where(w => w.Key == "test").DeleteAsync();

			await db.InsertAsync(new Workspace { Key = "test", Name = "Test", CreatedAt = DateTime.UtcNow });
			await db.InsertAsync(new Project { Key = TestProjectKey, WorkspaceKey = "test", Name = "KpVotes" });
			await db.InsertAsync(new ApiKey
			{
				Key = TestApiKey,
				ProjectKey = TestProjectKey,
				Scopes = "data:read,data:write,data:schema",
				CreatedAt = DateTime.UtcNow,
			});
		}

		_http.DefaultRequestHeaders.Add("X-Api-Key", TestApiKey);

		var transport = new HttpClientTransport(new HttpClientTransportOptions
		{
			Endpoint = new Uri(_http.BaseAddress!, "/mcp"),
			AdditionalHeaders = new Dictionary<string, string> { ["X-Api-Key"] = TestApiKey },
		}, _http);

		_mcp = await McpClient.CreateAsync(transport, cancellationToken: default);
	}

	public async Task DisposeAsync()
	{
		await _mcp.DisposeAsync();
		_http.Dispose();
		await _factory.DisposeAsync();
		SqliteConnection.ClearAllPools();
		if (Directory.Exists(_baseDir)) Directory.Delete(_baseDir, recursive: true);
	}

	[Fact]
	public async Task ListTools_Discovers_Data_Tools()
	{
		var tools = await _mcp.ListToolsAsync();
		var names = tools.Select(t => t.Name).ToHashSet();

		// DataDb lifecycle moved to the generic entity.* tools (type "db").
		names.Should().Contain("entity.create");
		names.Should().Contain("entity.list");
		names.Should().Contain("entity.delete");
		names.Should().Contain("entity.describe");
		names.Should().NotContain("data.create_db");
		names.Should().NotContain("data.list_dbs");
		// Operational SQL/migration tools stay.
		names.Should().Contain("data.schema_apply");
		names.Should().Contain("data.query");
		names.Should().Contain("data.exec");
	}

	[Fact]
	public async Task Create_Migrate_Insert_Query_Roundtrip()
	{
		var tools = (await _mcp.ListToolsAsync()).ToDictionary(t => t.Name);

		var create = await tools["entity.create"].CallAsync(new Dictionary<string, object?>
		{
			["type"] = "db",
			["props"] = JsonSerializer.SerializeToElement(new { projectKey = TestProjectKey, name = TestDbName }),
		});
		create.IsError.Should().NotBe(true);

		var migrate = await tools["data.schema_apply"].CallAsync(new Dictionary<string, object?>
		{
			["projectKey"] = TestProjectKey,
			["dbName"] = TestDbName,
			["name"] = "M001",
			["sql"] = "CREATE TABLE votes (id INTEGER PRIMARY KEY, film TEXT NOT NULL)",
		});
		migrate.IsError.Should().NotBe(true);

		var insert = await tools["data.exec"].CallAsync(new Dictionary<string, object?>
		{
			["projectKey"] = TestProjectKey,
			["dbName"] = TestDbName,
			["sql"] = "INSERT INTO votes (id, film) VALUES (@id, @film)",
			["params"] = JsonSerializer.SerializeToElement(new[]
			{
				new { name = "@id", value = (object)1 },
				new { name = "@film", value = (object)"Matrix" },
			}),
		});
		insert.IsError.Should().NotBe(true);

		var query = await tools["data.query"].CallAsync(new Dictionary<string, object?>
		{
			["projectKey"] = TestProjectKey,
			["dbName"] = TestDbName,
			["sql"] = "SELECT id, film FROM votes",
		});
		query.IsError.Should().NotBe(true);
		var firstText = query.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().FirstOrDefault();
		firstText.Should().NotBeNull();
		firstText!.Text.Should().Contain("Matrix");
	}

	[Fact]
	public async Task CrossProject_Access_Rejected()
	{
		var tools = (await _mcp.ListToolsAsync()).ToDictionary(t => t.Name);

		var result = await tools["entity.list"].CallAsync(new Dictionary<string, object?>
		{
			["type"] = "db",
			["filter"] = JsonSerializer.SerializeToElement(new { projectKey = "some-other-project" }),
		});
		// entity.* now surfaces the project-scope rejection as a structured {error} (GuardAsync).
		result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().First().Text.Should().Contain("error");
	}

	[Fact]
	public async Task DeniedPragma_Blocked()
	{
		var tools = (await _mcp.ListToolsAsync()).ToDictionary(t => t.Name);
		await tools["entity.create"].CallAsync(new Dictionary<string, object?>
		{
			["type"] = "db",
			["props"] = JsonSerializer.SerializeToElement(new { projectKey = TestProjectKey, name = "tmp" }),
		});

		var result = await tools["data.exec"].CallAsync(new Dictionary<string, object?>
		{
			["projectKey"] = TestProjectKey,
			["dbName"] = "tmp",
			["sql"] = "PRAGMA writable_schema = 1",
		});
		result.IsError.Should().Be(true);
	}
}
