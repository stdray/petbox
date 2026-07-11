using System.Net;
using System.Text;
using System.Text.Json;
using LinqToDB;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Log.Core.Data;

namespace PetBox.Tests.Web;

// Shared per-class host for LogPipelineTests: xUnit news the test class per test, so
// without this fixture all 40+ tests each boot their own WebApplicationFactory. No
// per-test reset is needed: every test isolates by unique message/service-key/project
// (Guid suffixes) or asserts on before/after deltas, so accumulated log rows and seeded
// projects from earlier tests in the class are invisible to later ones.
public sealed class LogPipelineFixture : IAsyncLifetime
{
	public const string TestPasswordHash = "pbkdf2$100000$h1twJi/he3s8S7jSM9pkGQ==$efnLBffww5Gprn6BjpNgZkTcG+1zNu2L6z3TZ7YvD/o=";

	public WebApplicationFactory<Program> Factory { get; }
	public HttpClient Client { get; private set; } = null!;

	public LogPipelineFixture()
	{
		// WebApplication.CreateBuilder reads ASPNETCORE_ENVIRONMENT at construction
		// — before WithWebHostBuilder.UseEnvironment("Testing") gets a chance to
		// apply — so set it here. Without this, on Linux CI the env defaults to
		// Production, appsettings.Testing.json never loads, Features:Logging is
		// false at ConfigureServices time, and IIngestionPipeline isn't registered.
		Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");
		Factory = new WebApplicationFactory<Program>()
			.WithWebHostBuilder(b =>
			{
				b.UseEnvironment("Testing");
				b.ConfigureAppConfiguration((_, cfg) =>
				{
					cfg.AddInMemoryCollection(new Dictionary<string, string?>
					{
						// A fresh, uniquely-DIRECTORIED Core db per fixture instance — not just a
						// unique filename dropped in the bare OS temp root. Program.cs derives every
						// scoped module's storage dir (logs/config/tasks/memory/db) from
						// Path.GetDirectoryName(this connection string), so a shared bare-temp-root
						// directory collapsed EVERY WebApplicationFactory test host in the suite onto
						// the SAME physical logs/$system/petbox.db (self-log, auto-created at startup
						// since Features:Logging defaults true) and logs/$system/default.db (this
						// fixture's own log) — unrelated test classes' hosts raced uncoordinated
						// schema-create + writes on those files concurrently. See TestSchema for detail.
						["ConnectionStrings:PetBox"] = TestSchema.NewTempConnectionString(),
						["Features:Logging"] = "true",
						["Seq:SelfLog:Enabled"] = "true",
						["Admin:Username"] = "admin",
						["Admin:PasswordHash"] = TestPasswordHash,
					});
				});
			});
	}

	public async Task InitializeAsync()
	{
		var __testCs = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>(Factory.Services).GetConnectionString("PetBox")!;
		TestSchema.Core(__testCs);
		Client = Factory.CreateClient(new WebApplicationFactoryClientOptions
		{
			AllowAutoRedirect = false,
		});

		// Logs are explicit now; create the $system/default log these tests ingest into.
		using var scope = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
			.CreateScope(Factory.Services);
		var store = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
			.GetRequiredService<PetBox.Log.Core.Data.ILogStore>(scope.ServiceProvider);
		if (!await store.ExistsAsync("$system", "default"))
			await store.CreateAsync("$system", "default", null);
	}

	public async Task DisposeAsync()
	{
		Client.Dispose();
		await Factory.DisposeAsync();
	}
}

// Out of the serialized WebAppFactory collection: the fixture writes only the constant
// ASPNETCORE_ENVIRONMENT=Testing (never nulled) and uses its own Guid temp db.
public sealed class LogPipelineTests : IClassFixture<LogPipelineFixture>
{
	readonly WebApplicationFactory<Program> _factory;
	readonly HttpClient _client;

	const string ApiKey = "yb_key_system_internal";
	const string TestPassword = "test123";

	public LogPipelineTests(LogPipelineFixture fx)
	{
		_factory = fx.Factory;
		_client = fx.Client;
	}

	async Task<HttpResponseMessage> GetPageAsync(string url)
	{
		var resp = await _client.GetAsync(url);
		if (resp.StatusCode == HttpStatusCode.Found)
		{
			// Get anti-forgery token from login page
			var loginPage = await _client.GetAsync("/Login");
			var loginHtml = await loginPage.Content.ReadAsStringAsync();
			var tokenStart = loginHtml.IndexOf("__RequestVerificationToken");
			var valueStart = loginHtml.IndexOf("value=\"", tokenStart) + 7;
			var valueEnd = loginHtml.IndexOf('"', valueStart);
			var token = loginHtml[valueStart..valueEnd];

			var cookies = loginPage.Headers.GetValues("Set-Cookie").ToList();

			var loginReq = new HttpRequestMessage(HttpMethod.Post, "/Login?returnUrl=" + Uri.EscapeDataString(url));
			loginReq.Content = new FormUrlEncodedContent(new Dictionary<string, string>
			{
				["username"] = "admin",
				["password"] = TestPassword,
				["returnUrl"] = url,
				["__RequestVerificationToken"] = token,
			});
			foreach (var c in cookies)
				loginReq.Headers.Add("Cookie", c.Split(';')[0]);

			var loginResp = await _client.SendAsync(loginReq);
			loginResp.StatusCode.Should().Be(HttpStatusCode.Redirect);

			var authCookie = loginResp.Headers.GetValues("Set-Cookie").FirstOrDefault();
			if (authCookie is not null)
			{
				var req = new HttpRequestMessage(HttpMethod.Get, url);
				req.Headers.Add("Cookie", authCookie.Split(';')[0]);
				return await _client.SendAsync(req);
			}
		}
		return resp;
	}

	static HttpRequestMessage LogRequest(string path, HttpMethod? method = null)
	{
		var req = new HttpRequestMessage(method ?? HttpMethod.Get, path);
		req.Headers.Add("X-Api-Key", ApiKey);
		return req;
	}

