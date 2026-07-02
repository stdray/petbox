using System.Text.Json;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Tasks.Data;

namespace PetBox.Tests.Tasks;

// The user-defined methodology DEFINITION surface (engine wave 1.1: storage + validation
// + the tasks.methodology_def_* verbs), exercised end-to-end over MCP. The definition is
// pure data in this slice — live boards still run the built-in WorkflowCatalog preset —
// so these tests cover the document round-trip, the optimistic-concurrency contract and
// the whole-document integrity validation, not FSM behavior.
[Collection("DataModule")]
public sealed class MethodologyDefinitionTests : IAsyncLifetime
{
	const string ProjectKey = "mdef";
	const string AgentKey = "yb_key_mdef_agent"; // tasks:read,tasks:write

	readonly string _baseDir;
	readonly WebApplicationFactory<Program> _factory;
	HttpClient _http = null!;
	McpClient _mcp = null!;

	public MethodologyDefinitionTests()
	{
		_baseDir = Path.Combine(Path.GetTempPath(), "petbox-mdef-" + Guid.NewGuid().ToString("N"));
		Environment.SetEnvironmentVariable("PETBOX_MASTER_KEY", "test-key-for-secrets");
		Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");
		Environment.SetEnvironmentVariable("Features__Tasks", "true");

		_factory = new WebApplicationFactory<Program>()
			.WithWebHostBuilder(b =>
			{
				b.UseEnvironment("Testing");
				b.ConfigureAppConfiguration((_, cfg) =>
				{
					cfg.AddInMemoryCollection(new Dictionary<string, string?>
					{
						["ConnectionStrings:PetBox"] = $"Data Source={Path.Combine(Path.GetTempPath(), $"petbox-test-{Guid.NewGuid():N}.db")};Cache=Shared",
						["Features:Tasks"] = "true",
					});
				});
				b.ConfigureServices(svc =>
				{
					var tasksFactory = svc.SingleOrDefault(d => d.ServiceType == typeof(IScopedDbFactory<TasksDb>));
					if (tasksFactory is not null) svc.Remove(tasksFactory);
					svc.AddSingleton<IScopedDbFactory<TasksDb>>(_ => new ScopedDbFactory<TasksDb>(
						Path.Combine(_baseDir, "tasks"), PetBox.Core.Settings.Scope.Project,
						cs => new TasksDb(TasksDb.CreateOptions(cs)), TasksSchema.Ensure));
				});
			});
	}

	public async Task InitializeAsync()
	{
		var cs = _factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("PetBox")!;
		TestSchema.Core(cs);

		using (var scope = _factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<PetBoxDb>();
			await db.ApiKeys.Where(k => k.Key == AgentKey).DeleteAsync();
			await db.Projects.Where(p => p.Key == ProjectKey).DeleteAsync();
			await db.Workspaces.Where(w => w.Key == "test").DeleteAsync();
			await db.InsertAsync(new Workspace { Key = "test", Name = "Test", CreatedAt = DateTime.UtcNow });
			await db.InsertAsync(new Project { Key = ProjectKey, WorkspaceKey = "test", Name = "Methodology def" });
			await db.InsertAsync(new ApiKey { Key = AgentKey, ProjectKey = ProjectKey, Scopes = "tasks:read,tasks:write", CreatedAt = DateTime.UtcNow });
		}

		_http = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
		_http.DefaultRequestHeaders.Add("X-Api-Key", AgentKey);
		var transport = new HttpClientTransport(new HttpClientTransportOptions
		{
			Endpoint = new Uri(_http.BaseAddress!, "/mcp"),
			AdditionalHeaders = new Dictionary<string, string> { ["X-Api-Key"] = AgentKey },
		}, _http);
		_mcp = await McpClient.CreateAsync(transport, cancellationToken: default);
	}

	public async Task DisposeAsync()
	{
		await _mcp.DisposeAsync();
		_http.Dispose();
		await _factory.DisposeAsync();
		SqliteConnection.ClearAllPools();
		if (Directory.Exists(_baseDir)) Directory.Delete(_baseDir, recursive: true);
	}

	// ── helpers ──────────────────────────────────────────────────────────────

