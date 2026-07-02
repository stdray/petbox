using System.Text.Json;
using Json.Schema;
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

namespace PetBox.Tests.Mcp;

// Regression guard for the mcp-output-schema-conformance chore: a strict MCP client
// (opencode/DeepSeek) validates a tool's structuredContent against the outputSchema it
// declared in tools/list and rejects a non-conforming payload. Two ways we used to break
// that, both fixed and locked here:
//   (1) Null-omission vs a schema that marked every nullable property `required`. The
//       serializer drops null keys (deliberate token economy, incl. the bodyLen contract);
//       the schema must NOT require them. We generate an honest schema (nullable props are
//       not required) via McpOutputSchema.NullableAware — so the ACTUAL payload validates
//       against the ACTUAL declared schema (this is what this test asserts end-to-end).
//   (2) The error envelope. A declared outputSchema means a SUCCESS result must conform;
//       an error must ride isError=true (and need not conform). McpErrorEnvelopeFilter now
//       sets IsError=true, so a deliberate failure is asserted as isError — never validated.
[Collection("DataModule")]
public sealed class McpOutputSchemaConformanceTests : IAsyncLifetime
{
	const string ProjectKey = "conf";
	const string ApiKey = "yb_key_conf_agent";
	// Everything the battery touches: tasks + memory + logs + health.
	const string Scopes = "tasks:read,tasks:write,memory:read,memory:write,logs:query,logs:admin,health:read";

	readonly string _baseDir;
	readonly WebApplicationFactory<Program> _factory;
	HttpClient _http = null!;
	McpClient _mcp = null!;

	public McpOutputSchemaConformanceTests()
	{
		_baseDir = Path.Combine(Path.GetTempPath(), "petbox-conf-" + Guid.NewGuid().ToString("N"));
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
						["ConnectionStrings:PetBox"] = $"Data Source={Path.Combine(Path.GetTempPath(), $"petbox-test-{Guid.NewGuid():N}.db")};Cache=Shared",
						["Features:Tasks"] = "true",
						["Features:Memory"] = "true",
						["Features:Logging"] = "true",
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
			await db.ApiKeys.Where(k => k.Key == ApiKey).DeleteAsync();
			await db.Projects.Where(p => p.Key == ProjectKey).DeleteAsync();
			await db.Workspaces.Where(w => w.Key == "test").DeleteAsync();
			await db.InsertAsync(new Workspace { Key = "test", Name = "Test", CreatedAt = DateTime.UtcNow });
			await db.InsertAsync(new Project { Key = ProjectKey, WorkspaceKey = "test", Name = "Conf" });
			await db.InsertAsync(new ApiKey { Key = ApiKey, ProjectKey = ProjectKey, Scopes = Scopes, CreatedAt = DateTime.UtcNow });
		}

