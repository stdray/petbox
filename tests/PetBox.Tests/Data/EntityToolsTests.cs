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

// Covers the per-type lifecycle MCP tools that replaced the generic entity.* surface
// (typed-surface Phase 4): log.create/list/delete, db.create/describe, and the
// config.* binding tools. Each tool now takes flat, typed params (no JsonElement),
// so a real MCP client gets a per-field input schema. Provisioning (project/apikey)
// lives in ProvisioningToolsTests; SQL round-trips in McpDataToolsTests.
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
		TestSchema.Core(cs);
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
	public async Task PerTypeLifecycleTools_AreDiscoverable_GenericEntityToolsGone()
	{
		var names = (await _mcp.ListToolsAsync()).Select(t => t.Name).ToHashSet();
		// The generic dispatch family is gone — no aliases (no-legacy-redirects).
		names.Should().NotContain("entity.create");
		names.Should().NotContain("entity.list");
		names.Should().NotContain("entity.delete");
		names.Should().NotContain("entity.describe");
		// Typed per-type tools take its place.
		names.Should().Contain("log.create");
		names.Should().Contain("log.list");
		names.Should().Contain("log.delete");
		names.Should().Contain("db.create");
		names.Should().Contain("db.list");
		names.Should().Contain("db.delete");
		names.Should().Contain("db.describe");
		names.Should().Contain("project.create");
		names.Should().Contain("apikey.create");
	}

	[Fact]
	public async Task Log_Create_List_Delete_RoundTrips()
	{
		var create = await ToolAsync("log.create");
		var r1 = await create.CallAsync(new Dictionary<string, object?>
		{
			["projectKey"] = ProjectKey,
			["name"] = "audit",
			["description"] = "audit trail",
		});
		Text(r1).Should().NotContain("\"error\"");

		var list = await ToolAsync("log.list");
		var r2 = await list.CallAsync(new Dictionary<string, object?> { ["projectKey"] = ProjectKey });
		Text(r2).Should().Contain("audit");

		var del = await ToolAsync("log.delete");
		var r3 = await del.CallAsync(new Dictionary<string, object?> { ["projectKey"] = ProjectKey, ["name"] = "audit" });
		Text(r3).Should().NotContain("\"error\"");

		// Deleting a missing log surfaces a structured error (GuardAsync), not an opaque failure.
		var r4 = await del.CallAsync(new Dictionary<string, object?> { ["projectKey"] = ProjectKey, ["name"] = "nope" });
		Text(r4).Should().Contain("not found");
	}

	[Fact]
	public async Task Db_Create_Then_Describe()
	{
		var create = await ToolAsync("db.create");
		Text(await create.CallAsync(new Dictionary<string, object?>
		{
			["projectKey"] = ProjectKey,
			["name"] = "appdb",
		})).Should().NotContain("\"error\"");

		var apply = await ToolAsync("data.schema_apply");
		(await apply.CallAsync(new Dictionary<string, object?>
		{
			["projectKey"] = ProjectKey,
			["dbName"] = "appdb",
			["name"] = "M001",
			["sql"] = "CREATE TABLE widgets (id INTEGER PRIMARY KEY, name TEXT NOT NULL)",
		})).IsError.Should().NotBe(true);

		var describe = await ToolAsync("db.describe");
		var r = await describe.CallAsync(new Dictionary<string, object?>
		{
			["projectKey"] = ProjectKey,
			["dbName"] = "appdb",
		});
		var text = Text(r);
		text.Should().Contain("widgets");
		text.Should().Contain("name");
	}

	[Fact]
	public async Task Db_List_ReflectsCreate()
	{
		Text(await (await ToolAsync("db.create")).CallAsync(new Dictionary<string, object?>
		{
			["projectKey"] = ProjectKey,
			["name"] = "listdb",
		})).Should().NotContain("\"error\"");

		var listed = Text(await (await ToolAsync("db.list")).CallAsync(new Dictionary<string, object?> { ["projectKey"] = ProjectKey }));
		listed.Should().Contain("listdb");
	}

	[Fact]
	public async Task Db_Describe_MissingDb_SurfacesError()
	{
		var r = await (await ToolAsync("db.describe")).CallAsync(new Dictionary<string, object?>
		{
			["projectKey"] = ProjectKey,
			["dbName"] = "ghost",
		});
		Text(r).Should().Contain("not found");
	}

	[Fact]
	public async Task ConfigTools_TypedBinding_RoundTrips_AndSecretEncrypted()
	{
		// The typed per-type tool (mcp-typing) — flat scalar params, so the client
		// sends a real schema, no stringified-object trap.
		var create = await ToolAsync("config.binding_upsert");
		Text(await create.CallAsync(new Dictionary<string, object?>
		{
			["workspaceKey"] = "test",
			["path"] = "svc/url",
			["value"] = "https://x",
			["tags"] = "ws:test",
		})).Should().NotContain("\"error\"");

		Text(await create.CallAsync(new Dictionary<string, object?>
		{
			["workspaceKey"] = "test",
			["path"] = "svc/key",
			["value"] = "topsecret",
			["tags"] = "ws:test",
			["kind"] = "Secret",
		})).Should().NotContain("\"error\"");

		var list = await ToolAsync("config.binding_list");
		var listed = Text(await list.CallAsync(new Dictionary<string, object?> { ["workspaceKey"] = "test" }));
		listed.Should().Contain("svc/url");
		listed.Should().Contain("svc/key");
		listed.Should().Contain("Secret");
		listed.Should().NotContain("topsecret");

		// The secret is stored encrypted, not as plaintext in Value.
		using var scope = _factory.Services.CreateScope();
		var cf = scope.ServiceProvider.GetRequiredService<PetBox.Config.Data.IConfigDbFactory>();
		var cdb = cf.GetConfigDb("test");
		var secret = cdb.Bindings.First(b => b.Path == "svc/key" && !b.IsDeleted);
		secret.Value.Should().BeEmpty();
		secret.Ciphertext.Should().NotBeNullOrEmpty();
	}

	// spec explicit-write-semantics: config.binding_upsert is PUT by (path, tagset) — a repeat
	// upsert with the same path and the same normalized tag SET supersedes (soft-closes) the
	// old binding instead of leaving two active ambiguous twins; a different tagset at the
	// same path is a specificity variant and coexists.
	[Fact]
	public async Task ConfigTools_BindingUpsert_SupersedesSameTagset_KeepsDifferentTagset()
	{
		// Unique path per run: the workspace config DB outlives this fixture, so a fixed
		// path would collide with rows left by previous runs.
		var path = "dup/" + Guid.NewGuid().ToString("N")[..12];
		var create = await ToolAsync("config.binding_upsert");
		Text(await create.CallAsync(new Dictionary<string, object?>
		{
			["workspaceKey"] = "test",
			["path"] = path,
			["value"] = "v1",
			["tags"] = "ws:test,svc:a",
		})).Should().NotContain("\"error\"");

		// Same path + same tagset (different order/case/whitespace) -> supersedes, reported in the result.
		var second = Text(await create.CallAsync(new Dictionary<string, object?>
		{
			["workspaceKey"] = "test",
			["path"] = path,
			["value"] = "v2",
			["tags"] = " SVC:A , ws:test ",
		}));
		second.Should().NotContain("\"error\"");
		second.Should().Contain("superseded");

		using (var scope = _factory.Services.CreateScope())
		{
			var cf = scope.ServiceProvider.GetRequiredService<PetBox.Config.Data.IConfigDbFactory>();
			var cdb = cf.GetConfigDb("test");
			var active = cdb.Bindings.Where(b => b.Path == path && !b.IsDeleted).ToList();
			active.Should().ContainSingle("the twin must be soft-closed, not left as a silent duplicate");
			active[0].Value.Should().Be("v2");
			// History kept: the superseded row is soft-deleted, not erased.
			cdb.Bindings.Count(b => b.Path == path && b.IsDeleted).Should().Be(1);
		}

		// Different tagset at the same path is NOT superseded — both stay active.
		Text(await create.CallAsync(new Dictionary<string, object?>
		{
			["workspaceKey"] = "test",
			["path"] = path,
			["value"] = "v3",
			["tags"] = "ws:test,svc:a,env:prod",
		})).Should().NotContain("\"error\"");

		using (var scope = _factory.Services.CreateScope())
		{
			var cf = scope.ServiceProvider.GetRequiredService<PetBox.Config.Data.IConfigDbFactory>();
			var cdb = cf.GetConfigDb("test");
			cdb.Bindings.Count(b => b.Path == path && !b.IsDeleted).Should().Be(2);
		}
	}

	[Fact]
	public async Task ToolsList_FilteredByKeyScope()
	{
		// A7b: a tasks-only key should see tasks.* but not other modules' tools
		// (call-time scope still enforces; this only trims the listing).
		const string narrowKey = "yb_key_tasks_only";
		using (var scope = _factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<PetBoxDb>();
			await db.ApiKeys.Where(k => k.Key == narrowKey).DeleteAsync();
			await db.InsertAsync(new ApiKey
			{
				Key = narrowKey,
				ProjectKey = ProjectKey,
				Scopes = "tasks:read,tasks:write",
				CreatedAt = DateTime.UtcNow,
			});
		}

		var http = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
		http.DefaultRequestHeaders.Add("X-Api-Key", narrowKey);
		var transport = new HttpClientTransport(new HttpClientTransportOptions
		{
			Endpoint = new Uri(http.BaseAddress!, "/mcp"),
			AdditionalHeaders = new Dictionary<string, string> { ["X-Api-Key"] = narrowKey },
		}, http);
		var mcp = await McpClient.CreateAsync(transport, cancellationToken: default);
		try
		{
			var names = (await mcp.ListToolsAsync()).Select(t => t.Name).ToList();
			names.Should().Contain(n => n.StartsWith("tasks.", StringComparison.Ordinal));
			names.Should().NotContain(n => n.StartsWith("memory.", StringComparison.Ordinal));
			names.Should().NotContain(n => n.StartsWith("data.", StringComparison.Ordinal));
			names.Should().NotContain(n => n.StartsWith("db.", StringComparison.Ordinal));
			names.Should().NotContain(n => n.StartsWith("log.", StringComparison.Ordinal));
			names.Should().NotContain(n => n.StartsWith("config.", StringComparison.Ordinal));
		}
		finally
		{
			await mcp.DisposeAsync();
			http.Dispose();
		}
	}

	static string Text(ModelContextProtocol.Protocol.CallToolResult r) =>
		r.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().First().Text;
}
