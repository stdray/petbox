using System.Diagnostics;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace PetBox.Web.Mcp;

// Central error boundary for every MCP tool call. A tool body just THROWS on an
// auth/feature/project reject (the Assert* helpers) or any deeper failure — this filter
// catches it and returns a structured { error: { type, message, detail } } result instead
// of the framework's opaque "An error occurred invoking 'X'". The learning envelope stays,
// but the result IS flagged IsError=true: a tool that declares an outputSchema MUST, per the
// MCP spec, return structuredContent matching that schema on SUCCESS — an error is not a
// success, so it goes on the isError channel and carries NO structuredContent (the spec does
// not require schema conformance when isError=true). The {error} envelope rides the text
// content block, so agents still parse `.error`. This replaces the old per-tool GuardAsync
// wrapper — one place, every tool, concrete Task<T> return types kept.
static class McpErrorEnvelopeFilter
{
	// Match the server's tool serializer (relaxed encoder so Cyrillic in a message stays
	// readable rather than \uXXXX-escaped).
	static readonly JsonSerializerOptions Json = new(ModelContextProtocol.McpJsonUtilities.DefaultOptions)
	{
		Encoder = PetBox.Core.Json.PetBoxJsonEncoder.Relaxed,
	};

	public static void Register(IMcpRequestFilterBuilder filters) =>
		filters.AddCallToolFilter(next => async (request, ct) =>
		{
			try
			{
				return await next(request, ct);
			}
			catch (Exception ex)
			{
				// Mark the surrounding tool span (McpTracingFilter) as failed regardless of
				// filter ordering — we convert to a non-IsError result below, so the tracing
				// filter's own IsError check would otherwise miss it.
				Activity.Current?.SetStatus(ActivityStatusCode.Error, ex.Message);

				var envelope = new { error = new { type = ex.GetType().Name, message = ex.Message, detail = ex.ToString() } };
				return new CallToolResult
				{
					// No StructuredContent on an error: with a declared outputSchema, a success
					// result must conform — an error must not pretend to. The learning {error}
					// envelope rides the text content instead.
					Content = [new TextContentBlock { Text = JsonSerializer.Serialize(envelope, Json) }],
					IsError = true,
				};
			}
		});
}
