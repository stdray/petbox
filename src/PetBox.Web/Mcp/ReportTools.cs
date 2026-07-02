using System.ComponentModel;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Server;
using PetBox.Core.Features;
using PetBox.Tasks.Contract;
using PetBox.Web.Mcp.Contract;

namespace PetBox.Web.Mcp;

// Universal feedback channel: any authenticated agent can file a bug/issue about
// PetBox itself. Reports land in a FIXED $system board "client-issues" as Pending
// nodes for the maintainer to triage — regardless of which project the caller's key
// is scoped to. This is intentionally NOT project-scoped (it's report-to-maintainer,
// not "a task on my board"), so it does not AssertProject/AssertScope; a valid key
// (the /mcp endpoint already requires one) is enough. The write goes through the
// single tasks door (ITasksService); this adapter only composes the report body.
// Throws on a failed feature assert; McpErrorEnvelopeFilter renders the {error} body.
[McpServerToolType]
public static class ReportTools
{
	const string IssuesProject = "$system";
	const string IssuesBoard = "client-issues";

	[McpServerTool(Name = "report_issue", Title = "Report a PetBox bug or issue", UseStructuredContent = true, OutputSchemaType = typeof(ReportIssueResult))]
	[Description("""
		File a bug / issue / feedback about PetBox ITSELF — use when a tool misbehaves,
		a response is confusing/opaque, or something is broken. The report goes to the
		PetBox maintainer's triage board, not your project. Any authenticated key may
		call this; it is not scoped to your project or to a specific permission.
		""")]
	public static async Task<ReportIssueResult> IssueAsync(
		IHttpContextAccessor http, FeatureFlags features, ITasksService tasks,
		[Description("Short one-line title of the issue.")] string title,
		[Description("Full detail: what you did, what happened, expected vs actual, the tool/endpoint involved.")] string detail,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("title is required");

		var reporter = http.HttpContext?.User.Claims.FirstOrDefault(c => c.Type == "project")?.Value;
		var now = DateTime.UtcNow;
		var body = $"{detail}\n\n— via report_issue, reporting project '{reporter ?? "(unknown)"}', {now:u}";

		var key = await tasks.ReportIssueAsync(IssuesProject, IssuesBoard, title, body, ct);
		return new ReportIssueResult(true, IssuesProject, IssuesBoard, key);
	}
}