		_http = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
		_http.DefaultRequestHeaders.Add("X-Api-Key", ApiKey);
		var transport = new HttpClientTransport(new HttpClientTransportOptions
		{
			Endpoint = new Uri(_http.BaseAddress!, "/mcp"),
			AdditionalHeaders = new Dictionary<string, string> { ["X-Api-Key"] = ApiKey },
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

	// Every tool that declares an outputSchema and returns a SUCCESS structuredContent must
	// have that content conform to the schema. This is the property strict clients enforce.
	[Fact]
	public async Task Every_Declared_OutputSchema_Has_At_Least_One_Tool()
	{
		var tools = await _mcp.ListToolsAsync();
		var withSchema = tools.Where(t => t.ProtocolTool.OutputSchema is not null).ToList();
		// The whole point of the chore: a large, growing surface declares output schemas.
		withSchema.Count.Should().BeGreaterThan(20);
	}

	[Fact]
	public async Task Representative_Battery_StructuredContent_Conforms_To_Declared_Schema()
	{
		var tools = (await _mcp.ListToolsAsync()).ToDictionary(t => t.Name);

		// Seed a little data so the null-bearing shapes carry real rows (present + omitted
		// nullable keys both exercised).
		await Call(tools, "tasks_board_create", new { projectKey = ProjectKey, board = "work", kind = "simple" });
		await Call(tools, "tasks_upsert", new
		{
			projectKey = ProjectKey,
			board = "work",
			nodes = Nodes(new { key = "a", status = "Todo", type = "task", title = "Alpha", body = "hello" }),
		});
		await Call(tools, "log_create", new { projectKey = ProjectKey, name = "audit" });

		// (tool name, args) — each returns a success structuredContent we validate against its
		// declared outputSchema. Covers the tools named in the chore + the seeded shapes.
		var battery = new (string Tool, object Args)[]
		{
			("tasks_search",      new { projectKey = ProjectKey, board = "work" }),
			("tasks_upsert",      new { projectKey = ProjectKey, board = "work",
			                            nodes = Nodes(new { key = "a", version = 1, status = "InProgress" }) }),
			("memory_search",     new { projectKey = ProjectKey }),
			("memory_store_list", new { projectKey = ProjectKey, includeUsage = true }),
			("log_query",         new { projectKey = ProjectKey, logName = "audit", kql = "events | take 10" }),
			("session_search",    new { projectKey = ProjectKey, q = "anything" }), // no-digest-store: Distilled/Reason/Retrievers present
			("session_search",    new { projectKey = ProjectKey }),                 // listing mode: those fields omitted
			("health_search",     new { projectKey = ProjectKey }),
		};

		var failures = new List<string>();
		foreach (var (name, args) in battery)
		{
			tools.Should().ContainKey(name);
			var tool = tools[name];
			tool.ProtocolTool.OutputSchema.Should().NotBeNull($"{name} must declare an outputSchema");

			var res = await Call(tools, name, args);
			res.IsError.Should().NotBe(true, $"{name} should succeed: {Text(res)}");
			res.StructuredContent.Should().NotBeNull($"{name} success must carry structuredContent");

			foreach (var err in Validate(tool.ProtocolTool.OutputSchema!.Value, res.StructuredContent!.Value))
				failures.Add($"{name}: {err}");
		}

		failures.Should().BeEmpty(
			"structuredContent must conform to the declared outputSchema:\n" + string.Join("\n", failures));
	}

	[Fact]
	public async Task Deliberate_Error_Is_IsError_True_Without_StructuredContent()
	{
		var tools = (await _mcp.ListToolsAsync()).ToDictionary(t => t.Name);

		// A cross-project read: AssertProject throws → McpErrorEnvelopeFilter converts it.
		var res = await Call(tools, "tasks_search", new { projectKey = "some-other-project" });

		// The contract: errors ride the isError channel (NOT a schema-shaped success), and
		// carry the learning {error} envelope on the text content.
		res.IsError.Should().Be(true);
		res.StructuredContent.Should().BeNull();
		Text(res).Should().Contain("\"error\"");

		// Because it's isError, a strict client never schema-validates it — so a nonconforming
		// (envelope-shaped) body is legal exactly here.
	}

	// ── helpers ──────────────────────────────────────────────────────────────
	static IEnumerable<string> Validate(JsonElement schemaElement, JsonElement instance)
	{
		var schema = JsonSchema.FromText(schemaElement.GetRawText());
		var results = schema.Evaluate(instance, new EvaluationOptions { OutputFormat = OutputFormat.List });
		if (results.IsValid) yield break;
		foreach (var d in results.Details ?? [])
			if (d.Errors is { Count: > 0 } errors)
				foreach (var e in errors)
					yield return $"{d.InstanceLocation} {e.Key}: {e.Value}";
	}

	static async Task<CallToolResult> Call(IReadOnlyDictionary<string, McpClientTool> tools, string tool, object args) =>
		await tools[tool].CallAsync(ToArgs(args));

	static Dictionary<string, object?> ToArgs(object o) =>
		JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(o))!
			.ToDictionary(kv => kv.Key, kv => (object?)((JsonElement)kv.Value!));

	static JsonElement Nodes(params object[] nodes) => JsonSerializer.SerializeToElement(nodes);

	static string Text(CallToolResult r) =>
		r.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text ?? "";
}
