using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LinqToDB;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Data;

namespace PetBox.Tests.Mcp;

// work observation-promote-body-unescape: the same decisive instrument as
// McpWriteVerbEscapeParityTests (memory_remember vs memory_upsert), applied to
// tasks_observation_promote's flat `body` parameter vs tasks_upsert's `nodes[].body` DTO
// field. Hand-built JSON-RPC `tools/call` bodies are POSTed to the real /mcp with the
// argument spelled BYTE FOR BYTE, once per verb, and the stored body is read back through
// ITasksService directly (no read verb in between to re-spell anything). If the two verbs
// diverge, it happens here; if they agree, the reported wire divergence was in the bytes
// the caller sent, not in the server code that received them.
public sealed class TasksWriteVerbEscapeParityTests(TasksEscapeParityFixture fx)
	: IClassFixture<TasksEscapeParityFixture>
{
	// ONE backslash before `n`: a JSON \n escape — a conformant parser yields a real LF.
	const string OneBackslashN = "\\n";

	// TWO backslashes before `n`: an escaped backslash followed by a literal `n` — a
	// conformant parser yields the two LITERAL characters '\' and 'n'.
	const string TwoBackslashN = "\\\\n";

	[Fact]
	public async Task OneBackslashN_DecodesToRealNewline_ForBothVerbs()
	{
		var viaUpsert = await fx.UpsertBodyAsync("## H" + OneBackslashN + OneBackslashN + "body");
		var viaPromote = await fx.PromoteBodyAsync("## H" + OneBackslashN + OneBackslashN + "body");

		viaUpsert.Should().Be("## H\n\nbody", "a JSON \\n escape is decoded by the parser before any PetBox code sees it");
		viaPromote.Should().Be("## H\n\nbody",
			"tasks_observation_promote's `body` must decode a JSON \\n escape exactly like tasks_upsert's `nodes[].body`");
		viaUpsert.Should().Be(viaPromote);
	}

	[Fact]
	public async Task TwoBackslashN_StaysLiteral_ForBothVerbs()
	{
		var viaUpsert = await fx.UpsertBodyAsync("## H" + TwoBackslashN + TwoBackslashN + "body");
		var viaPromote = await fx.PromoteBodyAsync("## H" + TwoBackslashN + TwoBackslashN + "body");

		viaUpsert.Should().Be("## H\\n\\nbody", "`\\\\n` on the wire IS a backslash followed by 'n' — storing it verbatim is correct");
		viaPromote.Should().Be("## H\\n\\nbody");
		viaUpsert.Should().Be(viaPromote);
	}
}

// Posts hand-built JSON-RPC bodies to the real /mcp and reads the stored node body back
// through ITasksService — the storage truth, with no tasks_node_get in between.
public sealed class TasksEscapeParityFixture : IAsyncLifetime
{
	const string ProjectKey = "tvep";
	const string ApiKeyValue = "yb_key_tasks_escape_parity_probe";

	HttpClient _http = null!;
	int _seq;

	WebApplicationFactory<Program> Factory { get; }

	public TasksEscapeParityFixture()
	{
		Environment.SetEnvironmentVariable("PETBOX_MASTER_KEY", "test-key-for-secrets");
		Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");

		Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
		{
			b.UseEnvironment("Testing");
			b.UseSetting("Features:Tasks", "true");
			b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
			{
				["ConnectionStrings:PetBox"] = TestSchema.NewTempConnectionString(),
				["Features:Tasks"] = "true",
				["Host:BackgroundServices"] = "false",
			}));
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

	readonly string _baseDir = Path.Combine(Path.GetTempPath(), "petbox-tvep-" + Guid.NewGuid().ToString("N"));

	public async ValueTask InitializeAsync()
	{
		var cs = Factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("PetBox")!;
		TestSchema.Core(cs);
		_http = Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
		_http.DefaultRequestHeaders.Add("X-Api-Key", ApiKeyValue);

		using var scope = Factory.Services.CreateScope();
		using (var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open())
		{
			await db.ApiKeys.Where(k => k.Key == ApiKeyValue).DeleteAsync();
			await db.Projects.Where(p => p.Key == ProjectKey).DeleteAsync();
			await db.Workspaces.Where(w => w.Key == "test").DeleteAsync();
			await db.InsertAsync(new Workspace { Key = "test", Name = "Test", CreatedAt = DateTime.UtcNow });
			await db.InsertAsync(new Project { Key = ProjectKey, WorkspaceKey = "test", Name = "TasksEscapeParity" });
			await db.InsertAsync(new ApiKey { Key = ApiKeyValue, ProjectKey = ProjectKey, Scopes = "tasks:read,tasks:write", CreatedAt = DateTime.UtcNow });
		}

		var tasks = scope.ServiceProvider.GetRequiredService<ITasksService>();
		if (!await tasks.BoardExistsAsync(ProjectKey, "work", default))
			await tasks.CreateBoardAsync(ProjectKey, "work", "work", "work", null, methodologyInstance: TaskBoardMeta.UtilityWorld);
		if (!await tasks.BoardExistsAsync(ProjectKey, SystemBoards.Observations, default))
			await tasks.CreateBoardAsync(ProjectKey, SystemBoards.Observations, SystemBoards.ObservationKind, "obs", null, methodologyInstance: TaskBoardMeta.UtilityWorld);
	}

