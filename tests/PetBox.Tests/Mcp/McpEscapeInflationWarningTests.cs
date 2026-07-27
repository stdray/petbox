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
using PetBox.Memory.Contract;
using PetBox.Web.Mcp;

namespace PetBox.Tests.Mcp;

// ── the scanner, on bytes ─────────────────────────────────────────────────────────────────────
//
// JsonEscapeInflationScanner answers one question about a request body: how many bytes would this
// SAME body cost if its non-ASCII were raw UTF-8 instead of \uXXXX? Everything here feeds it real
// bytes and checks a number computed independently of it.
public sealed class JsonEscapeInflationScannerTests
{
	static JsonEscapeInflationScanner Scan(string wire)
	{
		var scanner = new JsonEscapeInflationScanner();
		scanner.Feed(Encoding.UTF8.GetBytes(wire));
		return scanner;
	}

	// THE property the production defect violated: a body with no escapes in it cannot inflate, at
	// any size. The old formula divided the whole request by a PART of it and reported 2.8x for a
	// body exactly like this one.
	[Theory]
	[InlineData("{}")]
	[InlineData("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"tasks_upsert\"}}")]
	[InlineData("{\"text\":\"plain ascii, no escapes at all, just words and punctuation!\"}")]
	public void PureAscii_MeasuresNoInflation(string wire)
	{
		var scanner = Scan(wire);
		scanner.RawUtf8Bytes.Should().Be(scanner.WireBytes);
	}

	// Raw UTF-8 non-ASCII is already the cheap spelling — nothing to save, ratio 1.0.
	[Fact]
	public void RawUtf8NonAscii_MeasuresNoInflation()
	{
		var scanner = Scan("{\"text\":\"привет мир\"}");
		scanner.WireBytes.Should().Be(Encoding.UTF8.GetByteCount("{\"text\":\"привет мир\"}"));
		scanner.RawUtf8Bytes.Should().Be(scanner.WireBytes);
	}

	// Cyrillic sits in U+0400-U+04FF: 6 wire bytes escaped, 2 raw — 4 saved each.
	[Fact]
	public void EscapedCyrillic_SavesFourBytesPerCharacter()
	{
		const string wire = "{\"text\":\"\\u043f\\u0440\\u0438\\u0432\\u0435\\u0442\"}";
		var scanner = Scan(wire);
		scanner.WireBytes.Should().Be(wire.Length);
		scanner.RawUtf8Bytes.Should().Be(wire.Length - 6 * 4);
	}

	// U+0080-U+07FF costs 2 raw bytes; U+0800 and up costs 3.
	[Theory]
	[InlineData("\\u00e9", 4)] // é — two-byte raw
	[InlineData("\\u4e2d", 3)] // 中 — three-byte raw
	public void SavingTracksTheRawUtf8Width(string escape, int expectedSaving)
	{
		var scanner = Scan(escape);
		scanner.RawUtf8Bytes.Should().Be(scanner.WireBytes - expectedSaving);
	}

	// A surrogate PAIR is one code point: 12 wire bytes, 4 raw.
	[Fact]
	public void SurrogatePair_CountsAsOneFourByteCodePoint()
	{
		var scanner = Scan("\\ud83d\\ude00");
		scanner.WireBytes.Should().Be(12);
		scanner.RawUtf8Bytes.Should().Be(4);
	}

	// An UNPAIRED surrogate has no raw UTF-8 spelling, so no saving may be claimed for it.
	[Theory]
	[InlineData("\\ud83dX")]      // high surrogate, then something else
	[InlineData("\\ude00")]       // lone low surrogate
	[InlineData("\\ud83d")]       // high surrogate at end of stream
	public void UnpairedSurrogate_ClaimsNothing(string wire)
	{
		var scanner = Scan(wire);
		scanner.RawUtf8Bytes.Should().Be(scanner.WireBytes);
	}

	// Control characters MUST be escaped in JSON — a raw-UTF-8 client could not have spent less.
	[Fact]
	public void ControlCharacterEscape_ClaimsNothing()
	{
		var scanner = Scan("{\"text\":\"a\\u0000b\\u001fc\"}");
		scanner.RawUtf8Bytes.Should().Be(scanner.WireBytes);
	}

	// `\\u0441` is an escaped BACKSLASH followed by the literal text "u0441" — not an escape.
	[Fact]
	public void EscapedBackslash_IsNotAnEscapeOpener()
	{
		var scanner = Scan("{\"text\":\"\\\\u0441\"}");
		scanner.RawUtf8Bytes.Should().Be(scanner.WireBytes);
	}

