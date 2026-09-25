using System.Text.Json;
using LinqToDB;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Tests.Support;

namespace PetBox.Tests.Mcp;

// work `mcp-tools-list-ignores-key-scopes`: McpToolScopeFilter trims tools/list to the modules a
// key's scopes grant, but the trim table (McpToolScopeFilter.ModuleOf) is hand-maintained and had
// fallen behind AssertScope reality for several families — apikey_*/project_* (an explicit "leave
// unclassified" comment that stopped matching what those tools actually require), and
// llm_*/comments_*/relations_*/health_* (never added when those tool families were). Calls were
// still REJECTED correctly at invocation (ModuleMcp.AssertScope), so this was never a security
// hole — but AGENTS.md promises "Each tool's visibility is gated by the calling key's scopes", and
// a restricted key saw ~34 tools it could never successfully call.
//
// THIS FILE is the generic regression guard the fix table itself asks for (see ModuleOf's own
// comment): it does not hardcode which tools are broken, it EMPIRICALLY invokes every tool
// tools/list shows a memory:read/memory:write-only key and fails if any of them answers the exact
// scope-axis refusal ModuleMcp.AssertScope raises. A new tool family added later that forgets a
// ModuleOf entry (or gets one wrong) reproduces the ORIGINAL bug shape and this test catches it in
// CI, without anyone having to remember to update a second list by hand.
public sealed class McpToolScopeFilterFixture : IAsyncLifetime
{
	public const string Workspace = "mcpscopefilter-ws";
	public const string ProjectKey = "mcpscopefilter";
	const string ApiKey = "yb_key_mcpscopefilter_agent";

	// The exact scope set the reporting card used: read + write on ONE module (memory), nothing
	// else — no admin:provision (which would make McpToolScopeFilter show everything, by its own
	// documented bypass), no tasks/llm/health/config/deploy/data/logs scope of any kind.
	const string RestrictedScopes = "memory:read,memory:write";

	readonly WebApplicationFactory<Program> _factory;
	HttpClient _http = null!;
	McpClient _mcp = null!;

	public IReadOnlyList<McpClientTool> Tools { get; private set; } = null!;

	public McpToolScopeFilterFixture()
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
				// Every module ON: the sweep only covers a tool family if its type actually
				// registered. A module left off would make that family's escape invisible here,
				// not fixed — the same mistake the original bug hid behind for llm_*/health_*.
				["Features:Config"] = "true",
				["Features:Logging"] = "true",
				["Features:Data"] = "true",
				["Features:Dashboard"] = "true",
				["Features:Tasks"] = "true",
				["Features:Memory"] = "true",
				["Features:LlmRouter"] = "true",
				["Features:Deploy"] = "true",
			}));
		});
	}

	public async ValueTask InitializeAsync()
	{
		var cs = _factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("PetBox")!;
		TestSchema.Core(cs);
		using (var scope = _factory.Services.CreateScope())
		{
			using var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open();
			await db.InsertAsync(new Workspace { Key = Workspace, Name = "ScopeFilter", CreatedAt = DateTime.UtcNow });
			await db.InsertAsync(new Project { Key = ProjectKey, WorkspaceKey = Workspace, Name = "ScopeFilter" });
			await db.InsertAsync(new ApiKey
			{
				Key = ApiKey,
				ProjectKey = ProjectKey,
				Scopes = RestrictedScopes,
				Name = "scope-filter probe",
				CreatedAt = DateTime.UtcNow,
			});
		}

		_http = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
		var transport = new HttpClientTransport(new HttpClientTransportOptions
		{
			Endpoint = new Uri(_http.BaseAddress!, "/mcp"),
			AdditionalHeaders = new Dictionary<string, string> { ["X-Api-Key"] = ApiKey },
		}, _http);
		_mcp = await McpTestClient.ConnectAsync(transport);
		Tools = [.. await _mcp.ListToolsAsync()];
	}

	public async ValueTask DisposeAsync()
	{
		await _mcp.DisposeAsync();
		_http.Dispose();
		await _factory.DisposeAsync();
	}
}

