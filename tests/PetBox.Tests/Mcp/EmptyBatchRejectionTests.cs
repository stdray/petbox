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

namespace PetBox.Tests.Mcp;

// card work/mcp-surface-naming-cleanup wave 4 (fix/mcp-empty-batch-rule): an empty EFFECTIVE
// batch on a write verb is almost always a client bug (a filter emptied a list and the call went
// out anyway) — it must be REJECTED with a uniform message naming the real batch parameter, never
// silently reported as applied:true (config_binding_upsert's old bug) or applied:false with no
// conflict to explain why (relations_create's old bug on items:[]).
//
// Covers the five guarded verbs (tasks_upsert/nodes, memory_upsert/entries, comments_upsert/items,
// config_binding_upsert/items, relations_create/items), the ONE documented exception
// (session_append: a batch filtered down to zero content is an idempotent cursor no-op, not an
// error), and the honest partial-refusal case that must NOT be swept into this rule
// (relations_create atomic:false with every item invalid — a real conflicts[] answer, not an
// empty-batch error).
public sealed class EmptyBatchRejectionFixture : IAsyncLifetime
{
	public const string ProjectKey = "ebatch";
	public const string WorkspaceKey = "ebatch-ws";
	public const string ApiKey = "yb_key_ebatch_agent";
	const string Scopes = "tasks:read,tasks:write,memory:read,memory:write,admin:provision";

	readonly string _baseDir;
	readonly WebApplicationFactory<Program> _factory;
	HttpClient _http = null!;

	public McpClient Mcp { get; private set; } = null!;
	public IReadOnlyDictionary<string, McpClientTool> Tools { get; private set; } = null!;

	public EmptyBatchRejectionFixture()
	{
		_baseDir = Path.Combine(Path.GetTempPath(), "petbox-ebatch-" + Guid.NewGuid().ToString("N"));
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
						["Features:Tasks"] = "true",
						["Features:Memory"] = "true",
						["Features:Config"] = "true",
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
			await db.Workspaces.Where(w => w.Key == WorkspaceKey).DeleteAsync();
			await db.InsertAsync(new Workspace { Key = WorkspaceKey, Name = "EBatch WS", CreatedAt = DateTime.UtcNow });
			await db.InsertAsync(new Project { Key = ProjectKey, WorkspaceKey = WorkspaceKey, Name = "EBatch" });
			await db.InsertAsync(new ApiKey { Key = ApiKey, ProjectKey = ProjectKey, Scopes = Scopes, CreatedAt = DateTime.UtcNow });
		}

		_http = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
		_http.DefaultRequestHeaders.Add("X-Api-Key", ApiKey);
		var transport = new HttpClientTransport(new HttpClientTransportOptions
		{
			Endpoint = new Uri(_http.BaseAddress!, "/mcp"),
			AdditionalHeaders = new Dictionary<string, string> { ["X-Api-Key"] = ApiKey },
		}, _http);
		Mcp = await McpClient.CreateAsync(transport, cancellationToken: default);
		Tools = (await Mcp.ListToolsAsync()).ToDictionary(t => t.Name);

		// tasks_upsert/comments_upsert/relations_create all need a real board to attach to.
		await Tools["tasks_board_create"].CallAsync(ToArgs(new { projectKey = ProjectKey, board = "work", kind = "simple" }));
		await Tools["tasks_upsert"].CallAsync(ToArgs(new
		{
			projectKey = ProjectKey,
			board = "work",
			nodes = new[] { new { key = "anchor", title = "Anchor node", body = "for comments/relations", version = 0 } },
		}));
	}

	public async ValueTask DisposeAsync()
	{
		await Mcp.DisposeAsync();
		_http.Dispose();
		await _factory.DisposeAsync();
		TestDirs.CleanupOrDefer(_baseDir);
	}

	internal static Dictionary<string, object?> ToArgs(object o) =>
		JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(o))!
			.ToDictionary(kv => kv.Key, kv => (object?)((JsonElement)kv.Value!));
}

public sealed class EmptyBatchRejectionTests : IClassFixture<EmptyBatchRejectionFixture>
{
	readonly EmptyBatchRejectionFixture _fx;
	public EmptyBatchRejectionTests(EmptyBatchRejectionFixture fx) => _fx = fx;

	const string Proj = EmptyBatchRejectionFixture.ProjectKey;
	const string Ws = EmptyBatchRejectionFixture.WorkspaceKey;

	async Task<CallToolResult> Call(string tool, object args) => await _fx.Tools[tool].CallAsync(EmptyBatchRejectionFixture.ToArgs(args));

