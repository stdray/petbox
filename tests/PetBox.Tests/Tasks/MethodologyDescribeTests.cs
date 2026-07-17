using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using PetBox.Tasks.Contract;
using PetBox.Web.Mcp;
using PetBox.Web.Mcp.Contract;

namespace PetBox.Tests.Tasks;

// spec methodology-describe-verb: tasks_methodology_describe edits ONE primitive's prose
// Description by its NATURAL KEY — apart from tasks_methodology_rules_upsert, which stays the
// whole-document version-CAS replace for STRUCTURE (granular structure patching was rejected;
// this verb can only ever replace a Description string, never reshape a kind/block/status/
// transition/effect/constraint). Paired with work/primitive-descriptions-as-data, which put
// the Description field on kind/status/transition/effect/constraint in the first place.
public sealed class MethodologyDescribeTests : IClassFixture<MethodologyDescribeFixture>, IAsyncLifetime
{
	const string ProjectKey = "mdsc";
	const string Inst = "support";
	const string Tmpl = "support-tmpl";

	readonly MethodologyDescribeFixture _fx;
	readonly McpClient _mcp;

	public MethodologyDescribeTests(MethodologyDescribeFixture fx)
	{
		_fx = fx;
		_mcp = fx.Mcp;
	}

	public Task InitializeAsync() => _fx.ResetAsync();

	public Task DisposeAsync() => Task.CompletedTask; // the fixture owns host teardown

	// ── helpers (same shape as MethodologyPrimitivesTests/MethodologyGuideTests) ─────────────

