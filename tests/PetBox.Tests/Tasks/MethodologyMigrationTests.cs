using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace PetBox.Tests.Tasks;

// Engine wave 3.2 — declarative schema migration (spec primitives-schema-migration),
// exercised end-to-end over MCP: changing a methodology INSTANCE's rules is validated
// against LIVE NODES; an incompatibility no `migration` mapping covers REJECTS the whole
// rules_upsert naming the offenders (nothing written), and a mapped one rewrites the node as
// a new temporal revision (a valid value is never rewritten — declarative repair, not bulk
// rename). Also the two seams: first rules overriding a preset kind with live boards, and a
// kind DROPPED from the rules (its boards fall back to presets).
//
// The custom kind under test: `support`, one block shared by ticket|incident
// (New → Open → Resolved).
public sealed class MethodologyMigrationTests : IClassFixture<MethodologyMigrationFixture>, IAsyncLifetime
{
	const string ProjectKey = "mmig";
	const string Inst = "helpdesk";
	const string Tmpl = "support-tmpl";

	readonly MethodologyMigrationFixture _fx;
	readonly McpClient _mcp;

	public MethodologyMigrationTests(MethodologyMigrationFixture fx)
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

	// Live rules edit (with optional migration) on the helpdesk instance.
	Task<CallToolResult> DefUpsert(object definition, long version = 0, object? migration = null) =>
		Call("tasks_methodology_rules_upsert", migration is null
			? new { projectKey = ProjectKey, name = Inst, definition, version }
			: new { projectKey = ProjectKey, name = Inst, definition, version, migration });

	Task<CallToolResult> DefGet() =>
		Call("tasks_methodology_rules_get", new { projectKey = ProjectKey, name = Inst });

	async Task<long> RulesVersion()
	{
		var g = Parse(await DefGet());
		return g.GetProperty("version").GetInt64();
	}

	// Bootstrap: store template + create instance (board named "helpdesk" for single kind).
	async Task EnsureInstance(object definition)
	{
		var up = await Call("tasks_methodology_template_upsert", new { projectKey = ProjectKey, key = Tmpl, definition, version = 0 });
		IsErr(up).Should().BeFalse(Text(up));
		var cr = await Call("tasks_methodology_create", new { projectKey = ProjectKey, name = Inst, source = "template", sourceKey = Tmpl });
		IsErr(cr).Should().BeFalse(Text(cr));
	}

	Task<CallToolResult> Upsert(string board, params object[] nodes) =>
		Call("tasks_upsert", new { projectKey = ProjectKey, board, nodes = Nodes(nodes) });

	async Task CreateBoard(string board, string? kind = "support")
	{
		var r = await Call("tasks_board_create", new { projectKey = ProjectKey, board, kind, methodologyInstance = Inst });
		IsErr(r).Should().BeFalse(Text(r));
	}

	// The full node view of tasks_node_get (status/type/version assertions).
	async Task<JsonElement> NodeGet(string board, string node)
	{
		var r = await Call("tasks_node_get", new { projectKey = ProjectKey, board, node });
		IsErr(r).Should().BeFalse(Text(r));
		return Parse(r).GetProperty("node");
	}