	// The scanner sees the body in whatever chunks the transport hands it, so an escape can
	// straddle any read boundary. The answer must not depend on where the splits fall.
	[Fact]
	public void ResultIsInvariantUnderAnyChunkSplit()
	{
		var bytes = Encoding.UTF8.GetBytes(
			"{\"a\":\"\\u043f\\u0440\\ud83d\\ude00\\\\u0441\\u0000\",\"b\":\"raw привет\"}");
		var whole = new JsonEscapeInflationScanner();
		whole.Feed(bytes);

		for (var split = 1; split < bytes.Length; split++)
		{
			var piecewise = new JsonEscapeInflationScanner();
			piecewise.Feed(bytes.AsSpan(0, split));
			piecewise.Feed(bytes.AsSpan(split));
			piecewise.WireBytes.Should().Be(whole.WireBytes, $"split at {split}");
			piecewise.RawUtf8Bytes.Should().Be(whole.RawUtf8Bytes, $"split at {split}");
		}

		// And byte-at-a-time, the worst case for a stateful scan.
		var oneByOne = new JsonEscapeInflationScanner();
		foreach (var b in bytes) oneByOne.Feed(new[] { b });
		oneByOne.RawUtf8Bytes.Should().Be(whole.RawUtf8Bytes);
	}
}

// ── the REAL path ─────────────────────────────────────────────────────────────────────────────
//
// THIS is the class that would be red on the shipped code (commit 8e0a48e4 / deploy
// 0.1.0-ci.1583). The defect was not in any formula a unit test could hold: it was that the two
// sides of the ratio described DIFFERENT things — Request.ContentLength (the whole HTTP request)
// over the reserialized tool ARGUMENTS (a part of it). Every test of that code stashed the
// "expected raw" number onto HttpContext.Items BY HAND, consistent with the ContentLength it also
// set by hand, so the mismatch was structurally unreachable: the tests agreed with the bug.
//
// The only instrument that can catch it is a request that is really a request — a hand-crafted
// JSON-RPC envelope POSTed to /mcp, with the JSON-RPC scaffolding (jsonrpc/id/method/params.name,
// ~178 bytes on the wire) actually present, and the warning read back off the tool result. On the
// shipped code PureAsciiSmallCall_GetsNoWarning fails with a ~2x warning on a body containing not
// one non-ASCII byte.
public sealed class McpEscapeInflationRealPathFixture : IAsyncLifetime
{
	public const string ProjectKey = "$system"; // seeded by the migrations
	public const string ApiKeyValue = "yb_key_escape_inflation_probe";
	public const string Store = "escape-probe";

	HttpClient _http = null!;

	public WebApplicationFactory<Program> Factory { get; }
	public HttpClient Http => _http;

	public McpEscapeInflationRealPathFixture()
	{
		Environment.SetEnvironmentVariable("PETBOX_MASTER_KEY", "test-key-for-secrets");
		Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");

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
				});
			});
		});
	}

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
			await db.InsertAsync(new ApiKey
			{
				Key = ApiKeyValue,
				ProjectKey = ProjectKey,
				Scopes = "memory:read,memory:write",
				CreatedAt = DateTime.UtcNow,
			});
		}

		// memory_remember refuses an unknown store (it no longer auto-creates one), so the probe
		// store is created up front rather than leaning on a reserved name.
		var memory = scope.ServiceProvider.GetRequiredService<IMemoryService>();
		if (!await memory.StoreExistsAsync(ProjectKey, Store, default))
			await memory.CreateStoreAsync(ProjectKey, Store, "escape-inflation probe", default);
	}

	public async ValueTask DisposeAsync()
	{
		_http.Dispose();
		await Factory.DisposeAsync();
	}
}

