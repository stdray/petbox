using System.Text.Json;
using Json.Schema;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Tasks.Data;

namespace PetBox.Tests.Mcp;

// Exhaustive strict-client conformance guard (chore mcp-conformance-exhaustive; directive
// "загнать всё в валидацию, чтобы ловить до строгого клиента").
//
// A strict MCP client (opencode/DeepSeek) validates a tool result the same way for EVERY
// tool: it is acceptable iff
//   (isError == true)  OR  (structuredContent is present AND conforms to the declared outputSchema).
// The failure we keep re-hitting is the third state — a declared outputSchema, no isError, and
// NO structuredContent (or a non-conforming one): the client then rejects with -32600/-32602.
// Two historic instances, both fixed & locked here:
//   -32602: nullable props marked `required` (McpOutputSchema.NullableAware).
//   -32600: a nullable get returning null → no structuredContent (mcp-nullable-get-strict-32600).
//
// This suite enforces that NO tool escapes validation:
//   1. Coverage gate: every tool that declares an outputSchema is EITHER exercised below OR
//      in the explicit Excluded map with a reason. A new tool fails CI until it is handled.
//   2. Success battery: reads/lists/gets + the writes that seed them — assert NOT isError and
//      structuredContent conforms (the happy path a strict client validates).
//   3. Edge battery: not-found / delete-missing branches — assert the universal strict-client
//      property (isError OR conforms). This is exactly where the -32600 class lives.
// Shared per-class host for the conformance battery (xUnit news the test class per test,
// so without this fixture all three tests boot their own WebApplicationFactory — with
// EVERY feature enabled, one of the most expensive hosts in the suite). No per-test reset
// is needed: the coverage gate is read-only, the success battery seeds its entities exactly
// once, and the edge battery only probes not-found branches (isError either way).
public sealed class McpOutputSchemaConformanceFixture : IAsyncLifetime
{
	public const string ProjectKey = "conf";
	public const string ApiKey = "yb_key_conf_agent";
	// Full enumerated scope set — so scope-gating never turns a covered read into an isError.
	const string Scopes =
		"config:read,config:write,logs:ingest,logs:query,logs:admin,health:read,health:write," +
		"data:read,data:write,data:schema,tasks:read,tasks:write,memory:read,memory:write," +
		"llm:invoke,llm:admin,deploy:read,deploy:write,agent:poll,agent:heartbeat,admin:provision";

	readonly string _baseDir;
	readonly WebApplicationFactory<Program> _factory;
	HttpClient _http = null!;
	McpClient _mcp = null!;

	public IReadOnlyDictionary<string, McpClientTool> Tools { get; private set; } = null!;

	public McpOutputSchemaConformanceFixture()
	{
		_baseDir = Path.Combine(Path.GetTempPath(), "petbox-conf-" + Guid.NewGuid().ToString("N"));
		Environment.SetEnvironmentVariable("PETBOX_MASTER_KEY", "test-key-for-secrets");
		Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");

		_factory = new WebApplicationFactory<Program>()
			.WithWebHostBuilder(b =>
			{
				b.UseEnvironment("Testing");
				b.ConfigureAppConfiguration((_, cfg) =>
				{
					cfg.AddInMemoryCollection(new Dictionary<string, string?>
					{
						["ConnectionStrings:PetBox"] = $"Data Source={Path.Combine(Path.GetTempPath(), $"petbox-test-{Guid.NewGuid():N}.db")};Cache=Shared",
						// Enable every feature so a covered read is never an isError just because
						// its subsystem was toggled off.
						["Features:Tasks"] = "true",
						["Features:Memory"] = "true",
						["Features:Logging"] = "true",
						["Features:Config"] = "true",
						["Features:Data"] = "true",
						["Features:Dashboard"] = "true",
						["Features:LlmRouter"] = "true",
						["Features:Deploy"] = "true",
					});
				});
				b.ConfigureServices(svc =>
				{
					var tasksFactory = svc.SingleOrDefault(d => d.ServiceType == typeof(IScopedDbFactory<TasksDb>));
					if (tasksFactory is not null) svc.Remove(tasksFactory);
					svc.AddSingleton<IScopedDbFactory<TasksDb>>(_ => new ScopedDbFactory<TasksDb>(
						Path.Combine(_baseDir, "tasks"), PetBox.Core.Settings.Scope.Project,
						cs => new TasksDb(TasksDb.CreateOptions(cs)), TasksSchema.Ensure));
				});
			});
	}

