using System.Text.Json;
using LinqToDB;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Contract;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Core.Services;

namespace PetBox.Web.Pages.Admin;

// Human-facing editor for the project's PORTABLE agent definitions (agent-definition-as-data):
// list / create / raw-JSON edit / delete, on the same documents agent_def_* MCP tools and the
// REST surface read and write. The REST API is ApiKey-scheme only, so this page talks to
// IAgentDefinitionService directly (the same door, minus the transport).
//
// Writes go through UpsertJsonAsync — the RAW-JSON path — on purpose: it runs the `model`-field
// reject over the wire shape the user actually typed (model binding is machine-local, never part
// of a portable definition). `version` is the optimistic-concurrency watermark (0 = create); a
// rejection (bad JSON / bad document / stale version) rerenders the editor with the service's
// message verbatim and the user's JSON preserved — never a silent overwrite.
[Authorize(Policy = "WorkspaceAdmin")]
public sealed class ProjectAgentDefsModel : PageModel
{
	readonly PetBoxDb _db;
	readonly IAgentDefinitionService _defs;

	public ProjectAgentDefsModel(PetBoxDb db, IAgentDefinitionService defs)
	{
		_db = db;
		_defs = defs;
	}

	// authz-bypass-project-create: route-only bind — see Admin/ProjectSettingsAdmin.cshtml.cs
	// for why these must never come from the form/query.
	[FromRoute(Name = "workspaceKey")]
	public string WorkspaceKey { get; set; } = string.Empty;

	[FromRoute(Name = "projectKey")]
	public string ProjectKey { get; set; } = string.Empty;

	// The definition currently open in the editor (?key=<slug>); empty = list only.
	[BindProperty(SupportsGet = true)]
	public string? Key { get; set; }

	public bool ProjectNotFound { get; private set; }
	public string? ErrorMessage { get; private set; }

	public IReadOnlyList<AgentDefinitionListItem> Items { get; private set; } = [];

	// The stored document behind ?key= (null when the key is unknown / none selected).
	public AgentDefinitionView? Stored { get; private set; }

	// The RAW stored text of that document — the editor prefills from THIS, not from the typed
	// projection: properties outside the schema (e.g. `notes`) are persisted verbatim and must
	// come back to the user unchanged.
	public string? StoredRawJson { get; private set; }

	// Textarea contents: the stored document pretty-printed, or the user's own JSON echoed
	// back after a rejected save.
	public string DefinitionJson { get; private set; } = string.Empty;

	// Optimistic-concurrency baseline for the next save (0 = create).
	public long Version { get; private set; }

	// A starter document for a freshly created key — the minimal shape the parser accepts.
	public static string StarterJson(string key) =>
		$$"""
		{
		  "name": "{{key}}",
		  "roles": [
		    {
		      "slug": "worker",
		      "tier": "worker",
		      "requiredCapabilities": [],
		      "spawn": { "allowed": false },
		      "escalation": { "available": false }
		    }
		  ]
		}
		""";

	public async Task<IActionResult> OnGetAsync(CancellationToken ct)
	{
		if (!await LoadStateAsync(ct)) return Page();
		PrefillStored();
		return Page();
	}

	// Create a definition under a new key by storing the starter document; the editor then
	// opens on it. The slug rule is enforced by the service (the input's `pattern` is a hint).
	public async Task<IActionResult> OnPostCreateAsync(string? key, CancellationToken ct)
	{
		var k = (key ?? string.Empty).Trim().ToLowerInvariant();
		try
		{
			var ack = await _defs.UpsertJsonAsync(ProjectKey, k, StarterJson(k), version: 0, ct);
			this.NotifySuccess($"Agent definition '{ack.Key}' created.");
			return RedirectToPage(new { WorkspaceKey, ProjectKey, Key = ack.Key });
		}
		catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or JsonException)
		{
			if (!await LoadStateAsync(ct)) return Page();
			PrefillStored();
			ErrorMessage = ex.Message;
			return Page();
		}
	}

	// Save the raw JSON in the editor against the posted watermark.
	public async Task<IActionResult> OnPostSaveAsync(string? key, string? definitionJson, long version, CancellationToken ct)
	{
		var k = (key ?? string.Empty).Trim().ToLowerInvariant();
		try
		{
			var ack = await _defs.UpsertJsonAsync(ProjectKey, k, definitionJson ?? string.Empty, version, ct);
			this.NotifySuccess($"Agent definition '{ack.Key}' saved (version {ack.Version}).");
			return RedirectToPage(new { WorkspaceKey, ProjectKey, Key = ack.Key });
		}
		catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or JsonException)
		{
			if (!await LoadStateAsync(ct)) return Page();
			KeepInput(k, definitionJson, version);
			ErrorMessage = ex.Message;
			return Page();
		}
	}

	public async Task<IActionResult> OnPostDeleteAsync(string? key, long version, CancellationToken ct)
	{
		var k = (key ?? string.Empty).Trim().ToLowerInvariant();
		try
		{
			await _defs.DeleteAsync(ProjectKey, k, version, ct);
			this.NotifySuccess($"Agent definition '{k}' deleted.");
			return RedirectToPage(new { WorkspaceKey, ProjectKey });
		}
		catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
		{
			if (!await LoadStateAsync(ct)) return Page();
			PrefillStored();
			ErrorMessage = ex.Message;
			return Page();
		}
	}

	// The project must exist AND live in the route workspace — the route is the only authority.
	async Task<bool> LoadStateAsync(CancellationToken ct)
	{
		var project = await _db.Projects.FirstOrDefaultAsync(
			(Project p) => p.Key == ProjectKey && p.WorkspaceKey == WorkspaceKey, ct);
		if (project is null) { ProjectNotFound = true; return false; }

		Items = await _defs.ListAsync(ProjectKey, ct);

		var pick = string.IsNullOrWhiteSpace(Key) ? null : Key.Trim().ToLowerInvariant();
		if (pick is not null)
		{
			try
			{
				Stored = await _defs.GetAsync(ProjectKey, pick, ct);
				StoredRawJson = Stored is null ? null : await _defs.GetJsonAsync(ProjectKey, pick, ct);
			}
			catch (ArgumentException)
			{
				Stored = null; // an unparseable key in the query string is just "nothing selected"
				StoredRawJson = null;
			}
		}
		Version = Stored?.Version ?? 0;
		return true;
	}

	// The stored document rendered into the editor: the RAW stored text, pretty-printed. Going
	// through the typed record here would strip every property outside the schema on display.
	void PrefillStored()
	{
		if (Stored is null) return;
		Key = Stored.Key;
		DefinitionJson = StoredRawJson is null
			? string.Empty
			: Pretty(StoredRawJson);
		Version = Stored.Version;
	}

	// Echo the user's input back after a rejected save (the POSTed version wins over the
	// freshly-loaded one — the user decides how to resolve a conflict).
	void KeepInput(string key, string? definitionJson, long version)
	{
		Key = key;
		DefinitionJson = definitionJson ?? string.Empty;
		Version = version;
	}

	// Display-only options: the wire shape, indented for a human editor.
	static readonly JsonSerializerOptions PrettyOptions =
		new(AgentDefinitionJson.Options) { WriteIndented = true };

	// Reformat the stored text without touching its content (unknown properties included).
	static string Pretty(string json)
	{
		try
		{
			using var doc = JsonDocument.Parse(json);
			return JsonSerializer.Serialize(doc.RootElement, PrettyOptions);
		}
		catch (JsonException)
		{
			return json; // never hide a stored document behind a formatting failure
		}
	}
}
