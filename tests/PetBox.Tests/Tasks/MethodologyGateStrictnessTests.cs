using LinqToDB;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Data;
using PetBox.Tasks.Services;
using PetBox.Tasks.Workflow;

namespace PetBox.Tests.Tasks;

// spec methodology-gate-strictness: the gate's DECLARATION (approval + requiredArtifacts) is
// separated from its FORCE (enforce/strictMode) — reason collapses into a requiredArtifacts
// entry with slug "reason", inline:true; there is no separate reason gate. Part 1 is validator
// rules through the service door (mirrors MethodologySchemaV2ValidationTests); part 2 is the
// domain-level EffectiveRequiredArtifacts/EffectiveEnforce* merge (pure, no storage); part 3 is
// the guide rendering the new "convention" artifact marks honestly.
public sealed class MethodologyGateStrictnessValidationTests : IDisposable
{
	const string Proj = "proj";
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<TasksDb> _factory;
	readonly TasksService _tasks;

	public MethodologyGateStrictnessValidationTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-gate-strictness-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });
		_factory = new ScopedDbFactory<TasksDb>(Path.Combine(_dir, "tasks"), Scope.Project,
			c => new TasksDb(TasksDb.CreateOptions(c)), TasksSchema.Ensure);
		_tasks = new TasksService(new TaskBoardStore(_db.Factory(), _factory), new RelationStore(_factory), new TagStore(_factory), new CommentService(_factory));
	}

	public void Dispose()
	{
		_db.Dispose();
		_factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		TestDirs.CleanupOrDefer(_dir);
	}

	static MethodologyKindDef GatedKind(MethodologyTransitionDef transition) =>
		new("support", QuickAddAllowed: true,
		[
			new MethodologyWorkflowDef(["ticket"],
				[
					new("New", "New", StatusKind.Open),
					new("Open", "Open", StatusKind.Open),
				],
				[transition]),
		]);

	static MethodologyDefinition Def(MethodologyTransitionDef transition, bool strictMode = false) =>
		new("gate-strictness", [GatedKind(transition)]) { StrictMode = strictMode };

	Task<MethodologyDefAck> Define(MethodologyDefinition def) => _tasks.DefineMethodologyAsync(Proj, def, 0);

	async Task AssertRejected(MethodologyDefinition def, params string[] messageParts)
	{
		var act = () => Define(def);
		var ex = await act.Should().ThrowAsync<ArgumentException>();
		foreach (var part in messageParts)
			ex.WithMessage($"*{part}*");
	}

	[Fact]
	public async Task RequiredArtifacts_MixedWithLegacyRequiresReason_Rejected() =>
		await AssertRejected(
			Def(new("New", "Open", RequiresReason: true) { RequiredArtifacts = [new RequiredArtifactDef("spec_plan")] }),
			"don't mix legacy requiresReason/preconditionArtifact with requiredArtifacts");

	[Fact]
	public async Task RequiredArtifacts_MixedWithLegacyPreconditionArtifact_Rejected() =>
		await AssertRejected(
			Def(new("New", "Open", PreconditionArtifact: "spec_plan") { RequiredArtifacts = [new RequiredArtifactDef("reason", Inline: true)] }),
			"don't mix legacy requiresReason/preconditionArtifact with requiredArtifacts");

	[Fact]
	public async Task RequiredArtifacts_InlineOnANonReasonSlug_Rejected() =>
		await AssertRejected(
			Def(new("New", "Open") { RequiredArtifacts = [new RequiredArtifactDef("spec_plan", Inline: true)] }),
			"only slug 'reason' may be inline");

	[Fact]
	public async Task RequiredArtifacts_InlineReason_IsAccepted()
	{
		var ack = await Define(Def(new("New", "Open") { RequiredArtifacts = [new RequiredArtifactDef("reason", Inline: true)] }));
		ack.Changed.Should().BeTrue();
	}

	[Fact]
	public async Task RequiredArtifacts_BadSlug_Rejected() =>
		await AssertRejected(
			Def(new("New", "Open") { RequiredArtifacts = [new RequiredArtifactDef("Not A Slug!")] }),
			"is not a valid slug");

	[Fact]
	public async Task RequiredArtifacts_DuplicateSlug_Rejected() =>
		await AssertRejected(
			Def(new("New", "Open") { RequiredArtifacts = [new RequiredArtifactDef("spec_plan"), new RequiredArtifactDef("spec_plan")] }),
			"duplicate requiredArtifacts slug 'spec_plan'");

	[Fact]
	public async Task EnforceApproval_ViaEnforceObject_WithoutRequiresApproval_Rejected() =>
		await AssertRejected(
			Def(new("New", "Open") { Enforce = new GateEnforcementDef(Approval: true) }),
			"enforce.approval is only meaningful with requiresApproval");

	[Fact]
	public async Task MultipleRequiredArtifacts_OnOneTransition_Stores()
	{
		var ack = await Define(Def(new("New", "Open")
		{
			RequiredArtifacts = [new RequiredArtifactDef("reason", Inline: true), new RequiredArtifactDef("spec_plan")],
			Enforce = new GateEnforcementDef(Artifacts: false),
		}));
		ack.Changed.Should().BeTrue();
	}
}

