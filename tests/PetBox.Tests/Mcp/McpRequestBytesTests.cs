using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Web.Mcp;

namespace PetBox.Tests.Mcp;

// card: request-chars-blind-to-escape-inflation, variant A.
//
// McpTracingFilter.SerializedLength re-serializes the ALREADY-PARSED JsonElement, so
// petbox.request_chars / Properties.ReqChars cannot tell a \uXXXX-escaped request from a raw
// UTF-8 one carrying the same value — a decoded 'П' is the same JsonElement either way. That
// blindness is exactly what makes request_chars useless for spotting the 2.7-2.9x byte inflation
// a client incurs by \uXXXX-escaping Cyrillic (measured on prod 2026-07-27). request_bytes fixes
// this by reading Request.ContentLength — the RAW wire size, taken BEFORE JSON parsing — so the
// two numbers together (chars vs bytes) surface the inflation ratio directly.
public sealed class McpRequestBytesUnitTests
{
	static IServiceProvider Services(HttpContext? ctx)
	{
		var accessor = new HttpContextAccessor { HttpContext = ctx };
		return new ServiceCollection().AddSingleton<IHttpContextAccessor>(accessor).BuildServiceProvider();
	}

	[Fact]
	public void ReadsContentLength_WhenPresent()
	{
		var ctx = new DefaultHttpContext();
		ctx.Request.ContentLength = 12345;
		McpTracingFilter.RequestBytes(Services(ctx)).Should().Be(12345L);
	}

	[Fact]
	public void IsNull_WhenContentLengthAbsent()
	{
		// The chunked-transfer case: no Content-Length header at all.
		var ctx = new DefaultHttpContext();
		ctx.Request.ContentLength = null;
		McpTracingFilter.RequestBytes(Services(ctx)).Should().BeNull(
			"an unknown wire size must stay unknown — never backfilled from reqChars");
	}

	[Fact]
	public void IsNull_WhenNoHttpContext()
	{
		McpTracingFilter.RequestBytes(Services(null)).Should().BeNull();
	}

	[Fact]
	public void IsNull_WhenServicesIsNull()
	{
		McpTracingFilter.RequestBytes(null).Should().BeNull();
	}
}

// ---- end-to-end: the pair actually decorrelates on the real HTTP surface ----
//
// Drives two REAL tool calls over the streamable-HTTP MCP surface (raw HttpClient, not the SDK
// client, so the test controls the EXACT wire bytes — the SDK would pick its own encoder) that
// carry the SAME semantic `kql` string, one \uXXXX-escaped and one raw UTF-8, and reads the
// self-log back over REST. The Stateless transport (Program.cs: WithHttpTransport(o =>
// o.Stateless = true)) accepts a bare "tools/call" JSON-RPC request with no prior "initialize"
// handshake (measured empirically against this host), which keeps the wire bytes fully
// hand-crafted with nothing the SDK would inject.
public sealed class McpRequestBytesEndToEndFixture : IAsyncLifetime
{
	// Seeded by M001/M004: $system + a key scoped logs:query — enough to call log_query.
	const string SystemApiKey = "yb_key_system_internal";

	HttpClient _http = null!;

	WebApplicationFactory<Program> Factory { get; }
	public HttpClient Http => _http;

	public McpRequestBytesEndToEndFixture()
	{
		Environment.SetEnvironmentVariable("PETBOX_MASTER_KEY", "test-key-for-secrets");
		Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");

		Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
		{
			b.UseEnvironment("Testing");
			b.UseSetting("Features:Logging", "true");
			b.UseSetting("Seq:SelfLog:Enabled", "true");
			b.ConfigureAppConfiguration((_, cfg) =>
			{
				cfg.AddInMemoryCollection(new Dictionary<string, string?>
				{
					// Uniquely-directoried Core db → its own self-log, so only THIS host's two
					// calls populate the events this test counts.
					["ConnectionStrings:PetBox"] = TestSchema.NewTempConnectionString(),
					["Features:Logging"] = "true",
					["Seq:SelfLog:Enabled"] = "true",
				});
			});
		});
	}

	public async ValueTask InitializeAsync()
	{
		var cs = Factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("PetBox")!;
		TestSchema.Core(cs);
		_http = Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
		_http.DefaultRequestHeaders.Add("X-Api-Key", SystemApiKey);

		using var scope = Factory.Services.CreateScope();
		var store = scope.ServiceProvider.GetRequiredService<ILogStore>();
		if (!await store.ExistsAsync(LogNames.SystemProject, LogNames.SelfLog))
			await store.CreateAsync(LogNames.SystemProject, LogNames.SelfLog, null);
	}

	public async ValueTask DisposeAsync()
	{
		_http.Dispose();
		await Factory.DisposeAsync();
	}
}

public sealed class McpRequestBytesEndToEndTests : IClassFixture<McpRequestBytesEndToEndFixture>
{
	readonly McpRequestBytesEndToEndFixture _fx;

	public McpRequestBytesEndToEndTests(McpRequestBytesEndToEndFixture fx) => _fx = fx;

	// A Cyrillic phrase long enough that the escape-vs-raw byte gap is unmistakable (each
	// character costs 6 ASCII bytes escaped — \uXXXX — vs 2 UTF-8 bytes raw).
	const string CyrillicPhrase = "привет мир тест телеметрии раздувание байт кириллица эскейпинг";

