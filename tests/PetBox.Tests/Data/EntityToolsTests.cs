using System.Text.Json;
using LinqToDB;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Data;

namespace PetBox.Tests.Data;

// Shared per-class host for EntityToolsTests (xUnit news the test class per test, so
// without this fixture every test boots its own WebApplicationFactory). No per-test reset
// is needed: each test uses its own entity names (log "audit" is created+deleted within one
// test; dbs "appdb"/"listdb"/ "ghost" are each touched by exactly one test), config-binding
// paths are either Guid-unique or asserted with idempotent Contains/single-active checks.
public sealed class EntityToolsFixture : IAsyncLifetime
{
	public const string ProjectKey = "entproj";
	public const string ApiKey = "yb_key_entity_tools";

	readonly string _baseDir;
	HttpClient _http = null!;

	public WebApplicationFactory<Program> Factory { get; }
	public McpClient Mcp { get; private set; } = null!;

	public EntityToolsFixture()
	{
		_baseDir = Path.Combine(Path.GetTempPath(), "petbox-entity-test-" + Guid.NewGuid().ToString("N"));
		Environment.SetEnvironmentVariable("PETBOX_MASTER_KEY", "test-key-for-secrets");
		Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");

		Factory = new WebApplicationFactory<Program>()
			.WithWebHostBuilder(b =>
			{
				b.UseEnvironment("Testing");
				// Features:Logging/Data gate registrations at BUILD time (Program.cs, before
				// builder.Build()). UseSetting IS visible at that pre-Build read (measured:
				// Architecture/ConfigVisibilityContractTests) — a process-global env var is
				// unnecessary and was leaking into every other test in the process
				// (chore/tests-env-leak).
				b.UseSetting("Features:Logging", "true");
				b.UseSetting("Features:Data", "true");
				b.ConfigureAppConfiguration((_, cfg) =>
				{
					cfg.AddInMemoryCollection(new Dictionary<string, string?>
					{
						["ConnectionStrings:PetBox"] = TestSchema.NewTempConnectionString(),
						["Host:BackgroundServices"] = "false",
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
						cs => new LogDb(LogDb.CreateOptions(cs)), TestSchema.Log));

					var dataFactory = svc.SingleOrDefault(d => d.ServiceType == typeof(IDataDbFactory));
					if (dataFactory is not null) svc.Remove(dataFactory);
					svc.AddSingleton<IDataDbFactory>(_ => new DataDbFactory(Path.Combine(_baseDir, "data")));
				});
			});
	}

	public async ValueTask InitializeAsync()
	{
		var cs = Factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("PetBox")!;
		TestSchema.Core(cs);
		_http = Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

		using (var scope = Factory.Services.CreateScope())
		{
			using var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open();
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
		Mcp = await McpClient.CreateAsync(transport, cancellationToken: default);
	}

	public async ValueTask DisposeAsync()
	{
		await Mcp.DisposeAsync();
		_http.Dispose();
		await Factory.DisposeAsync();
		TestDirs.CleanupOrDefer(_baseDir);
	}
}

// Covers the per-type lifecycle MCP tools that replaced the generic entity.* surface
// (typed-surface Phase 4): log_create/list/delete, db_create/describe, and the
// config_* binding tools. Each tool now takes flat, typed params (no JsonElement),
// so a real MCP client gets a per-field input schema. Provisioning (project/apikey)
// lives in ProvisioningToolsTests; SQL round-trips in McpDataToolsTests.
public sealed class EntityToolsTests : IClassFixture<EntityToolsFixture>
{
	const string ProjectKey = EntityToolsFixture.ProjectKey;

	readonly WebApplicationFactory<Program> _factory;
	readonly McpClient _mcp;

	public EntityToolsTests(EntityToolsFixture fx)
	{
		_factory = fx.Factory;
		_mcp = fx.Mcp;
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
		names.Should().Contain("log_create");
		names.Should().Contain("log_list");
		names.Should().Contain("log_delete");
		names.Should().Contain("db_create");
		names.Should().Contain("db_list");
		names.Should().Contain("db_delete");
		names.Should().Contain("db_describe");
		names.Should().Contain("project_create");
		names.Should().Contain("apikey_create");
	}