public sealed class McpToolScopeFilterTests : IClassFixture<McpToolScopeFilterFixture>
{
	readonly McpToolScopeFilterFixture _fx;
	public McpToolScopeFilterTests(McpToolScopeFilterFixture fx) => _fx = fx;

	// Guard the guard: a filter that (by a future bug) hid EVERYTHING, or an unfiltered full list,
	// would make every assertion below pass vacuously or for the wrong reason.
	[Fact]
	public void Sanity_FilterActuallyTrimmedTheList()
	{
		_fx.Tools.Should().NotBeEmpty("a memory:read/memory:write key must see at least its own tools");
		_fx.Tools.Should().HaveCountLessThan(40,
			"a key holding exactly one module's scopes must see a small slice of the ~99-tool surface — "
			+ "a count this high means the filter stopped trimming");
		_fx.Tools.Select(t => t.Name).Should().Contain("memory_search",
			"the key's own module must still be visible");
	}

	// THE ORIGINAL BUG, named explicitly so a fix that only half-works cannot pass by omission.
	// Every one of these requires a scope this key does not hold and must NOT be listed.
	[Theory]
	[InlineData("apikey_create")]
	[InlineData("apikey_list")]
	[InlineData("apikey_update")]
	[InlineData("apikey_delete")]
	[InlineData("project_create")]
	[InlineData("project_list")]
	[InlineData("llm_config_get")]
	[InlineData("llm_config_upsert")]
	[InlineData("llm_embed")]
	[InlineData("llm_rerank")]
	[InlineData("llm_chat")]
	[InlineData("comments_upsert")]
	[InlineData("comments_search")]
	[InlineData("comments_delta")]
	[InlineData("comments_get")]
	[InlineData("comments_delete")]
	[InlineData("relations_create")]
	[InlineData("relations_list")]
	[InlineData("relations_delete")]
	[InlineData("health_search")]
	// Sampled from families that were ALREADY correctly gated before this fix (tasks/data/deploy/
	// config/logs) — this memory-only key holds none of their scopes either, so the theory also
	// proves the fix did not accidentally show MORE than before while closing the escape.
	[InlineData("tasks_search")]
	[InlineData("data_query")]
	[InlineData("deploy_list")]
	[InlineData("config_binding_search")]
	[InlineData("log_query")]
	public void KnownFamilies_AreCorrectlyGated(string tool)
	{
		_fx.Tools.Select(t => t.Name).Should().NotContain(tool,
			$"{tool} requires a scope this memory:read/memory:write-only key does not hold");
	}

	// share_revoke needs no scope AT ALL by design (see ShareTools' header) — it must stay visible to
	// every authenticated key, this one included. Pinned so a future "fix" cannot accidentally start
	// hiding it under a module it was never meant to belong to.
	[Fact]
	public void ScopeFreeTools_StayVisible()
	{
		_fx.Tools.Select(t => t.Name).Should().Contain(["whoami", "tool_describe", "share_revoke"]);
	}

