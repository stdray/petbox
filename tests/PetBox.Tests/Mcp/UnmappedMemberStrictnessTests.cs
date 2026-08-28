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
using PetBox.Tasks.Data;
using PetBox.Tests.Support;

namespace PetBox.Tests.Mcp;

// Card work/mcp-unmapped-member-disallow. The MCP serializer options carry
// UnmappedMemberHandling.Disallow (Program.cs, where mcpJson is built), so an argument member
// with no home on the target type is a REFUSAL at EVERY depth rather than a silent drop.
//
// WHY IT MATTERS, in one case: tasks_methodology_rules_upsert / _template_upsert REPLACE the
// whole document. A typo two hops inside `definition` used to bind the DEFAULT and wipe the real
// value, with no error and no warning — the same failure mode that wiped AutoWireFrom/Delivery/
// DefaultView/OutlineReveal in prod twice (work/mcp-rules-upsert-is-lossy). McpUnknownParameter-
// Filter could never see it: it walks the top level plus ONE hop into an array-of-objects
// parameter and fails open on everything else, `definition` (an object parameter) included.
//
// These tests drive the REAL server over the REAL MCP wire — the point of the card is that this
// reproduces here, not only on the in-memory bench the design was measured on. Each one is
// RED without the Program.cs change (or, for the round trip, without MethodologyWorkflowInput.
// Initial), which is the only reason they are worth their runtime.
//
// The boundaries matter as much as the refusals: an open Dictionary member and a JsonElement
// parameter have no closed member set, so neither is affected. Losing that would quietly break
// tasks_upsert `links` — hence a test each, in the same file, so they cannot drift apart.
public sealed class UnmappedMemberStrictnessFixture : IAsyncLifetime
{
	public const string ProjectKey = "unmap";
	const string ApiKey = "yb_key_unmap_agent";
	// agents:write is here for the JsonElement boundary (agent_def_upsert `definition`);
	// methodology:write for the template/rules document verbs, which are the deep-payload case.
	const string Scopes = "tasks:read,tasks:write,methodology:write,agents:read,agents:write";

	readonly string _baseDir;
	readonly WebApplicationFactory<Program> _factory;
	HttpClient _http = null!;

	public McpClient Mcp { get; private set; } = null!;

