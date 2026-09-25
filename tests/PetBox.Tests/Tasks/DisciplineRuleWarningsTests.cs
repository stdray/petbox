using LinqToDB;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Data;
using PetBox.Tasks.Services;
using PetBox.Tasks.Workflow;

namespace PetBox.Tests.Tasks;

// idea discipline-rules-warn-in-tool-response, spec write-response-warnings: three NON-blocking
// warnings in the tasks_upsert write response — convention-approval-gate,
// terminal-ok-without-commits, and unlinked-intake-twin's non-embedder branch (closing without
// an outgoing link). The rule's OTHER branch (a freshly created node resembling an unlinked
// source-board twin, needing an embedder) lives in PetBox.Web and is covered separately by
// UnlinkedIntakeTwinWarnServiceTests. Every kind/link here is TEST-LOCAL DATA declared through a
// methodology TEMPLATE + a live INSTANCE (the "live process = open methodology instance, not
// methodology_defs" resolution MethodologyEngineV2Tests already exercises) — nothing asserts
// anything "quartet"-specific, proving the rules read purely from methodology data
// (MethodologyRuntime.CommitBearingTypes/EffectiveLinkKinds), not a hardcoded quartet special
// case. CreateMethodologyInstanceAsync auto-provisions one board per declared kind, named after
// the kind slug (PickBoardName) — so the board names below (ticket/ticket2/src/dst/…) exist
// without an explicit tasks_board_create.
public sealed class DisciplineRuleWarningsTests : IDisposable
{
	const string Proj = "proj";
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<TasksDb> _factory;
	readonly RelationStore _relations;
	readonly TasksService _tasks;

	public DisciplineRuleWarningsTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-warn-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });
		_factory = new ScopedDbFactory<TasksDb>(Path.Combine(_dir, "tasks"), Scope.Project,
			c => new TasksDb(TasksDb.CreateOptions(c)), TestSchema.Tasks);
		_relations = new RelationStore(_factory);
		_tasks = new TasksService(new TaskBoardStore(_db.Factory(), _factory), _relations, new TagStore(_factory), new CommentService(_factory));
	}

	public void Dispose()
	{
		_db.Dispose();
		_factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		TestDirs.CleanupOrDefer(_dir);
	}

	int _tmplSeq;
	// PickBoardName (MethodologyInstanceService) tries the INSTANCE KEY first for a single-kind
	// definition — so naming the instance after the kind slug itself (the caller passes it,
	// e.g. "ticket") makes the auto-provisioned board land at that exact name; a multi-kind
	// definition (e.g. LinkDef's src+dst) doesn't need this — it tries the kind slugs directly.
	async Task InstallLive(MethodologyDefinition def, string? instanceKey = null)
	{
		var tmpl = $"tmpl-{++_tmplSeq}";
		await _tasks.UpsertMethodologyTemplateAsync(Proj, tmpl, def, 0);
		await _tasks.CreateMethodologyInstanceAsync(Proj, instanceKey ?? $"inst-{_tmplSeq}", "template", tmpl);
	}

	// ─────────────────────────── write-warnings-channel: a clean write carries none ───────────

	[Fact]
	public async Task CleanWrite_OnTheOrdinaryBuiltinWorkPreset_CarriesNoWarnings()
	{
		await _tasks.CreateBoardAsync(Proj, "w", "work", "d", null);
		var up = await _tasks.UpsertAsync(Proj, "w",
			[new NodePatch { Key = "f1", Version = 0, Title = "f1", Type = "chore" }]);

		up.Result.Applied.Should().BeTrue();
		up.Warnings.Should().BeNull("an ordinary create with no methodology violation must not carry a warnings channel at all");
	}

	// ─────────────────────────────────── convention-approval-gate ─────────────────────────────

	// RequiresApproval:true, EnforceApproval left at its default (false) — an owner-only
	// transition by CONVENTION, never mechanically blocked (WorkflowEngine.Validate would refuse
	// it outright if EnforceApproval were true, which is a different, already-covered path).
	static MethodologyDefinition ApprovalGateDef() => new("wt-approval",
	[
		new MethodologyKindDef("ticket", QuickAddAllowed: true,
		[
			new MethodologyWorkflowDef(["issue"],
			[
				new("Open", "Open", StatusKind.Open),
				new("Done", "Done", StatusKind.TerminalOk),
			],
			[
				new MethodologyTransitionDef("Open", "Done", RequiresApproval: true),
			]),
		]),
	]);

	[Fact]
	public async Task ApprovalGate_ConventionTransitionByNonApprover_Warns()
	{
		await InstallLive(ApprovalGateDef(), "ticket");
		var born = await _tasks.UpsertAsync(Proj, "ticket",
			[new NodePatch { Key = "t1", Version = 0, Title = "t1", Type = "issue", Status = "Open" }]);
		var v = born.Result.Added.Single().Version;

		var up = await _tasks.UpsertAsync(Proj, "ticket",
			[new NodePatch { Key = "t1", Version = v, Status = "Done" }], actor: TasksActor.None);

		up.Result.Applied.Should().BeTrue("a CONVENTION gate never blocks the write — only a warning fires");
		var w = up.Warnings.Should().ContainSingle().Subject;
		w.Rule.Should().Be("convention-approval-gate");
		w.Key.Should().Be("t1");
	}

	[Fact]
	public async Task ApprovalGate_SameTransitionByAnApprover_NoWarning()
	{
		await InstallLive(ApprovalGateDef(), "ticket");
		var born = await _tasks.UpsertAsync(Proj, "ticket",
			[new NodePatch { Key = "t1", Version = 0, Title = "t1", Type = "issue", Status = "Open" }]);
		var v = born.Result.Added.Single().Version;

		var up = await _tasks.UpsertAsync(Proj, "ticket",
			[new NodePatch { Key = "t1", Version = v, Status = "Done" }], actor: TasksActor.Approver);

		up.Warnings.Should().BeNull("an actor with approval scope performing the SAME transition trips nothing");
	}

	// ─────────────────────────────────── terminal-ok-without-commits ──────────────────────────

	static MethodologyDefinition CommitsDef() => new("wt-commits",
	[
		new MethodologyKindDef("ticket2", QuickAddAllowed: true,
		[
			new MethodologyWorkflowDef(["issue"],
			[
				new("Open", "Open", StatusKind.Open),
				new("Done", "Done", StatusKind.TerminalOk),
			],
			[
				new MethodologyTransitionDef("Open", "Done"), // no approval gate — isolates this rule
			]),
		])
		{ CommitBearingTypes = ["issue"] },
	]);

	[Fact]
	public async Task TerminalOkWithoutCommits_TypeDeclaredCommitBearing_EmptyCommits_Warns()
	{
		await InstallLive(CommitsDef(), "ticket2");
		var born = await _tasks.UpsertAsync(Proj, "ticket2",
			[new NodePatch { Key = "t1", Version = 0, Title = "t1", Type = "issue", Status = "Open" }]);
		var v = born.Result.Added.Single().Version;

		var up = await _tasks.UpsertAsync(Proj, "ticket2",
			[new NodePatch { Key = "t1", Version = v, Status = "Done" }]);

		up.Result.Applied.Should().BeTrue();
		var w = up.Warnings.Should().ContainSingle().Subject;
		w.Rule.Should().Be("terminal-ok-without-commits");
		w.Key.Should().Be("t1");
	}

	[Fact]
	public async Task TerminalOkWithCommits_NoWarning()
	{
		await InstallLive(CommitsDef(), "ticket2");
		var born = await _tasks.UpsertAsync(Proj, "ticket2",
			[new NodePatch { Key = "t1", Version = 0, Title = "t1", Type = "issue", Status = "Open" }]);
		var v = born.Result.Added.Single().Version;

		var up = await _tasks.UpsertAsync(Proj, "ticket2",
			[new NodePatch { Key = "t1", Version = v, Status = "Done", Commits = ["abc1234"] }]);

		up.Warnings.Should().BeNull("commits[] is non-empty at the moment the node reaches terminal-ok");
	}

	[Fact]
	public async Task TerminalOkWithoutCommits_TypeNotDeclaredCommitBearing_NoWarning()
	{
		// Same shape as CommitsDef but the type ISN'T in CommitBearingTypes (e.g. a chore-like
		// type an instance deliberately exempts) — the rule must not fire universally.
		var def = new MethodologyDefinition("wt-commits-exempt",
		[
			new MethodologyKindDef("ticket3", QuickAddAllowed: true,
			[
				new MethodologyWorkflowDef(["chore"],
				[
					new("Open", "Open", StatusKind.Open),
					new("Done", "Done", StatusKind.TerminalOk),
				],
				[
					new MethodologyTransitionDef("Open", "Done"),
				]),
			])
			{ CommitBearingTypes = [] }, // explicitly empty — nothing declared commit-bearing
		]);
		await InstallLive(def, "ticket3");
		var born = await _tasks.UpsertAsync(Proj, "ticket3",
			[new NodePatch { Key = "t1", Version = 0, Title = "t1", Type = "chore", Status = "Open" }]);
		var v = born.Result.Added.Single().Version;

		var up = await _tasks.UpsertAsync(Proj, "ticket3", [new NodePatch { Key = "t1", Version = v, Status = "Done" }]);

		up.Warnings.Should().BeNull();
	}

	// ───────────────────────── unlinked-intake-twin — closing-without-link branch ─────────────

	static MethodologyDefinition LinkDef() => new("wt-link",
	[
		new MethodologyKindDef("src", QuickAddAllowed: true,
		[
			new MethodologyWorkflowDef(["issue"],
			[
				new("triage", "triage", StatusKind.Open),
				new("done", "done", StatusKind.TerminalOk),
			],
			[
				new MethodologyTransitionDef("triage", "done"),
			]),
		]),
		new MethodologyKindDef("dst", QuickAddAllowed: true,
		[
			new MethodologyWorkflowDef(["task"],
			[
				new("Pending", "Pending", StatusKind.Open),
			], []),
		]),
	])
	{
		LinkKinds = [new MethodologyLinkKindDef("promotes", Category: LinkCategory.Process,
			Direction: new MethodologyLinkDirectionDef("src", "dst"))],
	};

	[Fact]
	public async Task UnlinkedIntakeTwin_ClosesWithoutOutgoingLink_Warns()
	{
		await InstallLive(LinkDef());
		var born = await _tasks.UpsertAsync(Proj, "src",
			[new NodePatch { Key = "i1", Version = 0, Title = "i1", Type = "issue", Status = "triage" }]);
		var v = born.Result.Added.Single().Version;

		var up = await _tasks.UpsertAsync(Proj, "src", [new NodePatch { Key = "i1", Version = v, Status = "done" }]);

		up.Result.Applied.Should().BeTrue();
		var w = up.Warnings.Should().ContainSingle().Subject;
		w.Rule.Should().Be("unlinked-intake-twin");
		w.Key.Should().Be("i1");
	}

	[Fact]
	public async Task UnlinkedIntakeTwin_ClosesWithAnOutgoingLink_NoWarning()
	{
		await InstallLive(LinkDef());
		var srcBorn = await _tasks.UpsertAsync(Proj, "src",
			[new NodePatch { Key = "i1", Version = 0, Title = "i1", Type = "issue", Status = "triage" }]);
		var srcNode = srcBorn.Result.Added.Single();
		var dstBorn = await _tasks.UpsertAsync(Proj, "dst",
			[new NodePatch { Key = "w1", Version = 0, Title = "w1", Type = "task" }]);
		var dstNode = dstBorn.Result.Added.Single();

		await _relations.CreateAsync(Proj, "promotes", srcNode.NodeId, dstNode.NodeId);

		var up = await _tasks.UpsertAsync(Proj, "src",
			[new NodePatch { Key = "i1", Version = srcNode.Version, Status = "done" }]);

		up.Warnings.Should().BeNull("the closing node already carries an outgoing edge of the declared link kind");
	}

	// ── regression: live false positive 2026-09-25 ──────────────────────────────────────────
	// A work `chore` closing Review->Done tripped "unlinked-intake-twin" against `task_spec` —
	// `iwork` (this fixture's work-shaped kind) is the FromKind of BOTH its own creation-required
	// link (`wspec`, like task_spec: iwork -> ispec, required for `feature` only) AND is the
	// TARGET, not source, of the promotion link (`promote`, like issue_task: iintake -> iwork).
	// The naive "any process link whose FromKind is this kind" match picked `wspec` instead of
	// finding no eligible link at all. Reproduces the quartet shape (task_spec + issue_task) with
	// test-local kinds/links so the fix is proven data-driven, not quartet-specific.
	static MethodologyDefinition IntakeTwinScopeDef() => new("wt-scope",
	[
		new MethodologyKindDef("iwork", QuickAddAllowed: true,
		[
			new MethodologyWorkflowDef(["feature", "chore"],
			[
				new("Pending", "Pending", StatusKind.Open),
				new("Review", "Review", StatusKind.Open),
				new("Done", "Done", StatusKind.TerminalOk),
			],
			[
				new MethodologyTransitionDef("Pending", "Review"),
				new MethodologyTransitionDef("Review", "Done"),
			]),
		])
		{
			// Mirrors the quartet's work kind: only `feature` needs the outbound link at creation;
			// `chore` is exempt — same as task_spec's real LinkConstraints.
			LinkConstraints = [new MethodologyLinkConstraintDef("feature", "wspec") { TargetKind = "ispec" }],
		},
		new MethodologyKindDef("ispec", QuickAddAllowed: true,
		[
			new MethodologyWorkflowDef(["spec"], [new("defined", "defined", StatusKind.Open)], []),
		]),
		new MethodologyKindDef("iintake", QuickAddAllowed: true,
		[
			new MethodologyWorkflowDef(["issue"],
			[
				new("triage", "triage", StatusKind.Open),
				new("done", "done", StatusKind.TerminalOk),
			],
			[
				new MethodologyTransitionDef("triage", "done"),
			]),
		]),
	])
	{
		LinkKinds =
		[
			new MethodologyLinkKindDef("wspec", Category: LinkCategory.Process,
				Direction: new MethodologyLinkDirectionDef("iwork", "ispec")),
			new MethodologyLinkKindDef("promote", Category: LinkCategory.Process,
				Direction: new MethodologyLinkDirectionDef("iintake", "iwork")),
		],
	};

	[Fact]
	public async Task UnlinkedIntakeTwin_ClosingAWorkChore_NoWarning_EvenThoughWorkIsFromKindOfAnotherProcessLink()
	{
		await InstallLive(IntakeTwinScopeDef());
		var born = await _tasks.UpsertAsync(Proj, "iwork",
			[new NodePatch { Key = "c1", Version = 0, Title = "c1", Type = "chore", Status = "Review" }]);
		var v = born.Result.Added.Single().Version;

		var up = await _tasks.UpsertAsync(Proj, "iwork", [new NodePatch { Key = "c1", Version = v, Status = "Done" }]);

		up.Result.Applied.Should().BeTrue();
		up.Warnings.Should().BeNull("a work chore needs no wspec/task_spec-like link at all, and iwork is not the SOURCE of the promotion link");
	}

	[Fact]
	public async Task UnlinkedIntakeTwin_ClosingAWorkFeatureWithItsRequiredLink_NoWarning()
	{
		await InstallLive(IntakeTwinScopeDef());
		var spec = await _tasks.UpsertAsync(Proj, "ispec", [new NodePatch { Key = "s1", Version = 0, Title = "s1", Type = "spec", Status = "defined" }]);
		var born = await _tasks.UpsertAsync(Proj, "iwork",
			[new NodePatch { Key = "f1", Version = 0, Title = "f1", Type = "feature", Status = "Review",
				Links = new Dictionary<string, IReadOnlyList<string>> { ["wspec"] = ["s1"] } }]);
		var v = born.Result.Added.Single().Version;

		var up = await _tasks.UpsertAsync(Proj, "iwork", [new NodePatch { Key = "f1", Version = v, Status = "Done" }]);

		up.Result.Applied.Should().BeTrue();
		up.Warnings.Should().BeNull("a feature closing with its own required wspec link is not an unlinked-intake-twin case at all — that link is a DIFFERENT obligation");
	}

	[Fact]
	public async Task UnlinkedIntakeTwin_ClosingAnIntakeNodeWithoutThePromotionLink_StillWarns()
	{
		await InstallLive(IntakeTwinScopeDef());
		var born = await _tasks.UpsertAsync(Proj, "iintake",
			[new NodePatch { Key = "i1", Version = 0, Title = "i1", Type = "issue", Status = "triage" }]);
		var v = born.Result.Added.Single().Version;

		var up = await _tasks.UpsertAsync(Proj, "iintake", [new NodePatch { Key = "i1", Version = v, Status = "done" }]);

		up.Result.Applied.Should().BeTrue();
		var w = up.Warnings.Should().ContainSingle().Subject;
		w.Rule.Should().Be("unlinked-intake-twin");
		w.Key.Should().Be("i1");
	}
}
