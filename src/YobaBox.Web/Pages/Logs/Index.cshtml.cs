using Kusto.Language;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using YobaBox.Log.Core.Data;
using YobaBox.Log.Core.Query;

namespace YobaBox.Web.Pages.Logs;

public sealed class IndexModel : PageModel
{
	readonly LogDb _logDb;

	public IndexModel(LogDb logDb)
	{
		_logDb = logDb;
	}

	[BindProperty(SupportsGet = true, Name = "kql")]
	public string? RawKql { get; set; }

	public string UserKql { get; private set; } = "";
	public string? KqlError { get; private set; }
	public bool SchemaMissing { get; private set; }
	public bool IsShapeChanged { get; private set; }

	public List<LogEntryViewModel> Events { get; } = [];
	public List<object?[]> KqlRows { get; } = [];
	public KqlResult? KqlResult { get; private set; }
	public string? NextCursor { get; private set; }
	public List<string> Services { get; private set; } = [];

	const int PageSize = 50;

	public async Task<IActionResult> OnGetAsync(CancellationToken ct)
	{
		UserKql = string.IsNullOrWhiteSpace(RawKql) ? "events" : RawKql.Trim();

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

		KustoCode code;
		try
		{
			code = KustoCode.Parse(UserKql);
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
				KqlResult = KqlTransformer.Execute(_logDb.LogEntries, code);
				await foreach (var row in KqlResult.Rows.WithCancellation(ct))
					KqlRows.Add(row);
			}
			else
			{
				var query = KqlTransformer.Apply(_logDb.LogEntries, code);
				var list = await query.ToListAsync(ct);
				foreach (var r in list)
					Events.Add(LogEntryViewModel.FromRecord(r));

				if (Events.Count > PageSize)
				{
					Events.RemoveAt(Events.Count - 1);
				}
			}
		}
		catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 1)
		{
			SchemaMissing = true;
			return Page();
		}
		catch (UnsupportedKqlException ex)
		{
			KqlError = ex.Message;
			return Page();
		}

		Services = await _logDb.LogEntries
			.Select(e => e.ServiceKey)
			.Distinct()
			.OrderBy(s => s)
			.ToListAsync(ct);

		if (Request.Headers.ContainsKey("HX-Request") && !IsShapeChanged)
			return Partial("_RowsFragment", this);

		return Page();
	}
}