	[Fact]
	public async Task Log_Create_List_Delete_RoundTrips()
	{
		var create = await ToolAsync("log_create");
		var r1 = await create.CallAsync(new Dictionary<string, object?>
		{
			["projectKey"] = ProjectKey,
			["logName"] = "audit",
			["description"] = "audit trail",
		});
		Text(r1).Should().NotContain("\"error\"");

		var list = await ToolAsync("log_list");
		var r2 = await list.CallAsync(new Dictionary<string, object?> { ["projectKey"] = ProjectKey });
		Text(r2).Should().Contain("audit");

		var del = await ToolAsync("log_delete");
		var r3 = await del.CallAsync(new Dictionary<string, object?> { ["projectKey"] = ProjectKey, ["logName"] = "audit" });
		Text(r3).Should().NotContain("\"error\"");

		// Deleting a missing log surfaces a structured error (GuardAsync), not an opaque failure.
		var r4 = await del.CallAsync(new Dictionary<string, object?> { ["projectKey"] = ProjectKey, ["logName"] = "nope" });
		Text(r4).Should().Contain("not found");
	}

	[Fact]
	public async Task Db_Create_Then_Describe()
	{
		var create = await ToolAsync("db_create");
		Text(await create.CallAsync(new Dictionary<string, object?>
		{
			["projectKey"] = ProjectKey,
			["dbName"] = "appdb",
		})).Should().NotContain("\"error\"");

		var apply = await ToolAsync("data_schema_apply");
		(await apply.CallAsync(new Dictionary<string, object?>
		{
			["projectKey"] = ProjectKey,
			["dbName"] = "appdb",
			["migrationName"] = "M001",
			["sql"] = "CREATE TABLE widgets (id INTEGER PRIMARY KEY, name TEXT NOT NULL)",
		})).IsError.Should().NotBe(true);

		var describe = await ToolAsync("db_describe");
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
		Text(await (await ToolAsync("db_create")).CallAsync(new Dictionary<string, object?>
		{
			["projectKey"] = ProjectKey,
			["dbName"] = "listdb",
		})).Should().NotContain("\"error\"");

		var listed = Text(await (await ToolAsync("db_list")).CallAsync(new Dictionary<string, object?> { ["projectKey"] = ProjectKey }));
		listed.Should().Contain("listdb");
	}

	[Fact]
	public async Task Db_Describe_MissingDb_SurfacesError()
	{
		var r = await (await ToolAsync("db_describe")).CallAsync(new Dictionary<string, object?>
		{
			["projectKey"] = ProjectKey,
			["dbName"] = "ghost",
		});
		Text(r).Should().Contain("not found");
	}

	// ── mcp-surface-naming-cleanup wave 2: db_*/log_*/data_schema_apply's bare `name` retired ──
	//
	// UnknownParameterFilterTests carries the general shape of this, but its fixture's host never
	// turns on Features:Data/Features:Logging, so db_*/log_*/data_schema_apply are not registered
	// there at all. This fixture (EntityToolsFixture) does enable both, so the six point checks live
	// here instead.
	//
	// WHAT IS PINNED (drop-retired-parameter-hints): the bare `name` is REFUSED, and the refusal
	// carries the accepted vocabulary — which necessarily includes the replacement, because the list
	// is read off the live input schema. The hand-written table that used to append a sentence naming
	// each old name's successor is gone, and with it the drift guard that existed only to keep that
	// table honest; the assertions below cover the same ground without a second contract surface,
	// because a tool that still took `name` would return a SUCCESS here, and a tool that had lost
	// `dbName` would not list it.

	[Fact]
	public async Task DbCreate_OldNameParameter_IsRejected_AndListsDbName()
	{
		var result = await (await ToolAsync("db_create")).CallAsync(new Dictionary<string, object?>
		{
			["projectKey"] = ProjectKey,
			["name"] = "shouldnotcreate",
		});
		result.IsError.Should().Be(true);
		ErrorText(result).Should().Contain("'name'").And.Contain("Accepted parameters").And.Contain("dbName");
	}

	[Fact]
	public async Task DbDelete_OldNameParameter_IsRejected_AndListsDbName()
	{
		var result = await (await ToolAsync("db_delete")).CallAsync(new Dictionary<string, object?>
		{
			["projectKey"] = ProjectKey,
			["name"] = "whatever",
		});
		result.IsError.Should().Be(true);
		ErrorText(result).Should().Contain("'name'").And.Contain("Accepted parameters").And.Contain("dbName");
	}

	[Fact]
	public async Task LogCreate_OldNameParameter_IsRejected_AndListsLogName()
	{
		var result = await (await ToolAsync("log_create")).CallAsync(new Dictionary<string, object?>
		{
			["projectKey"] = ProjectKey,
			["name"] = "shouldnotcreate",
		});
		result.IsError.Should().Be(true);
		ErrorText(result).Should().Contain("'name'").And.Contain("Accepted parameters").And.Contain("logName");
	}

