using System.ComponentModel;
using ModelContextProtocol.Server;
using PetBox.Core.Auth;
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
// TENANT DECLARATION (spec authz-scope-declaration): `feedback` — "доклад сопровождающему в
// фиксированный арендатор". The report lands in $system/client-issues NO MATTER which project the
// caller's key names, so the target tenant is a constant of the surface rather than an input: there
// is nothing here for a caller to aim, and the tool takes no projectKey at all. The class is what
// makes the write into a foreign tenant ($system) a DECLARED property instead of an accident — the
// paragraph above ("intentionally NOT project-scoped") was the comment version of it, and a comment
// is invisible to the ratchet.
//
// THE CHANNEL HAS TWO VERBS (work report-issue-has-no-reply-channel). petbox_report_issue was
// one-way: an external agent filed reports containing direct questions to the maintainers and had
// no way to read any answer — its key is project-scoped and $system/client-issues is closed to it.
// That isolation is CORRECT and is not weakened here; what was missing was a verb to read back.
// petbox_report_issue_status is that verb, and it is a PULL over the caller's OWN reports, never a
// push into the reporter's project: for a pull the credential IS the address, whereas a push would
// need an address-resolution and failure-delivery story that does not exist and could not keep a
// status current. Both verbs share the FULL `petbox_report_issue` token prefix on purpose — Claude
// Code's deferred-tool search matches on the NAME, not the description (cf. 61af775d), so a search
// that finds one finds the other. Do not shorten the name.
[McpServerToolType]
[TenantExempt(TenantExemption.Feedback, "files into (and reads back from) the maintainer's fixed $system/client-issues board, never the caller's tenant")]
public static class ReportTools
{
	const string IssuesProject = "$system";
	const string IssuesBoard = "client-issues";

	// ── the reporter's identity, ONE definition for both verbs ────────────────────────────────
	//
	// The trailing line the write verb appends to every report body. It is composed here and parsed
	// here so the two halves cannot drift: the read filter's second leg (below) is a parse of
	// EXACTLY the text this method writes.
	//
	// SPOOFING: `detail` is caller-controlled and the marker is appended AFTER it, so a caller can
	// put the marker's own text inside its detail. The genuine marker is therefore the LAST one in
	// the body, always — and only a TRAILING match may be trusted. A substring-anywhere match would
	// let any caller plant "reporting project 'victim'" in its own detail and inject a forged report
	// into victim's read-back. ReporterFromMarker takes the last occurrence and requires the
	// remainder to be a single-line timestamp, i.e. that nothing follows it.
	internal const string MarkerPrefix = "\n\n— via petbox_report_issue, reporting project '";
	internal const string MarkerMid = "', ";
	internal const string UnknownReporter = "(unknown)";

	internal static string Marker(string? reporter, DateTime utcNow) =>
		$"{MarkerPrefix}{reporter ?? UnknownReporter}{MarkerMid}{utcNow:u}";

	// The reporting project named by the body's TRAILING marker, or null when the body does not end
	// in one. Deliberately NOT "does the body contain …" — see the spoofing note above.
	internal static string? ReporterFromMarker(string? body)
	{
		if (string.IsNullOrEmpty(body)) return null;
		var at = body.LastIndexOf(MarkerPrefix, StringComparison.Ordinal);
		if (at < 0) return null;
		var rest = body[(at + MarkerPrefix.Length)..];
		var close = rest.IndexOf(MarkerMid, StringComparison.Ordinal);
		if (close < 0) return null;
		var reporter = rest[..close];
		var tail = rest[(close + MarkerMid.Length)..];
		// The genuine tail is one `{u}` timestamp and NOTHING else. A newline on either side means
		// this is text that merely looks like the marker sitting in the middle of a body, not the
		// server's own trailer.
		if (reporter.Contains('\n') || tail.Length == 0 || tail.Contains('\n')) return null;
		return reporter;
	}

	// The `reporter:` tag the write verb records on the node (structured, so the read filter does not
	// have to depend on prose surviving a maintainer's edit). TagStore lowercases every tag, so the
	// comparison is case-insensitive on both legs — a project key is case-insensitively unique.
	internal const string ReporterTagPrefix = "reporter:";

