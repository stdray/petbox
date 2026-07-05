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

// Create / view / edit / delete the project's METHODOLOGY DEFINITION — the human-facing
// equivalent of the MCP tasks_methodology_def_* tools, sharing their wire shape
// (MethodologyWire), so a document moves freely between this page and MCP.
//
// The page is a small state machine (Mode), NOT a SPA — plain Razor handlers + a `step`
// query param for deep links:
//   - stored definition → VIEW mode (summary + preview; explicit Edit / Delete), ?step=edit
//     opens the editor prefilled;
//   - no definition → a "Create methodology" call-to-action, ?step=base the base picker
//     (builtin provisioning presets + user definitions from other projects, each with an
//     SVG preview), then the editor, then a confirm summary → save;
//   - POST-rendered states (template loaded, preview, confirm, rejected save/delete) set
//     Mode directly.
// All writes go through ITasksService (full validation + live-node compatibility,
// optimistic concurrency on `version`); rejections render verbatim in the errors block
// with the user's JSON preserved (never a silent overwrite).
[Authorize(Policy = "WorkspaceAdmin")]
public sealed class ProjectMethodologyModel : PageModel
{
	public enum EditorMode { View, Cta, Base, Edit, Confirm }

	readonly PetBoxDb _db;
	readonly FeatureFlags _features;
	readonly ITasksService _tasks;

	public ProjectMethodologyModel(PetBoxDb db, FeatureFlags features, ITasksService tasks)
	{
		_db = db;
		_features = features;
		_tasks = tasks;
	}

	// authz-bypass-project-create: route-only bind — see Admin/Projects.cshtml.cs for why.
	[FromRoute(Name = "workspaceKey")]
	public string WorkspaceKey { get; set; } = string.Empty;

	[FromRoute(Name = "projectKey")]
	public string ProjectKey { get; set; } = string.Empty;

	// Set by the post-save redirect (?saved=True) — renders the success alert exactly once.
	[BindProperty(SupportsGet = true)]
	public bool Saved { get; set; }

	// Set by the post-delete redirect (?deleted=True) — renders the reverted-to-presets
	// alert exactly once.
	[BindProperty(SupportsGet = true)]
	public bool Deleted { get; set; }

	// The wizard step a GET deep-links into: "base" (choose a base) or "edit" (the editor);
	// anything else falls back to the state's default (view mode / the create CTA).
	[BindProperty(SupportsGet = true)]
	public string? Step { get; set; }

	// What the page renders — derived from the stored state + Step on GET, set directly by
	// the POST handlers.
	public EditorMode Mode { get; private set; } = EditorMode.Edit;

	public bool ProjectNotFound { get; private set; }
	public string? ErrorMessage { get; private set; }

	// The stored definition + revision metadata; null = the project runs on builtin presets.
	public MethodologyDefView? Stored { get; private set; }

	// The project's ACTIVE boards — surfaced in the definition-less state so a methodology
	// enabled from a preset (boards only, no stored document) is still VISIBLE here: the
	// owner sees which kinds run and where their process actually comes from.
	public IReadOnlyList<TaskBoardMeta> ActiveBoards { get; private set; } = [];

	// The preset the "Load preset as template" control last loaded — echoed back so the
	// select keeps the user's choice instead of snapping to the first option.
	public string? SelectedPreset { get; private set; }

	// Textarea contents: the stored definition rendered as the def_get document (prefill), a
	// preset template, or the user's own JSON echoed back after a rejected save/preview.
	public string DefinitionJson { get; private set; } = string.Empty;
	public string MigrationJson { get; private set; } = string.Empty;

	// Optimistic-concurrency baseline for the next save (0 = no definition yet).
	public long Version { get; private set; }

	// JSON island for the SVG preview (ts/methodology-preview.ts → renderWorkflow): an array
	// of {kind, blocks, effectNotes} docs, one per definition kind. Empty = nothing to preview.
	public string PreviewJson { get; private set; } = string.Empty;

	// ── wizard state ──────────────────────────────────────────────────────────