	async Task<HttpResponseMessage> PostClefAsync(string svc, string jsonl)
	{
		// Path-based ingest into $system/default; X-Service-Key tags the emitter.
		var req = LogRequest("/api/ingest/$system/default/clef", HttpMethod.Post);
		req.Headers.Add("X-Service-Key", svc);
		req.Content = new StringContent(jsonl, Encoding.UTF8, "text/plain");
		return await _client.SendAsync(req);
	}

	async Task<JsonDocument> QueryAsync(string kql, string projectKey = "$system", string logName = "default")
	{
		var req = LogRequest($"/api/logs/{projectKey}/{logName}/query?q={Uri.EscapeDataString(kql)}");
		using var resp = await _client.SendAsync(req);
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var body = await resp.Content.ReadAsStringAsync();
		return JsonDocument.Parse(body);
	}

	async Task<int> TotalCount()
	{
		var doc = await QueryAsync("events | count");
		return doc.RootElement.GetProperty("rows")[0][0].GetInt32();
	}

	static string UniqueMsg(string marker) => $"__test__{marker}__{Guid.NewGuid():N}";

	// Ingestion is asynchronous — ChannelIngestionPipeline enqueues and a background
	// writer loop persists — so a 200 from an ingest endpoint does not mean the rows
	// are queryable yet. Poll until a marker message is visible; the channel is FIFO
	// per (project, log), so once the LAST message of a batch lands, everything posted
	// before it has landed too. Every test that ingests into a log it (or a later
	// test) queries must wait — under the shared per-class host an unflushed batch
	// would otherwise leak into the next test's before/after count.
	async Task WaitForIngestAsync(string marker, string projectKey = "$system", string logName = "default")
	{
		for (var i = 0; i < 400; i++)
		{
			var doc = await QueryAsync($"events | where Message contains \"{marker}\" | take 1", projectKey, logName);
			if (doc.RootElement.GetProperty("count").GetInt32() > 0) return;
			await Task.Delay(25);
		}
		throw new Xunit.Sdk.XunitException($"ingested marker '{marker}' not visible in {projectKey}/{logName} after 10s");
	}

	// Same, for project logs the test's $system key cannot query over REST — count
	// rows directly through ILogStore.
	async Task WaitForProjectIngestAsync(string projectKey, string logName, string message)
	{
		for (var i = 0; i < 400; i++)
		{
			if (ProjectLogCount(projectKey, logName, message) > 0) return;
			await Task.Delay(25);
		}
		throw new Xunit.Sdk.XunitException($"ingested message '{message}' not visible in {projectKey}/{logName} after 10s");
	}

	[Fact]
	public async Task Ingest_ValidClef_ReturnsIngestedCount()
	{
		var msg = UniqueMsg("a1");
		var msgLast = UniqueMsg("a3");
		var resp = await PostClefAsync("svc-a",
			$$"""
			{"@t":"2024-01-01T00:00:00Z","@l":"Info","@m":"{{msg}}"}
			{"@t":"2024-01-01T00:00:01Z","@l":"Error","@m":"{{UniqueMsg("a2")}}","@x":"ex"}
			{"@t":"2024-01-01T00:00:02Z","@l":"Warning","@m":"{{msgLast}}","drive":"C:"}
			""");

		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var body = await resp.Content.ReadAsStringAsync();
		var doc = JsonDocument.Parse(body);
		doc.RootElement.GetProperty("ingested").GetInt32().Should().Be(3);
		doc.RootElement.GetProperty("errors").GetInt32().Should().Be(0);

		await WaitForIngestAsync(msgLast);
		var c = await TotalCount();
		c.Should().BeGreaterThanOrEqualTo(3);
	}

	[Fact]
	public async Task Ingest_WithoutApiKey_Returns401()
	{
		var req = new HttpRequestMessage(HttpMethod.Post, "/api/ingest/$system/default/clef");
		req.Headers.Add("X-Service-Key", "svc-b");
		req.Content = new StringContent(
			$$"""{"@t":"2024-01-01T00:00:00Z","@l":"Info","@m":"{{UniqueMsg("b1")}}"}""",
			Encoding.UTF8, "text/plain");
		using var resp = await _client.SendAsync(req);
		resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
	}

	[Fact]
	public async Task Ingest_WithoutServiceKey_Returns400()
	{
		var req = LogRequest("/api/ingest/$system/default/clef", HttpMethod.Post);
		req.Content = new StringContent(
			$$"""{"@t":"2024-01-01T00:00:00Z","@l":"Info","@m":"{{UniqueMsg("c1")}}"}""",
			Encoding.UTF8, "text/plain");
		using var resp = await _client.SendAsync(req);
		resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
	}

	[Fact]
	public async Task Query_Events_EndToEnd()
	{
		var before = await TotalCount();
		var msg = UniqueMsg("d1");
		var msgLast = UniqueMsg("d2");

		await PostClefAsync("svc-d",
			$$"""
			{"@t":"2024-01-01T00:00:00Z","@l":"Info","@m":"{{msg}}"}
			{"@t":"2024-01-01T00:00:01Z","@l":"Error","@m":"{{msgLast}}"}
			""");

		await WaitForIngestAsync(msgLast);
		var after = await TotalCount();
		after.Should().Be(before + 2);

		var doc = await QueryAsync($"events | where Message == \"{msg}\" | take 1");
		doc.RootElement.GetProperty("count").GetInt32().Should().Be(1);
		doc.RootElement.GetProperty("events")[0].GetProperty("Level").GetString().Should().Be("Information");
	}

	[Fact]
	public async Task Query_WhereLevel_Basic()
	{
		var msgError = UniqueMsg("e2");
		var msgWarn = UniqueMsg("e3");

		await PostClefAsync("svc-e",
			$$"""
			{"@t":"2024-01-01T00:00:00Z","@l":"Info","@m":"{{UniqueMsg("e1")}}"}
			{"@t":"2024-01-01T00:00:01Z","@l":"Error","@m":"{{msgError}}"}
			{"@t":"2024-01-01T00:00:02Z","@l":"Warning","@m":"{{msgWarn}}"}
			""");

		await WaitForIngestAsync(msgWarn);
		var doc = await QueryAsync($"events | where Message == \"{msgError}\" | take 1");
		doc.RootElement.GetProperty("count").GetInt32().Should().Be(1);
		doc.RootElement.GetProperty("events")[0].GetProperty("Level").GetString().Should().Be("Error");

		var doc2 = await QueryAsync($"events | where Message == \"{msgWarn}\" | take 1");
		doc2.RootElement.GetProperty("events")[0].GetProperty("Level").GetString().Should().Be("Warning");
	}

