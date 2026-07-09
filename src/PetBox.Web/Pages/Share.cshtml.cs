using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Log.Core.Contract;
using PetBox.Log.Core.Data;
using PetBox.Log.Core.Query;
using PetBox.Log.Core.Sharing;

namespace PetBox.Web.Pages;

[AllowAnonymous]
public sealed class ShareModel : PageModel
{
	readonly PetBoxDb _db;
	readonly ILogService _logService;

	public ShareModel(PetBoxDb db, ILogService logService)
	{
		_db = db;
		_logService = logService;
	}

	[BindProperty(SupportsGet = true)]
	public string Token { get; set; } = string.Empty;

	public bool Expired { get; private set; }
	public bool ShareNotFound { get; private set; }
	public ShareLink? Link { get; private set; }
	public IReadOnlyList<string> Columns { get; private set; } = [];
	public IReadOnlyList<IReadOnlyList<string>> Rows { get; private set; } = [];
	public string? Error { get; private set; }

	public async Task OnGetAsync(CancellationToken ct)
	{
		var share = await _db.ShareLinks.FirstOrDefaultAsync((ShareLink s) => s.Id == Token, ct);
		if (share is null)
		{
			ShareNotFound = true;
			return;
		}
		Link = share;
		if (share.ExpiresAt < DateTime.UtcNow)
		{
			Expired = true;
			return;
		}

		var columns = JsonSerializer.Deserialize<string[]>(share.ColumnsJson) ?? [];
		var modesDict = JsonSerializer.Deserialize<Dictionary<string, MaskMode>>(share.ModesJson) ?? [];
		var policy = new FieldMaskingPolicy(modesDict.ToImmutableDictionary(
			kv => kv.Key, kv => kv.Value, StringComparer.Ordinal));
		var masker = new ValueMasker(Convert.FromBase64String(share.SaltBase64));

		try
		{
			// Memory guard only (KqlLimits.MaxTake, no default take) — same bound as the TSV export.
			var (records, _) = await _logService.ExecutePlainEventsQueryAsync(
				share.ProjectKey, share.LogName, share.Kql, KqlLimits.MaxTake, ct);
			var visible = columns.Where(c => policy.ModeFor(c) != MaskMode.Hide).ToArray();
			Columns = visible;

			var rendered = new List<IReadOnlyList<string>>();
			foreach (var r in records)
			{
				var e = r.ToEntry();
				var row = new List<string>(visible.Length);
				foreach (var col in visible)
				{
					var raw = LookupScalar(col, e);
					var cell = policy.ModeFor(col) == MaskMode.Mask ? masker.Mask(col, raw) : raw ?? "";
					row.Add(cell);
				}
				rendered.Add(row);
			}
			Rows = rendered;
		}
		catch (Exception ex)
		{
			Error = ex.Message;
		}
	}

	static string? LookupScalar(string column, PetBox.Log.Core.Models.LogEntry e) => column switch
	{
		"Id" => e.Id.ToString(CultureInfo.InvariantCulture),
		"Timestamp" => e.Timestamp.ToString("O", CultureInfo.InvariantCulture),
		"Level" => e.Level.ToString(),
		"MessageTemplate" => e.MessageTemplate,
		"Message" => e.Message,
		"Exception" => e.Exception ?? "",
		"ServiceKey" => e.ServiceKey,
		_ => null,
	};
}
