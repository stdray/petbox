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

// THE MINE THIS GUARDS (work qwen-subagent-spawn-json-parse-error-go-gateway). A tool whose
// generated input schema is `{"type":"object","properties":{}}` is legal MCP and legal JSON Schema —
// and it is the shape that killed two of the owner's runs. qwen-code converts such a tool with
// `parameters` OMITTED (its converter reads an empty `properties` as "no parameters"), and part of
// the Console Go gateway's upstream pool deserializes the request strictly and answers
// `400 [json_parse_error] ... tools[74].function: missing field ``parameters```. Intermittent
// (~4% per turn, measured 2/56 vs 0/50 in an A/B over 106 turns), so a 20-turn session dies about
// half the time and the symptom reads as flakiness rather than a schema fact.
//
// The point of this file is NOT the two tools that were empty on the day (`whoami`,
// `deploy_node_list`). It is that the NEXT no-argument tool cannot re-arm the mine: the sweep below
// walks a real `tools/list` off a booted server and fails on any empty `properties` at all.
public sealed class McpEmptyInputSchemaMechanismTests
{
	[Fact]
	public void Empty_properties_gains_one_optional_ignored_member()
	{
		var schema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };

		McpOutputSchema.EnsureNonEmptyProperties(schema).Should().BeTrue();

		var properties = schema["properties"]!.AsObject();
		properties.Should().ContainSingle().Which.Key.Should().Be(McpOutputSchema.EmptyPropertiesPlaceholder);
		// A plain "string", not a ["string","null"] union: optionality is carried by the ABSENCE from
		// `required`, and a union is one more shape for a lossy client converter to mishandle.
		properties[McpOutputSchema.EmptyPropertiesPlaceholder]!["type"]!.GetValue<string>().Should().Be("string");
		// OPTIONAL: nothing may become required — an existing caller passes no arguments at all.
		schema.Should().NotContainKey("required");
	}

	[Fact]
	public void Absent_properties_is_treated_as_empty()
	{
		var schema = new JsonObject { ["type"] = "object" };

		McpOutputSchema.EnsureNonEmptyProperties(schema).Should().BeTrue();

		schema["properties"]!.AsObject().Should().ContainKey(McpOutputSchema.EmptyPropertiesPlaceholder);
	}

	// The guard fires ONLY on the no-argument case. A tool that already declares a parameter is left
	// byte-identical — the placeholder must never appear next to a real argument, where a model could
	// read it as part of the contract.
	[Fact]
	public void Non_empty_properties_is_untouched()
	{
		var schema = new JsonObject
		{
			["type"] = "object",
			["properties"] = new JsonObject { ["board"] = new JsonObject { ["type"] = "string" } },
			["required"] = new JsonArray { "board" },
		};
		var before = schema.ToJsonString();

		McpOutputSchema.EnsureNonEmptyProperties(schema).Should().BeFalse();

		schema.ToJsonString().Should().Be(before);
	}
}

// A booted server + a full-scope key, so `tools/list` returns the WHOLE surface (the scope filter
// trims what a narrower key may see, and a trimmed list would make the sweep below quietly partial).
public sealed class McpEmptyInputSchemaWireFixture : IAsyncLifetime
{
	const string ProjectKey = "emptyschema";
	const string ApiKey = "yb_key_emptyschema_agent";
	// Full enumerated scope set — same list the conformance fixture uses, for the same reason: a tool
	// hidden by a missing scope is a tool this guard never inspected.
	const string Scopes =
		"config:read,config:write,logs:ingest,logs:query,logs:admin,health:read,health:write," +
		"data:read,data:write,data:schema,tasks:read,tasks:write,memory:read,memory:write," +
		"llm:invoke,llm:admin,deploy:read,deploy:write,agent:poll,agent:heartbeat,admin:provision";

	readonly WebApplicationFactory<Program> _factory;
	HttpClient _http = null!;
	McpClient _mcp = null!;

	public IReadOnlyList<McpClientTool> Tools { get; private set; } = null!;

