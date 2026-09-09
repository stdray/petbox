using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using LinqToDB;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using PetBox.Core.Data;
using PetBox.Core.Json;
using PetBox.Core.Models;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Workflow;
using PetBox.Web.Mcp;
using PetBox.Web.Pages.ProjectHome;

namespace PetBox.Tests.Mcp;

// ── the MCP wire is written with a relaxed encoder; HTML surfaces are NOT ─────────────────────
//
// Card mcp-json-escaping-relaxed-encoder. Two halves, and the second one is the reason the card
// carried a warning: relaxing the encoder that writes the JSON-RPC envelope also stops it escaping
// < > & ' `, so the fix is only correct while nothing that serializer feeds is embedded in HTML.
// Both halves are pinned here, in one file, so a future change cannot satisfy one and quietly break
// the other.
public sealed class McpWireEncodingUnitTests
{
	// Cached because CA1869 says so; each is exactly the encoder under test, nothing else.
	static readonly JsonSerializerOptions WireJson = new() { Encoder = McpWireEncoding.Encoder };
	static readonly JsonSerializerOptions HtmlSafeJson = new() { Encoder = PetBoxJsonEncoder.Relaxed };

	// WHY THE FIX IS NOT A JsonSerializerOptions. The SDK writes the JSON-RPC envelope into a
	// Utf8JsonWriter it constructs with DEFAULT JsonWriterOptions, so the strict encoder is welded
	// into the transport and no options object can reach it. This test states the half we CAN see
	// from here — the SDK's own options carry no encoder of their own — so that a future SDK that
	// starts honouring one shows up as a red test with a note pointing at the simpler fix.
	[Fact]
	public void Sdk_Envelope_Options_Carry_No_Encoder_Of_Their_Own()
	{
		McpJsonUtilities.DefaultOptions.Encoder.Should().BeNull(
			"nothing in PetBox configures the SDK's envelope options any more — the escaping is "
			+ "fixed at the transport (McpResponseEscapeRelaxingMiddleware) because the SDK's "
			+ "Utf8JsonWriter ignores them. If this ever stops being null, re-read McpWireEncoding.cs");
	}

	// What the transport rewrite is FOR, stated on the encoder itself rather than only end-to-end.
	[Theory]
	[InlineData("Дефолт", "Дефолт")]                 // Cyrillic: raw UTF-8, not Д…
	[InlineData("say \"hi\"", "say \\\"hi\\\"")]      // a quote costs \" (2), not " (6)
	[InlineData("a `fence`", "a `fence`")]            // backtick raw, not `
	public void McpWireEncoder_Spells_Text_Cheaply(string text, string expectedInsideTheQuotes)
	{
		JsonSerializer.Serialize(text, WireJson).Should().Be("\"" + expectedInsideTheQuotes + "\"");
	}

	// ── the XSS half ──────────────────────────────────────────────────────────────────────────
	//
	// PetBoxJsonEncoder.Relaxed is the encoder behind ConfigureHttpJsonOptions, the Razor/MVC
	// JsonOptions, the log-property serializers and the @Html.Raw'd rendered log message
	// (Pages/Logs/_EventRow.cshtml). The card's fix must NOT have loosened it: it added a second
	// encoder instead. A change that "simplifies" by pointing these at UnsafeRelaxedJsonEscaping
	// turns every one of those surfaces into an injection point.
	[Theory]
	[InlineData('<')]
	[InlineData('>')]
	[InlineData('&')]
	[InlineData('\'')]
	public void SharedRelaxedEncoder_Still_Escapes_HtmlSensitiveChars(char c)
	{
		JsonSerializer.Serialize("x" + c + "y", HtmlSafeJson)
			.Should().NotContain(c.ToString(),
				"PetBoxJsonEncoder.Relaxed is emitted into HTML; only the MCP wire encoder may drop this");
	}