	[McpServerTool(Name = "petbox_report_issue", Title = "Report an issue about PetBox itself", UseStructuredContent = true, OutputSchemaType = typeof(ReportIssueResult))]
	[Description("""
		Report an issue about PetBox itself — a bug, confusing behavior, misleading docs, or a
		missing capability — to the people who maintain PetBox. Every call lands on the
		maintainers' fixed $system triage queue, never in your own project's intake, no matter
		which project key you call with. Friction with your OWN project's code or workflow
		belongs in memory_remember (or your own project's intake), not here.

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

		This is not one-way: read the maintainers' status and replies with
		petbox_report_issue_status (it takes the `key` this call returns).
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
		if (string.IsNullOrWhiteSpace(detail)) throw new ArgumentException("detail is required");

		// RESOLVED, not the raw "project" claim: a "*" claim authorizes every project and names
		// none, so recording it verbatim would file every wildcard key's report under one shared
		// pseudo-identity that petbox_report_issue_status would then hand to all of them. Same
		// resolution as ModuleMcp.ResolveProject uses for an omitted projectKey, and the same one
		// the read side matches with — one identity, computed once, at both ends.
		var reporter = ModuleMcp.DefaultProjectOf(http.HttpContext?.User);
		var body = $"{detail}{Marker(reporter, DateTime.UtcNow)}";

		var key = await tasks.ReportIssueAsync(IssuesProject, IssuesBoard, title, body, reporter, ct);
		return new ReportIssueResult(true, IssuesProject, IssuesBoard, key,
			$"Read the maintainers' status and replies with petbox_report_issue_status (key: \"{key}\").");
	}

	[McpServerTool(Name = "petbox_report_issue_status", Title = "Read back your own PetBox issue reports", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(ReportIssueStatusResult))]
	[Description("""
		Read back the reports YOUR project filed with petbox_report_issue: their current triage
		status, and the maintainers' comments on them. This is the answer side of that channel —
		file with petbox_report_issue, read the reply here.

		Returns, per report: `key` (what petbox_report_issue returned), `title`, `status` (the
		maintainers' triage board status — Todo/InProgress/Done/…), `created`, `updated`, the full
		`body` as filed, and `comments` (each with `author`, `body`, `created`) — the replies.
		Resolved reports are included; the status is how you tell.

		You see ONLY your own project's reports. There is no parameter for whose reports to read —
		the key you call with IS the address. Narrow with `key` to poll one report.
		""")]
	public static async Task<ReportIssueStatusResult> IssueStatusAsync(
		IHttpContextAccessor http, FeatureFlags features, ITasksService tasks, ICommentService comments,
		[Description("Narrow to ONE report by the key petbox_report_issue returned. Omitted = every report your project has filed. A key belonging to another project is simply not found.")] string? key = null,
		[Description("Max reports to return, most recently created first. Default 20.")] int? limit = null,
		CancellationToken ct = default)
	{
		// GATING — deliberately Feature.Tasks and NOTHING else, symmetric with petbox_report_issue.
		// A key that can file a report must be able to read the answer, or the channel is still
		// one-way and this whole verb is theatre. No scope gate: the write verb has none either, and
		// a `tasks:read` requirement would lock out exactly the minimal reporting keys the channel
		// exists for. This does NOT suspend the scope axis anywhere — the surface simply DECLARES no
		// scope, the same way the write verb does; every other tasks_* verb keeps its own gate. If
		// you are reading this as a missing check: the check that matters here is the IDENTITY
		// filter below, which is what keeps one reporter out of another's reports.
		ModuleMcp.AssertFeature(features, Feature.Tasks);

		// The reporter identity, resolved exactly as at write time (ModuleMcp.DefaultProjectOf).
		// A "*" key with no project_default has no identity to match on. Refuse and SAY SO — an
		// empty list would read as "you filed nothing", and matching on the raw "*" claim would
		// hand every wildcard key every other wildcard key's reports. Nothing is lost: a
		// non-sandbox "*" key can read the board directly.
		var reporter = ModuleMcp.DefaultProjectOf(http.HttpContext?.User)
			?? throw new UnauthorizedAccessException(
				"This API key names no single project ('*' with no default project), so there is no reporter " +
				"identity to read reports back for. Set a default project on the key, or read the board " +
				"directly: tasks_search projectKey:\"" + IssuesProject + "\" board:\"" + IssuesBoard + "\".");

		// Never filed into, never read out of — and never an error either: "no reports" is the
		// honest answer, and GetAsync would throw "board not found".
		if (!await tasks.BoardExistsAsync(IssuesProject, IssuesBoard, ct))
			return new ReportIssueStatusResult([]);

		// includeClosed: a Done report is precisely the answer the reporter came for.
		var board = await tasks.GetAsync(IssuesProject, IssuesBoard, includeClosed: true, ct: ct);

		var mine = board.Nodes.Where(n => IsReporter(n.Tags, n.Body, reporter));
		if (!string.IsNullOrWhiteSpace(key))
			mine = mine.Where(n => string.Equals(n.Key, key.Trim(), StringComparison.OrdinalIgnoreCase));

		var rows = mine
			.OrderByDescending(n => n.CreatedAt ?? DateTime.MinValue)
			.ThenBy(n => n.Key, StringComparer.Ordinal)
			.Take(limit is > 0 ? limit.Value : DefaultLimit)
			.ToList();

		// One pass for the whole board's comments rather than one call per report (mirrors
		// TasksService's own use of ListForBoardAsync). Skipped when nothing matched.
		var threads = rows.Count == 0
			? null
			: await comments.ListForBoardAsync(IssuesProject, IssuesBoard, ct);

		return new ReportIssueStatusResult(rows
			.Select(n => new ReportIssueStatusItem(
				n.Key, n.Title, n.Status, n.CreatedAt, n.UpdatedAt, n.Body,
				threads is null ? [] : threads[n.NodeId].OrderBy(c => c.Created).ToList()))
			.ToList());
	}

	const int DefaultLimit = 20;

	// ── THE READ FILTER: TWO PERMANENT LEGS ───────────────────────────────────────────────────
	//
	// This is the security boundary of the read-back channel — it is the only thing standing
	// between one reporter and another's reports. Both legs are permanent; neither is a shim.
	//
	// LEG 1 — the `reporter:<project>` tag. Structured, the primary leg, and the one a query could
	//   be pushed down to later.
	//
	// LEG 2 — the body's TRAILING marker. NOT a legacy accommodation for the reports that predate
	//   the tag. NodeTag is temporal and tags REPLACE: a maintainer who re-tags a report through
	//   tasks_upsert with a full tag list silently drops `reporter:<project>` and, with leg 1 alone,
	//   would orphan that report from the only caller entitled to see it — permanently, and
	//   invisibly. The body marker survives that, because a tag edit does not touch the body.
	//   Anchored to the END of the body: see ReporterFromMarker for why an anywhere-match would be
	//   a spoofing vector.
	static bool IsReporter(IReadOnlyList<string> tags, string? body, string reporter) =>
		tags.Contains(ReporterTagPrefix + reporter, StringComparer.OrdinalIgnoreCase) ||
		string.Equals(ReporterFromMarker(body), reporter, StringComparison.OrdinalIgnoreCase);
}
