using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using PetBox.Tasks.Contract;
using PetBox.Web.Mcp;
using PetBox.Web.Mcp.Contract;

namespace PetBox.Tests.Tasks;

// Engine wave 2 — the three additive DEFINITION primitives, exercised end-to-end over MCP:
//   link constraints (per kind): a NEW node of a constrained type must carry the link at
//     creation (the data-driven RequireSpecLinks; edits don't re-require);
//   link kinds (project-wide): builtin NEUTRAL kinds (relates_to|depends_on|mirrors) for
//     every project + definition-declared kinds, all effect-free; unknown kinds rejected
//     listing the project's vocabulary;
//   tag axes (project-wide): declared namespaces enforced on definition-resolved boards
//     (bare tags rejected, same posture as the catalog's enforced mode); none declared =
//     free-form (the wave-1.2 behavior).
//
// The custom kind under test: `support`, one block shared by ticket|incident
// (New → Open → Resolved), with `incident` constrained to carry a `blocks` link at birth.
public sealed class MethodologyPrimitivesTests : IClassFixture<MethodologyPrimitivesFixture>, IAsyncLifetime
{
	const string ProjectKey = "mprm";

	readonly MethodologyPrimitivesFixture _fx;
	readonly McpClient _mcp;

	public MethodologyPrimitivesTests(MethodologyPrimitivesFixture fx)
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

	static JsonElement Parse(CallToolResult r) =>
		JsonDocument.Parse(Text(r)).RootElement.Clone();

	// The `support` kind's workflow surface (shared by every definition variant below).
	static object SupportWorkflows() => new object[]
	{
		new
		{
			types = new[] { "ticket", "incident" },
			statuses = new object[]
			{
				new { slug = "New", kind = "open" },
				new { slug = "Open", kind = "open" },
				new { slug = "Resolved", kind = "terminalok" },
			},
			transitions = new object[]
			{
				new { from = "New", to = "Open" },
				new { from = "Open", to = "Resolved" },
			},
		},
	};

	// All three wave-2 primitives: `incident` must carry a `blocks` link at creation,
	// `escalates` is a project-declared relation kind, tags run on two declared axes.
	static object FullDefinition() => new
	{
		name = "support-process",
		kinds = new object[]
		{
			new
			{
				kind = "support",
				quickAddAllowed = true,
				workflows = SupportWorkflows(),
				linkConstraints = new object[] { new { type = "incident", link = "blocks" } },
			},
		},
		linkKinds = new object[] { new { slug = "escalates", description = "support escalation edge" } },
		tagAxes = new object[]
		{
			new { @namespace = "severity" },
			new { @namespace = "channel", description = "intake channel" },
		},
	};

	// No wave-2 primitives at all — the wave-1 document shape (free-form regression).
	static object PlainDefinition() => new
	{
		name = "support-process",
		kinds = new object[] { new { kind = "support", quickAddAllowed = true, workflows = SupportWorkflows() } },
	};

	const string Tmpl = "support-tmpl";
	static readonly JsonSerializerOptions WireJson = new() { PropertyNameCaseInsensitive = true };

	// Install as a live methodology instance so RuntimeAsync / FSM validation sees the kinds.
	const string Inst = "support";

	async Task Define(object definition)
	{
		var json = JsonSerializer.Serialize(definition);
		var input = JsonSerializer.Deserialize<MethodologyDefInput>(json, WireJson)!;
		var def = MethodologyWire.ParseDefinition(input);
		using var scope = _fx.Factory.Services.CreateScope();
		var tasks = scope.ServiceProvider.GetRequiredService<ITasksService>();
		// Re-define: if instance already exists (re-run), rules_upsert; else create.
		var existing = await tasks.GetMethodologyInstanceAsync(ProjectKey, Inst);
		if (existing is null)
		{
			await tasks.UpsertMethodologyTemplateAsync(ProjectKey, Tmpl, def, 0);
			await tasks.CreateMethodologyInstanceAsync(ProjectKey, Inst, "template", Tmpl);
		}
		else
		{
			var rules = await tasks.GetMethodologyInstanceRulesAsync(ProjectKey, Inst);
			await tasks.DefineMethodologyInstanceRulesAsync(ProjectKey, Inst, def, rules!.Version);
		}
	}

	// Document-only write for validation-reject tests (template surface).
	Task<CallToolResult> DefUpsert(object definition) =>
		Call("tasks_methodology_template_upsert", new { projectKey = ProjectKey, key = Tmpl, definition, version = 0 });