// Pure domain-level merge (no storage): MethodologyTransitionDef.EffectiveRequiredArtifacts /
// EffectiveEnforceApproval / EffectiveEnforceArtifacts — the seam WorkflowEngine/GuardEngine/
// MethodologyGuide all read through, so it alone decides whether an existing document (legacy
// shape) and a new one (requiredArtifacts/enforce) behave identically when they declare the
// same gate.
public sealed class MethodologyTransitionDefEffectiveGateTests
{
	[Fact]
	public void LegacyShape_TranslatesToTheUnifiedArtifactList()
	{
		var t = new MethodologyTransitionDef("a", "b", RequiresReason: true, PreconditionArtifact: "spec_plan");
		t.EffectiveRequiredArtifacts().Should().BeEquivalentTo(
		[
			new RequiredArtifactDef("reason", Inline: true),
			new RequiredArtifactDef("spec_plan", Inline: false),
		]);
	}

	[Fact]
	public void NewShape_WinsOverLegacyFieldsLeftAtTheirDefaults()
	{
		var t = new MethodologyTransitionDef("a", "b") { RequiredArtifacts = [new RequiredArtifactDef("custom")] };
		t.EffectiveRequiredArtifacts().Should().Equal(new RequiredArtifactDef("custom"));
	}

	[Fact]
	public void NeitherShapeDeclared_IsEmpty() =>
		new MethodologyTransitionDef("a", "b").EffectiveRequiredArtifacts().Should().BeEmpty();

	[Fact]
	public void EnforceApproval_DefaultsToTheLegacyFlagOrStrictMode_NeverImplyingRequiresApproval()
	{
		var undeclared = new MethodologyTransitionDef("a", "b", RequiresApproval: true);
		undeclared.EffectiveEnforceApproval(strictMode: false).Should().BeFalse("today's default — owner-only by convention");
		undeclared.EffectiveEnforceApproval(strictMode: true).Should().BeTrue("the definition's strictMode is the fallback default");

		var legacyEnforced = new MethodologyTransitionDef("a", "b", RequiresApproval: true) { EnforceApproval = true };
		legacyEnforced.EffectiveEnforceApproval(strictMode: false).Should().BeTrue("the legacy per-transition flag still works");

		var overridden = new MethodologyTransitionDef("a", "b", RequiresApproval: true) { Enforce = new GateEnforcementDef(Approval: false) };
		overridden.EffectiveEnforceApproval(strictMode: true).Should().BeFalse("an explicit Enforce.Approval:false wins over strictMode");
	}

