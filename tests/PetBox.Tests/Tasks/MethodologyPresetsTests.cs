using LinqToDB;
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
		MethodologyPresets.QuickAddAllowed(BoardKind.Classic).Should().BeTrue();
		MethodologyPresets.QuickAddAllowed(BoardKind.Ideas).Should().BeTrue();
		MethodologyPresets.QuickAddAllowed(BoardKind.Intake).Should().BeTrue();
		MethodologyPresets.QuickAddAllowed(BoardKind.Spec).Should().BeFalse();
		MethodologyPresets.QuickAddAllowed(BoardKind.Work).Should().BeFalse();

		// The untyped/quick-add default type per kind = first type of the first block.
		Runtime.DefaultType("ideas").Should().Be("idea");
		Runtime.DefaultType("spec").Should().Be("spec");
		Runtime.DefaultType("intake").Should().Be("issue");
		Runtime.DefaultType("simple").Should().Be("task");
		Runtime.DefaultType("classic").Should().Be("task");
	}

	// ── the `classic` preset (spec preset-classic): a single-kind status model at the
	// level of the GitHub/Jira/Linear defaults ──────────────────────────────────────

	static readonly WorkflowStatus[] ClassicStatuses =
	[
		new("Backlog", "Backlog", StatusKind.Open),
		new("Todo", "Todo", StatusKind.Open),
		new("InProgress", "In progress", StatusKind.Open),
		new("InReview", "In review", StatusKind.Open),
		new("Done", "Done", StatusKind.TerminalOk),
		new("Cancelled", "Cancelled", StatusKind.TerminalCancel),
		new("Duplicate", "Duplicate", StatusKind.TerminalCancel),
	];

	// The full classic edge set: free among the open statuses, explicit closes (a reason
	// only into Duplicate — Done and Cancelled are ungated), reopen-to-Todo from every
	// terminal.
	static IReadOnlyList<(string From, string To, bool Reason)> ClassicEdges()
	{
		var open = ClassicStatuses.Where(s => s.Kind == StatusKind.Open).Select(s => s.Slug).ToList();
		var edges = new List<(string, string, bool)>();
		foreach (var from in open)
			foreach (var to in open.Where(t => t != from))
				edges.Add((from, to, false));
		foreach (var from in open)
		{
			edges.Add((from, "Done", false));
			edges.Add((from, "Cancelled", false));
			edges.Add((from, "Duplicate", true));
		}
		foreach (var terminal in new[] { "Done", "Cancelled", "Duplicate" })
			edges.Add((terminal, "Todo", false));
		return edges;
	}

	[Fact]
	public void ClassicPreset_Snapshot_Statuses_Transitions_Gates()
	{
		// Every type resolves the SAME status vocabulary and edge set — type is a label
		// over one FSM, not a branch.
		foreach (var type in new[] { "task", "feature", "bug" })
		{
			var wf = Runtime.For("classic", type)!;
			wf.Statuses.Should().Equal(ClassicStatuses, $"classic/{type} statuses are the snapshot");
			wf.Initial.Should().Be("Backlog");
			wf.Transitions.Select(t => (t.From, t.To, t.RequiresReason))
				.Should().BeEquivalentTo(ClassicEdges(), $"classic/{type} edges are the snapshot");
			// Duplicate is the ONE reason-gated target (a duplicate without a pointer to
			// the original is useless; Cancelled closes reason-free, the GitHub way).
			wf.Transitions.Where(t => t.RequiresReason).Should().OnlyContain(t => t.To == "Duplicate");
			// No approval gates at all, no precondition artifacts — low-ceremony by design.
			wf.Transitions.Should().OnlyContain(t =>
				!t.RequiresApproval && !t.EnforceApproval && t.PreconditionArtifact == null);
		}
		Runtime.ValidTypes("classic").Should().Be("task|feature|bug");
	}

	[Fact]
	public void ClassicPreset_SingleBlock_TypeIsLabel_UnknownTypeRejected()
	{
		// Untyped resolves the default type (quick-add contract)...
		var untyped = Runtime.For("classic", null)!;
		untyped.Type.Should().Be("task");
		untyped.Initial.Should().Be("Backlog");
		// ...and the type VOCABULARY is still a door: an out-of-vocab type yields null —
		// classic is strict like work (the engine names the valid types), even though the
		// single block would fit any label.
		Runtime.For("classic", "banana").Should().BeNull();
		// ONE block: task|feature|bug are labels over the same FSM (owner review: identical
		// per-type state machines are one state machine).
		var block = Runtime.Blocks("classic").Should().ContainSingle().Subject;
		block.Types.Should().Equal("task", "feature", "bug");
		MethodologyPresets.ParseKind("classic").Should().Be(BoardKind.Classic);
	}

	[Fact]
	public void ClassicPreset_NoChecklists_AnywhereInTheKind()
	{
		// The former bug-only repro checklist is GONE from the preset (its semantics moved
		// to a deliberation idea) — no classic transition carries checklist data, so the
		// task and bug renderings are identical by construction.
		var kind = MethodologyPresets.KindDef(BoardKind.Classic);
		kind.QuickAddAllowed.Should().BeTrue();
		kind.Workflows.Should().ContainSingle().Which.Transitions
			.Should().OnlyContain(t => t.Checklist == null || t.Checklist.Count == 0);
	}

	[Fact]
	public void ClassicPreset_LiveBoards_EveryPreReworkStatusStillResolves()
	{
		// Rework guarantee: the status vocabulary is unchanged, so a live node parked in
		// ANY status — from either former block — stays valid under the single block.
		foreach (var type in new[] { "task", "feature", "bug" })
		{
			var wf = Runtime.For("classic", type)!;
			foreach (var slug in new[] { "Backlog", "Todo", "InProgress", "InReview", "Done", "Cancelled", "Duplicate" })
				wf.Statuses.Should().Contain(s => s.Slug == slug, $"a live {type} in {slug} must stay valid");
		}
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
		Runtime.IsTerminalSlug("Duplicate").Should().BeTrue(); // intake + classic agree: TerminalCancel
		Runtime.IsTerminalSlug("Blocked").Should().BeFalse();
		Runtime.IsTerminalSlug("review").Should().BeFalse();
		// classic's new open slugs classify as open, not legacy-unknown.
		Runtime.KindOfSlug("Backlog").Should().Be(StatusKind.Open);
		Runtime.KindOfSlug("InReview").Should().Be(StatusKind.Open);
		Runtime.KindOfSlug("not-a-status").Should().BeNull(); // legacy/unknown slug
		MethodologyPresets.ParseKind("free").Should().Be(BoardKind.Simple); // M029 legacy mapping
	}

	// ── Part 2: the wave-2 primitives are preset DATA now ────────────────────

	[Fact]
	public void WorkPreset_LinkConstraints_FeatureBugNeedSpec_ChoreExempt()
	{
		// Schema v2: the constraints now DECLARE their target — a specRef must point at a
		// spec-kind node (the guard the service used to hardcode).
		var constraints = Runtime.LinkConstraints("work");
		constraints.Select(c => (c.Type, c.Link, c.TargetKind)).Should().Equal(
			("feature", "task_spec", "spec"),
			("bug", "task_spec", "spec"));
		constraints.Should().OnlyContain(c => c.TargetStatuses == null); // any spec status links
																		 // chore is exempt BECAUSE no constraint names it — the exemption is data-shaped.
		constraints.Should().NotContain(c => c.Type == "chore");
		// Besides spec's ideaRef governance (its own fact below), no other preset kind
		// constrains creation.
		foreach (var kind in new[] { "simple", "classic", "ideas", "intake" })
			Runtime.LinkConstraints(kind).Should().BeEmpty();
	}

	// Schema v2 (engine-v2-quartet-parity): spec-write-needs-accepted-idea is constraint
	// DATA — link idea_spec targeting an ideas node in `accepted`. idea_spec is a
	// provenance link, so the service requires it on EVERY write of the type.
	[Fact]
	public void SpecPreset_IdeaRefGovernance_IsConstraintData()
	{
		var c = Runtime.LinkConstraints("spec").Should().ContainSingle().Subject;
		(c.Type, c.Link, c.TargetKind).Should().Be(("spec", "idea_spec", "ideas"));
		c.TargetStatuses.Should().Equal("accepted");
	}

	// Schema v2 (engine-v2-quartet-parity): the Done automation is effect DATA on the work
	// preset — intake auto-close rides the INCOMING issue_task edge (issue -> task), the
	// unblock rides the OUTGOING blocks edge (blocker -> blocked) gated on OnlyFrom=Blocked.
	[Fact]
	public void WorkPreset_DoneAutomation_IsEffectData()
	{
		Runtime.Effects("work").Should().Equal(
			new MethodologyTransitionEffectDef("Done", "issue_task", "incoming", "done"),
			new MethodologyTransitionEffectDef("Done", "blocks", "outgoing", "InProgress", "Blocked"));
		// No other preset kind declares effects.
		foreach (var kind in new[] { "simple", "classic", "spec", "ideas", "intake" })
			Runtime.Effects(kind).Should().BeEmpty();
	}

	[Fact]
	public void IdeasPreset_ReviewGate_IsTransitionData()
	{
		var wf = Runtime.For("ideas", "idea")!;
		wf.Transition("exploring", "review")!.PreconditionArtifact.Should().Be("spec_plan");

		// ...and it is the ONLY precondition artifact in the whole preset surface.
		foreach (var kind in new[] { "simple", "classic", "spec", "ideas", "intake", "work" })
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
		// simple and classic declare NO axes → axes-emptiness = free-form tags, the one rule.
		Runtime.TagAxes("simple").Should().BeEmpty();
		Runtime.TagAxes("classic").Should().BeEmpty();
	}

	// ── Part 3: the provisioning-preset registry (enable + copy-as-definition) ──

	[Fact]
	public void ProvisioningRegistry_ResolvesQuartet_CaseInsensitive_DefaultsOnBlank()
	{
		var expected = new[] { BoardKind.Intake, BoardKind.Ideas, BoardKind.Spec, BoardKind.Work };
		foreach (var slug in new[] { "quartet", "QUARTET", " Quartet ", null, "" })
			MethodologyPresets.ResolveProvisioningPreset(slug).Kinds.Should().Equal(expected,
				$"'{slug ?? "<null>"}' must resolve the quartet (default) in pipeline order");
		MethodologyPresets.DefaultProvisioningPreset.Should().Be("quartet");
	}

	[Fact]
	public void ProvisioningRegistry_UnknownPreset_ErrorListsAvailableSlugs()
	{
		var act = () => MethodologyPresets.ResolveProvisioningPreset("banana");
		act.Should().Throw<ArgumentException>()
			.WithMessage("*unknown methodology preset 'banana'*")
			.WithMessage("*quartet*")   // names the available slugs...
			.WithMessage("*classic*");  // ...both of them
	}

	[Fact]
	public void ProvisioningRegistry_Classic_OneStandaloneBoardKind()
	{
		MethodologyPresets.ResolveProvisioningPreset("classic").Kinds.Should().Equal(BoardKind.Classic);
	}

	[Fact]
	public void RenderPresetDefinition_Classic_MirrorsPresetShape_NoAxes()
	{
		var def = MethodologyPresets.RenderPresetDefinition("classic");
		def.Name.Should().Be("classic");
		def.Kinds.Should().ContainSingle().Which.Should().BeEquivalentTo(MethodologyPresets.KindDef(BoardKind.Classic));
		// classic's tag posture travels with the copy: NO axes = free-form tags (the quartet
		// render keeps area/concern — its own test above).
		def.TagAxes.Should().BeEmpty();
		def.LinkKinds.Should().BeEmpty();
	}

	[Fact]
	public void RenderPresetDefinition_Quartet_MirrorsPresetShapes()
	{
		var def = MethodologyPresets.RenderPresetDefinition("quartet");
		// The preset slug is the definition name (a valid slug); one KindDef per board kind,
		// in pipeline order; the builtin tag axes carry over as tagAxes.
		def.Name.Should().Be("quartet");
		def.Kinds.Select(k => k.Kind).Should().Equal("intake", "ideas", "spec", "work");
		def.TagAxes.Select(a => a.Namespace).Should().Equal("area", "concern");

		// Each rendered kind IS the preset KindDef — same workflows/statuses/transitions and
		// the work link constraints (feature/bug → task_spec) verbatim.
		foreach (var kind in new[] { BoardKind.Intake, BoardKind.Ideas, BoardKind.Spec, BoardKind.Work })
		{
			var rendered = def.Kinds.Single(k => string.Equals(k.Kind, kind.ToString(), StringComparison.OrdinalIgnoreCase));
			rendered.Should().BeEquivalentTo(MethodologyPresets.KindDef(kind));
		}
	}
}