	public async Task InitializeAsync()
	{
		var cs = _factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("PetBox")!;
		TestSchema.Core(cs);

		using (var scope = _factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<PetBoxDb>();
			await db.ApiKeys.Where(k => k.Key == ApiKey).DeleteAsync();
			await db.Projects.Where(p => p.Key == ProjectKey).DeleteAsync();
			await db.Workspaces.Where(w => w.Key == "test").DeleteAsync();
			await db.InsertAsync(new Workspace { Key = "test", Name = "Test", CreatedAt = DateTime.UtcNow });
			await db.InsertAsync(new Project { Key = ProjectKey, WorkspaceKey = "test", Name = "Conf" });
			await db.InsertAsync(new ApiKey { Key = ApiKey, ProjectKey = ProjectKey, Scopes = Scopes, CreatedAt = DateTime.UtcNow });
		}

		_http = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
		_http.DefaultRequestHeaders.Add("X-Api-Key", ApiKey);
		var transport = new HttpClientTransport(new HttpClientTransportOptions
		{
			Endpoint = new Uri(_http.BaseAddress!, "/mcp"),
			AdditionalHeaders = new Dictionary<string, string> { ["X-Api-Key"] = ApiKey },
		}, _http);
		_mcp = await McpClient.CreateAsync(transport, cancellationToken: default);
		Tools = (await _mcp.ListToolsAsync()).ToDictionary(t => t.Name);
	}

	public async Task DisposeAsync()
	{
		await _mcp.DisposeAsync();
		_http.Dispose();
		await _factory.DisposeAsync();
		TestDirs.CleanupOrDefer(_baseDir);
	}
}

public sealed class McpOutputSchemaConformanceTests : IClassFixture<McpOutputSchemaConformanceFixture>
{
	const string ProjectKey = McpOutputSchemaConformanceFixture.ProjectKey;

	readonly IReadOnlyDictionary<string, McpClientTool> _tools;

	public McpOutputSchemaConformanceTests(McpOutputSchemaConformanceFixture fx)
	{
		_tools = fx.Tools;
	}

	// ── Explicit non-coverage, each with a reason. The coverage gate requires every
	// outputSchema tool to be here OR exercised in the battery. Two kinds of reason:
	//   external  — cannot succeed in-process (needs a live LLM endpoint / SSH to a fleet node).
	//   pending   — a write with complex/chained state or args not yet wired; TRACKED in the
	//               chore so it is enforced-visible, never silently missed.
	static readonly IReadOnlyDictionary<string, string> Excluded = new Dictionary<string, string>
	{
		// external: real infrastructure
		["llm_chat"] = "external: needs a live LLM endpoint",
		["llm_embed"] = "external: needs a live LLM endpoint",
		["llm_rerank"] = "external: needs a live LLM endpoint",
		["deploy_start"] = "external: SSHes to a fleet node",
		["deploy_stop"] = "external: SSHes to a fleet node",
		// pending: chained/complex state — tracked in mcp-conformance-exhaustive
		["session_append"] = "pending: message-array shape not yet wired",
		["tasks_board_set_spec"] = "pending: needs a spec board seeded",
		["tasks_methodology_enable"] = "pending: mutates board setup mid-battery",
		["tasks_methodology_def_upsert"] = "pending: full methodology-definition JSON",
		["config_binding_upsert"] = "pending: binding args not yet wired",
		["config_binding_delete"] = "pending: binding args not yet wired",
		["db_create"] = "pending: Data chain (create→schema→exec→query→describe)",
		["db_delete"] = "pending: Data chain",
		["db_describe"] = "pending: Data chain (needs a db+schema)",
		["data_schema_apply"] = "pending: Data chain",
		["data_query"] = "pending: Data chain (needs a db+table)",
		["data_exec"] = "pending: Data chain (needs a db+table)",
		["deploy_node_upsert"] = "pending: node args not yet wired",
		["deploy_upsert"] = "pending: deployment args not yet wired",
		["deploy_move"] = "pending: deployment+node args",
		["deploy_node_delete"] = "pending: needs a seeded node",
		["deploy_delete"] = "pending: needs a seeded deployment",
		["project_create"] = "pending: workspace/project args not yet wired",
		["relations_create"] = "pending: endpoint refs not yet wired",
		["apikey_create"] = "pending: mint args not yet wired",
		["apikey_delete"] = "pending: needs a minted key",
		["report_issue"] = "pending: issue args not yet wired",
		["llm_config_set"] = "pending: registry payload not yet wired",
	};

