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

namespace PetBox.Tests.Fragments;

// `fragment` over the REAL MCP wire, on ALL THREE write verbs (work/write-fragment-patch). The
// service suites prove the semantics; this one proves the two things only the wire can show:
//
//   1. the new field is reachable — it survives schema generation, McpUnknownParameterFilter and
//      the DTO -> contract mapping, and a fragment sent by a client really patches the row;
//   2. adding it did NOT soften the filter: a typo'd `fragmnet` is still REFUSED on every one of
//      the three verbs (the card's explicit constraint), and the refusal names `fragment`.
//
// (2) is invisible to a service-level test by construction: the filter reads the LIVE generated
// schema, so a field wired into the wrong DTO would be rejected when valid or accepted when
// misspelt, and both failures live entirely above the service door.
//
// The fixture enables Tasks AND Memory (modelled on EmptyBatchRejectionFixture) because
// memory_upsert is otherwise not served at all — a tasks-only host silently omits the verb, and a
// test that cannot even see the tool would stop covering it without failing.
public sealed class FragmentMcpFixture : IAsyncLifetime
{
	public const string ProjectKey = "fragmcp";
	public const string WorkspaceKey = "fragmcp-ws";
	const string ApiKeyValue = "yb_key_fragmcp_agent";
	const string Scopes = "tasks:read,tasks:write,memory:read,memory:write,admin:provision";

	readonly string _baseDir;
	readonly WebApplicationFactory<Program> _factory;
	HttpClient _http = null!;
	McpClient _mcp = null!;

	public IReadOnlyDictionary<string, McpClientTool> Tools { get; private set; } = null!;

