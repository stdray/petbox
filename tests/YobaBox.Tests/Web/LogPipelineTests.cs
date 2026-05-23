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
		var req = new HttpRequestMessage(HttpMethod.Post, "/ingest/clef");
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
		var req = LogRequest("/ingest/clef", HttpMethod.Post);
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
		var svc = $"svc-i-{Guid.NewGuid():N}"[..12];
		await PostClefAsync(svc,
			$$"""{"@t":"2024-01-01T00:00:00Z","@l":"Info","@m":"{{UniqueMsg("i1")}}"}""");

		var req = LogRequest("/api/logs/services");
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

	[Fact]
	public async Task LogPage_RendersHtml()
	{
		var resp = await _client.GetAsync("/logs");
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();
		html.Should().Contain("Logs");
		html.Should().Contain("kql-input");
	}

	[Fact]
	public async Task LogPage_WithKql_HtmxFragment()
	{
		await PostClefAsync("svc-j",
			$$"""{"@t":"2024-01-01T00:00:00Z","@l":"Error","@m":"{{UniqueMsg("j1")}}"}""");

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
		var resp = await _client.GetAsync("/logs?kql=events+|+count");
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();
		html.Should().Contain("Count");
	}
}
