using LinqToDB;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Data;
using PetBox.Tasks.Services;
using PetBox.Tasks.Workflow;

namespace PetBox.Tests.Tasks;

// primitives-enum-residual: AutoWire work→spec and delivery type roles (feature/bug) are
// methodology DATA, not BoardKind/string special-cases in the service. Preset data carries
// the quartet defaults; a custom definition can declare its own wiring + type roles.
public sealed class EngineEnumResidualTests : IDisposable
{
	const string Proj = "proj";
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<TasksDb> _factory;
	readonly TasksService _tasks;

	public EngineEnumResidualTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-enum-residual-" + Guid.NewGuid().ToString("N"));
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

	// ── preset data ──────────────────────────────────────────────────────────

	[Fact]
	public void PresetWork_DeclaresAutoWireSpecFrom()
	{
		var work = MethodologyPresets.KindDef(BoardKind.Work);
		work.AutoWireSpecFrom.Should().Be("spec");
		MethodologyRuntime.PresetsOnly.AutoWireSpecFrom("work").Should().Be("spec");
		MethodologyRuntime.PresetsOnly.AutoWireSpecFrom("classic").Should().BeNull();
	}

	[Fact]
	public void PresetSpec_DeclaresDeliveryTypeRoles()
	{
		var spec = MethodologyPresets.KindDef(BoardKind.Spec);
		spec.Delivery.Should().NotBeNull();
		spec.Delivery!.RequiredTypes.Should().Equal("feature");
		spec.Delivery.DefectTypes.Should().Equal("bug");
		var d = MethodologyRuntime.PresetsOnly.DeliveryOf("spec");
		d.Should().NotBeNull();
		d!.RequiredTypes.Should().Equal("feature");
		MethodologyRuntime.PresetsOnly.DeliveryOf("work").Should().BeNull();
	}

	[Fact]
	public void RenderPresetDefinition_Quartet_CarriesAutoWireAndDelivery()
	{
		var def = MethodologyPresets.RenderPresetDefinition("quartet");
		def.Kinds.Single(k => k.Kind == "work").AutoWireSpecFrom.Should().Be("spec");
		var delivery = def.Kinds.Single(k => k.Kind == "spec").Delivery;
		delivery.Should().NotBeNull();
		delivery!.RequiredTypes.Should().Equal("feature");
		delivery.DefectTypes.Should().Equal("bug");
	}

	// ── auto-wire from data ──────────────────────────────────────────────────

	[Fact]
	public async Task AutoWire_QuartetParity_WiresWorkToSpec()
	{
		await _tasks.CreateBoardAsync(Proj, "spec", "spec", "s", null);
		await _tasks.CreateBoardAsync(Proj, "work", "work", "w", null);
		var work = (await _tasks.ListBoardsAsync(Proj)).Single(b => b.Name == "work");
		work.SpecBoard.Should().Be("spec");
	}

	// A custom definition kind pair with AutoWireSpecFrom wires without any BoardKind
	// enum involvement — the multi-instance honesty precondition.
	[Fact]
	public async Task AutoWire_CustomKinds_WiresFromDefinitionData()
	{
		var def = new MethodologyDefinition("custom-wire",
		[
			new("requirements", QuickAddAllowed: false,
			[
				new MethodologyWorkflowDef(["req"],
					[new("open", "Open", StatusKind.Open), new("closed", "Closed", StatusKind.TerminalOk)],
					[new("open", "closed")]),
			]),
			new("delivery", QuickAddAllowed: true,
			[
				new MethodologyWorkflowDef(["task"],
					[new("Todo", "Todo", StatusKind.Open), new("Done", "Done", StatusKind.TerminalOk)],
					[new("Todo", "Done")]),
			])
			{
				AutoWireSpecFrom = "requirements",
			},
		]);
		await _tasks.UpsertMethodologyTemplateAsync(Proj, "wire-tmpl", def, 0);
		await _tasks.CreateMethodologyInstanceAsync(Proj, "wire", "template", "wire-tmpl");

		// Create provisions boards named after kind slugs and auto-wires within the instance.
		var deliv = (await _tasks.ListBoardsAsync(Proj)).Single(b => b.Kind == "delivery");
		var reqs = (await _tasks.ListBoardsAsync(Proj)).Single(b => b.Kind == "requirements");
		deliv.SpecBoard.Should().Be(reqs.Name, "AutoWireSpecFrom=requirements on the delivery kind");
	}

