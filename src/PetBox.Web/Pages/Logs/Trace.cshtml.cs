using System.Text.Json;
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
	readonly ILogStore _logStore;

	public TraceModel(ILogStore logStore) => _logStore = logStore;

	[BindProperty(SupportsGet = true)]
	public string? WorkspaceKey { get; set; }

	[BindProperty(SupportsGet = true)]
	public string? ProjectKey { get; set; }

	[BindProperty(SupportsGet = true, Name = "log")]
	public string? LogName { get; set; }

	[BindProperty(SupportsGet = true)]
	public string TraceId { get; set; } = string.Empty;

	public string EffectiveWorkspaceKey { get; private set; } = "";
	public string EffectiveProjectKey { get; private set; } = "";
	// The log actually resolved for this trace — carried on the "← traces" back link so it
	// returns to the same log the user was viewing.
	public string? SelectedLog { get; private set; }
	public IReadOnlyList<WaterfallRow> Rows { get; private set; } = [];
	public bool TraceNotFound { get; private set; }
	public DateTime StartTime { get; private set; }
	public TimeSpan TotalDuration { get; private set; }

	public sealed record WaterfallRow(
		SpanRecord Span,
		double StartPct,
		double WidthPct,
		int Depth);

	// Resource/SDK attributes are merged into every span's AttributesJson at ingest and are
	// constant across the whole trace — dropping them leaves what distinguishes THIS span.
	static readonly string[] NoisePrefixes = ["telemetry.sdk.", "service."];

	public static IReadOnlyList<KeyValuePair<string, string>> DisplayAttributes(SpanRecord span)
	{
		if (string.IsNullOrWhiteSpace(span.AttributesJson)) return [];
		try
		{
			using var doc = JsonDocument.Parse(span.AttributesJson);
			if (doc.RootElement.ValueKind != JsonValueKind.Object) return [];
			return doc.RootElement.EnumerateObject()
				.Where(p => !NoisePrefixes.Any(n => p.Name.StartsWith(n, StringComparison.Ordinal)))
				.OrderBy(p => p.Name, StringComparer.Ordinal)
				.Select(p => new KeyValuePair<string, string>(p.Name,
					p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : p.Value.GetRawText()))
				.ToList();
		}
		catch (JsonException)
		{
			return [];
		}
	}

	public static string KindName(int kind) => kind switch
	{
		1 => "Server",
		2 => "Client",
		3 => "Producer",
		4 => "Consumer",
		_ => "Internal",
	};

	public static string StatusName(int statusCode) => statusCode switch
	{
		1 => "Ok",
		2 => "Error",
		_ => "Unset",
	};

	public async Task OnGetAsync(CancellationToken ct)
	{
		EffectiveWorkspaceKey = WorkspaceKey ?? "";
		EffectiveProjectKey = ProjectKey ?? "";
		if (string.IsNullOrEmpty(EffectiveProjectKey)) { TraceNotFound = true; return; }

		var logs = (await _logStore.ListAsync(EffectiveProjectKey, ct)).Select(l => l.Name).ToList();
		var selectedLog = !string.IsNullOrWhiteSpace(LogName) && logs.Contains(LogName, StringComparer.Ordinal)
			? LogName
			: logs.Contains(LogNames.Default, StringComparer.Ordinal) ? LogNames.Default : logs.FirstOrDefault();
		SelectedLog = selectedLog;
		if (selectedLog is null) { TraceNotFound = true; return; }

		using var logDb = _logStore.GetContext(EffectiveProjectKey, selectedLog);

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
		// unix-ns/100 = ticks SINCE THE UNIX EPOCH, not since year 1 — rebase explicitly.
		StartTime = DateTime.UnixEpoch.AddTicks(minStart / 100);
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
