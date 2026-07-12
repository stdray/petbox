using LinqToDB;
using LinqToDB.Async;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using PetBox.Core.Auth;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Sessions.Contract;

namespace PetBox.Tests.Mcp;

// The sandbox write gate (spec work/smoke-writes-into-real-projects). The owner's decision: sandbox
// background jobs run exactly like production (enrichment stays ON — no separate "smoke mode"), and
// the guarantee instead lives in WHO can write WHERE. A sandboxOnly API key can physically write only
// into projects flagged Sandbox=true — checked in ProjectScope.AuthorizesAsync, which ModuleMcp
// (and therefore every *Tools.cs MCP verb) now goes through instead of the old claim-only Authorizes.
//
// The case that matters most: a sandboxOnly WILDCARD ("*") key. The wildcard claim authorizes every
// project by IDENTITY (Authorizes("*", anything) == true) — that check is UNCHANGED. The containment
// check is a second, orthogonal DB read that never looks at the claim, so the wildcard does NOT
// smuggle a sandboxOnly key into a real project. Mirrors ProjectExistsFilterTests' WebApplicationFactory
// + McpClient style.
public sealed class SandboxWriteGateFixture : IAsyncLifetime
{
	public const string Workspace = "sandboxgatews";
	public const string RealProject = "sandboxgatereal";       // Sandbox = false (a normal project)
	public const string SandboxProject = "sandboxgatesandbox"; // Sandbox = true

	public const string ScopedSandboxKey = "yb_key_sbx_scoped";     // sandboxOnly=true, claim = SandboxProject
	public const string WildcardSandboxKey = "yb_key_sbx_wild";     // sandboxOnly=true, claim = "*"
	public const string WildcardPlainKey = "yb_key_sbx_wild_plain"; // sandboxOnly=false, claim = "*" (control)

	const string Scopes = "tasks:read,tasks:write";

	readonly WebApplicationFactory<Program> _factory;
	readonly List<HttpClient> _clients = [];
	readonly List<McpClient> _mcps = [];

	public McpClient ScopedSandbox { get; private set; } = null!;
	public McpClient WildcardSandbox { get; private set; } = null!;
	public McpClient WildcardPlain { get; private set; } = null!;

	public SandboxWriteGateFixture()
	{
		Environment.SetEnvironmentVariable("PETBOX_MASTER_KEY", "test-key-for-secrets");
		Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");
		_factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
		{
			b.UseEnvironment("Testing");
			b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
			{
				["ConnectionStrings:PetBox"] = TestSchema.NewTempConnectionString(),
				["Host:BackgroundServices"] = "false",
				["Features:Tasks"] = "true",
			}));
		});
	}

	public async Task InitializeAsync()
	{
		var cs = _factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("PetBox")!;
		TestSchema.Core(cs);

		using (var scope = _factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<PetBoxDb>();
			await db.InsertAsync(new Workspace { Key = Workspace, Name = "SBX", CreatedAt = DateTime.UtcNow });
			await db.InsertAsync(new Project { Key = RealProject, WorkspaceKey = Workspace, Name = "Real", Sandbox = false });
			await db.InsertAsync(new Project { Key = SandboxProject, WorkspaceKey = Workspace, Name = "Sandbox", Sandbox = true });

			await db.InsertAsync(new ApiKey
			{
				Key = ScopedSandboxKey,
				ProjectKey = SandboxProject,
				Scopes = Scopes,
				SandboxOnly = true,
				CreatedAt = DateTime.UtcNow,
			});
			await db.InsertAsync(new ApiKey
			{
				Key = WildcardSandboxKey,
				ProjectKey = ProjectScope.AllProjects,
				Scopes = Scopes,
				SandboxOnly = true,
				CreatedAt = DateTime.UtcNow,
			});
			await db.InsertAsync(new ApiKey
			{
				Key = WildcardPlainKey,
				ProjectKey = ProjectScope.AllProjects,
				Scopes = Scopes,
				SandboxOnly = false,
				CreatedAt = DateTime.UtcNow,
			});
		}

		ScopedSandbox = await ConnectAsync(ScopedSandboxKey);
		WildcardSandbox = await ConnectAsync(WildcardSandboxKey);
		WildcardPlain = await ConnectAsync(WildcardPlainKey);
	}

	public IServiceScope Scope() => _factory.Services.CreateScope();

	async Task<McpClient> ConnectAsync(string apiKey)
	{
		var http = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
		_clients.Add(http);
		var transport = new HttpClientTransport(new HttpClientTransportOptions
		{
			Endpoint = new Uri(http.BaseAddress!, "/mcp"),
			AdditionalHeaders = new Dictionary<string, string> { ["X-Api-Key"] = apiKey },
		}, http);
		var mcp = await McpClient.CreateAsync(transport, cancellationToken: default);
		_mcps.Add(mcp);
		return mcp;
	}

	public async Task DisposeAsync()
	{
		foreach (var mcp in _mcps) await mcp.DisposeAsync();
		foreach (var http in _clients) http.Dispose();
		await _factory.DisposeAsync();
	}
}

