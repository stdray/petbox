using System.Text.Json;
using System.Text.Json.Nodes;
using LinqToDB;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Tests.Support;
using PetBox.Web.Mcp;

namespace PetBox.Tests.Mcp;

// THE MITIGATION (work mcp-nullable-schema-anyof). On the mimo-v2.6 × opencode-go link a raw-HTTP
// A/B (research/mcp-parallel-arg-corruption) isolates the trigger precisely: with a nullable written
// in the ARRAY form — `"type": ["string","null"]`, what STJ/MEAI generates and what PetBox put on
// the wire for EVERY nullable field — 5/5 runs came back BROKEN (both parallel tool_calls sharing
// index 0, arguments destroyed, the real call only in the content XML). With the equivalent `anyOf`
// the identical payload gave 5/5 OK. The array-form is valid JSON Schema; this is a mitigation for
// a model+gateway defect filed upstream, not a correction of a mistake.
//
// So the question these tests answer is never "is anyOf nicer" but: is the array-form GONE from
// everything we serve, has anything that used to be able to read a type lost that ability, and are
// the two forms we DELIBERATELY keep still there (converting those too would trade one client
// failure for another, and would blind McpUnknownParameterFilter's per-parameter diagnostics).
public sealed class McpNullableUnionAnyOfMechanismTests
{
	[Fact]
	public void NullablePrimitiveUnion_becomesAnyOf_keepingEverySiblingKeyword()
	{
		var schema = new JsonObject
		{
			["type"] = new JsonArray("string", "null"),
			["description"] = "the search text",
			["default"] = null,
		};

		McpOutputSchema.PrimitiveNullableUnionToAnyOf(schema).Should().BeTrue();

		schema["type"].Should().BeNull("the array-form union is what this rewrites");
		var branches = schema["anyOf"]!.AsArray();
		branches.Should().HaveCount(2);
		branches[0]!["type"]!.GetValue<string>().Should().Be("string");
		branches[1]!["type"]!.GetValue<string>().Should().Be("null");
		schema["description"]!.GetValue<string>().Should().Be("the search text");
		schema.Should().ContainKey("default");
	}

	[Theory]
	[InlineData("integer")]
	[InlineData("number")]
	[InlineData("boolean")]
	public void EveryPrimitiveUnionForm_is_converted(string primitive)
	{
		var schema = new JsonObject { ["type"] = new JsonArray(primitive, "null") };

		McpOutputSchema.PrimitiveNullableUnionToAnyOf(schema).Should().BeTrue();

		schema["anyOf"]!.AsArray()[0]!["type"]!.GetValue<string>().Should().Be(primitive);
	}

	// Reversed order is the same union — `["null","string"]` must not survive as an unconverted node.
	[Fact]
	public void NullFirstUnion_is_converted_too()
	{
		var schema = new JsonObject { ["type"] = new JsonArray("null", "string") };

		McpOutputSchema.PrimitiveNullableUnionToAnyOf(schema).Should().BeTrue();

		schema["anyOf"]!.AsArray()[0]!["type"]!.GetValue<string>().Should().Be("string");
	}

	// The SCOPE boundary, case by case. Each of these would be an over-conversion: array/object unions
	// are excluded on purpose (the trigger's own link rejects an anyOf carrying such branches, and
	// ItemSchema's `items` lookup depends on the flat form), and a plain type has no null to convert.
	[Theory]
	[InlineData("array")]
	[InlineData("object")]
	public void ArrayAndObjectUnions_keep_the_array_form(string kind)
	{
		var schema = new JsonObject
		{
			["type"] = new JsonArray(kind, "null"),
			["items"] = new JsonObject { ["type"] = "object" },
		};
		var before = schema.ToJsonString();

		McpOutputSchema.PrimitiveNullableUnionToAnyOf(schema).Should().BeFalse();

		schema.ToJsonString().Should().Be(before);
	}

	[Fact]
	public void NonUnionType_is_untouched()
	{
		var schema = new JsonObject { ["type"] = "string" };
		var before = schema.ToJsonString();

		McpOutputSchema.PrimitiveNullableUnionToAnyOf(schema).Should().BeFalse();

		schema.ToJsonString().Should().Be(before);
	}

