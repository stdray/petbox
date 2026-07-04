using LinqToDB;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Data;
using PetBox.Core.Features;
using PetBox.Core.Models;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Workflow;
using PetBox.Web.Mcp;
using PetBox.Web.Pages.ProjectHome;

namespace PetBox.Web.Pages.Admin;

// Create / edit the project's METHODOLOGY DEFINITION as a JSON document — the human-facing
// equivalent of the MCP tasks_methodology_def_get / def_upsert pair, and the SAME wire shape
// (MethodologyWire), so a document moves freely between this textarea and the MCP tools.
// All writes go through ITasksService.DefineMethodologyAsync — full validation + live-node
// compatibility, optimistic concurrency on `version`; rejections render verbatim in the
// errors block with the user's JSON preserved (never a silent overwrite).
[Authorize(Policy = "WorkspaceAdmin")]
public sealed class ProjectMethodologyModel : PageModel
{
	readonly PetBoxDb _db;
	readonly FeatureFlags _features;
	readonly ITasksService _tasks;

	public ProjectMethodologyModel(PetBoxDb db, FeatureFlags features, ITasksService tasks)
	{
		_db = db;
		_features = features;
		_tasks = tasks;
	}

	[BindProperty(SupportsGet = true)]
	public string WorkspaceKey { get; set; } = string.Empty;

	[BindProperty(SupportsGet = true)]
	public string ProjectKey { get; set; } = string.Empty;

	// Set by the post-save redirect (?saved=True) — renders the success alert exactly once.
	[BindProperty(SupportsGet = true)]
	public bool Saved { get; set; }

	public bool ProjectNotFound { get; private set; }
	public string? ErrorMessage { get; private set; }

	// The stored definition + revision metadata; null = the project runs on builtin presets.
	public MethodologyDefView? Stored { get; private set; }

	// Textarea contents: the stored definition rendered as the def_get document (prefill), a
	// preset template, or the user's own JSON echoed back after a rejected save/preview.
	public string DefinitionJson { get; private set; } = string.Empty;
	public string MigrationJson { get; private set; } = string.Empty;

	// Optimistic-concurrency baseline for the next save (0 = no definition yet).
	public long Version { get; private set; }

	// JSON island for the SVG preview (ts/methodology-preview.ts → renderWorkflow): an array
	// of {kind, blocks} docs, one per definition kind. Empty = nothing to preview.
	public string PreviewJson { get; private set; } = string.Empty;

	// Preset templates offered by the "Load preset as template" control — read straight off
	// the registry (quartet, classic today), so a new preset appears without touching the page.
	public IReadOnlyList<MethodologyPresets.MethodologyProvisioningPreset> Presets { get; } =
		MethodologyPresets.ProvisioningPresets;

	public async Task<IActionResult> OnGetAsync(CancellationToken ct)
	{
		if (!_features.IsEnabled(Feature.Tasks)) return NotFound();
		if (!await LoadStateAsync(ct)) return Page();

		if (Stored is not null)
		{
			DefinitionJson = MethodologyWire.ToJson(
				MethodologyWire.ProjectDefinition(Stored.Definition, Stored.Version, Stored.Created, Stored.Updated));
			PreviewJson = PreviewOf(Stored.Definition);
		}
		return Page();
	}

	// Fill the textarea with a builtin preset rendered as a definition document — the same
	// template tasks_methodology_def_get returns for `preset:` — and preview it right away.
	public async Task<IActionResult> OnPostLoadPresetAsync(string? preset, CancellationToken ct)
	{
		if (!_features.IsEnabled(Feature.Tasks)) return NotFound();
		if (!await LoadStateAsync(ct)) return Page();

		try
		{
			var def = MethodologyPresets.RenderPresetDefinition(preset);
			DefinitionJson = MethodologyWire.ToJson(
				MethodologyWire.ProjectDefinition(def, version: 0, created: null, updated: null));
			PreviewJson = PreviewOf(def);
		}
		catch (ArgumentException ex)
		{
			ErrorMessage = ex.Message;
		}
		return Page();
	}

	// Render the FSM preview for the document currently in the textarea — parse-only, nothing
	// is written; parse failures land in the errors block with the JSON preserved.
	public async Task<IActionResult> OnPostPreviewAsync(string? definitionJson, string? migrationJson, long version, CancellationToken ct)
	{
		if (!_features.IsEnabled(Feature.Tasks)) return NotFound();
		if (!await LoadStateAsync(ct)) return Page();
		KeepInput(definitionJson, migrationJson, version);

		try
		{
			PreviewJson = PreviewOf(MethodologyWire.ParseDocument(definitionJson));
		}
		catch (ArgumentException ex)
		{
			ErrorMessage = ex.Message;
		}
		return Page();
	}

	// Install the document via the service door (validation + live-node compatibility +
	// version watermark). Success redirects (fresh state + saved alert); any rejection —
	// bad JSON, validation, incompatible live nodes, version conflict — rerenders with the
	// service's message and the user's JSON intact.
	public async Task<IActionResult> OnPostSaveAsync(string? definitionJson, string? migrationJson, long version, CancellationToken ct)
	{
		if (!_features.IsEnabled(Feature.Tasks)) return NotFound();

		try
		{
			var def = MethodologyWire.ParseDocument(definitionJson);
			var migration = MethodologyWire.ParseMigrationDocument(migrationJson);
			await _tasks.DefineMethodologyAsync(ProjectKey, def, version, migration, ct);
		}
		catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
		{
			if (!await LoadStateAsync(ct)) return Page();
			KeepInput(definitionJson, migrationJson, version);
			ErrorMessage = ex.Message;
			return Page();
		}

		return RedirectToPage(new { WorkspaceKey, ProjectKey, Saved = true });
	}

	async Task<bool> LoadStateAsync(CancellationToken ct)
	{
		var project = await _db.Projects.FirstOrDefaultAsync((Project p) => p.Key == ProjectKey, ct);
		if (project is null) { ProjectNotFound = true; return false; }
		Stored = await _tasks.GetMethodologyDefinitionAsync(ProjectKey, ct);
		Version = Stored?.Version ?? 0;
		return true;
	}

	// Echo the user's input back after a rejected save / a preview (the posted version wins
	// over the freshly-loaded one — the user decides how to resolve a conflict).
	void KeepInput(string? definitionJson, string? migrationJson, long version)
	{
		DefinitionJson = definitionJson ?? string.Empty;
		MigrationJson = migrationJson ?? string.Empty;
		Version = version;
	}

	// Project the definition onto the workflow-graph doc array the SVG renderer consumes —
	// per kind, per workflow block, through the SAME WorkflowGraphJson mapping the per-type
	// workflow modal uses.
	static string PreviewOf(MethodologyDefinition def) =>
		WorkflowGraphJson.SerializeMany(def.Kinds.Select(k => new BoardWorkflowView(
			k.Kind,
			[.. k.Workflows.Select(w => new WorkflowBlock(w.Types, w.ToWorkflow(w.Types.Count > 0 ? w.Types[0] : k.Kind)))])));
}