	[Fact]
	public async Task Query_WhereMessageContains()
	{
		var needle = $"ctest_{Guid.NewGuid():N}";
		var msg1 = $"hello {needle} world";
		var msg2 = "goodbye";

		await PostClefAsync("svc-f",
			$$"""
			{"@t":"2024-01-01T00:00:00Z","@l":"Info","@m":"{{msg1}}"}
			{"@t":"2024-01-01T00:00:01Z","@l":"Error","@m":"{{msg2}}"}
			{"@t":"2024-01-01T00:00:02Z","@l":"Info","@m":"prefix {{needle}} suffix"}
			""");

		await WaitForIngestAsync($"prefix {needle} suffix");
		var doc = await QueryAsync($"events | where Message contains \"{needle}\" | take 10");
		doc.RootElement.GetProperty("count").GetInt32().Should().Be(2);
	}

	[Fact]
	public async Task Query_Count()
	{
		var before = await TotalCount();
		var msgLast = UniqueMsg("g3");
		await PostClefAsync("svc-g",
			$$"""
			{"@t":"2024-01-01T00:00:00Z","@l":"Info","@m":"{{UniqueMsg("g1")}}"}
			{"@t":"2024-01-01T00:00:01Z","@l":"Info","@m":"{{UniqueMsg("g2")}}"}
			{"@t":"2024-01-01T00:00:02Z","@l":"Error","@m":"{{msgLast}}"}
			""");

		await WaitForIngestAsync(msgLast);
		var after = await TotalCount();
		after.Should().Be(before + 3);
	}

	[Fact]
	public async Task Query_SummarizeCountByLevel()
	{
		var before = await TotalCount();
		var msgLast = UniqueMsg("h2");
		await PostClefAsync("svc-h",
			$$"""
			{"@t":"2024-01-01T00:00:00Z","@l":"Info","@m":"{{UniqueMsg("h1")}}"}
			{"@t":"2024-01-01T00:00:01Z","@l":"Error","@m":"{{msgLast}}"}
			""");

		await WaitForIngestAsync(msgLast);
		var after = await TotalCount();
		after.Should().Be(before + 2);

		var doc = await QueryAsync("events | summarize count() by Level");
		var rows = doc.RootElement.GetProperty("rows");
		rows.GetArrayLength().Should().BeGreaterThanOrEqualTo(1);
	}

	[Fact]
	public async Task Query_BadKql_Returns400()
	{
		var req = LogRequest("/api/logs/$system/default/query?q=not%20valid%20kql");
		using var resp = await _client.SendAsync(req);
		resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
	}

	[Fact]
	public async Task Query_UnsupportedKql_Returns400()
	{
		// Parses fine but 'Level' is not a table root (events/spans are) —
		// UnsupportedKqlException is a user error, not a 500.
		var req = LogRequest($"/api/logs/$system/default/query?q={Uri.EscapeDataString("Level | take 1")}");
		using var resp = await _client.SendAsync(req);
		resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
		var body = await resp.Content.ReadAsStringAsync();
		body.Should().Contain("events");
	}

	[Fact]
	public async Task Query_ExecutionError_ReturnsStructuredJson500()
	{
		// Valid syntax, passes the transformer (matches regex is supported), but the PATTERN is
		// malformed — the failure happens at EXECUTION (inside the native sqlean regexp_like invocation
		// during materialization), not at parse. That used to escape as the HTML /Error page;
		// an API caller must get structured JSON with the failure type and message. (This test used to
		// ride on `where LevelName == ...` being untranslatable; LevelName now translates via a CASE
		// mapping — the spans-review fix 1 — so a malformed regex is the execution-fault vehicle.)
		// The scalar function only runs when a row is scanned — seed one so an empty log
		// (possible under test-order variance) can't turn the fault into an empty 200.
		var seed = UniqueMsg("regex-fault");
		await PostClefAsync("svc-regex-fault",
			$$"""{"@t":"2024-01-01T00:00:00Z","@l":"Info","@m":"{{seed}}"}""");
		await WaitForIngestAsync(seed);
		var kql = "events | where Message matches regex \"(\"";
		var req = LogRequest($"/api/logs/$system/default/query?q={Uri.EscapeDataString(kql)}");
		using var resp = await _client.SendAsync(req);

		resp.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
		resp.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
		var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
		doc.RootElement.GetProperty("error").GetString().Should().Contain("KQL execution failed");
		doc.RootElement.GetProperty("type").GetString().Should().NotBeNullOrEmpty();
	}

	[Fact]
	public async Task Query_ExecutionError_TablePath_ReturnsStructuredJson500()
	{
		// Shape-changing pipeline whose pre-filter fails during row STREAMING — the
		// engine fault surfaces in the endpoint's await-foreach over Rows, a different
		// code path than events materialization. Must still be structured JSON.
		// Seed a row for the same reason as the materialization variant above.
		var seed = UniqueMsg("regex-fault-t");
		await PostClefAsync("svc-regex-fault",
			$$"""{"@t":"2024-01-01T00:00:00Z","@l":"Info","@m":"{{seed}}"}""");
		await WaitForIngestAsync(seed);
		var kql = "events | where Message matches regex \"(\" | summarize count() by Level";
		var req = LogRequest($"/api/logs/$system/default/query?q={Uri.EscapeDataString(kql)}");
		using var resp = await _client.SendAsync(req);

		resp.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
		resp.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
		var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
		doc.RootElement.GetProperty("error").GetString().Should().Contain("KQL execution failed");
		doc.RootElement.GetProperty("type").GetString().Should().NotBeNullOrEmpty();
	}

	[Fact]
	public async Task Query_WithoutApiKey_Returns401()
	{
		using var resp = await _client.GetAsync("/api/logs/$system/default/query?q=events");
		resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
	}

