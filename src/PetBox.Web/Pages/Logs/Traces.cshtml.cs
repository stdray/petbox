using LinqToDB;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Log.Core.Contract;
using PetBox.Log.Core.Data;
using PetBox.Log.Core.Tracing;

namespace PetBox.Web.Pages.Logs;

[Authorize]
public sealed class TracesModel : PageModel
{
	readonly PetBoxDb _db;
	readonly ILogService _logService;

	public TracesModel(PetBoxDb db, ILogService logService)
	{
		_db = db;
		_logService = logService;
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

	public sealed record TraceSummary(string TraceId, string RootName, DateTime StartTime, TimeSpan Duration, int SpanCount, int WorstStatus);

	public async Task OnGetAsync(CancellationToken ct)
	{
		EffectiveProjectKey = ProjectKey ?? "";
		if (string.IsNullOrEmpty(EffectiveProjectKey)) { SchemaMissing = true; return; }

		var project = await _db.Projects.FirstOrDefaultAsync((Project p) => p.Key == EffectiveProjectKey, ct);
		ProjectName = project?.Name;

		var logs = (await _logService.ListLogNamesAsync(EffectiveProjectKey, ct)).ToList();
		AvailableLogs = logs;
		SelectedLog = !string.IsNullOrWhiteSpace(LogName) && logs.Contains(LogName, StringComparer.Ordinal)
			? LogName
			: logs.Contains(LogNames.Default, StringComparer.Ordinal) ? LogNames.Default : logs.FirstOrDefault();
		if (SelectedLog is null) { SchemaMissing = true; return; }

		if (PageNum < 0) PageNum = 0;
		try
		{
			var offset = PageNum * PageSize;
			var summaries = await _logService.GetTraceGroupSummariesAsync(
				EffectiveProjectKey, SelectedLog, ErrorsOnly, offset, PageSize + 1, ct);

			HasNext = summaries.Count > PageSize;
			var pageItems = HasNext ? summaries.Take(PageSize).ToList() : summaries.ToList();

			var traceIds = pageItems.Select(g => g.TraceId).ToList();
			var roots = await _logService.GetRootSpansForTracesAsync(
				EffectiveProjectKey, SelectedLog, traceIds, ct);
			var rootByTrace = roots.ToDictionary(s => s.TraceId, s => s.Name);

			Traces = pageItems.Select(g => new TraceSummary(
				g.TraceId,
				rootByTrace.GetValueOrDefault(g.TraceId, "(no root)"),
				// unix-ns/100 = ticks SINCE THE UNIX EPOCH, not since year 1 — rebase explicitly.
				DateTime.UnixEpoch.AddTicks(g.MinStartNs / 100),
				TimeSpan.FromTicks((g.MaxEndNs - g.MinStartNs) / 100),
				g.SpanCount,
				g.WorstStatus)).ToList();
		}
		catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 1)
		{
			SchemaMissing = true;
		}
	}
}
