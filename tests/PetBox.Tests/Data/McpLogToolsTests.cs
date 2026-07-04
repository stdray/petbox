using LinqToDB;
using LinqToDB.Async;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Log.Core.Data;
using PetBox.Log.Core.Ingestion;

namespace PetBox.Tests.Data;

// Shared per-class host for McpLogToolsTests (xUnit news the test class per test, so
// without this fixture every test boots its own WebApplicationFactory). The two log rows
// are seeded once and never mutated; the only shared-state mutation across tests is
// LogQuery_MissingScope_Rejected narrowing the ApiKey's scopes in the core db, which
// ResetAsync restores before every test.
public sealed class McpLogToolsFixture : IAsyncLifetime
{
	public const string TestProjectKey = "kpvotes";
	public const string TestApiKey = "yb_key_test_mcp_log_xyz";
	public const string TestServiceKey = "kpvotes-net";
	public const string TestScopes = "logs:query,logs:ingest";

	readonly string _baseDir;
	HttpClient _http = null!;

	public WebApplicationFactory<Program> Factory { get; }
	public McpClient Mcp { get; private set; } = null!;

	public McpLogToolsFixture()
	{
		_baseDir = Path.Combine(Path.GetTempPath(), "petbox-mcp-log-test-" + Guid.NewGuid().ToString("N"));
		Environment.SetEnvironmentVariable("PETBOX_MASTER_KEY", "test-key-for-secrets");
		// WebApplication.CreateBuilder reads ASPNETCORE_ENVIRONMENT + Features__*
		// env vars at construction — before WithWebHostBuilder callbacks apply —
		// so the feature flag checks in ConfigureServices see them. Without these,
		// on Linux CI the env defaults to Production, Features:Logging/Data are
		// false, and IIngestionPipeline / DataDbFactory aren't registered.
		Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");
		Environment.SetEnvironmentVariable("Features__Logging", "true");
		Environment.SetEnvironmentVariable("Features__Data", "true");

		Factory = new WebApplicationFactory<Program>()
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
			var db = scope.ServiceProvider.GetRequiredService<PetBoxDb>();
			await db.ApiKeys.Where(k => k.Key == TestApiKey).DeleteAsync();
			await db.Projects.Where(p => p.Key == TestProjectKey).DeleteAsync();
			await db.Workspaces.Where(w => w.Key == "test").DeleteAsync();

			await db.InsertAsync(new Workspace { Key = "test", Name = "Test", CreatedAt = DateTime.UtcNow });
			await db.InsertAsync(new Project { Key = TestProjectKey, WorkspaceKey = "test", Name = "KpVotes" });
			await db.InsertAsync(new ApiKey { Key = TestApiKey, ProjectKey = TestProjectKey, Scopes = TestScopes, CreatedAt = DateTime.UtcNow });

			var store = scope.ServiceProvider.GetRequiredService<ILogStore>();
			await store.CreateAsync(TestProjectKey, LogNames.Default, null);
		}

		// Seed two log entries so log_query has something to return.
		using (var scope = Factory.Services.CreateScope())
		{
			var pipeline = scope.ServiceProvider.GetRequiredService<IIngestionPipeline>();
			var records = new[]
			{
				new PetBox.Log.Core.Models.LogEntryCandidate
				{
					ServiceKey = TestServiceKey,
					Timestamp = DateTime.UtcNow.AddSeconds(-30),
					Level = PetBox.Log.Core.Models.LogLevel.Information,
					Message = "Service started",
					MessageTemplate = "Service started",
					Properties = "{}",
				},
				new PetBox.Log.Core.Models.LogEntryCandidate
				{
					ServiceKey = TestServiceKey,
					Timestamp = DateTime.UtcNow,
					Level = PetBox.Log.Core.Models.LogLevel.Error,
					Message = "Boom",
					MessageTemplate = "Boom",
					Properties = "{}",
				},
			};
			await pipeline.IngestAsync(TestProjectKey, LogNames.Default, records, default);
			// Give the channel pipeline a moment to drain its writer loop.
			await Task.Delay(500);
		}

		_http.DefaultRequestHeaders.Add("X-Api-Key", TestApiKey);
		var transport = new HttpClientTransport(new HttpClientTransportOptions
		{
			Endpoint = new Uri(_http.BaseAddress!, "/mcp"),
			AdditionalHeaders = new Dictionary<string, string> { ["X-Api-Key"] = TestApiKey },
		}, _http);
		Mcp = await McpClient.CreateAsync(transport, cancellationToken: default);
	}

	// Per-test reset under the shared host: restore the ApiKey's scopes, which
	// LogQuery_MissingScope_Rejected narrows to data:read (auth reads the key row per
	// request, so the restore takes effect immediately).
	public async Task ResetAsync()
	{
		using var scope = Factory.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<PetBoxDb>();
		await db.ApiKeys.Where(k => k.Key == TestApiKey)
			.Set(k => k.Scopes, TestScopes)
			.UpdateAsync();
	}

	public async Task DisposeAsync()
	{
		await Mcp.DisposeAsync();
		_http.Dispose();
		await Factory.DisposeAsync();
		TestDirs.CleanupOrDefer(_baseDir);
	}
}

// Sanity tests for the single Log MCP tool (log_query). Deep KQL behavior is
// covered by the LogPipeline + KqlTransformer tests; this just verifies the
// MCP surface routes correctly and respects auth.
public sealed class McpLogToolsTests : IClassFixture<McpLogToolsFixture>, IAsyncLifetime
{
	const string TestProjectKey = McpLogToolsFixture.TestProjectKey;
	const string TestApiKey = McpLogToolsFixture.TestApiKey;