	Task<CallToolResult> Upsert(string board, params object[] nodes) =>
		Call("tasks_upsert", new { projectKey = ProjectKey, board, nodes = Nodes(nodes) });

	async Task CreateBoard(string board, string? kind = "support")
	{
		var r = await Call("tasks_board_create", new { projectKey = ProjectKey, board, kind, methodologyInstance = Inst });
		IsErr(r).Should().BeFalse(Text(r));
	}

	// ── Part A: link constraints ─────────────────────────────────────────────

	// 1. a NEW node of the constrained type without the ref is rejected (naming the link and
	// the upsert field); with the ref (an in-batch sibling blocker) it is created; an EDIT
	// of an existing node doesn't re-require the ref; the unconstrained type is unaffected.
	[Fact]
	public async Task LinkConstraint_NewNodeNeedsRef_EditAndOtherTypesDoNot()
	{
		await Define(FullDefinition());
		await CreateBoard("helpdesk");

		// Happy path first: the blocker is a sibling created in the SAME batch (slug
		// resolves in-batch), so both rows land at version 1.
		var ok = await Upsert("helpdesk",
			new { key = "root-cause", type = "ticket", title = "Find root cause", body = "x" },
			new { key = "outage", type = "incident", title = "DB down", body = "x", blockedBy = "root-cause" });
		IsErr(ok).Should().BeFalse(Text(ok));

		// A NEW constrained node without the ref is rejected.
		var bare = await Upsert("helpdesk", new { key = "outage2", type = "incident", title = "DB down again", body = "x" });
		IsErr(bare).Should().BeTrue(Text(bare));
		Text(bare).Should().Contain("must carry a blocks link", "the constraint names the link kind");
		Text(bare).Should().Contain("links.blocks", "…and the generic links door that expresses it");
		Text(bare).Should().Contain("outage2");

		// The unconstrained type needs nothing.
		IsErr(await Upsert("helpdesk", new { key = "faq", type = "ticket", title = "FAQ", body = "x" }))
			.Should().BeFalse();

		// An edit doesn't re-require the link.
		var edit = await Upsert("helpdesk", new { key = "outage", version = 1, title = "DB down (postmortem pending)" });
		IsErr(edit).Should().BeFalse(Text(edit));
	}

	// 2a. a constraint naming a link kind the project does NOT know (not builtin, not the quartet
	// trio, not declared) is rejected by the definition validator — every constraint link must be a
	// known relation kind (spec methodology-link-kinds-declared: the generic links door made every
	// KNOWN kind expressible, so the gate is "known", not the old three-slug allowlist).
	[Fact]
	public async Task Validator_ConstraintOnUnknownLink_Rejected()
	{
		var def = new
		{
			name = "bad",
			kinds = new object[]
			{
				new
				{
					kind = "support",
					workflows = SupportWorkflows(),
					linkConstraints = new object[] { new { type = "incident", link = "made_up_link" } },
				},
			},
		};
		var r = await DefUpsert(def);
		IsErr(r).Should().BeTrue(Text(r));
		Text(r).Should().Contain("is not a relation kind this project knows");
	}

	// 2b. a constraint type outside the kind's workflow blocks, and a duplicated
	// (type, link) pair, are both rejected.
	[Fact]
	public async Task Validator_ConstraintTypeUnknown_And_DuplicatePair_Rejected()
	{
		object Def(params object[] constraints) => new
		{
			name = "bad",
			kinds = new object[] { new { kind = "support", workflows = SupportWorkflows(), linkConstraints = constraints } },
		};

		// NB: the envelope JSON-escapes apostrophes ('), so assertions avoid quoted spans.
		var unknown = await DefUpsert(Def(new { type = "meeting", link = "blocks" }));
		IsErr(unknown).Should().BeTrue(Text(unknown));
		Text(unknown).Should().Contain("workflow blocks");
		Text(unknown).Should().Contain("meeting");
		Text(unknown).Should().Contain("ticket|incident");

		var dup = await DefUpsert(Def(
			new { type = "incident", link = "blocks" },
			new { type = "incident", link = "blocks" }));
		IsErr(dup).Should().BeTrue(Text(dup));
		Text(dup).Should().Contain("duplicate link constraint");
	}

