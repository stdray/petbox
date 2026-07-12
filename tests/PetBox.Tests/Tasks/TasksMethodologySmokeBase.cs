using System.Text.Json;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Tasks.Data;

namespace PetBox.Tests.Tasks;

// Shared per-class host for the Tasks methodology smoke suites: one WebApplicationFactory +
// one MCP handshake pair for the whole class (xUnit news the test CLASS per test, so without
// this fixture every one of the ~37 tests boots its own host — the single biggest wall-clock
// cost in the suite). Per-test DATA isolation is restored by ResetAsync: the core catalog rows
// (task_boards, relations) for the test project are wiped and the per-project tasks file (nodes,
// comments, tags, version cursors, search index) is deleted outright, so every test still starts
// from an empty project.
//
// The scenarios are split across FOUR sibling classes (see TasksMethodologySmokeBase). xUnit v2
// parallelizes across test COLLECTIONS and runs the classes inside one collection SERIALLY, so a
// shared collection fixture would keep the whole 37-test span on one thread. Each sibling
// therefore takes its own IClassFixture instance: xUnit builds one fixture (= one host, one temp
// core db, one temp tasks dir) PER CLASS, the four classes land in four implicit collections and
// run in parallel, and ResetAsync keeps wiping rows only in the host it owns — no cross-class race
// on the shared `wf` project. Four ~3s boots buy back ~30s of serialized span.
public sealed class TasksMethodologySmokeFixture : IAsyncLifetime
{
	public const string ProjectKey = "wf";
	public const string AgentKey = "yb_key_wf_agent";        // tasks:read,tasks:write   (an agent — cannot approve)
	public const string MaintainerKey = "yb_key_wf_approve"; // + tasks:approve          (the maintainer — confirms Done)

	readonly string _baseDir;
	HttpClient _httpAgent = null!;
	HttpClient _httpApprove = null!;

	public WebApplicationFactory<Program> Factory { get; }
	public McpClient Agent { get; private set; } = null!;    // default client used by most scenarios
	public McpClient Approver { get; private set; } = null!;

