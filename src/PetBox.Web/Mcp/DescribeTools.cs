using System.ComponentModel;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using PetBox.Core.Auth;
using PetBox.Web.Mcp.Contract;

namespace PetBox.Web.Mcp;

// spec tool-description-economy — the addressed FULL read. tools/list serves a COMPACT HEAD for
// heavy tools (McpToolDescriptions/McpToolScopeFilter); this tool returns the complete description
// for one tool by name, read from the server's canonical ToolCollection (which keeps the full text
// because the list filter clones rather than mutates). Reflection-free: the schemas come along for
// free from the same ProtocolTool.
// TENANT DECLARATION (spec authz-scope-declaration): `identity` — the class covers "метаданные
// поверхности" as well as facts about the caller. tool_describe reads the server's own
// ToolCollection: a tool's description and its JSON schemas. No tenant's data is touched, and the
// same text is already served to every authenticated key by tools/list.
[McpServerToolType]
[TenantExempt(TenantExemption.Identity, "describes the SURFACE itself (tool text + schemas); touches no tenant's data")]
public static class DescribeTools
{
	[McpServerTool(Name = "tool_describe", Title = "Describe an MCP tool (full text)", ReadOnly = true,
		UseStructuredContent = true, OutputSchemaType = typeof(ToolDescribeResult))]
	[Description("Return the FULL description of a tool by name. tools/list serves a COMPACT head for heavy tools (purpose + critical gotchas); call this to fetch the complete prose (all sections, sentinel merged out) plus the tool's input/output JSON schema. Pass `name` exactly as it appears in tools/list (e.g. tasks_upsert).")]
	public static ToolDescribeResult Describe(
		IOptions<McpServerOptions> options,
		[Description("The tool name exactly as it appears in tools/list (e.g. tasks_upsert).")] string name)
	{
		var tools = options.Value.ToolCollection
			?? throw new InvalidOperationException("tool collection is unavailable");
		if (!tools.TryGetPrimitive(name, out var tool) || tool is null)
			throw new ArgumentException($"unknown tool '{name}' — pass a name exactly as it appears in tools/list");
		var pt = tool.ProtocolTool;
		return new ToolDescribeResult(
			pt.Name,
			pt.Title,
			McpToolDescriptions.Full(pt.Description),
			// raw JSON schema TEXT — NOT a JsonElement (which exports as `true` and breaks strict
			// clients' outputSchema validation); the caller JSON-parses these strings.
			pt.InputSchema.GetRawText(),
			pt.OutputSchema?.GetRawText());
	}
}