	[Fact]
	public async Task Services_ReturnsDistinctKeys()
	{
		var svc = $"svc-i-{Guid.NewGuid():N}"[..12];
		var msg = UniqueMsg("i1");
		await PostClefAsync(svc,
			$$"""{"@t":"2024-01-01T00:00:00Z","@l":"Info","@m":"{{msg}}"}""");
		await WaitForIngestAsync(msg);

		var req = LogRequest("/api/logs/$system/default/services");
		using var resp = await _client.SendAsync(req);
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var body = await resp.Content.ReadAsStringAsync();
		var arr = JsonDocument.Parse(body).RootElement;
		arr.ValueKind.Should().Be(JsonValueKind.Array);
		arr.EnumerateArray().Select(x => x.GetString()).Should().Contain(svc);
	}

	[Fact]
	public async Task AuthValidate_ValidKey_Returns200()
	{
		var req = new HttpRequestMessage(HttpMethod.Get, "/api/auth/validate");
		req.Headers.Add("X-Api-Key", ApiKey);
		using var resp = await _client.SendAsync(req);
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
		doc.RootElement.GetProperty("project").GetString().Should().Be("$system");
		// The key is project-scoped; the project's row names its workspace. Clients (the CLI)
		// read this instead of guessing a personal workspace.
		doc.RootElement.GetProperty("workspace").GetString().Should().Be("$system");
	}

	// The workspace must come from the PROJECT ROW, not from the project key: a key scoped to a
	// project inside another workspace reports that workspace.
	[Fact]
	public async Task AuthValidate_ValidKey_ReportsProjectsWorkspace()
	{
		const string key = "yb_key_ws_probe";
		const string project = "wsprobe";
		using (var scope = _factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<PetBoxDb>();
			await db.InsertAsync(new Project { Key = project, WorkspaceKey = "acme", Name = project });
			await db.InsertAsync(new ApiKey
			{
				Key = key,
				ProjectKey = project,
				Scopes = "logs:read",
				Name = key,
				CreatedAt = DateTime.UtcNow,
			});
		}

		var req = new HttpRequestMessage(HttpMethod.Get, "/api/auth/validate");
		req.Headers.Add("X-Api-Key", key);
		using var resp = await _client.SendAsync(req);
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var root = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
		root.GetProperty("project").GetString().Should().Be(project);
		root.GetProperty("workspace").GetString().Should().Be("acme");
	}

	[Fact]
	public async Task AuthValidate_InvalidKey_Returns401()
	{
		var req = new HttpRequestMessage(HttpMethod.Get, "/api/auth/validate");
		req.Headers.Add("X-Api-Key", "bad_key");
		using var resp = await _client.SendAsync(req);
		resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
	}

	[Fact]
	public async Task AuthValidate_NoKey_Returns401()
	{
		using var resp = await _client.GetAsync("/api/auth/validate");
		resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
	}

	// Log-page HTML rendering moved to workspace/project-scoped routes (/ui/{ws}/{proj})
	// during the IA rework and is covered by the Playwright E2E suite. The old /ui/logs
	// smoke tests were deleted rather than re-pointed (low-value HTML-contains duplicates).

	[Fact]
	public async Task SeqIngest_ValidKey_ReturnsOk()
	{
		var msg = UniqueMsg("seq1");
		var req = new HttpRequestMessage(HttpMethod.Post, "/api/events/raw");
		req.Headers.Add("X-Seq-ApiKey", ApiKey);
		req.Content = new StringContent(
			$$"""{"@t":"2024-01-01T00:00:00Z","@l":"Info","@m":"{{msg}}"}""" + "\n",
			Encoding.UTF8, "text/plain");
		using var resp = await _client.SendAsync(req);
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
	}

	[Fact]
	public async Task SeqIngest_BadKey_Returns401()
	{
		var req = new HttpRequestMessage(HttpMethod.Post, "/api/events/raw");
		req.Headers.Add("X-Seq-ApiKey", "bad_key");
		req.Content = new StringContent(
			"""{"@t":"2024-01-01T00:00:00Z","@l":"Info","@m":"test"}""" + "\n",
			Encoding.UTF8, "text/plain");
		using var resp = await _client.SendAsync(req);
		resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
	}

	[Fact]
	public async Task SeqIngest_NoKey_Returns401()
	{
		var req = new HttpRequestMessage(HttpMethod.Post, "/api/events/raw");
		req.Content = new StringContent(
			"""{"@t":"2024-01-01T00:00:00Z","@l":"Info","@m":"test"}""" + "\n",
			Encoding.UTF8, "text/plain");
		using var resp = await _client.SendAsync(req);
		resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
	}

	[Fact]
	public async Task SeqIngest_EndToEnd_AppearsInKql()
	{
		var msg = UniqueMsg("seq-e2e");

		var req = new HttpRequestMessage(HttpMethod.Post, "/api/events/raw");
		req.Headers.Add("X-Seq-ApiKey", ApiKey);
		req.Content = new StringContent(
			$$"""{"@t":"2024-01-01T00:00:00Z","@l":"Error","@m":"{{msg}}"}""" + "\n",
			Encoding.UTF8, "text/plain");
		using var resp = await _client.SendAsync(req);
		resp.StatusCode.Should().Be(HttpStatusCode.OK);

		// Seq self-log lands in the petbox self-log ($system/petbox), not default.
		await WaitForIngestAsync(msg, "$system", "petbox");
		var doc = await QueryAsync($"events | where Message == \"{msg}\" | take 1", "$system", "petbox");
		doc.RootElement.GetProperty("count").GetInt32().Should().Be(1);
		var evt = doc.RootElement.GetProperty("events")[0];
		evt.GetProperty("Level").GetString().Should().Be("Error");
		evt.GetProperty("ServiceKey").GetString().Should().Be("petbox-web");
	}