	public async ValueTask DisposeAsync()
	{
		_http.Dispose();
		await Factory.DisposeAsync();
	}

	// `spelling` is spliced into the JSON body VERBATIM — the caller states the wire bytes.
	public async Task<string> UpsertBodyAsync(string spelling)
	{
		var key = $"parity-upsert-{Interlocked.Increment(ref _seq)}";
		var body =
			"{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"tasks_upsert\"," +
			"\"arguments\":{\"projectKey\":\"" + ProjectKey + "\",\"board\":\"work\"," +
			"\"nodes\":[{\"key\":\"" + key + "\",\"version\":0,\"type\":\"chore\",\"title\":\"t\"," +
			"\"body\":\"" + spelling + "\"}]}}}";
		await CallAsync(body);
		return await ReadBodyAsync("work", key);
	}

	public async Task<string> PromoteBodyAsync(string spelling)
	{
		var obsKey = $"parity-obs-{Interlocked.Increment(ref _seq)}";
		var targetKey = $"parity-promote-{_seq}";
		using (var scope = Factory.Services.CreateScope())
		{
			var tasks = scope.ServiceProvider.GetRequiredService<ITasksService>();
			var seeded = await tasks.UpsertAsync(ProjectKey, SystemBoards.Observations,
				[new NodePatch { Key = obsKey, Version = 0, Title = "seed", Body = "seed body" }]);
			seeded.Result.Applied.Should().BeTrue($"seeding the observation to promote must succeed: {string.Join(";", seeded.Result.Conflicts.Select(c => c.Reason))}");
		}

		var body =
			"{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"tasks_observation_promote\"," +
			"\"arguments\":{\"projectKey\":\"" + ProjectKey + "\",\"observation\":\"" + obsKey + "\"," +
			"\"targetBoard\":\"work\",\"type\":\"chore\",\"key\":\"" + targetKey + "\",\"title\":\"t\"," +
			"\"body\":\"" + spelling + "\"}}}";
		await CallAsync(body);
		return await ReadBodyAsync("work", targetKey);
	}

	async Task<string> ReadBodyAsync(string board, string key)
	{
		using var scope = Factory.Services.CreateScope();
		var tasks = scope.ServiceProvider.GetRequiredService<ITasksService>();
		var node = await tasks.GetNodeOnBoardAsync(ProjectKey, board, key);
		return node.Node.Body ?? "";
	}

	async Task<JsonElement> CallAsync(string body)
	{
		var req = new HttpRequestMessage(HttpMethod.Post, "/mcp")
		{
			Content = new StringContent(body, Encoding.UTF8, "application/json"),
		};
		req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
		req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

		using var resp = await _http.SendAsync(req);
		var text = await resp.Content.ReadAsStringAsync();
		resp.StatusCode.Should().Be(HttpStatusCode.OK, $"the bare tools/call must be accepted: {text}");

		using var doc = JsonDocument.Parse(JsonPayload(text));
		doc.RootElement.TryGetProperty("error", out _).Should().BeFalse($"JSON-RPC error: {text}");
		var result = doc.RootElement.GetProperty("result");
		(result.TryGetProperty("isError", out var isError) && isError.ValueKind == JsonValueKind.True)
			.Should().BeFalse($"the write must apply: {text}");
		return result.TryGetProperty("structuredContent", out var sc) ? sc.Clone() : default;
	}

	static string JsonPayload(string body)
	{
		if (body.TrimStart().StartsWith('{')) return body;
		foreach (var line in body.Split('\n'))
			if (line.StartsWith("data:", StringComparison.Ordinal))
				return line["data:".Length..].Trim();
		throw new Xunit.Sdk.XunitException($"no JSON payload in the MCP response: {body}");
	}
}
