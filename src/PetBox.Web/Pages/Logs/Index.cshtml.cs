using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Kusto.Language;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Log.Core.Data;
using PetBox.Log.Core.Query;

namespace PetBox.Web.Pages.Logs;

[Authorize]
public sealed class IndexModel : PageModel
{
	readonly ILogStore _logStore;
	readonly PetBoxDb _db;

	public IndexModel(ILogStore logStore, PetBoxDb db)
	{
		_logStore = logStore;
		_db = db;
	}

	[BindProperty(SupportsGet = true, Name = "kql")]
	public string? RawKql { get; set; }

	[BindProperty(SupportsGet = true)]
	public string? Cursor { get; set; }

	[BindProperty(SupportsGet = true, Name = "saved")]
	public string? SavedName { get; set; }

	[BindProperty(SupportsGet = true, Name = "workspaceKey")]
	public string? WorkspaceKey { get; set; }

	[BindProperty(SupportsGet = true, Name = "projectKey")]
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
	public KqlResult? KqlResult { get; private set; }
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

		var project = await _db.Projects
			.FirstOrDefaultAsync((Project p) => p.Key == projectFilter, ct);
		if (project is null)
			return Page();

		ProjectKey = project.Key;
		ProjectName = project.Name;

		// Named logs available in this project; pick the requested one, else the
		// conventional `default`, else the first alphabetically.
		AvailableLogs = (await _logStore.ListAsync(ProjectKey, ct)).Select(l => l.Name).ToList();
		SelectedLog = ResolveSelectedLog();
		if (SelectedLog is null)
		{
			NoLogs = true;
			return Page();
		}

		// Saved queries are project-scoped (shared across the project's logs).
		var pk = ProjectKey;
		SavedQueries = await _db.SavedQueries
			.Where(q => q.ProjectKey == pk)
			.OrderBy(q => q.Name)
			.ToListAsync(ct);

		if (!string.IsNullOrWhiteSpace(SavedName))
		{
			var saved = await _db.SavedQueries
				.FirstOrDefaultAsync((SavedQuery q) => q.ProjectKey == pk && q.Name == SavedName, ct);
			if (saved is not null)
			{
				RawKql = saved.Kql;
				ActiveSavedName = saved.Name;
			}
		}

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

		var logDb = _logStore.GetContext(ProjectKey, SelectedLog);

		try
		{
			if (IsShapeChanged)
			{
				KqlResult = KqlTransformer.Execute(logDb.LogEntries, code);
				await foreach (var row in KqlResult.Rows.WithCancellation(ct))
					KqlRows.Add(row);
			}
			else
			{
				var query = KqlTransformer.Apply(logDb.LogEntries, code);
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

		Services = await logDb.LogEntries
			.Select(e => e.ServiceKey)
			.Distinct()
			.OrderBy(s => s)
			.ToListAsync(ct);

		if (Request.Headers.ContainsKey("HX-Request") && !IsShapeChanged)
			return Partial("_RowsFragment", this);

		return Page();
	}

	string? ResolveSelectedLog()
	{
		if (AvailableLogs.Count == 0)
			return null;
		if (!string.IsNullOrWhiteSpace(LogNameRoute) && AvailableLogs.Contains(LogNameRoute, StringComparer.Ordinal))
			return LogNameRoute;
		if (AvailableLogs.Contains(LogNames.Default, StringComparer.Ordinal))
			return LogNames.Default;
		return AvailableLogs[0];
	}

	public async Task<IActionResult> OnPostSaveAsync(
		[FromForm(Name = "name")] string? name,
		[FromForm(Name = "kql")] string? kql,
		CancellationToken ct)
	{
		if (string.IsNullOrWhiteSpace(ProjectKeyRoute) || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(kql))
			return RedirectToProject();

		var existing = await _db.SavedQueries
			.FirstOrDefaultAsync((SavedQuery q) => q.ProjectKey == ProjectKeyRoute && q.Name == name.Trim(), ct);
		if (existing is not null)
		{
#pragma warning disable CA2016
			await _db.UpdateAsync(existing with { Kql = kql, UpdatedAt = DateTime.UtcNow });
#pragma warning restore CA2016
		}
		else
		{
#pragma warning disable CA2016
			await _db.InsertAsync(new SavedQuery
			{
				Name = name.Trim(),
				Kql = kql,
				ProjectKey = ProjectKeyRoute,
				CreatedAt = DateTime.UtcNow,
				UpdatedAt = DateTime.UtcNow,
			});
#pragma warning restore CA2016
		}

		return RedirectToProject(name.Trim());
	}

	public async Task<IActionResult> OnPostDeleteAsync(
		[FromForm(Name = "savedId")] long savedId,
		CancellationToken ct)
	{
#pragma warning disable CA2016
		await _db.SavedQueries
			.Where(q => q.Id == savedId)
			.DeleteAsync();
#pragma warning restore CA2016

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

	static (long TimestampMs, long Id)? DecodeCursor(string? s)
	{
		if (string.IsNullOrEmpty(s)) return null;
		try
		{
			var bytes = Convert.FromBase64String(s);
			if (bytes.Length != 16) return null;
			var ts = BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(0, 8));
			var id = BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(8, 8));
			return (ts, id);
		}
		catch (FormatException) { return null; }
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