	// The two @Html.Raw JSON islands in the app — TaskBoard/TaskBoardNode's workflow modal and
	// Admin/ProjectMethodology's preview islands — are BOTH fed by WorkflowGraphJson, which keeps
	// the default encoder on purpose. Status names come from user-defined methodologies, so a
	// `</script>` in one is a real payload, not a hypothetical.
	[Fact]
	public void WorkflowGraphJson_Still_Escapes_Html_For_Its_Script_Island()
	{
		var view = new BoardWorkflowView("work", [
			new WorkflowBlock(["task"], new Workflow("task",
				[new WorkflowStatus("pwn", "</script><script>alert(1)</script>", StatusKind.Open)],
				[])),
		]);

		var json = WorkflowGraphJson.Serialize(view);

		json.Should().NotContain("<", "this JSON is dropped into a <script> island via @Html.Raw");
		json.Should().Contain("\\u003C", "the default encoder is what makes that island safe");
	}
}

// ── the rewriter itself, on bytes ─────────────────────────────────────────────────────────────
//
// JsonEscapeRelaxer sits on the /mcp response path, so the only property that really matters is
// that it cannot change what the client parses. Everything here feeds it real bytes and checks the
// output against a document comparison rather than against a hand-written expected string.
public sealed class JsonEscapeRelaxerTests
{
	// Canonical form: same document, one fixed spelling. Two JSON texts that canonicalize alike are
	// the same document, whatever escapes they were written with.
	static readonly JsonSerializerOptions Canonical = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

	static string Relax(string json, int chunk = int.MaxValue)
	{
		var relaxer = new JsonEscapeRelaxer();
		var output = new ArrayBufferWriter<byte>();
		var bytes = Encoding.UTF8.GetBytes(json);
		for (var i = 0; i < bytes.Length; i += chunk)
			relaxer.Feed(bytes.AsSpan(i, Math.Min(chunk, bytes.Length - i)), output);
		relaxer.Finish(output);
		return Encoding.UTF8.GetString(output.WrittenSpan);
	}

	static string Canonicalize(string json)
	{
		using var doc = JsonDocument.Parse(json);
		return JsonSerializer.Serialize(doc.RootElement, Canonical);
	}

	// Documents covering every branch: plain text, Cyrillic escapes, an escaped quote and backslash,
	// the HTML-sensitive set, a control character, a surrogate PAIR, an UNPAIRED high surrogate, a
	// lone low surrogate, and a literal `\\u0441` that must NOT be read as an escape.
	//
	// The two UNPAIRED surrogates are legal JSON text that System.Text.Json refuses to re-serialize
	// ("Cannot read invalid UTF-16 JSON text as string"), so they cannot take part in the canonical
	// comparison below — the comparison, not the rewriter, is what cannot express them. They are
	// still bytes that can reach the wire, so they stay in every check that needs no canonical form:
	// chunk-invariance here, and byte-identity in Escapes_That_Cannot_Be_Respelled.
	static readonly string[] Representable =
	[
		"""{"a":"plain ascii"}""",
		"""{"a":"\u0414\u0435\u0444\u043E\u043B\u0442"}""",
		"""{"a":"say \u0022hi\u0022 and \\ back"}""",
		"""{"a":"\u003Cscript\u003E \u0026 \u0027 \u0060fence\u0060"}""",
		"""{"a":"tab\u0009newline\u000A del\u007F"}""",
		"""{"a":"\uD83D\uDE00 grin"}""",
		"""{"a":"literal \\u0441 is not an escape"}""",
		"""{"nested":{"b":[1,2,"\u041F\u0440\u0438\u0432\u0435\u0442"],"c":null,"d":true}}""",
	];

	static readonly string[] Unrepresentable =
	[
		"""{"a":"\uD800 lone high"}""",
		"""{"a":"\uDC00 lone low"}""",
	];

	static readonly string[] All = [.. Representable, .. Unrepresentable];

	public static TheoryData<string> RepresentableDocuments => new(Representable);
	public static TheoryData<string> Documents => new(All);

	// THE property. Everything else in this class is a detail of HOW; this is the contract.
	[Theory]
	[MemberData(nameof(RepresentableDocuments))]
	public void Rewriting_Never_Changes_The_Document(string json)
	{
		Canonicalize(Relax(json)).Should().Be(Canonicalize(json));
	}

