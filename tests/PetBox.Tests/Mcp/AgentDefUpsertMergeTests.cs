using System.Text.Json;
using System.Text.Json.Nodes;
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
//     forbids exactly this), so nothing could compare a field name against a vocabulary.
//   * the verb was a full-document REPLACE. Combine the two and a mistyped field name did not fail
//     — it silently DELETED the field it meant to set, and a caller resending four of six roles
//     silently deleted the other two.
// Typing alone would have left the whole document travelling for a one-role edit
// (spec/write-cost-follows-change, measured 10 520 B on six roles); merging alone would have left
// the typo unpoliced. Hence one change.
//
// SHAPE NOTE, because it was got wrong once. `definition` is ONE typed NESTED object mirroring
// agent_def_get's output. An earlier draft destructured it into `roles[]` + `name` and flattened
// the role (`spawn:{allowed}` → `spawnAllowed`) so every field would sit inside
// McpUnknownParameterFilter's one-hop walk. work/mcp-unmapped-member-disallow removed that
// constraint — UnmappedMemberHandling.Disallow refuses an unmapped member from the TYPE, at any
// depth — and the flattening it forced does not scale (a third field on `spawn` would have become
// a third top-level parameter). PastedGetOutput_RoundTrips below is the test that pins the shape:
// it fails against ANY reshaping, flattening included.
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

	// A role in the SAME nested shape agent_def_get emits.
	static JsonObject Role(
		string slug, string? tier = null, string[]? caps = null, string? notes = null,
		bool? spawnAllowed = null, string[]? spawnRoles = null, bool? deleted = null)
	{
		var d = new JsonObject { ["slug"] = slug };
		if (tier is not null) d["tier"] = tier;
		if (caps is not null) d["requiredCapabilities"] = new JsonArray([.. caps.Select(c => (JsonNode?)JsonValue.Create(c))]);
		if (notes is not null) d["notes"] = notes;
		if (spawnAllowed is not null || spawnRoles is not null)
		{
			var spawn = new JsonObject();
			if (spawnAllowed is not null) spawn["allowed"] = spawnAllowed;
			if (spawnRoles is not null)
				spawn["allowedRoles"] = new JsonArray([.. spawnRoles.Select(r => (JsonNode?)JsonValue.Create(r))]);
			d["spawn"] = spawn;
		}
		if (deleted is not null) d["deleted"] = deleted;
		return d;
	}

	// NB: named apart from UpsertDoc on purpose — an overload pair (params JsonObject[] / JsonObject)
	// silently binds a SINGLE role to the document overload, which is how the first run of this file
	// produced eight refusals reading "'slug' could not be mapped to AgentDefDocumentInput".
	static Dictionary<string, object?> Upsert(string key, long version, params JsonObject[] roles) =>
		UpsertDoc(key, version, new JsonObject { ["roles"] = new JsonArray([.. roles.Cast<JsonNode?>()]) });

	static Dictionary<string, object?> UpsertDoc(string key, long version, JsonObject definition) =>
		new()
		{
			["projectKey"] = AgentDefMcpFixture.ProjectKey,
			["key"] = key,
			["version"] = version,
			["definition"] = definition,
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

	// ── 1. the PUBLISHED schema is typed, and typed all the way DOWN ──────────────────────────
	//
	// Asserted on the GENERATED schema, not on the C# record: a typed CLR parameter that failed to
	// export its shape would satisfy a type-level assertion and still leave every strict client
	// with nothing to bind to, which is the whole defect.
	[Fact]
	public async Task InputSchema_Declares_TheNestedDocument_PerField()
	{
		var schema = (await Tool(_fx.Mcp, "agent_def_upsert")).ProtocolTool.InputSchema;
		schema.TryGetProperty("properties", out var props).Should().BeTrue();

		props.TryGetProperty("definition", out var definition).Should().BeTrue();
		definition.TryGetProperty("properties", out var docFields).Should().BeTrue(
			"[McpJsonShape(\"object\")] stamped only \"type\":\"object\" — the bare-JsonElement shape "
			+ "spec/typed-mcp-inputs forbids. The document must publish per-field `properties`.");
		docFields.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(["name", "roles"],
			"the document is exactly what agent_def_get emits: a name and the roles");

		definition.TryGetProperty("required", out var docRequired).Should().BeTrue(
			"the document schema must carry `required`, not only `properties`");
		docRequired.EnumerateArray().Select(e => e.GetString()).Should().Contain("roles");

		var roles = docFields.GetProperty("roles");
		roles.TryGetProperty("items", out var items).Should().BeTrue("the roles array must declare its ITEM shape");
		items.TryGetProperty("properties", out var fields).Should().BeTrue();

		var declared = fields.EnumerateObject().ToDictionary(
			p => p.Name,
			p => p.Value.TryGetProperty("type", out var t)
				? (t.ValueKind == JsonValueKind.Array
					? string.Join(",", t.EnumerateArray().Select(x => x.GetString()))
					: t.GetString())
				: null);

		declared.Should().ContainKeys(
			"slug", "tier", "requiredCapabilities", "spawn", "escalation", "notes", "deleted");
		declared.Should().NotContainKeys("spawnAllowed", "spawnAllowedRoles", "escalationAvailable", "escalationTargets",
			"the role is NESTED, mirroring the read shape — the flattened draft was rejected");
		declared["slug"].Should().Be("string", "slug is the identity: a ctor param with no default is `required`");
		declared["tier"].Should().Be("string,null");
		declared["deleted"].Should().Be("boolean");

		items.TryGetProperty("required", out var roleRequired).Should().BeTrue(
			"the role schema must carry `required`, not only `properties`");
		roleRequired.EnumerateArray().Select(e => e.GetString()).Should().Contain("slug");

		// TWO hops down: the nested block publishes its own fields. This is the level the earlier
		// flattened draft could not express and where the strictness now polices a typo.
		var spawn = fields.GetProperty("spawn");
		spawn.TryGetProperty("properties", out var spawnFields).Should().BeTrue(
			"`spawn` must be a typed nested object, not an opaque one");
		spawnFields.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(["allowed", "allowedRoles"]);
		fields.GetProperty("escalation").GetProperty("properties").EnumerateObject().Select(p => p.Name)
			.Should().BeEquivalentTo(["available", "targets"]);
	}

	// ── 2. a typo inside the NESTED spawn object is REFUSED, naming the field ─────────────────
	//
	// The sharpest point of the rework. `spawn.allwed` sits TWO hops under the parameter —
	// exactly where McpUnknownParameterFilter cannot reach (it walks the top level plus one hop
	// into an array-of-objects, and `definition` is an object). Under the old binder it bound
	// Allowed=false and SILENTLY CLEARED the flag. UnmappedMemberHandling.Disallow
	// (work/mcp-unmapped-member-disallow) refuses it from the TYPE instead — which is what makes
	// the nested shape safe and the flattened draft unnecessary.
	[Fact]
	public async Task TypoInsideTheNestedSpawnBlock_IsRefused_NamingTheField()
	{
		const string key = "typo-spawn";
		var version = await SeedAsync(key);

		var typo = await Call("agent_def_upsert", Upsert(key, version,
			new JsonObject
			{
				["slug"] = "orchestrator",
				["spawn"] = new JsonObject { ["allwed"] = false },
			}));

		typo.IsError.Should().Be(true,
			"a member the nested spawn schema does not carry must REFUSE the write, not clear the flag");
		Text(typo).Should().Contain("allwed", "the refusal must name the offending member");

		// And nothing was written: the orchestrator still spawns.
		RoleOf(await GetAsync(key), "orchestrator").GetProperty("spawn").GetProperty("allowed")
			.GetBoolean().Should().BeTrue();
	}

	// The same guarantee one hop up, on the role itself.
	[Fact]
	public async Task TypoOnTheRole_IsRefused_NamingTheField()
	{
		const string key = "typo-role";
		var version = await SeedAsync(key);

		var typo = await Call("agent_def_upsert", Upsert(key, version,
			new JsonObject { ["slug"] = "worker", ["tierr"] = "worker" }));

		typo.IsError.Should().Be(true, "a field name the role schema does not carry must REFUSE the write");
		Text(typo).Should().Contain("tierr", "the refusal must name the offending field, not just say 'invalid'");

		var doc = await GetAsync(key);
		doc.GetProperty("roles").GetArrayLength().Should().Be(3);
		RoleOf(doc, "worker").GetProperty("tier").GetString().Should().Be("worker");
	}

	// ── 3. role.model is STILL rejected ──────────────────────────────────────────────────────
	//
	// This was the ONLY stated reason the hand-rolled JsonElement parse existed
	// ("// Parse from JsonElement so role.model is rejected on the wire shape"). Typing must not
	// lose it. The mechanism is now the TYPE — `model` has no member on AgentDefRoleInput, so the
	// binder refuses it by name — and AgentDefinitionJson.Parse still re-checks the merged
	// document, which is what keeps the REST path covered.
	[Fact]
	public async Task RoleModel_IsStillRejected_ByName()
	{
		const string key = "model-role";
		var version = await SeedAsync(key);

		var withModel = await Call("agent_def_upsert", Upsert(key, version,
			new JsonObject
			{
				["slug"] = "worker",
				["tier"] = "worker",
				["model"] = "claude-opus-5",
			}));

		withModel.IsError.Should().Be(true,
			"a portable definition must not carry model binding — that axis is LOCAL");
		Text(withModel).Should().Contain("model");

		// Nothing smuggled through: the stored worker role carries no model of any kind.
		RoleOf(await GetAsync(key), "worker").TryGetProperty("model", out _).Should().BeFalse();
	}

	// …and one level down too, where the old hand-rolled walk also looked.
	[Fact]
	public async Task ModelInsideANestedBlock_IsAlsoRejected()
	{
		const string key = "model-nested";
		var version = await SeedAsync(key);

		var withModel = await Call("agent_def_upsert", Upsert(key, version,
			new JsonObject
			{
				["slug"] = "worker",
				["spawn"] = new JsonObject { ["allowed"] = true, ["model"] = "claude-opus-5" },
			}));

		withModel.IsError.Should().Be(true);
		Text(withModel).Should().Contain("model");
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

	// Either half of the nested block may be sent alone: the other is read off the stored block, so
	// `spawn:{allowedRoles:[…]}` does not silently clear `allowed`. This is the behaviour the
	// flattened draft claimed only a flat shape could give.
	[Fact]
	public async Task NestedBlock_EitherHalfAlone_KeepsTheOther()
	{
		const string key = "merge-halves";
		var version = await SeedAsync(key);

		var list = await Call("agent_def_upsert", Upsert(key, version,
			Role("orchestrator", spawnRoles: ["worker", "explore"])));
		list.IsError.Should().NotBe(true, Text(list));
		var spawn = RoleOf(await GetAsync(key), "orchestrator").GetProperty("spawn");
		spawn.GetProperty("allowed").GetBoolean().Should().BeTrue("sending only the list must not clear the flag");
		spawn.GetProperty("allowedRoles").EnumerateArray().Select(e => e.GetString())
			.Should().BeEquivalentTo(["worker", "explore"]);

		var v2 = list.StructuredContent!.Value.GetProperty("version").GetInt64();
		var flag = await Call("agent_def_upsert", Upsert(key, v2, Role("orchestrator", spawnAllowed: false)));
		flag.IsError.Should().NotBe(true, Text(flag));
		var after = RoleOf(await GetAsync(key), "orchestrator").GetProperty("spawn");
		after.GetProperty("allowed").GetBoolean().Should().BeFalse();
		after.GetProperty("allowedRoles").EnumerateArray().Select(e => e.GetString())
			.Should().BeEquivalentTo(["worker", "explore"], "sending only the flag must not clear the list");
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

	// ── 6. agent_def_get output pastes straight back ─────────────────────────────────────────
	//
	// THE SHAPE REQUIREMENT, and the reason the flattened `roles[]`+`name` draft was rejected. The
	// definition sent here is the READ RESULT with only its temporal envelope (key/version/created/
	// updated) removed — every remaining property forwarded VERBATIM, no renaming, no unnesting.
	// Two things fail this test rather than one:
	//   * a reshaped input (flat `spawnAllowed`, or `roles`+`name` hoisted to top level) — the
	//     pasted `spawn:{…}` object is then an unmapped member and the call is refused;
	//   * a field the VIEW emits that the INPUT has no slot for — the `workflows[].initial` class
	//     of bug (work/mcp-unmapped-member-disallow), which under the strictness turns a documented
	//     read-modify-write cycle into a refusal.
	// And the round trip is a NO-OP: an unchanged document pasted back reports changed:false rather
	// than minting a phantom revision.
	[Fact]
	public async Task PastedGetOutput_RoundTrips_WithNoReshaping()
	{
		const string key = "paste-back";
		await SeedAsync(key);

		var read = await GetAsync(key);
		var definition = new JsonObject();
		foreach (var prop in read.EnumerateObject())
		{
			// The temporal envelope is the ONLY thing a caller drops: key addresses the document and
			// version is its own argument, created/updated are server clocks.
			if (prop.Name is "key" or "version" or "created" or "updated") continue;
			definition[prop.Name] = JsonNode.Parse(prop.Value.GetRawText());
		}

		var pasted = await Call("agent_def_upsert",
			UpsertDoc(key, read.GetProperty("version").GetInt64(), definition));

		pasted.IsError.Should().NotBe(true,
			"what agent_def_get emits must be accepted by agent_def_upsert verbatim — read → edit → "
			+ "write is a paste, not a reshaping exercise: " + Text(pasted));
		pasted.StructuredContent!.Value.GetProperty("changed").GetBoolean().Should().BeFalse(
			"an unedited paste-back changes nothing and must not mint a revision");

		// And the roster survived the round trip intact, nested blocks included.
		var after = await GetAsync(key);
		after.GetProperty("roles").GetArrayLength().Should().Be(3);
		RoleOf(after, "orchestrator").GetProperty("spawn").GetProperty("allowed").GetBoolean().Should().BeTrue();
		RoleOf(after, "orchestrator").GetProperty("spawn").GetProperty("allowedRoles").EnumerateArray()
			.Select(e => e.GetString()).Should().BeEquivalentTo(["worker"]);
	}

	// The same paste with ONE field edited — the actual read-modify-write cycle, end to end.
	[Fact]
	public async Task PastedGetOutput_WithOneFieldEdited_AppliesOnlyThatEdit()
	{
		const string key = "paste-edit";
		await SeedAsync(key);

		var read = await GetAsync(key);
		var definition = new JsonObject
		{
			["name"] = read.GetProperty("name").GetString(),
			["roles"] = JsonNode.Parse(read.GetProperty("roles").GetRawText()),
		};
		((JsonArray)definition["roles"]!).OfType<JsonObject>()
			.Single(r => (string?)r["slug"] == "orchestrator")["notes"] = "EDITED in place";

		var res = await Call("agent_def_upsert",
			UpsertDoc(key, read.GetProperty("version").GetInt64(), definition));
		res.IsError.Should().NotBe(true, Text(res));

		var after = await GetAsync(key);
		RoleOf(after, "orchestrator").GetProperty("notes").GetString().Should().Be("EDITED in place");
		RoleOf(after, "worker").GetProperty("notes").GetString().Should().Be("original worker prose");
		RoleOf(after, "explore").GetProperty("notes").GetString().Should().Be("original explore prose");
	}

	// ── 7. the version watermark still refuses a stale baseline ──────────────────────────────
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

	// ── 8. an empty batch is refused, not a silent no-op ─────────────────────────────────────
	//
	// Surface-wide convention (tasks_upsert's "'nodes': empty batch — nothing to write"): under
	// MERGE semantics `roles:[]` would otherwise mean "change nothing", which is indistinguishable
	// from a payload that lost its items in transit.
	//
	// Lives HERE rather than in EmptyBatchRejectionTests because that file's sentinel discovers
	// candidates by shape — a TOP-LEVEL array-of-objects parameter — and this verb's batch is one
	// level down, inside `definition`. Registering it there would fail the sentinel's own reverse
	// check ("still matches the discovery heuristic"); see the note left in that file.
	[Fact]
	public async Task EmptyRolesBatch_IsRefused()
	{
		const string key = "merge-empty";
		await SeedAsync(key);

		var empty = await Call("agent_def_upsert", UpsertDoc(key, 0, new JsonObject { ["roles"] = new JsonArray() }));
		empty.IsError.Should().Be(true);
		Text(empty).Should().Contain("empty batch");
	}
}
