using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Kusto.Language;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Auth;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Log.Core.Contract;
using PetBox.Log.Core.Query;
using PetBox.Log.Core.Sharing;

namespace PetBox.Web.Pages;

[AllowAnonymous]
// The browser half of the same grant PetBox.Log.Core.ShareApi's /api/share/{token}/tsv serves, and it
// carries the same declaration for the same reason: /ui/share/{token} has NO tenant in its route. The
// `{token}` is not a tenant reference — it is looked up (IShareLinkDirectory) and the STORED LINK, not
// the caller, names the project, the log, the KQL, the visible columns and the per-column masking. The
// caller supplies a token and nothing else; there is no field on this surface through which a tenant
// could be aimed.
//
// This is the class's strict reading, not its loose one (TenantDeclaration.cs): the token carries the
// EXTENT of access, it does not merely identify who is asking. Expiry is enforced below, on the link's
// own ExpiresAt.
[TenantExempt(TenantExemption.CapabilityToken,
	"the share token IS the grant: issued by an explicit act, and the stored link — not the caller — "
	+ "names the project, log, query, columns and masking of exactly what may be read")]
public sealed class ShareModel : PageModel
{
	readonly IShareLinkDirectory _shareLinks;
	readonly ILogService _logs;

	public ShareModel(IShareLinkDirectory shareLinks, ILogService logs)
	{
		_shareLinks = shareLinks;
		_logs = logs;
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
		var share = await _shareLinks.FindAsync(Token, ct);
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

		KustoCode code;
		try { code = KustoCode.Parse(share.Kql); }
		catch (Exception ex) { Error = ex.Message; return; }

		var columns = JsonSerializer.Deserialize<string[]>(share.ColumnsJson) ?? [];
		var modesDict = JsonSerializer.Deserialize<Dictionary<string, MaskMode>>(share.ModesJson) ?? [];
		var policy = new FieldMaskingPolicy(modesDict.ToImmutableDictionary(
			kv => kv.Key, kv => kv.Value, StringComparer.Ordinal));
		var masker = new ValueMasker(Convert.FromBase64String(share.SaltBase64));

		try
		{
			// Memory guard only (KqlLimits.MaxTake, no default take) — same bound as the TSV export.
			var records = await _logs.QueryEventsAsync(
				share.ProjectKey, share.LogName, code, KqlLimits.MaxTake, ct);
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
