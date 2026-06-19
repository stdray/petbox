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
using PetBox.Log.Core.Data;
using PetBox.Log.Core.Ingestion;

namespace PetBox.Tests.Data;

// Sanity tests for the single Log MCP tool (log.query). Deep KQL behavior is
// covered by the LogPipeline + KqlTransformer tests; this just verifies the
// MCP surface routes correctly and respects auth.
[Collection("DataModule")]
public sealed class McpLogToolsTests : IAsyncLifetime
{
	const string TestProjectKey = "kpvotes";
	const string TestApiKey = "yb_key_test_mcp_log_xyz";
	const string TestServiceKey = "kpvotes-net";

	readonly string _baseDir;
	readonly WebApplicationFactory<Program> _factory;
	McpClient _mcp = null!;
	HttpClient _http = null!;

	public McpLogToolsTests()
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
			await db.ApiKeys.Where(k => k.Key == TestApiKey).DeleteAsync();
			await db.Projects.Where(p => p.Key == TestProjectKey).DeleteAsync();
			await db.Workspaces.Where(w => w.Key == "test").DeleteAsync();

			await db.InsertAsync(new Workspace { Key = "test", Name = "Test", CreatedAt = DateTime.UtcNow });
			await db.InsertAsync(new Project { Key = TestProjectKey, WorkspaceKey = "test", Name = "KpVotes" });
			await db.InsertAsync(new ApiKey { Key = TestApiKey, ProjectKey = TestProjectKey, Scopes = "logs:query,logs:ingest", CreatedAt = DateTime.UtcNow });

			var store = scope.ServiceProvider.GetRequiredService<ILogStore>();
			await store.CreateAsync(TestProjectKey, LogNames.Default, null);
		}

		// Seed two log entries so log.query has something to return.
		using (var scope = _factory.Services.CreateScope())
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
	public async Task LogQuery_Tool_IsDiscoverable()
	{
		var tools = await _mcp.ListToolsAsync();
		tools.Select(t => t.Name).Should().Contain("log.query");
	}

	[Fact]
	public async Task LogQuery_Events_ReturnsSeededRows()
	{
		var tool = (await _mcp.ListToolsAsync()).First(t => t.Name == "log.query");

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
	}

	[Fact]
	public async Task LogQuery_ShapeChanging_ReturnsTable()
	{
		var tool = (await _mcp.ListToolsAsync()).First(t => t.Name == "log.query");

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
	public async Task LogQuery_CrossProject_Rejected()
	{
		var tool = (await _mcp.ListToolsAsync()).First(t => t.Name == "log.query");
		var result = await tool.CallAsync(new Dictionary<string, object?>
		{
			["projectKey"] = "some-other-project",
			["logName"] = LogNames.Default,
			["kql"] = "events",
		});
		// log.query is GuardAsync-wrapped: a foreign project surfaces as a structured
		// {error} body, not the framework's IsError flag (consistent with tasks.*).
		result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().First().Text
			.Should().Contain("UnauthorizedAccessException");
	}

	[Fact]
	public async Task LogQuery_MissingScope_Rejected()
	{
		// Grab the tool while the key still lists it (A7b hides log.* once the logs
		// scope is gone); call-time AssertScope is the actual boundary under test.
		var tool = (await _mcp.ListToolsAsync()).First(t => t.Name == "log.query");

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