	[Fact]
	public async Task AutoWire_NoTargetBoard_LeavesSpecBoardEmpty()
	{
		await _tasks.CreateBoardAsync(Proj, "work", "work", "w", null);
		var work = (await _tasks.ListBoardsAsync(Proj)).Single(b => b.Name == "work");
		work.SpecBoard.Should().BeNull("no active spec board → nothing to wire");
	}

	// ── delivery from data ───────────────────────────────────────────────────

	// Quartet parity: feature drives progress, open bug → done_with_defects.
	[Fact]
	public async Task Delivery_QuartetParity_FeatureAndBugRoles()
	{
		await _tasks.EnableMethodologyAsync(Proj);
		// Direct-to-accepted at creation is legal (no transition fires at birth).
		await _tasks.UpsertAsync(Proj, "ideas", [new NodePatch { Key = "i1", Title = "I", Body = "x", Status = "accepted", Type = "idea" }]);
		var ideaId = (await _tasks.GetAsync(Proj, "ideas", includeClosed: true)).Nodes.Single().NodeId;
		await _tasks.UpsertAsync(Proj, "spec", [new NodePatch { Key = "s1", Title = "S", Body = "x", Status = "defined", Type = "spec", Links = PetBox.Tests.TestLinks.IdeaSpec(ideaId) }]);
		var specId = (await _tasks.GetAsync(Proj, "spec")).Nodes.Single().NodeId;

		// Born Done (TerminalOk) — no Review→Done transition at birth.
		await _tasks.UpsertAsync(Proj, "work", [new NodePatch { Key = "f1", Title = "F", Body = "x", Type = "feature", Status = "Done", Links = PetBox.Tests.TestLinks.TaskSpec(specId) }]);
		(await _tasks.GetAsync(Proj, "spec")).Nodes.Single().Delivery.Should().Be("done");

		await _tasks.UpsertAsync(Proj, "work", [new NodePatch { Key = "b1", Title = "B", Body = "x", Type = "bug", Status = "Pending", Links = PetBox.Tests.TestLinks.TaskSpec(specId) }]);
		(await _tasks.GetAsync(Proj, "spec")).Nodes.Single().Delivery.Should().Be("done_with_defects");
	}

	// Custom type roles via a definition that overrides `spec` — delivery uses story/defect
	// instead of feature/bug; the service has no knowledge of those literals.
	[Fact]
	public async Task Delivery_CustomTypeRoles_FromDefinition()
	{
		var def = new MethodologyDefinition("custom-delivery",
		[
			MethodologyPresets.KindDef(BoardKind.Ideas),
			new("spec", QuickAddAllowed: false,
			[
				new MethodologyWorkflowDef(["spec"],
					[new("defined", "Defined", StatusKind.Open), new("deprecated", "Deprecated", StatusKind.TerminalCancel)],
					[new("defined", "deprecated")]),
			])
			{
				LinkConstraints =
				[
					new MethodologyLinkConstraintDef("spec", "idea_spec") { TargetKind = "ideas", TargetStatuses = ["accepted"] },
				],
				Delivery = new MethodologyDeliveryDef(["story"], ["defect"], "task_spec"),
			},
			new("work", QuickAddAllowed: false,
			[
				new MethodologyWorkflowDef(["story", "defect"],
					[
						new("Pending", "Pending", StatusKind.Open),
						new("Done", "Done", StatusKind.TerminalOk),
					],
					[new("Pending", "Done")]),
			])
			{
				LinkConstraints =
				[
					new MethodologyLinkConstraintDef("story", "task_spec") { TargetKind = "spec" },
					new MethodologyLinkConstraintDef("defect", "task_spec") { TargetKind = "spec" },
				],
				AutoWireSpecFrom = "spec",
			},
		]);
		await _tasks.UpsertMethodologyTemplateAsync(Proj, "delivery-tmpl", def, 0);
		await _tasks.CreateMethodologyInstanceAsync(Proj, "delivery", "template", "delivery-tmpl");

		await _tasks.UpsertAsync(Proj, "ideas", [new NodePatch { Key = "i1", Title = "I", Body = "x", Status = "accepted", Type = "idea" }]);
		var ideaId = (await _tasks.GetAsync(Proj, "ideas", includeClosed: true)).Nodes.Single().NodeId;
		await _tasks.UpsertAsync(Proj, "spec", [new NodePatch { Key = "s1", Title = "S", Body = "x", Status = "defined", Type = "spec", Links = PetBox.Tests.TestLinks.IdeaSpec(ideaId) }]);
		var specId = (await _tasks.GetAsync(Proj, "spec")).Nodes.Single().NodeId;

		// A Done story → done (story is requiredTypes; feature would NOT count).
		await _tasks.UpsertAsync(Proj, "work", [new NodePatch { Key = "st1", Title = "Story", Body = "x", Type = "story", Status = "Done", Links = PetBox.Tests.TestLinks.TaskSpec(specId) }]);
		(await _tasks.GetAsync(Proj, "spec")).Nodes.Single().Delivery.Should().Be("done");

		// An open defect → done_with_defects (no hardcoded "bug" needed).
		await _tasks.UpsertAsync(Proj, "work", [new NodePatch { Key = "d1", Title = "Defect", Body = "x", Type = "defect", Status = "Pending", Links = PetBox.Tests.TestLinks.TaskSpec(specId) }]);
		(await _tasks.GetAsync(Proj, "spec")).Nodes.Single().Delivery.Should().Be("done_with_defects");
	}

