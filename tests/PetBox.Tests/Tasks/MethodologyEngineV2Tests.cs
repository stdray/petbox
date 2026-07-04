using System.Security.Claims;
using LinqToDB;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using PetBox.Core.Data;
using PetBox.Core.Features;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Data;
using PetBox.Tasks.Services;
using PetBox.Tasks.Workflow;
using PetBox.Web.Mcp;

namespace PetBox.Tests.Tasks;

// engine-v2-quartet-parity: the schema-v2 primitives EXECUTE for definition-declared
// kinds — transition effects fire end-to-end through the service, link-target
// declarations (TargetKind/TargetStatuses) guard writes, and enforceApproval transitions
// demand an approving actor (tasks:approve at the MCP door; the UI owner as
// TasksActor.Approver). The quartet parity itself is covered by the existing preset/smoke
// tests; this file proves the same machinery works for a kind no preset ever heard of.
[Collection("DataModule")]
public sealed class MethodologyEngineV2Tests : IDisposable
{
	const string Proj = "proj";
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<TasksDb> _factory;
	readonly RelationStore _relations;
	readonly TasksService _tasks;

	public MethodologyEngineV2Tests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-engine-v2-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });
		_factory = new ScopedDbFactory<TasksDb>(Path.Combine(_dir, "tasks"), Scope.Project,
			c => new TasksDb(TasksDb.CreateOptions(c)), TasksSchema.Ensure);
		_relations = new RelationStore(_db);
		_tasks = new TasksService(new TaskBoardStore(_db, _factory), _relations, new TagStore(_factory), new CommentService(_factory));
	}

	public void Dispose()
	{
		_db.Dispose();
		_factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		SqliteConnection.ClearAllPools();
		if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
	}

	// A methodology no preset knows: `ticket` declares a transition EFFECT over the
	// project-declared `spawned` link; `job` declares a link constraint WITH a target
	// (task_spec -> an Open ticket); `case` declares an ENFORCED approval gate.
	static MethodologyDefinition Def() => new("engine-v2",
	[
		new MethodologyKindDef("ticket", QuickAddAllowed: true,
		[
			new MethodologyWorkflowDef(["ticket"],
				[
					new("Open", "Open", StatusKind.Open),
					new("Waiting", "Waiting", StatusKind.Open),
					new("Resolved", "Resolved", StatusKind.TerminalOk),
				],
				[
					new("Open", "Resolved"),
					new("Open", "Waiting"),
					new("Waiting", "Open"),
				]),
		])
		{
			// When a ticket resolves, every ticket that points at it through `spawned`
			// (incoming) and is still Open resolves too.
			Effects = [new MethodologyTransitionEffectDef("Resolved", "spawned", "incoming", "Resolved", OnlyFrom: "Open")],
		},
		new MethodologyKindDef("job", QuickAddAllowed: true,
		[
			new MethodologyWorkflowDef(["job"],
				[
					new("Todo", "Todo", StatusKind.Open),
					new("Finished", "Finished", StatusKind.TerminalOk),
				],
				[
					new("Todo", "Finished"),
				]),
		])
		{
			LinkConstraints = [new MethodologyLinkConstraintDef("job", "task_spec") { TargetKind = "ticket", TargetStatuses = ["Open"] }],
		},
		new MethodologyKindDef("case", QuickAddAllowed: true,
		[
			new MethodologyWorkflowDef(["case"],
				[
					new("Open", "Open", StatusKind.Open),
					new("Closed", "Closed", StatusKind.TerminalOk),
				],
				[
					new("Open", "Closed", RequiresApproval: true) { EnforceApproval = true },
				]),
		]),
	])
	{
		LinkKinds = [new MethodologyLinkKindDef("spawned", "spawned-by provenance")],
	};

	Task<UpsertOutcome> Upsert(string board, params NodePatch[] nodes) =>
		_tasks.UpsertAsync(Proj, board, nodes);

	async Task<(string NodeId, long Version, string Status)> NodeInfo(string board, string key)
	{
		var n = (await _tasks.GetAsync(Proj, board, includeClosed: true)).Nodes.Single(x => x.Key == key);
		return (n.NodeId, n.Version, n.Status);
	}

	// ── 1. a definition-declared effect executes end-to-end through the service ──

	[Fact]
	public async Task Effect_OnDefinitionKind_ExecutesOnEnteringStatus_FilteredByOnlyFrom()
	{
		await _tasks.DefineMethodologyAsync(Proj, Def(), 0);
		await _tasks.CreateBoardAsync(Proj, "tickets", "ticket", null, null);
		await Upsert("tickets",
			new NodePatch { Key = "root", Type = "ticket", Status = "Open", Title = "R", Body = "x" },
			new NodePatch { Key = "follower", Type = "ticket", Status = "Open", Title = "F", Body = "x" },
			new NodePatch { Key = "parked", Type = "ticket", Status = "Waiting", Title = "P", Body = "x" });
		var root = await NodeInfo("tickets", "root");
		var follower = await NodeInfo("tickets", "follower");
		var parked = await NodeInfo("tickets", "parked");

		// follower/parked point AT root: incoming edges relative to the transitioned node.
		await _relations.CreateAsync(Proj, "spawned", follower.NodeId, root.NodeId);
		await _relations.CreateAsync(Proj, "spawned", parked.NodeId, root.NodeId);

		await Upsert("tickets", new NodePatch { Key = "root", Status = "Resolved", Version = root.Version });

		(await NodeInfo("tickets", "follower")).Status.Should().Be("Resolved",
			"the declared effect resolves incoming `spawned` tickets still in Open");
		(await NodeInfo("tickets", "parked")).Status.Should().Be("Waiting",
			"OnlyFrom=Open excludes a Waiting ticket from the effect");
	}

	[Fact]
	public async Task Effect_DoesNotRefire_WhenStatusUnchanged()
	{
		await _tasks.DefineMethodologyAsync(Proj, Def(), 0);
		await _tasks.CreateBoardAsync(Proj, "tickets", "ticket", null, null);
		await Upsert("tickets", new NodePatch { Key = "root", Type = "ticket", Status = "Open", Title = "R", Body = "x" });
		var root = await NodeInfo("tickets", "root");
		await Upsert("tickets", new NodePatch { Key = "root", Status = "Resolved", Version = root.Version });

		// Linked AFTER the transition; an edit that does not re-enter the status fires nothing.
		await Upsert("tickets", new NodePatch { Key = "late", Type = "ticket", Status = "Open", Title = "L", Body = "x" });
		var late = await NodeInfo("tickets", "late");
		await _relations.CreateAsync(Proj, "spawned", late.NodeId, root.NodeId);
		var resolved = await NodeInfo("tickets", "root");
		await Upsert("tickets", new NodePatch { Key = "root", Status = "Resolved", Version = resolved.Version, Title = "R2" });

		(await NodeInfo("tickets", "late")).Status.Should().Be("Open",
			"effects fire on ENTERING the status, not on every edit of a node already in it");
	}

	// ── 2. link-target guard: positive + negatives (wrong kind, wrong status) ──

	[Fact]
	public async Task LinkTargetGuard_AcceptsMatchingTarget_CreatesEdge()
	{
		await _tasks.DefineMethodologyAsync(Proj, Def(), 0);
		await _tasks.CreateBoardAsync(Proj, "tickets", "ticket", null, null);
		await _tasks.CreateBoardAsync(Proj, "jobs", "job", null, null);
		await Upsert("tickets", new NodePatch { Key = "t", Type = "ticket", Status = "Open", Title = "T", Body = "x" });
		var ticket = await NodeInfo("tickets", "t");

		await Upsert("jobs", new NodePatch { Key = "j", Type = "job", Status = "Todo", Title = "J", Body = "x", SpecRef = ticket.NodeId });

		var job = await NodeInfo("jobs", "j");
		(await _relations.ListAsync(Proj, ticket.NodeId, "to"))
			.Should().ContainSingle(e => e.Kind == "task_spec" && e.FromNodeId == job.NodeId,
				"the constrained link lands as a task_spec edge job -> ticket");
	}

	[Fact]
	public async Task LinkTargetGuard_RejectsWrongKind_AndWrongStatus_AndMissingLink()
	{
		await _tasks.DefineMethodologyAsync(Proj, Def(), 0);
		await _tasks.CreateBoardAsync(Proj, "tickets", "ticket", null, null);
		await _tasks.CreateBoardAsync(Proj, "jobs", "job", null, null);
		await _tasks.CreateBoardAsync(Proj, "misc", null, null, null); // simple
		await Upsert("tickets", new NodePatch { Key = "t-res", Type = "ticket", Status = "Resolved", Title = "T", Body = "x" });
		await Upsert("misc", new NodePatch { Key = "m", Status = "Todo", Title = "M", Body = "x" });
		var wrongKind = await NodeInfo("misc", "m");
		var wrongStatus = await NodeInfo("tickets", "t-res");

		var kind = () => Upsert("jobs", new NodePatch { Key = "j1", Type = "job", Status = "Todo", Title = "J", Body = "x", SpecRef = wrongKind.NodeId });
		(await kind.Should().ThrowAsync<ArgumentException>())
			.WithMessage("*is not a ticket board*");

		var status = () => Upsert("jobs", new NodePatch { Key = "j2", Type = "job", Status = "Todo", Title = "J", Body = "x", SpecRef = wrongStatus.NodeId });
		(await status.Should().ThrowAsync<ArgumentException>())
			.WithMessage("*'Resolved', not Open*");

		var missing = () => Upsert("jobs", new NodePatch { Key = "j3", Type = "job", Status = "Todo", Title = "J", Body = "x" });
		(await missing.Should().ThrowAsync<ArgumentException>())
			.WithMessage("a job job must link a ticket node — provide specRef (node 'j3')");
	}

	// ── 3. enforceApproval through the MCP door: the SESSION key's scopes decide ──

	[Fact]
	public async Task EnforcedApproval_McpDoor_RejectedWithoutApproveScope_AllowedWithIt()
	{
		await _tasks.DefineMethodologyAsync(Proj, Def(), 0);
		await _tasks.CreateBoardAsync(Proj, "cases", "case", null, null);

		var agent = Http("tasks:read,tasks:write");
		await TasksTools.UpsertAsync(agent, Flags(), _tasks, Proj, "cases",
			McpInputs.NodesJson("""[{"key":"c1","type":"case","status":"Open","title":"C","body":"x"}]"""));

		// Without tasks:approve the ENFORCED gate blocks the transition...
		var move = () => TasksTools.UpsertAsync(agent, Flags(), _tasks, Proj, "cases",
			McpInputs.NodesJson("""[{"key":"c1","status":"Closed","version":1}]"""));
		(await move.Should().ThrowAsync<ArgumentException>())
			.WithMessage("*'Open' -> 'Closed' requires maintainer approval*");

		// ...and birth straight into the gated status is an approval too.
		var born = () => TasksTools.UpsertAsync(agent, Flags(), _tasks, Proj, "cases",
			McpInputs.NodesJson("""[{"key":"c2","type":"case","status":"Closed","title":"C2","body":"x"}]"""));
		(await born.Should().ThrowAsync<ArgumentException>())
			.WithMessage("*only a maintainer can set status 'Closed'*");

		// A key holding tasks:approve performs the same transition.
		var approver = Http("tasks:read,tasks:write,tasks:approve");
		await TasksTools.UpsertAsync(approver, Flags(), _tasks, Proj, "cases",
			McpInputs.NodesJson("""[{"key":"c1","status":"Closed","version":1}]"""));
		(await NodeInfo("cases", "c1")).Status.Should().Be("Closed");
	}

	// The UI door counts the cookie-authenticated owner as an approver — the page passes
	// TasksActor.Approver; the service-level equivalent is asserted here.
	[Fact]
	public async Task EnforcedApproval_UiActor_Allowed_DefaultActorBlocked()
	{
		await _tasks.DefineMethodologyAsync(Proj, Def(), 0);
		await _tasks.CreateBoardAsync(Proj, "cases", "case", null, null);
		await Upsert("cases", new NodePatch { Key = "c1", Type = "case", Status = "Open", Title = "C", Body = "x" });

		var anonymous = () => _tasks.UpsertAsync(Proj, "cases", [new NodePatch { Key = "c1", Status = "Closed", Version = 1 }]);
		(await anonymous.Should().ThrowAsync<ArgumentException>())
			.WithMessage("*requires maintainer approval*");

		await _tasks.UpsertAsync(Proj, "cases", [new NodePatch { Key = "c1", Status = "Closed", Version = 1 }], TasksActor.Approver);
		(await NodeInfo("cases", "c1")).Status.Should().Be("Closed");
	}

	// Presets stay UNENFORCED: the work approve gate (Review -> Done) still passes for a
	// plain agent — live behavior of every existing project is unchanged.
	[Fact]
	public void Presets_DeclareNoEnforcedApproval()
	{
		foreach (var kind in new[] { "simple", "spec", "ideas", "intake", "work" })
			foreach (var wf in MethodologyRuntime.PresetsOnly.Types(kind))
				wf.Transitions.Should().OnlyContain(t => !t.EnforceApproval,
					$"the builtin presets must not enforce approval (kind '{kind}')");
	}

	static IHttpContextAccessor Http(string scopes)
	{
		var id = new ClaimsIdentity([new Claim("project", Proj), new Claim("scopes", scopes)], "test");
		var ctx = new DefaultHttpContext { User = new ClaimsPrincipal(id) };
		ctx.Request.Scheme = "https";
		ctx.Request.Host = new HostString("box.test");
		return new HttpContextAccessor { HttpContext = ctx };
	}

	static FeatureFlags Flags()
	{
		var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
		{
			["Features:Tasks"] = "true",
		}).Build();
		return new FeatureFlags(cfg);
	}
}
