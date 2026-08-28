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

namespace PetBox.Tests.Mcp;

// ── ESCAPE PARITY BETWEEN THE MEMORY WRITE VERBS, MEASURED ON THE WIRE ────────────────────────
//
// The claim under test (work `memory-remember-text-escape-corruption`): `memory_remember` stores
// LITERAL `\uXXXX` where `memory_upsert`, handed "the same content", stores decoded Cyrillic — so
// the flat `string text` parameter and the typed `MemoryEntryInputDto[] entries` field would be
// deserialized differently by the MCP SDK.
//
// That claim is not decidable by reading MemoryTools.cs, because BOTH verbs pass their string
// through untouched into the one `IMemoryService.UpsertAsync` (MemoryTools.cs:552 and :320) and
// neither contains a decoder. It is only decidable on the BYTES: `"Н"` and `"\\u041d"` are
// two different JSON strings, and a caller that spells one where it meant the other gets exactly
// the reported symptom out of a perfectly correct server.
//
// So these tests do not hand the server a C# string and hope for a spelling. They POST a
// hand-built JSON-RPC `tools/call` to the real /mcp endpoint with the argument spelled BYTE FOR
// BYTE, twice — once per verb — and read the stored body back through the service layer. The two
// verbs receive identical argument bytes; anything that differs afterwards is the server's doing.
//
// Same instrument as McpEscapeInflationRealPathTests, for the same reason: the stateless transport
// (Program.cs: WithHttpTransport(o => o.Stateless = true)) accepts a bare tools/call with no prior
// initialize, so no SDK client sits between the test and the bytes and re-spells them.
public sealed class McpWriteVerbEscapeParityTests(McpEscapeParityFixture fx)
	: IClassFixture<McpEscapeParityFixture>
{
	// The reported payload's opening word, as it appears in the card.
	const string Decoded = "Набито";

	// ONE backslash, spelled so nothing between here and the socket can quietly collapse it.
	const string B = "\\";

	// The six escape sequences that spell `Набито`, with `lead` in front of each `u`.
	static string Spell(string lead) => string.Concat(
		new[] { "041d", "0430", "0431", "0438", "0442", "043e" }.Select(c => lead + "u" + c));

	// Spelling A, spliced into the request body verbatim: ONE backslash before each `u`, so the
	// body carries a JSON \u escape and any conformant parser yields `Набито`.
	static readonly string EscapeSpelling = Spell(B);

	// Spelling B: TWO backslashes, i.e. an escaped BACKSLASH followed by `u041d`. A conformant
	// parser yields the six LITERAL characters. This is the byte sequence the card's corrupted
	// entry actually contained.
	static readonly string BackslashSpelling = Spell(B + B);

	// What spelling B must decode to -- which is, character for character, spelling A's own
	// source text. That identity is the diagnosis in one line: the two spellings are DIFFERENT
	// requests, and the reported "corruption" is one of them arriving where the other was meant.
	static readonly string LiteralEscapes = EscapeSpelling;

	// ── THE OBSERVATION ───────────────────────────────────────────────────────────────────────
	//
	// Identical argument bytes into both verbs. If the SDK really bound a flat `string` parameter
	// differently from a DTO field, this is where it shows: remember would keep the escapes and
	// upsert would decode them.
	[Fact]
	public async Task EscapeSpelling_DecodesIdenticallyForRememberAndUpsert()
	{
		var viaRemember = await fx.RememberAsync(EscapeSpelling);
		var viaUpsert = await fx.UpsertAsync(EscapeSpelling);

		viaRemember.Should().Be(Decoded,
			"a JSON \\uXXXX escape is decoded by the parser before any PetBox code sees it");
		viaUpsert.Should().Be(Decoded);
		viaRemember.Should().Be(viaUpsert,
			"both verbs hand the same string to the same IMemoryService.UpsertAsync — the flat "
			+ "`text` parameter and the `entries[].body` DTO field cannot diverge on identical bytes");
	}

	// ── THE COUNTER-HYPOTHESIS, REPRODUCED ────────────────────────────────────────────────────
	//
	// The reported corruption is what an ESCAPED BACKSLASH produces — and it produces it on BOTH
	// verbs, equally. This is the half that says the defect was in the bytes the caller sent, not
	// in the verb that received them.
	[Fact]
	public async Task BackslashSpelling_StaysLiteralForRememberAndUpsert()
	{
		var viaRemember = await fx.RememberAsync(BackslashSpelling);
		var viaUpsert = await fx.UpsertAsync(BackslashSpelling);

		viaRemember.Should().Be(LiteralEscapes,
			"`\\\\u041d` on the wire IS a backslash followed by u041d — storing it verbatim is correct");
		viaUpsert.Should().Be(LiteralEscapes,
			"and upsert must not decode it either: bodies legitimately carry regexes, code and "
			+ "JSON samples where a literal \\u must survive a round trip");
		viaRemember.Should().Be(viaUpsert);
	}

	// ── THE ANTI-FIX PIN ──────────────────────────────────────────────────────────────────────
	//
	// The tempting "fix" for the card as filed is a blanket unescape of `\uXXXX` on input. It
	// would silently rewrite legitimate content. This pins that it must never be added.
	[Fact]
	public async Task LiteralBackslashU_InCodeLikeContent_SurvivesBothVerbs()
	{
		// FOUR backslashes then `u[0-9a-f]{4}`, then TWO backslashes then `u041d`.
		var wire = "regex: " + B + B + B + B + "u[0-9a-f]{4} matches " + B + B + "u041d in a JSON sample";
		// Decoded: two backslashes and one backslash -- both still literal, neither turned into text.
		var stored = "regex: " + B + B + "u[0-9a-f]{4} matches " + B + "u041d in a JSON sample";

		(await fx.RememberAsync(wire)).Should().Be(stored,
			"a body describing escape sequences must keep them literal — a blanket input unescape "
			+ "would trade one silent corruption for a wider one");
		(await fx.UpsertAsync(wire)).Should().Be(stored);
	}

	// ── RAW UTF-8, THE SPELLING THE TOOL DESCRIPTIONS ASK FOR ─────────────────────────────────
	[Fact]
	public async Task RawUtf8Cyrillic_RoundTripsThroughBothVerbs()
	{
		(await fx.RememberAsync(Decoded)).Should().Be(Decoded);
		(await fx.UpsertAsync(Decoded)).Should().Be(Decoded);
	}
}