	[Fact]
	public async Task Query_WhereServiceKey_Equality()
	{
		var svcA = $"svc-wsk-{Guid.NewGuid():N}"[..12];
		var svcB = $"svc-wsk-{Guid.NewGuid():N}"[..12];
		var msgA = UniqueMsg("ska");
		var msgB = UniqueMsg("skb");

		await PostClefAsync(svcA,
			$$"""{"@t":"2024-01-01T00:00:00Z","@l":"Info","@m":"{{msgA}}"}""");
		await PostClefAsync(svcB,
			$$"""{"@t":"2024-01-01T00:00:01Z","@l":"Info","@m":"{{msgB}}"}""");
		await WaitForIngestAsync(msgB); // FIFO: msgB visible ⇒ msgA landed too

		var doc = await QueryAsync($"events | where ServiceKey == \"{svcA}\" | take 10");
		doc.RootElement.GetProperty("count").GetInt32().Should().Be(1);
		doc.RootElement.GetProperty("events")[0].GetProperty("Message").GetString().Should().Contain(msgA);
	}

	// --- Seq header-routed ingest by project key (no log in URL) ---

	async Task SeedProjectKeyAsync(string apiKey, string projectKey, string scopes, bool createDefaultLog)
	{
		using var scope = _factory.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<PetBoxDb>();
		await db.InsertAsync(new Project
		{
			Key = projectKey,
			WorkspaceKey = LogNames.SystemProject,
			Name = projectKey,
		});
		await db.InsertAsync(new ApiKey
		{
			Key = apiKey,
			ProjectKey = projectKey,
			Scopes = scopes,
			Name = apiKey,
			CreatedAt = DateTime.UtcNow,
		});
		if (createDefaultLog)
		{
			var store = scope.ServiceProvider.GetRequiredService<ILogStore>();
			if (!await store.ExistsAsync(projectKey, LogNames.Default))
				await store.CreateAsync(projectKey, LogNames.Default, null);
		}
	}

	int ProjectLogCount(string projectKey, string logName, string message)
	{
		using var scope = _factory.Services.CreateScope();
		var store = scope.ServiceProvider.GetRequiredService<ILogStore>();
		var ctx = store.GetContext(projectKey, logName);
		return ctx.LogEntries.Count(e => e.Message == message);
	}

	static HttpRequestMessage SeqRequest(string apiKey, string jsonl, string? serviceKey = null)
	{
		var req = new HttpRequestMessage(HttpMethod.Post, "/api/events/raw");
		req.Headers.Add("X-Seq-ApiKey", apiKey);
		if (serviceKey is not null) req.Headers.Add("X-Service-Key", serviceKey);
		req.Content = new StringContent(jsonl, Encoding.UTF8, "text/plain");
		return req;
	}

	[Fact]
	public async Task SeqIngest_ProjectKey_RoutesToProjectDefaultLog()
	{
		var proj = $"seqproj{Guid.NewGuid():N}"[..16];
		var key = $"yb_key_{Guid.NewGuid():N}";
		await SeedProjectKeyAsync(key, proj, "logs:ingest,logs:query", createDefaultLog: true);

		var msg = UniqueMsg("seq-proj");
		using var resp = await _client.SendAsync(SeqRequest(key,
			$$"""{"@t":"2024-01-01T00:00:00Z","@l":"Info","@m":"{{msg}}"}""" + "\n"));
		resp.StatusCode.Should().Be(HttpStatusCode.OK);

		// Lands in the project's OWN default log — not petbox's $system self-log.
		await WaitForProjectIngestAsync(proj, LogNames.Default, msg);
		ProjectLogCount(proj, LogNames.Default, msg).Should().Be(1);
		ProjectLogCount(LogNames.SystemProject, LogNames.SelfLog, msg).Should().Be(0);
	}

	[Fact]
	public async Task SeqIngest_ProjectKey_MissingDefaultLog_Returns404()
	{
		var proj = $"seqproj{Guid.NewGuid():N}"[..16];
		var key = $"yb_key_{Guid.NewGuid():N}";
		await SeedProjectKeyAsync(key, proj, "logs:ingest", createDefaultLog: false);

		using var resp = await _client.SendAsync(SeqRequest(key,
			$$"""{"@t":"2024-01-01T00:00:00Z","@l":"Info","@m":"{{UniqueMsg("seq-404")}}"}""" + "\n"));
		resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
		(await resp.Content.ReadAsStringAsync()).Should().Contain("not found");
	}

	[Fact]
	public async Task SeqIngest_ProjectKey_WithoutIngestScope_Returns403()
	{
		var proj = $"seqproj{Guid.NewGuid():N}"[..16];
		var key = $"yb_key_{Guid.NewGuid():N}";
		await SeedProjectKeyAsync(key, proj, "logs:query", createDefaultLog: true);

		using var resp = await _client.SendAsync(SeqRequest(key,
			$$"""{"@t":"2024-01-01T00:00:00Z","@l":"Info","@m":"{{UniqueMsg("seq-403")}}"}""" + "\n"));
		resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
	}

	// --- Log read endpoints enforce project ownership + logs:query scope ---

	async Task<HttpResponseMessage> SendWithKeyAsync(string apiKey, string path)
	{
		var req = new HttpRequestMessage(HttpMethod.Get, path);
		req.Headers.Add("X-Api-Key", apiKey);
		return await _client.SendAsync(req);
	}

	[Fact]
	public async Task LogQuery_ForeignProject_Returns403()
	{
		var proj = $"seqproj{Guid.NewGuid():N}"[..16];
		var key = $"yb_key_{Guid.NewGuid():N}";
		await SeedProjectKeyAsync(key, proj, "logs:query,logs:ingest", createDefaultLog: true);

		// proj's key must not be able to read the foreign $system/default log.
		using var resp = await SendWithKeyAsync(key, "/api/logs/$system/default/query?q=events%20%7C%20count");
		resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
	}

	[Fact]
	public async Task LogQuery_OwnProject_WithScope_Returns200()
	{
		var proj = $"seqproj{Guid.NewGuid():N}"[..16];
		var key = $"yb_key_{Guid.NewGuid():N}";
		await SeedProjectKeyAsync(key, proj, "logs:query", createDefaultLog: true);

		using var resp = await SendWithKeyAsync(key, $"/api/logs/{proj}/default/query?q=events%20%7C%20count");
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
	}