	// 2c. link-kind and tag-axis dictionary hygiene: builtin collision, duplicate slugs,
	// duplicate axes.
	[Fact]
	public async Task Validator_LinkKindCollisionAndDuplicates_TagAxisDuplicates_Rejected()
	{
		object Def(object[]? linkKinds = null, object[]? tagAxes = null) => new
		{
			name = "bad",
			kinds = new object[] { new { kind = "support", workflows = SupportWorkflows() } },
			linkKinds = linkKinds ?? [],
			tagAxes = tagAxes ?? [],
		};

		var collide = await DefUpsert(Def(linkKinds: [new { slug = "relates_to" }]));
		IsErr(collide).Should().BeTrue(Text(collide));
		Text(collide).Should().Contain("collides with a builtin relation kind");

		// NB: the envelope JSON-escapes apostrophes ('), so assertions avoid quoted spans.
		var dupKind = await DefUpsert(Def(linkKinds: [new { slug = "escalates" }, new { slug = "escalates" }]));
		IsErr(dupKind).Should().BeTrue(Text(dupKind));
		Text(dupKind).Should().Contain("link kind");
		Text(dupKind).Should().Contain("escalates");
		Text(dupKind).Should().Contain("is declared more than once");

		var dupAxis = await DefUpsert(Def(tagAxes: [new { @namespace = "severity" }, new { @namespace = "severity" }]));
		IsErr(dupAxis).Should().BeTrue(Text(dupAxis));
		Text(dupAxis).Should().Contain("tag axis");
		Text(dupAxis).Should().Contain("severity");
		Text(dupAxis).Should().Contain("is declared more than once");
	}

	// ── Part B: link kinds ───────────────────────────────────────────────────

	// 3. neutral builtin kinds and definition-declared kinds are accepted by
	// relations_create and round-trip via relations_list; an undeclared kind is rejected
	// listing the project's whole vocabulary.
	[Fact]
	public async Task Relations_NeutralAndDeclaredKindsAccepted_UnknownListsVocabulary()
	{
		await Define(FullDefinition());
		await CreateBoard("helpdesk");
		IsErr(await Upsert("helpdesk",
			new { key = "printer", type = "ticket", title = "Printer", body = "x" },
			new { key = "toner", type = "ticket", title = "Toner", body = "x" })).Should().BeFalse();

		var neutral = await Call("relations_create", new { projectKey = ProjectKey, kind = "relates_to", fromNodeId = "printer", toNodeId = "toner" });
		IsErr(neutral).Should().BeFalse(Text(neutral));

		var declared = await Call("relations_create", new { projectKey = ProjectKey, kind = "escalates", fromNodeId = "printer", toNodeId = "toner" });
		IsErr(declared).Should().BeFalse(Text(declared));

		var list = await Call("relations_list", new { projectKey = ProjectKey, nodeId = "printer" });
		IsErr(list).Should().BeFalse(Text(list));
		Text(list).Should().Contain("relates_to");
		Text(list).Should().Contain("escalates");

		// NB: the envelope JSON-escapes apostrophes ('), so assertions avoid quoted spans.
		var unknown = await Call("relations_create", new { projectKey = ProjectKey, kind = "banana", fromNodeId = "printer", toNodeId = "toner" });
		IsErr(unknown).Should().BeTrue(Text(unknown));
		Text(unknown).Should().Contain("invalid relation kind");
		Text(unknown).Should().Contain("banana");
		Text(unknown).Should().Contain("task_spec"); // builtin process…
		Text(unknown).Should().Contain("relates_to"); // …builtin neutral…
		Text(unknown).Should().Contain("escalates"); // …and the declared kind
	}

	// 4. a neutral edge carries NO process effects: moving one end to a terminal status
	// leaves the other end untouched and the edge active (unlike issue_task/blocks, whose
	// effects key on their specific kinds).
	[Fact]
	public async Task Relations_NeutralKind_TriggersNoProcessEffects()
	{
		await Call("tasks_board_create", new { projectKey = ProjectKey, board = "scratch" }); // simple
		IsErr(await Upsert("scratch",
			new { key = "a", status = "Todo", title = "A", body = "x" },
			new { key = "b", status = "Todo", title = "B", body = "x" })).Should().BeFalse();
		IsErr(await Call("relations_create", new { projectKey = ProjectKey, kind = "relates_to", fromNodeId = "a", toNodeId = "b" }))
			.Should().BeFalse();

		IsErr(await Upsert("scratch", new { key = "a", version = 1, status = "Done" })).Should().BeFalse();

		var board = await Call("tasks_search", new { projectKey = ProjectKey, board = "scratch" });
		Text(board).Should().Contain("\"key\":\"b\"");
		Text(board).Should().Contain("\"status\":\"Todo\"", "the related node is untouched by a's Done");

		var list = await Call("relations_list", new { projectKey = ProjectKey, nodeId = "b" });
		Text(list).Should().Contain("relates_to");
		Text(list).Should().NotContain("closedAt", "the neutral edge stays active — no Done effect closes it");
	}