	[Fact]
	public async Task LogUpdate_OldNameParameter_IsRejected_AndListsLogName()
	{
		var result = await (await ToolAsync("log_update")).CallAsync(new Dictionary<string, object?>
		{
			["projectKey"] = ProjectKey,
			["name"] = "whatever",
			["retentionDays"] = 7,
		});
		result.IsError.Should().Be(true);
		ErrorText(result).Should().Contain("'name'").And.Contain("Accepted parameters").And.Contain("logName");
	}

	[Fact]
	public async Task LogDelete_OldNameParameter_IsRejected_AndListsLogName()
	{
		var result = await (await ToolAsync("log_delete")).CallAsync(new Dictionary<string, object?>
		{
			["projectKey"] = ProjectKey,
			["name"] = "whatever",
		});
		result.IsError.Should().Be(true);
		ErrorText(result).Should().Contain("'name'").And.Contain("Accepted parameters").And.Contain("logName");
	}

	[Fact]
	public async Task DataSchemaApply_OldNameParameter_IsRejected_AndListsMigrationName()
	{
		var result = await (await ToolAsync("data_schema_apply")).CallAsync(new Dictionary<string, object?>
		{
			["projectKey"] = ProjectKey,
			["dbName"] = "whatever",
			["name"] = "M001",
			["sql"] = "SELECT 1",
		});
		result.IsError.Should().Be(true);
		ErrorText(result).Should().Contain("'name'").And.Contain("Accepted parameters").And.Contain("migrationName");
	}

	// ── work/mcp-surface-naming-cleanup wave 3b: data_schema_apply's Failed/Conflict stop riding
	// home as fields of a successful response and throw through the central error envelope
	// (McpErrorEnvelopeFilter) instead. AlreadyApplied is the one soft-success kind left untouched.

	[Fact]
	public async Task DataSchemaApply_Failed_ThrowsThroughErrorEnvelope()
	{
		Text(await (await ToolAsync("db_create")).CallAsync(new Dictionary<string, object?>
		{
			["projectKey"] = ProjectKey,
			["dbName"] = "schemafail",
		})).Should().NotContain("\"error\"");

		var apply = await ToolAsync("data_schema_apply");
		var result = await apply.CallAsync(new Dictionary<string, object?>
		{
			["projectKey"] = ProjectKey,
			["dbName"] = "schemafail",
			["migrationName"] = "M001",
			["sql"] = "THIS IS NOT VALID SQL AT ALL (((",
		});
		result.IsError.Should().Be(true, "a bad migration script must refuse via isError, not a kind:'Failed' field on a successful response");
		ErrorText(result).Should().Contain("M001");
	}

	[Fact]
	public async Task DataSchemaApply_Conflict_ThrowsThroughErrorEnvelope_AndNamesBothHashes()
	{
		Text(await (await ToolAsync("db_create")).CallAsync(new Dictionary<string, object?>
		{
			["projectKey"] = ProjectKey,
			["dbName"] = "schemaconflict",
		})).Should().NotContain("\"error\"");

		var apply = await ToolAsync("data_schema_apply");
		(await apply.CallAsync(new Dictionary<string, object?>
		{
			["projectKey"] = ProjectKey,
			["dbName"] = "schemaconflict",
			["migrationName"] = "M001",
			["sql"] = "CREATE TABLE a (id INTEGER)",
		})).IsError.Should().NotBe(true);

		// Same migrationName, different sql -> a 409-style conflict, refused via isError.
		var result = await apply.CallAsync(new Dictionary<string, object?>
		{
			["projectKey"] = ProjectKey,
			["dbName"] = "schemaconflict",
			["migrationName"] = "M001",
			["sql"] = "CREATE TABLE b (id INTEGER)",
		});
		result.IsError.Should().Be(true, "a same-name/different-sql re-apply must refuse via isError, not a kind:'Conflict' field on a successful response");
		var text = ErrorText(result);
		// Error text is a product surface: the conflict must name BOTH the hash on file and the
		// one this call provided, not just one side of the collision.
		text.Should().Contain("existingHash");
		text.Should().Contain("providedHash");
		text.Should().Contain("M001");
	}