	public FragmentMcpFixture()
	{
		_baseDir = Path.Combine(Path.GetTempPath(), "petbox-fragmcp-" + Guid.NewGuid().ToString("N"));
		Environment.SetEnvironmentVariable("PETBOX_MASTER_KEY", "test-key-for-secrets");
		Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");

		_factory = new WebApplicationFactory<Program>()
			.WithWebHostBuilder(b =>
			{
				b.UseEnvironment("Testing");
				// Feature flags are read BEFORE builder.Build(), where only UseSetting is visible.
				b.UseSetting("Features:Tasks", "true");
				b.UseSetting("Features:Memory", "true");
				b.ConfigureAppConfiguration((_, cfg) =>
				{
					cfg.AddInMemoryCollection(new Dictionary<string, string?>
					{
						["ConnectionStrings:PetBox"] = TestSchema.NewTempConnectionString(),
						// Background services only hold native SQLite handles open on Windows.
						["Host:BackgroundServices"] = "false",
						["Features:Tasks"] = "true",
						["Features:Memory"] = "true",
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
			await db.InsertAsync(new Workspace { Key = WorkspaceKey, Name = "Frag WS", CreatedAt = DateTime.UtcNow });
			await db.InsertAsync(new Project { Key = ProjectKey, WorkspaceKey = WorkspaceKey, Name = "Frag" });
			await db.InsertAsync(new ApiKey { Key = ApiKeyValue, ProjectKey = ProjectKey, Scopes = Scopes, CreatedAt = DateTime.UtcNow });
		}

		_http = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
		_http.DefaultRequestHeaders.Add("X-Api-Key", ApiKeyValue);
		var transport = new HttpClientTransport(new HttpClientTransportOptions
		{
			Endpoint = new Uri(_http.BaseAddress!, "/mcp"),
			AdditionalHeaders = new Dictionary<string, string> { ["X-Api-Key"] = ApiKeyValue },
		}, _http);
		_mcp = await McpTestClient.ConnectAsync(transport);
		Tools = (await _mcp.ListToolsAsync()).ToDictionary(t => t.Name);

		await Tools["tasks_board_create"].CallAsync(Args(new { projectKey = ProjectKey, board = "work", kind = "simple" }));
	}

	public async ValueTask DisposeAsync()
	{
		await _mcp.DisposeAsync();
		_http.Dispose();
		await _factory.DisposeAsync();
		TestDirs.CleanupOrDefer(_baseDir);
	}

	internal static Dictionary<string, object?> Args(object o) =>
		JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(o))!
			.ToDictionary(kv => kv.Key, kv => (object?)((JsonElement)kv.Value!));
}

public sealed class FragmentMcpSurfaceTests : IClassFixture<FragmentMcpFixture>
{
	readonly FragmentMcpFixture _fx;
	public FragmentMcpSurfaceTests(FragmentMcpFixture fx) => _fx = fx;

	const string Proj = FragmentMcpFixture.ProjectKey;

	Task<CallToolResult> Call(string tool, object args) => _fx.Tools[tool].CallAsync(FragmentMcpFixture.Args(args)).AsTask();

	// Same technique as UnknownParameterFilterTests.Text: parse the {error} envelope and read
	// .message rather than string-matching the raw, HTML-escaped wire text (which also repeats the
	// message inside `detail`).
	static string Text(CallToolResult r)
	{
		var raw = string.Concat(r.Content.OfType<TextContentBlock>().Select(c => c.Text));
		try
		{
			using var doc = JsonDocument.Parse(raw);
			if (doc.RootElement.TryGetProperty("error", out var e)
				&& e.TryGetProperty("message", out var m) && m.GetString() is { } text)
				return text;
		}
		catch (JsonException) { /* not an envelope */ }
		return raw;
	}

	static JsonElement Json(CallToolResult r) =>
		JsonDocument.Parse(string.Concat(r.Content.OfType<TextContentBlock>().Select(c => c.Text))).RootElement.Clone();

	// ── 1. reachable, and it really patches ──────────────────────────────────────────

	[Fact]
	public async Task Tasks_Fragment_OverTheWire_PatchesTheBody()
	{
		var key = "wire-task-" + Guid.NewGuid().ToString("N")[..8];
		var seed = await Call("tasks_upsert", new
		{
			projectKey = Proj,
			board = "work",
			bodyLen = -1,
			nodes = new[] { new { key, title = "Probe", body = "## A\n\nkeep this\n\n## B\n\nchange this", version = 0 } },
		});
		seed.IsError.Should().NotBe(true, Text(seed));
		var v = Json(seed).GetProperty("added")[0].GetProperty("version").GetInt64();

		var r = await Call("tasks_upsert", new
		{
			projectKey = Proj,
			board = "work",
			bodyLen = -1,
			nodes = new[] { new { key, version = v, fragment = new[] { new { old = "change this", @new = "CHANGED" } } } },
		});

		r.IsError.Should().NotBe(true, Text(r));
		var json = Json(r);
		json.GetProperty("applied").GetBoolean().Should().BeTrue();
		json.GetProperty("updated")[0].GetProperty("body").GetString()
			.Should().Be("## A\n\nkeep this\n\n## B\n\nCHANGED");
	}

	[Fact]
	public async Task Memory_Fragment_OverTheWire_PatchesTheBody()
	{
		var key = "wire-mem-" + Guid.NewGuid().ToString("N")[..8];
		var seed = await Call("memory_upsert", new
		{
			projectKey = Proj,
			store = "notes",
			bodyLen = -1,
			entries = new[] { new { key, version = 0, type = "Project", description = "d", body = "alpha beta gamma" } },
		});
		seed.IsError.Should().NotBe(true, Text(seed));
		var v = Json(seed).GetProperty("added")[0].GetProperty("version").GetInt64();

		var r = await Call("memory_upsert", new
		{
			projectKey = Proj,
			store = "notes",
			bodyLen = -1,
			entries = new[] { new { key, version = v, fragment = new[] { new { old = "beta", @new = "BETA" } } } },
		});

		r.IsError.Should().NotBe(true, Text(r));
		var json = Json(r);
		json.GetProperty("applied").GetBoolean().Should().BeTrue();
		json.GetProperty("updated")[0].GetProperty("body").GetString().Should().Be("alpha BETA gamma");
	}

	[Fact]
	public async Task Tasks_FragmentMatchingTwice_IsAppliedFalseWithAConflict_NotAnMcpError()
	{
		var key = "wire-dup-" + Guid.NewGuid().ToString("N")[..8];
		var seed = await Call("tasks_upsert", new
		{
			projectKey = Proj,
			board = "work",
			bodyLen = -1,
			nodes = new[] { new { key, title = "Dup", body = "same same", version = 0 } },
		});
		var v = Json(seed).GetProperty("added")[0].GetProperty("version").GetInt64();

		var r = await Call("tasks_upsert", new
		{
			projectKey = Proj,
			board = "work",
			bodyLen = -1,
			nodes = new[] { new { key, version = v, fragment = new[] { new { old = "same", @new = "x" } } } },
		});

		// A refusal, NOT a protocol error: it rides the ordinary conflict channel exactly as a
		// stale baseline does, so `applied` stays the single source of truth.
		r.IsError.Should().NotBe(true, Text(r));
		var json = Json(r);
		json.GetProperty("applied").GetBoolean().Should().BeFalse();
		json.GetProperty("conflicts")[0].GetProperty("reason").GetString().Should().Contain("occurs 2 times");
	}

	// ── 2. the unknown-parameter guarantee survives on all three verbs ───────────────

	[Fact]
	public async Task MisspeltFragment_IsStillRejected_OnTasks()
	{
		var r = await Call("tasks_upsert", new
		{
			projectKey = Proj,
			board = "work",
			nodes = new[] { new { key = "typo-probe", title = "T", version = 0, fragmnet = new[] { new { old = "a", @new = "b" } } } },
		});

		r.IsError.Should().Be(true);
		Text(r).Should().Contain("fragmnet").And.Contain("fragment");
	}

	[Fact]
	public async Task MisspeltFragment_IsStillRejected_OnComments()
	{
		var r = await Call("comments_upsert", new
		{
			projectKey = Proj,
			board = "work",
			items = new[] { new { id = "whatever", version = 1L, fragmnet = new[] { new { old = "a", @new = "b" } } } },
		});

		r.IsError.Should().Be(true);
		Text(r).Should().Contain("fragmnet").And.Contain("fragment");
	}

	[Fact]
	public async Task MisspeltFragment_IsStillRejected_OnMemory()
	{
		var r = await Call("memory_upsert", new
		{
			projectKey = Proj,
			store = "notes",
			entries = new[] { new { key = "typo-probe", version = 1L, fragmnet = new[] { new { old = "a", @new = "b" } } } },
		});

		r.IsError.Should().Be(true);
		Text(r).Should().Contain("fragmnet").And.Contain("fragment");
	}

	[Fact]
	public void FragmentIsInTheGeneratedItemSchema_OfAllThreeWriteVerbs()
	{
		// The filter's accept/reject decision is driven entirely by this schema, so `fragment`
		// sitting at the right nesting (the batch param's items[], one hop down) IS the contract.
		foreach (var (tool, batch) in new[] { ("tasks_upsert", "nodes"), ("comments_upsert", "items"), ("memory_upsert", "entries") })
		{
			var item = _fx.Tools[tool].ProtocolTool.InputSchema
				.GetProperty("properties").GetProperty(batch).GetProperty("items").GetProperty("properties");
			item.TryGetProperty("fragment", out _).Should().BeTrue($"{tool}.{batch}[] must accept 'fragment'");
		}
	}
}
