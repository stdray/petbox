using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Tasks.Data;
using PetBox.Web.Mcp;

namespace PetBox.Tests.Mcp;

// spec tool-description-economy — tools/list serves a COMPACT HEAD (everything above the `[[full]]`
// sentinel) for tools that opted in; the full essay stays fetchable via the `tool_describe` tool.
// These tests cover the mechanism (McpToolDescriptions), the pilot descriptions' registered text,
// and the live wire path (a real MCP tools/list + tool_describe call through the server).
public sealed class ToolDescriptionEconomyMechanismTests
{
	const string Sentinel = McpToolDescriptions.Sentinel;

	static string Doc(params string[] lines) => string.Join("\n", lines);

	// (a) a description WITH a sentinel: Compact serves the head only, sentinel-and-below stripped.
	[Fact]
	public void Compact_WithSentinel_ServesHeadOnly()
	{
		var full = Doc("Compact purpose line.", "Second head line.", Sentinel, "Below the fold — the essay.", "More essay.");
		var tool = new Tool { Name = "x", Description = full };

		var compact = McpToolDescriptions.Compact(tool);

		compact.Should().NotBeSameAs(tool);                              // a fresh clone, original untouched
		compact.Description.Should().Be("Compact purpose line.\nSecond head line.");
		compact.Description.Should().NotContain("essay").And.NotContain(Sentinel);
		tool.Description.Should().Be(full);                              // canonical instance is NOT mutated
	}

	// (b) a description WITHOUT a sentinel: served unchanged (same reference — zero cost, byte-identical).
	[Fact]
	public void Compact_WithoutSentinel_ServedUnchanged()
	{
		var tool = new Tool { Name = "y", Description = "A short one-line description with no sentinel." };

		var compact = McpToolDescriptions.Compact(tool);

		compact.Should().BeSameAs(tool);
		compact.Description.Should().Be("A short one-line description with no sentinel.");
	}

	// (c) Full merges head + below-sentinel and strips the marker line — what tool_describe returns.
	[Fact]
	public void Full_MergesHeadAndBody_StripsSentinel()
	{
		var full = Doc("Head.", Sentinel, "Body paragraph one.", "Body paragraph two.");

		McpToolDescriptions.Full(full).Should().Be("Head.\nBody paragraph one.\nBody paragraph two.");
		McpToolDescriptions.Full(full).Should().NotContain(Sentinel);
	}

	[Fact]
	public void NoSentinel_HeadAndFull_AreThePassthroughText()
	{
		const string d = "Plain description, no sentinel.";
		McpToolDescriptions.HasSentinel(d).Should().BeFalse();
		McpToolDescriptions.Head(d).Should().Be(d);
		McpToolDescriptions.Full(d).Should().Be(d);
	}

	// ── (d) the pilot descriptions: each carries a sentinel, and its compact head keeps the
	// critical gotcha keywords while the deep-detail keywords ride below the fold. ──────────────

	public static IEnumerable<object[]> Pilots() => new[]
	{
		// tool, keywords the HEAD must keep, a phrase that must stay BELOW the sentinel.
		// The body-carrying writers (tasks_upsert, comments_upsert) MUST surface the GFM body-format
		// trap (## headings / real newlines, NOT ==headings==) in the HEAD — it is a top gotcha for
		// weak models, so it can't hide below the fold.
		new object[] { "tasks_upsert",     new[] { "WATERMARK", "GFM", "partOf", "applied", "headings", "==headings==" }, "autoResolved" },
		new object[] { "tasks_search",     new[] { "bodyLen", "part_of", "budget", "tasks:read" }, "matchedIn" },
		new object[] { "memory_search",    new[] { "bodyLen", "CASCADE", "version", "memory:read" }, "includeUsage" },
		new object[] { "memory_remember",  new[] { "memory_upsert", "workspace", "task board" }, "low-ceremony" },
		new object[] { "session_search",   new[] { "two-stage", "fullScan", "memory:read" }, "hitsPerSession" },
		new object[] { "comments_upsert",  new[] { "CREATE", "PATCH", "WATERMARK", "headings", "==headings==", "applied" }, "artifact" },
		// report_issue's HEAD carries the whole call decision — the SYSTEMIC-vs-one-off water line, the
		// memory_remember routing for own-project friction, and the batch-at-end-of-turn rule. Nothing
		// reads tool_describe before deciding to report, so only the auth plumbing may ride below.
		new object[] { "report_issue",     new[] { "SYSTEMIC", "memory_remember", "END of your turn", "triage" }, "authenticated key" },
	};

