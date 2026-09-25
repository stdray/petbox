using Microsoft.AspNetCore.Http;
using PetBox.Web.Mcp;

namespace PetBox.Tests.Support;

// For unit tests that call a tool method DIRECTLY (no MCP pipeline) and assert its scope refusal.
//
// Since spec mcp-scope-declared-once a tool's body no longer opens with ModuleMcp.AssertScope: the
// scope every call needs is declared on the tool ([RequiresScope] & co.) and enforced by
// McpToolScopeFilter's call gate BEFORE the body runs. A direct call therefore skips the gate the
// server would have applied. This runs the two in the server's order — the same gate code, keyed by
// the tool's wire name, then the body — so "a tasks:read key cannot call tasks_board_create" is still
// asserted against what production enforces, not against a copy of it.
static class McpScopeGate
{
	public static async Task Invoke(IHttpContextAccessor http, string tool, Func<Task> body)
	{
		McpToolScopeFilter.AssertScope(http.HttpContext?.User, tool);
		await body();
	}
}
