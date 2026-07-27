using System.Text;
using Microsoft.AspNetCore.Http;
using PetBox.Web.Mcp;

namespace PetBox.Tests.Mcp;

// Test-side stand-in for McpWireBodyMeasurementMiddleware, for the many tests that call a tool
// body DIRECTLY and never go through /mcp.
//
// The rule it exists to enforce: a test may hand this helper BYTES, never a ratio. The escape
// detector shipped a prod defect precisely because its tests stated the two sides of the ratio by
// hand — a hand-set ContentLength and a hand-set "expected raw" — and so agreed with a formula
// that was dividing the whole request by a part of it. Here both numbers come out of one real
// body, the way production derives them, so a test that gets the units wrong cannot be written.
//
// This is still only a plumbing check: it proves a verb SURFACES the warning and that the wording
// carries the measured multiplier. That the two measured quantities are commensurable at all is
// proven where it can only be proven — over a real POST /mcp, in
// McpEscapeInflationRealPathTests.
static class McpWireBody
{
	// A JSON-RPC tools/call envelope carrying `payload` as a `text` argument, spelled on the wire
	// either as raw UTF-8 or \uXXXX-escaped. The JSON-RPC scaffolding is present and identical in
	// both spellings — it is exactly the ~180 bytes the shipped formula counted on one side of the
	// division only.
	public static string Envelope(string payload, bool escaped) =>
		"{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"memory_remember\"," +
		"\"arguments\":{\"projectKey\":\"proj\",\"description\":\"d\",\"text\":\"" +
		(escaped ? EscapeAsUnicode(payload) : payload) + "\"}}}";

	// The inflation the server must report for Envelope(payload, escaped: true): the same request
	// in its two spellings, byte for byte.
	public static double InflationOf(string payload) =>
		(double)Encoding.UTF8.GetByteCount(Envelope(payload, escaped: true))
			/ Encoding.UTF8.GetByteCount(Envelope(payload, escaped: false));

	// Publishes what the middleware publishes for a real POST /mcp — by MEASURING these bytes.
	public static void Publish(HttpContext ctx, string wireBody)
	{
		var scanner = new JsonEscapeInflationScanner();
		scanner.Feed(Encoding.UTF8.GetBytes(wireBody));
		ctx.Items[ModuleMcp.WireBodyMeasurementItemKey] = scanner;
		// Content-Length is no longer what the detector divides, but a real request still carries
		// it and the self-log still reads it — keep the context faithful.
		ctx.Request.ContentLength = scanner.WireBytes;
	}

	static string EscapeAsUnicode(string s) =>
		string.Concat(s.Select(c => c > 127 ? $"\\u{(int)c:x4}" : c.ToString()));

	// 60 Cyrillic characters against ~120 bytes of ASCII scaffolding: escaped the body measures
	// ~2.0x its raw-UTF-8 spelling — clear of the 1.5x threshold, and a number the tests below
	// compute rather than assume.
	public const string CyrillicPayload =
		"кириллический текст достаточной длины для измеримого раздувания байтов ай";
}
