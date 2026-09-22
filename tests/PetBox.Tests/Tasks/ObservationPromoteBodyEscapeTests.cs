using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace PetBox.Tests.Tasks;

// work observation-promote-body-unescape: tasks_observation_promote's `body` parameter was
// measured on the live wire (2026-09-22, observation
// observation-promote-body-keeps-literal-newline-escapes) to land with literal backslash-n
// characters where the identical body sent through tasks_upsert lands with real newlines.
//
// This test drives BOTH tools over the real MCP wire (the same JSON-RPC argument-binding path
// a live client uses — TasksMcpFixture/McpClient, not a direct ITasksService call, since the
// suspected divergence is in argument binding, not in TasksService) with byte-identical body
// content (GFM: `##` headings, a fenced code block, blank lines) and asserts the two bodies
// round-trip through tasks_node_get identically.
public sealed class ObservationPromoteBodyEscapeTests : IClassFixture<ObservationPromoteBodyFixture>, IAsyncLifetime
{
	const string ProjectKey = "opbe";

	readonly ObservationPromoteBodyFixture _fx;
	readonly McpClient _mcp;

	public ObservationPromoteBodyEscapeTests(ObservationPromoteBodyFixture fx)
	{
		_fx = fx;
		_mcp = fx.Mcp;
	}

	public ValueTask InitializeAsync() => new(_fx.ResetAsync());

	public ValueTask DisposeAsync() => ValueTask.CompletedTask; // the fixture owns host teardown

	// ── helpers (same shape as the sibling Tasks/*Tests.cs files in this folder) ──────────────

	async Task<CallToolResult> Call(string tool, object args) =>
		await (await _mcp.ListToolsAsync()).First(t => t.Name == tool)
			.CallAsync(JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(args))!
				.ToDictionary(kv => kv.Key, kv => (object?)((JsonElement)kv.Value!)));

	static JsonElement Nodes(params object[] nodes) => JsonSerializer.SerializeToElement(nodes);

	static string Text(CallToolResult r) =>
		r.Content.OfType<TextContentBlock>().First().Text;

	static bool IsErr(CallToolResult r) =>
		r.IsError == true ||
		(r.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text?.Contains("\"error\"") ?? false);

	// The body under test: real LF newlines (C# `\n` in a verbatim/interpolated string IS the
	// control character, never the two-character escape), `##` headings, and a fenced block —
	// exactly the shape the card's Do 3 names.
	const string TestBody = "## Problem\n\nSome text with a blank line above.\n\n```csharp\nvar x = 1;\n```\n\nTrailing line.";

	[Fact]
	public async Task PromoteBody_RoundTripsIdenticallyToUpsertBody()
	{
		await Call("tasks_board_create", new { projectKey = ProjectKey, board = "work", kind = "work", methodologyInstance = "$utility" });
		await Call("tasks_board_create", new { projectKey = ProjectKey, board = "observations", kind = "observation", methodologyInstance = "$utility" });

		// Seed a `seen` observation to promote (its own body is irrelevant — the promote call
		// below passes an EXPLICIT body, which must win).
		var seeded = await Call("tasks_upsert", new
		{
			projectKey = ProjectKey,
			board = "observations",
			nodes = Nodes(new { key = "obs-body-1", title = "seed", body = "seed body" }),
		});
		IsErr(seeded).Should().BeFalse(Text(seeded));

		// Reference: the SAME body written through tasks_upsert directly.
		var upserted = await Call("tasks_upsert", new
		{
			projectKey = ProjectKey,
			board = "work",
			nodes = Nodes(new { key = "via-upsert", type = "chore", title = "via upsert", body = TestBody }),
		});
		IsErr(upserted).Should().BeFalse(Text(upserted));

		// Under test: the SAME body through tasks_observation_promote's explicit `body` param.
		var promoted = await Call("tasks_observation_promote", new
		{
			projectKey = ProjectKey,
			observation = "obs-body-1",
			targetBoard = "work",
			type = "chore",
			key = "via-promote",
			title = "via promote",
			body = TestBody,
		});
		IsErr(promoted).Should().BeFalse(Text(promoted));

		var read = await Call("tasks_node_get", new
		{
			projectKey = ProjectKey,
			board = "work",
			nodes = new[] { "via-upsert", "via-promote" },
			bodyLen = -1,
		});
		IsErr(read).Should().BeFalse(Text(read));

		using var doc = JsonDocument.Parse(Text(read));
		var bodies = doc.RootElement.GetProperty("nodes").EnumerateArray()
			.ToDictionary(
				n => n.GetProperty("node").GetProperty("key").GetString()!,
				n => n.GetProperty("node").GetProperty("body").GetString()!);

		bodies["via-upsert"].Should().Be(TestBody);
		bodies["via-promote"].Should().Be(TestBody, "tasks_observation_promote must write `body` through the same path tasks_upsert uses");
	}
}