	[Fact]
	public void EnforceArtifacts_DefaultsToTrue_RegardlessOfStrictMode()
	{
		var t = new MethodologyTransitionDef("a", "b", RequiresReason: true);
		t.EffectiveEnforceArtifacts().Should().BeTrue("reason/precondition are hard today, unconditionally");

		var soft = new MethodologyTransitionDef("a", "b", RequiresReason: true) { Enforce = new GateEnforcementDef(Artifacts: false) };
		soft.EffectiveEnforceArtifacts().Should().BeFalse();
	}
}

// The compiled guide (schema methodology-gate-strictness): the "_convention" invariant variants
// fire ONLY when a transition explicitly softens Enforce.Artifacts — every builtin preset and
// every pre-existing definition keeps the enforced variant, unchanged (PresetKinds_StayEnforced
// below is the bit-for-bit-reproduction assertion this whole leaf promised).
public sealed class MethodologyGuideGateStrictnessTests
{
	static MethodologyDefinition GuideDefinition(bool strictMode) => new("gate-strictness",
	[
		new MethodologyKindDef("support", QuickAddAllowed: true,
		[
			new MethodologyWorkflowDef(["ticket"],
				[
					new("New", "New", StatusKind.Open),
					new("Open", "Open", StatusKind.Open),
					new("Junk", "Junk", StatusKind.TerminalCancel),
				],
				[
					// Hard by default (Enforce omitted) — new shape, same force as legacy.
					new("New", "Open") { RequiredArtifacts = [new RequiredArtifactDef("spec_plan")] },
					// Explicitly softened — declared, not blocked.
					new("Open", "Junk") { RequiredArtifacts = [new RequiredArtifactDef("reason", Inline: true)], Enforce = new GateEnforcementDef(Artifacts: false) },
				]),
		]),
	])
	{ StrictMode = strictMode };

	static MethodologyGuideView Render(bool strictMode = false) =>
		MethodologyGuide.Render("gate-strictness", new MethodologyRuntime(GuideDefinition(strictMode)), "mixed", 1);

	[Fact]
	public void HardArtifactGate_RendersTheEnforcedInvariant_SameAsLegacy()
	{
		var guide = Render();
		guide.Markdown.Should().Contain("Add an `artifact:spec_plan` comment on the node before New -> Open — the transition is rejected without it.");
		guide.Invariants.Should().Contain(new MethodologyInvariant("support", "precondition_artifact", "New -> Open requires artifact:spec_plan"));
	}

	[Fact]
	public void SoftenedArtifactGate_RendersAsConvention_DistinctInvariantRule()
	{
		var guide = Render();
		guide.Markdown.Should().Contain("requires a reason (convention — the server does not block it)");
		guide.Invariants.Should().Contain(new MethodologyInvariant("support", "reason_required_convention", "Open -> Junk"));
		guide.Invariants.Should().NotContain(i => i.Rule == "reason_required" && i.Kind == "support");
	}

	[Fact]
	public void PresetKinds_StayEnforced_BitForBitReproduction()
	{
		// The quartet/classic presets are untouched by this leaf: `ideas` still renders its
		// exploring->review artifact:spec_plan gate as ENFORCED, `intake`'s reason gates the
		// same — never the new "_convention" rule name, whatever this project's OWN StrictMode
		// says (a definition's strictMode never reaches back into the presets it doesn't declare).
		var guide = MethodologyGuide.Render("presets", new MethodologyRuntime(GuideDefinition(strictMode: true)), "mixed", 1);
		guide.Invariants.Should().Contain(new MethodologyInvariant("ideas", "precondition_artifact", "exploring -> review requires artifact:spec_plan"));
		guide.Invariants.Should().Contain(new MethodologyInvariant("intake", "reason_required", "triage -> wontfix"));
		guide.Invariants.Should().Contain(new MethodologyInvariant("work", "approval_gate", "Review -> Done"));
		guide.Invariants.Should().NotContain(i => i.Rule.EndsWith("_convention") && i.Kind != "support");
	}
}