	// A union WITHOUT a null arm (`["integer","number"]`) is a different feature and must survive.
	[Fact]
	public void UnionWithoutNull_is_untouched()
	{
		var schema = new JsonObject { ["type"] = new JsonArray("integer", "number") };

		McpOutputSchema.PrimitiveNullableUnionToAnyOf(schema).Should().BeFalse();

		schema["type"].Should().BeOfType<JsonArray>();
	}

	// Siblings, not just the first match: a tool with two nullable parameters must have BOTH
	// rewritten. An early exit would leave the second in the old form — the mixed wire the whole
	// change exists to end.
	[Fact]
	public void EveryNullableUnion_inTheTree_is_rewritten()
	{
		var schema = new JsonObject
		{
			["type"] = "object",
			["properties"] = new JsonObject
			{
				["a"] = new JsonObject { ["type"] = new JsonArray("string", "null") },
				["b"] = new JsonObject { ["type"] = new JsonArray("array", "null"), ["items"] = new JsonObject { ["type"] = "string" } },
				["c"] = new JsonObject
				{
					["type"] = "object",
					["properties"] = new JsonObject
					{
						["deep"] = new JsonObject { ["type"] = new JsonArray("integer", "null") },
					},
				},
				["d"] = new JsonObject { ["type"] = new JsonArray("string", "null") },
			},
		};

		McpOutputSchema.RewriteNullableUnions(schema).Should().BeTrue();

		var props = schema["properties"]!.AsObject();
		props["a"]!["anyOf"].Should().NotBeNull();
		props["b"]!["type"].Should().BeOfType<JsonArray>("the array-union is outside the conversion's scope");
		props["c"]!["properties"]!["deep"]!["anyOf"].Should().NotBeNull("nested nodes are walked too");
		props["d"]!["anyOf"].Should().NotBeNull("a rewrite must not stop at the first match");
	}

	// The order-independent half of the pipeline: the two readers of the union form understand BOTH
	// shapes. They only ever see the array-form today (the rewrite runs last), so this is not about
	// correctness — it is so that moving the rewrite earlier cannot silently change the schema.
	[Fact]
	public void NullArmRemoval_understands_the_anyOf_form()
	{
		var schema = new JsonObject
		{
			["anyOf"] = new JsonArray(
				new JsonObject { ["type"] = "string" },
				new JsonObject { ["type"] = "null" }),
		};

		McpOutputSchema.DropNullArm(schema);

		schema.Should().NotContainKey("anyOf");
		schema["type"]!.GetValue<string>().Should().Be("string",
			"a single surviving bare-type branch collapses back to the plain form — the array-form equivalent");
	}

	[Fact]
	public void NullArmRemoval_leaves_an_anyOf_that_has_no_null_arm()
	{
		var schema = new JsonObject
		{
			["anyOf"] = new JsonArray(new JsonObject { ["type"] = "string" }),
		};
		var before = schema.ToJsonString();

		McpOutputSchema.DropNullArm(schema);

		schema.ToJsonString().Should().Be(before);
	}
}

public sealed class McpNullableUnionAnyOfWireFixture : IAsyncLifetime
{
	const string ProjectKey = "anyofschema";
	const string ApiKey = "yb_key_anyofschema_agent";
	// Full enumerated scope set (same reason as the other wire fixtures): a tool hidden by a missing
	// scope is a tool this sweep never inspected.
	const string Scopes =
		"config:read,config:write,logs:ingest,logs:query,logs:admin,health:read,health:write," +
		"data:read,data:write,data:schema,tasks:read,tasks:write,memory:read,memory:write," +
		"llm:invoke,llm:admin,deploy:read,deploy:write,agent:poll,agent:heartbeat,admin:provision";

	readonly WebApplicationFactory<Program> _factory;
	HttpClient _http = null!;
	McpClient _mcp = null!;

	public IReadOnlyList<McpClientTool> Tools { get; private set; } = null!;

