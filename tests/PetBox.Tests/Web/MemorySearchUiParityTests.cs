using System.Net;
using System.Security.Claims;
using LinqToDB;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Data;
using PetBox.Core.Features;
using PetBox.Core.Settings;
using PetBox.Memory.Contract;
using PetBox.Tests.Memory;
using PetBox.Web.Mcp;

namespace PetBox.Tests.Web;

// Live parity check for the memory UI's hybrid-search cutover (card ui-search-memory-hybrid, spec
// search-one-engine-for-human-and-agent): runs the ACTUAL app (WebApplicationFactory<Program> — a
// real Kestrel/TestServer pipeline, real routing/authorization/Razor rendering) against a real
// SQLite-backed IMemoryService, then asks the SAME question two ways over the SAME data:
//   - the UI's rendered HTML at /ui/{ws}/{project}/memory/{store}?q=... and
//     /ui/{ws}/{project}/memory?q=... (project-wide sweep), fetched over real HTTP with a real
//     cookie login;
//   - MemoryTools.SearchAsync — the exact static method memory_search dispatches to — called
//     in-process against the SAME IMemoryService instance the app resolved from DI.
// The two must agree on which keys come back for a Russian-language query (this project has no
// LLM route configured, so the semantic leg degrades to lexical-only FTS5 — reported honestly by
// both surfaces' retriever provenance; the parity claim is about ranking/filtering agreement, not
// about the semantic leg specifically).
// Shared per-class host (work share-fixtures-across-per-test-classes): xUnit news the test
// class per test, so without this fixture each of the 4 tests boots its own WebApplicationFactory.
// No per-test reset needed — every [Fact] below only READS the dataset seeded once here; none of
// them writes, so there is nothing a later test could observe leaking from an earlier one.
public sealed class MemorySearchUiParityFixture : IAsyncLifetime
{
	public const string Ws = "ws";
	public const string Proj = "proj";
	public const string DeployKey = "note-deploy-ru";
	public const string UnrelatedKey = "note-weather-ru";
	public const string WorkspaceDeployKey = "note-ws-deploy-ru";
	public const string OtherStoreKey = "note-decisions-ru";

	string _baseDir = "";
	public WebApplicationFactory<Program> Factory { get; private set; } = null!;
	public HttpClient Client { get; private set; } = null!;

	public async ValueTask InitializeAsync()
	{
		Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");
		_baseDir = Path.Combine(Path.GetTempPath(), "petbox-memparity-" + Guid.NewGuid().ToString("N"));
		Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
		{
			b.UseEnvironment("Testing");
			b.ConfigureAppConfiguration((_, cfg) =>
			{
				cfg.AddInMemoryCollection(new Dictionary<string, string?>
				{
					["ConnectionStrings:PetBox"] = TestSchema.NewTempConnectionString(),
					["Host:BackgroundServices"] = "false",
					["Features:Memory"] = "true",
					["Admin:Username"] = "admin",
					["Admin:PasswordHash"] = ModuleViewsFixture.TestPasswordHash,
				});
			});
			b.ConfigureServices(svc =>
			{
				var existing = svc.SingleOrDefault(d => d.ServiceType == typeof(IScopedDbFactory<PetBox.Memory.Data.MemoryDb>));
				if (existing is not null) svc.Remove(existing);
				svc.AddSingleton<IScopedDbFactory<PetBox.Memory.Data.MemoryDb>>(_ => new ScopedDbFactory<PetBox.Memory.Data.MemoryDb>(
					Path.Combine(_baseDir, "memory"), Scope.Project,
					c => new PetBox.Memory.Data.MemoryDb(PetBox.Memory.Data.MemoryDb.CreateOptions(c)), TestSchema.Memory));
			});
		});

		var cs = Factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("PetBox")!;
		TestSchema.Core(cs);
		using (var db = new PetBoxDb(PetBoxDb.CreateOptions(cs)))
			db.Insert(new PetBox.Core.Models.Project { Key = Proj, WorkspaceKey = Ws, Name = "P", Description = "" });

		Client = Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

		using var scope = Factory.Services.CreateScope();
		var memory = scope.ServiceProvider.GetRequiredService<IMemoryService>();
		// The workspace's shared-memory container needs its own Projects row before anything can
		// be written to it (MemoryStore.CreateAsync checks project existence) — same lazy-ensure
		// the Memory.cshtml.cs page does on first navigation to a shared-memory route.
		await scope.ServiceProvider.GetRequiredService<IWorkspaceMemoryDirectory>().EnsureWorkspaceContainerAsync(Ws);
		await memory.CreateStoreAsync(Proj, "notes", "test notes");
		await memory.CreateStoreAsync(Proj, "decisions", "test decisions");

