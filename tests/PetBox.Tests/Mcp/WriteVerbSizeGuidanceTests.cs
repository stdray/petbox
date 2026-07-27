using System.ComponentModel;
using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Server;
using PetBox.Web.Mcp;

namespace PetBox.Tests.Mcp;

// card mcp-write-degrades-silently-fix, point 3: every write verb that carries a body used to
// warn about client-side \uXXXX-escaping truncation with the word "oversized" and NO number —
// unactionable, because an agent cannot calibrate a batch size against a word. Each of these
// tools must name the SAME wording everywhere (one class of problem — work
// write-verbs-size-limit-still-has-no-number / comments-upsert-size-guidance — one sentence:
// ModuleMcp.SizeGuidanceText), not five drifting ad-hoc estimates.
//
// Updated by work drop-size-number-from-tool-descriptions: the shared sentence used to name a
// literal byte number. Publishing an evidence-derived guidance number in every write tool's
// description (in the agent's context on every call) read as a hard ceiling rather than a
// margin, and on 2026-07-27 caused an agent to skip a routine call over it. The number now lives
// ONLY in the postfactum SizeWarningOrNull warning on an already-applied write, where it is
// diagnostic, not a discouragement — so the two invariants below are: the public text has no
// byte-count number, and the warning's number matches the internal threshold constant.
//
// Updated again by work escape-inflation-warning: SizeWarningOrNull no longer compares
// Request.ContentLength against an ABSOLUTE byte threshold (WriteCallSizeGuidanceBytes, retired).
// It compares the request body's WIRE size against what that SAME body would cost as raw UTF-8
// (McpWireBodyMeasurementMiddleware measures both while the body streams past). A client sending
// raw UTF-8 (inflation ~1.0) gets silence at ANY size; a client \uXXXX-escaping non-ASCII
// (inflation >= ModuleMcp.EscapeInflationWarningThreshold) gets warned even on a SMALL call. See
// card escape-inflation-warning for why the old ReqBytes/ReqChars average and the absolute
// threshold were both the wrong instrument.
//
// Corrected by fix/escape-inflation-comparable-units: the first cut divided ContentLength (the
// WHOLE request) by the reserialized tool ARGUMENTS (a PART of it), so the JSON-RPC envelope sat
// in the numerator alone and pure-ASCII calls were warned in production. The tests below could
// not have caught it — they set both sides by hand, consistently. They now feed a REAL body and
// let the production scanner derive both numbers (McpWireBody), which removes the hand-stated
// ratio; but what actually pins the units is a real POST /mcp, in McpEscapeInflationRealPathTests.
public sealed class WriteVerbSizeGuidanceTests
{
	[Theory]
	[InlineData("memory_remember")]
	[InlineData("memory_upsert")]
	[InlineData("tasks_upsert")]
	[InlineData("comments_upsert")]
	[InlineData("session_append")]
	[InlineData("session_upsert")]
	public void WriteVerb_DescriptionCarriesTheSharedSizeGuidanceSentenceVerbatim(string tool)
	{
		var desc = RegisteredDescription(tool);
		desc.Should().Contain(ModuleMcp.SizeGuidanceText,
			$"{tool}'s description should carry the shared size-guidance sentence, not a bespoke one");
	}

	// The public sentence must not carry a byte-count figure (a thousands-grouped number like
	// "8,000" or "12,000") — see the comments above ModuleMcp.SizeGuidanceText for why
	// publishing one in every write tool's description backfired. This does NOT forbid every
	// digit — the Cyrillic escape-inflation ratio ("~2.8x", "2.74-2.88x") is a different,
	// non-threshold number the sentence still legitimately states.
	[Fact]
	public void SizeGuidanceText_CarriesNoByteCountNumber()
	{
		ModuleMcp.SizeGuidanceText.Should().NotMatchRegex(@"\d{1,3}(,\d{3})+",
			"the public guidance sentence must not name a byte-count threshold, only the action and reason");
	}

	// The headline behavioral flip this card makes: a LARGE call that is raw UTF-8 (inflation
	// ~1.0) must stay SILENT — the old absolute-size trigger would have warned here on size alone.
	[Fact]
	public void SizeWarningOrNull_RawUtf8LargeBody_NoWarning()
	{
		var big = string.Concat(Enumerable.Repeat(McpWireBody.CyrillicPayload + " ", 200));
		var http = Http(McpWireBody.Envelope(big, escaped: false));
		Encoding.UTF8.GetByteCount(McpWireBody.Envelope(big, escaped: false)).Should().BeGreaterThan(25_000,
			"the point is that SIZE alone no longer earns a complaint");

		ModuleMcp.SizeWarningOrNull(http).Should().BeNull();
	}

