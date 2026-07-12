using System.Diagnostics;
using System.Text.Json;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using PetBox.Core.Auth;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Core.Observability;
using PetBox.Web.Mcp;

namespace PetBox.Tests.Mcp;

// W2 — McpProjectDefaultFilter. The key's default project is applied to the tools whose `projectKey`
// is REQUIRED, per caller and in both directions:
//   leg 1 (tools/call) — inject the default when the caller omitted projectKey;
//   leg 2 (tools/list) — advertise exactly that (projectKey off `required`, note on the description).
// The gate for BOTH legs is the tool's own input schema, which mechanically excludes apikey_create
// (projectKey there is `string?` and mutually exclusive with allProjects) and the four tools whose
// projectKey is optional-by-design (an ABSENT projectKey is cascade semantics — ModuleMcp.ResolveProject
// owns those).
public sealed class ProjectDefaultFilterFixture : IAsyncLifetime
{
	public const string HomeProject = "w2home";     // the wildcard key's default project
	public const string OtherProject = "w2other";   // an explicitly-addressed project

	public const string StarWithDefaultKey = "yb_key_w2_star_default";
	public const string StarNoDefaultKey = "yb_key_w2_star_plain";
	public const string ScopedKey = "yb_key_w2_scoped";
	public const string MarkerKey = "yb_key_w2_marker";  // lives in OtherProject — the observable

	const string Scopes = "admin:provision,tasks:read,memory:read";

	readonly WebApplicationFactory<Program> _factory;
	readonly List<HttpClient> _clients = [];
	readonly List<McpClient> _mcps = [];

	public McpClient StarWithDefault { get; private set; } = null!;
	public McpClient StarNoDefault { get; private set; } = null!;
	public McpClient Scoped { get; private set; } = null!;

	// The host's own services — the canonical, unfiltered ToolCollection lives here (the same source
	// McpProjectDefaultFilter reads). Used by the parameter-NAME guard below, which is about what the
	// tools DECLARE, not about what any one caller is served.
	public IServiceProvider Services => _factory.Services;

	public ProjectDefaultFilterFixture()
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
				["Features:Tasks"] = "true",
				["Features:Memory"] = "true",
			}));
		});
	}

	public async Task InitializeAsync()
	{
		var cs = _factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("PetBox")!;
		TestSchema.Core(cs);
		using (var scope = _factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<PetBoxDb>();
			await db.InsertAsync(new Workspace { Key = "w2ws", Name = "W2", CreatedAt = DateTime.UtcNow });
			await db.InsertAsync(new Project { Key = HomeProject, WorkspaceKey = "w2ws", Name = "Home" });
			await db.InsertAsync(new Project { Key = OtherProject, WorkspaceKey = "w2ws", Name = "Other" });

			await db.InsertAsync(new ApiKey
			{
				Key = StarWithDefaultKey,
				ProjectKey = ProjectScope.AllProjects,
				Scopes = Scopes,
				DefaultProjectKey = HomeProject,
				CreatedAt = DateTime.UtcNow,
			});
			await db.InsertAsync(new ApiKey
			{
				Key = StarNoDefaultKey,
				ProjectKey = ProjectScope.AllProjects,
				Scopes = Scopes,
				CreatedAt = DateTime.UtcNow,
			});
			// Lives in HomeProject — apikey_list(HomeProject) returns it. That is how a test tells
			// "the default was injected" from the outside.
			await db.InsertAsync(new ApiKey
			{
				Key = ScopedKey,
				ProjectKey = HomeProject,
				Scopes = Scopes,
				CreatedAt = DateTime.UtcNow,
			});
			// Lives in OtherProject — apikey_list(OtherProject) returns THIS one and nothing else.
			await db.InsertAsync(new ApiKey
			{
				Key = MarkerKey,
				ProjectKey = OtherProject,
				Scopes = Scopes,
				CreatedAt = DateTime.UtcNow,
			});
		}

		StarWithDefault = await ConnectAsync(StarWithDefaultKey);
		StarNoDefault = await ConnectAsync(StarNoDefaultKey);
		Scoped = await ConnectAsync(ScopedKey);
	}

	async Task<McpClient> ConnectAsync(string apiKey)
	{
		var http = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
		_clients.Add(http);
		var transport = new HttpClientTransport(new HttpClientTransportOptions
		{
			Endpoint = new Uri(http.BaseAddress!, "/mcp"),
			AdditionalHeaders = new Dictionary<string, string> { ["X-Api-Key"] = apiKey },
		}, http);
		var mcp = await McpClient.CreateAsync(transport, cancellationToken: default);
		_mcps.Add(mcp);
		return mcp;
	}

	public async Task DisposeAsync()
	{
		foreach (var mcp in _mcps) await mcp.DisposeAsync();
		foreach (var http in _clients) http.Dispose();
		await _factory.DisposeAsync();
	}
}