	// One base the creation wizard offers: a builtin provisioning preset (`preset:<slug>`)
	// or another project's stored definition (`def:<projectKey>`).
	public sealed record BaseOption(string Ref, string Title, string Description);

	public IReadOnlyList<BaseOption> Bases { get; private set; } = [];

	// JSON island for the base picker's per-card SVG previews ([{ref, docs}]).
	public string BasePreviewsJson { get; private set; } = string.Empty;

	// Per-kind digest for the confirm step and the view mode: counts + the gate lines +
	// the effect sentences (MethodologyGuide phrasing).
	public sealed record KindSummary(
		string Kind, int TypeCount, int StatusCount, int TransitionCount,
		IReadOnlyList<string> Gates, IReadOnlyList<string> Effects);

	public IReadOnlyList<KindSummary> Summary { get; private set; } = [];

	// The parsed document's name shown on the confirm step (view mode reads Stored instead).
	public string? ConfirmName { get; private set; }

	// Preset templates offered by the "Load preset as template" control — read straight off
	// the registry (quartet, classic today), so a new preset appears without touching the page.
	public IReadOnlyList<MethodologyPresets.MethodologyProvisioningPreset> Presets { get; } =
		MethodologyPresets.ProvisioningPresets;

	public async Task<IActionResult> OnGetAsync(CancellationToken ct)
	{
		if (!_features.IsEnabled(Feature.Tasks)) return NotFound();
		if (!await LoadStateAsync(ct)) return Page();

		var step = Step?.Trim().ToLowerInvariant();
		if (Stored is not null)
		{
			// Existing definition: view mode by default; ?step=edit opens the editor.
			Mode = step == "edit" ? EditorMode.Edit : EditorMode.View;
			PrefillStored();
			if (Mode == EditorMode.View) Summary = SummaryOf(Stored.Definition);
		}
		else if (step == "base")
		{
			Mode = EditorMode.Base;
			await LoadBasesAsync(ct);
		}
		else if (step == "edit")
		{
			Mode = EditorMode.Edit; // paste-JSON path (empty editor)
		}
		else
		{
			Mode = EditorMode.Cta; // creation is an explicit action, not a bare textarea
		}
		return Page();
	}

	// Wizard step 1 → 2: resolve the chosen base (builtin preset or another project's
	// definition) and open the editor prefilled with it.
	public async Task<IActionResult> OnPostStartEditAsync(string? baseRef, CancellationToken ct)
	{
		if (!_features.IsEnabled(Feature.Tasks)) return NotFound();
		if (!await LoadStateAsync(ct)) return Page();

		try
		{
			var def = await ResolveBaseAsync(baseRef, ct);
			Mode = EditorMode.Edit;
			DefinitionJson = MethodologyWire.ToJson(
				MethodologyWire.ProjectDefinition(def, version: 0, created: null, updated: null));
			PreviewJson = PreviewOf(def);
		}
		catch (ArgumentException ex)
		{
			Mode = EditorMode.Base;
			await LoadBasesAsync(ct);
			ErrorMessage = ex.Message;
		}
		return Page();
	}

