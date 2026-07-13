using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Features;
using PetBox.Core.Models;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Workflow;
using PetBox.Web.Auth;
using PetBox.Web.Mcp;
using PetBox.Web.Pages.ProjectHome;

namespace PetBox.Web.Pages.Admin;

// Create / view / edit live methodology INSTANCE rules — the human-facing editor for
// process rules (kinds/statuses/gates). Shares the wire shape (MethodologyWire) with
// tasks_methodology_template_* / rules_* so a document moves freely between this page and
// MCP. Truth is open methodology instance rules (not the legacy methodology_defs singleton).
//
// The page is a small state machine (Mode), NOT a SPA — plain Razor handlers + a `step`
// query param for deep links:
//   - open instance rules → VIEW mode (summary + preview; explicit Edit), ?step=edit opens
//     the editor prefilled; pick instance via ?instance=<name> (default: first open by name);
//   - no open instance → a "Create methodology" call-to-action, ?step=base the base picker
//     (builtin presets + templates / open instance rules from other projects), then the
//     editor, then a confirm summary → save creates an instance from a template;
//   - POST-rendered states (template loaded, preview, confirm, rejected save) set Mode
//     directly.
// All writes go through ITasksService (full validation + live-node compatibility,
// optimistic concurrency on `version`); rejections render verbatim in the errors block
// with the user's JSON preserved (never a silent overwrite).
[Authorize(Policy = "WorkspaceAdmin")]
public sealed class ProjectMethodologyModel : PageModel
{
	public enum EditorMode { View, Cta, Base, Edit, Confirm }

	readonly IProjectDirectory _projects;
	readonly FeatureFlags _features;
	readonly ITasksService _tasks;