		// Project-scope entries — one on-topic (contains the query word), one an unrelated decoy.
		await memory.UpsertAsync(Proj, "notes",
		[
			new MemoryEntryInput
			{
				Key = DeployKey, Version = 0, Type = "Project",
				Description = "Развёртывание сервиса",
				Body = "Разворачивать сервис нужно через blue-green deployment, чтобы избежать простоя при выкладке.",
			},
			new MemoryEntryInput
			{
				Key = UnrelatedKey, Version = 0, Type = "Project",
				Description = "Погода",
				Body = "Сегодня в Москве идёт дождь и прохладно, метеослужба обещает похолодание.",
			},
		], []);

		// A second store in the SAME project — only reachable by the project-WIDE sweep
		// (Memory.cshtml), never by the per-store page (MemoryStore.cshtml is pinned to "notes").
		await memory.UpsertAsync(Proj, "decisions",
		[
			new MemoryEntryInput
			{
				Key = OtherStoreKey, Version = 0, Type = "Project",
				Description = "Решение о развёртывании",
				Body = "Команда приняла решение проводить развёртывание по пятницам только с ручным подтверждением.",
			},
		], []);

		// Workspace-scope entry (the shared-memory container of "ws", $ws-ws) — only reachable
		// with scope=workspace/cascade, never with the default scope=project.
		await memory.UpsertAsync(WorkspaceMemory.ContainerKeyFor(Ws), "notes",
		[
			new MemoryEntryInput
			{
				Key = WorkspaceDeployKey, Version = 0, Type = "Project",
				Description = "Флаг команды деплоя",
				Body = "Общий флаг для всех проектов воркспейса: используйте --strategy=blue-green при развёртывании.",
			},
		], []);
	}

	public async ValueTask DisposeAsync()
	{
		Client.Dispose();
		await Factory.DisposeAsync();
		TestDirs.CleanupOrDefer(_baseDir);
	}
}

public sealed class MemorySearchUiParityTests : IClassFixture<MemorySearchUiParityFixture>
{
	const string Ws = MemorySearchUiParityFixture.Ws;
	const string Proj = MemorySearchUiParityFixture.Proj;
	const string DeployKey = MemorySearchUiParityFixture.DeployKey;
	const string UnrelatedKey = MemorySearchUiParityFixture.UnrelatedKey;
	const string WorkspaceDeployKey = MemorySearchUiParityFixture.WorkspaceDeployKey;
	const string OtherStoreKey = MemorySearchUiParityFixture.OtherStoreKey;

	readonly WebApplicationFactory<Program> _factory;
	readonly HttpClient _client;

	public MemorySearchUiParityTests(MemorySearchUiParityFixture fx)
	{
		_factory = fx.Factory;
		_client = fx.Client;
	}

	// Logs in (anti-forgery + cookie) and returns the authenticated response for url — same recipe
	// ModuleViewsTests.GetAuthedAsync uses.
	async Task<HttpResponseMessage> GetAuthedAsync(string url)
	{
		var resp = await _client.GetAsync(url);
		if (resp.StatusCode != HttpStatusCode.Found) return resp;

		var loginPage = await _client.GetAsync("/Login");
		var loginHtml = await loginPage.Content.ReadAsStringAsync();
		var tokenStart = loginHtml.IndexOf("__RequestVerificationToken", StringComparison.Ordinal);
		var valueStart = loginHtml.IndexOf("value=\"", tokenStart, StringComparison.Ordinal) + 7;
		var valueEnd = loginHtml.IndexOf('"', valueStart);
		var token = loginHtml[valueStart..valueEnd];
		var cookies = loginPage.Headers.GetValues("Set-Cookie").ToList();

		var loginReq = new HttpRequestMessage(HttpMethod.Post, "/Login?returnUrl=" + Uri.EscapeDataString(url));
		loginReq.Content = new FormUrlEncodedContent(new Dictionary<string, string>
		{
			["username"] = "admin",
			["password"] = "test123",
			["returnUrl"] = url,
			["__RequestVerificationToken"] = token,
		});
		foreach (var c in cookies) loginReq.Headers.Add("Cookie", c.Split(';')[0]);

		var loginResp = await _client.SendAsync(loginReq);
		var authCookie = loginResp.Headers.GetValues("Set-Cookie").First();
		var req = new HttpRequestMessage(HttpMethod.Get, url);
		req.Headers.Add("Cookie", authCookie.Split(';')[0]);
		return await _client.SendAsync(req);
	}

