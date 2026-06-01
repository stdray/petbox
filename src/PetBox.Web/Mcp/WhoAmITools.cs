using System.ComponentModel;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Server;

namespace PetBox.Web.Mcp;

// Self-identification tool. An agent's key is project-scoped and cannot enumerate
// projects, so without this it has no way to discover which project it is bound to
// or what it is allowed to do (dogfooding finding d2). Requires no scope — any
// authenticated key may call it — and the A7b scope filter leaves it shown to every
// key (unclassified tool → fail-open).
[McpServerToolType]
public static class WhoAmITools
{
	[McpServerTool(Name = "whoami", Title = "Identify the calling ApiKey", ReadOnly = true)]
	[Description("Returns the calling ApiKey's identity: { project, scopes }. `project` is the key's project claim — every other tool needs a projectKey that must match it. `scopes` is the list of granted scopes (e.g. 'data:read', 'logs:query', 'tasks:write') that gate what you may do. Call this first when you do not already know your own project key and scopes.")]
	public static object WhoAmI(IHttpContextAccessor http)
	{
		var ctx = http.HttpContext ?? throw new InvalidOperationException("No HttpContext");
		var project = ctx.User.Claims.FirstOrDefault(c => c.Type == "project")?.Value;
		var scopes = (ctx.User.Claims.FirstOrDefault(c => c.Type == "scopes")?.Value ?? string.Empty)
			.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		return new { project, scopes };
	}
}