	// Fill the textarea with a builtin preset rendered as a definition document — the same
	// template tasks_methodology_def_get returns for `preset:` — and preview it right away.
	public async Task<IActionResult> OnPostLoadPresetAsync(string? preset, CancellationToken ct)
	{
		if (!_features.IsEnabled(Feature.Tasks)) return NotFound();
		if (!await LoadStateAsync(ct)) return Page();
		Mode = EditorMode.Edit;

		try
		{
			var def = MethodologyPresets.RenderPresetDefinition(preset);
			SelectedPreset = def.Name; // the resolved slug, so the select tracks what actually loaded
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
		Mode = EditorMode.Edit;

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

	// Wizard step 2 → 3: parse the document and render the confirm summary (kinds/statuses/
	// transitions counts, the gates, the effects) with the JSON carried in hidden fields.
	// Parse failures fall back to the editor with the message; the DEEP validation
	// (integrity + live-node compatibility) still happens in the service on Save.
	public async Task<IActionResult> OnPostConfirmAsync(string? definitionJson, string? migrationJson, long version, CancellationToken ct)
	{
		if (!_features.IsEnabled(Feature.Tasks)) return NotFound();
		if (!await LoadStateAsync(ct)) return Page();
		KeepInput(definitionJson, migrationJson, version);

		try
		{
			var def = MethodologyWire.ParseDocument(definitionJson);
			MethodologyWire.ParseMigrationDocument(migrationJson); // surface bad migration JSON now, not on save
			Mode = EditorMode.Confirm;
			ConfirmName = def.Name;
			Summary = SummaryOf(def);
		}
		catch (ArgumentException ex)
		{
			Mode = EditorMode.Edit;
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
			Mode = EditorMode.Edit;
			ErrorMessage = ex.Message;
			return Page();
		}

		return RedirectToPage(new { WorkspaceKey, ProjectKey, Saved = true });
	}

	// Delete the stored definition — revert the project to the builtin presets. The service
	// door validates live nodes against the preset resolution first (an incompatible node
	// rejects with a clear message) and applies the same version watermark as a save; any
	// rejection rerenders the view mode with the stored state intact and the service's message.
	public async Task<IActionResult> OnPostDeleteAsync(long version, CancellationToken ct)
	{
		if (!_features.IsEnabled(Feature.Tasks)) return NotFound();

		try
		{
			await _tasks.DeleteMethodologyAsync(ProjectKey, version, ct);
		}
		catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
		{
			if (!await LoadStateAsync(ct)) return Page();
			Mode = Stored is not null ? EditorMode.View : EditorMode.Cta;
			PrefillStored();
			if (Stored is not null) Summary = SummaryOf(Stored.Definition);
			ErrorMessage = ex.Message;
			return Page();
		}

		return RedirectToPage(new { WorkspaceKey, ProjectKey, Deleted = true });
	}

	async Task<bool> LoadStateAsync(CancellationToken ct)
	{
		var project = await _db.Projects.FirstOrDefaultAsync((Project p) => p.Key == ProjectKey, ct);
		if (project is null) { ProjectNotFound = true; return false; }
		Stored = await _tasks.GetMethodologyDefinitionAsync(ProjectKey, ct);
		Version = Stored?.Version ?? 0;
		ActiveBoards = (await _tasks.ListBoardsAsync(ProjectKey, ct)).Where(b => b.ClosedAt == null).ToList();
		return true;
	}

	// The stored definition rendered into the editor (document prefill + preview) — the GET
	// states and the delete-rejected state show the same thing.
	void PrefillStored()
	{
		if (Stored is null) return;
		DefinitionJson = MethodologyWire.ToJson(
			MethodologyWire.ProjectDefinition(Stored.Definition, Stored.Version, Stored.Created, Stored.Updated));
		PreviewJson = PreviewOf(Stored.Definition);
	}

	// Echo the user's input back after a rejected save / a preview (the posted version wins
	// over the freshly-loaded one — the user decides how to resolve a conflict).
	void KeepInput(string? definitionJson, string? migrationJson, long version)
	{
		DefinitionJson = definitionJson ?? string.Empty;
		MigrationJson = migrationJson ?? string.Empty;
		Version = version;
	}

	// The base picker's options: every builtin provisioning preset, then every OTHER
	// project's stored definition (the admin reuses a methodology already authored
	// elsewhere) — each with its graph docs for the per-card SVG preview.
	async Task LoadBasesAsync(CancellationToken ct)
	{
		var options = new List<BaseOption>();
		var previews = new List<(string Ref, IEnumerable<(BoardWorkflowView View, IReadOnlyList<string> EffectNotes)> Views)>();

		foreach (var p in MethodologyPresets.ProvisioningPresets)
		{
			var slug = $"preset:{p.Slug}";
			options.Add(new(slug, $"{p.DisplayName} — builtin preset", p.Description));
			previews.Add((slug, GraphViews(MethodologyPresets.RenderPresetDefinition(p.Slug))));
		}

		var projects = await _db.Projects.Where((Project p) => p.Key != ProjectKey)
			.OrderBy(p => p.Key).ToListAsync(ct);
		foreach (var p in projects)
		{
			var view = await _tasks.GetMethodologyDefinitionAsync(p.Key, ct);
			if (view is null) continue;
			var slug = $"def:{p.Key}";
			options.Add(new(slug,
				$"{view.Definition.Name} — definition of project {p.Key}",
				$"User definition (version {view.Version}) — kinds: {string.Join(", ", view.Definition.Kinds.Select(k => k.Kind))}."));
			previews.Add((slug, GraphViews(view.Definition)));
		}

		Bases = options;
		BasePreviewsJson = WorkflowGraphJson.SerializeBases(previews);
	}

	// Resolve a base picker choice: `preset:<slug>` renders the builtin preset as a
	// document; `def:<projectKey>` copies that project's stored definition.
	async Task<MethodologyDefinition> ResolveBaseAsync(string? baseRef, CancellationToken ct)
	{
		var slug = (baseRef ?? string.Empty).Trim();
		if (slug.StartsWith("preset:", StringComparison.Ordinal))
			return MethodologyPresets.RenderPresetDefinition(slug["preset:".Length..]);
		if (slug.StartsWith("def:", StringComparison.Ordinal))
		{
			var projectKey = slug["def:".Length..];
			var view = await _tasks.GetMethodologyDefinitionAsync(projectKey, ct);
			return view?.Definition
				?? throw new ArgumentException($"project '{projectKey}' has no stored methodology definition");
		}
		throw new ArgumentException("pick a base to start from (a builtin preset or an existing definition)");
	}

	// Project the definition onto the workflow-graph doc array the SVG renderer consumes —
	// per kind, per workflow block, through the SAME WorkflowGraphJson mapping the per-type
	// workflow modal uses. Kind-level transition effects have no edge to live on, so each
	// kind carries them as pre-phrased sentences (the guide's own phrasing) the preview
	// renders as an annotation list under the kind's graphs.
	static string PreviewOf(MethodologyDefinition def) =>
		WorkflowGraphJson.SerializeMany(GraphViews(def));

	static IEnumerable<(BoardWorkflowView View, IReadOnlyList<string> EffectNotes)> GraphViews(MethodologyDefinition def) =>
		def.Kinds.Select(k => (
			new BoardWorkflowView(
				k.Kind,
				[.. k.Workflows.Select(w => new WorkflowBlock(w.Types, w.ToWorkflow(w.Types.Count > 0 ? w.Types[0] : k.Kind)))]),
			(IReadOnlyList<string>)[.. (k.Effects ?? []).Select(e => MethodologyGuide.EffectSentence(e))]));

	// The confirm/view digest: per kind, the counts plus every gated transition as one
	// compact line and every effect as the guide sentence.
	static IReadOnlyList<KindSummary> SummaryOf(MethodologyDefinition def) =>
		[.. def.Kinds.Select(k => new KindSummary(
			k.Kind,
			k.Workflows.Sum(w => w.Types.Count),
			k.Workflows.Sum(w => w.Statuses.Count),
			k.Workflows.Sum(w => w.Transitions.Count),
			[.. k.Workflows.SelectMany(w => w.Transitions).SelectMany(GateLines)],
			[.. (k.Effects ?? []).Select(e => MethodologyGuide.EffectSentence(e))]))];

	static IEnumerable<string> GateLines(MethodologyTransitionDef t)
	{
		var gates = new List<string>();
		if (t.RequiresApproval) gates.Add(t.EnforceApproval ? "approve (enforced)" : "approve");
		if (t.RequiresReason) gates.Add("reason");
		if (t.PreconditionArtifact is not null) gates.Add($"artifact:{t.PreconditionArtifact}");
		if (t.Checklist is { Count: > 0 }) gates.Add($"checklist ({t.Checklist.Count})");
		if (gates.Count > 0)
			yield return $"{t.From} → {t.To}: {string.Join(", ", gates)}";
	}
}
