using System.Text.Json;
using LinqToDB;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Tests.Support;

namespace PetBox.Tests.Mcp;

// Shared per-class host for the agent_def_upsert contract tests. Modelled on TasksMcpFixture, with
// the one difference that matters here: the api key carries agents:read/agents:write (the tasks
// fixture's key does not, and this family is gated on those two). No per-test reset — every test
// writes its OWN definition key, so nothing one test stores can reach another.
public sealed class AgentDefMcpFixture : IAsyncLifetime
{
	public const string ProjectKey = "adefm";
	const string AgentKey = "yb_key_adefm_agent";

	readonly WebApplicationFactory<Program> _factory;
	HttpClient _http = null!;

	public McpClient Mcp { get; private set; } = null!;

	public AgentDefMcpFixture()
	{
		Environment.SetEnvironmentVariable("PETBOX_MASTER_KEY", "test-key-for-secrets");
		Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");

		_factory = new WebApplicationFactory<Program>()
			.WithWebHostBuilder(b =>
			{
				b.UseEnvironment("Testing");
				b.ConfigureAppConfiguration((_, cfg) =>
				{
					cfg.AddInMemoryCollection(new Dictionary<string, string?>
					{
						["ConnectionStrings:PetBox"] = TestSchema.NewTempConnectionString(),
						["Host:BackgroundServices"] = "false",
					});
				});
			});
	}

	public async ValueTask InitializeAsync()
	{
		var cs = _factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("PetBox")!;
		TestSchema.Core(cs);

		using (var scope = _factory.Services.CreateScope())
		{
			using var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open();
			await db.ApiKeys.Where(k => k.Key == AgentKey).DeleteAsync();
			await db.Projects.Where(p => p.Key == ProjectKey).DeleteAsync();
			await db.Workspaces.Where(w => w.Key == "test").DeleteAsync();
			await db.InsertAsync(new Workspace { Key = "test", Name = "Test", CreatedAt = DateTime.UtcNow });
			await db.InsertAsync(new Project { Key = ProjectKey, WorkspaceKey = "test", Name = "AgentDefMerge" });
			await db.InsertAsync(new ApiKey
			{
				Key = AgentKey,
				ProjectKey = ProjectKey,
				Scopes = "agents:read,agents:write",
				CreatedAt = DateTime.UtcNow,
			});
		}

		_http = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
		_http.DefaultRequestHeaders.Add("X-Api-Key", AgentKey);
		var transport = new HttpClientTransport(new HttpClientTransportOptions
		{
			Endpoint = new Uri(_http.BaseAddress!, "/mcp"),
			AdditionalHeaders = new Dictionary<string, string> { ["X-Api-Key"] = AgentKey },
		}, _http);
		Mcp = await McpTestClient.ConnectAsync(transport);
	}

	public async ValueTask DisposeAsync()
	{
		await Mcp.DisposeAsync();
		_http.Dispose();
		await _factory.DisposeAsync();
	}
}

// work/agent-def-upsert-typed-and-merge-by-role — the two halves of ONE signature change, driven
// over the real MCP wire (the client sends whatever dictionary it is given, so nothing strips a
// stray key before the server sees it — the same stance UnknownParameterFilterTests takes).
//
// What was wrong, and why the two fixes are one change:
//   * `definition` was a bare JsonElement carrying [McpJsonShape("object")], which stamps
//     "type":"object" and NOTHING else — no `properties`, no `required` (spec/typed-mcp-inputs
//     forbids exactly this). McpUnknownParameterFilter therefore had no field vocabulary to compare
//     against, so a typo inside a role was not refused.
//   * the verb was a full-document REPLACE. Combine the two and a mistyped field name did not fail
//     — it silently DELETED the field it meant to set, and a caller resending four of six roles
//     silently deleted the other two.
// Typing alone would have left the whole document travelling for a one-role edit
// (spec/write-cost-follows-change, measured 10 520 B on six roles); merging alone would have left
// the typo unpoliced. Hence one change.
public sealed class AgentDefUpsertMergeTests : IClassFixture<AgentDefMcpFixture>
{
	readonly AgentDefMcpFixture _fx;
	public AgentDefUpsertMergeTests(AgentDefMcpFixture fx) => _fx = fx;

	static async Task<McpClientTool> Tool(McpClient mcp, string name) =>
		(await mcp.ListToolsAsync()).First(t => t.Name == name);

