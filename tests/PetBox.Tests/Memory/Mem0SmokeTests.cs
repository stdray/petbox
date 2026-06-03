using System.Text.Json;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Memory.Data;

namespace PetBox.Tests.Memory;

// Scenario smoke for the mem0-compatible surface over the REAL MCP transport
// (WebApplicationFactory + McpClient over /mcp). Unlike the in-process Mem0ToolsTests,
// this exercises reflection tool registration, MCP JSON arg binding, and the tools/list
// scope filter — the "REST-green != MCP-green" gotcha class. Memory feature on; one key
// with memory scope, one without (to assert listing hygiene).
[Collection("DataModule")]
public sealed class Mem0SmokeTests : IAsyncLifetime
{
	const string ProjectKey = "mem";
	const string MemKey = "yb_key_mem_agent";   // memory:read,memory:write
	const string NoMemKey = "yb_key_mem_nomem"; // logs:query only — no memory scope

	readonly string _baseDir;
	readonly WebApplicationFactory<Program> _factory;
	HttpClient _http = null!;
	HttpClient _httpNoMem = null!;
	McpClient _mcp = null!;
	McpClient _mcpNoMem = null!;

	public Mem0SmokeTests()
	{
		_baseDir = Path.Combine(Path.GetTempPath(), "petbox-mem0-smoke-" + Guid.NewGuid().ToString("N"));
		Environment.SetEnvironmentVariable("PETBOX_MASTER_KEY", "test-key-for-secrets");
		Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");
		Environment.SetEnvironmentVariable("Features__Memory", "true");

		_factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
		{
			b.UseEnvironment("Testing");
			b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
			{
				["ConnectionStrings:PetBox"] = $"Data Source={Path.Combine(Path.GetTempPath(), $"petbox-mem0-{Guid.NewGuid():N}.db")};Cache=Shared",
				["Features:Memory"] = "true",
			}));
			b.ConfigureServices(svc =>
			{
				var memFactory = svc.SingleOrDefault(d => d.ServiceType == typeof(IScopedDbFactory<MemoryDb>));
				if (memFactory is not null) svc.Remove(memFactory);
				svc.AddSingleton<IScopedDbFactory<MemoryDb>>(_ => new ScopedDbFactory<MemoryDb>(
					Path.Combine(_baseDir, "memory"), PetBox.Core.Settings.Scope.Project,
					cs => new MemoryDb(MemoryDb.CreateOptions(cs)), MemorySchema.Ensure));
			});
		});
	}

	public async Task InitializeAsync()
	{
		var cs = _factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("PetBox")!;
		MigrationRunner.Run(cs);
		using var scope = _factory.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<PetBoxDb>();
		await db.Workspaces.Where(w => w.Key == "test").DeleteAsync();
		await db.InsertAsync(new Workspace { Key = "test", Name = "Test", CreatedAt = DateTime.UtcNow });
		await db.InsertAsync(new Project { Key = ProjectKey, WorkspaceKey = "test", Name = "Memory" });
		await db.InsertAsync(new ApiKey { Key = MemKey, ProjectKey = ProjectKey, Scopes = "memory:read,memory:write", CreatedAt = DateTime.UtcNow });
		await db.InsertAsync(new ApiKey { Key = NoMemKey, ProjectKey = ProjectKey, Scopes = "logs:query", CreatedAt = DateTime.UtcNow });

		(_http, _mcp) = await ConnectAsync(MemKey);
		(_httpNoMem, _mcpNoMem) = await ConnectAsync(NoMemKey);
	}

	async Task<(HttpClient, McpClient)> ConnectAsync(string apiKey)
	{
		var http = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
		http.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
		var transport = new HttpClientTransport(new HttpClientTransportOptions
		{
			Endpoint = new Uri(http.BaseAddress!, "/mcp"),
			AdditionalHeaders = new Dictionary<string, string> { ["X-Api-Key"] = apiKey },
		}, http);
		return (http, await McpClient.CreateAsync(transport, cancellationToken: default));
	}

	public async Task DisposeAsync()
	{
		await _mcp.DisposeAsync();
		await _mcpNoMem.DisposeAsync();
		_http.Dispose();
		_httpNoMem.Dispose();
		await _factory.DisposeAsync();
		SqliteConnection.ClearAllPools();
		if (Directory.Exists(_baseDir)) Directory.Delete(_baseDir, recursive: true);
	}

	[Fact]
	public async Task Mem0_Add_Search_Get_Update_Delete_Roundtrip_OverMcp()
	{
		var add = await Mem("add_memory", new { projectKey = ProjectKey, messages = "auth uses jwt middleware", user_id = "alice" });
		IsErr(add).Should().BeFalse(Text(add));
		var id = JsonDocument.Parse(Text(add)).RootElement.GetProperty("results")[0].GetProperty("id").GetString()!;

		var search = await Mem("search_memories", new { projectKey = ProjectKey, query = "jwt", user_id = "alice" });
		IsErr(search).Should().BeFalse(Text(search));
		Text(search).Should().Contain(id).And.Contain("auth uses jwt middleware");

		var get = await Mem("get_memory", new { projectKey = ProjectKey, id });
		Text(get).Should().Contain("auth uses jwt middleware");

		var upd = await Mem("update_memory", new { projectKey = ProjectKey, id, text = "auth uses jwt and refresh tokens" });
		IsErr(upd).Should().BeFalse(Text(upd));
		Text(await Mem("get_memory", new { projectKey = ProjectKey, id })).Should().Contain("refresh tokens");

		var del = await Mem("delete_memory", new { projectKey = ProjectKey, id });
		IsErr(del).Should().BeFalse(Text(del));
		Text(await Mem("get_memory", new { projectKey = ProjectKey, id })).Should().Contain("not found");
	}

	[Fact]
	public async Task ToolsList_ShowsMem0Tools_ForMemoryKey_HidesForNonMemoryKey()
	{
		var memTools = (await _mcp.ListToolsAsync()).Select(t => t.Name).ToList();
		memTools.Should().Contain("add_memory").And.Contain("search_memories").And.Contain("delete_memory");

		var noMemTools = (await _mcpNoMem.ListToolsAsync()).Select(t => t.Name).ToList();
		noMemTools.Should().NotContain("add_memory", "a non-memory key must not see mem0 tools in tools/list");
	}

	Task<CallToolResult> Mem(string tool, object args) => Call(_mcp, tool, args);

	static async Task<CallToolResult> Call(McpClient mcp, string tool, object args) =>
		await (await mcp.ListToolsAsync()).First(t => t.Name == tool).CallAsync(ToArgs(args));

	static Dictionary<string, object?> ToArgs(object o) =>
		JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(o))!
			.ToDictionary(kv => kv.Key, kv => (object?)((JsonElement)kv.Value!));

	static string Text(CallToolResult r) =>
		r.Content.OfType<TextContentBlock>().First().Text;

	static bool IsErr(CallToolResult r) =>
		r.IsError == true ||
		(r.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text?.Contains("\"error\"") ?? false);
}
