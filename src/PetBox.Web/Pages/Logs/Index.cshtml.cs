using System.Globalization;
using System.Text;
using Kusto.Language;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Auth;
using PetBox.Core.Models;
using PetBox.Log.Core.Contract;
using PetBox.Log.Core.Data;
using PetBox.Log.Core.Query;
using PetBox.Web.Auth;

namespace PetBox.Web.Pages.Logs;

// viewer-member-consistency: reading (KQL query, saved-query list) needs only WorkspaceViewer — a
// Viewer who can see the board list must not 403 on the log page itself. Saving/deleting a saved
// query is a MUTATION and stays Member+ (guarded per-handler below, not by the class policy, since
// the class also carries the read handler).
[Authorize(Policy = "WorkspaceViewer")]
// {projectKey} in the route IS the target tenant; ProjectWorkspaceBindingFilter still binds it to
// {workspaceKey} as a ROUTING question (404 on a mismatched URL), which is a different question.
[TenantFrom(TenantSource.Route, "projectKey")]
public sealed class IndexModel : PageModel
{
	readonly ILogService _logs;
	readonly IProjectDirectory _projects;
	readonly ISavedQueryStore _savedQueries;

	public IndexModel(ILogService logs, IProjectDirectory projects, ISavedQueryStore savedQueries)
	{
		_logs = logs;
		_projects = projects;
		_savedQueries = savedQueries;
	}

	[BindProperty(SupportsGet = true, Name = "kql")]
	public string? RawKql { get; set; }

	[BindProperty(SupportsGet = true)]
	public string? Cursor { get; set; }

	[BindProperty(SupportsGet = true, Name = "saved")]
	public string? SavedName { get; set; }

	// Event permalink: the URL carries only the row id (?event=), never query text.
	[BindProperty(SupportsGet = true, Name = "event")]
	public long? EventRowId { get; set; }

	// authz-bypass-project-create: route-only bind — see Admin/Projects.cshtml.cs for why.
	// ProjectKeyRoute in particular is the SavedQuery write scope (OnPostSaveAsync /
	// OnPostDeleteAsync below) — a form-supplied override would let a same-workspace member
	// write/delete another project's saved queries.
	[FromRoute(Name = "workspaceKey")]
	public string? WorkspaceKey { get; set; }

	[FromRoute(Name = "projectKey")]
	public string? ProjectKeyRoute { get; set; }

	[BindProperty(SupportsGet = true, Name = "logName")]
	public string? LogNameRoute { get; set; }

	public string UserKql { get; private set; } = "";
	public string? KqlError { get; private set; }
	public bool SchemaMissing { get; private set; }
	public bool IsShapeChanged { get; private set; }
	public bool NoLogs { get; private set; }

	public List<LogEntryViewModel> Events { get; } = [];
	public List<object?[]> KqlRows { get; } = [];

	// The shape-changed row accumulation hit KqlLimits.MaxTake and was cut (memory guard).
	public bool KqlTruncated { get; private set; }

	// Column headers for the shape-changed table render. Empty until a shape-changing query runs.
	public IReadOnlyList<KqlColumn> KqlColumns { get; private set; } = [];
	public string? NextCursor { get; private set; }
	public List<string> Services { get; private set; } = [];
	public List<SavedQuery> SavedQueries { get; private set; } = [];
	public string? ActiveSavedName { get; private set; }
	public string? ProjectKey { get; private set; }
	public string? ProjectName { get; private set; }
	public List<string> AvailableLogs { get; private set; } = [];
	public string? SelectedLog { get; private set; }

	[TempData]
	public string? FlashError { get; set; }

	const int PageSize = 50;