public sealed class ProjectDefaultFilterTests : IClassFixture<ProjectDefaultFilterFixture>
{
	readonly ProjectDefaultFilterFixture _fx;
	public ProjectDefaultFilterTests(ProjectDefaultFilterFixture fx) => _fx = fx;

	// apikey_list is the probe: its projectKey is REQUIRED (so it is inside the filter's coverage) and
	// its RESULT names the project it read — the only cheap way to observe, from the wire, WHICH project
	// a call was routed to.
	static async Task<CallToolResult> ListKeysAsync(McpClient mcp, string? projectKey = null)
	{
		var args = new Dictionary<string, object?>();
		if (projectKey is not null) args["projectKey"] = projectKey;
		return await (await Tool(mcp, "apikey_list")).CallAsync(args);
	}

	static async Task<McpClientTool> Tool(McpClient mcp, string name) =>
		(await mcp.ListToolsAsync()).First(t => t.Name == name);

	static IReadOnlyList<string> KeysOf(CallToolResult result) =>
		result.StructuredContent!.Value.GetProperty("keys").EnumerateArray()
			.Select(k => k.GetProperty("key").GetString()!)
			.ToList();

	// ── leg 1: injection ───────────────────────────────────────────────────────────────────────

	// A "*" key WITH a default omits projectKey on a tool that REQUIRES it → the server injects the
	// default. Before W2 this call was an error.
	[Fact]
	public async Task Injection_Fires_WhenWildcardKeyWithDefaultOmitsProjectKey()
	{
		var result = await ListKeysAsync(_fx.StarWithDefault);

		result.IsError.Should().NotBe(true);
		KeysOf(result).Should().Contain(ProjectDefaultFilterFixture.ScopedKey, "the call was routed to the key's default project")
			.And.NotContain(ProjectDefaultFilterFixture.MarkerKey);
	}

	// An explicitly-supplied projectKey ALWAYS wins — the default never overwrites it.
	[Fact]
	public async Task ExplicitProjectKey_Wins_OverTheDefault()
	{
		var result = await ListKeysAsync(_fx.StarWithDefault, ProjectDefaultFilterFixture.OtherProject);

		result.IsError.Should().NotBe(true);
		KeysOf(result).Should().Contain(ProjectDefaultFilterFixture.MarkerKey)
			.And.NotContain(ProjectDefaultFilterFixture.ScopedKey);
	}

	// A "*" key with NO default: nothing to inject → the old failure stands (loudly), it is not
	// silently routed anywhere.
	[Fact]
	public async Task NoDefault_OmittedProjectKey_StillFails()
	{
		var result = await ListKeysAsync(_fx.StarNoDefault);

		result.IsError.Should().Be(true);
		Text(result).Should().Contain("projectKey");
	}

	// A project-scoped key resolves to its OWN claim (ModuleMcp.DefaultProjectOf), so injection covers
	// it too — which is what makes leg 2's schema (see below) honest for such a key. It can only ever
	// be routed to the one project it is authorized for.
	[Fact]
	public async Task Injection_AlsoCovers_AProjectScopedKey()
	{
		var result = await ListKeysAsync(_fx.Scoped);

		result.IsError.Should().NotBe(true);
		KeysOf(result).Should().Contain(ProjectDefaultFilterFixture.ScopedKey)
			.And.NotContain(ProjectDefaultFilterFixture.MarkerKey);
	}

