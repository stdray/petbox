using System.ComponentModel;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Server;
using PetBox.Core.Data.Temporal;
using PetBox.Core.Features;
using PetBox.Tasks.Data;

namespace PetBox.Web.Mcp;

// Universal feedback channel: any authenticated agent can file a bug/issue about
// PetBox itself. Reports land in a FIXED $system board "client-issues" as Pending
// nodes for the maintainer to triage — regardless of which project the caller's key
// is scoped to. This is intentionally NOT project-scoped (it's report-to-maintainer,
// not "a task on my board"), so it does not AssertProject/AssertScope; a valid key
// (the /mcp endpoint already requires one) is enough.
[McpServerToolType]
public static partial class ReportTools
{
	const string IssuesProject = "$system";
	const string IssuesBoard = "client-issues";

	[GeneratedRegex("[^a-z0-9]+")]
	private static partial Regex NonSlug();

	[McpServerTool(Name = "report.issue", Title = "Report a PetBox bug or issue")]
	[Description("""
		File a bug / issue / feedback about PetBox ITSELF — use when a tool misbehaves,
		a response is confusing/opaque, or something is broken. The report goes to the
		PetBox maintainer's triage board, not your project. Any authenticated key may
		call this; it is not scoped to your project or to a specific permission.
		""")]
	public static Task<object> IssueAsync(
		IHttpContextAccessor http, FeatureFlags features, ITaskBoardStore boards,
		[Description("Short one-line title of the issue.")] string title,
		[Description("Full detail: what you did, what happened, expected vs actual, the tool/endpoint involved.")] string detail,
		CancellationToken ct = default) => ModuleMcp.GuardAsync(async () =>
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("title is required");

		var reporter = http.HttpContext?.User.Claims.FirstOrDefault(c => c.Type == "project")?.Value;
		var now = DateTime.UtcNow;
		var key = new TaskNodeId("incoming", Slug(title), null).ToKey();
		var body = $"{detail}\n\n— via report.issue, reporting project '{reporter ?? "(unknown)"}', {now:u}";

		await boards.EnsureAsync(IssuesProject, IssuesBoard, ct); // auto-create the triage board on first report
		var ctx = boards.GetContext(IssuesProject, IssuesBoard);
		var r = await TemporalStore.UpsertAsync(ctx, new[]
		{
			new PlanNode { Key = key, Version = 0, Status = "reported", Type = "issue", Name = title.Trim(), Body = body, Priority = 50 },
		}, ct: ct);
		if (r.Applied) await boards.TouchAsync(IssuesProject, IssuesBoard, ct);
		return (object)new { reported = true, project = IssuesProject, board = IssuesBoard, key };
	});

	// phase/wave key: "incoming/<title-slug>-<short-guid>" (slug capped, guid keeps it unique).
	static string Slug(string title)
	{
		var s = NonSlug().Replace(title.ToLowerInvariant(), "-").Trim('-');
		if (s.Length == 0 || !char.IsLetter(s[0])) s = "issue-" + s;
		if (s.Length > 32) s = s[..32].Trim('-');
		return $"{s}-{Guid.NewGuid():N}"[..(s.Length + 1 + 6)];
	}
}
