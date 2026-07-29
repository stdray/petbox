using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LinqToDB;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Tasks.Data;

namespace PetBox.Tests.Mcp;

// BACK-COMPAT GATE for the 2026-07-28 protocol revision (chore mcp-spec-2026-07-28-sdk2-migration).
//
// The whole point of the SDK 2.0.0 migration was: do not break agents that speak an OLDER
// revision. 2026-07-28 adds `ttlMs`/`cacheScope` (tools/list cache hints) and `resultType`.
// Those fields MUST appear only when the negotiated revision actually is 2026-07-28 — the
// version gate lives INSIDE the SDK. csharp-sdk issue #1721 (P0) was exactly this bug: in
// 2.0.0-preview.3 the three fields were emitted UNCONDITIONALLY and strict clients pinned to
// 2025-11-25 (MCP Inspector 1.0.0) rejected every tools/list. It was fixed by PR #1753, in 2.0.0.
//
// We can re-introduce that bug ourselves, because tools/list on this server is REBUILT by our own
// McpToolScopeFilter (scope-trim) + McpToolDescriptions.Compact (description economy). Anything
// our code stamps onto the result bypasses the SDK's gate. This suite is the lock: it asserts the
// gate holds on the WIRE, per revision.
//
// Deliberately raw JSON-RPC over HTTP, NOT the typed SDK client: the regression we are guarding
// against is an EXTRA KEY in the JSON an old client receives. A typed-client assertion would read
// the same CLR property on both sides and never see it. Only the serialized payload can.
//
// Raw HTTP also means this fixture never calls McpClient.CreateAsync, so it cannot contribute to
// the ClientTransportClosedException flake seen in the McpClient-based smoke fixtures.
//
// MEASURED ASYMMETRY between the two directions, worth knowing before editing either test.
// The SDK applies the cache hints DOWNSTREAM of our filter chain:
//   - Stamping the three fields in McpToolScopeFilter DOES reach an old client's wire — the
//     absence test below goes red. That is the #1721 hazard, and it is ours to cause.
//   - Nulling them in that same filter does NOT make the presence test red: the SDK re-stamps
//     them afterwards on a July-2026 negotiation. Our filters therefore cannot LOSE the fields.
// So the absence direction guards OUR code; the presence direction guards the SDK's gate and the
// negotiation contract (it goes red if the _meta/header handshake drifts — verified with a
// deliberate header/_meta mismatch, which the server rejects with -32020).
public sealed class McpProtocolVersionGateFixture : IAsyncLifetime
{
	public const string ProjectKey = "pvgate";
	public const string ApiKey = "yb_key_pvgate_agent";

	// Deliberately NOT admin:provision and NOT every module: with a partial scope set
	// McpToolScopeFilter actually REBUILDS the tool list, so the fields are observed after our
	// filter has done real work — which is where a hand-stamped field would be introduced.
	const string Scopes = "tasks:read,tasks:write";

	readonly string _baseDir = Path.Combine(Path.GetTempPath(), "petbox-pvgate-" + Guid.NewGuid().ToString("N"));
	WebApplicationFactory<Program> _factory = null!;

	public HttpClient Http { get; private set; } = null!;

	public async ValueTask InitializeAsync()
	{
		Environment.SetEnvironmentVariable("PETBOX_MASTER_KEY", "test-key-for-secrets");
		Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");

		_factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
		{
			b.UseEnvironment("Testing");
			b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
			{
				["ConnectionStrings:PetBox"] = TestSchema.NewTempConnectionString(),
				// Same reason as the other MCP fixtures: these tests only need the MCP stack, and
				// wall-clock background timers hold pooled SqliteConnections on Windows.
				["Host:BackgroundServices"] = "false",
				// Two modules so the scope-trim above has something to actually remove (memory_*).
				["Features:Tasks"] = "true",
				["Features:Memory"] = "true",
			}));
			b.ConfigureServices(svc =>
			{
				var f = svc.SingleOrDefault(d => d.ServiceType == typeof(IScopedDbFactory<TasksDb>));
				if (f is not null) svc.Remove(f);
				svc.AddSingleton<IScopedDbFactory<TasksDb>>(_ => new ScopedDbFactory<TasksDb>(
					Path.Combine(_baseDir, "tasks"), PetBox.Core.Settings.Scope.Project,
					cs => new TasksDb(TasksDb.CreateOptions(cs)), TestSchema.Tasks));
			});
		});

		var cs = _factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("PetBox")!;
		TestSchema.Core(cs);

		using (var scope = _factory.Services.CreateScope())
		{
			using var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open();
			await db.ApiKeys.Where(k => k.Key == ApiKey).DeleteAsync();
			await db.Projects.Where(p => p.Key == ProjectKey).DeleteAsync();
			await db.Workspaces.Where(w => w.Key == "pvgate-ws").DeleteAsync();
			await db.InsertAsync(new Workspace { Key = "pvgate-ws", Name = "PvGate", CreatedAt = DateTime.UtcNow });
			await db.InsertAsync(new Project { Key = ProjectKey, WorkspaceKey = "pvgate-ws", Name = "PvGate" });
			await db.InsertAsync(new ApiKey { Key = ApiKey, ProjectKey = ProjectKey, Scopes = Scopes, CreatedAt = DateTime.UtcNow });
		}

		Http = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
		Http.DefaultRequestHeaders.Add("X-Api-Key", ApiKey);
	}

	public async ValueTask DisposeAsync()
	{
		Http.Dispose();
		await _factory.DisposeAsync();
		TestDirs.CleanupOrDefer(_baseDir);
	}
}