	public ProjectMethodologyModel(IProjectDirectory projects, FeatureFlags features, ITasksService tasks)
	{
		_projects = projects;
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

	// Set by the post-delete redirect (?deleted=True) — kept for URL compatibility; delete
	// of rules is rejected (close the instance instead).
	[BindProperty(SupportsGet = true)]
	public bool Deleted { get; set; }

	// The wizard step a GET deep-links into: "base" (choose a base) or "edit" (the editor);
	// anything else falls back to the state's default (view mode / the create CTA).
	[BindProperty(SupportsGet = true)]
	public string? Step { get; set; }

	// Selected methodology instance name (?instance=); default = first open by name.
	[BindProperty(SupportsGet = true)]
	public string? Instance { get; set; }

	// What the page renders — derived from the stored state + Step on GET, set directly by
	// the POST handlers.
	public EditorMode Mode { get; private set; } = EditorMode.Edit;

	public bool ProjectNotFound { get; private set; }
	public string? ErrorMessage { get; private set; }

	// The selected open instance's rules + revision metadata; null = no open instance.
	public MethodologyInstanceRulesView? Stored { get; private set; }

	// Open instances for the instance switcher (name + definition display name).
	public IReadOnlyList<MethodologyInstanceView> OpenInstances { get; private set; } = [];

	// The project's ACTIVE boards — surfaced in the instance-less state so boards still
	// show where process comes from.
	public IReadOnlyList<TaskBoardMeta> ActiveBoards { get; private set; } = [];

	// The preset the "Load preset as template" control last loaded — echoed back so the
	// select keeps the user's choice instead of snapping to the first option.
	public string? SelectedPreset { get; private set; }

	// Textarea contents: the stored rules rendered as the template document (prefill), a
	// preset template, or the user's own JSON echoed back after a rejected save/preview.
	public string DefinitionJson { get; private set; } = string.Empty;
	public string MigrationJson { get; private set; } = string.Empty;

	// Optimistic-concurrency baseline for the next save (0 = no rules yet / create path).
	public long Version { get; private set; }

	// JSON island for the SVG preview (ts/methodology-preview.ts → renderWorkflow): an array
	// of {kind, blocks, effectNotes} docs, one per definition kind. Empty = nothing to preview.
	public string PreviewJson { get; private set; } = string.Empty;

	// ── wizard state ──────────────────────────────────────────────────────────

	// One base the creation wizard offers: a builtin provisioning preset (`preset:<slug>`),
	// another project's stored template (`template:<project>:<key>`), or another project's
	// open instance rules (`instance:<project>:<name>`).
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
			// Existing open instance: view mode by default; ?step=edit opens the editor.
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

	// Wizard step 1 → 2: resolve the chosen base (builtin preset, template, or instance rules)
	// and open the editor prefilled with it.
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

	// Fill the textarea with a builtin template rendered as a definition document — the same
	// document tasks_methodology_template_get returns for key=quartet|classic|simple — and preview it.
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
	public async Task<IActionResult> OnPostPreviewAsync(string? definitionJson, string? migrationJson, long version, string? instance, CancellationToken ct)
	{
		if (!_features.IsEnabled(Feature.Tasks)) return NotFound();
		if (!await LoadStateAsync(ct)) return Page();
		KeepInput(definitionJson, migrationJson, version, instance);
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
	public async Task<IActionResult> OnPostConfirmAsync(string? definitionJson, string? migrationJson, long version, string? instance, CancellationToken ct)
	{
		if (!_features.IsEnabled(Feature.Tasks)) return NotFound();
		if (!await LoadStateAsync(ct)) return Page();
		KeepInput(definitionJson, migrationJson, version, instance);

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

	// Install/update instance rules via the service door. When an open instance is selected,
	// rules_upsert; when none, create an instance from a snapshot template of the document.
	// Success redirects (fresh state + saved alert); any rejection rerenders with the service's
	// message and the user's JSON intact.
	public async Task<IActionResult> OnPostSaveAsync(string? definitionJson, string? migrationJson, long version, string? instance, CancellationToken ct)
	{
		if (!_features.IsEnabled(Feature.Tasks)) return NotFound();

		string? savedInstance;
		try
		{
			var def = MethodologyWire.ParseDocument(definitionJson);
			var migration = MethodologyWire.ParseMigrationDocument(migrationJson);
			var instanceName = string.IsNullOrWhiteSpace(instance)
				? null
				: instance.Trim().ToLowerInvariant();

			// Prefer the explicitly posted instance, else the loaded selection.
			if (instanceName is null)
			{
				var open = (await _tasks.ListMethodologyInstancesAsync(ProjectKey, ct))
					.Where(i => !i.Closed)
					.OrderBy(i => i.Name, StringComparer.Ordinal)
					.ToList();
				if (open.Count > 0)
					instanceName = open[0].Name;
			}

			if (instanceName is not null
				&& await _tasks.GetMethodologyInstanceAsync(ProjectKey, instanceName, ct) is { Closed: false })
			{
				await _tasks.DefineMethodologyInstanceRulesAsync(ProjectKey, instanceName, def, version, migration, ct);
				savedInstance = instanceName;
			}
			else
			{
				// Create path: snapshot the document as a template, then create an instance.
				var name = InstanceSlug(def.Name);
				var tmplKey = $"editor-{name}";
				var existingTmpl = await _tasks.GetMethodologyTemplateAsync(ProjectKey, tmplKey, ct);
				// Builtin dual-read returns version 0 — only use stored/definition baseline.
				var tmplVersion = existingTmpl is { Source: "stored" } ? existingTmpl.Version : 0;
				await _tasks.UpsertMethodologyTemplateAsync(ProjectKey, tmplKey, def, tmplVersion, ct);
				if (await _tasks.GetMethodologyInstanceAsync(ProjectKey, name, ct) is not null)
					throw new InvalidOperationException(
						$"methodology instance '{name}' already exists — open it with ?instance={name} and edit its rules, or close it first");
				await _tasks.CreateMethodologyInstanceAsync(ProjectKey, name, "template", tmplKey, ct);
				savedInstance = name;
			}
		}
		catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
		{
			if (!await LoadStateAsync(ct)) return Page();
			KeepInput(definitionJson, migrationJson, version, instance);
			Mode = EditorMode.Edit;
			ErrorMessage = ex.Message;
			return Page();
		}

		return RedirectToPage(new { WorkspaceKey, ProjectKey, Saved = true, Instance = savedInstance });
	}

	// Rules are not deleted independently of the instance. Reject with a clear close CTA.
	public async Task<IActionResult> OnPostDeleteAsync(long version, string? instance, CancellationToken ct)
	{
		if (!_features.IsEnabled(Feature.Tasks)) return NotFound();
		if (!await LoadStateAsync(ct)) return Page();

		Mode = Stored is not null ? EditorMode.View : EditorMode.Cta;
		PrefillStored();
		if (Stored is not null) Summary = SummaryOf(Stored.Definition);
		ErrorMessage =
			"Close the methodology instance (tasks_methodology_close / admin boards) rather than deleting rules. "
			+ "Instance history keeps the last rules for read; new work requires a new or reopened instance.";
		return Page();
	}

	async Task<bool> LoadStateAsync(CancellationToken ct)
	{
		var project = await _projects.GetAsync(ProjectKey, ct);
		if (project is null) { ProjectNotFound = true; return false; }

		OpenInstances = (await _tasks.ListMethodologyInstancesAsync(ProjectKey, ct))
			.Where(i => !i.Closed)
			.OrderBy(i => i.Name, StringComparer.Ordinal)
			.ToList();

		var pick = string.IsNullOrWhiteSpace(Instance)
			? (OpenInstances.Count > 0 ? OpenInstances[0].Name : null)
			: Instance.Trim().ToLowerInvariant();
		if (pick is not null)
		{
			// If the named instance is closed/missing, fall back to first open.
			Stored = await _tasks.GetMethodologyInstanceRulesAsync(ProjectKey, pick, ct);
			if (Stored is { Closed: true })
				Stored = null;
			if (Stored is null && OpenInstances.Count > 0 && !string.Equals(pick, OpenInstances[0].Name, StringComparison.OrdinalIgnoreCase))
			{
				pick = OpenInstances[0].Name;
				Stored = await _tasks.GetMethodologyInstanceRulesAsync(ProjectKey, pick, ct);
			}
			if (Stored is not null)
				Instance = Stored.Name;
		}

		Version = Stored?.Version ?? 0;
		ActiveBoards = (await _tasks.ListBoardsAsync(ProjectKey, ct)).Where(b => b.ClosedAt == null).ToList();
		return true;
	}

	// The stored instance rules rendered into the editor (document prefill + preview).
	void PrefillStored()
	{
		if (Stored is null) return;
		DefinitionJson = MethodologyWire.ToJson(
			MethodologyWire.ProjectDefinition(Stored.Definition, Stored.Version, Stored.Created, Stored.Updated));
		PreviewJson = PreviewOf(Stored.Definition);
	}

	// Echo the user's input back after a rejected save / a preview (the posted version wins
	// over the freshly-loaded one — the user decides how to resolve a conflict).
	void KeepInput(string? definitionJson, string? migrationJson, long version, string? instance)
	{
		DefinitionJson = definitionJson ?? string.Empty;
		MigrationJson = migrationJson ?? string.Empty;
		Version = version;
		if (!string.IsNullOrWhiteSpace(instance))
			Instance = instance.Trim().ToLowerInvariant();
	}

	// The base picker's options: every builtin provisioning preset, then other projects'
	// stored templates and open instance rules — each with graph docs for the per-card SVG preview.
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

		// Every project across every workspace (the base picker's whole point — another project's
		// template/instance may live in a different workspace than this one). Workspace memory
		// containers never hold methodology templates/instances, so the directory's safe default
		// (containers excluded) changes nothing observable here.
		var projects = (await _projects.ListAllAsync(ct: ct)).Where(p => p.Key != ProjectKey);
		foreach (var p in projects)
		{
			var templates = await _tasks.ListMethodologyTemplatesAsync(p.Key, ct);
			foreach (var t in templates.Where(t => t.Source == "stored"))
			{
				var view = await _tasks.GetMethodologyTemplateAsync(p.Key, t.Key, ct);
				if (view is null) continue;
				var slug = $"template:{p.Key}:{t.Key}";
				options.Add(new(slug,
					$"{view.Definition.Name} — template '{t.Key}' of project {p.Key}",
					$"Stored template (version {view.Version}) — kinds: {string.Join(", ", view.Definition.Kinds.Select(k => k.Kind))}."));
				previews.Add((slug, GraphViews(view.Definition)));
			}

			var open = (await _tasks.ListMethodologyInstancesAsync(p.Key, ct)).Where(i => !i.Closed);
			foreach (var inst in open)
			{
				var rules = await _tasks.GetMethodologyInstanceRulesAsync(p.Key, inst.Name, ct);
				if (rules is null) continue;
				var slug = $"instance:{p.Key}:{inst.Name}";
				options.Add(new(slug,
					$"{rules.Definition.Name} — instance '{inst.Name}' of project {p.Key}",
					$"Open instance rules (version {rules.Version}) — kinds: {string.Join(", ", rules.Definition.Kinds.Select(k => k.Kind))}."));
				previews.Add((slug, GraphViews(rules.Definition)));
			}
		}

		Bases = options;
		BasePreviewsJson = WorkflowGraphJson.SerializeBases(previews);
	}

	// Resolve a base picker choice.
	async Task<MethodologyDefinition> ResolveBaseAsync(string? baseRef, CancellationToken ct)
	{
		var slug = (baseRef ?? string.Empty).Trim();
		if (slug.StartsWith("preset:", StringComparison.Ordinal))
			return MethodologyPresets.RenderPresetDefinition(slug["preset:".Length..]);
		if (slug.StartsWith("template:", StringComparison.Ordinal))
		{
			var rest = slug["template:".Length..];
			var sep = rest.IndexOf(':');
			if (sep <= 0) throw new ArgumentException("template base ref must be template:<project>:<key>");
			var projectKey = rest[..sep];
			var key = rest[(sep + 1)..];
			var view = await _tasks.GetMethodologyTemplateAsync(projectKey, key, ct);
			return view?.Definition
				?? throw new ArgumentException($"project '{projectKey}' has no methodology template '{key}'");
		}
		if (slug.StartsWith("instance:", StringComparison.Ordinal))
		{
			var rest = slug["instance:".Length..];
			var sep = rest.IndexOf(':');
			if (sep <= 0) throw new ArgumentException("instance base ref must be instance:<project>:<name>");
			var projectKey = rest[..sep];
			var name = rest[(sep + 1)..];
			var rules = await _tasks.GetMethodologyInstanceRulesAsync(projectKey, name, ct);
			return rules?.Definition
				?? throw new ArgumentException($"project '{projectKey}' has no methodology instance '{name}'");
		}
		// Legacy def: refs still accepted by resolving open instance / template of that project.
		if (slug.StartsWith("def:", StringComparison.Ordinal))
		{
			var projectKey = slug["def:".Length..];
			var open = (await _tasks.ListMethodologyInstancesAsync(projectKey, ct))
				.Where(i => !i.Closed).OrderBy(i => i.Name, StringComparer.Ordinal).FirstOrDefault();
			if (open is not null)
			{
				var rules = await _tasks.GetMethodologyInstanceRulesAsync(projectKey, open.Name, ct);
				if (rules is not null) return rules.Definition;
			}
			throw new ArgumentException($"project '{projectKey}' has no open methodology instance to copy");
		}
		throw new ArgumentException("pick a base to start from (a builtin preset, template, or open instance)");
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

	// Instance name from a definition document name (slug rules match methodology instances).
	static string InstanceSlug(string name)
	{
		var s = Regex.Replace((name ?? string.Empty).Trim().ToLowerInvariant(), @"[^a-z0-9_-]+", "-").Trim('-');
		if (s.Length == 0 || !char.IsLetter(s[0])) s = "main";
		if (s.Length > 100) s = s[..100].Trim('-');
		return s;
	}
}
