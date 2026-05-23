using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace YobaBox.Tests.Web;

public sealed class LogPipelineTests : IAsyncLifetime
{
	readonly WebApplicationFactory<Program> _factory;
	HttpClient _client = null!;

	const string ApiKey = "yb_key_system_internal";

	public LogPipelineTests()
	{
		_factory = new WebApplicationFactory<Program>()
			.WithWebHostBuilder(b =>
			{
				b.UseEnvironment("Testing");
				b.ConfigureAppConfiguration((_, cfg) =>
				{
					cfg.AddInMemoryCollection(new Dictionary<string, string?>
					{
						["ConnectionStrings:YobaBox"] = "Data Source=:memory:;Cache=Shared",
						["Features:Logging"] = "true",
					});
				});
			});
	}

	public Task InitializeAsync()
	{
		_client = _factory.CreateClient(new WebApplicationFactoryClientOptions
		{
			AllowAutoRedirect = false,
		});
		return Task.CompletedTask;
	}

	public async Task DisposeAsync()
	{
		_client.Dispose();
		await _factory.DisposeAsync();
	}

	static HttpRequestMessage LogRequest(string path, HttpMethod? method = null)
	{
		var req = new HttpRequestMessage(method ?? HttpMethod.Get, path);
		req.Headers.Add("X-Api-Key", ApiKey);
		return req;
	}

	async Task<HttpResponseMessage> PostClefAsync(string svc, string jsonl)
	{
		var req = LogRequest("/ingest/clef", HttpMethod.Post);
		req.Headers.Add("X-Service-Key", svc);
		req.Content = new StringContent(jsonl, Encoding.UTF8, "text/plain");
		return await _client.SendAsync(req);
	}

	async Task<JsonDocument> QueryAsync(string kql)
	{
		var req = LogRequest($"/api/logs/query?q={Uri.EscapeDataString(kql)}");
		using var resp = await _client.SendAsync(req);
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var body = await resp.Content.ReadAsStringAsync();
		return JsonDocument.Parse(body);
	}

	async Task<int> CountEventsForSvc(string svc)
	{
		var doc = await QueryAsync($"events | where ServiceKey == \"{svc}\" | count");
		return doc.RootElement.GetProperty("rows")[0][0].GetInt32();
	}

	[Fact]
	public async Task Ingest_ValidClef_ReturnsIngestedCount()
	{
		var resp = await PostClefAsync("intg-1",
			"""
			{"@t":"2024-01-01T00:00:00Z","@l":"Info","@m":"hello"}
			{"@t":"2024-01-01T00:00:01Z","@l":"Error","@m":"error msg","@x":"ex"}
			{"@t":"2024-01-01T00:00:02Z","@l":"Warning","@m":"warn","drive":"C:"}
			""");

		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var body = await resp.Content.ReadAsStringAsync();
		var doc = JsonDocument.Parse(body);
		doc.RootElement.GetProperty("ingested").GetInt32().Should().Be(3);
		doc.RootElement.GetProperty("errors").GetInt32().Should().Be(0);
	}

	[Fact]
	public async Task Ingest_WithoutApiKey_Returns401()
	{
		var req = new HttpRequestMessage(HttpMethod.Post, "/ingest/clef");
		req.Headers.Add("X-Service-Key", "intg-2");
		req.Content = new StringContent(
			"""{"@t":"2024-01-01T00:00:00Z","@l":"Info","@m":"hello"}""",
			Encoding.UTF8, "text/plain");
		using var resp = await _client.SendAsync(req);
		resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
	}

	[Fact]
	public async Task Ingest_WithoutServiceKey_Returns400()
	{
		var req = LogRequest("/ingest/clef", HttpMethod.Post);
		req.Content = new StringContent(
			"""{"@t":"2024-01-01T00:00:00Z","@l":"Info","@m":"hello"}""",
			Encoding.UTF8, "text/plain");
		using var resp = await _client.SendAsync(req);
		resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
	}

	[Fact]
	public async Task Query_Events_ReturnsAll()
	{
		await PostClefAsync("intg-3",
			"""
			{"@t":"2024-01-01T00:00:00Z","@l":"Info","@m":"first"}
			{"@t":"2024-01-01T00:00:01Z","@l":"Error","@m":"second"}
			""");

		var c = await CountEventsForSvc("intg-3");
		c.Should().Be(2);
	}

	[Fact]
	public async Task Query_WhereLevel_Basic()
	{
		await PostClefAsync("intg-4",
			"""
			{"@t":"2024-01-01T00:00:00Z","@l":"Info","@m":"info msg"}
			{"@t":"2024-01-01T00:00:01Z","@l":"Error","@m":"error msg"}
			{"@t":"2024-01-01T00:00:02Z","@l":"Warning","@m":"warn msg"}
			""");

		var doc = await QueryAsync("events | where ServiceKey == \"intg-4\" | where Level >= 3 | take 10");
		doc.RootElement.GetProperty("count").GetInt32().Should().Be(2);
		var events = doc.RootElement.GetProperty("events");
		var levels = events.EnumerateArray().Select(e => e.GetProperty("level").GetString()).ToList();
		levels.Should().BeEquivalentTo(["Warning", "Error"]);
	}