	// The same JSON-RPC "tools/call" envelope, differing ONLY in how the Cyrillic run inside the
	// `kql` string literal is spelled on the wire — \uXXXX-escaped or the raw UTF-8 bytes. Both
	// parse to the IDENTICAL string value, so reqChars (computed from the parsed JsonElement)
	// must come out equal; only reqBytes (Request.ContentLength, read before parsing) can see the
	// wire-level difference.
	static string Envelope(string kqlCyrillicSpelling) =>
		"{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"log_query\"," +
		"\"arguments\":{\"projectKey\":\"" + LogNames.SystemProject + "\",\"logName\":\"" + LogNames.SelfLog + "\"," +
		"\"kql\":\"events | where MessageTemplate contains '" + kqlCyrillicSpelling + "' | take 1\"}}}";

	static string EscapeAsUnicode(string s) =>
		string.Concat(s.Select(c => c > 127 ? $"\\u{(int)c:x4}" : c.ToString()));

	async Task SendRawAsync(string body)
	{
		var req = new HttpRequestMessage(HttpMethod.Post, "/mcp")
		{
			Content = new StringContent(body, Encoding.UTF8, "application/json"),
		};
		req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
		req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

		var resp = await _fx.Http.SendAsync(req);
		resp.StatusCode.Should().Be(HttpStatusCode.OK, "the bare tools/call must be accepted by the stateless transport");
	}

	async Task<JsonDoc> QueryAsync(string kql)
	{
		var url = $"/api/logs/{LogNames.SystemProject}/{LogNames.SelfLog}/query?q={Uri.EscapeDataString(kql)}";
		using var resp = await _fx.Http.GetAsync(url);
		resp.StatusCode.Should().Be(HttpStatusCode.OK, "self-log query must succeed");
		return new JsonDoc(System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync()));
	}

	// KQL cell values can come back as either JSON numbers or numeric strings depending on the
	// column (dynamic Properties.* projections render as strings) — normalize both.
	static long AsLong(System.Text.Json.JsonElement e) => e.ValueKind switch
	{
		System.Text.Json.JsonValueKind.Number => e.GetInt64(),
		System.Text.Json.JsonValueKind.String => long.Parse(e.GetString()!),
		_ => throw new Xunit.Sdk.XunitException($"expected a numeric cell, got {e.ValueKind}"),
	};

	async Task<long> ToolCallCountAsync() =>
		AsLong((await QueryAsync("events | where MessageTemplate contains 'mcp tool' | where Properties.Tool == 'log_query' | count"))
			.Root.GetProperty("rows")[0][0]);

	async Task WaitForCountAsync(long atLeast)
	{
		for (var i = 0; i < 400; i++)
		{
			if (await ToolCallCountAsync() >= atLeast) return;
			await Task.Delay(25);
		}
		throw new Xunit.Sdk.XunitException($"self-log did not reach {atLeast} 'mcp tool log_query' events within 10s");
	}

	[Fact]
	public async Task EscapedAndRawInput_OfIdenticalSemantics_YieldSameReqChars_ButDifferentReqBytes()
	{
		var escapedBody = Envelope(EscapeAsUnicode(CyrillicPhrase));
		var rawBody = Envelope(CyrillicPhrase);

		// Sanity on the fixture itself: the escaped body really is bigger on the wire, and the two
		// requests are NOT byte-identical (otherwise the test would prove nothing).
		var escapedByteCount = Encoding.UTF8.GetByteCount(escapedBody);
		var rawByteCount = Encoding.UTF8.GetByteCount(rawBody);
		escapedByteCount.Should().BeGreaterThan(rawByteCount,
			"escaping every Cyrillic char to \\uXXXX must cost strictly more wire bytes than the raw UTF-8 spelling");

		await SendRawAsync(escapedBody);
		await WaitForCountAsync(1);
		await SendRawAsync(rawBody);
		await WaitForCountAsync(2);

		// Ordered by insertion (Id) so row 0 is unambiguously the escaped call and row 1 the raw one.
		var doc = await QueryAsync(
			"events | where MessageTemplate contains 'mcp tool' | where Properties.Tool == 'log_query' " +
			"| order by Id asc | project ReqChars=Properties.ReqChars, ReqBytes=Properties.ReqBytes");
		var rows = doc.Root.GetProperty("rows");
		rows.GetArrayLength().Should().Be(2, "exactly the two calls this test made");

		var escapedReqChars = AsLong(rows[0][0]);
		var escapedReqBytes = AsLong(rows[0][1]);
		var rawReqChars = AsLong(rows[1][0]);
		var rawReqBytes = AsLong(rows[1][1]);

		// THE claim: request_chars is blind to the escaping — both calls parse to the identical
		// argument value, so the already-parsed-and-reserialized measurement cannot tell them apart.
		escapedReqChars.Should().Be(rawReqChars,
			"request_chars re-serializes the PARSED JsonElement — escaped vs raw UTF-8 input decode to the same value");

		// THE fix: request_bytes reads Content-Length, taken BEFORE parsing, so it sees exactly
		// the wire-level difference request_chars cannot.
		escapedReqBytes.Should().BeGreaterThan(rawReqBytes,
			"request_bytes must see the \\uXXXX-escaping inflation that request_chars is blind to");

		// The measured ratio should land in the same neighborhood as the prod evidence (2.7x-2.9x
		// for Cyrillic-heavy payloads) rather than some unrelated artifact of the envelope's fixed
		// (non-Cyrillic) JSON scaffolding, which is identical between the two calls and dilutes the
		// ratio slightly below the pure-payload 3x.
		((double)escapedReqBytes / rawReqBytes).Should().BeGreaterThan(1.5,
			"the byte gap must be substantial, not a rounding artifact");
	}
}

// Minimal wrapper so the fixture's JsonDocument disposal doesn't need to leak into every call site.
sealed class JsonDoc(System.Text.Json.JsonDocument doc) : IDisposable
{
	public System.Text.Json.JsonElement Root => doc.RootElement;
	public void Dispose() => doc.Dispose();
}