public sealed class McpEscapeInflationRealPathTests(McpEscapeInflationRealPathFixture fx)
	: IClassFixture<McpEscapeInflationRealPathFixture>
{
	// 100 Cyrillic characters: escaped they cost 600 wire bytes where raw UTF-8 spends 200, which
	// is a big enough share of the ~200-byte ASCII envelope to clear the 1.5x threshold with room.
	static readonly string Cyrillic = string.Concat(Enumerable.Repeat("кириллица ", 10));

	// A hand-crafted JSON-RPC "tools/call" — the envelope is REAL, which is the whole point: the
	// shipped formula counted these ~178 bytes in the numerator only. The stateless transport
	// (Program.cs: WithHttpTransport(o => o.Stateless = true)) takes a bare tools/call with no
	// prior initialize, so nothing an SDK client would inject perturbs the wire bytes.
	static string Envelope(string textSpelling) =>
		"{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"memory_remember\"," +
		"\"arguments\":{\"projectKey\":\"" + McpEscapeInflationRealPathFixture.ProjectKey + "\"," +
		"\"store\":\"" + McpEscapeInflationRealPathFixture.Store + "\"," +
		"\"description\":\"escape inflation probe\"," +
		"\"text\":\"" + textSpelling + "\"}}}";

	static string EscapeAsUnicode(string s) =>
		string.Concat(s.Select(c => c > 127 ? $"\\u{(int)c:x4}" : c.ToString()));

	// THE REGRESSION TEST. A small, entirely ASCII write — no non-ASCII byte anywhere in the
	// request — must produce no warning whatsoever. The shipped code answered this exact shape
	// with "measured 2.8x its expected raw-UTF-8 size (276 vs ~98 bytes)" on prod, because the
	// envelope it was dividing by was not in the divisor. `description` is non-empty so the only
	// thing that could populate `warning` is the size detector.
	[Fact]
	public async Task PureAsciiSmallCall_GetsNoWarning()
	{
		var body = Envelope("a short ascii fact worth remembering");
		Encoding.UTF8.GetByteCount(body).Should().Be(body.Length,
			"the fixture must be pure ASCII — a body that cannot inflate is the point of the test");
		Encoding.UTF8.GetByteCount(body).Should().BeLessThan(400,
			"and small: the shipped formula tripped its threshold below ~356 bytes of arguments");

		(await WarningOfAsync(body)).Should().BeNull(
			"pure ASCII cannot be \\uXXXX-inflated — a warning here means the ratio is comparing "
			+ "the whole request against a part of it");
	}

	// The other half of the prod A/B: the SAME text spelled raw UTF-8 stays silent too.
	[Fact]
	public async Task RawUtf8Cyrillic_GetsNoWarning()
	{
		(await WarningOfAsync(Envelope(Cyrillic))).Should().BeNull(
			"raw UTF-8 is the spelling this detector is asking for; size alone is never the complaint");
	}

	// And the positive case still fires — with a multiplier matching what the wire actually shows,
	// computed here from the two bodies rather than from the server's own arithmetic.
	[Fact]
	public async Task EscapedCyrillic_WarnsWithTheMeasuredMultiplier()
	{
		var escapedBody = Envelope(EscapeAsUnicode(Cyrillic));
		var wireBytes = Encoding.UTF8.GetByteCount(escapedBody);
		// Same body, escapes undone: exactly what the scanner claims the raw spelling would cost.
		var rawBytes = Encoding.UTF8.GetByteCount(Envelope(Cyrillic));
		var expected = (double)wireBytes / rawBytes;
		expected.Should().BeGreaterThanOrEqualTo(ModuleMcp.EscapeInflationWarningThreshold,
			"the fixture must actually exercise the warning path, not assert it into existence");

		var warning = await WarningOfAsync(escapedBody);

		warning.Should().NotBeNull();
		warning!.Should().Contain(expected.ToString("0.0"))
			.And.Contain($"{wireBytes:N0} vs {rawBytes:N0} bytes",
				"the warning must quote the two comparable whole-body figures it divided");
	}

	// POSTs the raw bytes and returns the tool result's `warning` (null when absent).
	async Task<string?> WarningOfAsync(string body)
	{
		var req = new HttpRequestMessage(HttpMethod.Post, "/mcp")
		{
			Content = new StringContent(body, Encoding.UTF8, "application/json"),
		};
		req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
		req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

		using var resp = await fx.Http.SendAsync(req);
		var text = await resp.Content.ReadAsStringAsync();
		resp.StatusCode.Should().Be(HttpStatusCode.OK, $"the bare tools/call must be accepted: {text}");

		using var doc = JsonDocument.Parse(JsonPayload(text));
		doc.RootElement.TryGetProperty("error", out _).Should().BeFalse($"JSON-RPC error: {text}");
		var result = doc.RootElement.GetProperty("result");
		(result.TryGetProperty("isError", out var isError) && isError.ValueKind == JsonValueKind.True)
			.Should().BeFalse($"the write must apply — a refused write carries no size warning: {text}");

		var structured = result.GetProperty("structuredContent");
		return structured.TryGetProperty("warning", out var w) && w.ValueKind == JsonValueKind.String
			? w.GetString()
			: null;
	}

	// The streamable-HTTP transport answers either a bare JSON body or an SSE frame
	// ("event: message\ndata: {...}") depending on what it negotiates — accept both.
	static string JsonPayload(string body)
	{
		if (body.TrimStart().StartsWith('{')) return body;
		foreach (var line in body.Split('\n'))
			if (line.StartsWith("data:", StringComparison.Ordinal))
				return line["data:".Length..].Trim();
		throw new Xunit.Sdk.XunitException($"no JSON payload in the MCP response: {body}");
	}
}