	// apikey_create is EXCLUDED by the schema gate: its projectKey is `string?` (mutually exclusive
	// with allProjects), so injecting one would turn this valid call into an error. It must not be.
	[Fact]
	public async Task ApiKeyCreate_IsNotTouched_ByInjection()
	{
		var result = await (await Tool(_fx.StarWithDefault, "apikey_create")).CallAsync(
			new Dictionary<string, object?>
			{
				["name"] = "w2-probe",
				["scopes"] = "tasks:read",
				["allProjects"] = true,
			});

		result.IsError.Should().NotBe(true, "an injected projectKey would collide with allProjects");
		result.StructuredContent!.Value.GetProperty("projectKey").GetString()
			.Should().Be(ProjectScope.AllProjects);
	}

	// The mechanism, asserted on the LIVE registered schemas: the gate is `required`, and the five
	// tools whose projectKey is optional-by-design are outside it (so neither leg touches them). Read
	// off the no-default key's listing, which is served UNREWRITTEN.
	[Theory]
	[InlineData("memory_search")]
	[InlineData("memory_remember")]
	[InlineData("session_search")]
	[InlineData("search_reindex")]
	[InlineData("apikey_create")]
	public async Task OptionalProjectKeyTools_AreOutsideTheGate(string tool)
	{
		var schema = (await Tool(_fx.StarNoDefault, tool)).ProtocolTool.InputSchema;

		McpProjectDefaultFilter.RequiresProjectKey(schema).Should().BeFalse(
			$"{tool}'s projectKey is optional — an ABSENT one is meaningful, so the filter must keep its hands off");
	}

	// …and the same five are served to a defaulted key with their description UNCHANGED (leg 2 shares
	// the gate, so it cannot promise a default the call path will not apply).
	[Theory]
	[InlineData("memory_search")]
	[InlineData("apikey_create")]
	public async Task OptionalProjectKeyTools_KeepTheirAdvertisedDescription(string tool)
	{
		var plain = ProjectKeyProp((await Tool(_fx.StarNoDefault, tool)).ProtocolTool);
		var defaulted = ProjectKeyProp((await Tool(_fx.StarWithDefault, tool)).ProtocolTool);

		defaulted.Should().Be(plain);
		defaulted.Should().NotContain("defaults to");
	}

	// ── leg 2: the advertised schema ───────────────────────────────────────────────────────────

	// A key that CANNOT resolve a project is told the truth: projectKey is required.
	[Fact]
	public async Task ListTools_NoDefaultKey_StillSeesProjectKeyAsRequired()
	{
		var tool = (await Tool(_fx.StarNoDefault, "apikey_list")).ProtocolTool;

		Required(tool).Should().Contain("projectKey");
		ProjectKeyProp(tool).Should().NotContain("defaults to");
	}

	// A key that CAN resolve one is told THAT truth: projectKey is optional, and what it defaults to.
	[Theory]
	[InlineData(true)]   // the "*" key with a default
	[InlineData(false)]  // a project-scoped key (defaults to its own claim)
	public async Task ListTools_DefaultedKey_SeesProjectKeyAsOptional(bool wildcard)
	{
		var mcp = wildcard ? _fx.StarWithDefault : _fx.Scoped;

		var tool = (await Tool(mcp, "apikey_list")).ProtocolTool;

		// apikey_list's ONLY required arg is projectKey, so `required` is dropped wholesale — the
		// caller may now call it with no arguments at all.
		Required(tool).Should().NotContain("projectKey");
		ProjectKeyProp(tool).Should().Contain($"defaults to `{ProjectDefaultFilterFixture.HomeProject}`");
		// the rest of the schema survives the JsonNode round-trip
		tool.InputSchema.GetProperty("properties").TryGetProperty("projectKey", out _).Should().BeTrue();

		// A tool with OTHER required args keeps them — only projectKey is dropped.
		var multi = (await Tool(mcp, "comments_get")).ProtocolTool;
		Required(multi).Should().Contain("id").And.NotContain("projectKey");
	}

	// ── the parameter NAME both filters key on ─────────────────────────────────────────────────

