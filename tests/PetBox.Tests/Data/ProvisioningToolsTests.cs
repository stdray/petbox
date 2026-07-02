using LinqToDB;
using LinqToDB.Async;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using PetBox.Core.Data;
using PetBox.Core.Models;

namespace PetBox.Tests.Data;

// WS6.2 — agent-provisioning MCP tools, now typed per-type (typed-surface Phase 4):
// project_create/list, apikey_create/list/delete (replacing the generic entity.* dispatch).
// An agent key with admin:provision can create a project and mint a downstream key; a key
// without the scope is rejected. Every tool surfaces failures as a structured {error}
// payload (GuardAsync), so we assert error CONTENT, not just IsError.
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
		TestSchema.Core(cs);
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

	async Task<McpClientTool> ToolAsync(string name) =>
		(await _mcp.ListToolsAsync()).First(t => t.Name == name);

	static string Text(ModelContextProtocol.Protocol.CallToolResult r) =>
		r.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().First().Text;

	[Fact]
	public async Task ProvisioningTools_AreDiscoverable()
	{
		// Provisioning verbs are now typed per-type tools; the generic entity.* family and
		// the old workspace.create_project / project_create_apikey names are gone.
		var names = (await _mcp.ListToolsAsync()).Select(t => t.Name).ToList();
		names.Should().Contain("project_create");
		names.Should().Contain("project_list");
		names.Should().Contain("apikey_create");
		names.Should().Contain("apikey_list");
		names.Should().Contain("apikey_delete");
		names.Should().NotContain("entity.create");
		names.Should().NotContain("workspace.create_project");
		names.Should().NotContain("project_create_apikey");
	}

	[Fact]
	public async Task CreateProject_ThenMintKey_RoundTrips()
	{
		var projectKey = "p" + Guid.NewGuid().ToString("N")[..8];

		var create = await ToolAsync("project_create");
		Text(await create.CallAsync(new Dictionary<string, object?>
		{
			["workspaceKey"] = Workspace, ["key"] = projectKey, ["name"] = "Provisioned",
		})).Should().NotContain("\"error\"");

		var mint = await ToolAsync("apikey_create");
		Text(await mint.CallAsync(new Dictionary<string, object?>
		{
			["projectKey"] = projectKey, ["name"] = "pet", ["scopes"] = "data:read,data:write", ["expiresInSeconds"] = 3600,
		})).Should().NotContain("\"error\"");

		using var scope = _factory.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<PetBoxDb>();
		(await db.Projects.AnyAsync(p => p.Key == projectKey)).Should().BeTrue();
		(await db.ApiKeys.AnyAsync(k => k.ProjectKey == projectKey && k.ExpiresAt != null)).Should().BeTrue();

		// apikey_list reflects the minted key.
		var listed = Text(await (await ToolAsync("apikey_list")).CallAsync(new Dictionary<string, object?> { ["projectKey"] = projectKey }));
		listed.Should().Contain("data:read");
	}

	[Fact]
	public async Task ProjectList_ReflectsCreate()
	{
		var projectKey = "p" + Guid.NewGuid().ToString("N")[..8];
		await (await ToolAsync("project_create")).CallAsync(new Dictionary<string, object?>
		{
			["workspaceKey"] = Workspace, ["key"] = projectKey, ["name"] = "Listed",
		});
		var listed = Text(await (await ToolAsync("project_list")).CallAsync(new Dictionary<string, object?> { ["workspaceKey"] = Workspace }));
		listed.Should().Contain(projectKey);
	}

	[Fact]
	public async Task CreateProject_InvalidKey_Rejected()
	{
		var result = await (await ToolAsync("project_create")).CallAsync(new Dictionary<string, object?>
		{
			["workspaceKey"] = Workspace, ["key"] = "Bad Key!", ["name"] = "x",
		});
		// Structured {error} payload (GuardAsync) — assert the cause is the key validation.
		Text(result).Should().Contain("invalid");
	}

	[Fact]
	public async Task MintKey_UnknownScope_Rejected()
	{
		var projectKey = "p" + Guid.NewGuid().ToString("N")[..8];
		await (await ToolAsync("project_create")).CallAsync(new Dictionary<string, object?>
		{
			["workspaceKey"] = Workspace, ["key"] = projectKey, ["name"] = "x",
		});
		var result = await (await ToolAsync("apikey_create")).CallAsync(new Dictionary<string, object?>
		{
			["projectKey"] = projectKey, ["name"] = "k", ["scopes"] = "data:read,bogus:scope",
		});
		Text(result).Should().Contain("Unknown scopes");
	}

	[Fact]
	public async Task MintKey_AllProjects_MintsWildcardClaim()
	{
		// allProjects mints the cross-project key: claim "*" (not a project row), so the
		// project-existence check must be skipped and projectKey omitted.
		var result = Text(await (await ToolAsync("apikey_create")).CallAsync(new Dictionary<string, object?>
		{
			["name"] = "maintenance", ["scopes"] = "tasks:read,tasks:write", ["allProjects"] = true,
		}));
		result.Should().NotContain("\"error\"");
		result.Should().Contain("\"*\"");

		using var scope = _factory.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<PetBoxDb>();
		(await db.ApiKeys.AnyAsync(k => k.ProjectKey == "*" && k.Name == "maintenance")).Should().BeTrue();

		// apikey_list addresses wildcard keys by the literal "*" project.
		var listed = Text(await (await ToolAsync("apikey_list")).CallAsync(new Dictionary<string, object?> { ["projectKey"] = "*" }));
		listed.Should().Contain("maintenance");
	}

	[Fact]
	public async Task MintKey_AllProjects_WithProjectKey_Rejected()
	{
		var result = Text(await (await ToolAsync("apikey_create")).CallAsync(new Dictionary<string, object?>
		{
			["name"] = "k", ["scopes"] = "tasks:read", ["projectKey"] = "$system", ["allProjects"] = true,
		}));
		result.Should().Contain("mutually exclusive");
	}

	[Fact]
	public async Task MintKey_NoProjectKey_Rejected()
	{
		var result = Text(await (await ToolAsync("apikey_create")).CallAsync(new Dictionary<string, object?>
		{
			["name"] = "k", ["scopes"] = "tasks:read",
		}));
		result.Should().Contain("projectKey is required");
	}

	[Fact]
	public async Task ProjectDelete_DoesNotExist_NoAlias()
	{
		// project has no delete (it would orphan logs/dbs/keys) — there is no project.delete tool.
		var names = (await _mcp.ListToolsAsync()).Select(t => t.Name).ToList();
		names.Should().NotContain("project.delete");
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
			var tool = (await mcp.ListToolsAsync()).First(t => t.Name == "project_create");
			var result = await tool.CallAsync(new Dictionary<string, object?>
			{
				["workspaceKey"] = Workspace, ["key"] = "shouldfail", ["name"] = "x",
			});
			// Structured {error} payload (GuardAsync) — the cause is the missing scope.
			Text(result).Should().Contain("scope");
		}
		finally
		{
			await mcp.DisposeAsync();
			http.Dispose();
		}
	}
}
