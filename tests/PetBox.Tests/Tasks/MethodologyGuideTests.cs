using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using PetBox.Tasks.Contract;
using PetBox.Web.Mcp;
using PetBox.Web.Mcp.Contract;

namespace PetBox.Tests.Tasks;

// Engine wave 4 (spec artifacts-from-definition) — tasks_methodology_guide: the agent
// process guide (markdown + structured invariants) DERIVED AT RUNTIME from the project's
// effective methodology. Exercised end-to-end over MCP:
//   (a) a definition-less project renders the preset quartet + simple — the hardcoded
//       process invariants (never self-set Done/accepted, spec_plan gate, feature→specRef)
//       appear as DATA-BORN text, and simple's all-pairs block collapses to "free";
//   (b) a project with a custom `support` kind renders that kind's gates/constraints/axes
//       from its definition (source reflects it) alongside the preset fallback;
//   (c) the rendering is deterministic — two calls, identical text.
public sealed class MethodologyGuideTests : IClassFixture<MethodologyGuideFixture>, IAsyncLifetime
{
	const string ProjectKey = "mgd";

	readonly MethodologyGuideFixture _fx;
	readonly McpClient _mcp;

	public MethodologyGuideTests(MethodologyGuideFixture fx)
	{
		_fx = fx;
		_mcp = fx.Mcp;
	}

	public Task InitializeAsync() => _fx.ResetAsync();

	public Task DisposeAsync() => Task.CompletedTask; // the fixture owns host teardown

	// ── helpers ──────────────────────────────────────────────────────────────