	async Task<CallToolResult> Call(string tool, object args) =>
		await (await _mcp.ListToolsAsync()).First(t => t.Name == tool)
			.CallAsync(JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(args))!
				.ToDictionary(kv => kv.Key, kv => (object?)((JsonElement)kv.Value!)));

	static string Text(CallToolResult r) =>
		r.Content.OfType<TextContentBlock>().First().Text;

	static bool IsErr(CallToolResult r) =>
		r.IsError == true ||
		(r.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text?.Contains("\"error\"") ?? false);

	static JsonElement Parse(CallToolResult r) =>
		JsonDocument.Parse(Text(r)).RootElement.Clone();

	static readonly JsonSerializerOptions WireJson = new() { PropertyNameCaseInsensitive = true };

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
							new { from = "New", to = "Open" },
							new { from = "Open", to = "Resolved" },
							new { from = "Open", to = "Junk" },
						},
					},
				},
				linkConstraints = new object[] { new { type = "incident", link = "blocks" } },
				effects = new object[] { new { on = "Resolved", link = "blocks", direction = "outgoing", onlyFrom = "Open" } },
			},
		},
		linkKinds = new object[] { new { slug = "escalates" } },
		tagAxes = new object[] { new { @namespace = "severity" } },
	};

	async Task InstallAsync()
	{
		var json = JsonSerializer.Serialize(SupportDefinition());
		var input = JsonSerializer.Deserialize<MethodologyDefInput>(json, WireJson)!;
		var def = MethodologyWire.ParseDefinition(input);
		using var scope = _fx.Factory.Services.CreateScope();
		var tasks = scope.ServiceProvider.GetRequiredService<ITasksService>();
		await tasks.UpsertMethodologyTemplateAsync(ProjectKey, Tmpl, def, 0);
		await tasks.CreateMethodologyInstanceAsync(ProjectKey, Inst, "template", Tmpl);
	}

	// Merges the fixed {projectKey, name} envelope onto the primitive-specific natural-key
	// fields, then round-trips through Call's own JsonElement pipeline like every other verb.
	Task<CallToolResult> Describe(Dictionary<string, object?> args)
	{
		args["projectKey"] = ProjectKey;
		args["name"] = Inst;
		return Call("tasks_methodology_describe", args);
	}

	async Task<JsonElement> RulesGet()
	{
		var r = await Call("tasks_methodology_rules_get", new { projectKey = ProjectKey, name = Inst });
		IsErr(r).Should().BeFalse(Text(r));
		return Parse(r);
	}

	// 1. Every primitive kind (kind/status/transition/effect/constraint/linkKind/tagAxis) can
	// have its Description set by natural key, WITHOUT the caller ever naming a version — and
	// the write survives on the very next rules_get (proving it went through the real
	// document, not just an in-memory echo).
	[Fact]
	public async Task Describe_EveryPrimitiveKind_SetsDescription_VisibleOnNextRulesGet()
	{
		await InstallAsync();

		IsErr(await Describe(new() { ["primitive"] = "kind", ["kind"] = "support", ["description"] = "Support requests." }))
			.Should().BeFalse();
		IsErr(await Describe(new() { ["primitive"] = "status", ["kind"] = "support", ["type"] = "ticket", ["slug"] = "Open", ["description"] = "Actively triaged." }))
			.Should().BeFalse();
		IsErr(await Describe(new() { ["primitive"] = "transition", ["kind"] = "support", ["type"] = "ticket", ["from"] = "New", ["to"] = "Open", ["description"] = "Triage assigns severity." }))
			.Should().BeFalse();
		IsErr(await Describe(new() { ["primitive"] = "effect", ["kind"] = "support", ["on"] = "Resolved", ["link"] = "blocks", ["direction"] = "outgoing", ["onLeave"] = false, ["description"] = "Releases the outage." }))
			.Should().BeFalse();
		IsErr(await Describe(new() { ["primitive"] = "constraint", ["kind"] = "support", ["type"] = "incident", ["link"] = "blocks", ["description"] = "Every incident names an outage." }))
			.Should().BeFalse();
		IsErr(await Describe(new() { ["primitive"] = "linkKind", ["slug"] = "escalates", ["description"] = "support escalation edge" }))
			.Should().BeFalse();
		IsErr(await Describe(new() { ["primitive"] = "tagAxis", ["namespace"] = "severity", ["description"] = "how urgent" }))
			.Should().BeFalse();

		var rules = await RulesGet();
		var kind = rules.GetProperty("kinds").EnumerateArray().Single();
		kind.GetProperty("description").GetString().Should().Be("Support requests.");

		var block = kind.GetProperty("workflows").EnumerateArray().Single();
		var open = block.GetProperty("statuses").EnumerateArray().Single(s => s.GetProperty("slug").GetString() == "Open");
		open.GetProperty("description").GetString().Should().Be("Actively triaged.");

		var transition = block.GetProperty("transitions").EnumerateArray()
			.Single(t => t.GetProperty("from").GetString() == "New" && t.GetProperty("to").GetString() == "Open");
		transition.GetProperty("description").GetString().Should().Be("Triage assigns severity.");

		var effect = kind.GetProperty("effects").EnumerateArray().Single();
		effect.GetProperty("description").GetString().Should().Be("Releases the outage.");

		var constraint = kind.GetProperty("linkConstraints").EnumerateArray().Single();
		constraint.GetProperty("description").GetString().Should().Be("Every incident names an outage.");

		var linkKind = rules.GetProperty("linkKinds").EnumerateArray().Single();
		linkKind.GetProperty("description").GetString().Should().Be("support escalation edge");

		var tagAxis = rules.GetProperty("tagAxes").EnumerateArray().Single();
		tagAxis.GetProperty("description").GetString().Should().Be("how urgent");
	}

	// 2. A description SET first and then CLEARED with "" round-trips to absent (the wire
	// omits null, same posture as every other optional prose field).
	[Fact]
	public async Task Describe_EmptyString_ClearsAPreviouslySetDescription()
	{
		await InstallAsync();
		IsErr(await Describe(new() { ["primitive"] = "kind", ["kind"] = "support", ["description"] = "Support requests." })).Should().BeFalse();
		(await RulesGet()).GetProperty("kinds").EnumerateArray().Single().GetProperty("description").GetString()
			.Should().Be("Support requests.");

		IsErr(await Describe(new() { ["primitive"] = "kind", ["kind"] = "support", ["description"] = "" })).Should().BeFalse();
		var kind = (await RulesGet()).GetProperty("kinds").EnumerateArray().Single();
		kind.TryGetProperty("description", out _).Should().BeFalse("a cleared description is omitted by the serializer, same as never having been set");
	}

	// 3. A natural key that matches nothing is a clear, actionable error — nothing is written
	// (the structural document is untouched: a second, correct call still finds everything).
	[Fact]
	public async Task Describe_UnmatchedNaturalKey_IsAClearError_NothingWritten()
	{
		await InstallAsync();

		var badKind = await Describe(new() { ["primitive"] = "kind", ["kind"] = "no-such-kind", ["description"] = "x" });
		IsErr(badKind).Should().BeTrue();
		Text(badKind).Should().Contain("no kind matched");

		var badStatus = await Describe(new() { ["primitive"] = "status", ["kind"] = "support", ["type"] = "ticket", ["slug"] = "NoSuchStatus", ["description"] = "x" });
		IsErr(badStatus).Should().BeTrue();
		Text(badStatus).Should().Contain("no status matched");

		// The rejected calls above must not have perturbed the live document at all.
		var rules = await RulesGet();
		rules.GetProperty("kinds").EnumerateArray().Single().TryGetProperty("description", out _).Should().BeFalse();
	}

	// 4. An unknown `primitive` value is rejected naming the legal vocabulary.
	[Fact]
	public async Task Describe_UnknownPrimitive_RejectedNamingTheVocabulary()
	{
		await InstallAsync();
		var r = await Describe(new() { ["primitive"] = "workflow", ["kind"] = "support", ["description"] = "x" });
		IsErr(r).Should().BeTrue();
		Text(r).Should().Contain("kind, status, transition, effect, constraint, linkKind, tagAxis");
	}

	// 5. A missing instance is a clear error (not a silent no-op).
	[Fact]
	public async Task Describe_MissingInstance_IsAClearError()
	{
		var r = await Call("tasks_methodology_describe", new { projectKey = ProjectKey, name = "no-such-instance", primitive = "kind", kind = "support", description = "x" });
		IsErr(r).Should().BeTrue();
		Text(r).Should().Contain("not found");
	}
}