	static IHttpContextAccessor McpHttp(string projectKey) =>
		new HttpContextAccessor
		{
			HttpContext = new DefaultHttpContext
			{
				RequestServices = TestProjectCatalog.Services,
				User = new ClaimsPrincipal(new ClaimsIdentity(
					[new Claim("project", projectKey), new Claim("scopes", "memory:read,memory:write")], "test")),
			},
		};

	static FeatureFlags McpFlags() => new(new ConfigurationBuilder()
		.AddInMemoryCollection(new Dictionary<string, string?> { ["Features:Memory"] = "true" }).Build());

	// The store page (MemoryStore.cshtml, scope=project default): a Russian query for
	// "развёртывание" (deployment/rollout) must find the on-topic entry, not the weather decoy,
	// via SearchEntriesAsync — no substring LIKE involved.
	[Fact]
	public async Task StorePage_Search_FindsOnTopicEntry_NotTheUnrelatedOne()
	{
		using var resp = await GetAuthedAsync($"/ui/{Ws}/{Proj}/memory/notes?q=" + Uri.EscapeDataString("развёртывание"));
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();

		// data-entry-key on a HIT card, not a bare substring match: the store-wide usage aggregate
		// band lists every key (incl. the decoy) in a "never surfaced" tooltip regardless of the
		// query, so a plain Contains/NotContain on the key would false-positive there.
		html.Should().Contain($"data-entry-key=\"{DeployKey}\"");
		html.Should().NotContain($"data-entry-key=\"{UnrelatedKey}\"");
		html.Should().Contain("data-testid=\"hit-score\"");   // score badge survives the cutover
		html.Should().Contain("data-testid=\"hit-retriever\""); // retriever provenance shown
	}

	// The store page's scope=cascade must ALSO surface the workspace-container hit — unreachable
	// under the old per-store LIKE listing, which never looked outside the project.
	[Fact]
	public async Task StorePage_Search_Cascade_AlsoFindsTheWorkspaceScopedEntry()
	{
		using var resp = await GetAuthedAsync(
			$"/ui/{Ws}/{Proj}/memory/notes?q=" + Uri.EscapeDataString("развёртывании") + "&scope=cascade");
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();

		html.Should().Contain(WorkspaceDeployKey);
		html.Should().Contain("data-testid=\"hit-scope\">workspace");
	}

	// The project-wide sweep (Memory.cshtml, requirement 4 of the card): a query with NO store
	// filter must reach the second store ("decisions") that the per-store page could never see.
	[Fact]
	public async Task ProjectPage_Search_SweepsEveryStore_FindsHitInASecondStore()
	{
		using var resp = await GetAuthedAsync($"/ui/{Ws}/{Proj}/memory?q=" + Uri.EscapeDataString("развёртывании"));
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();

		html.Should().Contain(DeployKey);       // from "notes"
		html.Should().Contain(OtherStoreKey);   // from "decisions" — the cross-store part
		html.Should().Contain("data-testid=\"hit-store\">decisions");
	}

	// THE PARITY CLAIM: the UI's store-scoped search and MemoryTools.SearchAsync (the exact method
	// memory_search dispatches to) must select the SAME keys for the same query against the SAME
	// IMemoryService instance — "one engine for human and agent", not two independently-behaving
	// substring/hybrid implementations that happen to look similar.
	[Fact]
	public async Task UiSearch_And_McpMemorySearch_AgreeOnTheSameKeys()
	{
		const string q = "развёртывание";

		using var uiResp = await GetAuthedAsync($"/ui/{Ws}/{Proj}/memory/notes?q=" + Uri.EscapeDataString(q));
		uiResp.StatusCode.Should().Be(HttpStatusCode.OK);
		var uiHtml = await uiResp.Content.ReadAsStringAsync();

		using var scope = _factory.Services.CreateScope();
		var memory = scope.ServiceProvider.GetRequiredService<IMemoryService>();
		var mcpResult = await MemoryTools.SearchAsync(
			McpHttp(Proj), McpFlags(), scope.ServiceProvider.GetRequiredService<IWorkspaceMemoryDirectory>(),
			memory, new NoopUsageRecorder(), q, scope: "project", projectKey: Proj, store: "notes");

		var mcpKeys = mcpResult.Items.Select(i => i.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
		mcpKeys.Should().Contain(DeployKey);
		mcpKeys.Should().NotContain(UnrelatedKey);

		foreach (var key in mcpKeys)
			uiHtml.Should().Contain(key, $"the UI search result must contain every key memory_search returned ({key})");
	}
}