	// A board kind with no Delivery config does not compute delivery — even if named "spec"
	// by coincidence of process role under a definition that omits the field.
	[Fact]
	public async Task Delivery_AbsentConfig_NoRollup()
	{
		var def = new MethodologyDefinition("no-delivery",
		[
			new("spec", QuickAddAllowed: true,
			[
				new MethodologyWorkflowDef(["spec"],
					[new("defined", "Defined", StatusKind.Open)],
					[]),
			]),
			// no Delivery, no LinkConstraints
		]);
		await _tasks.UpsertMethodologyTemplateAsync(Proj, "no-deliv-tmpl", def, 0);
		var ack = await _tasks.CreateMethodologyInstanceAsync(Proj, "no-deliv", "template", "no-deliv-tmpl");
		// Single-kind instance: board is named after the instance.
		var board = ack.Boards.Single().Name;
		await _tasks.UpsertAsync(Proj, board, [new NodePatch { Key = "s1", Title = "S", Body = "x", Status = "defined", Type = "spec" }]);
		(await _tasks.GetAsync(Proj, board)).Nodes.Single().Delivery.Should().BeNull("no Delivery config on the kind");
	}

	// ── guide ────────────────────────────────────────────────────────────────

	[Fact]
	public void Guide_RendersAutoWireAndDeliveryFromData()
	{
		var guide = MethodologyGuide.Render(MethodologyPresets.Name, MethodologyRuntime.PresetsOnly, "presets", null);
		guide.Invariants.Should().Contain(i => i.Kind == "work" && i.Rule == "auto_wire" && i.Detail == "spec");
		guide.Invariants.Should().Contain(i => i.Kind == "spec" && i.Rule == "delivery" && i.Detail == "required:feature; defects:bug");
		guide.Markdown.Should().Contain("### Auto-wire");
		guide.Markdown.Should().Contain("### Delivery roll-up");
	}

	// ── validation ───────────────────────────────────────────────────────────

	[Fact]
	public async Task Define_InvalidAutoWire_Rejected()
	{
		var def = new MethodologyDefinition("bad-wire",
		[
			new("work", QuickAddAllowed: true,
			[
				new MethodologyWorkflowDef(["task"],
					[new("Todo", "Todo", StatusKind.Open)],
					[]),
			])
			{
				AutoWireSpecFrom = "work", // self
			},
		]);
		var act = () => _tasks.DefineMethodologyAsync(Proj, def, 0);
		(await act.Should().ThrowAsync<ArgumentException>()).WithMessage("*autoWireSpecFrom cannot name the same kind*");
	}

	[Fact]
	public async Task Define_EmptyDeliveryRequiredTypes_Rejected()
	{
		var def = new MethodologyDefinition("bad-delivery",
		[
			new("spec", QuickAddAllowed: true,
			[
				new MethodologyWorkflowDef(["spec"],
					[new("defined", "Defined", StatusKind.Open)],
					[]),
			])
			{
				Delivery = new MethodologyDeliveryDef([], ["bug"], "task_spec"),
			},
		]);
		var act = () => _tasks.DefineMethodologyAsync(Proj, def, 0);
		(await act.Should().ThrowAsync<ArgumentException>()).WithMessage("*delivery.requiredTypes must be non-empty*");
	}
}
