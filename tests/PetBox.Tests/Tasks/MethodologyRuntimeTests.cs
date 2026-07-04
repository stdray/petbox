using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using PetBox.Tasks.Contract;

namespace PetBox.Tests.Tasks;

// The DATA-DRIVEN FSM engine (wave 1.2): a kind declared in the project's methodology
// definition resolves board creation, types, statuses, transitions, gates and terminal
// semantics from the DEFINITION, while every other kind — in the same project — keeps the
// built-in preset definitions exactly as before. Exercised end-to-end over MCP.
//
// The custom kind under test: `support`, one workflow block shared by ticket|incident —
//   New(open) → Open(open) → Resolved(terminalok) [preconditionArtifact resolution_note]
//   New/Open → Rejected(terminalcancel) [requiresReason]
public sealed class MethodologyRuntimeTests : IClassFixture<MethodologyRuntimeFixture>, IAsyncLifetime
{
	const string ProjectKey = "mrt";

	readonly MethodologyRuntimeFixture _fx;
	readonly McpClient _mcp;

	public MethodologyRuntimeTests(MethodologyRuntimeFixture fx)
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

	static JsonElement Nodes(params object[] nodes) => JsonSerializer.SerializeToElement(nodes);

	static string Text(CallToolResult r) =>
		r.Content.OfType<TextContentBlock>().First().Text;

	// Errors arrive as the central envelope {"error":{...}} on the isError channel (IsError=true).
	static bool IsErr(CallToolResult r) =>
		r.IsError == true ||
		(r.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text?.Contains("\"error\"") ?? false);

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

	static string NodeIdOf(CallToolResult r, string key) => FieldOf(r, key, "nodeId");

