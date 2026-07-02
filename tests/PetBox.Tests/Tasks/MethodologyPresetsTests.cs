using LinqToDB;
using Microsoft.Data.Sqlite;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Data;
using PetBox.Tasks.Services;
using PetBox.Tasks.Workflow;

namespace PetBox.Tests.Tasks;

// primitives-preset-quartet: the built-in processes are PRESET METHODOLOGY DEFINITIONS on
// the engine now — the hardcoded WorkflowCatalog is gone. Part 1 is the migration-
// equivalence snapshot: every preset kind's resolved workflows must be IDENTICAL to the
// old catalog's content (statuses, transitions, gates, types, initial, quick-add policy),
// hardcoded here as constants — statuses and transitions unchanged means live boards need
// NO data migration. Part 2 checks the wave-2 primitives that used to be imperative
// service code are now preset DATA (work link constraints, the ideas review gate, the tag
// axes).
public sealed class MethodologyPresetsTests
{
	// Everything resolves through the no-definition runtime — the same fallback path the
	// service uses for a project without a stored definition.
	static readonly MethodologyRuntime Runtime = MethodologyRuntime.PresetsOnly;

	// ── the old WorkflowCatalog content, snapshotted ─────────────────────────

	static readonly WorkflowStatus[] SimpleStatuses =
	[
		new("Todo", "Todo", StatusKind.Open),
		new("InProgress", "In progress", StatusKind.Open),
		new("Blocked", "Blocked", StatusKind.Open),
		new("Done", "Done", StatusKind.TerminalOk),
		new("Cancelled", "Cancelled", StatusKind.TerminalCancel),
	];

	static readonly WorkflowStatus[] WorkStatuses =
	[
		new("Pending", "Pending", StatusKind.Open),
		new("InProgress", "In progress", StatusKind.Open),
		new("Review", "Review", StatusKind.Open),
		new("Done", "Done", StatusKind.TerminalOk),
		new("Blocked", "Blocked", StatusKind.Open),
		new("Deferred", "Deferred", StatusKind.Open),
		new("Cancelled", "Cancelled", StatusKind.TerminalCancel),
	];

	static readonly WorkflowTransition[] WorkTransitions =
	[
		new("Pending", "InProgress"),
		new("InProgress", "Review"),
		new("Review", "InProgress"),
		new("Review", "Done", RequiresApproval: true),
		new("InProgress", "Blocked"),
		new("Blocked", "InProgress"),
		new("Pending", "Deferred"),
		new("Deferred", "Pending"),
		new("Pending", "Cancelled"),
		new("InProgress", "Cancelled"),
		new("Review", "Cancelled"),
	];

	static readonly WorkflowStatus[] SpecStatuses =
	[
		new("defined", "Defined", StatusKind.Open),
		new("deprecated", "Deprecated", StatusKind.TerminalCancel),
	];

	static readonly WorkflowStatus[] IdeaStatuses =
	[
		new("raw", "Raw", StatusKind.Open),
		new("exploring", "Exploring", StatusKind.Open),
		new("review", "Review", StatusKind.Open),
		new("deferred", "Deferred", StatusKind.Open),
		new("accepted", "Accepted", StatusKind.TerminalOk),
		new("rejected", "Rejected", StatusKind.TerminalCancel),
	];

	// The old catalog carried the exploring→review edge WITHOUT transition data (the
	// spec_plan gate was hardcoded in the service) — the FSM edge set itself is unchanged;
	// the gate as data is asserted separately below.
	static readonly (string From, string To, bool Approval, bool Reason)[] IdeaTransitions =
	[
		("raw", "exploring", false, false),
		("exploring", "review", false, false),
		("review", "accepted", true, false),
		("review", "exploring", false, false),
		("review", "rejected", false, true),
		("exploring", "rejected", false, true),
		("exploring", "deferred", false, false),
		("deferred", "exploring", false, false),
	];

	static readonly WorkflowStatus[] IssueStatuses =
	[
		new("reported", "Reported", StatusKind.Open),
		new("triage", "Triage", StatusKind.Open),
		new("confirmed", "Confirmed", StatusKind.Open),
		new("duplicate", "Duplicate", StatusKind.TerminalCancel),
		new("wontfix", "Won't fix", StatusKind.TerminalCancel),
		new("done", "Done", StatusKind.TerminalOk),
	];

	static readonly WorkflowTransition[] IssueTransitions =
	[
		new("reported", "triage"),
		new("triage", "confirmed"),
		new("triage", "duplicate", RequiresReason: true),
		new("triage", "wontfix", RequiresReason: true),
		new("confirmed", "done", RequiresApproval: true),
	];