	[Fact]
	public async Task LogQuery_OwnProject_WithoutQueryScope_Returns403()
	{
		var proj = $"seqproj{Guid.NewGuid():N}"[..16];
		var key = $"yb_key_{Guid.NewGuid():N}";
		await SeedProjectKeyAsync(key, proj, "logs:ingest", createDefaultLog: true);

		using var resp = await SendWithKeyAsync(key, $"/api/logs/{proj}/default/query?q=events%20%7C%20count");
		resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
	}

	[Fact]
	public async Task LogServices_ForeignProject_Returns403()
	{
		var proj = $"seqproj{Guid.NewGuid():N}"[..16];
		var key = $"yb_key_{Guid.NewGuid():N}";
		await SeedProjectKeyAsync(key, proj, "logs:query,logs:ingest", createDefaultLog: true);

		using var resp = await SendWithKeyAsync(key, "/api/logs/$system/default/services");
		resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
	}

	// --- application/json {"Events":[…]} envelope (seq-logging / @datalust/winston-seq parity) ---

	static HttpRequestMessage JsonEnvelope(string path, string apiKey, string apiKeyHeader, string serviceKey, string json)
	{
		var req = new HttpRequestMessage(HttpMethod.Post, path);
		req.Headers.Add(apiKeyHeader, apiKey);
		if (serviceKey is not null) req.Headers.Add("X-Service-Key", serviceKey);
		req.Content = new StringContent(json, Encoding.UTF8, "application/json");
		return req;
	}

	[Fact]
	public async Task Ingest_JsonEnvelope_ClefEvents_Lands()
	{
		var msg = UniqueMsg("env-clef");
		var body = $$"""{"Events":[{"@t":"2024-01-01T00:00:00Z","@l":"Info","@m":"{{msg}}"}]}""";
		using var resp = await _client.SendAsync(
			JsonEnvelope("/api/ingest/$system/default/clef", ApiKey, "X-Api-Key", "env-svc", body));
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		JsonDocument.Parse(await resp.Content.ReadAsStringAsync())
			.RootElement.GetProperty("ingested").GetInt32().Should().Be(1);

		await WaitForIngestAsync(msg);
		var q = await QueryAsync($"events | where Message == \"{msg}\" | take 1");
		q.RootElement.GetProperty("count").GetInt32().Should().Be(1);
	}

	[Fact]
	public async Task Ingest_JsonEnvelope_RawSeqEvents_NormalizedAndLands()
	{
		// seq-logging's legacy Raw shape — what @datalust/winston-seq posts.
		var msg = UniqueMsg("env-raw");
		var body = $$$"""{"Events":[{"Timestamp":"2024-01-01T00:00:00.000Z","Level":"Warning","MessageTemplate":"{{{msg}}}","Properties":{"Sha":"abc123"}}]}""";
		using var resp = await _client.SendAsync(
			JsonEnvelope("/api/ingest/$system/default/clef", ApiKey, "X-Api-Key", "env-raw-svc", body));
		resp.StatusCode.Should().Be(HttpStatusCode.OK);

		await WaitForIngestAsync(msg);
		var doc = await QueryAsync($"events | where Message == \"{msg}\" | take 1");
		doc.RootElement.GetProperty("count").GetInt32().Should().Be(1);
		var evt = doc.RootElement.GetProperty("events")[0];
		evt.GetProperty("Level").GetString().Should().Be("Warning");
		evt.GetProperty("Properties").GetProperty("Sha").GetString().Should().Contain("abc123");
	}

	[Fact]
	public async Task SeqIngest_JsonEnvelope_RawSeqEvents_RoutesToProjectDefault()
	{
		// The full @datalust/winston-seq path: application/json {"Events":[Raw]} POSTed to
		// /api/events/raw with a project key → lands in the project's default log.
		var proj = $"seqproj{Guid.NewGuid():N}"[..16];
		var key = $"yb_key_{Guid.NewGuid():N}";
		await SeedProjectKeyAsync(key, proj, "logs:ingest", createDefaultLog: true);

		var msg = UniqueMsg("seq-env-raw");
		var body = $$"""{"Events":[{"Timestamp":"2024-01-01T00:00:00.000Z","Level":"Error","MessageTemplate":"{{msg}}"}]}""";
		using var resp = await _client.SendAsync(
			JsonEnvelope("/api/events/raw", key, "X-Seq-ApiKey", null!, body));
		resp.StatusCode.Should().Be(HttpStatusCode.OK);

		await WaitForProjectIngestAsync(proj, LogNames.Default, msg);
		ProjectLogCount(proj, LogNames.Default, msg).Should().Be(1);
	}

	[Fact]
	public async Task Ingest_JsonEnvelope_Malformed_Returns400()
	{
		// application/json that is not a {"Events":[…]} envelope is rejected.
		using var resp = await _client.SendAsync(
			JsonEnvelope("/api/ingest/$system/default/clef", ApiKey, "X-Api-Key", "env-bad", """{"not":"an envelope"}"""));
		resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
	}

	// --- Compat ingest: stock Seq client into a NAMED log (…/compat/seq) ---

	static HttpRequestMessage CompatSeqRequest(
		string projectKey, string logName, string apiKey, string jsonl, string? serviceKey = null)
	{
		// A stock Seq client appends `api/events/raw` to its configured serverUrl
		// (= …/compat/seq), posts CLEF NDJSON and authenticates with X-Seq-ApiKey only.
		var req = new HttpRequestMessage(
			HttpMethod.Post, $"/api/ingest/{projectKey}/{logName}/compat/seq/api/events/raw");
		req.Headers.Add("X-Seq-ApiKey", apiKey);
		if (serviceKey is not null) req.Headers.Add("X-Service-Key", serviceKey);
		req.Content = new StringContent(jsonl, Encoding.UTF8, "application/vnd.serilog.clef");
		return req;
	}

	async Task CreateNamedLogAsync(string projectKey, string logName)
	{
		using var scope = _factory.Services.CreateScope();
		var store = scope.ServiceProvider.GetRequiredService<ILogStore>();
		if (!await store.ExistsAsync(projectKey, logName))
			await store.CreateAsync(projectKey, logName, null);
	}

