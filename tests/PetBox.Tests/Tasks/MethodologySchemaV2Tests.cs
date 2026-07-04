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

// Methodology schema v2 (engine-v2-schema): the new DATA primitives — approval-gate mode
// (enforceApproval), pre-transition checklists, declared transition effects, and
// link-constraint targets — as SCHEMA + VALIDATION only (no runtime behavior in this
// wave). Part 1 exercises every new validator rule through the service door
// (DefineMethodologyAsync validates the whole document before storing); part 2 (separate
// class below) checks the guide renders the new data honestly.
[Collection("DataModule")]
public sealed class MethodologySchemaV2ValidationTests : IDisposable
{
	const string Proj = "proj";
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<TasksDb> _factory;
	readonly TasksService _tasks;

	public MethodologySchemaV2ValidationTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-schema-v2-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });
		_factory = new ScopedDbFactory<TasksDb>(Path.Combine(_dir, "tasks"), Scope.Project,
			c => new TasksDb(TasksDb.CreateOptions(c)), TasksSchema.Ensure);
		_tasks = new TasksService(new TaskBoardStore(_db, _factory), new RelationStore(_db), new TagStore(_factory), new CommentService(_factory));
	}

	public void Dispose()
	{
		_db.Dispose();
		_factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		SqliteConnection.ClearAllPools();
		if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
	}

	// ── definition builders ──────────────────────────────────────────────────

	static readonly MethodologyTransitionDef[] DefaultTransitions =
	[
		new("New", "Open"),
		new("Open", "Resolved", RequiresApproval: true),
	];

	// One kind, one block (ticket|incident over New→Open→Resolved) — every scenario mutates
	// exactly one aspect of this baseline.
	static MethodologyKindDef SupportKind(
		MethodologyTransitionDef[]? transitions = null,
		IReadOnlyList<MethodologyTransitionEffectDef>? effects = null,
		IReadOnlyList<MethodologyLinkConstraintDef>? constraints = null) =>
		new("support", QuickAddAllowed: true,
		[
			new MethodologyWorkflowDef(["ticket", "incident"],
				[
					new("New", "New", StatusKind.Open),
					new("Open", "Open", StatusKind.Open),
					new("Resolved", "Resolved", StatusKind.TerminalOk),
				],
				transitions ?? DefaultTransitions),
		])
		{
			Effects = effects ?? [],
			LinkConstraints = constraints ?? [],
		};

	static MethodologyDefinition Def(params MethodologyKindDef[] kinds) => new("schema-v2", kinds);

	Task<MethodologyDefAck> Define(MethodologyDefinition def) => _tasks.DefineMethodologyAsync(Proj, def, 0);

	async Task AssertRejected(MethodologyDefinition def, params string[] messageParts)
	{
		var act = () => Define(def);
		var ex = await act.Should().ThrowAsync<ArgumentException>();
		foreach (var part in messageParts)
			ex.WithMessage($"*{part}*");
	}

	// ── positive: a definition using EVERY new primitive is valid and stores ──

	[Fact]
	public async Task AllNewPrimitives_ValidDefinition_Stores()
	{
		var def = new MethodologyDefinition("schema-v2",
		[
			SupportKind(
				transitions:
				[
					new("New", "Open") { Checklist = ["repro steps recorded", "owner notified"] },
					new("Open", "Resolved", RequiresApproval: true) { EnforceApproval = true },
				],
				effects:
				[
					// builtin process link + a definition-declared one; Set/OnlyFrom are
					// cross-kind statuses (format-checked only).
					new("Resolved", "issue_task", "incoming", "done", OnlyFrom: "confirmed"),
					new("Resolved", "escalates", "outgoing", "Closed"),
				],
				constraints:
				[
					// targetKind declared by THIS definition → statuses checked against it;
					// plus a cross-preset target (format-only).
					new("ticket", "blocks") { TargetKind = "docs", TargetStatuses = ["published"] },
					new("incident", "task_spec") { TargetKind = "spec", TargetStatuses = ["defined"] },
				]),
			new MethodologyKindDef("docs", QuickAddAllowed: true,
			[
				new MethodologyWorkflowDef(["doc"],
					[
						new("published", "Published", StatusKind.Open),
						new("retired", "Retired", StatusKind.TerminalCancel),
					],
					[new("published", "retired")]),
			]),
		])
		{
			LinkKinds = [new MethodologyLinkKindDef("escalates", "support escalation edge")],
		};

		var ack = await Define(def);
		ack.Version.Should().Be(1);
		ack.Changed.Should().BeTrue();
	}

	// ── enforceApproval / checklist (transition-level) ───────────────────────

	[Fact]
	public async Task EnforceApproval_WithoutRequiresApproval_Rejected() =>
		await AssertRejected(
			Def(SupportKind(transitions: [new("New", "Open") { EnforceApproval = true }])),
			"enforceApproval is only meaningful with requiresApproval");

	[Fact]
	public async Task Checklist_EmptyItem_Rejected() =>
		await AssertRejected(
			Def(SupportKind(transitions: [new("New", "Open") { Checklist = ["ok", "  "] }])),
			"checklist item must be a non-empty string");

	[Fact]
	public async Task Checklist_OverlongItem_Rejected() =>
		await AssertRejected(
			Def(SupportKind(transitions: [new("New", "Open") { Checklist = [new string('x', 501)] }])),
			"checklist item exceeds 500 chars");

	// ── effects ──────────────────────────────────────────────────────────────

	[Fact]
	public async Task Effect_OnNotAStatusOfOwningKind_Rejected() =>
		await AssertRejected(
			Def(SupportKind(effects: [new("Shipped", "issue_task", "incoming", "done")])),
			"'on' 'Shipped' is not a status this kind's workflow blocks declare");

	[Fact]
	public async Task Effect_InvalidDirection_Rejected() =>
		await AssertRejected(
			Def(SupportKind(effects: [new("Resolved", "issue_task", "sideways", "done")])),
			"direction 'sideways' is not valid (incoming|outgoing)");

	[Fact]
	public async Task Effect_UnknownLinkKind_Rejected() =>
		await AssertRejected(
			Def(SupportKind(effects: [new("Resolved", "escalates", "incoming", "done")])),
			"link 'escalates' is not a relation kind this project knows");

	[Fact]
	public async Task Effect_SetNotAStatusSlug_Rejected() =>
		await AssertRejected(
			Def(SupportKind(effects: [new("Resolved", "issue_task", "incoming", "not a slug!")])),
			"'set' 'not a slug!' is not a valid status slug");

	[Fact]
	public async Task Effect_OnlyFromNotAStatusSlug_Rejected() =>
		await AssertRejected(
			Def(SupportKind(effects: [new("Resolved", "issue_task", "incoming", "done", OnlyFrom: "9bad")])),
			"onlyFrom '9bad' is not a valid status slug");

	[Fact]
	public async Task Effect_DuplicateOnLinkDirection_Rejected() =>
		await AssertRejected(
			Def(SupportKind(effects:
			[
				new("Resolved", "issue_task", "incoming", "done"),
				new("resolved", "ISSUE_TASK", "Incoming", "wontfix"), // same triple, case-insensitive
			])),
			"duplicate effect (on resolved, Incoming ISSUE_TASK)");

	// ── link-constraint targets ──────────────────────────────────────────────

	[Fact]
	public async Task Target_KindNotASlug_Rejected() =>
		await AssertRejected(
			Def(SupportKind(constraints: [new("ticket", "blocks") { TargetKind = "Big Kind" }])),
			"targetKind 'Big Kind' is not a valid slug");

	[Fact]
	public async Task Target_StatusesEmptyWhenProvided_Rejected() =>
		await AssertRejected(
			Def(SupportKind(constraints: [new("ticket", "blocks") { TargetKind = "spec", TargetStatuses = [] }])),
			"targetStatuses must be non-empty when provided");

	[Fact]
	public async Task Target_KindDeclaredHere_StatusMustBelongToIt() =>
		await AssertRejected(
			// `support` IS a kind of this definition — targetStatuses resolve against it.
			Def(SupportKind(constraints: [new("ticket", "blocks") { TargetKind = "support", TargetStatuses = ["Banana"] }])),
			"target status 'Banana' is not a status of kind 'support'");

	[Fact]
	public async Task Target_CrossPresetKind_StatusesFormatCheckedOnly()
	{
		// `spec` is NOT declared by this definition → "defined" passes on format alone
		// (runtime resolution is a later task), while a malformed slug is still rejected.
		var ack = await Define(
			Def(SupportKind(constraints: [new("ticket", "task_spec") { TargetKind = "spec", TargetStatuses = ["defined"] }])));
		ack.Changed.Should().BeTrue();

		await AssertRejected(
			Def(SupportKind(constraints: [new("incident", "task_spec") { TargetKind = "spec", TargetStatuses = ["not a slug!"] }])),
			"target status 'not a slug!' is not a valid status slug");
	}
}