	public McpNullableUnionAnyOfWireFixture()
	{
		Environment.SetEnvironmentVariable("PETBOX_MASTER_KEY", "test-key-for-secrets");
		Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");

		_factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
		{
			b.UseEnvironment("Testing");
			b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
			{
				["ConnectionStrings:PetBox"] = TestSchema.NewTempConnectionString(),
				["Host:BackgroundServices"] = "false",
				["Features:Config"] = "true",
				["Features:Logging"] = "true",
				["Features:Data"] = "true",
				["Features:Dashboard"] = "true",
				["Features:Tasks"] = "true",
				["Features:Memory"] = "true",
				["Features:LlmRouter"] = "true",
				["Features:Deploy"] = "true",
			}));
		});
	}

	public async ValueTask InitializeAsync()
	{
		var cs = _factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("PetBox")!;
		TestSchema.Core(cs);
		using (var scope = _factory.Services.CreateScope())
		{
			using var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open();
			await db.ApiKeys.Where(k => k.Key == ApiKey).DeleteAsync();
			await db.Projects.Where(p => p.Key == ProjectKey).DeleteAsync();
			await db.Workspaces.Where(w => w.Key == "test").DeleteAsync();
			await db.InsertAsync(new Workspace { Key = "test", Name = "Test", CreatedAt = DateTime.UtcNow });
			await db.InsertAsync(new Project { Key = ProjectKey, WorkspaceKey = "test", Name = "AnyOfSchema" });
			await db.InsertAsync(new ApiKey { Key = ApiKey, ProjectKey = ProjectKey, Scopes = Scopes, CreatedAt = DateTime.UtcNow });
		}

		_http = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
		var transport = new HttpClientTransport(new HttpClientTransportOptions
		{
			Endpoint = new Uri(_http.BaseAddress!, "/mcp"),
			AdditionalHeaders = new Dictionary<string, string> { ["X-Api-Key"] = ApiKey },
		}, _http);
		_mcp = await McpTestClient.ConnectAsync(transport);
		Tools = [.. await _mcp.ListToolsAsync()];
	}

	public McpClientTool Tool(string name) => Tools.Single(t => t.Name == name);

	public async ValueTask DisposeAsync()
	{
		await _mcp.DisposeAsync();
		_http.Dispose();
		await _factory.DisposeAsync();
	}
}

public sealed class McpNullableUnionAnyOfWireTests : IClassFixture<McpNullableUnionAnyOfWireFixture>
{
	readonly McpNullableUnionAnyOfWireFixture _fx;
	public McpNullableUnionAnyOfWireTests(McpNullableUnionAnyOfWireFixture fx) => _fx = fx;

	// THE GUARD, over the real wire: nothing we serve carries a nullable PRIMITIVE in the array-form
	// any more — not one tool, in either schema. That is the whole mitigation; a single survivor is
	// enough for the broken link to corrupt a call again.
	[Fact]
	public void ToolsList_NoNullablePrimitiveIsLeftInArrayForm()
	{
		// Guard the guard: an empty (or scope-trimmed) list passes everything below by vacuity.
		_fx.Tools.Should().HaveCountGreaterThan(50, "a full-scope key sees the whole tool surface");

		var survivors = new List<string>();
		foreach (var tool in _fx.Tools)
		{
			survivors.AddRange(SurvivingArrayFormUnions("input", tool.ProtocolTool.Name, tool.ProtocolTool.InputSchema));
			if (tool.ProtocolTool.OutputSchema is { } output)
				survivors.AddRange(SurvivingArrayFormUnions("output", tool.Name, output));
		}

		survivors.Should().BeEmpty(
			"a nullable primitive in the array-form is what corrupts tool-call arguments on the "
			+ "mimo × opencode-go link — McpOutputSchema.WithAnyOfNullableUnions is what keeps the wire "
			+ "clear of it. If you are reading this, it was removed, bypassed, or outgrew by a tool "
			+ "whose schema is shaped after this transform runs.");
	}

