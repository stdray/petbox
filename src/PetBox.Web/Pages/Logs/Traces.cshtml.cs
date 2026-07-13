using LinqToDB;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Log.Core.Data;
using PetBox.Log.Core.Tracing;
using PetBox.Web.Auth;

namespace PetBox.Web.Pages.Logs;

// WorkspaceViewer: membership in the ROUTE workspace ({workspaceKey}), sysadmin free-pass.
// A bare [Authorize] here let ANY signed-in user read another tenant's data by typing the URL
// (workspace-access-isolation).
[Authorize(Policy = "WorkspaceViewer")]
public sealed class TracesModel : PageModel
{
	readonly IProjectDirectory _projects;
	readonly ILogStore _logStore;

	public TracesModel(IProjectDirectory projects, ILogStore logStore)
	{
		_projects = projects;
		_logStore = logStore;
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

		var logMetas = await _logStore.ListAsync(EffectiveProjectKey, ct);
		AvailableLogs = logMetas.Select(l => l.Name).ToList();
		SelectedLog = DefaultLogSelector.Resolve(logMetas, LogName);
		if (SelectedLog is null) { NoLogs = true; return; }

		if (PageNum < 0) PageNum = 0;
		using var logDb = _logStore.NewEnsuredContext(EffectiveProjectKey, SelectedLog);
		try
		{
			var q = logDb.Spans
				.GroupBy(s => s.TraceId)
				.Select(g => new
				{
					TraceId = g.Key,
					MinStart = g.Min(s => s.StartUnixNs),
					MaxEnd = g.Max(s => s.EndUnixNs),
					Count = g.Count(),
					WorstStatus = g.Max(s => s.StatusCode),
				});
			// Error filter runs at the query (a HAVING over the per-trace worst status), so
			// paging counts filtered traces — never a client-side cull of a full page.
			if (ErrorsOnly) q = q.Where(g => g.WorstStatus == 2);

			var offset = PageNum * PageSize;
			var grouped = await q
				.OrderByDescending(g => g.MinStart)
				.Skip(offset)
				.Take(PageSize + 1)
				.ToListAsync(ct);

			HasNext = grouped.Count > PageSize;
			if (HasNext) grouped.RemoveAt(grouped.Count - 1);

			var traceIds = grouped.Select(g => g.TraceId).ToList();
			var roots = await logDb.Spans
				.Where(s => traceIds.Contains(s.TraceId) && s.ParentSpanId == null)
				.ToListAsync(ct);
			var rootByTrace = roots.ToDictionary(s => s.TraceId, s => s.Name);

			Traces = grouped.Select(g => new TraceSummary(
				g.TraceId,
				rootByTrace.GetValueOrDefault(g.TraceId, "(no root)"),
				// unix-ns/100 = ticks SINCE THE UNIX EPOCH, not since year 1 — rebase explicitly.
				DateTime.UnixEpoch.AddTicks(g.MinStart / 100),
				TimeSpan.FromTicks((g.MaxEnd - g.MinStart) / 100),
				g.Count,
				g.WorstStatus)).ToList();
		}
		catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 1)
		{
			SchemaMissing = true;
		}
	}
}