	async Task<CallToolResult> Call(string tool, object args) =>
		await (await _mcp.ListToolsAsync()).First(t => t.Name == tool)
			.CallAsync(JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(args))!
				.ToDictionary(kv => kv.Key, kv => (object?)((JsonElement)kv.Value!)));

	static string Text(CallToolResult r) =>
		r.Content.OfType<TextContentBlock>().First().Text;

	// Errors arrive as the central envelope {"error":{...}} on the isError channel (IsError=true).
	static bool IsErr(CallToolResult r) =>
		r.IsError == true ||
		(r.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text?.Contains("\"error\"") ?? false);

	static JsonElement Parse(CallToolResult r) =>
		JsonDocument.Parse(Text(r)).RootElement.Clone();

	async Task<JsonElement> Guide()
	{
		var r = await Call("tasks_methodology_guide", new { projectKey = ProjectKey });
		IsErr(r).Should().BeFalse(Text(r));
		return Parse(r);
	}

	static IReadOnlyList<(string Kind, string Rule, string Detail)> Invariants(JsonElement guide) =>
		guide.GetProperty("invariants").EnumerateArray()
			.Select(i => (i.GetProperty("kind").GetString()!, i.GetProperty("rule").GetString()!, i.GetProperty("detail").GetString()!))
			.ToList();

	// The custom kind under test: `support` (ticket|incident) with all four data-born rule
	// sources — an approval gate (Open→Resolved), a reason gate (Open→Junk), an artifact
	// precondition (New→Open needs artifact:triage-note), a creation link constraint
	// (incident must carry blocks), declared tag axes and a declared link kind.
	static object SupportDefinition() => new
	{
		name = "support-process",
		kinds = new object[]
		{
			new
			{
				kind = "support",
				description = "Support requests: tickets and incidents.",
				quickAddAllowed = true,
				workflows = new object[]
				{
					new
					{
						types = new[] { "ticket", "incident" },
						statuses = new object[]
						{
							new { slug = "New", kind = "open" },
							new { slug = "Open", kind = "open", description = "Actively triaged." },
							new { slug = "Resolved", kind = "terminalok" },
							new { slug = "Junk", kind = "terminalcancel" },
						},
						transitions = new object[]
						{
							new { from = "New", to = "Open", preconditionArtifact = "triage-note", description = "Triage assigns severity." },
							new { from = "Open", to = "Resolved", requiresApproval = true },
							new { from = "Open", to = "Junk", requiresReason = true },
						},
					},
				},
				linkConstraints = new object[] { new { type = "incident", link = "blocks", description = "Every incident names a related outage." } },
			},
		},
		linkKinds = new object[] { new { slug = "escalates", description = "support escalation edge" } },
		tagAxes = new object[]
		{
			new { @namespace = "severity" },
			new { @namespace = "channel" },
		},
	};

	// (a) no definition → the guide covers the preset quartet + classic + simple, and the
	// invariants that used to be hardcoded prose are rendered from the preset DATA.
	[Fact]
	public async Task PresetsOnly_GuideCoversQuartetAndSimple_InvariantsAreDataBorn()
	{
		var guide = await Guide();

		guide.GetProperty("source").GetString().Should().Be("presets");
		guide.TryGetProperty("definitionVersion", out _).Should().BeFalse("no definition — null is omitted by the serializer");

		var md = guide.GetProperty("markdown").GetString()!;
		md.Should().Contain("## Kind: intake");
		md.Should().Contain("## Kind: ideas");
		md.Should().Contain("## Kind: spec");
		md.Should().Contain("## Kind: work");
		md.Should().Contain("## Kind: classic");
		md.Should().Contain("## Kind: simple");

		// The approve gates — 'agent never self-sets Done/accepted', born from RequiresApproval.
		md.Should().Contain("The agent NEVER performs Review -> Done");
		md.Should().Contain("The agent NEVER performs review -> accepted");
		// The idea-review gate, born from PreconditionArtifact.
		md.Should().Contain("Add an `artifact:spec_plan` comment on the node before exploring -> review");
		// The work creation constraint, born from LinkConstraints.
		md.Should().Contain("A new `feature` must carry a `task_spec` link (provide `links.task_spec`");
		// Quartet tag axes.
		md.Should().Contain("area, concern");
		// Simple's all-pairs block collapses instead of listing 20 edges.
		md.Should().Contain("Transitions: free — any status may move to any other (Todo | InProgress | Blocked | Done | Cancelled)");

		// classic (preset-classic, reworked): ONE workflow section for all types — task,
		// feature and bug are labels over the same FSM, so the task and bug renderings
		// can't diverge. The reason gate exists only INTO Duplicate (Cancelled closes
		// reason-free, the GitHub way) and no checklist renders anywhere (the bug repro
		// checklist left the preset for a deliberation idea).
		md.Should().Contain("### Workflow: task | feature | bug");
		md.Should().NotContain("### Workflow: bug");
		md.Should().Contain("- Types: task (default), feature, bug");
		md.Should().Contain("InProgress -> Duplicate requires a reason");
		md.Should().NotContain("Cancelled requires a reason");
		md.Should().NotContain("convention — the server does not check these");
		md.Should().NotContain("Есть воспроизведение бага, или зафиксирована причина, почему воспроизведения нет");

		var inv = Invariants(guide);
		inv.Should().Contain(("work", "approval_gate", "Review -> Done"));
		inv.Should().Contain(("ideas", "approval_gate", "review -> accepted"));
		inv.Should().Contain(("ideas", "precondition_artifact", "exploring -> review requires artifact:spec_plan"));
		// engine-v2: constraints carry their declared target, effects render as invariants.
		inv.Should().Contain(("work", "link_constraint", "feature requires task_spec (links.task_spec) -> spec"));
		inv.Should().Contain(("spec", "link_constraint", "spec requires idea_spec (links.idea_spec) -> ideas[accepted]"));
		inv.Should().Contain(("work", "transition_effect", "Done: incoming issue_task -> done"));
		inv.Should().Contain(("work", "transition_effect", "Done: outgoing blocks from Blocked -> InProgress"));
		inv.Should().Contain(("work", "tag_axes", "area|concern"));
		// primitives-enum-residual: auto-wire + delivery type roles are DATA-born.
		inv.Should().Contain(("work", "auto_wire", "spec"));
		inv.Should().Contain(("spec", "delivery", "required:feature; defects:bug"));
		md.Should().Contain("auto-wires SpecBoard of the `work` board to the `spec` board");
		md.Should().Contain("required types (feature)");
		md.Should().Contain("defect types (bug)");
		inv.Should().NotContain(i => i.Kind == "simple" && i.Rule == "tag_axes", "simple declares no axes");

		// classic's gates, machine-readable: the Duplicate reason gate ONLY — no Cancelled
		// reasons, no checklist, no axes, no approval gate anywhere in the kind.
		inv.Should().Contain(("classic", "reason_required", "Todo -> Duplicate"));
		inv.Should().Contain(("classic", "reason_required", "InReview -> Duplicate"));
		inv.Should().NotContain(i => i.Kind == "classic" && i.Rule == "reason_required" && i.Detail.EndsWith("Cancelled"));
		inv.Should().NotContain(i => i.Kind == "classic" &&
			(i.Rule == "checklist" || i.Rule == "tag_axes" || i.Rule.StartsWith("approval_gate")));
	}

	// (b) an instance kind renders its gates/constraints/axes from DATA — the same renderer,
	// nothing support-specific in code — and the preset kinds it doesn't override remain.
	[Fact]
	public async Task Definition_CustomKindRendersFromData_SourceReflectsIt()
	{
		var version = await InstallSupportInstanceAsync();

		var guide = await Guide();
		guide.GetProperty("source").GetString().Should().Be("instance", "one open instance drives the guide");
		guide.GetProperty("definitionVersion").GetInt64().Should().Be(version);

		var md = guide.GetProperty("markdown").GetString()!;
		md.Should().Contain("## Kind: support");
		md.Should().Contain("Support requests: tickets and incidents.", "a kind's Description is data the compiled guide must convey (spec methodology-primitive-descriptions)");
		md.Should().Contain("Types: ticket (default), incident");
		md.Should().Contain("Open (Actively triaged.)", "a status's Description renders alongside its slug");
		md.Should().Contain("The agent NEVER performs Open -> Resolved");
		md.Should().Contain("Open -> Junk requires a reason");
		md.Should().Contain("Add an `artifact:triage-note` comment on the node before New -> Open");
		md.Should().Contain("note: Triage assigns severity.", "a transition's Description renders as a note alongside its other gates");
		md.Should().Contain("A new `incident` must carry a `blocks` link (provide `links.blocks`");
		md.Should().Contain("Every incident names a related outage.", "a link constraint's Description renders alongside its cadence sentence");
		md.Should().Contain("severity, channel");
		md.Should().Contain("escalates (support escalation edge)");
		md.Should().Contain("## Kind: work", "an undeclared preset kind still serves the project");

		var inv = Invariants(guide);
		inv.Should().Contain(("support", "approval_gate", "Open -> Resolved"));
		inv.Should().Contain(("support", "reason_required", "Open -> Junk"));
		inv.Should().Contain(("support", "precondition_artifact", "New -> Open requires artifact:triage-note"));
		inv.Should().Contain(("support", "link_constraint", "incident requires blocks (links.blocks)"));
		inv.Should().Contain(("support", "tag_axes", "severity|channel"));
		inv.Should().Contain(("work", "approval_gate", "Review -> Done"), "preset invariants stay alongside the definition's");
	}

	// (c) deterministic rendering: two calls, identical text (tests and downstream
	// consumers may assert substrings without flakes).
	[Fact]
	public async Task Guide_IsDeterministic()
	{
		await InstallSupportInstanceAsync();

		var first = await Guide();
		var second = await Guide();
		second.GetProperty("markdown").GetString().Should().Be(first.GetProperty("markdown").GetString());
		Invariants(second).Should().Equal(Invariants(first));
	}

	// Guide derives from open methodology instance rules.
	static readonly JsonSerializerOptions WireJson = new() { PropertyNameCaseInsensitive = true };

	async Task<long> InstallSupportInstanceAsync()
	{
		var json = JsonSerializer.Serialize(SupportDefinition());
		var input = JsonSerializer.Deserialize<MethodologyDefInput>(json, WireJson)
			?? throw new InvalidOperationException("failed to deserialize SupportDefinition");
		var def = MethodologyWire.ParseDefinition(input);
		def.Name.Should().NotBeNullOrEmpty("SupportDefinition must carry a methodology name");
		using var scope = _fx.Factory.Services.CreateScope();
		var tasks = scope.ServiceProvider.GetRequiredService<ITasksService>();
		await tasks.UpsertMethodologyTemplateAsync(ProjectKey, "support-tmpl", def, 0);
		await tasks.CreateMethodologyInstanceAsync(ProjectKey, "support", "template", "support-tmpl");
		var rules = await tasks.GetMethodologyInstanceRulesAsync(ProjectKey, "support");
		rules.Should().NotBeNull();
		return rules!.Version;
	}
}