	[Theory]
	[MemberData(nameof(Pilots))]
	public void Pilot_Head_KeepsGotchas_DeepDetailStaysBelow(string tool, string[] headKeywords, string belowSentinelPhrase)
	{
		var full = RegisteredDescription(tool);
		McpToolDescriptions.HasSentinel(full).Should().BeTrue($"{tool} should opt into compaction");

		var head = McpToolDescriptions.Head(full)!;
		head.Should().ContainAll(headKeywords, $"the {tool} compact head must keep its critical gotchas");
		head.Should().NotContain(belowSentinelPhrase, $"deep detail should ride below the sentinel for {tool}");

		// the full text tool_describe returns still contains BOTH the head gotchas and the deep detail.
		var whole = McpToolDescriptions.Full(full)!;
		whole.Should().ContainAll(headKeywords);
		whole.Should().Contain(belowSentinelPhrase);
		whole.Should().NotContain(Sentinel);
		// compaction is a real saving, not a no-op rename.
		head.Length.Should().BeLessThan(full.Length);
	}

	// The registered [Description] essay for a tool, by its McpServerTool name (the source of truth).
	static string RegisteredDescription(string toolName)
	{
		foreach (var type in typeof(McpToolDescriptions).Assembly.GetTypes())
			foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance))
				if (m.GetCustomAttribute<McpServerToolAttribute>()?.Name == toolName)
					return m.GetCustomAttribute<DescriptionAttribute>()?.Description
						?? throw new InvalidOperationException($"{toolName} has no [Description]");
		throw new InvalidOperationException($"no MCP tool named '{toolName}'");
	}
}

// End-to-end wire path: a real server tools/list serves the head, and tool_describe returns the full text.
public sealed class ToolDescriptionEconomyWireFixture : IAsyncLifetime
{
	public const string ProjectKey = "econ";
	public const string ApiKey = "yb_key_econ_agent";
	const string Scopes = "tasks:read,tasks:write,memory:read,memory:write";

	readonly string _baseDir;
	readonly WebApplicationFactory<Program> _factory;
	HttpClient _http = null!;
	McpClient _mcp = null!;

	public IReadOnlyDictionary<string, McpClientTool> Tools { get; private set; } = null!;
	public McpClient Mcp => _mcp;

	public ToolDescriptionEconomyWireFixture()
	{
		_baseDir = Path.Combine(Path.GetTempPath(), "petbox-econ-" + Guid.NewGuid().ToString("N"));
		Environment.SetEnvironmentVariable("PETBOX_MASTER_KEY", "test-key-for-secrets");
		Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");

		_factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
		{
			b.UseEnvironment("Testing");
			b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
			{
				["ConnectionStrings:PetBox"] = TestSchema.NewTempConnectionString(),
				["Features:Tasks"] = "true",
				["Features:Memory"] = "true",
			}));
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
			await db.ApiKeys.Where(k => k.Key == ApiKey).DeleteAsync();
			await db.Projects.Where(p => p.Key == ProjectKey).DeleteAsync();
			await db.Workspaces.Where(w => w.Key == "test").DeleteAsync();
			await db.InsertAsync(new Workspace { Key = "test", Name = "Test", CreatedAt = DateTime.UtcNow });
			await db.InsertAsync(new Project { Key = ProjectKey, WorkspaceKey = "test", Name = "Econ" });
			await db.InsertAsync(new ApiKey { Key = ApiKey, ProjectKey = ProjectKey, Scopes = Scopes, CreatedAt = DateTime.UtcNow });
		}

		_http = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
		var transport = new HttpClientTransport(new HttpClientTransportOptions
		{
			Endpoint = new Uri(_http.BaseAddress!, "/mcp"),
			AdditionalHeaders = new Dictionary<string, string> { ["X-Api-Key"] = ApiKey },
		}, _http);
		_mcp = await McpClient.CreateAsync(transport, cancellationToken: default);
		Tools = (await _mcp.ListToolsAsync()).ToDictionary(t => t.Name);
	}

	public async Task DisposeAsync()
	{
		await _mcp.DisposeAsync();
		_http.Dispose();
		await _factory.DisposeAsync();
		TestDirs.CleanupOrDefer(_baseDir);
	}
}

