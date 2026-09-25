using System.Text.Json;
using System.Text.RegularExpressions;
using LinqToDB;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Tests.Support;
using PetBox.Web.Mcp;

namespace PetBox.Tests.Mcp;

// The runtime half of spec mcp-scope-declared-once / mcp-tool-visibility-by-scope / mcp-whoami: a
// real host, real keys with chosen scope sets, real tools/list and tools/call over /mcp.
//
// History: work `mcp-tools-list-ignores-key-scopes` found that tools/list was trimmed by a
// hand-maintained prefix table (McpToolScopeFilter.ModuleOf) that had fallen behind the real gates
// (AssertScope calls in each tool body). Work `mcp-scope-declaration-visibility-catalog` replaced
// both with ONE per-tool declaration ([RequiresScope] & co., read by McpToolScopes) that the call
// gate, the list trim and whoami's catalog all read. This file proves those readers agree:
//
//   * DIRECT sweep  — every tool tools/list shows a key is invoked and must NOT be refused on scope.
//   * MIRROR sweep  — every tool tools/list HIDES from that key is invoked and MUST be refused, and
//                     refused exactly on the scope axis (not on tenant, arguments or existence).
//   * EQUIVALENCE   — the scopes each tool enforces, discovered empirically, are exactly the ones its
//                     body enforced before the declaration existed (McpScopeGateBaseline).
public sealed class McpScopeProbeHost : IAsyncLifetime
{
	public const string Workspace = "mcpscopefilter-ws";
	public const string ProjectKey = "mcpscopefilter";

	readonly WebApplicationFactory<Program> _factory;
	readonly Dictionary<string, McpClient> _clients = new(StringComparer.Ordinal);
	readonly Dictionary<string, IReadOnlyList<string>> _listings = new(StringComparer.Ordinal);
	readonly SemaphoreSlim _gate = new(1, 1);
	HttpClient _http = null!;
	int _keys;

	public McpScopeProbeHost()
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
				// Every module ON: a sweep only covers a tool family if its type actually registered
				// and its feature gate does not answer first.
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

	// The server's canonical tool set (full descriptions, every tool — nothing trimmed).
	public IReadOnlyList<McpServerTool> AllTools { get; private set; } = [];

	public async ValueTask InitializeAsync()
	{
		var cs = _factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("PetBox")!;
		TestSchema.Core(cs);
		using (var scope = _factory.Services.CreateScope())
		{
			using var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open();
			await db.InsertAsync(new Workspace { Key = Workspace, Name = "ScopeFilter", CreatedAt = DateTime.UtcNow });
			await db.InsertAsync(new Project { Key = ProjectKey, WorkspaceKey = Workspace, Name = "ScopeFilter" });
		}

		_http = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
		AllTools = [.. _factory.Services.GetServices<McpServerTool>().OrderBy(t => t.ProtocolTool.Name, StringComparer.Ordinal)];
	}

	public async ValueTask DisposeAsync()
	{
		foreach (var c in _clients.Values) await c.DisposeAsync();
		_http.Dispose();
		await _factory.DisposeAsync();
		_gate.Dispose();
	}

	static string Id(IEnumerable<string> scopes) => string.Join(",", scopes.Order(StringComparer.Ordinal));

	// One minted key (and one MCP session) per distinct scope set, reused across tests.
	public async Task<McpClient> ClientFor(IEnumerable<string> scopes)
	{
		var id = Id(scopes);
		await _gate.WaitAsync();
		try
		{
			if (_clients.TryGetValue(id, out var existing)) return existing;
			var key = $"yb_key_mcpscopefilter_{_keys++}";
			using (var scope = _factory.Services.CreateScope())
			{
				using var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open();
				await db.InsertAsync(new ApiKey
				{
					Key = key,
					ProjectKey = ProjectKey,
					Scopes = id,
					Name = "scope probe " + id,
					CreatedAt = DateTime.UtcNow,
				});
			}

			var transport = new HttpClientTransport(new HttpClientTransportOptions
			{
				Endpoint = new Uri(_http.BaseAddress!, "/mcp"),
				AdditionalHeaders = new Dictionary<string, string> { ["X-Api-Key"] = key },
			}, _http);
			var client = await McpTestClient.ConnectAsync(transport);
			_clients[id] = client;
			return client;
		}
		finally
		{
			_gate.Release();
		}
	}