	public async Task<IActionResult> OnGetAsync(CancellationToken ct)
	{
		var projectFilter = ProjectKeyRoute;
		if (string.IsNullOrWhiteSpace(projectFilter))
			return Page();

		var project = await _projects.GetAsync(projectFilter, ct);
		if (project is null)
			return Page();

		ProjectKey = project.Key;
		ProjectName = project.Name;

		// Named logs available in this project; pick the requested one, else the sole log,
		// else the conventional `default`, else the oldest (see DefaultLogSelector for why
		// "oldest", not "alphabetically first" — logs-traces-default-log).
		var logMetas = await _logs.ListAsync(ProjectKey, ct);
		AvailableLogs = logMetas.Select(l => l.Name).ToList();
		SelectedLog = DefaultLogSelector.Resolve(logMetas, LogNameRoute);
		if (SelectedLog is null)
		{
			NoLogs = true;
			return Page();
		}

		// Saved queries are project-scoped (shared across the project's logs).
		SavedQueries = [.. await _savedQueries.ListAsync(ProjectKey, ct)];

		if (!string.IsNullOrWhiteSpace(SavedName))
		{
			var saved = await _savedQueries.FindAsync(ProjectKey, SavedName, ct);
			if (saved is not null)
			{
				RawKql = saved.Kql;
				ActiveSavedName = saved.Name;
			}
		}

		UserKql = string.IsNullOrWhiteSpace(RawKql) ? "events" : RawKql.Trim();

		if (EventRowId is { } eventRowId)
			UserKql = FormattableString.Invariant($"events | where Id == {eventRowId}");

		KustoCode userCode;
		try
		{
			userCode = KustoCode.Parse(UserKql);
			var parseErrors = userCode.GetDiagnostics().Where(d => d.Severity == "Error").ToList();
			if (parseErrors.Count > 0)
			{
				KqlError = "KQL parse error: " + string.Join("; ", parseErrors.Select(d => d.Message));
				return Page();
			}
		}
		catch (Exception ex)
		{
			KqlError = ex.Message;
			return Page();
		}

		IsShapeChanged = KqlTransformer.HasShapeChangingOps(userCode);
		var root = KqlTransformer.GetRootTableName(userCode);
		var isSpans = string.Equals(root, KqlTransformer.SpansTable, StringComparison.Ordinal);
		var isMetrics = string.Equals(root, KqlTransformer.MetricsTable, StringComparison.Ordinal);
		// This page routes ALL roots (like LogQueryService), so an unknown root gets the full
		// supported-table list rather than the events-only message the engine entries would emit.
		if (root is not null && !isSpans && !isMetrics
			&& !string.Equals(root, KqlTransformer.EventsTable, StringComparison.Ordinal))
		{
			KqlError = KqlTransformer.UnknownTableMessage(root);
			return Page();
		}
		// A spans/metrics query always yields the streamed column shape (there is no LogEntry row form for
		// a span/metric point), so it renders through the same table branch as shape-changed events
		// queries and skips the events cursor paging (Timestamp/Id ordering doesn't apply to them).
		if (isSpans || isMetrics)
			IsShapeChanged = true;
		var effectiveKql = IsShapeChanged ? UserKql : AppendPageLimits(UserKql);

		KustoCode code;
		try
		{
			code = KustoCode.Parse(effectiveKql);
			var errors = code.GetDiagnostics().Where(d => d.Severity == "Error").ToList();
			if (errors.Count > 0)
			{
				KqlError = "KQL error: " + string.Join("; ", errors.Select(d => d.Message));
				return Page();
			}
		}
		catch (Exception ex)
		{
			KqlError = ex.Message;
			return Page();
		}

		try
		{
			if (IsShapeChanged)
			{
				var target = isSpans
					? KqlTransformer.SpansTable
					: isMetrics ? KqlTransformer.MetricsTable : KqlTransformer.EventsTable;

				var table = await _logs.QueryTableAsync(
					ProjectKey, SelectedLog, code, target, KqlLimits.MaxTake, ct);

				KqlColumns = table.Columns;
				KqlRows.AddRange(table.Rows);
				KqlTruncated = table.Truncated;
			}
			else
			{
				// No extra cap here: AppendPageLimits already welded `| take PageSize + 1` into the
				// KQL, and the extra row is how the next-page cursor is probed.
				var records = await _logs.QueryEventsAsync(ProjectKey, SelectedLog, code, ct: ct);
				foreach (var r in records)
					Events.Add(LogEntryViewModel.FromRecord(r));

				if (Events.Count > PageSize)
				{
					Events.RemoveAt(Events.Count - 1);
					var last = Events[^1];
					NextCursor = EncodeCursor(last.Timestamp, last.Id);
				}
			}

			Services = [.. await _logs.ListServiceKeysAsync(ProjectKey, SelectedLog, ct)];
		}
		catch (LogSchemaMissingException)
		{
			SchemaMissing = true;
			return Page();
		}
		catch (UnsupportedKqlException ex)
		{
			KqlError = ex.Message;
			return Page();
		}

		if (Request.Headers.ContainsKey("HX-Request") && !IsShapeChanged)
			return Partial("_RowsFragment", this);

		return Page();
	}