	async Task<CallToolResult> Call(string tool, Dictionary<string, object?> args) =>
		await (await Tool(_fx.Mcp, tool)).CallAsync(args);

	// error.message alone: the raw envelope repeats the message inside `detail` (the stack trace)
	// and keeps HTML-sensitive characters escaped, so matching over the raw text lies. Same helper,
	// same reasons, as UnknownParameterFilterTests.Text.
	static string Text(CallToolResult result)
	{
		var raw = string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));
		try
		{
			using var doc = JsonDocument.Parse(raw);
			if (doc.RootElement.TryGetProperty("error", out var error)
				&& error.TryGetProperty("message", out var message)
				&& message.GetString() is { } text)
				return text;
		}
		catch (JsonException) { /* not an envelope — fall through */ }
		return raw;
	}

	static Dictionary<string, object?> Role(
		string slug, string? tier = null, string[]? caps = null, string? notes = null,
		bool? spawnAllowed = null, string[]? spawnRoles = null, bool? deleted = null)
	{
		var d = new Dictionary<string, object?> { ["slug"] = slug };
		if (tier is not null) d["tier"] = tier;
		if (caps is not null) d["requiredCapabilities"] = caps;
		if (notes is not null) d["notes"] = notes;
		if (spawnAllowed is not null) d["spawnAllowed"] = spawnAllowed;
		if (spawnRoles is not null) d["spawnAllowedRoles"] = spawnRoles;
		if (deleted is not null) d["deleted"] = deleted;
		return d;
	}

	static Dictionary<string, object?> Upsert(string key, long version, params Dictionary<string, object?>[] roles) =>
		new()
		{
			["projectKey"] = AgentDefMcpFixture.ProjectKey,
			["key"] = key,
			["version"] = version,
			["roles"] = roles,
		};

	// Seed a three-role roster and return its version.
	async Task<long> SeedAsync(string key)
	{
		var res = await Call("agent_def_upsert", Upsert(key, 0,
			Role("orchestrator", "orchestrator", ["mcp", "spawn"], notes: "ORIGINAL orchestrator prose",
				spawnAllowed: true, spawnRoles: ["worker"]),
			Role("worker", "worker", ["mcp"], notes: "original worker prose"),
			Role("explore", "utility", ["mcp"], notes: "original explore prose")));
		res.IsError.Should().NotBe(true, "seeding the roster must succeed: " + Text(res));
		return res.StructuredContent!.Value.GetProperty("version").GetInt64();
	}

	async Task<JsonElement> GetAsync(string key)
	{
		var res = await Call("agent_def_get", new Dictionary<string, object?>
		{
			["projectKey"] = AgentDefMcpFixture.ProjectKey,
			["key"] = key,
		});
		res.IsError.Should().NotBe(true, "reading the roster back must succeed: " + Text(res));
		return res.StructuredContent!.Value;
	}

	static JsonElement RoleOf(JsonElement doc, string slug) =>
		doc.GetProperty("roles").EnumerateArray().Single(r => r.GetProperty("slug").GetString() == slug);

	// ── 1. the PUBLISHED schema is typed ─────────────────────────────────────────────────────
	//
	// Asserted on the GENERATED schema, not on the C# record: a typed CLR parameter that failed to
	// export its shape would satisfy a type-level assertion and still leave every strict client —
	// and McpUnknownParameterFilter — with nothing to bind to, which is the whole defect.
	[Fact]
	public async Task InputSchema_Declares_PerFieldRoleProperties_AndRequiredSlug()
	{
		var schema = (await Tool(_fx.Mcp, "agent_def_upsert")).ProtocolTool.InputSchema;
		schema.TryGetProperty("properties", out var props).Should().BeTrue();

		props.TryGetProperty("definition", out _).Should().BeFalse(
			"the untyped whole-document blob is gone — its replacement is the typed `roles` array");

		props.TryGetProperty("roles", out var roles).Should().BeTrue("agent_def_upsert must take a typed `roles` array");
		roles.GetProperty("type").GetString().Should().Be("array");

		roles.TryGetProperty("items", out var items).Should().BeTrue("the array must declare its ITEM shape");
		items.TryGetProperty("properties", out var fields).Should().BeTrue(
			"a role item must publish per-field `properties` — [McpJsonShape(\"object\")] stamped only "
			+ "\"type\":\"object\", which is the bare-JsonElement shape spec/typed-mcp-inputs forbids");

		// Every field of the role contract, with its declared JSON type. A missing one is a field
		// McpUnknownParameterFilter would refuse and a caller could therefore never send.
		var declared = fields.EnumerateObject().ToDictionary(
			p => p.Name,
			p => p.Value.TryGetProperty("type", out var t)
				? (t.ValueKind == JsonValueKind.Array
					? string.Join(",", t.EnumerateArray().Select(x => x.GetString()))
					: t.GetString())
				: null);

		declared.Should().ContainKeys(
			"slug", "tier", "requiredCapabilities",
			"spawnAllowed", "spawnAllowedRoles", "escalationAvailable", "escalationTargets",
			"notes", "deleted");

		declared["slug"].Should().Be("string", "slug is the identity — [McpRequiredMember] drops its null arm");
		declared["tier"].Should().Be("string,null");
		declared["requiredCapabilities"].Should().Be("array,null");
		declared["spawnAllowed"].Should().Be("boolean,null");
		declared["spawnAllowedRoles"].Should().Be("array,null");
		declared["deleted"].Should().Be("boolean");

		items.TryGetProperty("required", out var required).Should().BeTrue(
			"the item schema must carry `required`, not only `properties`");
		required.EnumerateArray().Select(e => e.GetString()).Should().Contain("slug");
	}

	// ── 2. a typo inside a role is REFUSED, naming the field ─────────────────────────────────
	//
	// The sharpest point of the card. With no per-field schema AND full-replace semantics, a
	// mistyped field name was not an error and not a no-op — it was a DELETE: the document got
	// rewritten from a payload whose real field was missing.
	[Fact]
	public async Task TypoInsideARole_IsRefused_NamingTheField_NotSilentlyDropped()
	{
		const string key = "typo-role";
		var version = await SeedAsync(key);

		var typo = await Call("agent_def_upsert", Upsert(key, version,
			new Dictionary<string, object?> { ["slug"] = "worker", ["tierr"] = "worker" }));

		typo.IsError.Should().Be(true, "a field name the role schema does not carry must REFUSE the write");
		var text = Text(typo);
		text.Should().Contain("tierr", "the refusal must name the offending field, not just say 'invalid'");
		text.Should().Contain("tier", "and offer the nearest real field name");

		// And nothing was written: the roster is exactly what the seed left.
		var doc = await GetAsync(key);
		doc.GetProperty("roles").GetArrayLength().Should().Be(3);
		RoleOf(doc, "worker").GetProperty("tier").GetString().Should().Be("worker");
	}

	// ── 3. role.model is STILL rejected ──────────────────────────────────────────────────────
	//
	// This was the ONLY stated reason the hand-rolled JsonElement parse existed
	// ("// Parse from JsonElement so role.model is rejected on the wire shape"). Typing must not
	// lose it. The mechanism is now the surface-wide one — McpUnknownParameterFilter, whose one-hop
	// walk reaches a batch item's own fields — which is exactly why the role input is FLAT: a nested
	// spawn/escalation object would sit outside that reach.
	[Fact]
	public async Task RoleModel_IsStillRejected_ByName()
	{
		const string key = "model-role";
		var version = await SeedAsync(key);

		var withModel = await Call("agent_def_upsert", Upsert(key, version,
			new Dictionary<string, object?>
			{
				["slug"] = "worker",
				["tier"] = "worker",
				["model"] = "claude-opus-5",
			}));

		withModel.IsError.Should().Be(true,
			"a portable definition must not carry model binding — that axis is LOCAL");
		Text(withModel).Should().Contain("model");

		// Nothing smuggled through: the stored worker role carries no model of any kind.
		var doc = await GetAsync(key);
		RoleOf(doc, "worker").TryGetProperty("model", out _).Should().BeFalse();
	}

	// ── 4. editing ONE role leaves the others intact ─────────────────────────────────────────
	//
	// THE test that proves the merge. Under the old full-replace this call — a payload naming only
	// `worker` — left the document with one role and silently destroyed the other two.
	[Fact]
	public async Task EditingOneRole_LeavesTheOtherRolesIntact()
	{
		const string key = "merge-one";
		var version = await SeedAsync(key);

		var edit = await Call("agent_def_upsert", Upsert(key, version,
			Role("worker", notes: "REWRITTEN worker prose")));
		edit.IsError.Should().NotBe(true, Text(edit));
		edit.StructuredContent!.Value.GetProperty("changed").GetBoolean().Should().BeTrue();

		var doc = await GetAsync(key);
		doc.GetProperty("roles").EnumerateArray().Select(r => r.GetProperty("slug").GetString())
			.Should().BeEquivalentTo(["orchestrator", "worker", "explore"],
				"a role absent from the call is UNTOUCHED — absence is not deletion");

		RoleOf(doc, "worker").GetProperty("notes").GetString().Should().Be("REWRITTEN worker prose");
		// The fields of the edited role that the call did not mention survive too.
		RoleOf(doc, "worker").GetProperty("tier").GetString().Should().Be("worker");
		RoleOf(doc, "worker").GetProperty("requiredCapabilities").EnumerateArray()
			.Select(e => e.GetString()).Should().BeEquivalentTo(["mcp"]);
		// And the untouched roles keep everything, including their nested spawn block.
		RoleOf(doc, "orchestrator").GetProperty("notes").GetString().Should().Be("ORIGINAL orchestrator prose");
		RoleOf(doc, "orchestrator").GetProperty("spawn").GetProperty("allowed").GetBoolean().Should().BeTrue();
		RoleOf(doc, "explore").GetProperty("notes").GetString().Should().Be("original explore prose");
	}

	// ── 5. a role is deleted ONLY when deletion is explicit ──────────────────────────────────
	[Fact]
	public async Task ARoleIsDeleted_OnlyWhenDeletionIsExplicit()
	{
		const string key = "merge-delete";
		var version = await SeedAsync(key);

		// Not naming `explore` does NOT delete it — asserted here as well so this test cannot pass
		// by a change that simply deletes everything.
		var edit = await Call("agent_def_upsert", Upsert(key, version, Role("worker", notes: "still here")));
		edit.IsError.Should().NotBe(true, Text(edit));
		(await GetAsync(key)).GetProperty("roles").GetArrayLength().Should().Be(3);

		// deleted:true DOES.
		var version2 = edit.StructuredContent!.Value.GetProperty("version").GetInt64();
		var del = await Call("agent_def_upsert", Upsert(key, version2, Role("explore", deleted: true)));
		del.IsError.Should().NotBe(true, Text(del));

		var doc = await GetAsync(key);
		doc.GetProperty("roles").EnumerateArray().Select(r => r.GetProperty("slug").GetString())
			.Should().BeEquivalentTo(["orchestrator", "worker"],
				"the explicitly deleted role — and only it — is gone");
	}

	// ── 6. the version watermark still refuses a stale baseline ──────────────────────────────
	//
	// Fork decided: the watermark stays on the WHOLE document (not per role). Per-role watermarks
	// would need per-role rows — the temporal unit here is the document — and the merge is what
	// makes document-level CAS affordable: losing the race now costs a re-read plus YOUR role, not
	// the whole roster.
	[Fact]
	public async Task StaleBaseline_IsStillRefused()
	{
		const string key = "merge-cas";
		var version = await SeedAsync(key);

		var first = await Call("agent_def_upsert", Upsert(key, version, Role("worker", notes: "first edit")));
		first.IsError.Should().NotBe(true, Text(first));

		// The same (now stale) baseline, with a DIFFERENT payload.
		var stale = await Call("agent_def_upsert", Upsert(key, version, Role("worker", notes: "second edit")));
		stale.IsError.Should().Be(true, "a write against a superseded baseline must be refused, not merged blindly");
		Text(stale).Should().Contain("conflict").And.Contain("stale");

		// The refused write left nothing behind.
		RoleOf(await GetAsync(key), "worker").GetProperty("notes").GetString().Should().Be("first edit");
	}

	// ── 7. an empty batch is refused, not a silent no-op ─────────────────────────────────────
	// Surface-wide convention (tasks_upsert's "'nodes': empty batch — nothing to write"): under
	// MERGE semantics `roles:[]` would otherwise mean "change nothing", which is indistinguishable
	// from a payload that lost its items in transit.
	[Fact]
	public async Task EmptyRolesBatch_IsRefused()
	{
		const string key = "merge-empty";
		await SeedAsync(key);

		var empty = await Call("agent_def_upsert", new Dictionary<string, object?>
		{
			["projectKey"] = AgentDefMcpFixture.ProjectKey,
			["key"] = key,
			["version"] = 0,
			["roles"] = Array.Empty<object>(),
		});
		empty.IsError.Should().Be(true);
		Text(empty).Should().Contain("empty batch");
	}
}