	public async Task<IReadOnlyList<string>> ListedFor(IReadOnlyCollection<string> scopes)
	{
		var id = Id(scopes);
		if (_listings.TryGetValue(id, out var cached)) return cached;
		var client = await ClientFor(scopes);
		var names = (await client.ListToolsAsync()).Select(t => t.Name).Order(StringComparer.Ordinal).ToList();
		_listings[id] = names;
		return names;
	}

	// Invoke `tool` as a key holding exactly `scopes`. Returns null on success, otherwise the error
	// envelope's (type, message) — McpErrorEnvelopeFilter's {"error":{"type","message"}} shape — or
	// ("(protocol)", …) when the SDK itself refused before any filter ran.
	public async Task<(string Type, string Message)?> Call(
		IEnumerable<string> scopes, string tool, IReadOnlyDictionary<string, object?>? extra = null)
	{
		var client = await ClientFor(scopes);
		var schema = AllTools.Single(t => t.ProtocolTool.Name == tool).ProtocolTool.InputSchema;
		var args = ArgsFor(schema);
		foreach (var (name, value) in extra ?? new Dictionary<string, object?>()) args[name] = value;
		CallToolResult result;
		try
		{
			result = await client.CallToolAsync(tool, args);
		}
		catch (Exception ex)
		{
			return ("(protocol)", ex.GetType().Name + ": " + ex.Message);
		}

		if (result.IsError != true) return null;
		return ErrorOf(string.Join(" ", result.Content.OfType<TextContentBlock>().Select(c => c.Text)));
	}

	// `projectKey`/`workspaceKey` get the key's own tenant (so a TENANT refusal can never masquerade as
	// a scope one), every other REQUIRED property gets type-shaped garbage — same economy as
	// AuthzCrossTenantProbe.ArgumentsFor.
	static Dictionary<string, object?> ArgsFor(JsonElement schema)
	{
		var args = new Dictionary<string, object?>(StringComparer.Ordinal);
		if (schema.ValueKind != JsonValueKind.Object) return args;

		var required = schema.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.Array
			? req.EnumerateArray().Select(e => e.GetString()).OfType<string>().ToHashSet(StringComparer.Ordinal)
			: [];

		if (schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
		{
			foreach (var property in properties.EnumerateObject())
			{
				if (property.Name == "projectKey") { args[property.Name] = ProjectKey; continue; }
				if (property.Name == "workspaceKey") { args[property.Name] = Workspace; continue; }
				if (required.Contains(property.Name)) args[property.Name] = GarbageFor(property.Value);
			}
		}

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
			// Not the envelope — report the raw text as unparsed.
		}

		return ("(unparsed)", text);
	}

	public static bool IsScopeRefusal((string Type, string Message)? error) =>
		error is { Type: "UnauthorizedAccessException" } e
		&& e.Message.Contains("lacks required scope", StringComparison.Ordinal);
}

public sealed class McpToolScopeFilterTests : IClassFixture<McpScopeProbeHost>
{
	readonly McpScopeProbeHost _host;
	public McpToolScopeFilterTests(McpScopeProbeHost host) => _host = host;

	// The restricted keys every sweep runs under. `memory:read` is the single READ-ONLY scope (a
	// read+write fixture would hide a write tool leaking to a read key); `tasks:read` is the case the
	// module-level table got wrong (it showed tasks_upsert & co.); `memory:read,memory:write` is the
	// scope set of the original report.
	public static TheoryData<string> RestrictedKeys => new() { "memory:read", "tasks:read", "memory:read,memory:write" };

	static string[] Scopes(string csv) => csv.Split(',');

	// Guard the guard: a filter that hid EVERYTHING, or returned the full list, would make every
	// sweep below pass vacuously or for the wrong reason.
	[Theory]
	[MemberData(nameof(RestrictedKeys))]
	public async Task Sanity_FilterActuallyTrimmedTheList(string scopes)
	{
		var listed = await _host.ListedFor(Scopes(scopes));
		listed.Should().NotBeEmpty();
		listed.Should().HaveCountLessThan(_host.AllTools.Count / 2,
			"a key holding one module's scopes must see a small slice of the surface");
		listed.Should().Contain(["whoami", "tool_describe", "share_revoke"],
			"tools that declare [RequiresNoScope] stay visible to every key");
	}