public sealed class SandboxWriteGateTests : IClassFixture<SandboxWriteGateFixture>
{
	readonly SandboxWriteGateFixture _fx;
	public SandboxWriteGateTests(SandboxWriteGateFixture fx) => _fx = fx;

	static async Task<McpClientTool> Tool(McpClient mcp, string name) =>
		(await mcp.ListToolsAsync()).First(t => t.Name == name);

	static string Text(CallToolResult result) =>
		string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));

	static async Task<CallToolResult> SessionUpsertAsync(McpClient mcp, string projectKey, string sessionId)
	{
		var tool = await Tool(mcp, "session_upsert");
		return await tool.CallAsync(new Dictionary<string, object?>
		{
			["projectKey"] = projectKey,
			["sessionId"] = sessionId,
			["agent"] = "test-agent",
			["content"] = "sandbox gate probe",
		});
	}

	static async Task<CallToolResult> BoardCreateAsync(McpClient mcp, string projectKey, string board)
	{
		var tool = await Tool(mcp, "tasks_board_create");
		return await tool.CallAsync(new Dictionary<string, object?> { ["projectKey"] = projectKey, ["board"] = board });
	}

	// ── session_upsert ──────────────────────────────────────────────────────────

	[Fact]
	public async Task ScopedSandboxKey_WritesIntoItsOwnSandboxProject_Succeeds()
	{
		var result = await SessionUpsertAsync(_fx.ScopedSandbox, SandboxWriteGateFixture.SandboxProject, "s-scoped-ok");
		result.IsError.Should().NotBe(true, Text(result));
	}

	[Fact]
	public async Task ScopedSandboxKey_WritesIntoARealProject_IsRejected()
	{
		// The key's own claim IS the sandbox project, so a real-project projectKey fails identity
		// (Authorizes) before containment is even reached — either way, it must be refused.
		var result = await SessionUpsertAsync(_fx.ScopedSandbox, SandboxWriteGateFixture.RealProject, "s-scoped-bad");
		result.IsError.Should().Be(true);
	}

	// THE case: a sandboxOnly WILDCARD key. Identity (Authorizes("*", RealProject)) says yes — the
	// containment check is what has to say no.
	[Fact]
	public async Task WildcardSandboxKey_WritesIntoARealProject_IsRejected_EvenThoughTheClaimAuthorizesIt()
	{
		var result = await SessionUpsertAsync(_fx.WildcardSandbox, SandboxWriteGateFixture.RealProject, "s-wild-bad");

		result.IsError.Should().Be(true,
			"the wildcard claim authorizes every project by identity, but a sandboxOnly key must still be "
			+ "refused on a non-sandbox project — that is the entire point of the containment check");

		using var scope = _fx.Scope();
		var sessions = scope.ServiceProvider.GetRequiredService<ISessionService>();
		(await sessions.ListAsync(SandboxWriteGateFixture.RealProject))
			.Should().NotContain(s => s.SessionId == "s-wild-bad", "a rejected write must not land in the real project");
	}

	// …and the SAME wildcard sandboxOnly key succeeds against a sandbox project — one smoke key,
	// many sandbox projects, exactly as the design intends.
	[Fact]
	public async Task WildcardSandboxKey_WritesIntoASandboxProject_Succeeds()
	{
		var result = await SessionUpsertAsync(_fx.WildcardSandbox, SandboxWriteGateFixture.SandboxProject, "s-wild-ok");
		result.IsError.Should().NotBe(true, Text(result));

		using var scope = _fx.Scope();
		var sessions = scope.ServiceProvider.GetRequiredService<ISessionService>();
		(await sessions.ListAsync(SandboxWriteGateFixture.SandboxProject))
			.Should().Contain(s => s.SessionId == "s-wild-ok");
	}

	// Control: a PLAIN (non-sandboxOnly) wildcard key is unaffected by the new gate — it still
	// writes into a real project exactly like before this change.
	[Fact]
	public async Task PlainWildcardKey_WritesIntoARealProject_StillSucceeds()
	{
		var result = await SessionUpsertAsync(_fx.WildcardPlain, SandboxWriteGateFixture.RealProject, "s-plain-ok");
		result.IsError.Should().NotBe(true, Text(result));
	}

	// ── tasks_board_create (tasks:write path — the same ModuleMcp.AssertProject gate) ─────────────

	[Fact]
	public async Task WildcardSandboxKey_BoardCreate_OnARealProject_IsRejected()
	{
		var result = await BoardCreateAsync(_fx.WildcardSandbox, SandboxWriteGateFixture.RealProject, "sbx-board-bad");
		result.IsError.Should().Be(true);

		using var scope = _fx.Scope();
		var db = scope.ServiceProvider.GetRequiredService<PetBoxDb>();
		(await db.TaskBoards.AnyAsync(b => b.ProjectKey == SandboxWriteGateFixture.RealProject))
			.Should().BeFalse("a rejected board_create must not materialize a board in the real project");
	}

	[Fact]
	public async Task WildcardSandboxKey_BoardCreate_OnASandboxProject_Succeeds()
	{
		var result = await BoardCreateAsync(_fx.WildcardSandbox, SandboxWriteGateFixture.SandboxProject, "sbx-board-ok");
		result.IsError.Should().NotBe(true, Text(result));
	}
}
