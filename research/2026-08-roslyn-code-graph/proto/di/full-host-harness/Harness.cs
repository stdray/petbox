using LinqToDB;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using PetBox.Core.Data;
using PetBox.Core.Models;

// FULL-HOST DI/MCP runtime-dump harness (research task 11-di-introspection, worker B).
//
// Raises PetBox.Web's REAL Program via WebApplicationFactory<Program> (an in-memory ASP.NET
// TestServer - no Kestrel, no real port) with the SAME isolation technique the product's own
// integration tests use (tests/PetBox.Tests/Web/AgentDefsApiAuthzTests.cs,
// tests/PetBox.Tests/Mcp/ProjectDefaultFilterTests.cs):
//   - ConnectionStrings:PetBox -> a throwaway temp-dir SQLite file (never the real ./data/petbox.db)
//   - Host:BackgroundServices=false -> all 13 IHostedService instances registered but never started
//     (see src/PetBox.Web/HostedServiceGate.cs)
//   - schema built via the REAL PetBox.Core.Data.MigrationRunner.Run (same code path production uses)
// Nothing here touches D:\my\prj\petbox's shared checkout, any real port, or any prod data.
//
// Two things this proves that the no-Build harness (registrations-no-build.json) cannot:
//   1. the exact count of McpServerTool instances the SERVER actually registered (vs. what
//      WithSchemaHonestToolsFromAssembly *could* find by reflection alone);
//   2. the McpTenantEnforcementFilter / McpProjectExistsFilter ORDER claim in Program.cs's comments
//      ("tenant PEP is OUTSIDE/ABOVE the existence check"), by sending two REAL tools/call requests
//      over the live filter chain and reading the actual JSON-RPC error back - not by reading the
//      comment and trusting it.
internal static class DiDumpHostEntry
{
	const string Ws = "didumpws";
	const string ProjA = "didump-proja"; // exists, owned by the test key
	const string ProjGhost = "didump-does-not-exist"; // never inserted - for the order probe

	static async Task<int> Main(string[] args)
	{
		Environment.SetEnvironmentVariable("PETBOX_MASTER_KEY", "test-key-for-secrets");
		Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");

		var tempDataDir = Path.Combine(Path.GetTempPath(), "petbox-di-dump-host-" + Guid.NewGuid().ToString("N")[..8]);
		Directory.CreateDirectory(tempDataDir);
		var cs = $"Data Source={Path.Combine(tempDataDir, "petbox.db")};Cache=Shared";

		// Built by hand (not WebApplicationFactory<Program>): WebApplicationFactory's default content
		// root resolution walks up from the harness's OWN bin/ looking for PetBox's .sln/.slnx, and
		// this harness deliberately lives outside that tree (scratchpad, to keep build artifacts out
		// of the shared checkout) so that probe throws. Building the WebApplicationBuilder directly
		// and calling PetBox.Web's OWN two composition-root entry points -
		// Program.ConfigureServices(builder) then Program.Configure(app) - plus TestServer instead of
		// Kestrel is the same shape WebApplicationFactory would produce, without the path probing.
		var builder = WebApplication.CreateBuilder(new WebApplicationOptions
		{
			Args = [],
			EnvironmentName = "Testing",
			ContentRootPath = @"D:\my\prj\petbox\.claude\worktrees\agent-aec38125713175147\src\PetBox.Web",
		});
		builder.WebHost.UseTestServer();
		builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
		{
			["ConnectionStrings:PetBox"] = cs,
			["Host:BackgroundServices"] = "false",
			["Features:Tasks"] = "true",
		});

		global::Program.ConfigureServices(builder);
		var app = builder.Build();
		global::Program.Configure(app);
		await app.StartAsync();

		// Real production migration code, same as TestSchema.Core(cs) in the test project.
		MigrationRunner.Run(cs);