	// The other half of "the array-form is gone": what we KEEP is still there. An over-eager
	// conversion of the array/object unions would satisfy the test above and cost us the flat `items`
	// lookup McpUnknownParameterFilter.ItemSchema depends on for its per-parameter diagnostics.
	[Fact]
	public void ArrayAndObjectNullableUnions_KeepTheArrayForm()
	{
		var kept = _fx.Tools
			.SelectMany(t => ArrayFormUnions(t.ProtocolTool.InputSchema).Concat(
				t.ProtocolTool.OutputSchema is { } o ? ArrayFormUnions(o) : []))
			.ToList();

		kept.Should().NotBeEmpty(
			"the array-form unions are excluded from the conversion ON PURPOSE — if this is empty, the "
			+ "exclusion was implemented by converting everything after all, which is the failure the "
			+ "exclusion exists to prevent");
	}

	// Named, so the two shapes are pinned to real tools and not only to the aggregate sweep: the raw
	// JSON parameter of data_query/data_exec is a `[McpJsonShape("array","null")]` parameter and must
	// still be a flat `["array","null"]` — that flatness is what ItemSchema reads.
	[Theory]
	[InlineData("data_query")]
	[InlineData("data_exec")]
	public void RawPayloadParameter_StaysFlatArrayForm(string tool)
	{
		var properties = _fx.Tool(tool).ProtocolTool.InputSchema.GetProperty("properties");
		var payload = properties.GetProperty("params");

		payload.GetProperty("type").EnumerateArray().Select(t => t.GetString())
			.Should().BeEquivalentTo(["array", "null"],
				"converting this to anyOf would hide `items` from ItemSchema and lose the filter's "
				+ "per-parameter diagnostic");
		payload.TryGetProperty("anyOf", out _).Should().BeFalse();
	}

	// A [McpRequiredMember] key must arrive required AND non-nullable — the whole point of the marker.
	// It reaches us as `["string","null"]`, loses its null arm (WithRequiredMembers), and the rewrite
	// then has nothing left to convert. Named rather than left to the aggregate sweep, because a
	// silent success here would mean a required key the client may legally send as null.
	[Fact]
	public void RequiredMemberKey_IsRequiredAndNotNullable()
	{
		var properties = _fx.Tool("tasks_upsert").ProtocolTool.InputSchema
			.GetProperty("properties").GetProperty("nodes");
		var item = properties.GetProperty("items");
		var key = item.GetProperty("properties").GetProperty("key");

		key.GetProperty("type").GetString().Should().Be("string");
		key.TryGetProperty("anyOf", out _).Should().BeFalse("a required key must not offer null");
		item.GetProperty("required").EnumerateArray().Select(r => r.GetString())
			.Should().Contain("key");
	}

	// The nullable primitive that actually gets passed on every call — the argument the broken link
	// destroyed. Pinned by name because it is the field the incident was observed on.
	[Fact]
	public void NullableStringArgument_IsAnyOfWithANullBranch()
	{
		var properties = _fx.Tool("memory_search").ProtocolTool.InputSchema.GetProperty("properties");
		var q = properties.GetProperty("q");

		q.TryGetProperty("type", out _).Should().BeFalse("the array-form is the thing being removed");
		var branches = q.GetProperty("anyOf").EnumerateArray().ToList();
		branches.Should().HaveCount(2);
		branches.Select(b => b.GetProperty("type").GetString())
			.Should().BeEquivalentTo(["string", "null"]);
		q.GetProperty("description").GetString().Should().NotBeNullOrWhiteSpace(
			"the rewrite must keep sibling keywords — a description lost here is a contract silently narrowed");
	}

	// Every anyOf we now emit is exactly the two-branch primitive+null form the mitigation produces —
	// no pre-existing anyOf got rewritten, and none came out malformed.
	[Fact]
	public void EveryAnyOfBranch_IsWellFormed()
	{
		var malformed = new List<string>();
		foreach (var tool in _fx.Tools)
		{
			foreach (var (path, schema) in AnyOfNodes("input", tool.ProtocolTool.InputSchema))
			{
				var branches = schema.EnumerateArray().ToList();
				if (branches.Any(b => b.ValueKind != JsonValueKind.Object
						|| b.TryGetProperty("type", out var t) && t.ValueKind != JsonValueKind.String
						|| !b.TryGetProperty("type", out _))
					|| branches.Count(b => b.GetProperty("type").GetString() == "null") > 1)
					malformed.Add($"{tool.Name}/{path}");
			}
		}

		malformed.Should().BeEmpty("a malformed anyOf branch breaks every strict validator downstream");
	}

