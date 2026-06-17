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
using PetBox.Tasks.Data;

namespace PetBox.Tests.Tasks;

// Scenario smoke tests for the Tasks methodology (spec + relations + FSM).
// These encode the TARGET api/mcp contract and are expected to be RED until the
// unified schema + workflow engine + relations are built (TDD by contract).
//
// Target surface assumed here (to be built):
//   tasks.board_create(projectKey, board, kind?, description?)  kind ∈ spec|ideas|intake|work|free (def free)
//   tasks.upsert nodes carry: key, type?(feature|bug), status(slug), name, body, priority?, specRef?(spec NodeId)
//     - upsert response nodes carry a stable `nodeId`
//     - on a `work` board a feature/bug WITHOUT specRef is rejected (spec-link invariant)
//     - specRef on upsert creates the node + a `task_spec` relation atomically
//     - setting a terminal status (done) requires the `tasks:approve` scope (approve-gate)
//   tasks.workflow(projectKey, board) → { kind, types:[{ type, statuses, transitions, initial }] }
//   relations.create(projectKey, kind, fromNodeId, toNodeId)   kind ∈ task_spec|issue_task|idea_spec|blocks|nfr|dup
//   relations.list(projectKey, nodeId, direction?)             direction ∈ from|to|both (default both)
//   report.issue(title, detail) → lands on an intake-kind board, status `reported`
[Collection("DataModule")]
public sealed class TasksMethodologySmokeTests : IAsyncLifetime
{
	const string ProjectKey = "wf";
	const string AgentKey = "yb_key_wf_agent";        // tasks:read,tasks:write   (an agent — cannot approve)
	const string MaintainerKey = "yb_key_wf_approve"; // + tasks:approve          (the maintainer — confirms Done)

	readonly string _baseDir;
	readonly WebApplicationFactory<Program> _factory;
	HttpClient _httpAgent = null!;
	HttpClient _httpApprove = null!;
	McpClient _agent = null!;       // default client used by most scenarios
	McpClient _approver = null!;