	string? ProjectLogServiceKey(string projectKey, string logName, string message)
	{
		using var scope = _factory.Services.CreateScope();
		var store = scope.ServiceProvider.GetRequiredService<ILogStore>();
		var ctx = store.GetContext(projectKey, logName);
		return ctx.LogEntries.Where(e => e.Message == message).Select(e => e.ServiceKey).FirstOrDefault();
	}

	[Fact]
	public async Task CompatSeq_NamedLog_LandsWithSeqServiceKey()
	{
		var proj = $"seqproj{Guid.NewGuid():N}"[..16];
		var key = $"yb_key_{Guid.NewGuid():N}";
		await SeedProjectKeyAsync(key, proj, "logs:ingest", createDefaultLog: false);
		await CreateNamedLogAsync(proj, "backend");

		var msg = UniqueMsg("compat-seq");
		using var resp = await _client.SendAsync(CompatSeqRequest(proj, "backend", key,
			$$"""{"@t":"2024-01-01T00:00:00Z","@l":"Info","@m":"{{msg}}"}""" + "\n"));
		resp.StatusCode.Should().Be(HttpStatusCode.OK);

		await WaitForProjectIngestAsync(proj, "backend", msg);
		ProjectLogCount(proj, "backend", msg).Should().Be(1);
		ProjectLogServiceKey(proj, "backend", msg).Should().Be("seq");
	}

	[Fact]
	public async Task CompatSeq_ServiceKeyHeader_Respected()
	{
		var proj = $"seqproj{Guid.NewGuid():N}"[..16];
		var key = $"yb_key_{Guid.NewGuid():N}";
		await SeedProjectKeyAsync(key, proj, "logs:ingest", createDefaultLog: false);
		await CreateNamedLogAsync(proj, "backend");

		var msg = UniqueMsg("compat-svc");
		using var resp = await _client.SendAsync(CompatSeqRequest(proj, "backend", key,
			$$"""{"@t":"2024-01-01T00:00:00Z","@l":"Info","@m":"{{msg}}"}""" + "\n",
			serviceKey: "yobapub"));
		resp.StatusCode.Should().Be(HttpStatusCode.OK);

		await WaitForProjectIngestAsync(proj, "backend", msg);
		ProjectLogServiceKey(proj, "backend", msg).Should().Be("yobapub");
	}

	[Fact]
	public async Task CompatSeq_ForeignProjectKey_Returns403()
	{
		var proj = $"seqproj{Guid.NewGuid():N}"[..16];
		var key = $"yb_key_{Guid.NewGuid():N}";
		await SeedProjectKeyAsync(key, proj, "logs:ingest", createDefaultLog: false);

		// proj's key must not be able to write into the foreign $system/default log.
		using var resp = await _client.SendAsync(CompatSeqRequest("$system", "default", key,
			$$"""{"@t":"2024-01-01T00:00:00Z","@l":"Info","@m":"{{UniqueMsg("compat-403")}}"}""" + "\n"));
		resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
		(await resp.Content.ReadAsStringAsync()).Should().Contain("not authorized for project");
	}

	[Fact]
	public async Task CompatSeq_WithoutIngestScope_Returns403()
	{
		var proj = $"seqproj{Guid.NewGuid():N}"[..16];
		var key = $"yb_key_{Guid.NewGuid():N}";
		await SeedProjectKeyAsync(key, proj, "logs:query", createDefaultLog: false);
		await CreateNamedLogAsync(proj, "backend");

		using var resp = await _client.SendAsync(CompatSeqRequest(proj, "backend", key,
			$$"""{"@t":"2024-01-01T00:00:00Z","@l":"Info","@m":"{{UniqueMsg("compat-noscope")}}"}""" + "\n"));
		resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
		(await resp.Content.ReadAsStringAsync()).Should().Contain("logs:ingest");
	}

	[Fact]
	public async Task CompatSeq_BadKey_Returns401()
	{
		using var resp = await _client.SendAsync(CompatSeqRequest("$system", "default", "bad_key",
			"""{"@t":"2024-01-01T00:00:00Z","@l":"Info","@m":"test"}""" + "\n"));
		resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
	}

	[Fact]
	public async Task CompatSeq_NoKey_Returns401()
	{
		var req = new HttpRequestMessage(HttpMethod.Post, "/api/ingest/$system/default/compat/seq/api/events/raw");
		req.Content = new StringContent(
			"""{"@t":"2024-01-01T00:00:00Z","@l":"Info","@m":"test"}""" + "\n",
			Encoding.UTF8, "application/vnd.serilog.clef");
		using var resp = await _client.SendAsync(req);
		resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
	}

	[Fact]
	public async Task CompatSeq_MissingLog_Returns404()
	{
		var proj = $"seqproj{Guid.NewGuid():N}"[..16];
		var key = $"yb_key_{Guid.NewGuid():N}";
		await SeedProjectKeyAsync(key, proj, "logs:ingest", createDefaultLog: false);

		using var resp = await _client.SendAsync(CompatSeqRequest(proj, "backend", key,
			$$"""{"@t":"2024-01-01T00:00:00Z","@l":"Info","@m":"{{UniqueMsg("compat-404")}}"}""" + "\n"));
		resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
		(await resp.Content.ReadAsStringAsync()).Should().Contain("create it first");
	}

	[Fact]
	public async Task CompatSeq_JsonEnvelope_RawSeqEvents_Lands()
	{
		// seq-logging / @datalust/winston-seq parity on the compat route: the same
		// {"Events":[Raw]} envelope handling as the other ingest endpoints.
		var proj = $"seqproj{Guid.NewGuid():N}"[..16];
		var key = $"yb_key_{Guid.NewGuid():N}";
		await SeedProjectKeyAsync(key, proj, "logs:ingest", createDefaultLog: false);
		await CreateNamedLogAsync(proj, "backend");

		var msg = UniqueMsg("compat-env");
		var body = $$"""{"Events":[{"Timestamp":"2024-01-01T00:00:00.000Z","Level":"Error","MessageTemplate":"{{msg}}"}]}""";
		using var resp = await _client.SendAsync(JsonEnvelope(
			$"/api/ingest/{proj}/backend/compat/seq/api/events/raw", key, "X-Seq-ApiKey", null!, body));
		resp.StatusCode.Should().Be(HttpStatusCode.OK);

		await WaitForProjectIngestAsync(proj, "backend", msg);
		ProjectLogCount(proj, "backend", msg).Should().Be(1);
	}