	// A migrated schema is only worth anything if arguments still BIND. Two shapes of the same call:
	// every nullable omitted (the common case), and one passed as an explicit null — the value the
	// anyOf's second branch exists to admit, and which a schema that lost the branch would now refuse.
	[Fact]
	public async Task NullableArguments_Bind_omitted_and_explicitly_null()
	{
		var bare = await _fx.Tool("memory_search").CallAsync(new Dictionary<string, object?>
		{
			["projectKey"] = "anyofschema",
			["q"] = "alpha",
		});
		var explicitNulls = await _fx.Tool("memory_search").CallAsync(new Dictionary<string, object?>
		{
			["projectKey"] = "anyofschema",
			["q"] = "alpha",
			["cursor"] = null,
			["limit"] = null,
		});

		bare.IsError.Should().NotBe(true, "a nullable parameter omitted is the ordinary call path");
		explicitNulls.IsError.Should().NotBe(true,
			"the anyOf's null branch must stay admissible, or a client that echoes null is refused for "
			+ "a schema we rewrote");
	}

	// ── helpers ──

	// Nullable-PRIMITIVE array-form unions anywhere in the tree — the shape that must not survive.
	static IEnumerable<string> SurvivingArrayFormUnions(string which, string tool, JsonElement schema)
	{
		foreach (var (path, node) in Nodes(which, schema))
			if (node.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.Array)
			{
				var arms = type.EnumerateArray().Select(a => a.GetString()).ToList();
				if (arms.Contains("null") && arms.Where(a => a != "null").All(IsPrimitive))
					yield return $"{tool}/{path}";
			}
	}

	// Array-form unions of ANY kind, including the array/object ones deliberately left in place.
	static IEnumerable<string> ArrayFormUnions(JsonElement schema) =>
		Nodes("path", schema)
			.Where(n => n.Node.TryGetProperty("type", out var type)
				&& type.ValueKind == JsonValueKind.Array
				&& type.EnumerateArray().Select(a => a.GetString()).Contains("null"))
			.Select(n => $"{n.Path}{n.Node.GetProperty("type").GetRawText()}");

	static IEnumerable<(string Path, JsonElement Schema)> AnyOfNodes(string prefix, JsonElement schema) =>
		Nodes(prefix, schema).Where(n => n.Node.TryGetProperty("anyOf", out _)).Select(n => (n.Path, n.Node.GetProperty("anyOf")));

	static bool IsPrimitive(string? arm) => arm is "string" or "integer" or "number" or "boolean";

	// Depth-capped walk over every subschema: `properties` values, `items`, combinator branches and
	// `$defs` — the same surfaces CollectPropertyNames walks, because they are where a nullable hides.
	static IEnumerable<(string Path, JsonElement Node)> Nodes(string prefix, JsonElement schema, int depth = 0)
	{
		if (depth > 8 || schema.ValueKind != JsonValueKind.Object) yield break;
		yield return (prefix, schema);
		if (schema.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
			foreach (var p in props.EnumerateObject())
				foreach (var hit in Nodes($"{prefix}/properties/{p.Name}", p.Value, depth + 1))
					yield return hit;
		if (schema.TryGetProperty("items", out var items))
			foreach (var hit in Nodes($"{prefix}/items", items, depth + 1))
				yield return hit;
		foreach (var keyword in new[] { "anyOf", "oneOf", "allOf" })
			if (schema.TryGetProperty(keyword, out var branches) && branches.ValueKind == JsonValueKind.Array)
				foreach (var (branch, i) in branches.EnumerateArray().Select((b, i) => (b, i)))
					foreach (var hit in Nodes($"{prefix}/{keyword}[{i}]", branch, depth + 1))
						yield return hit;
		if (schema.TryGetProperty("$defs", out var defs) && defs.ValueKind == JsonValueKind.Object)
			foreach (var def in defs.EnumerateObject())
				foreach (var hit in Nodes($"{prefix}/$defs/{def.Name}", def.Value, depth + 1))
					yield return hit;
	}
}