		const string apiKey = "yb_key_di_dump_proba";
		using (var scope = app.Services.CreateScope())
		{
			using var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open();
			await db.InsertAsync(new Workspace { Key = Ws, Name = "DiDump", CreatedAt = DateTime.UtcNow });
			await db.InsertAsync(new Project { Key = ProjA, WorkspaceKey = Ws, Name = "DiDump A" });
			await db.InsertAsync(new ApiKey
			{
				Key = apiKey,
				ProjectKey = ProjA,
				Scopes = "tasks:read",
				Name = "di-dump-probe",
				CreatedAt = DateTime.UtcNow,
			});
		}

		// (1) The server's OWN registered tool count - resolved from its OWN DI container, not
		// re-derived by reflection.
		var registeredTools = app.Services.GetServices<McpServerTool>().Select(t => t.ProtocolTool.Name).ToList();
		Console.WriteLine($"mcp-tools-registered={registeredTools.Count}");

		using var http = app.GetTestClient();

		// (2) THE ORDER PROBE. Same tool ("tasks_search"), same caller (a key that owns ProjA and
		// NOTHING else), two different projectKey arguments:
		//   probeB -> a project this key does NOT own, but which EXISTS in core.db
		//   probeGhost -> a project that does not exist in core.db at all
		// Program.cs's comment claims McpTenantEnforcementFilter (the tenant PEP) sits OUTSIDE/ABOVE
		// McpProjectExistsFilter, specifically so an unauthorized caller gets a tenant refusal and
		// NEVER an "existence" answer - i.e. probeB and probeGhost must come back with the SAME error
		// shape (both refused on the tenant axis), and neither may leak "does not exist" /
		// "did you mean" text, which would mean the existence check ran FIRST for the ghost project
		// and the registry is acting as an existence oracle.
		var ownProject = await CallToolAsync(http, apiKey, "tasks_search", new Dictionary<string, object?>
		{
			["projectKey"] = ProjA, ["query"] = "x",
		});
		Console.WriteLine($"own-project (ProjA, owned) -> IsError={ownProject.IsError} text={FirstText(ownProject)}");

		// A second, real, EXISTING project this key does not own.
		using (var scope = app.Services.CreateScope())
		{
			using var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open();
			await db.InsertAsync(new Project { Key = "didump-projb", WorkspaceKey = Ws, Name = "DiDump B" });
		}
		var otherExisting = await CallToolAsync(http, apiKey, "tasks_search", new Dictionary<string, object?>
		{
			["projectKey"] = "didump-projb", ["query"] = "x",
		});
		Console.WriteLine($"other-existing-project (not owned) -> IsError={otherExisting.IsError} text={FirstText(otherExisting)}");

		var ghost = await CallToolAsync(http, apiKey, "tasks_search", new Dictionary<string, object?>
		{
			["projectKey"] = ProjGhost, ["query"] = "x",
		});
		Console.WriteLine($"ghost-project (does not exist)  -> IsError={ghost.IsError} text={FirstText(ghost)}");

		Console.WriteLine($"same-error-shape(other-existing vs ghost)={FirstText(otherExisting) == FirstText(ghost)}");

		var outPath = args.Length > 0 ? args[0] : "mcp-tools-registered.json";
		await File.WriteAllTextAsync(outPath, System.Text.Json.JsonSerializer.Serialize(registeredTools,
			new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
		Console.WriteLine($"wrote: {Path.GetFullPath(outPath)}");
		Console.WriteLine($"tempDataDir: {tempDataDir}");

		await app.StopAsync();
		await app.DisposeAsync();
		return 0;
	}

	static async Task<CallToolResult> CallToolAsync(HttpClient http, string apiKey, string tool, Dictionary<string, object?> args)
	{
		var transport = new HttpClientTransport(new HttpClientTransportOptions
		{
			Endpoint = new Uri(http.BaseAddress!, "/mcp"),
			AdditionalHeaders = new Dictionary<string, string> { ["X-Api-Key"] = apiKey },
		}, http);
		await using var mcp = await McpClient.CreateAsync(transport, cancellationToken: default);
		return await mcp.CallToolAsync(tool, args, cancellationToken: default);
	}

	static string FirstText(CallToolResult r) =>
		r.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text ?? "(no text content)";
}