// Part 2 — the guide renders the schema-v2 data honestly (pure derivation, no storage):
// gate MODE stated (enforced vs convention) with distinct machine rules, checklists as a
// block under the transition, effects one line each, and the creation-requirement line
// extended with the declared link target.
public sealed class MethodologyGuideSchemaV2Tests
{
	// One kind using every new primitive: an ENFORCED approval gate, a CONVENTION approval
	// gate, a checklist, one effect (with onlyFrom) and a targeted link constraint.
	static MethodologyDefinition GuideDefinition() => new("schema-v2",
	[
		new MethodologyKindDef("support", QuickAddAllowed: true,
		[
			new MethodologyWorkflowDef(["ticket", "incident"],
				[
					new("New", "New", StatusKind.Open),
					new("Open", "Open", StatusKind.Open),
					new("Resolved", "Resolved", StatusKind.TerminalOk),
					new("Junk", "Junk", StatusKind.TerminalCancel),
				],
				[
					new("New", "Open") { Checklist = ["repro steps recorded", "owner notified"] },
					new("Open", "Resolved", RequiresApproval: true) { EnforceApproval = true },
					new("Open", "Junk", RequiresApproval: true),
				]),
		])
		{
			Effects = [new MethodologyTransitionEffectDef("Resolved", "issue_task", "incoming", "done", OnlyFrom: "confirmed")],
			LinkConstraints = [new MethodologyLinkConstraintDef("ticket", "task_spec") { TargetKind = "spec", TargetStatuses = ["defined"] }],
		},
	]);