	// The `support` definition; parameterized so v2 variants (status/type rename) build the
	// same shape with different vocab.
	static object SupportDefinition(string openStatus = "Open", string ticketType = "ticket") => new
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
						types = new[] { ticketType, "incident" },
						statuses = new object[]
						{
							new { slug = "New", kind = "open" },
							new { slug = openStatus, kind = "open" },
							new { slug = "Resolved", kind = "terminalok" },
						},
						transitions = new object[]
						{
							new { from = "New", to = openStatus },
							new { from = openStatus, to = "Resolved" },
						},
					},
				},
			},
		},
	};

	// A definition WITHOUT the support kind — the "kind dropped" variant (its boards then
	// resolve via the presets: unknown slug → simple).
	static object FlowOnlyDefinition() => new
	{
		name = "flow-only",
		kinds = new object[]
		{
			new
			{
				kind = "flow",
				workflows = new object[]
				{
					new
					{
						types = new[] { "job" },
						statuses = new object[]
						{
							new { slug = "Todo", kind = "open" },
							new { slug = "Done", kind = "terminalok" },
						},
						transitions = new object[] { new { from = "Todo", to = "Done" } },
					},
				},
			},
		},
	};

	// A first-time definition that OVERRIDES the preset `simple` kind with a different
	// status vocabulary.
	static object SimpleOverrideDefinition() => new
	{
		name = "strict-simple",
		kinds = new object[]
		{
			new
			{
				kind = "simple",
				quickAddAllowed = true,
				workflows = new object[]
				{
					new
					{
						types = new[] { "task" },
						statuses = new object[]
						{
							new { slug = "Open", kind = "open" },
							new { slug = "Closed", kind = "terminalok" },
						},
						transitions = new object[] { new { from = "Open", to = "Closed" } },
					},
				},
			},
		},
	};

	// Instance helpdesk (board = helpdesk) with a node in New and a node moved to Open.
	async Task SeedSupportBoard()
	{
		await EnsureInstance(SupportDefinition());
		IsErr(await Upsert("helpdesk",
			new { key = "t-new", type = "ticket", title = "Still new", body = "x" },
			new { key = "t-open", type = "ticket", title = "In flight", body = "x" })).Should().BeFalse();
		IsErr(await Upsert("helpdesk", new { key = "t-open", version = 1, status = "Open" })).Should().BeFalse();
	}

	// ── status rename ────────────────────────────────────────────────────────

	// 1. renaming a status WITHOUT a migration is rejected naming board/node/status; the
	// stored definition and the node are untouched.
	[Fact]
	public async Task StatusRename_WithoutMigration_RejectedNamingOffenders()
	{
		await SeedSupportBoard();
		var ver = await RulesVersion();

		// NB: the envelope JSON-escapes apostrophes ('), so assertions avoid quoted spans.
		var r = await DefUpsert(SupportDefinition(openStatus: "Active"), version: ver);
		IsErr(r).Should().BeTrue(Text(r));
		Text(r).Should().Contain("incompatible with live nodes");
		Text(r).Should().Contain("helpdesk");
		Text(r).Should().Contain("t-open");
		Text(r).Should().Contain("Open");
		Text(r).Should().Contain("migration");

		// Nothing was written: rules_get still answers the seed version, the node still reads Open.
		var got = Parse(await DefGet());
		got.GetProperty("version").GetInt64().Should().Be(ver);
		Text(await DefGet()).Should().NotContain("Active");
		var node = await NodeGet("helpdesk", "t-open");
		node.GetProperty("status").GetString().Should().Be("Open");
		node.GetProperty("version").GetInt64().Should().Be(2, "created at 1, transitioned at 2 — the rejected define adds nothing");
	}

	// 2. the same rename WITH a status mapping is applied: the invalid node is rewritten,
	// the valid one is untouched (version unchanged), and the ack counts the rewrites.
	[Fact]
	public async Task StatusRename_WithMigration_RewritesOnlyInvalidNodes()
	{
		await SeedSupportBoard();
		var ver = await RulesVersion();

		var r = await DefUpsert(SupportDefinition(openStatus: "Active"), version: ver,
			migration: new object[] { new { kind = "support", statuses = new object[] { new { from = "Open", to = "Active" } } } });
		IsErr(r).Should().BeFalse(Text(r));
		var ack = Parse(r);
		ack.GetProperty("version").GetInt64().Should().Be(ver + 1);
		ack.GetProperty("changed").GetBoolean().Should().BeTrue();
		ack.GetProperty("migrated").GetInt64().Should().Be(1, "only t-open was invalid");

		var open = await NodeGet("helpdesk", "t-open");
		open.GetProperty("status").GetString().Should().Be("Active");

		// The mapping never touches a valid value: t-new stays New at its original version.
		var fresh = await NodeGet("helpdesk", "t-new");
		fresh.GetProperty("status").GetString().Should().Be("New");
		fresh.GetProperty("version").GetInt64().Should().Be(1, "a node valid under the new resolution is never rewritten");
	}

	// 3. a change that strands NO live node needs no migration and reports migrated=0 (the
	// check judges live values, not the vocabulary diff).
	[Fact]
	public async Task CompatibleChange_NoNodeStranded_AppliedWithZeroMigrated()
	{
		await EnsureInstance(SupportDefinition());
		IsErr(await Upsert("helpdesk", new { key = "t-new", type = "ticket", title = "Still new", body = "x" })).Should().BeFalse();
		var ver = await RulesVersion();

		// Rename Open→Active while nothing sits in Open: compatible as-is.
		var r = await DefUpsert(SupportDefinition(openStatus: "Active"), version: ver);
		IsErr(r).Should().BeFalse(Text(r));
		var ack = Parse(r);
		ack.GetProperty("changed").GetBoolean().Should().BeTrue();
		ack.GetProperty("migrated").GetInt64().Should().Be(0);
	}

	// ── type rename ──────────────────────────────────────────────────────────

	// 4. a type rename maps the node's type; combined with a status rename both mappings
	// apply to one node (type first, then the mapped type's workflow judges the status).
	[Fact]
	public async Task TypeRename_CombinedWithStatusMapping_RewritesBoth()
	{
		await SeedSupportBoard();
		var ver = await RulesVersion();

		var r = await DefUpsert(SupportDefinition(openStatus: "Active", ticketType: "request"), version: ver,
			migration: new object[]
			{
				new
				{
					kind = "support",
					types = new object[] { new { from = "ticket", to = "request" } },
					statuses = new object[] { new { from = "Open", to = "Active" } },
				},
			});
		IsErr(r).Should().BeFalse(Text(r));
		Parse(r).GetProperty("migrated").GetInt64().Should().Be(2, "both ticket nodes carry an invalid type");

		var fresh = await NodeGet("helpdesk", "t-new");
		fresh.GetProperty("type").GetString().Should().Be("request");
		fresh.GetProperty("status").GetString().Should().Be("New", "the status was already valid — only the type is repaired");

		var open = await NodeGet("helpdesk", "t-open");
		open.GetProperty("type").GetString().Should().Be("request");
		open.GetProperty("status").GetString().Should().Be("Active");
	}

	// ── migration-document validation ────────────────────────────────────────

	// 5a. a mapping whose `to` doesn't exist under the new resolution is rejected before
	// anything is written.
	[Fact]
	public async Task Migration_UnknownTo_Rejected()
	{
		await SeedSupportBoard();
		var ver = await RulesVersion();

		var r = await DefUpsert(SupportDefinition(openStatus: "Active"), version: ver,
			migration: new object[] { new { kind = "support", statuses = new object[] { new { from = "Open", to = "Banana" } } } });
		IsErr(r).Should().BeTrue(Text(r));
		Text(r).Should().Contain("Banana");
		Text(r).Should().Contain("is not a status of the new resolution");
		Parse(await DefGet()).GetProperty("version").GetInt64().Should().Be(ver, "nothing was written");
	}

	// 5b. mapping the same `from` twice within a kind is ambiguous and rejected.
	[Fact]
	public async Task Migration_DuplicateFrom_Rejected()
	{
		await SeedSupportBoard();
		var ver = await RulesVersion();

		var r = await DefUpsert(SupportDefinition(openStatus: "Active"), version: ver,
			migration: new object[]
			{
				new
				{
					kind = "support",
					statuses = new object[] { new { from = "Open", to = "Active" }, new { from = "open", to = "New" } },
				},
			});
		IsErr(r).Should().BeTrue(Text(r));
		Text(r).Should().Contain("mapped more than once");
		Parse(await DefGet()).GetProperty("version").GetInt64().Should().Be(ver, "nothing was written");
	}

	// ── preset seams (on instance rules) ─────────────────────────────────────

	// 6. rules that override the preset `simple` kind must not silently orphan live simple
	// boards on the instance: unmapped → rejected; mapped → applied and nodes rewritten.
	[Fact]
	public async Task FirstRules_OverridingPresetKind_GuardsLiveNodes()
	{
		// Start from builtin simple (board named after instance).
		var cr = await Call("tasks_methodology_create", new { projectKey = ProjectKey, name = Inst, source = "builtin", sourceKey = "simple" });
		IsErr(cr).Should().BeFalse(Text(cr));
		IsErr(await Upsert(Inst,
			new { key = "a", title = "A", body = "x" },
			new { key = "b", title = "B", body = "x" })).Should().BeFalse();
		IsErr(await Upsert(Inst, new { key = "b", version = 1, status = "InProgress" })).Should().BeFalse();
		var ver = await RulesVersion();

		var bare = await DefUpsert(SimpleOverrideDefinition(), version: ver);
		IsErr(bare).Should().BeTrue(Text(bare));
		Text(bare).Should().Contain(Inst);
		Text(bare).Should().Contain("Todo");
		Parse(await DefGet()).GetProperty("version").GetInt64().Should().Be(ver, "the rejected define stored nothing");

		var mapped = await DefUpsert(SimpleOverrideDefinition(), version: ver, migration: new object[]
		{
			new
			{
				kind = "simple",
				statuses = new object[] { new { from = "Todo", to = "Open" }, new { from = "InProgress", to = "Open" } },
			},
		});
		IsErr(mapped).Should().BeFalse(Text(mapped));
		Parse(mapped).GetProperty("migrated").GetInt64().Should().Be(2);

		(await NodeGet(Inst, "a")).GetProperty("status").GetString().Should().Be("Open");
		(await NodeGet(Inst, "b")).GetProperty("status").GetString().Should().Be("Open");
		Parse(await DefGet()).GetProperty("definitionName").GetString().Should().Be("strict-simple");
	}

	// 7. DROPPING a kind whose boards still exist: those boards fall back to the presets
	// (unknown slug → simple), so their nodes hit the same rejection/mapping machinery —
	// unmapped is rejected, a mapping onto the preset vocabulary repairs them.
	[Fact]
	public async Task KindDropped_BoardsFallBackToPresets_SameMachineryCatches()
	{
		await EnsureInstance(SupportDefinition());
		IsErr(await Upsert("helpdesk", new { key = "tk", type = "ticket", title = "Ticket", body = "x" })).Should().BeFalse();
		var ver = await RulesVersion();

		// Without a mapping: `New` is unknown to the simple preset the board now resolves to.
		var dropped = await DefUpsert(FlowOnlyDefinition(), version: ver);
		IsErr(dropped).Should().BeTrue(Text(dropped));
		Text(dropped).Should().Contain("helpdesk");
		Text(dropped).Should().Contain("tk");
		Text(dropped).Should().Contain("New");
		Parse(await DefGet()).GetProperty("definitionName").GetString().Should().Be("support-process", "nothing was written");

		// The migration entry keys on the BOARD's kind slug (the dropped `support`), mapping
		// onto the preset vocabulary it now resolves to.
		var mapped = await DefUpsert(FlowOnlyDefinition(), version: ver, migration: new object[]
		{
			new { kind = "support", statuses = new object[] { new { from = "New", to = "Todo" } } },
		});
		IsErr(mapped).Should().BeFalse(Text(mapped));
		Parse(mapped).GetProperty("migrated").GetInt64().Should().Be(1);

		var node = await NodeGet("helpdesk", "tk");
		node.GetProperty("status").GetString().Should().Be("Todo");
		Parse(await DefGet()).GetProperty("definitionName").GetString().Should().Be("flow-only");
	}
}