	// 1. COVERAGE GATE — nothing escapes. Every tool that declares an outputSchema is either
	// exercised in the battery below or explicitly Excluded with a reason.
	[Fact]
	public void Every_OutputSchema_Tool_Is_Covered_Or_Excluded()
	{
		var declared = _tools.Values
			.Where(t => t.ProtocolTool.OutputSchema is not null)
			.Select(t => t.Name)
			.ToHashSet();

		var covered = new HashSet<string>(SuccessTools.Concat(EdgeTools).Concat(Excluded.Keys));

		var uncovered = declared.Where(n => !covered.Contains(n)).OrderBy(n => n).ToList();
		uncovered.Should().BeEmpty(
			"every outputSchema tool must be exercised or Excluded-with-reason (add it to the battery " +
			"or Excluded):\n" + string.Join("\n", uncovered));

		// Hygiene: no stale names in our lists (a rename must not leave a dangling entry).
		var stale = covered.Where(n => !declared.Contains(n)).OrderBy(n => n).ToList();
		stale.Should().BeEmpty("battery/Excluded names must all be real outputSchema tools:\n" + string.Join("\n", stale));

		// Excluded and exercised are disjoint (a tool is one or the other, not both).
		Excluded.Keys.Intersect(SuccessTools.Concat(EdgeTools)).Should().BeEmpty();
	}