// The preset-driven guards exercised through the SERVICE (the paths that used to be the
// hardcoded RequireSpecLinks / RequireSpecPlanForReviewAsync / TagStore builtin pair).
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
		_tasks = new TasksService(new TaskBoardStore(_db, _factory), new RelationStore(_factory), new TagStore(_factory), _comments);
	}

	public void Dispose()
	{
		_db.Dispose();
		_factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		TestDirs.CleanupOrDefer(_dir);
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

	// The classic preset end-to-end through the service: standalone (non-singleton) board
	// creation, the quick-add default, free open-status movement, the ungated Cancelled,
	// the reason gate into Duplicate, the type-vocabulary door, the reopen edge.
	[Fact]
	public async Task Classic_Board_EndToEnd_QuickAdd_ReasonGate_TypeDoor()
	{
		// Standalone AND unlimited — no quartet singleton rule for classic.
		await _tasks.CreateBoardAsync(Proj, "backlog", "classic", null, null);
		await _tasks.CreateBoardAsync(Proj, "backlog2", "classic", null, null);

		// Quick-add creates a `task` born in Backlog.
		await _tasks.QuickAddAsync(Proj, "backlog", "First thing", null, 0);
		var node = (await _tasks.GetAsync(Proj, "backlog")).Nodes.Single();
		node.Type.Should().Be("task");
		node.Status.Should().Be("Backlog");

		// Open statuses move freely (Backlog → InProgress skips Todo, no gate).
		await Upsert("backlog", new NodePatch { Key = node.Key, Status = "InProgress", Version = node.Version });

		// Cancelled is UNGATED — a body-less status change applies (GitHub closes
		// "not planned" without a mandatory reason).
		var v1 = (await _tasks.GetAsync(Proj, "backlog")).Nodes.Single().Version;
		await Upsert("backlog", new NodePatch { Key = node.Key, Status = "Cancelled", Version = v1 });
		(await _tasks.GetAsync(Proj, "backlog", includeClosed: true)).Nodes.Single().Status.Should().Be("Cancelled");

		// A closed node reopens to Todo — but ONLY to Todo (terminals are not free).
		var v2 = (await _tasks.GetAsync(Proj, "backlog", includeClosed: true)).Nodes.Single().Version;
		var badReopen = () => Upsert("backlog", new NodePatch { Key = node.Key, Status = "InProgress", Version = v2 });
		(await badReopen.Should().ThrowAsync<ArgumentException>()).WithMessage("*no transition*");
		await Upsert("backlog", new NodePatch { Key = node.Key, Status = "Todo", Version = v2 });
		(await _tasks.GetAsync(Proj, "backlog")).Nodes.Single().Status.Should().Be("Todo");

		// Duplicate is the ONE reason-gated terminal (a duplicate without a pointer to the
		// original is useless): refused without the first-class `reason` field (a full body
		// does NOT count), applied with reason set (body may stay empty).
		var v3 = (await _tasks.GetAsync(Proj, "backlog")).Nodes.Single().Version;
		var noReason = () => Upsert("backlog", new NodePatch { Key = node.Key, Status = "Duplicate", Version = v3 });
		(await noReason.Should().ThrowAsync<ArgumentException>()).WithMessage("*requires a reason*");
		var bodyOnly = () => Upsert("backlog", new NodePatch
		{
			Key = node.Key,
			Status = "Duplicate",
			Version = v3,
			Body = "a full body is not a reason — the reason field is required",
		});
		(await bodyOnly.Should().ThrowAsync<ArgumentException>()).WithMessage("*requires a reason*");
		await Upsert("backlog", new NodePatch
		{
			Key = node.Key,
			Status = "Duplicate",
			Version = v3,
			Reason = "duplicate of the v2-flow card on backlog2",
		});
		var closed = (await _tasks.GetAsync(Proj, "backlog", includeClosed: true)).Nodes.Single();
		closed.Status.Should().Be("Duplicate");
		var reasons = await _comments.ListForNodeAsync(Proj, "backlog", closed.NodeId);
		reasons.Should().ContainSingle(c => c.Tags.Contains("artifact:reason")
			&& c.Body == "duplicate of the v2-flow card on backlog2");

		// The type door: bug is a label in the ONE shared block, an unknown type is
		// refused naming the vocabulary (classic is strict like work).
		await Upsert("backlog", new NodePatch { Key = "b", Type = "bug", Title = "B", Body = "x" });
		(await _tasks.GetAsync(Proj, "backlog")).Nodes.Single(n => n.Key == "b").Status.Should().Be("Backlog");
		var badType = () => Upsert("backlog", new NodePatch { Key = "z", Type = "banana", Title = "Z", Body = "x" });
		(await badType.Should().ThrowAsync<ArgumentException>()).WithMessage("*task|feature|bug*");

		// Free-form tags, like simple (classic declares no axes).
		await Upsert("backlog", new NodePatch { Key = "t", Title = "T", Body = "x", Tags = ["severity:high", "urgent"] });
		(await _tasks.GetAsync(Proj, "backlog")).Nodes.Single(n => n.Key == "t").Tags.Should().Equal("severity:high", "tag:urgent");
	}

	// Title-only create (empty body) remains OK — the reason gate is a transition concern,
	// not a create-time body requirement.
	[Fact]
	public async Task Classic_TitleOnlyCreate_Succeeds()
	{
		await _tasks.CreateBoardAsync(Proj, "backlog", "classic", null, null);
		await Upsert("backlog", new NodePatch { Key = "title-only", Title = "Just a title" });
		var n = (await _tasks.GetAsync(Proj, "backlog")).Nodes.Single();
		n.Key.Should().Be("title-only");
		n.Title.Should().Be("Just a title");
		n.Body.Should().BeEmpty();
	}

	// Rework guarantee for LIVE boards: the status vocabulary did not change, so a node in
	// every status — task or bug, from either former block — still writes and reads back
	// through the service under the single-block classic. Birth into Duplicate needs no
	// reason (RequiresReason only fires on a status *transition*).
	[Fact]
	public async Task Classic_NodeInEveryStatus_StillValid_UnderSingleBlock()
	{
		await _tasks.CreateBoardAsync(Proj, "backlog", "classic", null, null);
		var statuses = new[] { "Backlog", "Todo", "InProgress", "InReview", "Done", "Cancelled", "Duplicate" };
		foreach (var (status, i) in statuses.Select((s, i) => (s, i)))
			await Upsert("backlog", new NodePatch
			{
				Key = $"n{i}",
				Type = i % 2 == 0 ? "task" : "bug",
				Status = status,
				Title = status,
				Body = "kept",
			});
		(await _tasks.GetAsync(Proj, "backlog", includeClosed: true)).Nodes
			.Select(n => n.Status).Should().BeEquivalentTo(statuses);
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