public sealed class McpProtocolVersionGateTests : IClassFixture<McpProtocolVersionGateFixture>
{
	// The revision that introduced the cache hints — the ONLY one allowed to carry them.
	const string July2026 = "2026-07-28";

	readonly McpProtocolVersionGateFixture _fx;

	public McpProtocolVersionGateTests(McpProtocolVersionGateFixture fx) => _fx = fx;

	// The three fields 2026-07-28 added to a tools/list result. Absent verbatim on older revisions.
	static readonly string[] July2026OnlyFields = ["ttlMs", "cacheScope", "resultType"];

	// 2026-07-28 carries the cache hints…
	[Fact]
	public async Task ToolsList_On_July2026_Carries_CacheHints()
	{
		var result = await ListTools(July2026);

		foreach (var field in July2026OnlyFields)
			result.TryGetProperty(field, out _).Should().BeTrue(
				$"'{field}' is part of the {July2026} tools/list result and must survive our tools/list filters");

		// Pin the shapes, so "present" cannot degrade into "present but null/garbage".
		result.GetProperty("ttlMs").ValueKind.Should().Be(JsonValueKind.Number);
		result.GetProperty("cacheScope").ValueKind.Should().Be(JsonValueKind.String);
		result.GetProperty("resultType").ValueKind.Should().Be(JsonValueKind.String);
	}

	// …and every older revision must not see them at all (csharp-sdk #1721).
	[Theory]
	[InlineData("2025-11-25")]
	[InlineData("2025-06-18")]
	[InlineData("2024-11-05")]
	public async Task ToolsList_On_Older_Revisions_Omits_CacheHints(string protocolVersion)
	{
		var result = await ListTools(protocolVersion);

		foreach (var field in July2026OnlyFields)
			result.TryGetProperty(field, out _).Should().BeFalse(
				$"'{field}' is a {July2026} field: emitting it to a client on {protocolVersion} is csharp-sdk " +
				"issue #1721 — the version gate belongs to the SDK and must never be bypassed by our filters");
	}

	// ── wire plumbing ──────────────────────────────────────────────────────────
	// Returns the `result` object of a tools/list served at the given revision.
	async Task<JsonElement> ListTools(string protocolVersion)
	{
		// 2026-07-28 is stateless: it selects its revision through per-request _meta. Older
		// revisions negotiate through the initialize handshake instead and REJECT _meta selection
		// with -32022, so the two paths are not interchangeable.
		if (protocolVersion != July2026)
		{
			var init = await Rpc("initialize", protocolVersion, new
			{
				protocolVersion,
				capabilities = new { },
				clientInfo = new { name = "petbox-version-gate-test", version = "1.0" },
			});
			init.TryGetProperty("result", out var initResult).Should().BeTrue(
				$"the {protocolVersion} initialize handshake must succeed: {init.GetRawText()}");
			initResult.GetProperty("protocolVersion").GetString().Should().Be(protocolVersion,
				"the server must negotiate the revision the client asked for");
		}

		var list = await Rpc("tools/list", protocolVersion);
		list.TryGetProperty("result", out var result).Should().BeTrue(
			$"tools/list at {protocolVersion} must succeed: {list.GetRawText()}");

		// Guard against a vacuous pass: an empty/failed listing would satisfy every
		// "field is absent" assertion above without proving anything.
		result.GetProperty("tools").EnumerateArray().Should().NotBeEmpty(
			"the listing must actually contain tools for the absence assertions to mean anything");

		return result;
	}

	async Task<JsonElement> Rpc(string method, string protocolVersion, object? prms = null)
	{
		var p = JsonSerializer.SerializeToNode(prms ?? new { })!.AsObject();
		if (protocolVersion == July2026 && method != "initialize")
		{
			p["_meta"] = new JsonObject
			{
				["io.modelcontextprotocol/protocolVersion"] = protocolVersion,
				["io.modelcontextprotocol/clientCapabilities"] = new JsonObject(),
			};
		}

		var body = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method, @params = p });

		using var req = new HttpRequestMessage(HttpMethod.Post, "/mcp")
		{
			Content = new StringContent(body, Encoding.UTF8, "application/json"),
		};
		req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
		req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
		req.Headers.TryAddWithoutValidation("MCP-Protocol-Version", protocolVersion);
		// Mandatory from 2026-07-28 on (RequiresStandardHeaders); ignored by older revisions.
		req.Headers.TryAddWithoutValidation("Mcp-Method", method);

		var res = await _fx.Http.SendAsync(req);
		var text = await res.Content.ReadAsStringAsync();

		// The transport answers as SSE — unwrap the first `data:` frame.
		if (text.Contains("data:", StringComparison.Ordinal))
		{
			var line = text.Split('\n').FirstOrDefault(l => l.StartsWith("data:", StringComparison.Ordinal));
			if (line is not null) text = line["data:".Length..].Trim();
		}

		return JsonDocument.Parse(text).RootElement.Clone();
	}
}