	// ── Part 1: preset-vs-catalog equivalence ────────────────────────────────

	[Fact]
	public void WorkPreset_MatchesCatalog_ForEveryType()
	{
		foreach (var type in new[] { "feature", "bug", "chore" })
		{
			var wf = Runtime.For("work", type)!;
			wf.Statuses.Should().Equal(WorkStatuses, $"work/{type} statuses must be catalog-identical");
			wf.Transitions.Should().Equal(WorkTransitions, $"work/{type} transitions must be catalog-identical");
			wf.Initial.Should().Be("Pending");
		}
		// The "type required" contract survives: an unknown/empty work type has no workflow.
		Runtime.For("work", null).Should().BeNull();
		Runtime.For("work", "banana").Should().BeNull();
		Runtime.ValidTypes("work").Should().Be("feature|bug|chore");
	}

	[Fact]
	public void SimplePreset_MatchesCatalog_FreeTransitions_TypeIsLabel()
	{
		var wf = Runtime.For("simple", null)!;
		wf.Statuses.Should().Equal(SimpleStatuses);
		wf.Initial.Should().Be("Todo");

		// Free transitions = every ordered pair of distinct statuses, no gates.
		var expected = (from a in SimpleStatuses
						from b in SimpleStatuses
						where a.Slug != b.Slug
						select (a.Slug, b.Slug)).ToList();
		wf.Transitions.Select(t => (t.From, t.To)).Should().Equal(expected);
		wf.Transitions.Should().OnlyContain(t => !t.RequiresApproval && !t.RequiresReason && t.PreconditionArtifact == null);

		// Type is a label, not a branch — any type resolves the same FSM.
		var typed = Runtime.For("simple", "chore")!;
		typed.Statuses.Should().Equal(wf.Statuses);
		typed.Transitions.Should().Equal(wf.Transitions);

		MethodologyPresets.SimpleTypes.Should().Equal("task", "bug", "feature", "chore", "issue");
		Runtime.ValidTypes("simple").Should().Be("task|bug|feature|chore|issue");
	}

	[Fact]
	public void SpecPreset_MatchesCatalog()
	{
		var wf = Runtime.For("spec", "spec")!;
		wf.Statuses.Should().Equal(SpecStatuses);
		wf.Initial.Should().Be("defined");
		wf.Transitions.Should().Equal(new WorkflowTransition("defined", "deprecated"));
		Runtime.ValidTypes("spec").Should().Be("spec");
		// The catalog resolved spec's single FSM regardless of type (incl. untyped nodes).
		Runtime.For("spec", null)!.Statuses.Should().Equal(SpecStatuses);
	}

	[Fact]
	public void IdeasPreset_MatchesCatalog_EdgeSetUnchanged()
	{
		var wf = Runtime.For("ideas", "idea")!;
		wf.Statuses.Should().Equal(IdeaStatuses);
		wf.Initial.Should().Be("raw");
		wf.Transitions.Select(t => (t.From, t.To, t.RequiresApproval, t.RequiresReason))
			.Should().Equal(IdeaTransitions);
		Runtime.ValidTypes("ideas").Should().Be("idea");
	}

	[Fact]
	public void IntakePreset_MatchesCatalog()
	{
		var wf = Runtime.For("intake", "issue")!;
		wf.Statuses.Should().Equal(IssueStatuses);
		wf.Initial.Should().Be("reported");
		wf.Transitions.Should().Equal(IssueTransitions);
		Runtime.ValidTypes("intake").Should().Be("issue");
	}

	[Fact]
	public void QuickAddPolicy_AndDefaults_MatchCatalog()
	{
		// The single knob: only Spec and Work reject the bare quick-add form.
		MethodologyPresets.QuickAddAllowed(BoardKind.Simple).Should().BeTrue();
		MethodologyPresets.QuickAddAllowed(BoardKind.Ideas).Should().BeTrue();
		MethodologyPresets.QuickAddAllowed(BoardKind.Intake).Should().BeTrue();
		MethodologyPresets.QuickAddAllowed(BoardKind.Spec).Should().BeFalse();
		MethodologyPresets.QuickAddAllowed(BoardKind.Work).Should().BeFalse();

		// The untyped/quick-add default type per kind = first type of the first block.
		Runtime.DefaultType("ideas").Should().Be("idea");
		Runtime.DefaultType("spec").Should().Be("spec");
		Runtime.DefaultType("intake").Should().Be("issue");
		Runtime.DefaultType("simple").Should().Be("task");
	}

