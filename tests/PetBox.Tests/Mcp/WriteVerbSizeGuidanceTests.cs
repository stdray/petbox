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
// It compares ContentLength against THIS request's own expected raw-UTF-8 byte count
// (ModuleMcp.ExpectedRawBytesItemKey, stashed on HttpContext.Items by McpTracingFilter's request
// reserialization) — inflation = ContentLength / expectedRaw. A client sending raw UTF-8
// (inflation ~1.0) now gets silence at ANY size; a client \uXXXX-escaping non-ASCII (inflation >=
// ModuleMcp.EscapeInflationWarningThreshold) gets warned even on a SMALL call. See card
// escape-inflation-warning for why the old ReqBytes/ReqChars average and the absolute threshold
// were both the wrong instrument.
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
	// "8,000" or "12,000") — see the comment above ModuleMcp.WriteCallSizeGuidanceBytes for why
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
		const int expectedRaw = 50_000;
		var http = Http(contentLength: expectedRaw, expectedRawBytes: expectedRaw);

		ModuleMcp.SizeWarningOrNull(http).Should().BeNull();
	}

	// The other behavioral flip: a SMALL call that is \uXXXX-escaped must WARN — the old absolute
	// trigger stayed silent below its byte threshold regardless of escaping. Built honestly per
	// the card: a Cyrillic string's raw UTF-8 byte count vs. the byte length of its \uXXXX-escaped
	// (pure-ASCII) form, not a hand-picked ratio.
	[Fact]
	public void SizeWarningOrNull_EscapedSmallBody_Warns()
	{
		var raw = string.Concat(Enumerable.Repeat("привет мир ", 4)); // small: 44 chars
		var expectedRaw = Encoding.UTF8.GetByteCount(raw);
		var escaped = EscapeToUXXXX(raw);
		var contentLength = escaped.Length; // pure ASCII: 1 byte per char
		var expectedInflation = (double)contentLength / expectedRaw;
		expectedInflation.Should().BeGreaterThanOrEqualTo(ModuleMcp.EscapeInflationWarningThreshold,
			"the fixture must actually exercise the warning path, not assert it into existence");

		var http = Http(contentLength, expectedRaw);

		var warning = ModuleMcp.SizeWarningOrNull(http);

		warning.Should().NotBeNull();
		warning!.Should().Contain(expectedInflation.ToString("0.0"));
	}

	// No Content-Length (chunked transfer) → unknown, stay silent, as before the card.
	[Fact]
	public void SizeWarningOrNull_NoContentLength_ReturnsNull()
	{
		var ctx = new DefaultHttpContext();
		ctx.Items[ModuleMcp.ExpectedRawBytesItemKey] = 100;
		var http = new HttpContextAccessor { HttpContext = ctx };

		ModuleMcp.SizeWarningOrNull(http).Should().BeNull();
	}

	// No expectedRaw stashed (e.g. the tracing filter never ran) → unknown, never guess.
	[Fact]
	public void SizeWarningOrNull_ExpectedRawUnknown_ReturnsNull()
	{
		var ctx = new DefaultHttpContext();
		ctx.Request.ContentLength = 50_000;
		var http = new HttpContextAccessor { HttpContext = ctx };

		ModuleMcp.SizeWarningOrNull(http).Should().BeNull();
	}

	// ContentLength BELOW the expectation is transport compression, not an escaping saving — must
	// not be read as "cheaper than raw" and must never derive a sub-1.0 inflation warning.
	[Fact]
	public void SizeWarningOrNull_ContentLengthBelowExpected_ReturnsNull()
	{
		var http = Http(contentLength: 500, expectedRawBytes: 1_000);

		ModuleMcp.SizeWarningOrNull(http).Should().BeNull();
	}

	static string EscapeToUXXXX(string s)
	{
		var sb = new StringBuilder();
		foreach (var ch in s)
			sb.Append(ch <= 0x7F ? ch.ToString() : $"\\u{(int)ch:x4}");
		return sb.ToString();
	}

	static IHttpContextAccessor Http(long contentLength, int expectedRawBytes)
	{
		var ctx = new DefaultHttpContext();
		ctx.Request.ContentLength = contentLength;
		ctx.Items[ModuleMcp.ExpectedRawBytesItemKey] = expectedRawBytes;
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