	// THE GENERIC SWEEP. Every tool tools/list shows this restricted key is actually INVOKED (required
	// arguments filled with type-shaped garbage, `projectKey`/`workspaceKey` filled with the key's own
	// project so the TENANT axis never confounds the result) and the response is checked for the exact
	// refusal ModuleMcp.AssertScope raises. No hardcoded tool list on the failing side — a new tool
	// family that reaches tools/list without a matching ModuleOf entry fails HERE, generically.
	[Fact]
	public async Task EveryVisibleTool_InvokesPastTheScopeCheck()
	{
		var violations = new List<string>();
		var inconclusive = new List<string>();

		foreach (var tool in _fx.Tools)
		{
			var args = ArgsFor(tool.ProtocolTool.InputSchema, tool.Name);

			CallToolResult result;
			try
			{
				result = await tool.CallAsync(args);
			}
			catch (Exception ex)
			{
				// A protocol-level failure (the SDK's own argument binder refusing a required
				// parameter we could not shape) never reaches the tool body, so it is not a scope
				// decision either way — recorded, not treated as a pass or a violation.
				inconclusive.Add($"{tool.Name}: {ex.GetType().Name}: {ex.Message}");
				continue;
			}

			if (result.IsError != true) continue; // succeeded outright — definitely past the scope check

			var text = string.Join(" ", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
			var (type, message) = ErrorOf(text);
			if (type == "UnauthorizedAccessException" && message.Contains("lacks required scope", StringComparison.Ordinal))
				violations.Add($"{tool.Name}: {message}");
		}

		violations.Should().BeEmpty(
			"tools/list must never show a tool whose invocation then fails ModuleMcp.AssertScope for a "
			+ "memory:read/memory:write-only key — each name below is visible in tools/list but was refused "
			+ "on the scope axis when actually called:\n" + string.Join("\n", violations)
			+ (inconclusive.Count > 0
				? "\n\n(inconclusive, protocol-level, not counted either way:\n" + string.Join("\n", inconclusive) + ")"
				: ""));
	}

	// The argument set for one tool call: `projectKey`/`workspaceKey` get the key's own project (so a
	// tenant refusal can never masquerade as a scope refusal), every other REQUIRED property gets
	// type-shaped garbage — same economy as AuthzCrossTenantProbe.ArgumentsFor, which established that
	// a default-deny/refusal-shaped surface cannot tell a well-formed call from a malformed one.
	static Dictionary<string, object?> ArgsFor(JsonElement schema, string toolName)
	{
		var args = new Dictionary<string, object?>(StringComparer.Ordinal);
		if (schema.ValueKind != JsonValueKind.Object) return args;

		var required = schema.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.Array
			? req.EnumerateArray().Select(e => e.GetString()).Where(s => s is not null).ToHashSet(StringComparer.Ordinal)!
			: new HashSet<string?>(StringComparer.Ordinal);

		if (schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
		{
			foreach (var property in properties.EnumerateObject())
			{
				if (property.Name is "projectKey" or "workspaceKey")
				{
					args[property.Name] = McpToolScopeFilterFixture.ProjectKey;
					continue;
				}

				if (required.Contains(property.Name)) args[property.Name] = GarbageFor(property.Value);
			}
		}

		// search_reindex's scope requirement is ARGUMENT-DEPENDENT, not a fixed per-tool scope: the
		// default (omitted `tier`, meaning "all") resets every ENABLED tier and asserts write on each
		// one it touches, so with both Tasks and Memory features on it legitimately needs
		// tasks:write AND memory:write together — a memory-only key genuinely cannot run the default.
		// It CAN run tier:"memory" alone, which is the fair question this sweep asks: can a
		// memory:read/write key use this tool AT ALL, not can it use every mode of it. Pinning this one
		// non-required argument keeps the rest of the sweep fully generic.
		if (toolName == "search_reindex") args["tier"] = "memory";

		return args;
	}

	static object GarbageFor(JsonElement property)
	{
		var type = property.TryGetProperty("type", out var t) ? TypeName(t) : null;
		return type switch
		{
			"integer" or "number" => 0,
			"boolean" => false,
			"array" => Array.Empty<object>(),
			"object" => new Dictionary<string, object?>(StringComparer.Ordinal),
			_ => "petbox-probe",
		};
	}

	static string? TypeName(JsonElement type) => type.ValueKind switch
	{
		JsonValueKind.String => type.GetString(),
		JsonValueKind.Array => type.EnumerateArray()
			.Select(e => e.GetString())
			.FirstOrDefault(s => s is not null and not "null"),
		_ => null,
	};

	// Same envelope shape McpErrorEnvelopeFilter writes: {"error":{"type":...,"message":...}}.
	static (string Type, string Message) ErrorOf(string text)
	{
		try
		{
			using var doc = JsonDocument.Parse(text);
			if (doc.RootElement.TryGetProperty("error", out var error))
				return (error.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "",
					error.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "");
		}
		catch (JsonException)
		{
			// Not the envelope — fall through and report the raw text as unparsed.
		}

		return ("(unparsed)", text);
	}
}