	public TasksMethodologySmokeFixture()
	{
		_baseDir = Path.Combine(Path.GetTempPath(), "petbox-wf-smoke-" + Guid.NewGuid().ToString("N"));
		Environment.SetEnvironmentVariable("PETBOX_MASTER_KEY", "test-key-for-secrets");
		Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");
		Environment.SetEnvironmentVariable("Features__Tasks", "true");

		Factory = new WebApplicationFactory<Program>()
			.WithWebHostBuilder(b =>
			{
				b.UseEnvironment("Testing");
				b.ConfigureAppConfiguration((_, cfg) =>
				{
					cfg.AddInMemoryCollection(new Dictionary<string, string?>
					{
						["ConnectionStrings:PetBox"] = TestSchema.NewTempConnectionString(),
						["Features:Tasks"] = "true",
					});
				});
				b.ConfigureServices(svc =>
				{
					// Same reason as TasksMcpFixture: these smoke tests only need the MCP stack.
					// Background services (enrichment, orphan cleanup, WAL checkpoint, self-log
					// flush) tick on wall-clock timers, so under a LOADED parallel run — where the
					// class takes longer than their 10s/30s initial delays — they fire in the middle
					// of a test and hold pooled SqliteConnections / native file handles on Windows,
					// which makes ResetAsync's delete of the per-test tasks file fail.
					var hosted = svc.Where(d => typeof(IHostedService).IsAssignableFrom(d.ServiceType)).ToList();
					foreach (var h in hosted) svc.Remove(h);

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
		var cs = Factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("PetBox")!;
		TestSchema.Core(cs);

		using (var scope = Factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<PetBoxDb>();
			await db.ApiKeys.Where(k => k.Key == AgentKey || k.Key == MaintainerKey).DeleteAsync();
			await db.Projects.Where(p => p.Key == ProjectKey).DeleteAsync();
			await db.Workspaces.Where(w => w.Key == "test").DeleteAsync();
			await db.InsertAsync(new Workspace { Key = "test", Name = "Test", CreatedAt = DateTime.UtcNow });
			await db.InsertAsync(new Project { Key = ProjectKey, WorkspaceKey = "test", Name = "Workflow" });
			await db.InsertAsync(new ApiKey { Key = AgentKey, ProjectKey = ProjectKey, Scopes = "tasks:read,tasks:write", CreatedAt = DateTime.UtcNow });
			await db.InsertAsync(new ApiKey { Key = MaintainerKey, ProjectKey = ProjectKey, Scopes = "tasks:read,tasks:write,tasks:approve", CreatedAt = DateTime.UtcNow });
		}

		(_httpAgent, Agent) = await ConnectAsync(AgentKey);
		(_httpApprove, Approver) = await ConnectAsync(MaintainerKey);
	}

	// Wipe everything the previous test may have written under the shared host, so each test
	// sees an empty "wf" project: the board CATALOG lives in petbox.db (task_boards — boards
	// like `spec`/`ideas` are per-project singletons, so leftover rows would change
	// board_create/auto-wire behavior); nodes/edges/comments/tags/version cursors/search index
	// all live in the per-project tasks file, which we delete wholesale.
	//
	// Both stores are private to THIS fixture instance (own Guid core db + own Guid tasks dir),
	// and xUnit never runs two tests of one class concurrently — so the wipe cannot be seen by
	// the sibling smoke classes running in parallel against their own hosts.
	public async Task ResetAsync()
	{
		using (var scope = Factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<PetBoxDb>();
			await db.TaskBoards.Where(b => b.ProjectKey == ProjectKey).DeleteAsync();
		}

		var tasksFactory = Factory.Services.GetRequiredService<IScopedDbFactory<TasksDb>>();
		await tasksFactory.EvictAsync(ProjectKey);
		var path = Path.Combine(_baseDir, "tasks", ProjectKey + ".db");
		// Clears this file's pools, checkpoints, deletes — in that order, which matters.
		// (Never ClearAllPools(): it is process-global and yanks pooled connections out from
		// under every other collection running in parallel. See TestDirs.)
		TestDirs.ResetDbFile(path);
	}

	async Task<(HttpClient, McpClient)> ConnectAsync(string apiKey)
	{
		var http = Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
		http.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
		var transport = new HttpClientTransport(new HttpClientTransportOptions
		{
			Endpoint = new Uri(http.BaseAddress!, "/mcp"),
			AdditionalHeaders = new Dictionary<string, string> { ["X-Api-Key"] = apiKey },
		}, http);
		var mcp = await McpClient.CreateAsync(transport, cancellationToken: default);
		return (http, mcp);
	}

	public async Task DisposeAsync()
	{
		await Agent.DisposeAsync();
		await Approver.DisposeAsync();
		_httpAgent.Dispose();
		_httpApprove.Dispose();
		await Factory.DisposeAsync();
		TestDirs.CleanupOrDefer(_baseDir);
	}
}

// Scenario smoke tests for the Tasks methodology (spec + relations + FSM).
// These encode the TARGET api/mcp contract and are expected to be RED until the
// unified schema + workflow engine + relations are built (TDD by contract).
//
// Target surface assumed here (to be built):
//   tasks_board_create(projectKey, board, kind?, description?)  kind ∈ spec|ideas|intake|work|free (def free)
//   tasks_upsert nodes carry: key, type?(feature|bug), status(slug), name, body, priority?, specRef?(spec NodeId)
//     - upsert response nodes carry a stable `nodeId`
//     - on a `work` board a feature/bug WITHOUT specRef is rejected (spec-link invariant)
//     - specRef on upsert creates the node + a `task_spec` relation atomically
//     - setting a terminal status (done) requires the `tasks:approve` scope (approve-gate)
//   tasks_workflow(projectKey, board) → { kind, workflows:[{ types:[...], initial, statuses, transitions }] }
//   relations_create(projectKey, kind, fromNodeId, toNodeId)   kind ∈ task_spec|issue_task|idea_spec|blocks|nfr|dup
//   relations_list(projectKey, nodeId, direction?)             direction ∈ from|to|both (default both)
//   report_issue(title, detail) → lands on an intake-kind board, status `reported`
//
// The scenarios live in four sibling classes, grouped by theme — TasksMethodologyBoardsTests,
// TasksMethodologySpecTests, TasksMethodologyWorkFsmTests, TasksMethodologyRefsTests. Each takes
// its OWN TasksMethodologySmokeFixture (xUnit builds a class fixture per class), so the four run
// in parallel against four hosts instead of serializing 37 tests through one. This base holds the
// fixture wiring, the per-test reset, and the shared helpers; the assertions are untouched.
public abstract class TasksMethodologySmokeBase : IAsyncLifetime
{
	protected const string ProjectKey = TasksMethodologySmokeFixture.ProjectKey;

	readonly TasksMethodologySmokeFixture _fx;
	readonly McpClient _agent;     // default client used by most scenarios
	readonly McpClient _approver;

	protected TasksMethodologySmokeBase(TasksMethodologySmokeFixture fx)
	{
		_fx = fx;
		_agent = fx.Agent;
		_approver = fx.Approver;
	}

	public Task InitializeAsync() => _fx.ResetAsync();

	public Task DisposeAsync() => Task.CompletedTask; // the fixture owns host teardown

	// ── helpers ──────────────────────────────────────────────────────────────
	static async Task<CallToolResult> Call(McpClient mcp, string tool, object args) =>
		await (await mcp.ListToolsAsync()).First(t => t.Name == tool)
			.CallAsync((Dictionary<string, object?>)ToArgs(args));

	protected Task<CallToolResult> Agent(string tool, object args) => Call(_agent, tool, args);

	// The maintainer client (tasks:approve) — only the approve-gated scenarios use it.
	protected Task<CallToolResult> Approver(string tool, object args) => Call(_approver, tool, args);

	static Dictionary<string, object?> ToArgs(object o) =>
		o as Dictionary<string, object?> ??
		JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(o))!
			.ToDictionary(kv => kv.Key, kv => (object?)((JsonElement)kv.Value!));

	protected static JsonElement Nodes(params object[] nodes) => JsonSerializer.SerializeToElement(nodes);

	protected static string Text(CallToolResult r) =>
		r.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().First().Text;

	// Every tool routes failures through McpErrorEnvelopeFilter: the {"error":{...}}
	// envelope on the text content AND IsError=true. Helper stays tolerant of both.
	protected static bool IsErr(CallToolResult r) =>
		r.IsError == true ||
		(r.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().FirstOrDefault()?.Text?.Contains("\"error\"") ?? false);

	// Pull the first `nodeId` for a node with the given key out of an upsert/get JSON payload.
	protected static string NodeId(CallToolResult r, string key)
	{
		using var doc = JsonDocument.Parse(Text(r));
		foreach (var el in Descend(doc.RootElement))
			if (el.ValueKind == JsonValueKind.Object
				&& el.TryGetProperty("key", out var k) && k.GetString() == key
				&& el.TryGetProperty("nodeId", out var id))
				return id.GetString()!;
		throw new Xunit.Sdk.XunitException($"no nodeId for key '{key}' in: {Text(r)}");
	}

	static IEnumerable<JsonElement> Descend(JsonElement e)
	{
		yield return e;
		if (e.ValueKind == JsonValueKind.Object)
			foreach (var p in e.EnumerateObject())
				foreach (var c in Descend(p.Value)) yield return c;
		else if (e.ValueKind == JsonValueKind.Array)
			foreach (var item in e.EnumerateArray())
				foreach (var c in Descend(item)) yield return c;
	}

	protected static string StatusOf(CallToolResult r, string key)
	{
		using var doc = JsonDocument.Parse(Text(r));
		foreach (var el in Descend(doc.RootElement))
			if (el.ValueKind == JsonValueKind.Object
				&& el.TryGetProperty("key", out var k) && k.GetString() == key
				&& el.TryGetProperty("status", out var s))
				return s.GetString()!;
		throw new Xunit.Sdk.XunitException($"no status for key '{key}' in: {Text(r)}");
	}

	protected static string FieldOf(CallToolResult r, string key, string field)
	{
		using var doc = JsonDocument.Parse(Text(r));
		foreach (var el in Descend(doc.RootElement))
			if (el.ValueKind == JsonValueKind.Object
				&& el.TryGetProperty("key", out var k) && k.GetString() == key
				&& el.TryGetProperty(field, out var v))
				return v.ValueKind == JsonValueKind.Null ? "null" : v.GetString()!;
		throw new Xunit.Sdk.XunitException($"no {field} for key '{key}' in: {Text(r)}");
	}

	// Spec writes require an `accepted` idea (ideaRef). Drive one through the gate
	// (exploring → review[+spec_plan] → accepted) and return its NodeId. Creates the
	// project's ideas board (singleton) on first use; call once per test.
	protected async Task<string> AcceptedIdeaId(string key = "drv")
	{
		await Agent("tasks_board_create", new { projectKey = ProjectKey, board = "ideas", kind = "ideas" });
		var ideaId = NodeId(await Agent("tasks_upsert", new
		{
			projectKey = ProjectKey,
			board = "ideas",
			nodes = Nodes(new { key, type = "idea", status = "exploring", title = key, body = "x" })
		}), key);
		await Agent("comments_upsert", new { projectKey = ProjectKey, board = "ideas", items = new[] { new { nodeId = ideaId, author = "t", body = "plan", tags = new[] { "artifact:spec_plan" } } } });
		await Agent("tasks_upsert", new { projectKey = ProjectKey, board = "ideas", nodes = Nodes(new { key, type = "idea", status = "review", version = 1 }) });
		await Agent("tasks_upsert", new { projectKey = ProjectKey, board = "ideas", nodes = Nodes(new { key, type = "idea", status = "accepted", version = 2 }) });
		return ideaId;
	}
}
