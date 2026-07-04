using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace PetBox.Tests.Tasks;

// The user-defined methodology DEFINITION surface (engine wave 1.1: storage + validation
// + the tasks.methodology_def_* verbs), exercised end-to-end over MCP. The definition is
// pure data in this slice — live boards still run the built-in presets —
// so these tests cover the document round-trip, the optimistic-concurrency contract and
// the whole-document integrity validation, not FSM behavior.
public sealed class MethodologyDefinitionTests : IClassFixture<MethodologyDefinitionFixture>, IAsyncLifetime
{
	const string ProjectKey = "mdef";

	readonly MethodologyDefinitionFixture _fx;
	readonly McpClient _mcp;

	public MethodologyDefinitionTests(MethodologyDefinitionFixture fx)
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

	Task<CallToolResult> Upsert(object definition, long version = 0) =>
		Call("tasks_methodology_def_upsert", new { projectKey = ProjectKey, definition, version });

	Task<CallToolResult> Get() =>
		Call("tasks_methodology_def_get", new { projectKey = ProjectKey });

	static string Text(CallToolResult r) =>
		r.Content.OfType<TextContentBlock>().First().Text;

	// Errors arrive as the central envelope {"error":{...}} on the isError channel (IsError=true).
	static bool IsErr(CallToolResult r) =>
		r.IsError == true ||
		(r.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text?.Contains("\"error\"") ?? false);

	static JsonElement Parse(CallToolResult r) =>
		JsonDocument.Parse(Text(r)).RootElement.Clone();

	// A valid definition: two kinds; `flow` hosts TWO workflow blocks (task-vs-epic FSMs,
	// one gated transition carrying a preconditionArtifact), `triage` hosts one.
	static object ValidDefinition(string epicDoneName = "Shipped") => new
	{
		name = "acme-process",
		kinds = new object[]
		{
			new
			{
				kind = "flow",
				quickAddAllowed = false,
				workflows = new object[]
				{
					new
					{
						types = new[] { "task", "bug" },
						statuses = new object[]
						{
							new { slug = "Todo", name = "Todo", kind = "open" },
							new { slug = "Doing", name = "Doing", kind = "open" },
							new { slug = "Done", name = "Done", kind = "terminalok" },
						},
						transitions = new object[]
						{
							new { from = "Todo", to = "Doing" },
							new { from = "Doing", to = "Done", requiresApproval = true, preconditionArtifact = "review_notes" },
						},
					},
					new
					{
						types = new[] { "epic" },
						statuses = new object[]
						{
							new { slug = "Draft", kind = "open" },
							new { slug = "Shipped", name = epicDoneName, kind = "terminalok" },
							new { slug = "Dropped", kind = "terminalcancel" },
						},
						transitions = new object[]
						{
							new { from = "Draft", to = "Shipped" },
							new { from = "Draft", to = "Dropped", requiresReason = true },
						},
					},
				},
			},
			new
			{
				kind = "triage",
				workflows = new object[]
				{
					new
					{
						types = new[] { "ticket" },
						statuses = new object[]
						{
							new { slug = "New", kind = "open" },
							new { slug = "Closed", kind = "terminalcancel" },
						},
						transitions = new object[] { new { from = "New", to = "Closed" } },
					},
				},
			},
		},
	};

	// ── scenarios ────────────────────────────────────────────────────────────

