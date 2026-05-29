using LinqToDB;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Log.Core.Data;
using PetBox.Log.Core.Tracing;

namespace PetBox.Web.Pages.Logs;

[Authorize]
public sealed class TracesModel : PageModel
{
	readonly PetBoxDb _db;
	readonly ILogStore _logStore;

	public TracesModel(PetBoxDb db, ILogStore logStore)
	{
		_db = db;
		_logStore = logStore;
	}

	[BindProperty(SupportsGet = true)]
	public string? ProjectKey { get; set; }

	[BindProperty(SupportsGet = true, Name = "log")]
	public string? LogName { get; set; }

	public string EffectiveProjectKey { get; private set; } = "";
	public string? SelectedLog { get; private set; }
	public string? ProjectName { get; private set; }
	public IReadOnlyList<TraceSummary> Traces { get; private set; } = [];
	public bool SchemaMissing { get; private set; }

	public sealed record TraceSummary(string TraceId, string RootName, DateTime StartTime, TimeSpan Duration, int SpanCount, int WorstStatus);

	public async Task OnGetAsync(CancellationToken ct)
	{
		EffectiveProjectKey = ProjectKey ?? "";
		if (string.IsNullOrEmpty(EffectiveProjectKey)) { SchemaMissing = true; return; }

		var project = await _db.Projects.FirstOrDefaultAsync((Project p) => p.Key == EffectiveProjectKey, ct);
		ProjectName = project?.Name;

		var logs = (await _logStore.ListAsync(EffectiveProjectKey, ct)).Select(l => l.Name).ToList();
		SelectedLog = !string.IsNullOrWhiteSpace(LogName) && logs.Contains(LogName, StringComparer.Ordinal)
			? LogName
			: logs.Contains(LogNames.Default, StringComparer.Ordinal) ? LogNames.Default : logs.FirstOrDefault();
		if (SelectedLog is null) { SchemaMissing = true; return; }

		var logDb = _logStore.GetContext(EffectiveProjectKey, SelectedLog);
		try
		{
			var grouped = await logDb.Spans
				.GroupBy(s => s.TraceId)
				.Select(g => new
				{
					TraceId = g.Key,
					MinStart = g.Min(s => s.StartUnixNs),
					MaxEnd = g.Max(s => s.EndUnixNs),
					Count = g.Count(),
					WorstStatus = g.Max(s => s.StatusCode),
				})
				.OrderByDescending(g => g.MinStart)
				.Take(100)
				.ToListAsync(ct);

			var traceIds = grouped.Select(g => g.TraceId).ToList();
			var roots = await logDb.Spans
				.Where(s => traceIds.Contains(s.TraceId) && s.ParentSpanId == null)
				.ToListAsync(ct);
			var rootByTrace = roots.ToDictionary(s => s.TraceId, s => s.Name);

			Traces = grouped.Select(g => new TraceSummary(
				g.TraceId,
				rootByTrace.GetValueOrDefault(g.TraceId, "(no root)"),
				new DateTimeOffset(g.MinStart / 100, TimeSpan.Zero).UtcDateTime,
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