	// tools/list is EXACTLY the declared-and-satisfied set — no reader of its own.
	[Theory]
	[MemberData(nameof(RestrictedKeys))]
	public async Task Listing_IsExactlyTheToolsWhoseDeclarationTheKeySatisfies(string scopes)
	{
		var granted = new HashSet<string>(Scopes(scopes), StringComparer.Ordinal);
		var expected = _host.AllTools.Select(t => t.ProtocolTool.Name)
			.Where(n => McpToolScopes.Declared[n].IsSatisfiedBy(granted))
			.Order(StringComparer.Ordinal);
		(await _host.ListedFor(Scopes(scopes))).Should().Equal(expected);
	}

	[Fact]
	public async Task TasksRead_DoesNotSeeTheWriteVerbs()
	{
		var listed = await _host.ListedFor(["tasks:read"]);
		listed.Should().Contain(["tasks_search", "tasks_node_get", "comments_search", "session_get"]);
		listed.Should().NotContain(["tasks_upsert", "tasks_board_create", "comments_upsert", "relations_create",
			"session_append", "tasks_board_adopt", "tasks_methodology_rules_upsert"]);
	}

	[Fact]
	public async Task MemoryRead_DoesNotSeeTheWriteVerbs()
	{
		var listed = await _host.ListedFor(["memory:read"]);
		listed.Should().Contain(["memory_search", "memory_get"]);
		listed.Should().NotContain(["memory_upsert", "memory_remember", "memory_store_create", "search_reindex"]);
	}

	// The original bug's families, named so a fix that only half-works cannot pass by omission.
	[Theory]
	[InlineData("apikey_create")]
	[InlineData("apikey_list")]
	[InlineData("project_create")]
	[InlineData("llm_config_get")]
	[InlineData("llm_embed")]
	[InlineData("comments_upsert")]
	[InlineData("comments_search")]
	[InlineData("relations_list")]
	[InlineData("health_search")]
	[InlineData("tasks_search")]
	[InlineData("data_query")]
	[InlineData("deploy_list")]
	[InlineData("config_binding_search")]
	[InlineData("log_query")]
	public async Task KnownFamilies_AreGatedForAMemoryKey(string tool) =>
		(await _host.ListedFor(["memory:read", "memory:write"])).Should().NotContain(tool);

	[Fact]
	public async Task AdminProvision_StillSeesEverything() =>
		(await _host.ListedFor(["admin:provision"])).Should().HaveCount(_host.AllTools.Count,
			"an admin:provision key can mint itself any scope, so hiding tools from it saves nothing — the "
			+ "fail-open branch stays (reserve's advice on the idea)");

	// DIRECT: a listed tool is never refused on the scope axis.
	[Theory]
	[MemberData(nameof(RestrictedKeys))]
	public async Task EveryVisibleTool_InvokesPastTheScopeCheck(string scopes)
	{
		var violations = new List<string>();
		foreach (var tool in await _host.ListedFor(Scopes(scopes)))
		{
			var error = await _host.Call(Scopes(scopes), tool, DirectSweepArgs(tool, Scopes(scopes)));
			if (McpScopeProbeHost.IsScopeRefusal(error)) violations.Add($"{tool}: {error!.Value.Message}");
		}

		violations.Should().BeEmpty(
			$"tools/list must never show a [{scopes}] key a tool whose invocation it then refuses on scope:\n"
			+ string.Join("\n", violations));
	}

	// search_reindex is listed on its any-of FLOOR (memory:write OR tasks:write); its default `tier`
	// (all) then asks the body for EVERY enabled tier's write scope, which a single-module key does not
	// hold. The fair direct question is "can this key use the tool at all", so the sweep picks the
	// tier the key's scope covers — the one argument-dependent tool on the surface.
	static IReadOnlyDictionary<string, object?>? DirectSweepArgs(string tool, string[] scopes) =>
		tool == "search_reindex"
			? new Dictionary<string, object?> { ["tier"] = scopes.Contains("memory:write") ? "memory" : "tasks" }
			: null;