	// …and it holds however the transport happens to break the buffer. An escape split across two
	// writes is the one thing a streaming rewriter gets wrong, so every split is exercised.
	[Theory]
	[MemberData(nameof(Documents))]
	public void Chunking_Does_Not_Change_The_Output(string json)
	{
		var whole = Relax(json);
		for (var chunk = 1; chunk <= Encoding.UTF8.GetByteCount(json); chunk++)
			Relax(json, chunk).Should().Be(whole, $"a {chunk}-byte write must produce the same bytes");
	}

	// The savings the card is about, spelled out per escape class.
	[Theory]
	[InlineData("\"\\u0414\"", "\"Д\"")]                  // Cyrillic: 6 bytes -> 2
	[InlineData("\"\\u0022\"", "\"\\\"\"")]                // a quote: 6 -> 2, and still escaped
	[InlineData("\"\\u005C\"", "\"\\\\\"")]                // a backslash: 6 -> 2, and still escaped
	[InlineData("\"\\u0060\"", "\"`\"")]                   // backtick: 6 -> 1
	[InlineData("\"\\u003C\"", "\"<\"")]                   // HTML-sensitive: 6 -> 1
	[InlineData("\"\\uD83D\\uDE00\"", "\"😀\"")]           // surrogate pair: 12 -> 4
	public void Escapes_Are_Respelled_Cheaply(string json, string expected)
	{
		Relax(json).Should().Be(expected);
	}

	// What must NOT be touched, whatever it costs.
	[Theory]
	[InlineData("\"\\u0000\"")]   // JSON requires the C0 controls to stay escaped
	[InlineData("\"\\u001F\"")]
	[InlineData("\"\\u007F\"")]
	[InlineData("\"\\uD800\"")]   // an unpaired surrogate has no raw UTF-8 spelling
	[InlineData("\"\\uDFFF\"")]
	[InlineData("\"\\uZZZZ\"")]   // malformed: give it back exactly as it arrived
	[InlineData("\"\\u04\"")]
	public void Escapes_That_Cannot_Be_Respelled_Pass_Through_Verbatim(string json)
	{
		Relax(json).Should().Be(json);
	}
}