	[Fact]
	public async Task DataSchemaApply_AlreadyApplied_StaysSoftSuccess()
	{
		Text(await (await ToolAsync("db_create")).CallAsync(new Dictionary<string, object?>
		{
			["projectKey"] = ProjectKey,
			["dbName"] = "schemaok",
		})).Should().NotContain("\"error\"");

		var apply = await ToolAsync("data_schema_apply");
		var args = new Dictionary<string, object?>
		{
			["projectKey"] = ProjectKey,
			["dbName"] = "schemaok",
			["migrationName"] = "M001",
			["sql"] = "CREATE TABLE c (id INTEGER)",
		};
		(await apply.CallAsync(args)).IsError.Should().NotBe(true);

		// Same name + same sql, again -> AlreadyApplied, still a soft success (not a refusal).
		var second = await apply.CallAsync(args);
		second.IsError.Should().NotBe(true);
		Text(second).Should().Contain("AlreadyApplied");
	}

	[Fact]
	public async Task ConfigTools_TypedBinding_RoundTrips_AndSecretEncrypted()
	{
		// The typed per-type tool (mcp-typing) — flat scalar params, so the client
		// sends a real schema, no stringified-object trap.
		var create = await ToolAsync("config_binding_upsert");
		Text(await create.CallAsync(new Dictionary<string, object?>
		{
			["workspaceKey"] = "test",
			["items"] = new[] { new { path = "svc/url", value = "https://x", tags = "ws:test" } },
		})).Should().NotContain("\"error\"");

		Text(await create.CallAsync(new Dictionary<string, object?>
		{
			["workspaceKey"] = "test",
			["items"] = new[] { new { path = "svc/key", value = "topsecret", tags = "ws:test", kind = "Secret" } },
		})).Should().NotContain("\"error\"");

		var list = await ToolAsync("config_binding_search");
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

	// spec explicit-write-semantics: config_binding_upsert is PUT by (path, tagset) — a repeat
	// upsert with the same path and the same normalized tag SET supersedes (soft-closes) the
	// old binding instead of leaving two active ambiguous twins; a different tagset at the
	// same path is a specificity variant and coexists.
	[Fact]
	public async Task ConfigTools_BindingUpsert_SupersedesSameTagset_KeepsDifferentTagset()
	{
		// Unique path per run: the workspace config DB outlives this fixture, so a fixed
		// path would collide with rows left by previous runs.
		var path = "dup/" + Guid.NewGuid().ToString("N")[..12];
		var create = await ToolAsync("config_binding_upsert");
		Text(await create.CallAsync(new Dictionary<string, object?>
		{
			["workspaceKey"] = "test",
			["items"] = new[] { new { path, value = "v1", tags = "ws:test,svc:a" } },
		})).Should().NotContain("\"error\"");

		// Same path + same tagset (different order/case/whitespace) -> supersedes, reported in the result.
		var second = Text(await create.CallAsync(new Dictionary<string, object?>
		{
			["workspaceKey"] = "test",
			["items"] = new[] { new { path, value = "v2", tags = " SVC:A , ws:test " } },
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
			["items"] = new[] { new { path, value = "v3", tags = "ws:test,svc:a,env:prod" } },
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
		// A7b: a tasks-only key should see tasks_* but not other modules' tools
		// (call-time scope still enforces; this only trims the listing).
		const string narrowKey = "yb_key_tasks_only";
		using (var scope = _factory.Services.CreateScope())
		{
			using var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open();
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
			names.Should().Contain(n => n.StartsWith("tasks_", StringComparison.Ordinal));
			names.Should().NotContain(n => n.StartsWith("memory_", StringComparison.Ordinal));
			names.Should().NotContain(n => n.StartsWith("data_", StringComparison.Ordinal));
			names.Should().NotContain(n => n.StartsWith("db_", StringComparison.Ordinal));
			names.Should().NotContain(n => n.StartsWith("log_", StringComparison.Ordinal));
			names.Should().NotContain(n => n.StartsWith("config_", StringComparison.Ordinal));
		}
		finally
		{
			await mcp.DisposeAsync();
			http.Dispose();
		}
	}

	static string Text(ModelContextProtocol.Protocol.CallToolResult r) =>
		r.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().First().Text;

	// Same reasoning as UnknownParameterFilterTests.Text: PetBoxJsonEncoder.Relaxed keeps the
	// apostrophes the unknown-parameter refusal quotes its names with ("Unrecognized: 'name'")
	// wire-escaped ('), so pinning that exact punctuation needs the decoded error.message, not
	// the raw envelope text.
	static string ErrorText(ModelContextProtocol.Protocol.CallToolResult r)
	{
		var raw = Text(r);
		try
		{
			using var doc = JsonDocument.Parse(raw);
			if (doc.RootElement.TryGetProperty("error", out var error)
				&& error.TryGetProperty("message", out var message)
				&& message.GetString() is { } text)
				return text;
		}
		catch (JsonException) { /* not an envelope */ }
		return raw;
	}
}