// Posts hand-built JSON-RPC bodies to the real /mcp and reads the stored entry back through the
// service layer — the storage truth, with no read verb in between to re-spell anything.
public sealed class McpEscapeParityFixture : IAsyncLifetime
{
	public const string ProjectKey = "$system"; // seeded by the migrations
	const string ApiKeyValue = "yb_key_escape_parity_probe";
	public const string Store = "escape-parity-probe";

	HttpClient _http = null!;
	int _seq;

	WebApplicationFactory<Program> Factory { get; }

	public McpEscapeParityFixture()
	{
		Environment.SetEnvironmentVariable("PETBOX_MASTER_KEY", "test-key-for-secrets");
		Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");

		Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
		{
			b.UseEnvironment("Testing");
			b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
			{
				["ConnectionStrings:PetBox"] = TestSchema.NewTempConnectionString(),
				["Host:BackgroundServices"] = "false",
				["Features:Memory"] = "true",
			}));
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

		var memory = scope.ServiceProvider.GetRequiredService<IMemoryService>();
		if (!await memory.StoreExistsAsync(ProjectKey, Store, default))
			await memory.CreateStoreAsync(ProjectKey, Store, "escape parity probe", default);
	}

	public async ValueTask DisposeAsync()
	{
		_http.Dispose();
		await Factory.DisposeAsync();
	}

	// `spelling` is spliced into the JSON body VERBATIM — the caller states the wire bytes, not a
	// C# value that some serializer will re-spell on the way out.
	public async Task<string> RememberAsync(string spelling)
	{
		var body =
			"{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"memory_remember\"," +
			"\"arguments\":{\"projectKey\":\"" + ProjectKey + "\",\"store\":\"" + Store + "\"," +
			"\"type\":\"Reference\",\"description\":\"escape parity probe\"," +
			"\"text\":\"" + spelling + "\"}}}";
		var structured = await CallAsync(body);
		return await ReadBodyAsync(structured.GetProperty("key").GetString()!);
	}

	public async Task<string> UpsertAsync(string spelling)
	{
		var key = $"parity-{Interlocked.Increment(ref _seq)}";
		var body =
			"{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"memory_upsert\"," +
			"\"arguments\":{\"projectKey\":\"" + ProjectKey + "\",\"store\":\"" + Store + "\"," +
			"\"entries\":[{\"key\":\"" + key + "\",\"version\":0,\"type\":\"Reference\"," +
			"\"description\":\"escape parity probe\"," +
			"\"body\":\"" + spelling + "\"}]}}}";
		await CallAsync(body);
		return await ReadBodyAsync(key);
	}

	async Task<string> ReadBodyAsync(string key)
	{
		using var scope = Factory.Services.CreateScope();
		var memory = scope.ServiceProvider.GetRequiredService<IMemoryService>();
		var entry = await memory.GetAsync(ProjectKey, Store, key, default);
		entry.Should().NotBeNull($"the write must have landed under '{key}'");
		return entry!.Body;
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
		return result.GetProperty("structuredContent").Clone();
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