	public UnmappedMemberStrictnessFixture()
	{
		_baseDir = Path.Combine(Path.GetTempPath(), "petbox-unmap-" + Guid.NewGuid().ToString("N"));
		Environment.SetEnvironmentVariable("PETBOX_MASTER_KEY", "test-key-for-secrets");
		Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");

		_factory = new WebApplicationFactory<Program>()
			.WithWebHostBuilder(b =>
			{
				b.UseEnvironment("Testing");
				// Features:Tasks gates a registration at BUILD time (before builder.Build()), where
				// only UseSetting is visible — see Architecture/ConfigVisibilityContractTests.
				b.UseSetting("Features:Tasks", "true");
				b.ConfigureAppConfiguration((_, cfg) =>
				{
					cfg.AddInMemoryCollection(new Dictionary<string, string?>
					{
						["ConnectionStrings:PetBox"] = TestSchema.NewTempConnectionString(),
						["Features:Tasks"] = "true",
						["Host:BackgroundServices"] = "false",
					});
				});
				b.ConfigureServices(svc =>
				{
					var tasksFactory = svc.SingleOrDefault(d => d.ServiceType == typeof(IScopedDbFactory<TasksDb>));
					if (tasksFactory is not null) svc.Remove(tasksFactory);
					svc.AddSingleton<IScopedDbFactory<TasksDb>>(_ => new ScopedDbFactory<TasksDb>(
						Path.Combine(_baseDir, "tasks"), PetBox.Core.Settings.Scope.Project,
						cs => new TasksDb(TasksDb.CreateOptions(cs)), TestSchema.Tasks));
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
			await db.ApiKeys.Where(k => k.Key == ApiKey).DeleteAsync();
			await db.Projects.Where(p => p.Key == ProjectKey).DeleteAsync();
			await db.Workspaces.Where(w => w.Key == "test-unmap").DeleteAsync();
			await db.InsertAsync(new Workspace { Key = "test-unmap", Name = "Test", CreatedAt = DateTime.UtcNow });
			await db.InsertAsync(new Project { Key = ProjectKey, WorkspaceKey = "test-unmap", Name = "Unmapped" });
			await db.InsertAsync(new ApiKey { Key = ApiKey, ProjectKey = ProjectKey, Scopes = Scopes, CreatedAt = DateTime.UtcNow });
		}

		_http = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
		_http.DefaultRequestHeaders.Add("X-Api-Key", ApiKey);
		var transport = new HttpClientTransport(new HttpClientTransportOptions
		{
			Endpoint = new Uri(_http.BaseAddress!, "/mcp"),
			AdditionalHeaders = new Dictionary<string, string> { ["X-Api-Key"] = ApiKey },
		}, _http);
		Mcp = await McpTestClient.ConnectAsync(transport);
	}

	public async ValueTask DisposeAsync()
	{
		await Mcp.DisposeAsync();
		_http.Dispose();
		await _factory.DisposeAsync();
		TestDirs.CleanupOrDefer(_baseDir);
	}

	// Only the board catalog + the per-project tasks file carry per-test state here; templates and
	// agent definitions are keyed per test, so a full wipe between tests is unnecessary.
	public async Task ResetAsync()
	{
		using (var scope = _factory.Services.CreateScope())
		{
			using var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open();
			await db.TaskBoards.Where(b => b.ProjectKey == ProjectKey).DeleteAsync();
		}

		var tasksFactory = _factory.Services.GetRequiredService<IScopedDbFactory<TasksDb>>();
		using var tasks = tasksFactory.NewEnsuredConnection(ProjectKey);
		TestDataReset.WipeAllTables(tasks);
	}
}

public sealed class UnmappedMemberStrictnessTests(UnmappedMemberStrictnessFixture fx)
	: IClassFixture<UnmappedMemberStrictnessFixture>, IAsyncLifetime
{
	readonly UnmappedMemberStrictnessFixture _fx = fx;

	public ValueTask InitializeAsync() => new(_fx.ResetAsync());
	public ValueTask DisposeAsync() => ValueTask.CompletedTask;

	async Task<McpClientTool> Tool(string name) =>
		(await _fx.Mcp.ListToolsAsync()).First(t => t.Name == name);

	// The error envelope, decoded. Same reasoning as UnknownParameterFilterTests.Text: the raw
	// body carries '-escaped punctuation and a `detail` that repeats the message, so assert
	// on the decoded `error.message` alone. Falls back to the raw text for a success body, which
	// is what a failing assertion needs to print.
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
		catch (JsonException) { /* not an envelope */ }
		return raw;
	}

	static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

	// A minimal but REAL methodology document: one kind, one workflow, two statuses, one
	// transition. Built as raw JSON (not a typed record) so a test can plant a typo at an exact
	// depth — which is the whole subject here.
	const string OneKindDefinition = """
		{
		  "name": "probe",
		  "kinds": [
		    {
		      "kind": "work",
		      "quickAddAllowed": true,
		      "workflows": [
		        {
		          "types": ["chore"],
		          "statuses": [
		            { "slug": "open", "name": "Open", "kind": "open" },
		            { "slug": "done", "name": "Done", "kind": "terminalok" }
		          ],
		          "transitions": [ { "from": "open", "to": "done" } ]
		        }
		      ]
		    }
		  ]
		}
		""";

	static JsonElement DefinitionWith(string original, string replacement) =>
		Json(OneKindDefinition.Replace(original, replacement));

	// ── The refusals ────────────────────────────────────────────────────────────────────────

	// THE CARD'S CASE. `quickAddAlowed` sits TWO hops under the parameter
	// (definition -> kinds[] -> the field). McpUnknownParameterFilter cannot see it: `definition`
	// is an object, not an array-of-objects, so ItemSchema fails open and the walk stops at the
	// top level. Before this change the binder dropped the typo and bound quickAddAllowed's
	// default — on a full-document REPLACE verb that is a silent DELETE of the caller's value.
	[Fact]
	public async Task TypoTwoHopsInsideAWriteVerbPayload_IsRefused_AndNamesTheField()
	{
		var result = await (await Tool("tasks_methodology_template_upsert")).CallAsync(new Dictionary<string, object?>
		{
			["projectKey"] = UnmappedMemberStrictnessFixture.ProjectKey,
			["key"] = "probe-two-hops",
			["definition"] = DefinitionWith("\"quickAddAllowed\"", "\"quickAddAlowed\""),
		});

		result.IsError.Should().Be(true, "a typo two hops inside a REPLACE verb's document must not bind a default silently; got: {0}", Text(result));
		Text(result).Should().Contain("quickAddAlowed", "the refusal has to NAME the member the caller actually sent");
	}

	// Deeper still — four hops (definition -> kinds[] -> workflows[] -> statuses[] -> the field).
	// Same guarantee, no extra machinery: the type system carries it all the way down.
	[Fact]
	public async Task TypoFourHopsDown_IsRefused_AndNamesTheField()
	{
		var result = await (await Tool("tasks_methodology_template_upsert")).CallAsync(new Dictionary<string, object?>
		{
			["projectKey"] = UnmappedMemberStrictnessFixture.ProjectKey,
			["key"] = "probe-four-hops",
			["definition"] = DefinitionWith("\"name\": \"Open\"", "\"nmae\": \"Open\""),
		});

		result.IsError.Should().Be(true, "got: {0}", Text(result));
		Text(result).Should().Contain("nmae");
	}

	// ONE hop under an OBJECT-valued parameter — also outside the filter's reach for the same
	// reason (ItemSchema requires `items.properties`, an object parameter has neither). The rest
	// of the document is VALID on purpose: without the strictness this call SUCCEEDS with
	// strictMode quietly left at its default, which is the failure being fixed — not some other
	// validator catching the document for an unrelated reason.
	[Fact]
	public async Task TypoOneHopInsideAnObjectParameter_IsRefused_AndNamesTheField()
	{
		var result = await (await Tool("tasks_methodology_template_upsert")).CallAsync(new Dictionary<string, object?>
		{
			["projectKey"] = UnmappedMemberStrictnessFixture.ProjectKey,
			["key"] = "probe-one-hop",
			["definition"] = DefinitionWith("\"name\": \"probe\",", "\"name\": \"probe\", \"strictModee\": true,"),
		});

		result.IsError.Should().Be(true, "got: {0}", Text(result));
		Text(result).Should().Contain("strictModee");
	}

	// An unknown TOP-LEVEL argument key stays refused. McpUnknownParameterFilter reaches this depth
	// and runs before next(), so its richer message is the one that wins — asserted here so the
	// role split stays visible: the filter is the message, the type is the guarantee.
	[Fact]
	public async Task UnknownTopLevelArgumentKey_IsRefused()
	{
		await (await Tool("tasks_board_create")).CallAsync(new Dictionary<string, object?>
		{
			["projectKey"] = UnmappedMemberStrictnessFixture.ProjectKey,
			["board"] = "work",
			["kind"] = "work",
		});

		var result = await (await Tool("tasks_search")).CallAsync(new Dictionary<string, object?>
		{
			["projectKey"] = UnmappedMemberStrictnessFixture.ProjectKey,
			["board"] = "work",
			["zzz_nonexistent"] = 1,
		});

		result.IsError.Should().Be(true, "got: {0}", Text(result));
		Text(result).Should().Contain("zzz_nonexistent");
	}

	// ── The boundaries: what must stay OPEN ─────────────────────────────────────────────────

	// THE REGRESSION THAT WOULD QUIETLY BREAK tasks_upsert. `nodes[].links` is
	// Dictionary<string, LinkRefs> keyed by RELATION KIND — an open member set by design (a
	// project declares its own kinds). UnmappedMemberHandling applies to types with a CLOSED
	// member set only, so a dictionary is untouched; if that ever stopped being true, every
	// links-carrying write would start failing and this test is how we'd know.
	[Fact]
	public async Task OpenDictionaryMember_StillAcceptsArbitraryKeys()
	{
		await (await Tool("tasks_board_create")).CallAsync(new Dictionary<string, object?>
		{
			["projectKey"] = UnmappedMemberStrictnessFixture.ProjectKey,
			["board"] = "work",
			["kind"] = "work",
		});
		var upsert = await Tool("tasks_upsert");
		await upsert.CallAsync(new Dictionary<string, object?>
		{
			["projectKey"] = UnmappedMemberStrictnessFixture.ProjectKey,
			["board"] = "work",
			["nodes"] = Json("""[{ "key": "target", "title": "Target", "type": "chore" }]"""),
		});

		var result = await upsert.CallAsync(new Dictionary<string, object?>
		{
			["projectKey"] = UnmappedMemberStrictnessFixture.ProjectKey,
			["board"] = "work",
			["nodes"] = Json("""[{ "key": "source", "title": "Source", "type": "chore", "links": { "blocks": "target" } }]"""),
		});

		result.IsError.Should().NotBe(true, "an open dictionary member has no closed member set to violate; got: {0}", Text(result));
		// Belt and braces: even a REFUSAL for some unrelated domain reason must never be an
		// unmapped-member one — that is the shape of the regression this test exists for.
		Text(result).Should().NotContain("could not be mapped");
	}

	// A `JsonElement` parameter (agent_def_upsert `definition`) is a raw document by design —
	// AgentDefinitionJson parses it with its OWN options and stores unknown properties verbatim
	// for forward-compat. Nothing about the MCP options' strictness may reach into it.
	[Fact]
	public async Task JsonElementParameter_StillAcceptsArbitraryContent()
	{
		var result = await (await Tool("agent_def_upsert")).CallAsync(new Dictionary<string, object?>
		{
			["projectKey"] = UnmappedMemberStrictnessFixture.ProjectKey,
			["key"] = "probe-roster",
			["definition"] = Json("""
				{
				  "name": "probe-roster",
				  "roles": [ { "slug": "worker", "tier": "worker", "requiredCapabilities": [], "zzz_future_field": 1 } ],
				  "zzz_unknown_root_field": { "anything": ["at", "all"] }
				}
				"""),
		});

		result.IsError.Should().NotBe(true, "a JsonElement parameter carries an OPAQUE document; got: {0}", Text(result));
		Text(result).Should().NotContain("could not be mapped");
	}

	// ── The round trip the strictness would otherwise break ─────────────────────────────────

	// THE `initial` FIX. tasks_methodology_template_get EMITS `workflows[].initial`
	// (MethodologyWorkflowBlockView, derived from Statuses[0]), and TasksTools documents the
	// document as "copyable into def_upsert/template_upsert without reshaping". That worked only
	// because the binder silently dropped the field. Under Disallow it is a refusal unless
	// MethodologyWorkflowInput declares `Initial` — so this test is RED on the strictness change
	// alone and green only with both halves of the commit.
	//
	// A PASTE, deliberately: the document members are lifted VERBATIM out of the read result, not
	// rebuilt from typed records — rebuilding would test the test, not the round trip.
	[Fact]
	public async Task TemplateGet_PastedStraightBackIntoTemplateUpsert_StillRoundTrips()
	{
		var read = await (await Tool("tasks_methodology_template_get")).CallAsync(new Dictionary<string, object?>
		{
			["projectKey"] = UnmappedMemberStrictnessFixture.ProjectKey,
			["key"] = "quartet",
		});
		read.IsError.Should().NotBe(true, "fixture read must succeed; got: {0}", Text(read));
		var doc = read.StructuredContent!.Value;
		doc.TryGetProperty("kinds", out var kinds).Should().BeTrue("the read document must carry the kinds this test pastes back");
		kinds.GetArrayLength().Should().BeGreaterThan(0);

		// The document members template_upsert's `definition` declares, taken as they were read.
		var definition = new Dictionary<string, JsonElement>();
		foreach (var member in new[] { "name", "kinds", "linkKinds", "tagAxes", "strictMode" })
			if (doc.TryGetProperty(member, out var value))
				definition[member] = value;

		var result = await (await Tool("tasks_methodology_template_upsert")).CallAsync(new Dictionary<string, object?>
		{
			["projectKey"] = UnmappedMemberStrictnessFixture.ProjectKey,
			["key"] = "quartet-copy",
			["definition"] = Json(JsonSerializer.Serialize(definition)),
		});

		result.IsError.Should().NotBe(true,
			"the documented round trip (read the document, paste it back unreshaped) must survive the strictness; got: {0}",
			Text(result));
	}

	// THE ACCEPTED COST, pinned so nobody meets it only in production. The read result's ENVELOPE
	// (`key`, `source`, `version`, `created`, `updated`) is not part of `definition` —
	// MethodologyDefInput declares name/kinds/linkKinds/tagAxes/strictMode and nothing else. Before
	// the strictness a caller could shovel the WHOLE result object in and the envelope was quietly
	// dropped; now it is refused. That is right in principle (a read row is not a write payload,
	// the same hard edge McpUnknownParameterFilter already draws at the top level) but it IS a
	// call that used to work, so it is written down here rather than left to be discovered.
	//
	// The tolerant twin still exists and is deliberately NOT touched: MethodologyWire.ParseDocument
	// — the admin methodology editor's paste path — parses with its own WireOptions and still
	// ignores the envelope. MethodologyEditorViewsTests.ToJson_Inlines...AndRoundTrips depends on
	// that tolerance today (it parses a projected document carrying `defined`/`version`).
	[Fact]
	public async Task WholeReadResultPastedIntoDefinition_IsRefused_NamingTheEnvelopeField()
	{
		var read = await (await Tool("tasks_methodology_template_get")).CallAsync(new Dictionary<string, object?>
		{
			["projectKey"] = UnmappedMemberStrictnessFixture.ProjectKey,
			["key"] = "simple",
		});
		read.IsError.Should().NotBe(true, "fixture read must succeed; got: {0}", Text(read));

		var result = await (await Tool("tasks_methodology_template_upsert")).CallAsync(new Dictionary<string, object?>
		{
			["projectKey"] = UnmappedMemberStrictnessFixture.ProjectKey,
			["key"] = "simple-whole-envelope",
			["definition"] = read.StructuredContent!.Value, // the WHOLE result, envelope included
		});

		result.IsError.Should().Be(true, "the read envelope is not part of the write document; got: {0}", Text(result));
	}

	// The `initial` DECISION, pinned. It is VALIDATED against the block's own statuses, not
	// honoured as a declaration: honouring would mean reordering `statuses` behind the caller's
	// back to keep Statuses[0] == Initial. A disagreement is therefore named, in both directions.
	[Fact]
	public async Task WorkflowInitial_DisagreeingWithTheFirstDeclaredStatus_IsRefused_AndNamesBoth()
	{
		var result = await (await Tool("tasks_methodology_template_upsert")).CallAsync(new Dictionary<string, object?>
		{
			["projectKey"] = UnmappedMemberStrictnessFixture.ProjectKey,
			["key"] = "probe-initial-disagrees",
			["definition"] = DefinitionWith("\"types\": [\"chore\"],", "\"types\": [\"chore\"], \"initial\": \"done\","),
		});

		result.IsError.Should().Be(true, "a document claiming a different initial than its own statuses[0] is a caller bug, not something to fix silently; got: {0}", Text(result));
		Text(result).Should().Contain("done").And.Contain("open");
	}

	// The other half of the same decision: `initial` naming a status the block never declares is
	// refused too, rather than being ignored as decoration.
	[Fact]
	public async Task WorkflowInitial_NamingAnUndeclaredStatus_IsRefused()
	{
		var result = await (await Tool("tasks_methodology_template_upsert")).CallAsync(new Dictionary<string, object?>
		{
			["projectKey"] = UnmappedMemberStrictnessFixture.ProjectKey,
			["key"] = "probe-initial-unknown",
			["definition"] = DefinitionWith("\"types\": [\"chore\"],", "\"types\": [\"chore\"], \"initial\": \"nosuchstatus\","),
		});

		result.IsError.Should().Be(true, "got: {0}", Text(result));
		Text(result).Should().Contain("nosuchstatus");
	}

	// A document that agrees with itself — the normal case, and what every read document is —
	// passes through untouched.
	[Fact]
	public async Task WorkflowInitial_AgreeingWithTheFirstDeclaredStatus_IsAccepted()
	{
		var result = await (await Tool("tasks_methodology_template_upsert")).CallAsync(new Dictionary<string, object?>
		{
			["projectKey"] = UnmappedMemberStrictnessFixture.ProjectKey,
			["key"] = "probe-initial-agrees",
			["definition"] = DefinitionWith("\"types\": [\"chore\"],", "\"types\": [\"chore\"], \"initial\": \"open\","),
		});

		result.IsError.Should().NotBe(true, "got: {0}", Text(result));
	}

	// ── The schema side effect strict clients see ───────────────────────────────────────────

	// Disallow makes System.Text.Json's schema exporter emit `additionalProperties:false` on every
	// CLOSED object node, so a strict client catches the same typo before the call leaves — and it
	// must NOT appear on the open dictionary, which would be the schema-level face of the
	// tasks_upsert `links` regression.
	[Fact]
	public async Task InputSchema_ClosesTheObjectNodes_AndLeavesTheOpenDictionaryOpen()
	{
		var properties = (await Tool("tasks_upsert")).ProtocolTool.InputSchema.GetProperty("properties");
		var item = properties.GetProperty("nodes").GetProperty("items");

		Closed(item).Should().BeTrue("`nodes[]` is a closed record (TaskNodeInput) — a strict client should reject an unknown item field locally");

		var links = item.GetProperty("properties").GetProperty("links");
		Closed(Unwrap(links)).Should().BeFalse("`links` is Dictionary<string, LinkRefs>, keyed by relation kind — closing it would reject every project-declared kind");
	}

	// The same options generate the OUTPUT schema (McpServerTool.Create takes one
	// SerializerOptions for both), so results are declared closed too. Recorded because it is the
	// client-visible half: a strict client validates structuredContent against this schema, and
	// `additionalProperties:false` means a result carrying a field the schema omits is now its
	// error, not its shrug. Every tool's structuredContent is validated against its own declared
	// outputSchema by McpOutputSchemaConformanceTests, which is what says the results conform.
	[Fact]
	public async Task OutputSchema_IsAlsoClosed()
	{
		var schema = (await Tool("tasks_search")).ProtocolTool.OutputSchema;

		schema.Should().NotBeNull();
		Closed(schema!.Value).Should().BeTrue("a strict client validates the result against this schema");
	}

	// `additionalProperties: false` on this node.
	static bool Closed(JsonElement schema) =>
		schema.ValueKind == JsonValueKind.Object
		&& schema.TryGetProperty("additionalProperties", out var extra)
		&& extra.ValueKind == JsonValueKind.False;

	// A nullable member rides as an `["T","null"]`-style union or an anyOf; take the object branch
	// so the assertion is about the dictionary schema itself, not its nullability wrapper.
	static JsonElement Unwrap(JsonElement schema)
	{
		if (schema.ValueKind == JsonValueKind.Object
			&& schema.TryGetProperty("anyOf", out var branches)
			&& branches.ValueKind == JsonValueKind.Array)
			foreach (var branch in branches.EnumerateArray())
				if (branch.ValueKind == JsonValueKind.Object && branch.TryGetProperty("properties", out _))
					return branch;
		return schema;
	}
}
