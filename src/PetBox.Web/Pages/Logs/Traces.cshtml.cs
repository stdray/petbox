using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Auth;
using PetBox.Log.Core.Contract;
using PetBox.Log.Core.Data;
using PetBox.Web.Auth;

namespace PetBox.Web.Pages.Logs;

// WorkspaceViewer: membership in the ROUTE workspace ({workspaceKey}), sysadmin free-pass.
// A bare [Authorize] here let ANY signed-in user read another tenant's data by typing the URL
// (workspace-access-isolation).
[Authorize(Policy = "WorkspaceViewer")]
// {projectKey} in the route IS the target tenant; ProjectWorkspaceBindingFilter still binds it to
// {workspaceKey} as a ROUTING question (404 on a mismatched URL), which is a different question.
[TenantFrom(TenantSource.Route, "projectKey")]
public sealed class TracesModel : PageModel
{
	readonly IProjectDirectory _projects;
	readonly ILogService _logs;

	public TracesModel(IProjectDirectory projects, ILogService logs)
	{
		_projects = projects;
		_logs = logs;
	}

	[BindProperty(SupportsGet = true)]
	public string? WorkspaceKey { get; set; }

	[BindProperty(SupportsGet = true)]
	public string? ProjectKey { get; set; }

	[BindProperty(SupportsGet = true, Name = "log")]
	public string? LogName { get; set; }

	// Error-level filter: only traces whose worst span status is Error (StatusCode 2).
	[BindProperty(SupportsGet = true, Name = "errorsOnly")]
	public bool ErrorsOnly { get; set; }

	// The paging arg is 'pageNum', not 'page' — 'page' is a reserved route-key in Razor
	// Pages, so a ?page=N value never binds (see the Data-module table view lesson).
	[BindProperty(SupportsGet = true, Name = "pageNum")]
	public int PageNum { get; set; }

	const int PageSize = 50;

	public string EffectiveProjectKey { get; private set; } = "";
	public string? SelectedLog { get; private set; }
	public string? ProjectName { get; private set; }
	public IReadOnlyList<string> AvailableLogs { get; private set; } = [];
	public IReadOnlyList<TraceSummary> Traces { get; private set; } = [];
	public bool HasNext { get; private set; }
	public bool SchemaMissing { get; private set; }

	// Distinct from SchemaMissing: the project has NO logs at all yet (nothing to select),
	// vs. a log being selected but its spans table not (yet) existing. Rendered as its own
	// alert with a "create a log" CTA, mirroring Pages/Logs/Index's NoLogs (logs-traces-default-log).
	public bool NoLogs { get; private set; }

	public sealed record TraceSummary(string TraceId, string RootName, DateTime StartTime, TimeSpan Duration, int SpanCount, int WorstStatus);

	public async Task OnGetAsync(CancellationToken ct)
	{
		EffectiveProjectKey = ProjectKey ?? "";
		if (string.IsNullOrEmpty(EffectiveProjectKey)) { SchemaMissing = true; return; }

		// The route workspace is proved by ProjectWorkspaceBindingFilter before this runs (see
		// ProjectHome/Index) — resolve by key alone; the row is still needed for ProjectName.
		var project = await _projects.GetAsync(EffectiveProjectKey, ct);
		if (project is null) { SchemaMissing = true; return; }
		ProjectName = project.Name;

		var logMetas = await _logs.ListAsync(EffectiveProjectKey, ct);
		AvailableLogs = logMetas.Select(l => l.Name).ToList();
		SelectedLog = DefaultLogSelector.Resolve(logMetas, LogName);
		if (SelectedLog is null) { NoLogs = true; return; }

		if (PageNum < 0) PageNum = 0;
		try
		{
			var page = await _logs.ListTraceGroupsAsync(
				EffectiveProjectKey, SelectedLog, ErrorsOnly, PageNum * PageSize, PageSize, ct);

			HasNext = page.HasNext;
			Traces = page.Rows.Select(r => new TraceSummary(
				r.TraceId,
				r.RootName,
				// unix-ns/100 = ticks SINCE THE UNIX EPOCH, not since year 1 — rebase explicitly.
				DateTime.UnixEpoch.AddTicks(r.MinStartNs / 100),
				TimeSpan.FromTicks((r.MaxEndNs - r.MinStartNs) / 100),
				r.SpanCount,
				r.WorstStatus)).ToList();
		}
		catch (LogSchemaMissingException)
		{
			SchemaMissing = true;
		}
	}
}