	// 2. SUCCESS BATTERY — seed writes + reads/lists/gets. Each MUST return a non-error
	// structuredContent that conforms to its declared outputSchema.
	[Fact]
	public async Task Success_Battery_StructuredContent_Conforms()
	{
		var failures = new List<string>();

		// ── seed (writes are validated as they run) ──
		await Ok(failures, "tasks_board_create", new { projectKey = ProjectKey, board = "work", kind = "simple" });
		await Ok(failures, "tasks_upsert", new { projectKey = ProjectKey, board = "work", nodes = Nodes(new { key = "a", status = "Todo", type = "task", title = "Alpha", body = "hello" }) });
		await Ok(failures, "memory_upsert", new { projectKey = ProjectKey, store = "notes", entries = Entries(new { key = "k", type = "project", description = "d", body = "b" }) });
		await Ok(failures, "memory_store_create", new { projectKey = ProjectKey, store = "extra" });
		await Ok(failures, "memory_remember", new { text = "a durable fact", projectKey = ProjectKey, store = "notes", type = "Project" });
		await Ok(failures, "session_upsert", new { projectKey = ProjectKey, sessionId = "s1", agent = "claude-code", content = "# plan" });
		await Ok(failures, "log_create", new { projectKey = ProjectKey, name = "audit" });

		// comments need a node id + a version threaded through create→edit→list.
		var created = await Call("comments_create", new { projectKey = ProjectKey, board = "work", nodeId = "a", author = "tester", body = "first" });
		Conforms(failures, "comments_create", created);
		var cid = created.StructuredContent?.GetProperty("id").GetString();
		var cver = created.StructuredContent?.GetProperty("currentVersion").GetInt64() ?? 0;
		if (cid is not null)
			await Ok(failures, "comments_edit", new { projectKey = ProjectKey, board = "work", id = cid, body = "edited", version = cver });
		await Ok(failures, "comments_list", new { projectKey = ProjectKey, board = "work", nodeId = "a" });

		// ── reads / lists / gets (present shapes) ──
		var reads = new (string Tool, object Args)[]
		{
			("tasks_search", new { projectKey = ProjectKey, board = "work" }),
			("tasks_board_list", new { projectKey = ProjectKey }),
			("tasks_workflow", new { projectKey = ProjectKey, board = "work" }),
			("tasks_delta", new { projectKey = ProjectKey, board = "work", sinceVersion = 0 }),
			("tasks_node_get", new { projectKey = ProjectKey, board = "work", node = "a" }),
			("tasks_methodology_get", new { projectKey = ProjectKey }),
			("tasks_methodology_guide", new { projectKey = ProjectKey }),
			("tasks_methodology_def_get", new { projectKey = ProjectKey }),
			("memory_search", new { projectKey = ProjectKey }),
			("memory_store_list", new { projectKey = ProjectKey, includeUsage = true }),
			("memory_delta", new { projectKey = ProjectKey, store = "notes", sinceVersion = 0 }),
			("memory_get", new { projectKey = ProjectKey, store = "notes", key = "k" }),
			("session_search", new { projectKey = ProjectKey }),
			("session_get", new { projectKey = ProjectKey, sessionId = "s1" }),
			("config_binding_list", new { workspaceKey = "test" }),
			("log_list", new { projectKey = ProjectKey }),
			("log_query", new { projectKey = ProjectKey, logName = "audit", kql = "events | take 10" }),
			("health_search", new { projectKey = ProjectKey }),
			("deploy_list", new { projectKey = ProjectKey }),
			("deploy_node_list", new { projectKey = ProjectKey }),
			("project_list", new { projectKey = ProjectKey }),
			("relations_list", new { projectKey = ProjectKey, nodeId = "a" }),
			("llm_config_get", new { projectKey = ProjectKey }),
			("apikey_list", new { projectKey = ProjectKey }),
			("db_list", new { projectKey = ProjectKey }),
			("whoami", new { }),
		};
		foreach (var (tool, args) in reads)
			await Ok(failures, tool, args);

		failures.Should().BeEmpty("success structuredContent must conform:\n" + string.Join("\n", failures));
	}

	// 3. EDGE BATTERY — not-found / delete-missing branches. The universal strict-client
	// property must hold: isError OR structuredContent conforms (never the -32600 shape).
	[Fact]
	public async Task Edge_Battery_Is_StrictClient_Ok()
	{
		var failures = new List<string>();

		var edge = new (string Tool, object Args)[]
		{
			// not-found on get-by-id: MUST be isError (this is the -32600 class fixed by
			// mcp-nullable-get-strict-32600) — never a null-structured success.
			("memory_get", new { projectKey = ProjectKey, store = "notes", key = "no-such-key" }),
			("session_get", new { projectKey = ProjectKey, sessionId = "no-such-session" }),
			("tasks_node_get", new { projectKey = ProjectKey, board = "work", node = "no-such-node" }),
			// delete on a missing id: either a conformant {deleted:false} or an isError — both
			// are strict-client-ok; the null-structured-success shape is what we forbid.
			("session_delete", new { projectKey = ProjectKey, sessionId = "no-such-session" }),
			("memory_store_delete", new { projectKey = ProjectKey, store = "no-such-store" }),
			("log_delete", new { projectKey = ProjectKey, name = "no-such-log" }),
			("relations_delete", new { projectKey = ProjectKey, id = "no-such-relation" }),
			("comments_delete", new { projectKey = ProjectKey, board = "work", id = "no-such-comment" }),
			("tasks_board_close", new { projectKey = ProjectKey, board = "no-such-board" }),
			("tasks_board_reopen", new { projectKey = ProjectKey, board = "no-such-board" }),
			("tasks_board_delete", new { projectKey = ProjectKey, board = "no-such-board" }),
		};
		foreach (var (tool, args) in edge)
			await StrictOk(failures, tool, args);

		failures.Should().BeEmpty("edge branches must be strict-client-ok (isError OR conforms):\n" + string.Join("\n", failures));
	}