	static MethodologyGuideView Render() =>
		MethodologyGuide.Render("schema-v2", new MethodologyRuntime(GuideDefinition()), "mixed", 1);

	[Fact]
	public void ApprovalGates_StateTheModeExplicitly_DistinctInvariantRules()
	{
		var guide = Render();

		// Enforced: the server blocks it; convention: the rule binds but nothing blocks.
		guide.Markdown.Should().Contain("The agent NEVER performs Open -> Resolved")
			.And.Contain("owner-only (enforced by the server — it blocks the transition)");
		guide.Markdown.Should().Contain("The agent NEVER performs Open -> Junk")
			.And.Contain("owner-only (convention — the server does not block it)");
		// The transition map marks the mode too.
		guide.Markdown.Should().Contain("Open -> Resolved [OWNER-ONLY (enforced)]");
		guide.Markdown.Should().Contain("Open -> Junk [OWNER-ONLY]");

		guide.Invariants.Should().Contain(new MethodologyInvariant("support", "approval_gate_enforced", "Open -> Resolved"));
		guide.Invariants.Should().Contain(new MethodologyInvariant("support", "approval_gate", "Open -> Junk"));
	}

	[Fact]
	public void Checklist_RendersAsBlockUnderTheTransition()
	{
		var guide = Render();

		guide.Markdown.Should().Contain("Before New -> Open confirm (convention — the server does not check these):");
		guide.Markdown.Should().Contain("    - repro steps recorded");
		guide.Markdown.Should().Contain("    - owner notified");
		guide.Markdown.Should().Contain("New -> Open [checklist]");

		guide.Invariants.Should().Contain(new MethodologyInvariant("support", "checklist",
			"New -> Open: repro steps recorded | owner notified"));
	}

	[Fact]
	public void Effects_OneHonestLineEach()
	{
		var guide = Render();

		guide.Markdown.Should().Contain("### Transition effects");
		guide.Markdown.Should().Contain("On entering Resolved, incoming `issue_task` nodes currently in confirmed are set to done.");

		guide.Invariants.Should().Contain(new MethodologyInvariant("support", "transition_effect",
			"Resolved: incoming issue_task from confirmed -> done"));
	}

	[Fact]
	public void LinkConstraintTargets_ExtendTheCreationRequirementLine()
	{
		var guide = Render();

		guide.Markdown.Should().Contain(
			"A new `ticket` must carry a `task_spec` link (provide `specRef` in the creating upsert) — it must link a `spec` node in status defined. Edits don't re-require it.");

		guide.Invariants.Should().Contain(new MethodologyInvariant("support", "link_constraint",
			"ticket requires task_spec (specRef) -> spec[defined]"));
	}

	[Fact]
	public void PresetKinds_KeepTheirV1Rendering_ConventionRuleNameUnchanged()
	{
		var guide = Render();

		// The preset gates carry NO enforceApproval (presets unchanged in this wave) — they
		// render as convention gates under the ORIGINAL rule name, so existing consumers of
		// `approval_gate` keep working.
		guide.Invariants.Should().Contain(new MethodologyInvariant("work", "approval_gate", "Review -> Done"));
		guide.Invariants.Should().NotContain(i => i.Rule == "approval_gate_enforced" && i.Kind != "support");
		// Simple's all-pairs block still collapses to "free" (checklist-free transitions).
		guide.Markdown.Should().Contain("Transitions: free — any status may move to any other");
	}
}
