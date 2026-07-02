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

[Collection("WebAppFactory")]
public sealed class LogPipelineTests : IAsyncLifetime
{
	readonly WebApplicationFactory<Program> _factory;
	HttpClient _client = null!;

	const string ApiKey = "yb_key_system_internal";
	const string TestPassword = "test123";
	const string TestPasswordHash = "pbkdf2$100000$h1twJi/he3s8S7jSM9pkGQ==$efnLBffww5Gprn6BjpNgZkTcG+1zNu2L6z3TZ7YvD/o=";

	public LogPipelineTests()
	{
		// WebApplication.CreateBuilder reads ASPNETCORE_ENVIRONMENT at construction
		// — before WithWebHostBuilder.UseEnvironment("Testing") gets a chance to
		// apply — so set it here. Without this, on Linux CI the env defaults to
		// Production, appsettings.Testing.json never loads, Features:Logging is
		// false at ConfigureServices time, and IIngestionPipeline isn't registered.
		Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");
		var dbPath = Path.Combine(Path.GetTempPath(), $"petbox-test-{Guid.NewGuid():N}.db");
		_factory = new WebApplicationFactory<Program>()
			.WithWebHostBuilder(b =>
			{
				b.UseEnvironment("Testing");
				b.ConfigureAppConfiguration((_, cfg) =>
				{
					cfg.AddInMemoryCollection(new Dictionary<string, string?>
					{
						["ConnectionStrings:PetBox"] = $"Data Source={dbPath};Cache=Shared",
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
		var __testCs = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>(_factory.Services).GetConnectionString("PetBox")!;
		TestSchema.Core(__testCs);
		_client = _factory.CreateClient(new WebApplicationFactoryClientOptions
		{
			AllowAutoRedirect = false,
		});

		// Logs are explicit now; create the $system/default log these tests ingest into.
		using var scope = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
			.CreateScope(_factory.Services);
		var store = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
			.GetRequiredService<PetBox.Log.Core.Data.ILogStore>(scope.ServiceProvider);
		if (!await store.ExistsAsync("$system", "default"))
			await store.CreateAsync("$system", "default", null);
	}

	public async Task DisposeAsync()
	{
		_client.Dispose();
		await _factory.DisposeAsync();
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

	[Fact]
	public async Task Ingest_ValidClef_ReturnsIngestedCount()
	{
		var msg = UniqueMsg("a1");
		var resp = await PostClefAsync("svc-a",
			$$"""
			{"@t":"2024-01-01T00:00:00Z","@l":"Info","@m":"{{msg}}"}
			{"@t":"2024-01-01T00:00:01Z","@l":"Error","@m":"{{UniqueMsg("a2")}}","@x":"ex"}
			{"@t":"2024-01-01T00:00:02Z","@l":"Warning","@m":"{{UniqueMsg("a3")}}","drive":"C:"}
			""");

		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var body = await resp.Content.ReadAsStringAsync();
		var doc = JsonDocument.Parse(body);
		doc.RootElement.GetProperty("ingested").GetInt32().Should().Be(3);
		doc.RootElement.GetProperty("errors").GetInt32().Should().Be(0);

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

		await PostClefAsync("svc-d",
			$$"""
			{"@t":"2024-01-01T00:00:00Z","@l":"Info","@m":"{{msg}}"}
			{"@t":"2024-01-01T00:00:01Z","@l":"Error","@m":"{{UniqueMsg("d2")}}"}
			""");

		var after = await TotalCount();
		after.Should().Be(before + 2);

		var doc = await QueryAsync($"events | where Message == \"{msg}\" | take 1");
		doc.RootElement.GetProperty("count").GetInt32().Should().Be(1);
		doc.RootElement.GetProperty("events")[0].GetProperty("level").GetString().Should().Be("Information");
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

		var doc = await QueryAsync($"events | where Message == \"{msgError}\" | take 1");
		doc.RootElement.GetProperty("count").GetInt32().Should().Be(1);
		doc.RootElement.GetProperty("events")[0].GetProperty("level").GetString().Should().Be("Error");

		var doc2 = await QueryAsync($"events | where Message == \"{msgWarn}\" | take 1");
		doc2.RootElement.GetProperty("events")[0].GetProperty("level").GetString().Should().Be("Warning");
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

		var doc = await QueryAsync($"events | where Message contains \"{needle}\" | take 10");
		doc.RootElement.GetProperty("count").GetInt32().Should().Be(2);
	}

	[Fact]
	public async Task Query_Count()
	{
		var before = await TotalCount();
		await PostClefAsync("svc-g",
			$$"""
			{"@t":"2024-01-01T00:00:00Z","@l":"Info","@m":"{{UniqueMsg("g1")}}"}
			{"@t":"2024-01-01T00:00:01Z","@l":"Info","@m":"{{UniqueMsg("g2")}}"}
			{"@t":"2024-01-01T00:00:02Z","@l":"Error","@m":"{{UniqueMsg("g3")}}"}
			""");

		var after = await TotalCount();
		after.Should().Be(before + 3);
	}

	[Fact]
	public async Task Query_SummarizeCountByLevel()
	{
		var before = await TotalCount();
		await PostClefAsync("svc-h",
			$$"""
			{"@t":"2024-01-01T00:00:00Z","@l":"Info","@m":"{{UniqueMsg("h1")}}"}
			{"@t":"2024-01-01T00:00:01Z","@l":"Error","@m":"{{UniqueMsg("h2")}}"}
			""");

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
		// Parses fine but the transformer rejects it (only the 'events' table exists) —
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
		// Valid syntax, passes the transformer (LevelName is a known column), but the
		// built expression calls a CLR method linq2db cannot translate to SQL — the
		// query fails at EXECUTION (materialization), not at parse. That used to escape
		// as the HTML /Error page; an API caller must get structured JSON with the
		// failure type and message.
		var kql = "events | where LevelName == \"Error\"";
		var req = LogRequest($"/api/logs/$system/default/query?q={Uri.EscapeDataString(kql)}");
		using var resp = await _client.SendAsync(req);

		resp.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
		resp.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
		var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
		doc.RootElement.GetProperty("error").GetString().Should()
			.Contain("KQL execution failed").And.Contain("LevelName");
		doc.RootElement.GetProperty("type").GetString().Should().NotBeNullOrEmpty();
	}

	[Fact]
	public async Task Query_ExecutionError_TablePath_ReturnsStructuredJson500()
	{
		// Shape-changing pipeline whose pre-filter fails during row STREAMING — the
		// engine fault surfaces in the endpoint's await-foreach over Rows, a different
		// code path than events materialization. Must still be structured JSON.
		var kql = "events | where LevelName == \"Error\" | summarize count() by Level";
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
		await PostClefAsync(svc,
			$$"""{"@t":"2024-01-01T00:00:00Z","@l":"Info","@m":"{{UniqueMsg("i1")}}"}""");

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
		var doc = await QueryAsync($"events | where Message == \"{msg}\" | take 1", "$system", "petbox");
		doc.RootElement.GetProperty("count").GetInt32().Should().Be(1);
		var evt = doc.RootElement.GetProperty("events")[0];
		evt.GetProperty("level").GetString().Should().Be("Error");
		evt.GetProperty("serviceKey").GetString().Should().Be("petbox-web");
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

		var doc = await QueryAsync($"events | where ServiceKey == \"{svcA}\" | take 10");
		doc.RootElement.GetProperty("count").GetInt32().Should().Be(1);
		doc.RootElement.GetProperty("events")[0].GetProperty("message").GetString().Should().Contain(msgA);
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

		var doc = await QueryAsync($"events | where Message == \"{msg}\" | take 1");
		doc.RootElement.GetProperty("count").GetInt32().Should().Be(1);
		var evt = doc.RootElement.GetProperty("events")[0];
		evt.GetProperty("level").GetString().Should().Be("Warning");
		evt.GetProperty("properties").GetProperty("Sha").GetString().Should().Contain("abc123");
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

		ProjectLogCount(proj, "backend", msg).Should().Be(1);
	}
}