	readonly McpLogToolsFixture _fx;
	readonly WebApplicationFactory<Program> _factory;
	readonly McpClient _mcp;

	public McpLogToolsTests(McpLogToolsFixture fx)
	{
		_fx = fx;
		_factory = fx.Factory;
		_mcp = fx.Mcp;
	}

	public Task InitializeAsync() => _fx.ResetAsync();

	public Task DisposeAsync() => Task.CompletedTask; // the fixture owns host teardown

	[Fact]
	public async Task LogQuery_Tool_IsDiscoverable()
	{
		var tools = await _mcp.ListToolsAsync();
		tools.Select(t => t.Name).Should().Contain("log_query");
	}

	[Fact]
	public async Task LogQuery_Events_ReturnsSeededRows()
	{
		var tool = (await _mcp.ListToolsAsync()).First(t => t.Name == "log_query");

		var result = await tool.CallAsync(new Dictionary<string, object?>
		{
			["projectKey"] = TestProjectKey,
			["logName"] = LogNames.Default,
			["kql"] = "events",
		});

		result.IsError.Should().NotBe(true);
		var text = result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().First().Text;
		text.Should().Contain("\"kind\":\"events\"");
		text.Should().Contain("Boom");

		// Event field names are pinned to the PascalCase KQL schema (mirroring the table-shape
		// columns and the REST LogEventDto), NOT the MCP default camelCase — so an agent parser
		// reads one casing across both log_query shapes.
		text.Should().Contain("\"Timestamp\":").And.Contain("\"Level\":")
			.And.Contain("\"ServiceKey\":").And.Contain("\"MessageTemplate\":");
		text.Should().NotContain("\"timestamp\":").And.NotContain("\"serviceKey\":")
			.And.NotContain("\"messageTemplate\":");
	}

	[Fact]
	public async Task LogQuery_ShapeChanging_ReturnsTable()
	{
		var tool = (await _mcp.ListToolsAsync()).First(t => t.Name == "log_query");

		var result = await tool.CallAsync(new Dictionary<string, object?>
		{
			["projectKey"] = TestProjectKey,
			["logName"] = LogNames.Default,
			["kql"] = "events | summarize count() by Level",
		});

		result.IsError.Should().NotBe(true);
		var text = result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().First().Text;
		text.Should().Contain("\"kind\":\"table\"");
	}

	[Fact]
	public async Task LogQuery_ExecutionError_ReturnsStructuredError()
	{
		var tool = (await _mcp.ListToolsAsync()).First(t => t.Name == "log_query");

		// Valid syntax, but the regex PATTERN is malformed, so the registered scalar
		// function throws at EXECUTION time. The raw engine exception used to surface as
		// the framework's opaque "An error occurred invoking 'log_query'."; via
		// McpErrorEnvelopeFilter it must be a structured {error} with the failure class
		// (KqlExecutionException) and the reason. (This test used to ride on `where
		// LevelName == ...` being untranslatable; LevelName now translates via a CASE
		// mapping — spans-review fix 1 — so a malformed regex is the fault vehicle.)
		var result = await tool.CallAsync(new Dictionary<string, object?>
		{
			["projectKey"] = TestProjectKey,
			["logName"] = LogNames.Default,
			["kql"] = "events | where Message matches regex \"(\"",
		});

		var text = result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().First().Text;
		text.Should().Contain("\"error\"");
		text.Should().Contain("KqlExecutionException");
		text.Should().Contain("KQL execution failed");
	}

	[Fact]
	public async Task LogQuery_ExecutionError_TablePath_ReturnsStructuredError()
	{
		var tool = (await _mcp.ListToolsAsync()).First(t => t.Name == "log_query");

		// Same engine fault, but on the shape-changing path: rows stream lazily and the
		// exception surfaces in the tool's await-foreach — still inside the tool body,
		// still caught by the central envelope.
		var result = await tool.CallAsync(new Dictionary<string, object?>
		{
			["projectKey"] = TestProjectKey,
			["logName"] = LogNames.Default,
			["kql"] = "events | where Message matches regex \"(\" | summarize count() by Level",
		});

		var text = result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().First().Text;
		text.Should().Contain("\"error\"");
		text.Should().Contain("KqlExecutionException");
		text.Should().Contain("KQL execution failed");
	}

	[Fact]
	public async Task LogQuery_CrossProject_Rejected()
	{
		var tool = (await _mcp.ListToolsAsync()).First(t => t.Name == "log_query");
		var result = await tool.CallAsync(new Dictionary<string, object?>
		{
			["projectKey"] = "some-other-project",
			["logName"] = LogNames.Default,
			["kql"] = "events",
		});
		// A foreign project surfaces via McpErrorEnvelopeFilter: the structured {error}
		// body on the text content, with IsError=true (consistent with tasks.*).
		result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().First().Text
			.Should().Contain("UnauthorizedAccessException");
	}

	[Fact]
	public async Task LogQuery_MissingScope_Rejected()
	{
		// Grab the tool while the key still lists it (A7b hides log.* once the logs
		// scope is gone); call-time AssertScope is the actual boundary under test.
		var tool = (await _mcp.ListToolsAsync()).First(t => t.Name == "log_query");

		// Re-key without logs:query.
		using (var scope = _factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<PetBoxDb>();
			await db.ApiKeys.Where(k => k.Key == TestApiKey)
				.Set(k => k.Scopes, "data:read")
				.UpdateAsync();
		}
		var result = await tool.CallAsync(new Dictionary<string, object?>
		{
			["projectKey"] = TestProjectKey,
			["logName"] = LogNames.Default,
			["kql"] = "events",
		});
		result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().First().Text
			.Should().Contain("UnauthorizedAccessException");
	}
}