	[Fact]
	public void StatusClassification_MatchesCatalog()
	{
		// Cross-preset slug classification (spec delivery, closed predicate, indexability).
		Runtime.IsTerminalSlug("Done").Should().BeTrue();
		Runtime.IsTerminalSlug("Cancelled").Should().BeTrue();
		Runtime.IsTerminalSlug("deprecated").Should().BeTrue();
		Runtime.IsTerminalSlug("accepted").Should().BeTrue();
		Runtime.IsTerminalSlug("rejected").Should().BeTrue();
		Runtime.IsTerminalSlug("wontfix").Should().BeTrue();
		Runtime.IsTerminalSlug("Blocked").Should().BeFalse();
		Runtime.IsTerminalSlug("review").Should().BeFalse();
		Runtime.KindOfSlug("not-a-status").Should().BeNull(); // legacy/unknown slug
		MethodologyPresets.ParseKind("free").Should().Be(BoardKind.Simple); // M029 legacy mapping
	}

	// ── Part 2: the wave-2 primitives are preset DATA now ────────────────────

	[Fact]
	public void WorkPreset_LinkConstraints_FeatureBugNeedSpec_ChoreExempt()
	{
		var constraints = Runtime.LinkConstraints("work");
		constraints.Select(c => (c.Type, c.Link)).Should().Equal(
			("feature", "task_spec"),
			("bug", "task_spec"));
		// chore is exempt BECAUSE no constraint names it — the exemption is data-shaped.
		constraints.Should().NotContain(c => c.Type == "chore");
		// No other preset kind constrains creation.
		foreach (var kind in new[] { "simple", "spec", "ideas", "intake" })
			Runtime.LinkConstraints(kind).Should().BeEmpty();
	}

	[Fact]
	public void IdeasPreset_ReviewGate_IsTransitionData()
	{
		var wf = Runtime.For("ideas", "idea")!;
		wf.Transition("exploring", "review")!.PreconditionArtifact.Should().Be("spec_plan");

		// ...and it is the ONLY precondition artifact in the whole preset surface.
		foreach (var kind in new[] { "simple", "spec", "ideas", "intake", "work" })
			foreach (var w in Runtime.Types(kind))
				w.Transitions.Where(t => t.PreconditionArtifact is not null)
					.Should().BeEquivalentTo(kind == "ideas"
						? [new WorkflowTransition("exploring", "review", PreconditionArtifact: "spec_plan")]
						: Array.Empty<WorkflowTransition>());
	}

	[Fact]
	public void PresetTagAxes_QuartetBuiltinPair_SimpleNone()
	{
		foreach (var kind in new[] { "spec", "ideas", "intake", "work" })
			Runtime.TagAxes(kind).Select(a => a.Namespace).Should().Equal("area", "concern");
		// simple declares NO axes → axes-emptiness = free-form tags, the one rule.
		Runtime.TagAxes("simple").Should().BeEmpty();
	}
}

// The preset-driven guards exercised through the SERVICE (the paths that used to be the
// hardcoded RequireSpecLinks / RequireSpecPlanForReviewAsync / TagStore builtin pair).
[Collection("DataModule")]
public sealed class MethodologyPresetGuardsTests : IDisposable
{
	const string Proj = "proj";
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<TasksDb> _factory;
	readonly TasksService _tasks;
	readonly CommentService _comments;

	public MethodologyPresetGuardsTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-preset-guards-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });
		_factory = new ScopedDbFactory<TasksDb>(Path.Combine(_dir, "tasks"), Scope.Project,
			c => new TasksDb(TasksDb.CreateOptions(c)), TasksSchema.Ensure);
		_comments = new CommentService(_factory);
		_tasks = new TasksService(new TaskBoardStore(_db, _factory), new RelationStore(_db), new TagStore(_factory), _comments);
	}

	public void Dispose()
	{
		_db.Dispose();
		_factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		SqliteConnection.ClearAllPools();
		if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
	}

	Task<UpsertOutcome> Upsert(string board, params NodePatch[] nodes) =>
		_tasks.UpsertAsync(Proj, board, nodes);

	// An accepted idea + a defined spec node — the prerequisites of any work feature/bug.
	async Task<string> DefinedSpecNodeId()
	{
		await _tasks.CreateBoardAsync(Proj, "ideas", "ideas", null, null);
		await _tasks.CreateBoardAsync(Proj, "spec", "spec", null, null);
		// Direct-to-accepted at creation is legal (no transition fires at birth; only
		// gated TARGETS are refused, and accepted carries no precondition artifact).
		await Upsert("ideas", new NodePatch { Key = "why", Status = "accepted", Title = "Why", Body = "x" });
		var ideaId = (await _tasks.GetAsync(Proj, "ideas", includeClosed: true)).Nodes.Single().NodeId;
		await Upsert("spec", new NodePatch { Key = "root", Status = "defined", Title = "Root", Body = "x", IdeaRef = ideaId });
		return (await _tasks.GetAsync(Proj, "spec")).Nodes.Single().NodeId;
	}

	[Fact]
	public async Task Work_FeatureAndBug_RequireSpecRef_ChoreExempt_ViaPresetData()
	{
		var specId = await DefinedSpecNodeId();
		await _tasks.CreateBoardAsync(Proj, "work", "work", null, null);

		var feature = () => Upsert("work", new NodePatch { Key = "f", Type = "feature", Title = "F", Body = "x" });
		(await feature.Should().ThrowAsync<ArgumentException>())
			.WithMessage("a work feature must link a spec node — provide specRef (node 'f')");

		var bug = () => Upsert("work", new NodePatch { Key = "b", Type = "bug", Title = "B", Body = "x" });
		(await bug.Should().ThrowAsync<ArgumentException>())
			.WithMessage("a work bug must link a spec node — provide specRef (node 'b')");

		// chore needs no spec link (below-spec hygiene) — the preset simply has no
		// constraint for it; feature/bug pass once specRef is supplied.
		await Upsert("work", new NodePatch { Key = "c", Type = "chore", Title = "C", Body = "x" });
		await Upsert("work", new NodePatch { Key = "f", Type = "feature", Title = "F", Body = "x", SpecRef = specId });
		(await _tasks.GetAsync(Proj, "work")).Nodes.Select(n => n.Key).Should().BeEquivalentTo(["c", "f"]);
	}

	[Fact]
	public async Task Idea_ExploringToReview_GatedOnSpecPlanArtifact_ViaPresetData()
	{
		await _tasks.CreateBoardAsync(Proj, "ideas", "ideas", null, null);
		await Upsert("ideas", new NodePatch { Key = "i", Status = "exploring", Title = "I", Body = "x" });
		var node = (await _tasks.GetAsync(Proj, "ideas")).Nodes.Single();

		// Without the artifact the transition is refused, naming the required comment tag.
		var toReview = () => Upsert("ideas", new NodePatch { Key = "i", Status = "review", Version = node.Version });
		(await toReview.Should().ThrowAsync<InvalidOperationException>())
			.WithMessage("*artifact:spec_plan*");

		// Being born straight into the gated status is refused too.
		var bornInReview = () => Upsert("ideas", new NodePatch { Key = "i2", Status = "review", Title = "I2", Body = "x" });
		(await bornInReview.Should().ThrowAsync<InvalidOperationException>())
			.WithMessage("*artifact:spec_plan*");

		// With an artifact:spec_plan comment the same transition applies.
		await _comments.AddAsync(Proj, "ideas", node.NodeId, parentId: null, "t", "the plan", ["artifact:spec_plan"]);
		var ok = await Upsert("ideas", new NodePatch { Key = "i", Status = "review", Version = node.Version });
		ok.Result.Applied.Should().BeTrue();
		(await _tasks.GetAsync(Proj, "ideas")).Nodes.Single().Status.Should().Be("review");
	}

	[Fact]
	public async Task Tags_QuartetEnforcesBuiltinAxes_SimpleStaysFreeForm()
	{
		await _tasks.CreateBoardAsync(Proj, "ideas", "ideas", null, null);
		await _tasks.CreateBoardAsync(Proj, "scratch", null, null, null); // simple

		// Quartet board: the preset axes (area/concern) are the allowlist.
		await Upsert("ideas", new NodePatch { Key = "i", Status = "raw", Title = "I", Body = "x", Tags = ["area:ui", "concern:security"] });
		(await _tasks.GetAsync(Proj, "ideas")).Nodes.Single().Tags.Should().Equal("area:ui", "concern:security");

		var badNs = () => Upsert("ideas", new NodePatch { Key = "i2", Status = "raw", Title = "I2", Body = "x", Tags = ["severity:high"] });
		(await badNs.Should().ThrowAsync<ArgumentException>()).WithMessage("*unknown tag namespace*");
		var bare = () => Upsert("ideas", new NodePatch { Key = "i3", Status = "raw", Title = "I3", Body = "x", Tags = ["urgent"] });
		(await bare.Should().ThrowAsync<ArgumentException>()).WithMessage("*namespace:value*");

		// Simple board: no axes → free-form (any namespace; a bare word files under tag:).
		await Upsert("scratch", new NodePatch { Key = "s", Status = "Todo", Title = "S", Body = "x", Tags = ["severity:high", "urgent"] });
		(await _tasks.GetAsync(Proj, "scratch")).Nodes.Single().Tags.Should().Equal("severity:high", "tag:urgent");
	}
}
