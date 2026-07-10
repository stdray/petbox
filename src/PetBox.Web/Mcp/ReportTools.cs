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
		File a bug / issue / feedback about PetBox ITSELF — a tool misbehaved, a response is
		confusing/opaque, something is broken. The report goes to the PetBox maintainer's triage
		board, not your project. Friction with your OWN project's code or workflow is not an
		issue here — remember it with memory_remember instead.

		Report SYSTEMIC friction, not one-off noise. Worth reporting: the same call fails twice
		for the same root cause; you apply the same manual workaround more than once; a tool's
		output forces the same retry sequence every time; a description sent you down a path you
		had to back out of. Not worth reporting: one call you got right on the retry, an ordinary
		compile/lint error, anything the tool's own error message already explains.

		Say in the title what KIND of friction it is, so triage can group it — typically a tool
		error, misleading docs/descriptions, a confusing response shape, or a missing capability.

		Batch it. Report near the END of your turn, once the task is done and the whole pattern is
		visible, rather than interrupting the work at the first stumble. One report about a
		repeated problem is worth more than three about its instances.
		[[full]]
		Any authenticated key may call this; it is not scoped to your project or to a specific
		permission.
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