	// Same technique as UnknownParameterFilterTests.Text: parse the {error} envelope and read
	// .message, rather than string-matching the raw (HTML-escaped) wire text.
	static string ErrorText(CallToolResult r)
	{
		var raw = string.Concat(r.Content.OfType<TextContentBlock>().Select(c => c.Text));
		using var doc = JsonDocument.Parse(raw);
		return doc.RootElement.GetProperty("error").GetProperty("message").GetString() ?? "";
	}

	// ── the five guarded verbs: items:[] (empty EFFECTIVE batch) is a hard reject ──────────────

	[Fact]
	public async Task TasksUpsert_EmptyNodes_IsRejected_NamingNodes()
	{
		var r = await Call("tasks_upsert", new { projectKey = Proj, board = "work", nodes = Array.Empty<object>() });
		r.IsError.Should().BeTrue();
		ErrorText(r).Should().Contain("'nodes': empty batch — nothing to write");
	}

	[Fact]
	public async Task MemoryUpsert_EmptyEntries_IsRejected_NamingEntries()
	{
		var r = await Call("memory_upsert", new { projectKey = Proj, store = "notes", entries = Array.Empty<object>() });
		r.IsError.Should().BeTrue();
		ErrorText(r).Should().Contain("'entries': empty batch — nothing to write");
	}

	[Fact]
	public async Task CommentsUpsert_EmptyItems_IsRejected_NamingItems()
	{
		var r = await Call("comments_upsert", new { projectKey = Proj, board = "work", items = Array.Empty<object>() });
		r.IsError.Should().BeTrue();
		ErrorText(r).Should().Contain("'items': empty batch — nothing to write");
	}

	[Fact]
	public async Task ConfigBindingUpsert_EmptyItems_IsRejected_NamingItems_NotAppliedTrue()
	{
		var r = await Call("config_binding_upsert", new { workspaceKey = Ws, items = Array.Empty<object>() });
		r.IsError.Should().BeTrue("an empty items:[] used to slip through as applied:true — see ConfigTools.BindingUpsertAsync");
		ErrorText(r).Should().Contain("'items': empty batch — nothing to write");
	}

	[Fact]
	public async Task RelationsCreate_EmptyItems_IsRejected_NamingItems_NotAppliedFalseSilently()
	{
		var r = await Call("relations_create", new { projectKey = Proj, items = Array.Empty<object>() });
		r.IsError.Should().BeTrue("an empty items:[] used to return applied:false with zero conflicts — unexplained");
		ErrorText(r).Should().Contain("'items': empty batch — nothing to write");
	}

	// ── the one documented exception: session_append ────────────────────────────────────────────

	[Fact]
	public async Task SessionAppend_AllBlankContent_StaysApplied_AppendedZero_NotRejected()
	{
		var r = await Call("session_append", new
		{
			projectKey = Proj,
			sessionId = "ebatch-session",
			agent = "claude-code",
			fromOrdinal = 1,
			messages = new[] { new { role = "user", content = "" } },
		});

		r.IsError.Should().NotBe(true, "a batch filtered down to zero content is a legitimate idempotent cursor no-op");
		r.StructuredContent!.Value.GetProperty("applied").GetBoolean().Should().BeTrue();
		r.StructuredContent!.Value.GetProperty("appended").GetInt64().Should().Be(0);
	}

	// ── the honest partial refusal that must NOT be swept into the empty-batch rule ─────────────

	[Fact]
	public async Task RelationsCreate_AtomicFalse_AllItemsInvalid_ReturnsPartialRefusal_ViaConflicts_NotAnError()
	{
		// A NON-empty batch where every item independently fails validation. This is the
		// documented partial-apply contract (atomic:false), not an empty batch — it must come
		// back as a normal {applied:false, conflicts:[...]} answer, never a thrown error.
		var r = await Call("relations_create", new
		{
			projectKey = Proj,
			atomic = false,
			items = new[]
			{
				new { kind = "relates_to", from = "no-such-node-1", to = "anchor" },
				new { kind = "relates_to", from = "anchor", to = "no-such-node-2" },
			},
		});

		r.IsError.Should().NotBe(true, "atomic:false with invalid items is a partial refusal, not a call-level error: " + string.Concat(r.Content.OfType<TextContentBlock>().Select(c => c.Text)));
		var sc = r.StructuredContent!.Value;
		sc.GetProperty("applied").GetBoolean().Should().BeFalse();
		sc.GetProperty("relations").GetArrayLength().Should().Be(0);
		var conflicts = sc.GetProperty("conflicts");
		conflicts.GetArrayLength().Should().Be(2, "each invalid item gets its OWN conflict entry — this is what distinguishes it from the unexplained empty-batch bug");
	}

