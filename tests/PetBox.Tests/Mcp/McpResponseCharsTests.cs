using System.Text.Json;
using ModelContextProtocol.Protocol;
using PetBox.Web.Mcp;

namespace PetBox.Tests.Mcp;

// What RespChars MEANS (the always-on economy metric of McpTracingFilter, spec: economy-measurable).
// Every tool declares UseStructuredContent, so the SDK puts the SAME payload on the wire twice: as
// structuredContent AND as an escaped text mirror. No client reads both — Claude Code reads the
// structured copy, droid/opencode read the text copy — so the number an economy query must see is the
// payload ONE client puts in context, counted ONCE. (The old computation summed both copies plus the
// text one's escaping, overstating a real call by ~3x.)
public sealed class McpResponseCharsTests
{
	// The wire shape the SDK actually produces for a successful structured tool result: the POCO as
	// structuredContent, and its serialized JSON re-emitted as a text content block.
	static CallToolResult Duplicated(object payload)
	{
		var json = JsonSerializer.Serialize(payload);
		return new CallToolResult
		{
			StructuredContent = JsonSerializer.Deserialize<JsonElement>(json),
			Content = [new TextContentBlock { Text = json }],
		};
	}

	[Fact]
	public void Success_With_Both_Copies_Is_Counted_Once_As_The_Structured_Payload()
	{
		var payload = new { projectKey = "petbox", scopes = "logs:query", ok = true };
		var result = Duplicated(payload);

		var structuredOnly = JsonSerializer.Serialize(result.StructuredContent!.Value).Length;
		var chars = McpTracingFilter.ResponseChars(result);

		// The metric is the structured payload alone…
		chars.Should().Be(structuredOnly, "one client reads ONE copy — the structured payload");
		// …not the doubled wire form (structured + its escaped text mirror), which is what the old
		// `new { result.Content, result.StructuredContent }` measured.
		var doubled = JsonSerializer.Serialize(new { result.Content, result.StructuredContent }).Length;
		chars.Should().BeLessThan(doubled, "the wire carries ~2x; the metric must not");
	}

	[Fact]
	public void IsError_TextOnly_Envelope_Is_Still_Measured_Via_Content()
	{
		// The McpErrorEnvelopeFilter shape: {error:{…}} on the text channel, NO structuredContent
		// (a declared outputSchema must not be faked on an error). It must still count.
		var result = new CallToolResult
		{
			Content = [new TextContentBlock
			{
				Text = """{"error":{"type":"UnauthorizedAccessException","message":"scope logs:query required"}}""",
			}],
			IsError = true,
		};

		McpTracingFilter.ResponseChars(result).Should()
			.BeGreaterThan(0, "a text-only error envelope is still a payload the agent reads");
	}

	[Fact]
	public void Empty_Result_Measures_Zero()
	{
		McpTracingFilter.ResponseChars(new CallToolResult()).Should().Be(0);
	}
}
