using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using PetBox.Tests.Tasks;

namespace PetBox.Tests.Mcp;

// Card work/unknown-param-silently-ignored-breaks-renames-quietly. LIVE INCIDENT 27.07.2026:
// tasks_search called with the just-retired `under` (renamed to `underNode`, e328c7cf) did not
// error — it silently dropped the filter and returned the whole board instead of the subtree.
// A raw HTTP smoke, bypassing every MCP client/SDK, proved the drop happens SERVER-side (the
// framework's per-parameter argument lookup, not a client snapshot-schema artifact): sending
// {"board":"work","zzz_nonexistent":1,"bodyLen":0} still returned success with the field ignored.
//
// McpUnknownParameterFilter closes that gap. These tests drive it through the real MCP wire (the
// McpClient sends whatever dictionary it is given — like the raw-HTTP smoke, nothing strips an
// unknown key before the server sees it).
public sealed class UnknownParameterFilterFixture() : TasksMcpFixture("unkp", "UnknownParam");

public sealed class UnknownParameterFilterTests : IClassFixture<UnknownParameterFilterFixture>, IAsyncLifetime
{
	readonly UnknownParameterFilterFixture _fx;
	public UnknownParameterFilterTests(UnknownParameterFilterFixture fx) => _fx = fx;

	// The same proven pattern as every other TasksMcpFixture consumer (MethodologyDefinitionTests
	// et al.): the shared per-class host means per-test DATA isolation has to be restored
	// explicitly. This class used to be the one consumer that didn't — AllValidParameters_
	// PassesThrough had to be patched (c70cd815) to provision its own board rather than rely on
	// BatchVerbWithNestedObjects_IsNotBroken having run first and left one behind, which was the
	// symptom of exactly this gap. ResetAsync wipes the board catalog + per-project tasks file
	// between tests, so no test here may assume another has already run.
	public ValueTask InitializeAsync() => new(_fx.ResetAsync());

	public ValueTask DisposeAsync() => ValueTask.CompletedTask; // the fixture owns host teardown

	static async Task<McpClientTool> Tool(McpClient mcp, string name) =>
		(await mcp.ListToolsAsync()).First(t => t.Name == name);