	// 1. a valid custom methodology (2 kinds, one with 2 workflow blocks) round-trips
	// intact through upsert → get, including the preconditionArtifact field.
	[Fact]
	public async Task Define_Valid_RoundTripsIntact()
	{
		var up = await Upsert(ValidDefinition());
		IsErr(up).Should().BeFalse(Text(up));
		var ack = Parse(up);
		ack.GetProperty("version").GetInt64().Should().Be(1);
		ack.GetProperty("changed").GetBoolean().Should().BeTrue();

		var got = Parse(await Get());
		got.GetProperty("defined").GetBoolean().Should().BeTrue();
		got.GetProperty("name").GetString().Should().Be("acme-process");
		got.GetProperty("version").GetInt64().Should().Be(1);

		var kinds = got.GetProperty("kinds").EnumerateArray().ToList();
		kinds.Select(k => k.GetProperty("kind").GetString()).Should().Equal("flow", "triage");
		kinds[0].GetProperty("quickAddAllowed").GetBoolean().Should().BeFalse();
		kinds[1].GetProperty("quickAddAllowed").GetBoolean().Should().BeTrue("omitted = the default");

		var blocks = kinds[0].GetProperty("workflows").EnumerateArray().ToList();
		blocks.Should().HaveCount(2, "one kind may host several state machines");
		blocks[0].GetProperty("types").EnumerateArray().Select(t => t.GetString()).Should().Equal("task", "bug");
		blocks[0].GetProperty("initial").GetString().Should().Be("Todo", "statuses[0] is the initial");
		blocks[0].GetProperty("statuses").EnumerateArray()
			.Single(s => s.GetProperty("slug").GetString() == "Done")
			.GetProperty("kind").GetString().Should().Be("terminalok");
		var gated = blocks[0].GetProperty("transitions").EnumerateArray()
			.Single(t => t.GetProperty("to").GetString() == "Done");
		gated.GetProperty("requiresApproval").GetBoolean().Should().BeTrue();
		gated.GetProperty("preconditionArtifact").GetString().Should().Be("review_notes");

		blocks[1].GetProperty("types").EnumerateArray().Select(t => t.GetString()).Should().Equal("epic");
		blocks[1].GetProperty("statuses").EnumerateArray()
			.Single(s => s.GetProperty("slug").GetString() == "Draft")
			.GetProperty("name").GetString().Should().Be("Draft", "omitted status name defaults to the slug");
	}

	// 2. optimistic concurrency: two writers both submitting baseline 0 — the second is a
	// clear conflict naming the current version, not a clobber.
	[Fact]
	public async Task Define_SameBaselineTwice_Conflict()
	{
		IsErr(await Upsert(ValidDefinition())).Should().BeFalse();

		var second = await Upsert(ValidDefinition(epicDoneName: "Delivered"), version: 0);
		IsErr(second).Should().BeTrue(Text(second));
		Text(second).Should().Contain("is stale");
		Text(second).Should().Contain("current version is 1");
	}

	// 3. an edit against the correct baseline lands as a new revision; get shows the new
	// content and the bumped version. An identical resubmit then collapses to a no-op.
	[Fact]
	public async Task Update_WithCorrectBaseline_NewRevision()
	{
		IsErr(await Upsert(ValidDefinition())).Should().BeFalse();

		var edit = await Upsert(ValidDefinition(epicDoneName: "Delivered"), version: 1);
		IsErr(edit).Should().BeFalse(Text(edit));
		var ack = Parse(edit);
		ack.GetProperty("version").GetInt64().Should().Be(2);
		ack.GetProperty("changed").GetBoolean().Should().BeTrue();

		var got = Parse(await Get());
		got.GetProperty("version").GetInt64().Should().Be(2);
		Text(await Get()).Should().Contain("Delivered");

		var resubmit = Parse(await Upsert(ValidDefinition(epicDoneName: "Delivered"), version: 2));
		resubmit.GetProperty("changed").GetBoolean().Should().BeFalse("an identical resubmit is a no-op");
		resubmit.GetProperty("version").GetInt64().Should().Be(2);
	}

	// 3b. WATERMARK: test 3 already shows the `version` from methodology_def_get is the valid next
	// baseline. A baseline ABOVE the project's cursor is a FutureBaseline conflict whose message
	// names BOTH version spaces — the classic symptom of a cursor carried from another project.
	[Fact]
	public async Task Define_FutureBaseline_NamesBothVersionSpaces()
	{
		IsErr(await Upsert(ValidDefinition())).Should().BeFalse(); // -> version 1, cursor 1

		var future = await Upsert(ValidDefinition(epicDoneName: "Delivered"), version: 99);
		IsErr(future).Should().BeTrue(Text(future));
		// The wire JSON escapes the apostrophe in "project's" as ' — assert around it.
		Text(future).Should().Contain("ahead of this project").And.Contain("cursor 1");
	}