	// MIRROR: an unlisted tool is refused — and refused ON SCOPE, naming a scope the declaration
	// requires and the key lacks. Hiding is not the boundary; this proves the gate still is.
	[Theory]
	[MemberData(nameof(RestrictedKeys))]
	public async Task EveryHiddenTool_IsRefusedExactlyOnTheScopeCheck(string scopes)
	{
		var granted = new HashSet<string>(Scopes(scopes), StringComparer.Ordinal);
		var listed = (await _host.ListedFor(Scopes(scopes))).ToHashSet(StringComparer.Ordinal);
		var hidden = _host.AllTools.Select(t => t.ProtocolTool.Name).Where(n => !listed.Contains(n)).ToList();
		hidden.Should().NotBeEmpty();

		var violations = new List<string>();
		foreach (var tool in hidden)
		{
			var error = await _host.Call(Scopes(scopes), tool);
			if (!McpScopeProbeHost.IsScopeRefusal(error))
			{
				violations.Add($"{tool}: {(error is { } e ? e.Type + ": " + e.Message : "SUCCEEDED")}");
				continue;
			}

			var named = Regex.Match(error!.Value.Message, "lacks required scope '([^']+)'").Groups[1].Value;
			var requirement = McpToolScopes.Declared[tool];
			if (granted.Contains(named) || !requirement.Scopes.Contains(named))
				violations.Add($"{tool}: refused naming '{named}', declared {string.Join("+", requirement.Scopes)}");
		}

		violations.Should().BeEmpty(
			$"every tool hidden from a [{scopes}] key must be refused by the scope gate, naming a declared scope "
			+ "the key lacks:\n" + string.Join("\n", violations));
	}

	// tool_describe reads the canonical ToolCollection, not the trimmed listing — so it still
	// describes a tool this key cannot see (the whoami catalog names it; this explains it).
	[Fact]
	public async Task ToolDescribe_DescribesAHiddenTool()
	{
		(await _host.ListedFor(["memory:read"])).Should().NotContain("tasks_upsert");
		var client = await _host.ClientFor(["memory:read"]);
		var result = await client.CallToolAsync("tool_describe", new Dictionary<string, object?> { ["name"] = "tasks_upsert" });
		result.IsError.Should().NotBe(true);
		result.StructuredContent!.Value.GetProperty("name").GetString().Should().Be("tasks_upsert");
	}

	[Fact]
	public async Task WhoAmI_CatalogsTheWholeSurface_WithGrantedFlags()
	{
		var client = await _host.ClientFor(["memory:read"]);
		var result = await client.CallToolAsync("whoami", new Dictionary<string, object?>());
		result.IsError.Should().NotBe(true);
		var root = result.StructuredContent!.Value;

		// existing fields unchanged
		root.GetProperty("project").GetString().Should().Be(McpScopeProbeHost.ProjectKey);
		root.GetProperty("scopes").EnumerateArray().Select(e => e.GetString()).Should().Equal("memory:read");

		var modules = root.GetProperty("modules").EnumerateArray().ToDictionary(
			m => m.GetProperty("module").GetString()!,
			m => (Scopes: m.GetProperty("scopes").EnumerateArray()
					.ToDictionary(s => s.GetProperty("scope").GetString()!, s => s.GetProperty("granted").GetBoolean()),
				Tools: m.GetProperty("tools").EnumerateArray().Select(t => t.GetString()!).ToList()),
			StringComparer.Ordinal);

		// Every registered tool is named somewhere — the hidden ones above all.
		modules.Values.SelectMany(m => m.Tools).Distinct().Should()
			.BeEquivalentTo(_host.AllTools.Select(t => t.ProtocolTool.Name));

		modules["Memory"].Scopes.Should().Contain(new KeyValuePair<string, bool>("memory:read", true))
			.And.Contain(new KeyValuePair<string, bool>("memory:write", false));
		modules["Memory"].Tools.Should().Contain(["memory_search", "memory_upsert", "search_reindex"]);
		modules["Logs"].Scopes.Should().Contain(new KeyValuePair<string, bool>("logs:query", false));
		modules["Logs"].Tools.Should().Contain(["log_query", "log_list"]);
		modules["Tasks"].Scopes.Keys.Should().Contain(["tasks:read", "tasks:write", "tasks:approve", "methodology:write"]);
		modules["Tasks"].Tools.Should().Contain(["tasks_upsert", "tasks_board_adopt", "search_reindex"]);
		modules["Core"].Scopes.Should().BeEmpty();
		modules["Core"].Tools.Should().BeEquivalentTo(
			["whoami", "tool_describe", "share_revoke", "petbox_report_issue", "petbox_report_issue_status"]);
	}
}

