using System.Diagnostics;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace PetBox.Web.Mcp;

// Central error boundary for every MCP tool call. A tool body just THROWS on an
// auth/feature/project reject (the Assert* helpers) or any deeper failure — this filter
// catches it and returns a structured { error: { type, message, detail } } result instead
// of the framework's opaque "An error occurred invoking 'X'". The result is NOT flagged
// IsError: agents parse `.error` from the body (the established convention the MCP tests
// rely on), and a structured body is more useful than the boolean. This replaces the old
// per-tool GuardAsync wrapper — one place, every tool, concrete Task<T> return types kept.
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
					StructuredContent = JsonSerializer.SerializeToElement(envelope, Json),
					Content = [new TextContentBlock { Text = JsonSerializer.Serialize(envelope, Json) }],
					IsError = false,
				};
			}
		});
}
