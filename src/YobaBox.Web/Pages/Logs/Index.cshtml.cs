using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Kusto.Language;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using YobaBox.Log.Core.Data;
using YobaBox.Log.Core.Query;

namespace YobaBox.Web.Pages.Logs;

[Authorize]
public sealed class IndexModel : PageModel
{
	readonly LogDb _logDb;

	public IndexModel(LogDb logDb)
	{
		_logDb = logDb;
	}

	[BindProperty(SupportsGet = true, Name = "kql")]
	public string? RawKql { get; set; }

	[BindProperty(SupportsGet = true)]
	public string? Cursor { get; set; }

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
					var last = Events[^1];
					NextCursor = EncodeCursor(last.Timestamp, last.Id);
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

	static (long TimestampMs, long Id)? DecodeCursor(string? s)
	{
		if (string.IsNullOrEmpty(s))
			return null;
		try
		{
			var bytes = Convert.FromBase64String(s);
			if (bytes.Length != 16)
				return null;
			var ts = BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(0, 8));
			var id = BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(8, 8));
			return (ts, id);
		}
		catch (FormatException)
		{
			return null;
		}
	}

	static string EncodeCursor(DateTime timestamp, long id)
	{
		var dto = new DateTimeOffset(timestamp, TimeSpan.Zero);
		var bytes = new byte[16];
		BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(0, 8), dto.ToUnixTimeMilliseconds());
		BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(8, 8), id);
		return Convert.ToBase64String(bytes);
	}

	public IActionResult OnGetKqlCompletions(string q, int pos)
	{
		var response = KqlCompletionService.Complete(q, pos);
		return Partial("_KqlCompletions", response);
	}
}