// NO TOOL LOST A SCOPE CHECK (work mcp-scope-declaration-visibility-catalog).
//
// The declaration replaced ~100 unconditional ModuleMcp.AssertScope calls in tool bodies. Before any
// of them was deleted, the scopes every tool ENFORCED were DISCOVERED EMPIRICALLY on the pre-change
// code (origin/main 7a803914) and frozen below: start from a key with no scopes, call the tool, and
// while the answer is "ApiKey lacks required scope 'X'", mint a key that also holds X and call
// again. The sequence of X's is what that tool enforced for that call. This test reruns the same
// discovery on the current code and requires the SAME sequence for every tool — so a scope removed
// from a declaration (or a declaration looser than the retired AssertScope) fails here by name.
//
// Changing an entry is legitimate only when a tool's scope requirement is being changed ON PURPOSE;
// do it in the same commit, and say why in its message. A NEW tool needs no entry — it is checked
// against its own declaration instead.
public sealed class McpScopeGateEquivalenceTests : IClassFixture<McpScopeProbeHost>
{
	readonly McpScopeProbeHost _host;
	public McpScopeGateEquivalenceTests(McpScopeProbeHost host) => _host = host;

	// Discovered at 7a803914 with every feature on and garbage arguments (the probe below). The
	// governance verbs' second scope and search_reindex's two tier scopes are ordinary body asserts
	// that the discovery walked through one refusal at a time.
	static readonly Dictionary<string, string[]> Baseline = new(StringComparer.Ordinal)
	{
		["apikey_create"] = ["admin:provision"],
		["apikey_delete"] = ["admin:provision"],
		["apikey_list"] = ["admin:provision"],
		["apikey_update"] = ["admin:provision"],
		["comments_delete"] = ["tasks:write"],
		["comments_delta"] = ["tasks:read"],
		["comments_get"] = ["tasks:read"],
		["comments_search"] = ["tasks:read"],
		["comments_upsert"] = ["tasks:write"],
		["config_binding_delete"] = ["config:write"],
		["config_binding_get"] = ["config:read"],
		["config_binding_search"] = ["config:read"],
		["config_binding_upsert"] = ["config:write"],
		["data_exec"] = ["data:write"],
		["data_query"] = ["data:read"],
		["data_schema_apply"] = ["data:schema"],
		["db_create"] = ["data:schema"],
		["db_delete"] = ["data:schema"],
		["db_describe"] = ["data:read"],
		["db_list"] = ["data:read"],
		["deploy_delete"] = ["deploy:write"],
		["deploy_list"] = ["deploy:read"],
		["deploy_move"] = ["deploy:write"],
		["deploy_node_delete"] = ["deploy:write"],
		["deploy_node_list"] = ["deploy:read"],
		["deploy_node_upsert"] = ["deploy:write"],
		["deploy_start"] = ["deploy:write"],
		["deploy_stop"] = ["deploy:write"],
		["deploy_upsert"] = ["deploy:write"],
		["health_search"] = ["health:read"],
		["llm_chat"] = ["llm:invoke"],
		["llm_config_get"] = ["llm:admin"],
		["llm_config_upsert"] = ["llm:admin"],
		["llm_embed"] = ["llm:invoke"],
		["llm_rerank"] = ["llm:invoke"],
		["log_create"] = ["logs:admin"],
		["log_delete"] = ["logs:admin"],
		["log_list"] = ["logs:query"],
		["log_query"] = ["logs:query"],
		["log_update"] = ["logs:admin"],
		["memory_delta"] = ["memory:read"],
		["memory_get"] = ["memory:read"],
		["memory_remember"] = ["memory:write"],
		["memory_search"] = ["memory:read"],
		["memory_store_create"] = ["memory:write"],
		["memory_store_delete"] = ["memory:write"],
		["memory_store_list"] = ["memory:read"],
		["memory_upsert"] = ["memory:write"],
		["petbox_report_issue"] = [],
		["petbox_report_issue_status"] = [],
		["project_create"] = ["admin:provision"],
		["project_list"] = ["admin:provision"],
		["relations_create"] = ["tasks:write"],
		["relations_delete"] = ["tasks:write"],
		["relations_list"] = ["tasks:read"],
		["search_reindex"] = ["memory:write", "tasks:write"],
		["session_append"] = ["tasks:write"],
		["session_delete"] = ["tasks:write"],
		["session_get"] = ["tasks:read"],
		["session_search"] = ["tasks:read"],
		["session_upsert"] = ["tasks:write"],
		["share_revoke"] = [],
		["tasks_board_adopt"] = ["tasks:write", "methodology:write"],
		["tasks_board_close"] = ["tasks:write", "methodology:write"],
		["tasks_board_create"] = ["tasks:write"],
		["tasks_board_delete"] = ["tasks:write", "methodology:write"],
		["tasks_board_list"] = ["tasks:read"],
		["tasks_board_reopen"] = ["tasks:write", "methodology:write"],
		["tasks_board_set_wire"] = ["tasks:write", "methodology:write"],
		["tasks_delta"] = ["tasks:read"],
		["tasks_methodology_active_get"] = ["tasks:read"],
		["tasks_methodology_close"] = ["tasks:write", "methodology:write"],
		["tasks_methodology_create"] = ["tasks:write", "methodology:write"],
		["tasks_methodology_get"] = ["tasks:read"],
		["tasks_methodology_guide"] = ["tasks:read"],
		["tasks_methodology_list"] = ["tasks:read"],
		["tasks_methodology_rules_get"] = ["tasks:read"],
		["tasks_methodology_rules_upsert"] = ["tasks:write", "methodology:write"],
		["tasks_methodology_set_active"] = ["tasks:write", "methodology:write"],
		["tasks_methodology_set_description"] = ["tasks:write"],
		["tasks_methodology_template_delete"] = ["tasks:write"],
		["tasks_methodology_template_get"] = ["tasks:read"],
		["tasks_methodology_template_list"] = ["tasks:read"],
		["tasks_methodology_template_snapshot"] = ["tasks:write"],
		["tasks_methodology_template_upsert"] = ["tasks:write"],
		["tasks_methodology_utility_get"] = ["tasks:read"],
		["tasks_methodology_utility_upsert"] = ["tasks:write", "methodology:write"],
		["tasks_node_get"] = ["tasks:read"],
		["tasks_observation_promote"] = ["tasks:write"],
		["tasks_owner_digest"] = ["tasks:read"],
		["tasks_recurring_delete"] = ["tasks:write"],
		["tasks_recurring_list"] = ["tasks:read"],
		["tasks_recurring_upsert"] = ["tasks:write"],
		["tasks_schedule_run"] = ["tasks:write"],
		["tasks_search"] = ["tasks:read"],
		["tasks_upsert"] = ["tasks:write"],
		["tasks_workflow"] = ["tasks:read"],
		["tool_describe"] = [],
		["whoami"] = [],
	};

