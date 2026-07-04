using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

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
				quickAddAllowed = true,
				workflows = new object[]
				{
					new
					{
						types = new[] { "ticket", "incident" },
						statuses = new object[]
						{
							new { slug = "New", kind = "open" },
							new { slug = "Open", kind = "open" },
							new { slug = "Resolved", kind = "terminalok" },
							new { slug = "Junk", kind = "terminalcancel" },
						},
						transitions = new object[]
						{
							new { from = "New", to = "Open", preconditionArtifact = "triage-note" },
							new { from = "Open", to = "Resolved", requiresApproval = true },
							new { from = "Open", to = "Junk", requiresReason = true },
						},
					},
				},
				linkConstraints = new object[] { new { type = "incident", link = "blocks" } },
			},
		},
		linkKinds = new object[] { new { slug = "escalates", description = "support escalation edge" } },
		tagAxes = new object[]
		{
			new { @namespace = "severity" },
			new { @namespace = "channel" },
		},
	};

	// (a) no definition → the guide covers the preset quartet + simple, and the invariants
	// that used to be hardcoded prose are rendered from the preset DATA.
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
		md.Should().Contain("## Kind: simple");

		// The approve gates — 'agent never self-sets Done/accepted', born from RequiresApproval.
		md.Should().Contain("The agent NEVER performs Review -> Done");
		md.Should().Contain("The agent NEVER performs review -> accepted");
		// The idea-review gate, born from PreconditionArtifact.
		md.Should().Contain("Add an `artifact:spec_plan` comment on the node before exploring -> review");
		// The work creation constraint, born from LinkConstraints.
		md.Should().Contain("A new `feature` must carry a `task_spec` link (provide `specRef`");
		// Quartet tag axes.
		md.Should().Contain("area, concern");
		// Simple's all-pairs block collapses instead of listing 20 edges.
		md.Should().Contain("Transitions: free — any status may move to any other (Todo | InProgress | Blocked | Done | Cancelled)");

		var inv = Invariants(guide);
		inv.Should().Contain(("work", "approval_gate", "Review -> Done"));
		inv.Should().Contain(("ideas", "approval_gate", "review -> accepted"));
		inv.Should().Contain(("ideas", "precondition_artifact", "exploring -> review requires artifact:spec_plan"));
		inv.Should().Contain(("work", "link_constraint", "feature requires task_spec (specRef)"));
		inv.Should().Contain(("work", "tag_axes", "area|concern"));
		inv.Should().NotContain(i => i.Kind == "simple" && i.Rule == "tag_axes", "simple declares no axes");
	}

	// (b) a definition kind renders its gates/constraints/axes from DATA — the same renderer,
	// nothing support-specific in code — and the preset kinds it doesn't override remain.
	[Fact]
	public async Task Definition_CustomKindRendersFromData_SourceReflectsIt()
	{
		var up = await Call("tasks_methodology_def_upsert", new { projectKey = ProjectKey, definition = SupportDefinition(), version = 0 });
		IsErr(up).Should().BeFalse(Text(up));
		var version = Parse(up).GetProperty("version").GetInt64();

		var guide = await Guide();
		guide.GetProperty("source").GetString().Should().Be("mixed", "the definition adds `support` but the preset kinds still serve");
		guide.GetProperty("definitionVersion").GetInt64().Should().Be(version);

		var md = guide.GetProperty("markdown").GetString()!;
		md.Should().Contain("## Kind: support");
		md.Should().Contain("Types: ticket (default), incident");
		md.Should().Contain("The agent NEVER performs Open -> Resolved");
		md.Should().Contain("Open -> Junk requires a reason");
		md.Should().Contain("Add an `artifact:triage-note` comment on the node before New -> Open");
		md.Should().Contain("A new `incident` must carry a `blocks` link (provide `blockedBy`");
		md.Should().Contain("severity, channel");
		md.Should().Contain("escalates (support escalation edge)");
		md.Should().Contain("## Kind: work", "an undeclared preset kind still serves the project");

		var inv = Invariants(guide);
		inv.Should().Contain(("support", "approval_gate", "Open -> Resolved"));
		inv.Should().Contain(("support", "reason_required", "Open -> Junk"));
		inv.Should().Contain(("support", "precondition_artifact", "New -> Open requires artifact:triage-note"));
		inv.Should().Contain(("support", "link_constraint", "incident requires blocks (blockedBy)"));
		inv.Should().Contain(("support", "tag_axes", "severity|channel"));
		inv.Should().Contain(("work", "approval_gate", "Review -> Done"), "preset invariants stay alongside the definition's");
	}

	// (c) deterministic rendering: two calls, identical text (tests and downstream
	// consumers may assert substrings without flakes).
	[Fact]
	public async Task Guide_IsDeterministic()
	{
		var up = await Call("tasks_methodology_def_upsert", new { projectKey = ProjectKey, definition = SupportDefinition(), version = 0 });
		IsErr(up).Should().BeFalse(Text(up));

		var first = await Guide();
		var second = await Guide();
		second.GetProperty("markdown").GetString().Should().Be(first.GetProperty("markdown").GetString());
		Invariants(second).Should().Equal(Invariants(first));
	}
}
