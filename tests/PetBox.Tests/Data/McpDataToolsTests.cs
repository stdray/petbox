using System.Net.Http.Json;
using System.Text.Json;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Data;

namespace PetBox.Tests.Data;

// Shared per-class host for McpDataToolsTests (xUnit news the test class per test, so
// without this fixture every test boots its own WebApplicationFactory). No per-test reset
// is needed: each mutating test creates its own db name ("mcptest" / "tmp"), touched by
// exactly one test; the rest are read-only discovery/rejection checks.
public sealed class McpDataToolsFixture : IAsyncLifetime
{
	public const string TestProjectKey = "kpvotes";
	public const string TestApiKey = "yb_key_test_mcp_xyz";

	readonly string _baseDir;
	HttpClient _http = null!;

	public WebApplicationFactory<Program> Factory { get; }
	public McpClient Mcp { get; private set; } = null!;

	public McpDataToolsFixture()
	{
		_baseDir = Path.Combine(Path.GetTempPath(), "petbox-mcp-test-" + Guid.NewGuid().ToString("N"));
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
		// Force MigrationRunner.Run on the test DB up front — WebApplicationFactory + static
		// Configure(app) does not always trigger migrations for tests that only touch DI.
		var __testCs = Factory.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>().GetConnectionString("PetBox")!;
		TestSchema.Core(__testCs);
		_http = Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

		using (var scope = Factory.Services.CreateScope())
		{
			using var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open();
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

		Mcp = await McpClient.CreateAsync(transport, cancellationToken: default);
	}

	public async Task DisposeAsync()
	{
		await Mcp.DisposeAsync();
		_http.Dispose();
		await Factory.DisposeAsync();
		TestDirs.CleanupOrDefer(_baseDir);
	}
}

// Sanity tests for the MCP server: tools/list discovery + a couple of
// round-trips that exercise the most-trafficked Data tools (create_db,
// schema_apply, exec, query). Deep behavior is already covered by the REST
// QueryExecApiTests / SchemaApiTests — the MCP layer reuses the same code
// paths, so we just verify the surface works end-to-end through the protocol.
public sealed class McpDataToolsTests : IClassFixture<McpDataToolsFixture>
{
	const string TestProjectKey = McpDataToolsFixture.TestProjectKey;
	const string TestDbName = "mcptest";

	readonly McpClient _mcp;

	public McpDataToolsTests(McpDataToolsFixture fx)
	{
		_mcp = fx.Mcp;
	}

	[Fact]
	public async Task ListTools_Discovers_Data_Tools()
	{
		var tools = await _mcp.ListToolsAsync();
		var names = tools.Select(t => t.Name).ToHashSet();

		// DataDb lifecycle is typed per-type db.* tools (replaced the generic entity.* dispatch).
		names.Should().Contain("db_create");
		names.Should().Contain("db_list");
		names.Should().Contain("db_delete");
		names.Should().Contain("db_describe");
		names.Should().NotContain("entity.create");
		names.Should().NotContain("data.create_db");
		names.Should().NotContain("data.list_dbs");
		// Operational SQL/migration tools stay.
		names.Should().Contain("data_schema_apply");
		names.Should().Contain("data_query");
		names.Should().Contain("data_exec");
	}

	[Fact]
	public async Task Create_Migrate_Insert_Query_Roundtrip()
	{
		var tools = (await _mcp.ListToolsAsync()).ToDictionary(t => t.Name);

		var create = await tools["db_create"].CallAsync(new Dictionary<string, object?>
		{
			["projectKey"] = TestProjectKey,
			["name"] = TestDbName,
		});
		create.IsError.Should().NotBe(true);

		var migrate = await tools["data_schema_apply"].CallAsync(new Dictionary<string, object?>
		{
			["projectKey"] = TestProjectKey,
			["dbName"] = TestDbName,
			["name"] = "M001",
			["sql"] = "CREATE TABLE votes (id INTEGER PRIMARY KEY, film TEXT NOT NULL)",
		});
		migrate.IsError.Should().NotBe(true);

		var insert = await tools["data_exec"].CallAsync(new Dictionary<string, object?>
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

		var query = await tools["data_query"].CallAsync(new Dictionary<string, object?>
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

		var result = await tools["db_list"].CallAsync(new Dictionary<string, object?>
		{
			["projectKey"] = "some-other-project",
		});
		// db.* surfaces the project-scope rejection as a structured {error} (GuardAsync).
		result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().First().Text.Should().Contain("error");
	}

	[Fact]
	public async Task DeniedPragma_Blocked()
	{
		var tools = (await _mcp.ListToolsAsync()).ToDictionary(t => t.Name);
		await tools["db_create"].CallAsync(new Dictionary<string, object?>
		{
			["projectKey"] = TestProjectKey,
			["name"] = "tmp",
		});

		var result = await tools["data_exec"].CallAsync(new Dictionary<string, object?>
		{
			["projectKey"] = TestProjectKey,
			["dbName"] = "tmp",
			["sql"] = "PRAGMA writable_schema = 1",
		});
		// The PRAGMA denial surfaces via McpErrorEnvelopeFilter: the structured {error}
		// body on the text content, with IsError=true.
		result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().First().Text
			.Should().Contain("error");
	}
}