	static readonly Regex Lacks = new("lacks required scope '([^']+)'", RegexOptions.Compiled);

	async Task<List<string>> Discover(string tool)
	{
		var held = new List<string>();
		for (var step = 0; step < 8; step++)
		{
			var error = await _host.Call(held, tool);
			if (!McpScopeProbeHost.IsScopeRefusal(error)) return held;
			held.Add(Lacks.Match(error!.Value.Message).Groups[1].Value);
		}

		throw new InvalidOperationException($"{tool}: scope discovery did not converge ({string.Join(",", held)})");
	}

	[Fact]
	public async Task EveryTool_EnforcesExactlyTheScopesItEnforcedBeforeTheDeclaration()
	{
		var mismatches = new List<string>();
		foreach (var tool in _host.AllTools.Select(t => t.ProtocolTool.Name))
		{
			var discovered = await Discover(tool);
			if (Baseline.TryGetValue(tool, out var before))
			{
				if (!discovered.SequenceEqual(before))
					mismatches.Add($"{tool}: enforced [{string.Join(", ", before)}] before, now [{string.Join(", ", discovered)}]");
			}
			else
			{
				// A tool newer than the baseline: its gate must enforce at least what it declares.
				var requirement = McpToolScopes.Declared[tool];
				var ok = requirement.Kind switch
				{
					McpScopeRequirementKind.All => requirement.Scopes.All(discovered.Contains),
					McpScopeRequirementKind.Any => requirement.Scopes.Any(discovered.Contains),
					_ => true,
				};
				if (!ok) mismatches.Add($"{tool}: declares {string.Join("+", requirement.Scopes)}, enforced [{string.Join(", ", discovered)}]");
			}
		}

		mismatches.Should().BeEmpty(
			"the scope each tool enforces must not change as a side effect of moving the check into the "
			+ "declaration — see the header of this class before editing the baseline:\n" + string.Join("\n", mismatches));
	}

	[Fact]
	public void TheBaseline_NamesOnlyToolsThatExist() =>
		Baseline.Keys.Except(_host.AllTools.Select(t => t.ProtocolTool.Name)).Should().BeEmpty(
			"a removed or renamed tool's baseline line is stale — delete it (or rename it with the tool)");
}
