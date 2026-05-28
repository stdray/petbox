using LinqToDB;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Log.Core.Data;
using PetBox.Log.Core.Tracing;

namespace PetBox.Web.Pages.Logs;

[Authorize]
public sealed class TraceModel : PageModel
{
	readonly ILogDbFactory _logFactory;

	public TraceModel(ILogDbFactory logFactory) => _logFactory = logFactory;

	[BindProperty(SupportsGet = true)]
	public string? ProjectKey { get; set; }

	[BindProperty(SupportsGet = true)]
	public string TraceId { get; set; } = string.Empty;

	public string EffectiveProjectKey { get; private set; } = "$system";
	public IReadOnlyList<WaterfallRow> Rows { get; private set; } = [];
	public bool TraceNotFound { get; private set; }
	public DateTime StartTime { get; private set; }
	public TimeSpan TotalDuration { get; private set; }

	public sealed record WaterfallRow(
		SpanRecord Span,
		double StartPct,
		double WidthPct,
		int Depth);

	public async Task OnGetAsync(CancellationToken ct)
	{
		EffectiveProjectKey = string.IsNullOrEmpty(ProjectKey) ? "$system" : ProjectKey;
		var logDb = _logFactory.GetLogDb(EffectiveProjectKey);

		var spans = await logDb.Spans
			.Where(s => s.TraceId == TraceId)
			.OrderBy(s => s.StartUnixNs)
			.ToListAsync(ct);

		if (spans.Count == 0)
		{
			TraceNotFound = true;
			return;
		}

		var minStart = spans.Min(s => s.StartUnixNs);
		var maxEnd = spans.Max(s => s.EndUnixNs);
		var totalNs = Math.Max(1, maxEnd - minStart);
		StartTime = new DateTimeOffset(minStart / 100, TimeSpan.Zero).UtcDateTime;
		TotalDuration = TimeSpan.FromTicks(totalNs / 100);

		var childrenBy = spans.GroupBy(s => s.ParentSpanId ?? string.Empty)
			.ToDictionary(g => g.Key, g => g.OrderBy(s => s.StartUnixNs).ToList());

		var roots = spans.Where(s => string.IsNullOrEmpty(s.ParentSpanId)).ToList();
		if (roots.Count == 0)
			roots = [spans[0]];

		var rows = new List<WaterfallRow>();
		foreach (var root in roots)
			Walk(root, 0, childrenBy, minStart, totalNs, rows);
		Rows = rows;
	}

	static void Walk(SpanRecord span, int depth, Dictionary<string, List<SpanRecord>> childrenBy, long minStart, long totalNs, List<WaterfallRow> rows)
	{
		var startPct = 100.0 * (span.StartUnixNs - minStart) / totalNs;
		var widthPct = 100.0 * (span.EndUnixNs - span.StartUnixNs) / totalNs;
		rows.Add(new WaterfallRow(span, Math.Max(0, startPct), Math.Max(0.5, widthPct), depth));

		if (childrenBy.TryGetValue(span.SpanId, out var kids))
		{
			foreach (var k in kids)
				Walk(k, depth + 1, childrenBy, minStart, totalNs, rows);
		}
	}
}