	// The `support` kind: one FSM shared by ticket|incident, a reason-gated cancel edge and
	// an artifact-gated done edge (statuses[0] = New is the initial).
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
							new { slug = "Rejected", kind = "terminalcancel" },
						},
						transitions = new object[]
						{
							new { from = "New", to = "Open" },
							new { from = "Open", to = "Resolved", preconditionArtifact = "resolution_note" },
							new { from = "New", to = "Rejected", requiresReason = true },
							new { from = "Open", to = "Rejected", requiresReason = true },
						},
					},
				},
			},
		},
	};

	async Task Define()
	{
		var r = await Call("tasks_methodology_def_upsert", new { projectKey = ProjectKey, definition = SupportDefinition(), version = 0 });
		IsErr(r).Should().BeFalse(Text(r));
	}

	Task<CallToolResult> Upsert(string board, params object[] nodes) =>
		Call("tasks_upsert", new { projectKey = ProjectKey, board, nodes = Nodes(nodes) });

	// ── scenarios ────────────────────────────────────────────────────────────

	// 1. a definition-declared kind is accepted by board_create; an upsert without a status
	// lands in the block's initial status (statuses[0]); an undeclared type is rejected
	// naming the kind's type vocabulary.
	[Fact]
	public async Task DefinedKind_BoardCreate_InitialStatus_TypeVocabulary()
	{
		await Define();
		var created = await Call("tasks_board_create", new { projectKey = ProjectKey, board = "helpdesk", kind = "support" });
		IsErr(created).Should().BeFalse(Text(created));
		Text(created).Should().Contain("support");

		var up = await Upsert("helpdesk", new { key = "tik-one", type = "ticket", title = "Printer on fire", body = "x" });
		IsErr(up).Should().BeFalse(Text(up));

		var get = await Call("tasks_search", new { projectKey = ProjectKey, board = "helpdesk" });
		FieldOf(get, "tik-one", "status").Should().Be("New", "statuses[0] of the block is the initial status");
		Text(get).Should().Contain("\"kind\":\"support\"", "the board reports its defined kind, not a preset");

		var bad = await Upsert("helpdesk", new { key = "mtg", type = "meeting", title = "M", body = "x" });
		IsErr(bad).Should().BeTrue(Text(bad));
		Text(bad).Should().Contain("ticket|incident");
	}

	// 2. transitions come from the definition: a declared edge passes, an undeclared edge is
	// rejected naming the valid next statuses.
	[Fact]
	public async Task DefinedKind_LegalTransitionOk_IllegalListsValidTargets()
	{
		await Define();
		await Call("tasks_board_create", new { projectKey = ProjectKey, board = "flowb", kind = "support" });
		await Upsert("flowb", new { key = "t", type = "ticket", title = "T", body = "x" });

		var toOpen = await Upsert("flowb", new { key = "t", version = 1, status = "Open" });
		IsErr(toOpen).Should().BeFalse(Text(toOpen));

		// Open -> New is not a declared edge.
		var back = await Upsert("flowb", new { key = "t", version = 2, status = "New" });
		IsErr(back).Should().BeTrue(Text(back));
		Text(back).Should().Contain("no transition");
		Text(back).Should().Contain("Resolved"); // names the valid next statuses from Open
	}

	// 3. requiresReason from the definition: the transition demands a non-empty body.
	[Fact]
	public async Task DefinedKind_RequiresReason_Enforced()
	{
		await Define();
		await Call("tasks_board_create", new { projectKey = ProjectKey, board = "reasonb", kind = "support" });
		await Upsert("reasonb", new { key = "t", type = "ticket", title = "T" }); // no body

		var noReason = await Upsert("reasonb", new { key = "t", version = 1, status = "Rejected" });
		IsErr(noReason).Should().BeTrue(Text(noReason));
		Text(noReason).Should().Contain("requires a reason");

		var withReason = await Upsert("reasonb", new { key = "t", version = 1, status = "Rejected", body = "duplicate of another ticket" });
		IsErr(withReason).Should().BeFalse(Text(withReason));
	}

	// 4. preconditionArtifact from the definition: the transition is refused (naming the
	// artifact tag and the edge) until an artifact:<slug> comment exists on the node; and a
	// node can't be BORN directly in the gated target status either.
	[Fact]
	public async Task DefinedKind_PreconditionArtifact_GatesUntilCommentExists()
	{
		await Define();
		await Call("tasks_board_create", new { projectKey = ProjectKey, board = "gateb", kind = "support" });
		var up = await Upsert("gateb", new { key = "t", type = "ticket", title = "T", body = "x" });
		var nodeId = NodeIdOf(up, "t");
		await Upsert("gateb", new { key = "t", version = 1, status = "Open" });

		var gated = await Upsert("gateb", new { key = "t", version = 2, status = "Resolved" });
		IsErr(gated).Should().BeTrue(Text(gated));
		Text(gated).Should().Contain("artifact:resolution_note", "the rejection names the missing artifact tag");
		Text(gated).Should().Contain("Open"); // ...and the transition
		Text(gated).Should().Contain("Resolved");

		var cold = await Upsert("gateb", new { key = "cold", type = "ticket", status = "Resolved", title = "Cold", body = "x" });
		IsErr(cold).Should().BeTrue(Text(cold));
		Text(cold).Should().Contain("directly");

		var note = await Call("comments_create", new { projectKey = ProjectKey, board = "gateb", nodeId, author = "t", body = "fixed by rebooting", tags = new[] { "artifact:resolution_note" } });
		IsErr(note).Should().BeFalse(Text(note));

		var resolved = await Upsert("gateb", new { key = "t", version = 2, status = "Resolved" });
		IsErr(resolved).Should().BeFalse(Text(resolved));
	}

	// 5. the definition's terminal statuses drive the closed-node predicate: Resolved /
	// Rejected nodes vanish from the default listing and come back with includeClosed or an
	// explicit status filter.
	[Fact]
	public async Task DefinedKind_TerminalStatuses_HiddenByDefault()
	{
		await Define();
		await Call("tasks_board_create", new { projectKey = ProjectKey, board = "termb", kind = "support" });
		var up = await Upsert("termb",
			new { key = "stays", type = "ticket", title = "Stays", body = "x" },
			new { key = "fixed", type = "ticket", title = "Fixed", body = "x" },
			new { key = "dup", type = "incident", title = "Dup", body = "x" });
		IsErr(up).Should().BeFalse(Text(up));

		// fixed: New → Open → (artifact) → Resolved; dup: New → Rejected (reason).
		var fixedId = NodeIdOf(up, "fixed");
		await Upsert("termb", new { key = "fixed", version = 1, status = "Open" });
		await Call("comments_create", new { projectKey = ProjectKey, board = "termb", nodeId = fixedId, author = "t", body = "done", tags = new[] { "artifact:resolution_note" } });
		IsErr(await Upsert("termb", new { key = "fixed", version = 2, status = "Resolved" })).Should().BeFalse();
		IsErr(await Upsert("termb", new { key = "dup", version = 1, status = "Rejected", body = "dup of fixed" })).Should().BeFalse();

		var def = await Call("tasks_search", new { projectKey = ProjectKey, board = "termb" });
		Text(def).Should().Contain("stays");
		Text(def).Should().NotContain("\"key\":\"fixed\"", "Resolved is terminalok in the definition");
		Text(def).Should().NotContain("\"key\":\"dup\"", "Rejected is terminalcancel in the definition");

		var all = await Call("tasks_search", new { projectKey = ProjectKey, board = "termb", includeClosed = true });
		Text(all).Should().Contain("\"key\":\"fixed\"");
		Text(all).Should().Contain("\"key\":\"dup\"");

		var filtered = await Call("tasks_search", new { projectKey = ProjectKey, board = "termb", status = new[] { "Resolved" } });
		Text(filtered).Should().Contain("\"key\":\"fixed\"", "an explicitly named terminal status is returned without includeClosed");
		Text(filtered).Should().NotContain("\"key\":\"stays\"");
	}

	// 6. tasks_workflow on a defined-kind board answers from the definition — same response
	// shape as the presets, plus preconditionArtifact on the gated transition.
	[Fact]
	public async Task Workflow_DefinedKind_ReturnsDataDrivenBlocks()
	{
		await Define();
		await Call("tasks_board_create", new { projectKey = ProjectKey, board = "wfb", kind = "support" });
		var wf = await Call("tasks_workflow", new { projectKey = ProjectKey, board = "wfb" });
		IsErr(wf).Should().BeFalse(Text(wf));

		using var doc = JsonDocument.Parse(Text(wf));
		doc.RootElement.GetProperty("kind").GetString().Should().Be("support");
		var block = doc.RootElement.GetProperty("workflows").EnumerateArray()
			.Should().ContainSingle().Subject;
		block.GetProperty("types").EnumerateArray().Select(t => t.GetString())
			.Should().Equal("ticket", "incident");
		block.GetProperty("initial").GetString().Should().Be("New");
		block.GetProperty("statuses").EnumerateArray().Select(s => s.GetProperty("slug").GetString())
			.Should().Equal("New", "Open", "Resolved", "Rejected");
		var gated = block.GetProperty("transitions").EnumerateArray()
			.Single(t => t.GetProperty("to").GetString() == "Resolved");
		gated.GetProperty("preconditionArtifact").GetString().Should().Be("resolution_note");
		var reason = block.GetProperty("transitions").EnumerateArray()
			.Single(t => t.GetProperty("from").GetString() == "New" && t.GetProperty("to").GetString() == "Rejected");
		reason.GetProperty("requiresReason").GetBoolean().Should().BeTrue();
	}

	// 7. fallback: in the SAME project (definition present), a preset board keeps its
	// catalog behavior — vocab enforced, free transitions, workflow answers from the preset.
	[Fact]
	public async Task Fallback_PresetBoards_Unaffected_ByDefinition()
	{
		await Define();
		await Call("tasks_board_create", new { projectKey = ProjectKey, board = "scratch" }); // simple
		IsErr(await Upsert("scratch", new { key = "ok", status = "Todo", title = "OK", body = "x" })).Should().BeFalse();

		var bad = await Upsert("scratch", new { key = "bad", status = "Frobnicate", title = "B", body = "x" });
		IsErr(bad).Should().BeTrue(Text(bad));
		Text(bad).Should().Contain("Todo"); // the preset vocab, not the definition's

		// free transitions straight to terminal stay legal on the preset.
		IsErr(await Upsert("scratch", new { key = "ok", version = 1, status = "Done" })).Should().BeFalse();

		using var doc = JsonDocument.Parse(Text(await Call("tasks_workflow", new { projectKey = ProjectKey, board = "scratch" })));
		doc.RootElement.GetProperty("kind").GetString().Should().Be("simple");
		doc.RootElement.GetProperty("workflows").EnumerateArray().Should().ContainSingle()
			.Subject.GetProperty("initial").GetString().Should().Be("Todo");
	}

	// 8. an undeclared kind is still rejected at board_create, naming the valid kinds
	// (builtin + defined).
	[Fact]
	public async Task BoardCreate_UndeclaredKind_RejectedListingValidKinds()
	{
		await Define();
		var r = await Call("tasks_board_create", new { projectKey = ProjectKey, board = "nope", kind = "banana" });
		IsErr(r).Should().BeTrue(Text(r));
		Text(r).Should().Contain("unknown board kind");
		Text(r).Should().Contain("support"); // the defined kind is listed as valid
		Text(r).Should().Contain("work");    // alongside the builtin ones
	}

	// 9. UI quick-add on a defined-kind board: the node is born with the kind's default
	// type (first type of the first block) in the block's initial status.
	[Fact]
	public async Task QuickAdd_DefinedKind_DefaultTypeAndInitialStatus()
	{
		await Define();
		await Call("tasks_board_create", new { projectKey = ProjectKey, board = "qab", kind = "support" });

		using var scope = _fx.Factory.Services.CreateScope();
		var tasks = scope.ServiceProvider.GetRequiredService<ITasksService>();
		await tasks.QuickAddAsync(ProjectKey, "qab", "Printer on fire", null, 50);

		var nodes = await tasks.ListActiveNodesAsync(ProjectKey, "qab");
		var node = nodes.Should().ContainSingle().Subject;
		node.Type.Should().Be("ticket", "the first type of the first block is the quick-add default");
		node.Status.Should().Be("New");
	}
}