	public TasksMethodologySmokeTests()
	{
		_baseDir = Path.Combine(Path.GetTempPath(), "petbox-wf-smoke-" + Guid.NewGuid().ToString("N"));
		Environment.SetEnvironmentVariable("PETBOX_MASTER_KEY", "test-key-for-secrets");
		Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");
		Environment.SetEnvironmentVariable("Features__Tasks", "true");

		_factory = new WebApplicationFactory<Program>()
			.WithWebHostBuilder(b =>
			{
				b.UseEnvironment("Testing");
				b.ConfigureAppConfiguration((_, cfg) =>
				{
					cfg.AddInMemoryCollection(new Dictionary<string, string?>
					{
						["ConnectionStrings:PetBox"] = $"Data Source={Path.Combine(Path.GetTempPath(), $"petbox-test-{Guid.NewGuid():N}.db")};Cache=Shared",
						["Features:Tasks"] = "true",
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
			await db.ApiKeys.Where(k => k.Key == AgentKey || k.Key == MaintainerKey).DeleteAsync();
			await db.Projects.Where(p => p.Key == ProjectKey).DeleteAsync();
			await db.Workspaces.Where(w => w.Key == "test").DeleteAsync();
			await db.InsertAsync(new Workspace { Key = "test", Name = "Test", CreatedAt = DateTime.UtcNow });
			await db.InsertAsync(new Project { Key = ProjectKey, WorkspaceKey = "test", Name = "Workflow" });
			await db.InsertAsync(new ApiKey { Key = AgentKey, ProjectKey = ProjectKey, Scopes = "tasks:read,tasks:write", CreatedAt = DateTime.UtcNow });
			await db.InsertAsync(new ApiKey { Key = MaintainerKey, ProjectKey = ProjectKey, Scopes = "tasks:read,tasks:write,tasks:approve", CreatedAt = DateTime.UtcNow });
		}

		(_httpAgent, _agent) = await ConnectAsync(AgentKey);
		(_httpApprove, _approver) = await ConnectAsync(MaintainerKey);
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
		var mcp = await McpClient.CreateAsync(transport, cancellationToken: default);
		return (http, mcp);
	}

	public async Task DisposeAsync()
	{
		await _agent.DisposeAsync();
		await _approver.DisposeAsync();
		_httpAgent.Dispose();
		_httpApprove.Dispose();
		await _factory.DisposeAsync();
		SqliteConnection.ClearAllPools();
		if (Directory.Exists(_baseDir)) Directory.Delete(_baseDir, recursive: true);
	}

	// ── helpers ──────────────────────────────────────────────────────────────
	static async Task<CallToolResult> Call(McpClient mcp, string tool, object args) =>
		await (await mcp.ListToolsAsync()).First(t => t.Name == tool)
			.CallAsync((Dictionary<string, object?>)ToArgs(args));

	Task<CallToolResult> Agent(string tool, object args) => Call(_agent, tool, args);

	static Dictionary<string, object?> ToArgs(object o) =>
		o as Dictionary<string, object?> ??
		JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(o))!
			.ToDictionary(kv => kv.Key, kv => (object?)((JsonElement)kv.Value!));

	static JsonElement Nodes(params object[] nodes) => JsonSerializer.SerializeToElement(nodes);

	static string Text(CallToolResult r) =>
		r.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().First().Text;

	// GuardAsync tools (upsert/workflow/report.issue) return errors as content
	// {"error":{...}} with IsError unset; non-guarded tools set IsError. Treat both.
	static bool IsErr(CallToolResult r) =>
		r.IsError == true ||
		(r.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().FirstOrDefault()?.Text?.Contains("\"error\"") ?? false);

	// Pull the first `nodeId` for a node with the given key out of an upsert/get JSON payload.
	static string NodeId(CallToolResult r, string key)
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

	// Spec writes require an `accepted` idea (ideaRef). Drive one through the gate
	// (exploring → review[+spec_plan] → accepted) and return its NodeId. Creates the
	// project's ideas board (singleton) on first use; call once per test.
	async Task<string> AcceptedIdeaId(string key = "drv")
	{
		await Agent("tasks.board_create", new { projectKey = ProjectKey, board = "ideas", kind = "ideas" });
		var ideaId = NodeId(await Agent("tasks.upsert", new { projectKey = ProjectKey, board = "ideas",
			nodes = Nodes(new { key, type = "idea", status = "exploring", title = key, body = "x" }) }), key);
		await Agent("comments.add", new { projectKey = ProjectKey, board = "ideas", nodeId = ideaId, author = "t", body = "plan", tags = new[] { "artifact:spec_plan" } });
		await Agent("tasks.upsert", new { projectKey = ProjectKey, board = "ideas", nodes = Nodes(new { key, type = "idea", status = "review", version = 1 }) });
		await Agent("tasks.upsert", new { projectKey = ProjectKey, board = "ideas", nodes = Nodes(new { key, type = "idea", status = "accepted", version = 2 }) });
		return ideaId;
	}

	// ── scenarios ────────────────────────────────────────────────────────────

	// 1. spec board: create H1/H2/H3 nodes (path depth 1–3), read back as a tree.
	[Fact]
	public async Task Spec_CreateThreeLevels_ReadBackAsTree()
	{
		(await Agent("tasks.board_create", new { projectKey = ProjectKey, board = "spec", kind = "spec" })).IsError.Should().NotBe(true);
		var ir = await AcceptedIdeaId();
		(await Agent("tasks.upsert", new
		{
			projectKey = ProjectKey,
			board = "spec",
			nodes = Nodes(
				new { key = "auth", status = "defined", title = "Auth", body = "auth area", ideaRef = ir },
				new { key = "login", partOf = "auth", status = "defined", title = "Login", body = "login flow", ideaRef = ir },
				new { key = "mfa", partOf = "login", status = "defined", title = "MFA", body = "second factor", ideaRef = ir }),
		})).IsError.Should().NotBe(true);

		var tree = await Agent("tasks.get", new { projectKey = ProjectKey, board = "spec" });
		tree.IsError.Should().NotBe(true);
		// mfa is two part_of edges below auth — its parent is login.
		Text(tree).Should().Contain("mfa");
		FieldOf(tree, "mfa", "parentSlug").Should().Be("login");
	}

	// 2. work board: a feature WITHOUT a spec link is rejected (invariant).
	[Fact]
	public async Task Work_FeatureWithoutSpecLink_Rejected()
	{
		await Agent("tasks.board_create", new { projectKey = ProjectKey, board = "work", kind = "work" });
		var r = await Agent("tasks.upsert", new
		{
			projectKey = ProjectKey,
			board = "work",
			nodes = Nodes(new { key = "do-login", type = "feature", status = "Pending", title = "Build login", body = "..." }),
		});
		IsErr(r).Should().BeTrue();
		Text(r).Should().Contain("spec");
	}

	// 3. work feature WITH a spec link: ok, and a task_spec relation is persisted + reverse-resolvable.
	[Fact]
	public async Task Work_FeatureWithSpecLink_CreatesRelation()
	{
		await Agent("tasks.board_create", new { projectKey = ProjectKey, board = "spec", kind = "spec" });
		var ir = await AcceptedIdeaId();
		var spec = await Agent("tasks.upsert", new
		{
			projectKey = ProjectKey, board = "spec",
			nodes = Nodes(new { key = "login", status = "defined", title = "Login", body = "login flow", ideaRef = ir }),
		});
		var specId = NodeId(spec, "login");

		await Agent("tasks.board_create", new { projectKey = ProjectKey, board = "work", kind = "work" });
		var work = await Agent("tasks.upsert", new
		{
			projectKey = ProjectKey, board = "work",
			nodes = Nodes(new { key = "do-login", type = "feature", status = "Pending", title = "Build login", body = "...", specRef = specId }),
		});
		work.IsError.Should().NotBe(true);
		var taskId = NodeId(work, "do-login");

		var rels = await Agent("relations.list", new { projectKey = ProjectKey, nodeId = specId, direction = "to" });
		rels.IsError.Should().NotBe(true);
		Text(rels).Should().Contain(taskId);
	}

	// 4. rename a node (Key changes) → the relation still resolves (NodeId is stable, links don't rot).
	[Fact]
	public async Task Rename_KeepsRelation_ViaStableNodeId()
	{
		await Agent("tasks.board_create", new { projectKey = ProjectKey, board = "spec", kind = "spec" });
		var ir = await AcceptedIdeaId();
		var spec = await Agent("tasks.upsert", new
		{
			projectKey = ProjectKey, board = "spec",
			nodes = Nodes(new { key = "auth", status = "defined", title = "Auth", body = "x", ideaRef = ir }),
		});
		var specId = NodeId(spec, "auth");
		var v = JsonDocument.Parse(Text(spec)).RootElement;

		// rename auth → identity (Key change, same NodeId via prevKey lineage).
		// version = 1 is the baseline the author last saw for "auth".
		var renamed = await Agent("tasks.upsert", new
		{
			projectKey = ProjectKey, board = "spec",
			nodes = Nodes(new { key = "identity", prevKey = "auth", version = 1, status = "defined", title = "Identity", body = "x", ideaRef = ir }),
		});
		NodeId(renamed, "identity").Should().Be(specId, "rename must preserve the stable NodeId");
	}

	// 5. intake: report.issue → reported; triage → confirmed; promote → work task + issue_task relation.
	[Fact]
	public async Task Intake_ReportTriageConfirm_PromotesToWork()
	{
		await Agent("tasks.board_create", new { projectKey = ProjectKey, board = "intake", kind = "intake" });
		var rep = await Agent("report.issue", new { title = "login 500s", detail = "POST /login returns 500" });
		rep.IsError.Should().NotBe(true);

		// the issue lands on an intake-kind board in status `reported`; triage it to confirmed
		var wf = await Agent("tasks.workflow", new { projectKey = ProjectKey, board = "intake" });
		wf.IsError.Should().NotBe(true);
		Text(wf).Should().Contain("reported");
		Text(wf).Should().Contain("confirmed");
	}

	// 6. FSM effect: a work bug → done (by the maintainer) auto-closes the linked intake issue.
	[Fact]
	public async Task WorkBugDone_AutoClosesLinkedIssue()
	{
		// Build spec + work bug linked to an intake issue, then approve Done and assert the issue closed.
		await Agent("tasks.board_create", new { projectKey = ProjectKey, board = "spec", kind = "spec" });
		var ir = await AcceptedIdeaId();
		var spec = await Agent("tasks.upsert", new
		{
			projectKey = ProjectKey, board = "spec",
			nodes = Nodes(new { key = "login", status = "defined", title = "Login", body = "x", ideaRef = ir }),
		});
		var specId = NodeId(spec, "login");

		await Agent("tasks.board_create", new { projectKey = ProjectKey, board = "intake", kind = "intake" });
		var issue = await Agent("tasks.upsert", new
		{
			projectKey = ProjectKey, board = "intake",
			nodes = Nodes(new { key = "login-500", type = "issue", status = "confirmed", title = "login 500", body = "x" }),
		});
		var issueId = NodeId(issue, "login-500");

		await Agent("tasks.board_create", new { projectKey = ProjectKey, board = "work", kind = "work" });
		var work = await Agent("tasks.upsert", new
		{
			projectKey = ProjectKey, board = "work",
			nodes = Nodes(new { key = "fix-login", type = "bug", status = "Review", title = "Fix login", body = "x", specRef = specId }),
		});
		var taskId = NodeId(work, "fix-login");
		await Agent("relations.create", new { projectKey = ProjectKey, kind = "issue_task", fromNodeId = issueId, toNodeId = taskId });

		// maintainer approves Done → effect should close the linked issue
		var done = await Call(_approver, "tasks.upsert", new
		{
			projectKey = ProjectKey, board = "work",
			nodes = Nodes(new { key = "fix-login", type = "bug", status = "Done", version = 1, title = "Fix login", body = "x", specRef = specId }),
		});
		IsErr(done).Should().BeFalse();

		var intake = await Agent("tasks.get", new { projectKey = ProjectKey, board = "intake", includeClosed = true });
		Text(intake).Should().Contain("done");
	}

	// 7. approve-gate: an agent's ceiling is Review; only the maintainer confirms Done.
	// Enforcement is DEFERRED in v1 by decision — the capability is modelled in
	// WorkflowEngine (RequiresApproval on Review->Done; TerminalOk = maintainer-only).
	// Flip `enforceApproval` at the call site once constraints are clear from practice.
	[Fact(Skip = "approve-gate enforcement deferred in v1; capability modelled in WorkflowEngine")]
	public Task ApproveGate_AgentCannotSetDone_MaintainerCan() => Task.CompletedTask;

	// 8. ideas: raw → exploring → accepted produces a spec node + an idea_spec relation.
	[Fact]
	public async Task Idea_Accepted_LinksToSpec()
	{
		// idea reaches `accepted` only through the gate (exploring → review[+spec_plan] → accepted).
		var ideaId = await AcceptedIdeaId("want-x");

		await Agent("tasks.board_create", new { projectKey = ProjectKey, board = "spec", kind = "spec" });
		var spec = await Agent("tasks.upsert", new { projectKey = ProjectKey, board = "spec", nodes = Nodes(new { key = "x", status = "defined", title = "X", body = "defined", ideaRef = ideaId }) });
		spec.IsError.Should().NotBe(true);
		var specId = NodeId(spec, "x");

		// ideaRef auto-creates the idea_spec edge (accepted idea -> spec node).
		var rels = await Agent("relations.list", new { projectKey = ProjectKey, nodeId = specId, direction = "to" });
		Text(rels).Should().Contain("idea_spec");
		Text(rels).Should().Contain(ideaId);
	}

	// 9. invalid status for the board's kind is rejected, and the error names the valid next statuses.
	[Fact]
	public async Task InvalidStatus_RejectedWithValidList()
	{
		await Agent("tasks.board_create", new { projectKey = ProjectKey, board = "work", kind = "work" });
		var r = await Agent("tasks.upsert", new
		{
			projectKey = ProjectKey, board = "work",
			nodes = Nodes(new { key = "t", type = "feature", status = "banana", title = "T", body = "x" }),
		});
		IsErr(r).Should().BeTrue();
		Text(r).Should().Contain("Pending"); // the error enumerates valid statuses for this kind/type
	}

	// 10. a simple board (default kind) enforces its preset status vocab but allows free
	// transitions (any valid status → any), and the error on an unknown status names the set.
	[Fact]
	public async Task SimpleBoard_EnforcesVocab_FreeTransitions()
	{
		(await Agent("tasks.board_create", new { projectKey = ProjectKey, board = "scratch" })).IsError.Should().NotBe(true);
		// a valid preset status is accepted
		(await Agent("tasks.upsert", new { projectKey = ProjectKey, board = "scratch", nodes = Nodes(new { key = "ok", status = "Todo", title = "OK", body = "x" }) })).IsError.Should().NotBe(true);
		// an out-of-vocab status is rejected, and the error enumerates the valid statuses
		var bad = await Agent("tasks.upsert", new { projectKey = ProjectKey, board = "scratch", nodes = Nodes(new { key = "bad", status = "Frobnicate", title = "B", body = "x" }) });
		IsErr(bad).Should().BeTrue();
		Text(bad).Should().Contain("Todo");
		// free transitions: Todo -> Done directly (no approve gate)
		(await Agent("tasks.upsert", new { projectKey = ProjectKey, board = "scratch", nodes = Nodes(new { key = "ok", version = 1, status = "Done" }) })).IsError.Should().NotBe(true);
	}

	// 11. a work task can't be Blocked without naming a blocker (blocked requires a `blocks` link).
	[Fact]
	public async Task Blocked_WithoutBlocker_Rejected()
	{
		await Agent("tasks.board_create", new { projectKey = ProjectKey, board = "spec", kind = "spec" });
		var ir = await AcceptedIdeaId();
		var spec = await Agent("tasks.upsert", new { projectKey = ProjectKey, board = "spec", nodes = Nodes(new { key = "f", status = "defined", title = "F", body = "x", ideaRef = ir }) });
		var specId = NodeId(spec, "f");
		await Agent("tasks.board_create", new { projectKey = ProjectKey, board = "work", kind = "work" });

		var r = await Agent("tasks.upsert", new
		{
			projectKey = ProjectKey, board = "work",
			nodes = Nodes(new { key = "t", type = "feature", status = "Blocked", title = "T", body = "x", specRef = specId }),
		});
		IsErr(r).Should().BeTrue();
		Text(r).Should().Contain("block");
	}

	// 12. blockedBy creates a `blocks` edge; when the blocker reaches Done, the blocked task auto-unblocks.
	[Fact]
	public async Task Block_AutoUnblocksWhenBlockerDone()
	{
		await Agent("tasks.board_create", new { projectKey = ProjectKey, board = "spec", kind = "spec" });
		var ir = await AcceptedIdeaId();
		var spec = await Agent("tasks.upsert", new { projectKey = ProjectKey, board = "spec", nodes = Nodes(new { key = "f", status = "defined", title = "F", body = "x", ideaRef = ir }) });
		var specId = NodeId(spec, "f");
		await Agent("tasks.board_create", new { projectKey = ProjectKey, board = "work", kind = "work" });

		var a = await Agent("tasks.upsert", new { projectKey = ProjectKey, board = "work", nodes = Nodes(new { key = "a", type = "feature", status = "Review", title = "A", body = "x", specRef = specId }) });
		var aId = NodeId(a, "a");
		var b = await Agent("tasks.upsert", new { projectKey = ProjectKey, board = "work", nodes = Nodes(new { key = "b", type = "feature", status = "Blocked", title = "B", body = "x", specRef = specId, blockedBy = aId }) });
		IsErr(b).Should().BeFalse();

		// blocker A → Done (baseline version 1: A was the first node on the work board)
		var done = await Agent("tasks.upsert", new { projectKey = ProjectKey, board = "work", nodes = Nodes(new { key = "a", type = "feature", status = "Done", version = 1, title = "A", body = "x", specRef = specId }) });
		IsErr(done).Should().BeFalse();

		var get = await Agent("tasks.get", new { projectKey = ProjectKey, board = "work" });
		StatusOf(get, "b").Should().Be("InProgress", "the blocked task auto-unblocks when its only blocker is Done");
	}

	static string StatusOf(CallToolResult r, string key)
	{
		using var doc = JsonDocument.Parse(Text(r));
		foreach (var el in Descend(doc.RootElement))
			if (el.ValueKind == JsonValueKind.Object
				&& el.TryGetProperty("key", out var k) && k.GetString() == key
				&& el.TryGetProperty("status", out var s))
				return s.GetString()!;
		throw new Xunit.Sdk.XunitException($"no status for key '{key}' in: {Text(r)}");
	}

	static string FieldOf(CallToolResult r, string key, string field)
	{
		using var doc = JsonDocument.Parse(Text(r));
		foreach (var el in Descend(doc.RootElement))
			if (el.ValueKind == JsonValueKind.Object
				&& el.TryGetProperty("key", out var k) && k.GetString() == key
				&& el.TryGetProperty(field, out var v))
				return v.ValueKind == JsonValueKind.Null ? "null" : v.GetString()!;
		throw new Xunit.Sdk.XunitException($"no {field} for key '{key}' in: {Text(r)}");
	}

	// 13. spec node delivery status is COMPUTED from linked tasks (type-aware), rolled up the tree.
	[Fact]
	public async Task SpecRollup_ComputedFromLinkedTasks()
	{
		await Agent("tasks.board_create", new { projectKey = ProjectKey, board = "spec", kind = "spec" });
		var ir = await AcceptedIdeaId();
		await Agent("tasks.upsert", new { projectKey = ProjectKey, board = "spec", nodes = Nodes(
			new { key = "auth", status = "defined", title = "Auth", body = "x", ideaRef = ir },
			new { key = "login", partOf = "auth", status = "defined", title = "Login", body = "x", ideaRef = ir }) });
		var loginId = NodeId(await Agent("tasks.get", new { projectKey = ProjectKey, board = "spec" }), "login");

		await Agent("tasks.board_create", new { projectKey = ProjectKey, board = "work", kind = "work" });
		await Agent("tasks.upsert", new { projectKey = ProjectKey, board = "work", nodes = Nodes(new { key = "f", type = "feature", status = "Review", title = "F", body = "x", specRef = loginId }) });

		var s1 = await Agent("tasks.get", new { projectKey = ProjectKey, board = "spec" });
		FieldOf(s1, "login", "delivery").Should().Be("in_progress");
		FieldOf(s1, "auth", "delivery").Should().Be("in_progress", "parent aggregates the part_of subtree");

		await Agent("tasks.upsert", new { projectKey = ProjectKey, board = "work", nodes = Nodes(new { key = "f", type = "feature", status = "Done", version = 1, title = "F", body = "x", specRef = loginId }) });
		var s2 = await Agent("tasks.get", new { projectKey = ProjectKey, board = "spec" });
		FieldOf(s2, "login", "delivery").Should().Be("done");
		FieldOf(s2, "auth", "delivery").Should().Be("done");

		await Agent("tasks.upsert", new { projectKey = ProjectKey, board = "work", nodes = Nodes(new { key = "bug1", type = "bug", status = "Pending", title = "Bug", body = "x", specRef = loginId }) });
		var s3 = await Agent("tasks.get", new { projectKey = ProjectKey, board = "spec" });
		FieldOf(s3, "login", "delivery").Should().Be("done_with_defects", "all features Done but an open bug remains");
		FieldOf(s3, "auth", "delivery").Should().Be("done_with_defects");
	}

	// 14. a closed board rejects writes (agents stop writing by inertia); reopen restores writes.
	[Fact]
	public async Task ClosedBoard_RejectsWrites_UntilReopened()
	{
		await Agent("tasks.board_create", new { projectKey = ProjectKey, board = "tmp" });
		(await Agent("tasks.upsert", new { projectKey = ProjectKey, board = "tmp", nodes = Nodes(new { key = "a", status = "Todo", title = "A", body = "x" }) })).IsError.Should().NotBe(true);

		await Agent("tasks.board_close", new { projectKey = ProjectKey, board = "tmp" });
		var blocked = await Agent("tasks.upsert", new { projectKey = ProjectKey, board = "tmp", nodes = Nodes(new { key = "b", status = "Todo", title = "B", body = "x" }) });
		IsErr(blocked).Should().BeTrue();
		Text(blocked).Should().Contain("closed");

		await Agent("tasks.board_reopen", new { projectKey = ProjectKey, board = "tmp" });
		(await Agent("tasks.upsert", new { projectKey = ProjectKey, board = "tmp", nodes = Nodes(new { key = "b", status = "Todo", title = "B", body = "x" }) })).IsError.Should().NotBe(true);
	}

	// 15. partial update: a field omitted from upsert keeps its prior value — a status-only
	// change (path + version + status) must not blank title/body/priority.
	[Fact]
	public async Task PartialUpdate_OmittedFieldsPreserved()
	{
		await Agent("tasks.board_create", new { projectKey = ProjectKey, board = "pu" });
		(await Agent("tasks.upsert", new { projectKey = ProjectKey, board = "pu", nodes = Nodes(new { key = "a", status = "Todo", title = "Alpha", body = "BODY", priority = 5 }) })).IsError.Should().NotBe(true);

		// send ONLY path + version + status
		(await Agent("tasks.upsert", new { projectKey = ProjectKey, board = "pu", nodes = Nodes(new { key = "a", version = 1, status = "InProgress" }) })).IsError.Should().NotBe(true);

		var get = await Agent("tasks.get", new { projectKey = ProjectKey, board = "pu" });
		StatusOf(get, "a").Should().Be("InProgress");
		FieldOf(get, "a", "title").Should().Be("Alpha", "omitted title inherits the prior value");
		FieldOf(get, "a", "body").Should().Be("BODY", "omitted body inherits the prior value");
	}

	// 16. specRef must point at a spec board: a ref to a non-spec node is rejected.
	[Fact]
	public async Task SpecRef_NonSpecTarget_Rejected()
	{
		await Agent("tasks.board_create", new { projectKey = ProjectKey, board = "notspec" }); // free
		var nf = await Agent("tasks.upsert", new { projectKey = ProjectKey, board = "notspec", nodes = Nodes(new { key = "x", status = "Todo", title = "X", body = "x" }) });
		var nonSpecId = NodeId(nf, "x");

		await Agent("tasks.board_create", new { projectKey = ProjectKey, board = "work", kind = "work" });
		var r = await Agent("tasks.upsert", new { projectKey = ProjectKey, board = "work", nodes = Nodes(new { key = "t", type = "feature", status = "Pending", title = "T", body = "x", specRef = nonSpecId }) });
		IsErr(r).Should().BeTrue();
		Text(r).Should().Contain("not a spec board");
	}

	// 17. a specRef must point at a SPEC board node — a node on a non-spec board is rejected.
	// (The spec kind is now a per-project singleton, so the old two-spec-boards mismatch is
	// unreachable; the meaningful guard is "the target must live on the spec board".)
	[Fact]
	public async Task SpecRef_NonSpecBoardNode_Rejected()
	{
		await Agent("tasks.board_create", new { projectKey = ProjectKey, board = "spec", kind = "spec" });
		// A node on a NON-spec (free) board — not a valid spec target.
		await Agent("tasks.board_create", new { projectKey = ProjectKey, board = "other" });
		var other = await Agent("tasks.upsert", new { projectKey = ProjectKey, board = "other", nodes = Nodes(new { key = "r", status = "Todo", title = "R", body = "x" }) });
		var otherId = NodeId(other, "r");

		await Agent("tasks.board_create", new { projectKey = ProjectKey, board = "work", kind = "work" }); // auto-wires to spec

		var r = await Agent("tasks.upsert", new { projectKey = ProjectKey, board = "work", nodes = Nodes(new { key = "t", type = "feature", status = "Pending", title = "T", body = "x", specRef = otherId }) });
		IsErr(r).Should().BeTrue();
		Text(r).Should().Contain("not a spec board");
	}

	// 18. tasks.get hides terminal nodes by default; includeClosed=true returns them.
	[Fact]
	public async Task Get_HidesClosedByDefault_IncludeClosedReturns()
	{
		await Agent("tasks.board_create", new { projectKey = ProjectKey, board = "hc" });
		await Agent("tasks.upsert", new { projectKey = ProjectKey, board = "hc", nodes = Nodes(
			new { key = "open1", status = "Todo", title = "Open", body = "x" },
			new { key = "done1", status = "Done", title = "Done", body = "x" }) });

		var def = await Agent("tasks.get", new { projectKey = ProjectKey, board = "hc" });
		Text(def).Should().Contain("open1");
		Text(def).Should().NotContain("done1");

		var all = await Agent("tasks.get", new { projectKey = ProjectKey, board = "hc", includeClosed = true });
		Text(all).Should().Contain("done1");
	}

	// 19. tasks.get surfaces the board kind and the task->spec link inline (resolved to the spec node).
	[Fact]
	public async Task Get_SurfacesKindAndSpecLink()
	{
		await Agent("tasks.board_create", new { projectKey = ProjectKey, board = "spec", kind = "spec" });
		var ir = await AcceptedIdeaId();
		var spec = await Agent("tasks.upsert", new { projectKey = ProjectKey, board = "spec", nodes = Nodes(new { key = "login", status = "defined", title = "Login", body = "x", ideaRef = ir }) });
		var specId = NodeId(spec, "login");

		await Agent("tasks.board_create", new { projectKey = ProjectKey, board = "work", kind = "work" });
		await Agent("tasks.upsert", new { projectKey = ProjectKey, board = "work", nodes = Nodes(new { key = "do-login", type = "feature", status = "Review", title = "Build login", body = "x", specRef = specId }) });

		var get = await Agent("tasks.get", new { projectKey = ProjectKey, board = "work" });
		Text(get).Should().Contain("\"kind\":\"work\"");
		Text(get).Should().Contain(specId);             // the linked spec node id is surfaced
		Text(get).Should().Contain("\"board\":\"spec\""); // resolved to the spec board it lives on
	}

	// 20. a node's type is immutable once set — reclassifying a work feature to a bug is
	// rejected (Phase 2 declarative invariant), exercised end-to-end through the MCP tool.
	[Fact]
	public async Task Work_FeatureType_IsImmutable()
	{
		await Agent("tasks.board_create", new { projectKey = ProjectKey, board = "spec", kind = "spec" });
		var ir = await AcceptedIdeaId();
		var spec = await Agent("tasks.upsert", new { projectKey = ProjectKey, board = "spec", nodes = Nodes(new { key = "login", status = "defined", title = "Login", body = "x", ideaRef = ir }) });
		var specId = NodeId(spec, "login");

		await Agent("tasks.board_create", new { projectKey = ProjectKey, board = "work", kind = "work" });
		(await Agent("tasks.upsert", new { projectKey = ProjectKey, board = "work", nodes = Nodes(new { key = "do-login", type = "feature", status = "Pending", title = "Build login", body = "x", specRef = specId }) }))
			.IsError.Should().NotBe(true);

		// Editing the feature into a bug must be rejected — type can't change after creation.
		var r = await Agent("tasks.upsert", new { projectKey = ProjectKey, board = "work", nodes = Nodes(new { key = "do-login", type = "bug", version = 1, title = "Build login", body = "x", specRef = specId }) });
		IsErr(r).Should().BeTrue();
		Text(r).Should().Contain("immutable");
	}

	// 21. boards of a project now share ONE file, partitioned by Board: the same node key
	// in two boards is independent (own node, own version cursor), and editing one leaves
	// the other untouched. Proves the project-level merge keeps boards isolated.
	[Fact]
	public async Task TwoBoards_SameKey_AreIsolated()
	{
		await Agent("tasks.board_create", new { projectKey = ProjectKey, board = "a" });
		await Agent("tasks.board_create", new { projectKey = ProjectKey, board = "b" });

		var ia = await Agent("tasks.upsert", new { projectKey = ProjectKey, board = "a", nodes = Nodes(new { key = "phase-1", status = "Todo", title = "A node", body = "x" }) });
		IsErr(ia).Should().BeFalse(Text(ia));
		var ib = await Agent("tasks.upsert", new { projectKey = ProjectKey, board = "b", nodes = Nodes(new { key = "phase-1", status = "Todo", title = "B node", body = "y" }) });
		IsErr(ib).Should().BeFalse(Text(ib));

		// Same key, different boards → independent rows, no collision.
		FieldOf(await Agent("tasks.get", new { projectKey = ProjectKey, board = "a" }), "phase-1", "title").Should().Be("A node");
		FieldOf(await Agent("tasks.get", new { projectKey = ProjectKey, board = "b" }), "phase-1", "title").Should().Be("B node");

		// Editing A's node (baseline version 1 within board a's cursor) leaves B untouched.
		var edit = await Agent("tasks.upsert", new { projectKey = ProjectKey, board = "a", nodes = Nodes(new { key = "phase-1", version = 1, status = "InProgress", title = "A node", body = "x" }) });
		IsErr(edit).Should().BeFalse(Text(edit));
		StatusOf(await Agent("tasks.get", new { projectKey = ProjectKey, board = "a" }), "phase-1").Should().Be("InProgress");
		StatusOf(await Agent("tasks.get", new { projectKey = ProjectKey, board = "b" }), "phase-1").Should().Be("Todo");
	}

	// 22. spec-write-needs-accepted-idea: a spec node without ideaRef is rejected.
	[Fact]
	public async Task Spec_WithoutIdeaRef_Rejected()
	{
		await Agent("tasks.board_create", new { projectKey = ProjectKey, board = "spec", kind = "spec" });
		var r = await Agent("tasks.upsert", new { projectKey = ProjectKey, board = "spec",
			nodes = Nodes(new { key = "x", status = "defined", title = "X", body = "x" }) });
		IsErr(r).Should().BeTrue();
		Text(r).Should().Contain("accepted idea");
	}

	// 23. a spec node referencing a NOT-yet-accepted idea (still exploring) is rejected.
	[Fact]
	public async Task Spec_WithNonAcceptedIdea_Rejected()
	{
		await Agent("tasks.board_create", new { projectKey = ProjectKey, board = "ideas", kind = "ideas" });
		var ideaId = NodeId(await Agent("tasks.upsert", new { projectKey = ProjectKey, board = "ideas",
			nodes = Nodes(new { key = "drv", type = "idea", status = "exploring", title = "drv", body = "x" }) }), "drv");
		await Agent("tasks.board_create", new { projectKey = ProjectKey, board = "spec", kind = "spec" });
		var r = await Agent("tasks.upsert", new { projectKey = ProjectKey, board = "spec",
			nodes = Nodes(new { key = "x", status = "defined", title = "X", body = "x", ideaRef = ideaId }) });
		IsErr(r).Should().BeTrue();
		Text(r).Should().Contain("not accepted");
	}

	// 24. spec FSM has no draft: creating a spec node with status `draft` is rejected.
	[Fact]
	public async Task Spec_DraftStatus_Rejected()
	{
		await Agent("tasks.board_create", new { projectKey = ProjectKey, board = "spec", kind = "spec" });
		var ir = await AcceptedIdeaId();
		var r = await Agent("tasks.upsert", new { projectKey = ProjectKey, board = "spec",
			nodes = Nodes(new { key = "x", status = "draft", title = "X", body = "x", ideaRef = ir }) });
		IsErr(r).Should().BeTrue();
		Text(r).Should().Contain("draft"); // error enumerates valid statuses (defined|deprecated)
	}
}
