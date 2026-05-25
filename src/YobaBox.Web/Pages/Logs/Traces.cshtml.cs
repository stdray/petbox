using LinqToDB;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using YobaBox.Core.Data;
using YobaBox.Core.Models;
using YobaBox.Log.Core.Data;
using YobaBox.Log.Core.Tracing;

namespace YobaBox.Web.Pages.Logs;

[Authorize]
public sealed class TracesModel : PageModel
{
	readonly YobaBoxDb _db;
	readonly ILogDbFactory _logFactory;

	public TracesModel(YobaBoxDb db, ILogDbFactory logFactory)
	{
		_db = db;
		_logFactory = logFactory;
	}

	[BindProperty(SupportsGet = true)]
	public string? ProjectKey { get; set; }

	public string EffectiveProjectKey { get; private set; } = "$system";
	public string? ProjectName { get; private set; }
	public IReadOnlyList<TraceSummary> Traces { get; private set; } = [];
	public bool SchemaMissing { get; private set; }

	public sealed record TraceSummary(string TraceId, string RootName, DateTime StartTime, TimeSpan Duration, int SpanCount, int WorstStatus);

	public async Task OnGetAsync(CancellationToken ct)
	{
		EffectiveProjectKey = string.IsNullOrEmpty(ProjectKey) ? "$system" : ProjectKey;
		var project = await _db.Projects.FirstOrDefaultAsync((Project p) => p.Key == EffectiveProjectKey, ct);
		ProjectName = project?.Name;

		var logDb = _logFactory.GetLogDb(EffectiveProjectKey);
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