	// ── Part C: tag axes ─────────────────────────────────────────────────────

	// 5. with axes declared, a tag in a declared namespace passes; an undeclared namespace
	// and a bare tag are both rejected naming the axes (same posture as the catalog's
	// enforced mode — one rule).
	[Fact]
	public async Task TagAxes_Declared_EnforcedOnDefinitionBoard()
	{
		await Define(FullDefinition());
		await CreateBoard("helpdesk");

		var ok = await Upsert("helpdesk", new
		{
			key = "outage",
			type = "ticket",
			title = "Outage",
			body = "x",
			tags = new[] { "severity:high", "channel:email" },
		});
		IsErr(ok).Should().BeFalse(Text(ok));
		Text(await Call("tasks_search", new { projectKey = ProjectKey, board = "helpdesk" }))
			.Should().Contain("severity:high");

		var badNs = await Upsert("helpdesk", new
		{
			key = "low",
			type = "ticket",
			title = "Low",
			body = "x",
			tags = new[] { "priority:low" },
		});
		// NB: the envelope JSON-escapes apostrophes ('), so assertions avoid quoted spans.
		IsErr(badNs).Should().BeTrue(Text(badNs));
		Text(badNs).Should().Contain("unknown tag namespace");
		Text(badNs).Should().Contain("priority");
		Text(badNs).Should().Contain("severity|channel", "the rejection names the declared axes");

		var bare = await Upsert("helpdesk", new
		{
			key = "urgent",
			type = "ticket",
			title = "Urgent",
			body = "x",
			tags = new[] { "urgent" },
		});
		IsErr(bare).Should().BeTrue(Text(bare));
		Text(bare).Should().Contain("namespace:value", "a bare tag is rejected in enforced mode");
		Text(bare).Should().Contain("severity|channel");
	}

	// 6. no axes declared → free-form tags on a definition-resolved board (the wave-1.2
	// posture, unchanged): any namespace passes, a bare word files under `tag:`.
	[Fact]
	public async Task TagAxes_None_DefinitionBoardStaysFreeForm()
	{
		await Define(PlainDefinition());
		await CreateBoard("helpdesk");

		var ok = await Upsert("helpdesk", new
		{
			key = "misc",
			type = "ticket",
			title = "Misc",
			body = "x",
			tags = new[] { "anyns:val", "urgent" },
		});
		IsErr(ok).Should().BeFalse(Text(ok));

		var board = Text(await Call("tasks_search", new { projectKey = ProjectKey, board = "helpdesk" }));
		board.Should().Contain("anyns:val");
		board.Should().Contain("tag:urgent", "a bare word files under the default namespace");
	}

	// ── round-trip ───────────────────────────────────────────────────────────

	// 7. template_upsert with all three primitives → template_get returns them intact
	// (wire shapes: kinds[].linkConstraints[{type,link}], linkKinds[{slug,description}],
	// tagAxes[{namespace,description}]).
	[Fact]
	public async Task RoundTrip_AllThreePrimitives_SurviveTemplateGet()
	{
		var up = await Call("tasks_methodology_template_upsert", new { projectKey = ProjectKey, key = Tmpl, definition = FullDefinition(), version = 0 });
		IsErr(up).Should().BeFalse(Text(up));

		var got = Parse(await Call("tasks_methodology_template_get", new { projectKey = ProjectKey, key = Tmpl }));
		got.GetProperty("found").GetBoolean().Should().BeTrue();

		var kind = got.GetProperty("kinds").EnumerateArray().Single();
		var constraint = kind.GetProperty("linkConstraints").EnumerateArray().Single();
		constraint.GetProperty("type").GetString().Should().Be("incident");
		constraint.GetProperty("link").GetString().Should().Be("blocks");

		var linkKind = got.GetProperty("linkKinds").EnumerateArray().Single();
		linkKind.GetProperty("slug").GetString().Should().Be("escalates");
		linkKind.GetProperty("description").GetString().Should().Be("support escalation edge");

		var axes = got.GetProperty("tagAxes").EnumerateArray().ToList();
		axes.Select(a => a.GetProperty("namespace").GetString()).Should().Equal("severity", "channel");
		axes[1].GetProperty("description").GetString().Should().Be("intake channel");
	}
}