	static string Text(CallToolResult result) =>
		string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));

	// The exact shape of the live incident: a garbage top-level key alongside otherwise-valid
	// arguments is REJECTED — never silently dropped into an unfiltered success.
	[Fact]
	public async Task UnknownTopLevelKey_IsRejected_AndNamesIt()
	{
		var tool = await Tool(_fx.Mcp, "tasks_search");
		var result = await tool.CallAsync(new Dictionary<string, object?>
		{
			["projectKey"] = _fx.ProjectKey,
			["board"] = "work",
			["zzz_nonexistent"] = 1,
			["bodyLen"] = 0,
		});

		result.IsError.Should().Be(true);
		Text(result).Should().Contain("zzz_nonexistent").And.Contain("tasks_search");
	}

	// Reuses NamespaceSuggest (the same edit-distance ranking memory_upsert.store's "did you mean
	// 'X'?" already uses) rather than a second implementation — a near-miss typo of a real
	// parameter is offered back.
	[Fact]
	public async Task UnknownTopLevelKey_SuggestsNearestKnownName_WhenClose()
	{
		var tool = await Tool(_fx.Mcp, "tasks_search");
		var result = await tool.CallAsync(new Dictionary<string, object?>
		{
			["projectKey"] = _fx.ProjectKey,
			["boad"] = "work", // one-char-off typo of `board`
		});

		result.IsError.Should().Be(true);
		// Apostrophes ride the wire '-escaped (PetBoxJsonEncoder.Relaxed keeps HTML-sensitive
		// chars escaped even in the relaxed profile) — assert on the words, not the literal quotes.
		Text(result).Should().Contain("boad").And.Contain("Did you mean").And.Contain("board");
	}

	// The boundary the card demands: a call using ONLY valid parameter names (including an
	// omitted-with-default one, and the legitimately-optional projectKey) is untouched.
	[Fact]
	public async Task AllValidParameters_PassesThrough()
	{
		// Provision the board this call searches. It used to rely on BatchVerbWithNestedObjects_
		// IsNotBroken having run FIRST and created it through the shared class fixture — an order
		// dependence that was invisible under xunit v2's ordering and started failing the moment v3
		// ordered the class differently ("task board 'work' not found in project 'unkp'"). The
		// assertion here is about the PARAMETER FILTER, not about board existence, so the board is
		// setup, not subject: create it unconditionally and let the filter be the only thing tested.
		await (await Tool(_fx.Mcp, "tasks_board_create")).CallAsync(new Dictionary<string, object?>
		{
			["projectKey"] = _fx.ProjectKey,
			["board"] = "work",
			["kind"] = "work",
		});

		var tool = await Tool(_fx.Mcp, "tasks_search");
		var result = await tool.CallAsync(new Dictionary<string, object?>
		{
			["projectKey"] = _fx.ProjectKey,
			["board"] = "work",
			["underNode"] = (string?)null,
			["bodyLen"] = 0,
		});

		result.IsError.Should().NotBe(true, Text(result));
	}

	// The boundary the card demands the other way: a batch verb whose nested array-item objects
	// (`nodes[]`, here `key`/`title`/`body`/`type`) have their OWN field names must not be mistaken
	// for unknown TOP-LEVEL parameters. Since drop-legacy-aliases the filter also walks ONE level
	// into item shapes, so this test now pins both halves at once: the item field names are checked
	// against the ITEM schema (not the top-level one) and every one of these is valid, so the call
	// passes through untouched.
	[Fact]
	public async Task BatchVerbWithNestedObjects_IsNotBroken()
	{
		await (await Tool(_fx.Mcp, "tasks_board_create")).CallAsync(new Dictionary<string, object?>
		{
			["projectKey"] = _fx.ProjectKey,
			["board"] = "work",
			["kind"] = "work",
		});

		var result = await (await Tool(_fx.Mcp, "tasks_upsert")).CallAsync(new Dictionary<string, object?>
		{
			["projectKey"] = _fx.ProjectKey,
			["board"] = "work",
			["nodes"] = new[]
			{
				new Dictionary<string, object?>
				{
					["key"] = "unkp-batch-probe",
					["title"] = "Batch probe",
					["body"] = "nested object fields must not trip the top-level guard",
					["type"] = "chore",
					["version"] = 0,
				},
			},
		});

		result.IsError.Should().NotBe(true, Text(result));
	}

	// THE CARD'S MAIN SCENARIO, previously uncovered entirely: the live incident was a RENAME
	// (`under`->`underNode`, commit e328c7cf), not a typo. NamespaceSuggest's budget discarded this
	// candidate outright (length gap 4 > budget 1, before distance was even scored) — that is the
	// root cause the card asked to be pinned, not "the hint is lost in serialization".
	[Fact]
	public async Task UnknownTopLevelKey_RenamedParameter_SuggestsNewName()
	{
		var tool = await Tool(_fx.Mcp, "tasks_search");
		var result = await tool.CallAsync(new Dictionary<string, object?>
		{
			["projectKey"] = _fx.ProjectKey,
			["board"] = "work",
			["under"] = "umbrella-mcp-surface-consistency", // the exact retired param from the incident
		});

		result.IsError.Should().Be(true);
		Text(result).Should().Contain("under").And.Contain("Did you mean").And.Contain("underNode");
	}

	// A transposition (`boadr`->`board`, distance 2 under plain Levenshtein — no single-op swap)
	// is a second scenario the old NamespaceSuggest-budget path missed: equal length, so the
	// length-gap guard did not discard it, but budget 1 was still one short of distance 2.
	[Fact]
	public async Task UnknownTopLevelKey_Transposition_SuggestsNearestKnownName()
	{
		var tool = await Tool(_fx.Mcp, "tasks_search");
		var result = await tool.CallAsync(new Dictionary<string, object?>
		{
			["projectKey"] = _fx.ProjectKey,
			["boadr"] = "work",
		});

		result.IsError.Should().Be(true);
		Text(result).Should().Contain("boadr").And.Contain("Did you mean").And.Contain("board");
	}

	// The enumeration arm: a rename that shares neither prefix nor edit-distance shape with any
	// known parameter (`keys`->`nodes`, `nodeId`->`hostId` are this shape in production) gets NO
	// near-hit — but the caller still needs a way out, so the accepted-parameter list always rides
	// along. `zzz_nonexistent` is deliberately unlike anything in tasks_search's schema, so this
	// also doubles as the noise negative: a name with truly nothing near it must not be handed a
	// far-fetched "Did you mean" that would just be noise.
	[Fact]
	public async Task UnknownTopLevelKey_NoNearMatch_ListsAcceptedParametersAndSkipsTheHint()
	{
		var tool = await Tool(_fx.Mcp, "tasks_search");
		var result = await tool.CallAsync(new Dictionary<string, object?>
		{
			["projectKey"] = _fx.ProjectKey,
			["zzz_nonexistent"] = 1,
		});

		result.IsError.Should().Be(true);
		var text = Text(result);
		text.Should().Contain("Accepted parameters").And.Contain("board");
		text.Should().NotContain("Did you mean");
	}

	// ── drop-legacy-aliases: every retired alias must ERROR, never be quietly ignored ──────────
	//
	// This is the acceptance the retirement stands on. Deleting an alias from the schema is only
	// half a retirement: if the name then lands in the framework's per-parameter lookup and is
	// dropped, a caller still on the old spelling gets a SUCCESS with its argument missing — the
	// precise failure McpUnknownParameterFilter was built for, re-created by the very change meant
	// to clean the surface up. So each retired name is pinned here by name, on the real wire.

	// `includeClosed` (tasks_search) — a TOP-LEVEL alias for statusKind. The dangerous one: silently
	// ignored it would not error at all, it would narrow the result to the mode default and look
	// like a correct answer, exactly like the `under` incident above.
	[Fact]
	public async Task RetiredAlias_IncludeClosed_IsRejected_AndPointsAtStatusKind()
	{
		var tool = await Tool(_fx.Mcp, "tasks_search");
		var result = await tool.CallAsync(new Dictionary<string, object?>
		{
			["projectKey"] = _fx.ProjectKey,
			["board"] = "work",
			["includeClosed"] = true,
		});

		result.IsError.Should().Be(true);
		var text = Text(result);
		text.Should().Contain("Unknown parameter").And.Contain("includeClosed").And.Contain("tasks_search");
		// The replacement has to be reachable from the error itself — a caller whose schema snapshot
		// still shows the alias cannot look the retirement up.
		text.Should().Contain("Accepted parameters").And.Contain("statusKind");
	}

	// `l1` (tasks_upsert nodes[]) — an ITEM-level alias for `key`, and the reason the filter learned
	// to walk one level down. Without that walk this call would have failed on the generic "each node
	// needs a 'key'" instead of naming the field the caller actually sent.
	[Fact]
	public async Task RetiredAlias_L1_InBatchItem_IsRejected_AndNamesIt()
	{
		await (await Tool(_fx.Mcp, "tasks_board_create")).CallAsync(new Dictionary<string, object?>
		{
			["projectKey"] = _fx.ProjectKey,
			["board"] = "work",
			["kind"] = "work",
		});

		var result = await (await Tool(_fx.Mcp, "tasks_upsert")).CallAsync(new Dictionary<string, object?>
		{
			["projectKey"] = _fx.ProjectKey,
			["board"] = "work",
			["nodes"] = new[]
			{
				new Dictionary<string, object?>
				{
					["l1"] = "unkp-retired-l1",
					["title"] = "Retired alias",
					["version"] = 0,
				},
			},
		});

		result.IsError.Should().Be(true);
		var text = Text(result);
		text.Should().Contain("Unknown parameter").And.Contain("l1").And.Contain("tasks_upsert");
		// The suggestion is scored against the ITEM's own field names, so `key` — the field that
		// replaced this alias — is what comes back, not a top-level parameter that merely looks close.
		text.Should().Contain("key");
	}

	// `prevL1` (tasks_upsert nodes[]) — the rename-source half of the same pair. Pinned separately
	// because its replacement `prevKey` is NOT itself an alias (it names a different node state),
	// so a future cleanup must not sweep it away along with this one.
	[Fact]
	public async Task RetiredAlias_PrevL1_InBatchItem_IsRejected_AndNamesIt()
	{
		await (await Tool(_fx.Mcp, "tasks_board_create")).CallAsync(new Dictionary<string, object?>
		{
			["projectKey"] = _fx.ProjectKey,
			["board"] = "work",
			["kind"] = "work",
		});

		var result = await (await Tool(_fx.Mcp, "tasks_upsert")).CallAsync(new Dictionary<string, object?>
		{
			["projectKey"] = _fx.ProjectKey,
			["board"] = "work",
			["nodes"] = new[]
			{
				new Dictionary<string, object?>
				{
					["key"] = "unkp-retired-prevl1",
					["prevL1"] = "something-old",
					["version"] = 0,
				},
			},
		});

		result.IsError.Should().Be(true);
		Text(result).Should().Contain("Unknown parameter").And.Contain("prevL1").And.Contain("prevKey");
	}

	// `fromNodeId`/`toNodeId` inside relations_create `items[]` — item-level aliases for `from`/`to`.
	// NOTE the asymmetry this pins: the SINGLE form's `fromNodeId`/`toNodeId` TOOL parameters are NOT
	// aliases (nothing named `from`/`to` exists at the top level for them to duplicate) and stay —
	// see RelationsCreate_SingleForm_FromNodeId_StillAccepted for the other side of that line.
	[Fact]
	public async Task RetiredAlias_FromNodeIdInBatchItem_IsRejected_AndNamesIt()
	{
		var result = await (await Tool(_fx.Mcp, "relations_create")).CallAsync(new Dictionary<string, object?>
		{
			["projectKey"] = _fx.ProjectKey,
			["items"] = new[]
			{
				new Dictionary<string, object?>
				{
					["kind"] = "relates_to",
					["fromNodeId"] = "a",
					["toNodeId"] = "b",
				},
			},
		});

		result.IsError.Should().Be(true);
		Text(result).Should().Contain("Unknown parameter").And.Contain("fromNodeId").And.Contain("relations_create");
	}

	// The line the retirement must NOT cross. `fromNodeId` is a legitimate TOP-LEVEL parameter of
	// relations_create's single form; only the item-level duplicate was retired. If a later sweep
	// deletes the tool parameter too, this fails.
	[Fact]
	public async Task RelationsCreate_SingleForm_FromNodeId_StillAccepted()
	{
		var result = await (await Tool(_fx.Mcp, "relations_create")).CallAsync(new Dictionary<string, object?>
		{
			["projectKey"] = _fx.ProjectKey,
			["kind"] = "relates_to",
			["fromNodeId"] = "unkp-no-such-node",
			["toNodeId"] = "unkp-no-such-node-either",
		});

		// It fails on RESOLUTION (no such node), which is proof the parameter itself bound: an
		// unknown-parameter refusal would have happened in the filter, before any resolution ran.
		Text(result).Should().NotContain("Unknown parameter");
	}

	// Task 2: `key` is now REQUIRED in the published schema, not merely in the prose. While the `l1`
	// alias was live no single property could carry the marker — "exactly one of key or l1" is not
	// expressible in JSON Schema `required` — so the description had to apologise for a schema that
	// typed `key` as ["string","null"] and left it optional. Retiring `l1` is what makes this
	// assertable, and this test is the reason the retirement cannot be quietly reversed.
	[Fact]
	public async Task TasksUpsert_NodeKey_IsHonestlyRequiredInTheSchema()
	{
		var tool = await Tool(_fx.Mcp, "tasks_upsert");
		var item = tool.ProtocolTool.InputSchema
			.GetProperty("properties").GetProperty("nodes").GetProperty("items");

		item.GetProperty("required").EnumerateArray().Select(v => v.GetString())
			.Should().Contain("key", "the schema must state the requirement the tool body enforces");
		// And the type union must no longer offer the `null` the tool body rejects.
		item.GetProperty("properties").GetProperty("key").GetProperty("type").GetString()
			.Should().Be("string");
		// The retired aliases are GONE from the schema — this is what makes the filter reject them.
		var fields = item.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToList();
		fields.Should().NotContain("l1").And.NotContain("prevL1");
		fields.Should().Contain("key").And.Contain("prevKey");
	}

	// The same schema honesty for relations_create's item shape: `from`/`to` stand alone.
	[Fact]
	public async Task RelationsCreate_ItemSchema_HasNoNodeIdAliases()
	{
		var tool = await Tool(_fx.Mcp, "relations_create");
		var fields = tool.ProtocolTool.InputSchema
			.GetProperty("properties").GetProperty("items").GetProperty("items")
			.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToList();

		fields.Should().Contain("from").And.Contain("to");
		fields.Should().NotContain("fromNodeId").And.NotContain("toNodeId");
	}

	// tasks_search's schema must not carry the retired boolean either — the filter's rejection above
	// is DERIVED from the schema, so a stray property would silently re-legalise the alias.
	[Fact]
	public async Task TasksSearch_Schema_HasNoIncludeClosed()
	{
		var tool = await Tool(_fx.Mcp, "tasks_search");
		var fields = tool.ProtocolTool.InputSchema
			.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToList();

		fields.Should().NotContain("includeClosed");
		fields.Should().Contain("statusKind");
	}
}