	public async Task<IActionResult> OnPostSaveAsync(
		[FromForm(Name = "name")] string? name,
		[FromForm(Name = "kql")] string? kql,
		CancellationToken ct)
	{
		// viewer-member-consistency: the class policy is WorkspaceViewer (a Viewer must be able to
		// READ this page) — saving a query is a MUTATION, so it needs Member+ on its own.
		if (!User.HasWorkspaceRoleAtLeast(WorkspaceKey ?? string.Empty, WorkspaceRole.Member))
			return Forbid();

		if (string.IsNullOrWhiteSpace(ProjectKeyRoute) || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(kql))
			return RedirectToProject();

		await _savedQueries.SaveAsync(ProjectKeyRoute, name, kql, ct);

		this.NotifySuccess("Saved query saved.");
		return RedirectToProject(name.Trim());
	}

	public async Task<IActionResult> OnPostDeleteAsync(
		[FromForm(Name = "savedId")] long savedId,
		CancellationToken ct)
	{
		if (!User.HasWorkspaceRoleAtLeast(WorkspaceKey ?? string.Empty, WorkspaceRole.Member))
			return Forbid();

		// Defense in depth: scope the delete to the ROUTE project too, not just the id — even
		// with ProjectKeyRoute now route-locked, an id alone spans every project in every
		// workspace, so without this filter a same-workspace member could delete another
		// project's saved query by guessing/enumerating its id. ISavedQueryStore welds that
		// scope into the delete's address rather than leaving it to a filter here.
		if (!string.IsNullOrWhiteSpace(ProjectKeyRoute))
			await _savedQueries.DeleteAsync(ProjectKeyRoute, savedId, ct);

		this.NotifySuccess("Saved query deleted.");
		return RedirectToProject();
	}

	RedirectToPageResult RedirectToProject(string? saved = null) =>
		RedirectToPage("/Logs/Index", new
		{
			workspaceKey = WorkspaceKey,
			projectKey = ProjectKeyRoute,
			logName = LogNameRoute,
			saved,
		});

	string AppendPageLimits(string userKql)
	{
		var sb = new StringBuilder(userKql.TrimEnd());
		if (DecodeCursor(Cursor) is { } cur)
		{
			var dt = DateTimeOffset.FromUnixTimeMilliseconds(cur.TimestampMs);
			var tsLit = $"datetime({dt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)})";
			sb.Append(CultureInfo.InvariantCulture, $"\n| where Timestamp < {tsLit} or (Timestamp == {tsLit} and Id < {cur.Id})");
		}
		sb.Append("\n| order by Timestamp desc, Id desc");
		sb.Append(CultureInfo.InvariantCulture, $"\n| take {PageSize + 1}");
		return sb.ToString();
	}

	// The paging cursor and live-tail's catch-up cursor are the SAME (TimestampMs, Id) key in the
	// SAME encoding — LogCursor owns both, so the two surfaces cannot drift apart on how a tie in
	// the millisecond is broken (live-tail-sse-transport-broken).
	static LogCursor? DecodeCursor(string? s) => LogCursor.TryDecode(s);

	static string EncodeCursor(DateTime timestamp, long id) => LogCursor.From(timestamp, id).Encode();

	public IActionResult OnGetKqlCompletions(string q, int pos)
	{
		var response = KqlCompletionService.Complete(q, pos);
		return Partial("_KqlCompletions", response);
	}
}
