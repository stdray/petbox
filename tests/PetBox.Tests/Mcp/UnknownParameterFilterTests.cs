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

public sealed class UnknownParameterFilterTests : IClassFixture<UnknownParameterFilterFixture>
{
	readonly UnknownParameterFilterFixture _fx;
	public UnknownParameterFilterTests(UnknownParameterFilterFixture fx) => _fx = fx;

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
	// for unknown TOP-LEVEL parameters — the filter only ever looks at the top-level argument keys.
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
}
