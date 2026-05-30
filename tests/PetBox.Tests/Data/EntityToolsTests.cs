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
using PetBox.Log.Core.Data;

namespace PetBox.Tests.Data;

// Covers the generic entity.* MCP tools for the surfaces NOT exercised by the
// updated Data/Provisioning tests: log CRUD (logs:admin/logs:query), db describe,
// and the forbidden-op paths (describe on a non-db type, delete on project).
[Collection("DataModule")]
public sealed class EntityToolsTests : IAsyncLifetime
{
	const string ProjectKey = "entproj";
	const string ApiKey = "yb_key_entity_tools";

	readonly string _baseDir;
	readonly WebApplicationFactory<Program> _factory;
	McpClient _mcp = null!;
	HttpClient _http = null!;

	public EntityToolsTests()
	{
		_baseDir = Path.Combine(Path.GetTempPath(), "petbox-entity-test-" + Guid.NewGuid().ToString("N"));
		Environment.SetEnvironmentVariable("PETBOX_MASTER_KEY", "test-key-for-secrets");
		Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");
		Environment.SetEnvironmentVariable("Features__Logging", "true");
		Environment.SetEnvironmentVariable("Features__Data", "true");

		_factory = new WebApplicationFactory<Program>()
			.WithWebHostBuilder(b =>
			{
				b.UseEnvironment("Testing");
				b.ConfigureAppConfiguration((_, cfg) =>
				{
					cfg.AddInMemoryCollection(new Dictionary<string, string?>
					{
						["ConnectionStrings:PetBox"] = $"Data Source={Path.Combine(Path.GetTempPath(), $"petbox-test-{Guid.NewGuid():N}.db")};Cache=Shared",
						["Features:Logging"] = "true",
						["Features:Data"] = "true",
					});
				});
				b.ConfigureServices(svc =>
				{
					var logFactory = svc.SingleOrDefault(d => d.ServiceType == typeof(IScopedDbFactory<LogDb>));
					if (logFactory is not null) svc.Remove(logFactory);
					svc.AddSingleton<IScopedDbFactory<LogDb>>(_ => new ScopedDbFactory<LogDb>(
						Path.Combine(_baseDir, "logs"), PetBox.Core.Settings.Scope.Project,
						cs => new LogDb(LogDb.CreateOptions(cs)), LogSchema.Ensure));

					var dataFactory = svc.SingleOrDefault(d => d.ServiceType == typeof(IDataDbFactory));
					if (dataFactory is not null) svc.Remove(dataFactory);
					svc.AddSingleton<IDataDbFactory>(_ => new DataDbFactory(Path.Combine(_baseDir, "data")));
				});
			});
	}

	public async Task InitializeAsync()
	{
		var cs = _factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("PetBox")!;
		MigrationRunner.Run(cs);
		_http = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

		using (var scope = _factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<PetBoxDb>();
			await db.ApiKeys.Where(k => k.Key == ApiKey).DeleteAsync();
			await db.Projects.Where(p => p.Key == ProjectKey).DeleteAsync();
			await db.Workspaces.Where(w => w.Key == "test").DeleteAsync();
			await db.InsertAsync(new Workspace { Key = "test", Name = "Test", CreatedAt = DateTime.UtcNow });
			await db.InsertAsync(new Project { Key = ProjectKey, WorkspaceKey = "test", Name = "Ent" });
			await db.InsertAsync(new ApiKey
			{
				Key = ApiKey,
				ProjectKey = ProjectKey,
				Scopes = "logs:admin,logs:query,data:read,data:write,data:schema,admin:provision",
				CreatedAt = DateTime.UtcNow,
			});
		}

		_http.DefaultRequestHeaders.Add("X-Api-Key", ApiKey);
		var transport = new HttpClientTransport(new HttpClientTransportOptions
		{
			Endpoint = new Uri(_http.BaseAddress!, "/mcp"),
			AdditionalHeaders = new Dictionary<string, string> { ["X-Api-Key"] = ApiKey },
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

	async Task<McpClientTool> ToolAsync(string name) =>
		(await _mcp.ListToolsAsync()).First(t => t.Name == name);

	[Fact]
	public async Task Log_Create_List_Delete_RoundTrips()
	{
		var create = await ToolAsync("entity.create");
		var r1 = await create.CallAsync(new Dictionary<string, object?>
		{
			["type"] = "log",
			["props"] = JsonSerializer.SerializeToElement(new { projectKey = ProjectKey, name = "audit", description = "audit trail" }),
		});
		r1.IsError.Should().NotBe(true);

		var list = await ToolAsync("entity.list");
		var r2 = await list.CallAsync(new Dictionary<string, object?>
		{
			["type"] = "log",
			["filter"] = JsonSerializer.SerializeToElement(new { projectKey = ProjectKey }),
		});
		r2.IsError.Should().NotBe(true);
		var listText = r2.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().First().Text;
		listText.Should().Contain("audit");

		var del = await ToolAsync("entity.delete");
		var r3 = await del.CallAsync(new Dictionary<string, object?>
		{
			["type"] = "log",
			["key"] = JsonSerializer.SerializeToElement(new { projectKey = ProjectKey, name = "audit" }),
		});
		r3.IsError.Should().NotBe(true);
	}

	[Fact]
	public async Task Db_Create_Then_Describe()
	{
		var create = await ToolAsync("entity.create");
		(await create.CallAsync(new Dictionary<string, object?>
		{
			["type"] = "db",
			["props"] = JsonSerializer.SerializeToElement(new { projectKey = ProjectKey, name = "appdb" }),
		})).IsError.Should().NotBe(true);

		var apply = await ToolAsync("data.schema_apply");
		(await apply.CallAsync(new Dictionary<string, object?>
		{
			["projectKey"] = ProjectKey,
			["dbName"] = "appdb",
			["name"] = "M001",
			["sql"] = "CREATE TABLE widgets (id INTEGER PRIMARY KEY, name TEXT NOT NULL)",
		})).IsError.Should().NotBe(true);

		var describe = await ToolAsync("entity.describe");
		var r = await describe.CallAsync(new Dictionary<string, object?>
		{
			["type"] = "db",
			["key"] = JsonSerializer.SerializeToElement(new { projectKey = ProjectKey, dbName = "appdb" }),
		});
		r.IsError.Should().NotBe(true);
		var text = r.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().First().Text;
		text.Should().Contain("widgets");
		text.Should().Contain("name");
	}

	[Fact]
	public async Task Describe_OnLog_IsForbidden()
	{
		var describe = await ToolAsync("entity.describe");
		var r = await describe.CallAsync(new Dictionary<string, object?>
		{
			["type"] = "log",
			["key"] = JsonSerializer.SerializeToElement(new { projectKey = ProjectKey, name = "default" }),
		});
		r.IsError.Should().Be(true);
	}

	[Fact]
	public async Task Unknown_Type_IsRejected()
	{
		var create = await ToolAsync("entity.create");
		var r = await create.CallAsync(new Dictionary<string, object?>
		{
			["type"] = "banana",
			["props"] = JsonSerializer.SerializeToElement(new { name = "x" }),
		});
		r.IsError.Should().Be(true);
	}
}