public sealed class ToolDescriptionEconomyWireTests : IClassFixture<ToolDescriptionEconomyWireFixture>
{
	readonly ToolDescriptionEconomyWireFixture _fx;
	public ToolDescriptionEconomyWireTests(ToolDescriptionEconomyWireFixture fx) => _fx = fx;

	// (a) a piloted tool is served COMPACT in tools/list — head only, sentinel-and-below stripped.
	[Fact]
	public void ToolsList_PilotedTool_ServesCompactHead()
	{
		var desc = _fx.Tools["tasks_upsert"].ProtocolTool.Description!;
		desc.Should().Contain("WATERMARK");                 // head gotcha survives
		desc.Should().Contain("headings").And.Contain("==headings=="); // GFM body-format trap is in the served head
		// The "NOT literal \n" warning must READ as the two-character backslash+n, not a stray line
		// break — a single-backslash bug turns "`\n`" into a real newline and this fails.
		desc.Should().Contain("`\\n`");
		desc.Should().NotContain(McpToolDescriptions.Sentinel);
		desc.Should().NotContain("autoResolved");           // deep detail is below the fold, not served
	}

	// The body-format warning itself must be intact in the SERVED head: the literal two-character
	// sequence backslash+n (wrapped in backticks), never a real newline. This regression-guards the
	// single-backslash defect where "NOT literal `\n`" compiled to an actual line break in the head.
	[Theory]
	[InlineData("comments_upsert")]
	[InlineData("tasks_upsert")]
	public void ToolsList_BodyCarryingHead_KeepsLiteralBackslashN(string tool)
	{
		var desc = _fx.Tools[tool].ProtocolTool.Description!;
		desc.Should().Contain("literal `\\n`");            // "literal " + backtick + backslash + n + backtick
		desc.Should().NotContain(McpToolDescriptions.Sentinel);
	}

	// (b) a non-piloted tool is served UNCHANGED (no sentinel → full short description).
	[Fact]
	public void ToolsList_NonPilotedTool_ServedUnchanged()
	{
		var desc = _fx.Tools["tasks_board_list"].ProtocolTool.Description!;
		desc.Should().Contain("List task boards");
		desc.Should().NotContain(McpToolDescriptions.Sentinel);
	}

	// (c) tool_describe returns the FULL text — head + below-sentinel, marker stripped.
	[Fact]
	public async Task ToolDescribe_ReturnsFullText_IncludingBelowSentinel()
	{
		var res = await _fx.Tools["tool_describe"].CallAsync(
			new Dictionary<string, object?> { ["name"] = "tasks_upsert" });

		res.IsError.Should().NotBe(true);
		var sc = res.StructuredContent!.Value;
		var full = sc.GetProperty("description").GetString()!;
		full.Should().Contain("WATERMARK");        // head kept
		full.Should().Contain("autoResolved");     // below-sentinel detail restored
		full.Should().NotContain(McpToolDescriptions.Sentinel);
		sc.GetProperty("name").GetString().Should().Be("tasks_upsert");
	}

	// tool_describe on an unknown name is an error, not a null-structured success.
	[Fact]
	public async Task ToolDescribe_UnknownTool_IsError()
	{
		var res = await _fx.Tools["tool_describe"].CallAsync(
			new Dictionary<string, object?> { ["name"] = "no_such_tool" });
		res.IsError.Should().Be(true);
	}
}