	async Task<CallToolResult> Call(string tool, object args) =>
		await (await _mcp.ListToolsAsync()).First(t => t.Name == tool)
			.CallAsync(JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(args))!
				.ToDictionary(kv => kv.Key, kv => (object?)((JsonElement)kv.Value!)));

	Task<CallToolResult> Upsert(object definition, long version = 0) =>
		Call("tasks.methodology_def_upsert", new { projectKey = ProjectKey, definition, version });

	Task<CallToolResult> Get() =>
		Call("tasks.methodology_def_get", new { projectKey = ProjectKey });

	static string Text(CallToolResult r) =>
		r.Content.OfType<TextContentBlock>().First().Text;

	// Errors arrive as the central envelope {"error":{...}} (IsError stays false).
	static bool IsErr(CallToolResult r) =>
		r.IsError == true ||
		(r.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text?.Contains("\"error\"") ?? false);

	static JsonElement Parse(CallToolResult r) =>
		JsonDocument.Parse(Text(r)).RootElement.Clone();

	// A valid definition: two kinds; `flow` hosts TWO workflow blocks (task-vs-epic FSMs,
	// one gated transition carrying a preconditionArtifact), `triage` hosts one.
	static object ValidDefinition(string epicDoneName = "Shipped") => new
	{
		name = "acme-process",
		kinds = new object[]
		{
			new
			{
				kind = "flow",
				quickAddAllowed = false,
				workflows = new object[]
				{
					new
					{
						types = new[] { "task", "bug" },
						statuses = new object[]
						{
							new { slug = "Todo", name = "Todo", kind = "open" },
							new { slug = "Doing", name = "Doing", kind = "open" },
							new { slug = "Done", name = "Done", kind = "terminalok" },
						},
						transitions = new object[]
						{
							new { from = "Todo", to = "Doing" },
							new { from = "Doing", to = "Done", requiresApproval = true, preconditionArtifact = "review_notes" },
						},
					},
					new
					{
						types = new[] { "epic" },
						statuses = new object[]
						{
							new { slug = "Draft", kind = "open" },
							new { slug = "Shipped", name = epicDoneName, kind = "terminalok" },
							new { slug = "Dropped", kind = "terminalcancel" },
						},
						transitions = new object[]
						{
							new { from = "Draft", to = "Shipped" },
							new { from = "Draft", to = "Dropped", requiresReason = true },
						},
					},
				},
			},
			new
			{
				kind = "triage",
				workflows = new object[]
				{
					new
					{
						types = new[] { "ticket" },
						statuses = new object[]
						{
							new { slug = "New", kind = "open" },
							new { slug = "Closed", kind = "terminalcancel" },
						},
						transitions = new object[] { new { from = "New", to = "Closed" } },
					},
				},
			},
		},
	};

	// ── scenarios ────────────────────────────────────────────────────────────

	// 1. a valid custom methodology (2 kinds, one with 2 workflow blocks) round-trips
	// intact through upsert → get, including the preconditionArtifact field.
	[Fact]
	public async Task Define_Valid_RoundTripsIntact()
	{
		var up = await Upsert(ValidDefinition());
		IsErr(up).Should().BeFalse(Text(up));
		var ack = Parse(up);
		ack.GetProperty("version").GetInt64().Should().Be(1);
		ack.GetProperty("changed").GetBoolean().Should().BeTrue();

		var got = Parse(await Get());
		got.GetProperty("defined").GetBoolean().Should().BeTrue();
		got.GetProperty("name").GetString().Should().Be("acme-process");
		got.GetProperty("version").GetInt64().Should().Be(1);

		var kinds = got.GetProperty("kinds").EnumerateArray().ToList();
		kinds.Select(k => k.GetProperty("kind").GetString()).Should().Equal("flow", "triage");
		kinds[0].GetProperty("quickAddAllowed").GetBoolean().Should().BeFalse();
		kinds[1].GetProperty("quickAddAllowed").GetBoolean().Should().BeTrue("omitted = the default");

		var blocks = kinds[0].GetProperty("workflows").EnumerateArray().ToList();
		blocks.Should().HaveCount(2, "one kind may host several state machines");
		blocks[0].GetProperty("types").EnumerateArray().Select(t => t.GetString()).Should().Equal("task", "bug");
		blocks[0].GetProperty("initial").GetString().Should().Be("Todo", "statuses[0] is the initial");
		blocks[0].GetProperty("statuses").EnumerateArray()
			.Single(s => s.GetProperty("slug").GetString() == "Done")
			.GetProperty("kind").GetString().Should().Be("terminalok");
		var gated = blocks[0].GetProperty("transitions").EnumerateArray()
			.Single(t => t.GetProperty("to").GetString() == "Done");
		gated.GetProperty("requiresApproval").GetBoolean().Should().BeTrue();
		gated.GetProperty("preconditionArtifact").GetString().Should().Be("review_notes");

		blocks[1].GetProperty("types").EnumerateArray().Select(t => t.GetString()).Should().Equal("epic");
		blocks[1].GetProperty("statuses").EnumerateArray()
			.Single(s => s.GetProperty("slug").GetString() == "Draft")
			.GetProperty("name").GetString().Should().Be("Draft", "omitted status name defaults to the slug");
	}

	// 2. optimistic concurrency: two writers both submitting baseline 0 — the second is a
	// clear conflict naming the current version, not a clobber.
	[Fact]
	public async Task Define_SameBaselineTwice_Conflict()
	{
		IsErr(await Upsert(ValidDefinition())).Should().BeFalse();

		var second = await Upsert(ValidDefinition(epicDoneName: "Delivered"), version: 0);
		IsErr(second).Should().BeTrue(Text(second));
		Text(second).Should().Contain("is stale");
		Text(second).Should().Contain("current version is 1");
	}

	// 3. an edit against the correct baseline lands as a new revision; get shows the new
	// content and the bumped version. An identical resubmit then collapses to a no-op.
	[Fact]
	public async Task Update_WithCorrectBaseline_NewRevision()
	{
		IsErr(await Upsert(ValidDefinition())).Should().BeFalse();

		var edit = await Upsert(ValidDefinition(epicDoneName: "Delivered"), version: 1);
		IsErr(edit).Should().BeFalse(Text(edit));
		var ack = Parse(edit);
		ack.GetProperty("version").GetInt64().Should().Be(2);
		ack.GetProperty("changed").GetBoolean().Should().BeTrue();

		var got = Parse(await Get());
		got.GetProperty("version").GetInt64().Should().Be(2);
		Text(await Get()).Should().Contain("Delivered");

		var resubmit = Parse(await Upsert(ValidDefinition(epicDoneName: "Delivered"), version: 2));
		resubmit.GetProperty("changed").GetBoolean().Should().BeFalse("an identical resubmit is a no-op");
		resubmit.GetProperty("version").GetInt64().Should().Be(2);
	}

	// 4a. a transition referencing a status outside its block is rejected, naming the edge.
	[Fact]
	public async Task Define_TransitionToUnknownStatus_Rejected()
	{
		var r = await Upsert(new
		{
			name = "bad",
			kinds = new object[]
			{
				new
				{
					kind = "flow",
					workflows = new object[]
					{
						new
						{
							types = new[] { "task" },
							statuses = new object[] { new { slug = "Todo", kind = "open" } },
							transitions = new object[] { new { from = "Todo", to = "Banana" } },
						},
					},
				},
			},
		});
		IsErr(r).Should().BeTrue(Text(r));
		Text(r).Should().Contain("does not reference a status");
		Text(r).Should().Contain("Banana");
	}

	// 4b. a workflow block with no statuses is rejected.
	[Fact]
	public async Task Define_EmptyStatuses_Rejected()
	{
		var r = await Upsert(new
		{
			name = "bad",
			kinds = new object[]
			{
				new
				{
					kind = "flow",
					workflows = new object[]
					{
						new { types = new[] { "task" }, statuses = Array.Empty<object>(), transitions = Array.Empty<object>() },
					},
				},
			},
		});
		IsErr(r).Should().BeTrue(Text(r));
		Text(r).Should().Contain("at least one status");
	}

	// 4c. the same type in two blocks of one kind is rejected — a type must resolve to
	// exactly one state machine.
	[Fact]
	public async Task Define_DuplicateTypeAcrossBlocks_Rejected()
	{
		object Block() => new
		{
			types = new[] { "task" },
			statuses = new object[] { new { slug = "Todo", kind = "open" } },
			transitions = Array.Empty<object>(),
		};
		var r = await Upsert(new
		{
			name = "bad",
			kinds = new object[] { new { kind = "flow", workflows = new object[] { Block(), Block() } } },
		});
		IsErr(r).Should().BeTrue(Text(r));
		Text(r).Should().Contain("more than one workflow block");
	}

	// 4d. a kind slug outside the canonical spec is rejected (verbatim — no silent lowering).
	[Fact]
	public async Task Define_BadKindSlug_Rejected()
	{
		var r = await Upsert(new
		{
			name = "bad",
			kinds = new object[]
			{
				new
				{
					kind = "Big Kind",
					workflows = new object[]
					{
						new
						{
							types = new[] { "task" },
							statuses = new object[] { new { slug = "Todo", kind = "open" } },
							transitions = Array.Empty<object>(),
						},
					},
				},
			},
		});
		IsErr(r).Should().BeTrue(Text(r));
		Text(r).Should().Contain("is not a valid slug");
	}

	// 5. no definition stored → the structured "not defined" answer (built-in preset), not
	// an error and not an empty object.
	[Fact]
	public async Task Get_NoDefinition_StructuredNotDefined()
	{
		var r = await Get();
		IsErr(r).Should().BeFalse(Text(r));
		var got = Parse(r);
		got.GetProperty("defined").GetBoolean().Should().BeFalse();
		got.GetProperty("preset").GetString().Should().Be("builtin-workflow-catalog");
	}
}
