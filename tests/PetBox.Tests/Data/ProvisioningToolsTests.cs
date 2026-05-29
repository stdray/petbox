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

namespace PetBox.Tests.Data;

// WS6.2 — agent-provisioning MCP tools. An agent key with admin:provision can create a
// project and mint a downstream key; a key without the scope is rejected.
[Collection("DataModule")]
public sealed class ProvisioningToolsTests : IAsyncLifetime
{
	const string Workspace = "wsprov";
	const string AgentKey = "yb_key_agent_prov";
	const string NoScopeKey = "yb_key_no_prov";

	readonly WebApplicationFactory<Program> _factory;
	HttpClient _http = null!;
	McpClient _mcp = null!;

	public ProvisioningToolsTests()
	{
		Environment.SetEnvironmentVariable("PETBOX_MASTER_KEY", "test-key-for-secrets");
		Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");
		Environment.SetEnvironmentVariable("Features__Config", "true");

		Environment.SetEnvironmentVariable("CONNECTIONSTRINGS__PETBOX", $"Data Source={Path.Combine(Path.GetTempPath(), $"petbox-test-{Guid.NewGuid():N}.db")};Cache=Shared");
		_factory = new WebApplicationFactory<Program>()
			.WithWebHostBuilder(b =>
			{
				b.UseEnvironment("Testing");
				b.ConfigureAppConfiguration((_, cfg) =>
				{
					cfg.AddInMemoryCollection(new Dictionary<string, string?>
					{
						["Features:Config"] = "true",
					});
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
			await db.ApiKeys.Where(k => k.Key == AgentKey || k.Key == NoScopeKey).DeleteAsync();
			await db.Workspaces.Where(w => w.Key == Workspace).DeleteAsync();
			await db.InsertAsync(new Workspace { Key = Workspace, Name = "Prov", CreatedAt = DateTime.UtcNow });
			await db.InsertAsync(new ApiKey { Key = AgentKey, ProjectKey = "$system", Scopes = "admin:provision", CreatedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddHours(1) });
			await db.InsertAsync(new ApiKey { Key = NoScopeKey, ProjectKey = "$system", Scopes = "config:read", CreatedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddHours(1) });
		}

		_http.DefaultRequestHeaders.Add("X-Api-Key", AgentKey);
		var transport = new HttpClientTransport(new HttpClientTransportOptions
		{
			Endpoint = new Uri(_http.BaseAddress!, "/mcp"),
			AdditionalHeaders = new Dictionary<string, string> { ["X-Api-Key"] = AgentKey },
		}, _http);
		_mcp = await McpClient.CreateAsync(transport, cancellationToken: default);
	}

	public async Task DisposeAsync()
	{
		await _mcp.DisposeAsync();
		_http.Dispose();
		await _factory.DisposeAsync();
		Environment.SetEnvironmentVariable("CONNECTIONSTRINGS__PETBOX", null);
	}

	[Fact]
	public async Task ProvisioningTools_AreDiscoverable()
	{
		var names = (await _mcp.ListToolsAsync()).Select(t => t.Name).ToList();
		names.Should().Contain("workspace.create_project");
		names.Should().Contain("project.create_apikey");
	}

	[Fact]
	public async Task CreateProject_ThenMintKey_RoundTrips()
	{
		var projectKey = "p" + Guid.NewGuid().ToString("N")[..8];

		var createProject = (await _mcp.ListToolsAsync()).First(t => t.Name == "workspace.create_project");
		var r1 = await createProject.CallAsync(new Dictionary<string, object?>
		{
			["workspaceKey"] = Workspace,
			["key"] = projectKey,
			["name"] = "Provisioned",
		});
		r1.IsError.Should().NotBe(true);

		var mintKey = (await _mcp.ListToolsAsync()).First(t => t.Name == "project.create_apikey");
		var r2 = await mintKey.CallAsync(new Dictionary<string, object?>
		{
			["projectKey"] = projectKey,
			["name"] = "pet",
			["scopes"] = "data:read,data:write",
			["expiresInSeconds"] = 3600,
		});
		r2.IsError.Should().NotBe(true);

		using var scope = _factory.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<PetBoxDb>();
		(await db.Projects.AnyAsync(p => p.Key == projectKey)).Should().BeTrue();
		(await db.ApiKeys.AnyAsync(k => k.ProjectKey == projectKey && k.ExpiresAt != null)).Should().BeTrue();
	}

	[Fact]
	public async Task CreateProject_InvalidKey_Rejected()
	{
		var tool = (await _mcp.ListToolsAsync()).First(t => t.Name == "workspace.create_project");
		var result = await tool.CallAsync(new Dictionary<string, object?>
		{
			["workspaceKey"] = Workspace,
			["key"] = "Bad Key!", // uppercase + space + punctuation
			["name"] = "x",
		});
		result.IsError.Should().Be(true);
	}

	[Fact]
	public async Task WithoutProvisionScope_Rejected()
	{
		// Reconnect with a key that lacks admin:provision.
		var http = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
		http.DefaultRequestHeaders.Add("X-Api-Key", NoScopeKey);
		var transport = new HttpClientTransport(new HttpClientTransportOptions
		{
			Endpoint = new Uri(http.BaseAddress!, "/mcp"),
			AdditionalHeaders = new Dictionary<string, string> { ["X-Api-Key"] = NoScopeKey },
		}, http);
		var mcp = await McpClient.CreateAsync(transport, cancellationToken: default);
		try
		{
			var tool = (await mcp.ListToolsAsync()).First(t => t.Name == "workspace.create_project");
			var result = await tool.CallAsync(new Dictionary<string, object?>
			{
				["workspaceKey"] = Workspace,
				["key"] = "shouldfail",
				["name"] = "x",
			});
			result.IsError.Should().Be(true);
		}
		finally
		{
			await mcp.DisposeAsync();
			http.Dispose();
		}
	}
}