	// A body with no non-ASCII at all cannot have inflated — at any size, but especially at the
	// SMALL size where the shipped formula's missing envelope term dominated and warned on prod.
	[Fact]
	public void SizeWarningOrNull_SmallPureAsciiBody_NoWarning()
	{
		var body = McpWireBody.Envelope("a short ascii fact", escaped: false);
		Encoding.UTF8.GetByteCount(body).Should().Be(body.Length).And.BeLessThan(400);

		ModuleMcp.SizeWarningOrNull(Http(body)).Should().BeNull();
	}

	// The other behavioral flip: a SMALL call that is \uXXXX-escaped must WARN — the old absolute
	// trigger stayed silent below its byte threshold regardless of escaping. The expected
	// multiplier is the ratio between the SAME request's two spellings, computed from the bytes.
	[Fact]
	public void SizeWarningOrNull_EscapedSmallBody_Warns()
	{
		var expectedInflation = McpWireBody.InflationOf(McpWireBody.CyrillicPayload);
		expectedInflation.Should().BeGreaterThanOrEqualTo(ModuleMcp.EscapeInflationWarningThreshold,
			"the fixture must actually exercise the warning path, not assert it into existence");
		var body = McpWireBody.Envelope(McpWireBody.CyrillicPayload, escaped: true);
		Encoding.UTF8.GetByteCount(body).Should().BeLessThan(1_000, "and it must stay SMALL");

		var warning = ModuleMcp.SizeWarningOrNull(Http(body));

		warning.Should().NotBeNull();
		warning!.Should().Contain(expectedInflation.ToString("0.0"));
	}

	// No measurement on the context — a call that never came through POST /mcp (a direct tool
	// invocation, a future in-process transport) is unknown, and unknown stays silent.
	[Fact]
	public void SizeWarningOrNull_NoMeasurement_ReturnsNull()
	{
		var ctx = new DefaultHttpContext();
		ctx.Request.ContentLength = 50_000;
		var http = new HttpContextAccessor { HttpContext = ctx };

		ModuleMcp.SizeWarningOrNull(http).Should().BeNull();
	}

	// An empty body measures nothing — never divide by it.
	[Fact]
	public void SizeWarningOrNull_EmptyBody_ReturnsNull()
	{
		ModuleMcp.SizeWarningOrNull(Http("")).Should().BeNull();
	}

	// The scanner only ever SUBTRACTS bytes a raw-UTF-8 client could have avoided, so raw can
	// never exceed wire — the detector cannot manufacture a sub-1.0 ratio out of any real body.
	[Theory]
	[InlineData("")]
	[InlineData("plain ascii")]
	[InlineData("привет мир")]
	[InlineData("mixed привет and \\u0441 and \\\\u0441 and \\ud83d\\ude00 and \\u0000")]
	public void Measurement_NeverClaimsMoreRawBytesThanWireBytes(string payload)
	{
		var scanner = new JsonEscapeInflationScanner();
		scanner.Feed(Encoding.UTF8.GetBytes(McpWireBody.Envelope(payload, escaped: false)));
		scanner.RawUtf8Bytes.Should().BeLessThanOrEqualTo(scanner.WireBytes).And.BeGreaterThan(0);
	}

	static IHttpContextAccessor Http(string wireBody)
	{
		var ctx = new DefaultHttpContext();
		McpWireBody.Publish(ctx, wireBody);
		return new HttpContextAccessor { HttpContext = ctx };
	}

	// The registered [Description] essay for a tool, by its McpServerTool name (mirrors
	// ToolDescriptionEconomyMechanismTests.RegisteredDescription).
	static string RegisteredDescription(string toolName)
	{
		foreach (var type in typeof(ModuleMcp).Assembly.GetTypes())
			foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance))
				if (m.GetCustomAttribute<McpServerToolAttribute>()?.Name == toolName)
					return m.GetCustomAttribute<DescriptionAttribute>()?.Description
						?? throw new InvalidOperationException($"{toolName} has no [Description]");
		throw new InvalidOperationException($"no MCP tool named '{toolName}'");
	}
}