	// 3c. rule-6 alignment: an IDENTICAL definition on a STALE baseline is the same conflict a real
	// change would raise — the service no longer treats byte-equality as an unconditional no-op; the
	// store validates the baseline first (SamePayload runs AFTER baseline validation).
	[Fact]
	public async Task Define_IdenticalDefinition_StaleBaseline_Conflicts()
	{
		IsErr(await Upsert(ValidDefinition())).Should().BeFalse();                                       // -> version 1
		IsErr(await Upsert(ValidDefinition(epicDoneName: "Delivered"), version: 1)).Should().BeFalse();  // -> version 2

		// Resubmit the CURRENT definition byte-for-byte, but with the superseded baseline 1.
		var stale = await Upsert(ValidDefinition(epicDoneName: "Delivered"), version: 1);
		IsErr(stale).Should().BeTrue(Text(stale));
		Text(stale).Should().Contain("is stale");
	}

	// 4a. a transition referencing a status outside its block is rejected, naming the edge.
	[Fact]
	public async Task Define_TransitionToUnknownStatus_Rejected()
	{
		var r = await Upsert(new
		{
			name = "bad",
			kinds = new object[]
			{
				new
				{
					kind = "flow",
					workflows = new object[]
					{
						new
						{
							types = new[] { "task" },
							statuses = new object[] { new { slug = "Todo", kind = "open" } },
							transitions = new object[] { new { from = "Todo", to = "Banana" } },
						},
					},
				},
			},
		});
		IsErr(r).Should().BeTrue(Text(r));
		Text(r).Should().Contain("does not reference a status");
		Text(r).Should().Contain("Banana");
	}

	// 4b. a workflow block with no statuses is rejected.
	[Fact]
	public async Task Define_EmptyStatuses_Rejected()
	{
		var r = await Upsert(new
		{
			name = "bad",
			kinds = new object[]
			{
				new
				{
					kind = "flow",
					workflows = new object[]
					{
						new { types = new[] { "task" }, statuses = Array.Empty<object>(), transitions = Array.Empty<object>() },
					},
				},
			},
		});
		IsErr(r).Should().BeTrue(Text(r));
		Text(r).Should().Contain("at least one status");
	}

	// 4c. the same type in two blocks of one kind is rejected — a type must resolve to
	// exactly one state machine.
	[Fact]
	public async Task Define_DuplicateTypeAcrossBlocks_Rejected()
	{
		object Block() => new
		{
			types = new[] { "task" },
			statuses = new object[] { new { slug = "Todo", kind = "open" } },
			transitions = Array.Empty<object>(),
		};
		var r = await Upsert(new
		{
			name = "bad",
			kinds = new object[] { new { kind = "flow", workflows = new object[] { Block(), Block() } } },
		});
		IsErr(r).Should().BeTrue(Text(r));
		Text(r).Should().Contain("more than one workflow block");
	}

	// 4d. a kind slug outside the canonical spec is rejected (verbatim — no silent lowering).
	[Fact]
	public async Task Define_BadKindSlug_Rejected()
	{
		var r = await Upsert(new
		{
			name = "bad",
			kinds = new object[]
			{
				new
				{
					kind = "Big Kind",
					workflows = new object[]
					{
						new
						{
							types = new[] { "task" },
							statuses = new object[] { new { slug = "Todo", kind = "open" } },
							transitions = Array.Empty<object>(),
						},
					},
				},
			},
		});
		IsErr(r).Should().BeTrue(Text(r));
		Text(r).Should().Contain("is not a valid slug");
	}

	// 5. no definition stored → the structured "not defined" answer (built-in preset), not
	// an error and not an empty object.
	[Fact]
	public async Task Get_NoDefinition_StructuredNotDefined()
	{
		var r = await Get();
		IsErr(r).Should().BeFalse(Text(r));
		var got = Parse(r);
		got.GetProperty("defined").GetBoolean().Should().BeFalse();
		got.GetProperty("preset").GetString().Should().Be("builtin-presets");
	}
}