	[Fact]
	public async Task Query_WhereMessageContains()
	{
		await PostClefAsync("intg-5",
			"""
			{"@t":"2024-01-01T00:00:00Z","@l":"Info","@m":"hello world"}
			{"@t":"2024-01-01T00:00:01Z","@l":"Error","@m":"goodbye"}
			{"@t":"2024-01-01T00:00:02Z","@l":"Info","@m":"hello again"}
			""");

		var doc = await QueryAsync(
			"events | where ServiceKey == \"intg-5\" | where Message contains \"hello\" | take 10");
		doc.RootElement.GetProperty("count").GetInt32().Should().Be(2);
	}

	[Fact]
	public async Task Query_Count()
	{
		await PostClefAsync("intg-6",
			"""
			{"@t":"2024-01-01T00:00:00Z","@l":"Info","@m":"a"}
			{"@t":"2024-01-01T00:00:01Z","@l":"Info","@m":"b"}
			{"@t":"2024-01-01T00:00:02Z","@l":"Error","@m":"c"}
			""");

		var doc = await QueryAsync("events | where ServiceKey == \"intg-6\" | count");
		var rows = doc.RootElement.GetProperty("rows");
		rows[0][0].GetInt32().Should().Be(3);
	}

	[Fact]
	public async Task Query_SummarizeCountByLevel()
	{
		await PostClefAsync("intg-7",
			"""
			{"@t":"2024-01-01T00:00:00Z","@l":"Info","@m":"a"}
			{"@t":"2024-01-01T00:00:01Z","@l":"Info","@m":"b"}
			{"@t":"2024-01-01T00:00:02Z","@l":"Error","@m":"c"}
			{"@t":"2024-01-01T00:00:03Z","@l":"Error","@m":"d"}
			{"@t":"2024-01-01T00:00:04Z","@l":"Warning","@m":"e"}
			""");

		var doc = await QueryAsync(
			"events | where ServiceKey == \"intg-7\" | summarize count() by Level");
		var rows = doc.RootElement.GetProperty("rows");
		rows.GetArrayLength().Should().Be(3);
	}

	[Fact]
	public async Task Query_BadKql_Returns400()
	{
		await PostClefAsync("intg-8",
			"""{"@t":"2024-01-01T00:00:00Z","@l":"Info","@m":"a"}""");

		var req = LogRequest("/api/logs/query?q=not%20valid%20kql");
		using var resp = await _client.SendAsync(req);
		resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
	}

	[Fact]
	public async Task Query_WithoutApiKey_Returns401()
	{
		using var resp = await _client.GetAsync("/api/logs/query?q=events");
		resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
	}

	[Fact]
	public async Task Services_ReturnsDistinctKeys()
	{
		await PostClefAsync("intg-9",
			"""{"@t":"2024-01-01T00:00:00Z","@l":"Info","@m":"a"}""");

		var req = LogRequest("/api/logs/services");
		using var resp = await _client.SendAsync(req);
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var body = await resp.Content.ReadAsStringAsync();
		var arr = JsonDocument.Parse(body).RootElement;
		arr.ValueKind.Should().Be(JsonValueKind.Array);
		arr.EnumerateArray().Select(x => x.GetString()).Should().Contain("intg-9");
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

	[Fact]
	public async Task LogPage_RendersHtml()
	{
		await PostClefAsync("intg-a",
			"""{"@t":"2024-01-01T00:00:00Z","@l":"Info","@m":"test"}""");

		var resp = await _client.GetAsync("/logs");
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();
		html.Should().Contain("Logs");
		html.Should().Contain("kql-input");
	}

	[Fact]
	public async Task LogPage_WithKql_HtmxFragment()
	{
		await PostClefAsync("intg-b",
			"""{"@t":"2024-01-01T00:00:00Z","@l":"Error","@m":"boom"}""");

		var req = new HttpRequestMessage(HttpMethod.Get, "/logs?kql=events+|+take+10");
		req.Headers.Add("HX-Request", "true");
		using var resp = await _client.SendAsync(req);
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();
		html.Should().Contain("data-testid=\"events-row\"");
	}

	[Fact]
	public async Task LogPage_WithShapeChangingKql_RendersColumns()
	{
		await PostClefAsync("intg-c",
			"""{"@t":"2024-01-01T00:00:00Z","@l":"Info","@m":"test"}""");

		var resp = await _client.GetAsync("/logs?kql=events+|+count");
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();
		html.Should().Contain("Count");
	}

	[Fact]
	public async Task Ingest_ThenQuery_EndToEnd()
	{
		for (var i = 0; i < 10; i++)
		{
			var level = i % 3 == 0 ? "Error" : "Info";
			await PostClefAsync("intg-z",
				$$"""{"@t":"2024-01-01T00:00:{{i:D2}}Z","@l":"{{level}}","@m":"msg{{i}}"}""");
		}

		var doc = await QueryAsync(
			"events | where ServiceKey == \"intg-z\" | where Level >= 4 | take 10");
		var count = doc.RootElement.GetProperty("count").GetInt32();
		count.Should().BeInRange(3, 4);
	}
}