	// --- the `spans` table root over the REST surface (spans-review fixes 2/5/8): the same endpoint
	// routes the spans root through LogQueryService, errors are classified like events, and the
	// unknown-table message truthfully lists both roots on this surface ---

	async Task InsertSpanAsync(string traceId, string spanId, int kind, int status, long durMs)
	{
		using var scope = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
			.CreateScope(_factory.Services);
		var store = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
			.GetRequiredService<PetBox.Log.Core.Data.ILogStore>(scope.ServiceProvider);
		using var db = store.NewEnsuredContext("$system", "default");
		var startNs = new DateTimeOffset(2026, 4, 19, 10, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds() * 1_000_000L;
		await db.InsertAsync(new PetBox.Log.Core.Tracing.SpanRecord
		{
			SpanId = spanId,
			TraceId = traceId,
			Name = "GET /x",
			Kind = kind,
			StartUnixNs = startNs,
			EndUnixNs = startNs + durMs * 1_000_000L,
			StatusCode = status,
			AttributesJson = """{"peer":"eu"}""",
		});
	}

	[Fact]
	public async Task SpansQuery_OverRest_ReturnsTableWithSpanColumns()
	{
		var traceId = Guid.NewGuid().ToString("N");
		await InsertSpanAsync(traceId, "sp-" + Guid.NewGuid().ToString("N")[..8], kind: 2, status: 2, durMs: 400);

		var doc = await QueryAsync(
			$"spans | where TraceId == '{traceId}' and Duration > 200ms | project TraceId, KindName, StatusName, peer");
		var cols = doc.RootElement.GetProperty("columns").EnumerateArray().Select(c => c.GetString()).ToList();
		cols.Should().ContainInOrder("TraceId", "KindName", "StatusName", "peer");
		var rows = doc.RootElement.GetProperty("rows");
		rows.GetArrayLength().Should().Be(1);
		rows[0][0].GetString().Should().Be(traceId);
		rows[0][1].GetString().Should().Be("Client");
		rows[0][2].GetString().Should().Be("Error");
		rows[0][3].GetString().Should().Be("eu");
	}

	[Fact]
	public async Task UnknownTable_OverRest_400_ListsBothRoots()
	{
		var req = LogRequest("/api/logs/$system/default/query?q=" + Uri.EscapeDataString("bogus | take 1"));
		using var resp = await _client.SendAsync(req);
		resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
		var body = await resp.Content.ReadAsStringAsync();
		body.Should().Contain("events").And.Contain("spans");
	}

	[Fact]
	public async Task SpansUnsupportedConstruct_OverRest_400_SameErrorAsEvents()
	{
		async Task<(HttpStatusCode Status, string Body)> Q(string kql)
		{
			var req = LogRequest("/api/logs/$system/default/query?q=" + Uri.EscapeDataString(kql));
			using var resp = await _client.SendAsync(req);
			return (resp.StatusCode, await resp.Content.ReadAsStringAsync());
		}

		var spans = await Q("spans | sample 3");
		var events = await Q("events | sample 3");
		spans.Status.Should().Be(HttpStatusCode.BadRequest);
		events.Status.Should().Be(HttpStatusCode.BadRequest);
		spans.Body.Should().Be(events.Body); // structural-error parity across roots on the same surface
	}

	// --- the `metrics` table root over the REST surface: the same endpoint routes the metrics root
	// through LogQueryService (also the MCP log_query path), yielding a table with the metric columns ---

	async Task InsertMetricAsync(string metricName, int metricType, double? valueDouble, long? valueLong, int timeSec, string attrs)
	{
		using var scope = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
			.CreateScope(_factory.Services);
		var store = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
			.GetRequiredService<PetBox.Log.Core.Data.ILogStore>(scope.ServiceProvider);
		using var db = store.NewEnsuredContext("$system", "default");
		var timeNs = new DateTimeOffset(2026, 4, 19, 10, 0, timeSec, TimeSpan.Zero).ToUnixTimeMilliseconds() * 1_000_000L;
		await db.InsertAsync(new PetBox.Log.Core.Metrics.MetricPointRecord
		{
			MetricName = metricName,
			MetricType = metricType,
			TimeUnixNs = timeNs,
			ValueDouble = valueDouble,
			ValueLong = valueLong,
			AttributesJson = attrs,
		});
	}

	[Fact]
	public async Task MetricsQuery_OverRest_ReturnsTableWithMetricColumns()
	{
		var metricName = "kql.metric." + Guid.NewGuid().ToString("N")[..8];
		await InsertMetricAsync(metricName, metricType: 0, valueDouble: 0.7, valueLong: null, timeSec: 5, attrs: """{"host":"eu"}""");
		await InsertMetricAsync(metricName, metricType: 1, valueDouble: null, valueLong: 42, timeSec: 6, attrs: """{"host":"us"}""");

		var doc = await QueryAsync(
			$"metrics | where MetricName == '{metricName}' | project MetricName, TypeName, Value, host | order by Value asc");
		var cols = doc.RootElement.GetProperty("columns").EnumerateArray().Select(c => c.GetString()).ToList();
		cols.Should().ContainInOrder("MetricName", "TypeName", "Value", "host");
		var rows = doc.RootElement.GetProperty("rows");
		rows.GetArrayLength().Should().Be(2);
		// order by Value asc → Gauge(0.7) then Sum(42, via unified Value).
		rows[0][1].GetString().Should().Be("Gauge");
		rows[0][2].GetDouble().Should().Be(0.7);
		rows[0][3].GetString().Should().Be("eu");
		rows[1][1].GetString().Should().Be("Sum");
		rows[1][2].GetDouble().Should().Be(42);
	}
}