// ── the same thing, over a real POST /mcp ─────────────────────────────────────────────────────
//
// The unit tests above prove the encoder and the seam. Only a real request proves the ENVELOPE is
// the layer that was escaping: relaxing the tool-serialization options alone (which this repo had
// been doing for a while) changes nothing on the wire, because the SDK re-escapes when it writes
// the JsonRpcResponse. So the assertion has to be made on the response bytes.
public sealed class McpWireEncodingRealPathTests(McpWireEncodingFixture fx)
	: IClassFixture<McpWireEncodingFixture>
{
	// Cyrillic (6 wire bytes each escaped, 2 raw), a quote (6 vs 2) and a backtick (6 vs 1) — one
	// probe covering all three escape classes the card names.
	const string Probe = "Дефолт `roleScope`: \"владелец\"";

	// The two spellings the card compares: what the SDK used to write, and what it writes now.
	static readonly JsonSerializerOptions StrictJson = new() { Encoder = JavaScriptEncoder.Default };
	static readonly JsonSerializerOptions WireJson = new() { Encoder = McpWireEncoding.Encoder };

	[Fact]
	public async Task ToolResult_Carries_Raw_Utf8_Not_Uxxxx_Escapes()
	{
		var raw = await RoundTripAsync();

		raw.Should().Contain("Дефолт", "Cyrillic must reach the client as UTF-8, not as \\u0414…");
		raw.Should().NotContain("\\u04", "not one Cyrillic \\uXXXX escape may survive on the wire");
		raw.Should().NotContain("\\u0022", "a quote costs \\\" (2 bytes), never \\u0022 (6)");
		raw.Should().NotContain("\\u0060", "a backtick is not an escapable character in JSON at all");
	}

	// The card asks for the saving in bytes. Measured on THIS response: the same JSON document,
	// re-emitted with the encoder that used to write it, against the one that writes it now.
	[Fact]
	public async Task Relaxed_Spelling_Is_Measurably_Smaller_Than_The_Strict_One()
	{
		var payload = JsonPayload(await RoundTripAsync());
		using var doc = JsonDocument.Parse(payload);

		var strict = JsonSerializer.Serialize(doc.RootElement, StrictJson);
		var relaxed = JsonSerializer.Serialize(doc.RootElement, WireJson);

		Encoding.UTF8.GetByteCount(relaxed).Should().BeLessThan(Encoding.UTF8.GetByteCount(strict),
			"the point of the card is bytes, not only readability");
		// And the wire we actually received is the cheap spelling, not the expensive one.
		Encoding.UTF8.GetByteCount(payload).Should().Be(Encoding.UTF8.GetByteCount(relaxed));
	}

	// memory_remember writes the probe text, memory_get reads it back — the read is what puts the
	// text in a RESPONSE, which is the only place this defect ever lived.
	async Task<string> RoundTripAsync()
	{
		var written = await CallAsync("memory_remember",
			$$"""{"projectKey":"{{McpWireEncodingFixture.ProjectKey}}","description":"wire encoding probe","type":"Reference","text":{{JsonSerializer.Serialize(Probe)}}}""");
		var key = JsonDocument.Parse(JsonPayload(written)).RootElement
			.GetProperty("result").GetProperty("structuredContent").GetProperty("key").GetString();
		key.Should().NotBeNullOrEmpty();

		return await CallAsync("memory_get",
			$$"""{"projectKey":"{{McpWireEncodingFixture.ProjectKey}}","store":"notes","key":"{{key}}","bodyLen":-1}""");
	}

	async Task<string> CallAsync(string tool, string argumentsJson)
	{
		var body = $$"""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"{{tool}}","arguments":""" + argumentsJson + "}}";
		var req = new HttpRequestMessage(HttpMethod.Post, "/mcp")
		{
			Content = new StringContent(body, Encoding.UTF8, "application/json"),
		};
		req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
		req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

		using var resp = await fx.Http.SendAsync(req);
		// Read the BYTES, not a decoded convenience view: the whole defect is a spelling on the wire.
		var text = Encoding.UTF8.GetString(await resp.Content.ReadAsByteArrayAsync());
		resp.StatusCode.Should().Be(HttpStatusCode.OK, $"{tool} must be accepted: {text}");

		using var doc = JsonDocument.Parse(JsonPayload(text));
		doc.RootElement.TryGetProperty("error", out _).Should().BeFalse($"JSON-RPC error from {tool}: {text}");
		var result = doc.RootElement.GetProperty("result");
		(result.TryGetProperty("isError", out var isError) && isError.ValueKind == JsonValueKind.True)
			.Should().BeFalse($"{tool} refused the call: {text}");
		return text;
	}

	// The streamable-HTTP transport answers either a bare JSON body or an SSE frame — accept both.
	static string JsonPayload(string body)
	{
		if (body.TrimStart().StartsWith('{')) return body;
		foreach (var line in body.Split('\n'))
			if (line.StartsWith("data:", StringComparison.Ordinal))
				return line["data:".Length..].Trim();
		throw new Xunit.Sdk.XunitException($"no JSON payload in the MCP response: {body}");
	}
}

public sealed class McpWireEncodingFixture : IAsyncLifetime
{
	public const string ProjectKey = "$system"; // seeded by the migrations
	const string ApiKeyValue = "yb_key_wire_encoding_probe";

	HttpClient _http = null!;

	WebApplicationFactory<Program> Factory { get; }
	public HttpClient Http => _http;

	public McpWireEncodingFixture()
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
		using var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open();
		await db.ApiKeys.Where(k => k.Key == ApiKeyValue).DeleteAsync();
		await db.InsertAsync(new ApiKey
		{
			Key = ApiKeyValue,
			ProjectKey = ProjectKey,
			Scopes = "memory:read,memory:write",
			CreatedAt = DateTime.UtcNow,
		});
	}

	public async ValueTask DisposeAsync()
	{
		_http.Dispose();
		await Factory.DisposeAsync();
	}
}
