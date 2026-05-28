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

		_factory = new WebApplicationFactory<Program>()
			.WithWebHostBuilder(b =>
			{
				b.UseEnvironment("Testing");
				b.ConfigureAppConfiguration((_, cfg) =>
				{
					cfg.AddInMemoryCollection(new Dictionary<string, string?>
					{
						["ConnectionStrings:PetBox"] = $"Data Source=petbox-{Guid.NewGuid():N};Mode=Memory;Cache=Shared",
						["Features:Logging"] = "true",
						["Features:Data"] = "true",
					});
				});
				b.ConfigureServices(svc =>
				{
					var logFactory = svc.SingleOrDefault(d => d.ServiceType == typeof(ILogDbFactory));
					if (logFactory is not null) svc.Remove(logFactory);
					svc.AddSingleton<ILogDbFactory>(_ => new LogDbFactory(Path.Combine(_baseDir, "logs")));
				});
			});
	}

	public async Task InitializeAsync()
	{
		_http = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

		using (var scope = _factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<PetBoxDb>();
			await db.ApiKeys.Where(k => k.Key == TestApiKey).DeleteAsync();
			await db.Services.Where(s => s.Key == TestServiceKey).DeleteAsync();
			await db.Projects.Where(p => p.Key == TestProjectKey).DeleteAsync();
			await db.Workspaces.Where(w => w.Key == "test").DeleteAsync();

			await db.InsertAsync(new Workspace { Key = "test", Name = "Test", CreatedAt = DateTime.UtcNow });
			await db.InsertAsync(new Project { Key = TestProjectKey, WorkspaceKey = "test", Name = "KpVotes" });
			await db.InsertAsync(new Service { Key = TestServiceKey, ProjectKey = TestProjectKey, HealthModel = HealthModel.Endpoint, Health = ServiceHealth.Unknown });
			await db.InsertAsync(new ApiKey { Key = TestApiKey, ProjectKey = TestProjectKey, Scopes = "logs:query,logs:ingest", CreatedAt = DateTime.UtcNow });
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
			await pipeline.IngestAsync(TestProjectKey, records, default);
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
			["kql"] = "events",
		});

		result.IsError.Should().NotBe(true);
		var text = result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().First().Text;
		text.Should().Contain("\"kind\": \"events\"");
		text.Should().Contain("Boom");
	}

	[Fact]
	public async Task LogQuery_ShapeChanging_ReturnsTable()
	{
		var tool = (await _mcp.ListToolsAsync()).First(t => t.Name == "log.query");

		var result = await tool.CallAsync(new Dictionary<string, object?>
		{
			["projectKey"] = TestProjectKey,
			["kql"] = "events | summarize count() by Level",
		});

		result.IsError.Should().NotBe(true);
		var text = result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().First().Text;
		text.Should().Contain("\"kind\": \"table\"");
	}

	[Fact]
	public async Task LogQuery_CrossProject_Rejected()
	{
		var tool = (await _mcp.ListToolsAsync()).First(t => t.Name == "log.query");
		var result = await tool.CallAsync(new Dictionary<string, object?>
		{
			["projectKey"] = "some-other-project",
			["kql"] = "events",
		});
		result.IsError.Should().Be(true);
	}

	[Fact]
	public async Task LogQuery_MissingScope_Rejected()
	{
		// Re-key without logs:query.
		using (var scope = _factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<PetBoxDb>();
			await db.ApiKeys.Where(k => k.Key == TestApiKey)
				.Set(k => k.Scopes, "data:read")
				.UpdateAsync();
		}

		var tool = (await _mcp.ListToolsAsync()).First(t => t.Name == "log.query");
		var result = await tool.CallAsync(new Dictionary<string, object?>
		{
			["projectKey"] = TestProjectKey,
			["kql"] = "events",
		});
		result.IsError.Should().Be(true);
	}
}