	// Names exercised for success (seed writes + reads) — the coverage-gate source of truth.
	static readonly string[] SuccessTools =
	{
		"tasks_board_create", "tasks_upsert", "memory_upsert", "memory_store_create", "memory_remember",
		"session_upsert", "log_create", "comments_create", "comments_edit", "comments_list",
		"tasks_search", "tasks_board_list", "tasks_workflow", "tasks_delta", "tasks_node_get",
		"tasks_methodology_get", "tasks_methodology_guide", "tasks_methodology_def_get",
		"memory_search", "memory_store_list", "memory_delta", "memory_get",
		"session_search", "session_get", "config_binding_list", "log_list", "log_query",
		"health_search", "deploy_list", "deploy_node_list", "project_list", "relations_list",
		"llm_config_get", "apikey_list", "db_list", "whoami",
	};

	// Names exercised for edge branches (delete-missing + not-found).
	static readonly string[] EdgeTools =
	{
		"session_delete", "memory_store_delete", "log_delete", "relations_delete", "comments_delete",
		"tasks_board_close", "tasks_board_reopen", "tasks_board_delete",
	};

	// ── assertion helpers ──────────────────────────────────────────────────────
	// Success: NOT isError, structuredContent present + conforms.
	async Task Ok(List<string> failures, string tool, object args)
	{
		var res = await Call(tool, args);
		if (res.IsError == true) { failures.Add($"{tool}: expected success, got isError: {Text(res)}"); return; }
		Conforms(failures, tool, res);
	}

	// Strict-client-ok: isError (never schema-validated) OR structuredContent present + conforms.
	async Task StrictOk(List<string> failures, string tool, object args)
	{
		var res = await Call(tool, args);
		if (res.IsError == true) return;
		if (res.StructuredContent is null)
			failures.Add($"{tool}: outputSchema declared but no structuredContent and not isError (the -32600 shape)");
		else
			Conforms(failures, tool, res);
	}

	void Conforms(List<string> failures, string tool, CallToolResult res)
	{
		var t = _tools[tool];
		if (t.ProtocolTool.OutputSchema is null) { failures.Add($"{tool}: no outputSchema declared"); return; }
		if (res.StructuredContent is null) { failures.Add($"{tool}: no structuredContent to validate"); return; }
		foreach (var err in Validate(t.ProtocolTool.OutputSchema.Value, res.StructuredContent.Value))
			failures.Add($"{tool}: {err}");
	}

	static IEnumerable<string> Validate(JsonElement schemaElement, JsonElement instance)
	{
		var schema = JsonSchema.FromText(schemaElement.GetRawText());
		var results = schema.Evaluate(instance, new EvaluationOptions { OutputFormat = OutputFormat.List });
		if (results.IsValid) yield break;
		foreach (var d in results.Details ?? [])
			if (d.Errors is { Count: > 0 } errors)
				foreach (var e in errors)
					// `format` is annotation-only in draft 2020-12 and real strict clients
					// (ajv-based, incl. opencode) do NOT assert it — so a date-time without a
					// timezone offset is not a client-breaking defect. We validate the properties
					// clients actually enforce: required, type, and structuredContent presence.
					if (!string.Equals(e.Key, "format", StringComparison.OrdinalIgnoreCase))
						yield return $"{d.InstanceLocation} {e.Key}: {e.Value}";
	}

	async Task<CallToolResult> Call(string tool, object args) => await _tools[tool].CallAsync(ToArgs(args));

	static Dictionary<string, object?> ToArgs(object o) =>
		JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(o))!
			.ToDictionary(kv => kv.Key, kv => (object?)((JsonElement)kv.Value!));

	static JsonElement Nodes(params object[] nodes) => JsonSerializer.SerializeToElement(nodes);
	static JsonElement Entries(params object[] entries) => JsonSerializer.SerializeToElement(entries);

	static string Text(CallToolResult r) => r.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text ?? "";
}