	// The whole default/existence machinery is keyed on ONE literal parameter name: "projectKey"
	// (McpProjectDefaultFilter.ProjectKeyArg). A tool that spells its project-routing argument anything
	// else sits SILENTLY outside all three guards — no default injection, no existence check, no "*"
	// refusal — and the miss is invisible: the binder simply ignores the unknown argument the caller
	// passed, and the filter then fills `projectKey` with the key's default. deploy_upsert shipped that
	// way (its param was `project`, and a deployment landed in the wrong project); the only guard since
	// has been a DOCS test (McpToolArgNamesInDocsTests), which cannot see a new tool's C# signature.
	//
	// So: the LIVE registered schemas are the source of truth here — same ToolCollection the filter
	// reads, so a tool is covered the day it is written.
	[Fact]
	public void NoTool_SpellsItsProjectArgument_AnythingButProjectKey()
	{
		var collection = _fx.Services.GetRequiredService<IOptions<McpServerOptions>>().Value.ToolCollection;
		collection.Should().NotBeNull();

		var offenders = collection!
			.SelectMany(t => Parameters(t.ProtocolTool).Select(param => (Tool: t.ProtocolTool.Name, Param: param)))
			.Where(x => IsProjectish(x.Param))
			.Select(x => $"{x.Tool}({x.Param})")
			.OrderBy(x => x, StringComparer.Ordinal)
			.ToList();

		offenders.Should().BeEmpty(
			"a project-routing MCP argument MUST be named exactly `{0}` — McpProjectDefaultFilter (default "
			+ "injection + the per-caller schema) and McpProjectExistsFilter (the existence check and the "
			+ "'*' refusal) both key on that literal name, and a parameter under any other name is invisible "
			+ "to all of them: the call is neither defaulted nor validated, and an argument the caller does "
			+ "pass is silently dropped by the binder. Rename the parameter",
			McpProjectDefaultFilter.ProjectKeyArg);
	}

	// A parameter name that MEANS "the project this call routes to" but is not the one name the filters
	// look for. Kept deliberately literal (an exact-name list + a casing check) — `defaultProject` on
	// apikey_create is a VALUE, not a routing argument, and must not trip this.
	static readonly string[] Projectish = ["project", "project_key", "projectid", "project_id", "proj"];

	static bool IsProjectish(string name) =>
		name != McpProjectDefaultFilter.ProjectKeyArg
		&& (Projectish.Contains(name, StringComparer.OrdinalIgnoreCase)
			// …and a CASING drift ("ProjectKey") is the same miss: the lookup is Ordinal.
			|| string.Equals(name, McpProjectDefaultFilter.ProjectKeyArg, StringComparison.OrdinalIgnoreCase));

	static IEnumerable<string> Parameters(Tool tool) =>
		tool.InputSchema.ValueKind == JsonValueKind.Object
		&& tool.InputSchema.TryGetProperty("properties", out var properties)
		&& properties.ValueKind == JsonValueKind.Object
			? properties.EnumerateObject().Select(p => p.Name)
			: [];

	// ── the containment: every injection is greppable ──────────────────────────────────────────

	// The trace marker lands on the TOOL span (McpTracingFilter is the outermost call-tool filter, so
	// Activity.Current inside McpProjectDefaultFilter IS `mcp.tool <name>`) — and only when the server
	// actually supplied the project.
	[Theory]
	[InlineData(null, true)]                                  // omitted → injected → marked
	[InlineData(ProjectDefaultFilterFixture.OtherProject, false)]  // explicit → untouched → unmarked
	public async Task TraceMarker_IsSetOnTheToolSpan_OnlyWhenInjected(string? projectKey, bool marked)
	{
		var spans = new List<Activity>();
		using var listener = new ActivityListener
		{
			ShouldListenTo = s => s.Name == PetBoxActivitySources.McpSourceName,
			Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
			ActivityStopped = spans.Add,
		};
		ActivitySource.AddActivityListener(listener);

		await ListKeysAsync(_fx.StarWithDefault, projectKey);

		var span = spans.Last(a => a.OperationName == "mcp.tool apikey_list");
		span.GetTagItem(McpProjectDefaultFilter.InjectedTag).Should().Be(marked ? true : null);
	}

	static string Text(CallToolResult result) =>
		string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));

	static IReadOnlyList<string> Required(Tool tool) =>
		tool.InputSchema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array
			? required.EnumerateArray().Select(e => e.GetString()!).ToList()
			: [];

	// The advertised description of the tool's projectKey property ("" when the parameter carries no
	// [Description] at all — memory_search's does not).
	static string ProjectKeyProp(Tool tool) =>
		tool.InputSchema.GetProperty("properties").GetProperty("projectKey")
			.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
}