	public McpEmptyInputSchemaWireFixture()
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
				// Every module on: the sweep is only complete if every tool type registered.
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
			await db.InsertAsync(new Project { Key = ProjectKey, WorkspaceKey = "test", Name = "EmptySchema" });
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

public sealed class McpEmptyInputSchemaWireTests : IClassFixture<McpEmptyInputSchemaWireFixture>
{
	readonly McpEmptyInputSchemaWireFixture _fx;
	public McpEmptyInputSchemaWireTests(McpEmptyInputSchemaWireFixture fx) => _fx = fx;

	// THE GUARD. Not "whoami has properties" — every tool the server actually serves, off the real
	// wire, after every filter that can rewrite a schema on the way out. Add a no-argument tool
	// tomorrow and this is what tells you, instead of a 400 four days later in someone's run.
	[Fact]
	public void ToolsList_NoToolShipsWithEmptyProperties()
	{
		// Guard the guard: an empty (or scope-trimmed to nothing) list would pass every assertion below
		// by vacuity, which is the exact failure mode this whole file exists to prevent elsewhere.
		_fx.Tools.Should().HaveCountGreaterThan(50, "a full-scope key sees the whole tool surface");

		var empty = _fx.Tools
			.Where(t => !(t.ProtocolTool.InputSchema.TryGetProperty("properties", out var p)
				&& p.ValueKind == JsonValueKind.Object
				&& p.EnumerateObject().Any()))
			.Select(t => t.Name)
			.ToList();

		empty.Should().BeEmpty(
			"a tool served with an empty `properties` reaches qwen-code with no `parameters` member at "
			+ "all, and part of the Console Go upstream pool then rejects the WHOLE request with "
			+ "`missing field \"parameters\"` — intermittently, so it reads as flakiness. "
			+ "McpOutputSchema.WithNonEmptyInputProperties is what keeps this empty; if you are reading "
			+ "this failure, it was removed or bypassed, not out-grown");
	}

	// The two tools that were empty on the day the mine went off — named explicitly so the fix cannot
	// silently stop covering them while the sweep above passes for some other reason.
	[Theory]
	[InlineData("whoami")]
	[InlineData("deploy_node_list")]
	public void KnownNoArgumentTools_CarryTheOptionalPlaceholder(string name)
	{
		var schema = _fx.Tool(name).ProtocolTool.InputSchema;

		schema.GetProperty("properties").EnumerateObject().Select(p => p.Name)
			.Should().Contain(McpOutputSchema.EmptyPropertiesPlaceholder);
		// OPTIONAL, always: an existing caller sends `{}` and must keep working unchanged.
		schema.TryGetProperty("required", out var required).Should().BeFalse(
			"a no-argument tool must not acquire a required argument — that would break every existing caller");
		required.ValueKind.Should().Be(JsonValueKind.Undefined);
	}

	// Criterion: existing calls are unaffected. `whoami` with NO arguments answers exactly as before.
	[Fact]
	public async Task Whoami_WithNoArguments_StillAnswers()
	{
		var res = await _fx.Tool("whoami").CallAsync(new Dictionary<string, object?>());

		res.IsError.Should().NotBe(true);
		res.StructuredContent!.Value.GetProperty("project").GetString().Should().Be("emptyschema");
	}

	// …and the placeholder is IGNORED, not bound and not refused: the SDK binder never reads a key
	// that matches no C# parameter, and McpUnknownParameterFilter accepts it because it IS in the
	// advertised schema. A client that echoes the schema back gets the same answer, byte for byte.
	[Fact]
	public async Task Whoami_WithThePlaceholder_AnswersIdentically()
	{
		var bare = await _fx.Tool("whoami").CallAsync(new Dictionary<string, object?>());
		var withPlaceholder = await _fx.Tool("whoami").CallAsync(
			new Dictionary<string, object?> { [McpOutputSchema.EmptyPropertiesPlaceholder] = "anything" });

		withPlaceholder.IsError.Should().NotBe(true);
		withPlaceholder.StructuredContent!.Value.GetRawText()
			.Should().Be(bare.StructuredContent!.Value.GetRawText());
	}
}
