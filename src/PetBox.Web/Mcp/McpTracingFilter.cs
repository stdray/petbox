using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using PetBox.Core.Observability;

namespace PetBox.Web.Mcp;

// Self-tracing: one span per MCP tool invocation, named by the tool, so a `POST /mcp/`
// request trace is attributable to the tool that ran (spec: trace-write-path-spans).
// The span nests under the AspNetCore request span via Activity.Current.
static class McpTracingFilter
{
	public static void Register(IMcpRequestFilterBuilder filters) =>
		filters.AddCallToolFilter(next => async (request, ct) =>
		{
			using var span = StartToolSpan(request.Params?.Name);
			try
			{
				var result = await next(request, ct);
				if (result.IsError == true)
					span?.SetStatus(ActivityStatusCode.Error);
				return result;
			}
			catch (Exception ex)
			{
				span?.SetStatus(ActivityStatusCode.Error, ex.Message);
				throw;
			}
		});

	internal static Activity? StartToolSpan(string? toolName)
	{
		var span = PetBoxActivitySources.Mcp.StartActivity($"mcp.tool {toolName}");
		span?.SetTag("petbox.tool", toolName);
		return span;
	}
}