	// ── the sentinel: a future 6th batch-write verb must not silently skip this rule ────────────
	//
	// Hand-maintained registry, same pattern as the conformance-battery
	// Excluded map: every tool ending in _upsert/_create with a top-level array-of-objects
	// parameter is a "batch write verb" candidate. Not gated on JSON-Schema `required` —
	// relations_create's `items` is schema-optional (single-form kind/from/to is the sibling
	// arm) but is still very much one of the five. Each candidate must be either in GuardedVerbs
	// (and is live-driven above) or in ExemptedVerbs with a reason. A new candidate that is
	// neither fails this test — forcing a conscious decision, not a silent gap.
	static readonly IReadOnlyDictionary<string, string> GuardedVerbs = new Dictionary<string, string>
	{
		["tasks_upsert"] = "nodes",
		["memory_upsert"] = "entries",
		["comments_upsert"] = "items",
		["config_binding_upsert"] = "items",
		["relations_create"] = "items",
	};

	static readonly IReadOnlyDictionary<string, string> ExemptedVerbs = new Dictionary<string, string>
	{
		// The one deliberate exception (documented on the tool itself): a batch filtered down to
		// zero content is an idempotent cursor no-op, not a client mistake.
		["session_append"] = "idempotent cursor no-op on an all-blank batch — see tool description",
		// Methodology surface (TasksTools.cs outside the tasks_upsert region, ~1246-1360, that this
		// card's brief explicitly hands off) — a PARALLEL wave of mcp-surface-naming-cleanup is
		// rewriting the methodology region concurrently. Not covered here; tracked separately, not
		// silently dropped.
		["tasks_methodology_rules_upsert"] = "methodology surface — parallel wave, out of scope for fix/mcp-empty-batch-rule",
		["tasks_methodology_utility_upsert"] = "methodology surface — parallel wave, out of scope for fix/mcp-empty-batch-rule",
	};

	// A `type` node is either a bare string ("array") or, for a nullable CLR type, a
	// ["array","null"]-shaped union (measured live on relations_create.items — nullable batch
	// params serialize this way because the single-form kind/from/to arm makes `items` optional).
	static bool TypeIncludes(JsonElement typeProp, string want) =>
		typeProp.ValueKind == JsonValueKind.String
			? typeProp.GetString() == want
			: typeProp.ValueKind == JsonValueKind.Array && typeProp.EnumerateArray().Any(v => v.GetString() == want);

	[Fact]
	public async Task GuardedBatchVerbSurface_HasNoUndiscoveredSixthVerb()
	{
		var candidates = new List<string>();
		foreach (var tool in _fx.Tools.Values)
		{
			if (!(tool.Name.EndsWith("_upsert", StringComparison.Ordinal) || tool.Name.EndsWith("_create", StringComparison.Ordinal)))
				continue;
			var schema = tool.ProtocolTool.InputSchema;
			if (!schema.TryGetProperty("properties", out var props)) continue;

			// NOT gated on JSON-Schema `required`: relations_create's `items` is schema-optional
			// (the single-form kind/from/to arm is the alternative), yet it is very much one of
			// the five guarded batch verbs — its own tool body enforces "one of the two forms" at
			// runtime, a constraint JSON Schema cannot express. So the discovery signal here is
			// shape (an array-of-OBJECTS top-level parameter on an _upsert/_create verb), not
			// schema-level requiredness.
			foreach (var prop in props.EnumerateObject())
			{
				if (prop.Value.TryGetProperty("type", out var t) && TypeIncludes(t, "array")
					&& prop.Value.TryGetProperty("items", out var items)
					&& items.TryGetProperty("type", out var itemType) && TypeIncludes(itemType, "object"))
				{
					candidates.Add(tool.Name);
					break;
				}
			}
		}

		var undeclared = candidates.Where(n => !GuardedVerbs.ContainsKey(n) && !ExemptedVerbs.ContainsKey(n)).OrderBy(n => n).ToList();
		undeclared.Should().BeEmpty(
			"an _upsert/_create verb with an array-of-objects batch parameter appeared that is neither in " +
			"GuardedVerbs (empty-batch-rejected, live-tested above) nor ExemptedVerbs (documented exception) " +
			"in EmptyBatchRejectionTests: " + string.Join(", ", undeclared));

		// And the reverse: nothing hand-listed here may have quietly disappeared from the live surface.
		foreach (var name in GuardedVerbs.Keys)
			candidates.Should().Contain(name, $"{name} is in GuardedVerbs but no longer matches the discovery heuristic — has its schema changed?");
	}
}
