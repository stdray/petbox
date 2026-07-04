using LinqToDB;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Data;
using PetBox.Core.Features;
using PetBox.Core.Models;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Workflow;

namespace PetBox.Web.Pages.Admin;

// Lists and manages the named task boards of a project (create / delete / close).
// Mirrors ProjectLogs / ProjectData. Boards are also created by agents via the
// MCP tasks tools (tasks:write); this is the human-facing equivalent. All board
// operations go through ITasksService — the same door the MCP tools use — so the
// admin UI no longer skips spec validation (the divergence this refactor removes).
[Authorize(Policy = "WorkspaceAdmin")]
public sealed class ProjectTasksModel : PageModel
{
	readonly PetBoxDb _db;
	readonly FeatureFlags _features;
	readonly ITasksService _tasks;

	public ProjectTasksModel(PetBoxDb db, FeatureFlags features, ITasksService tasks)
	{
		_db = db;
		_features = features;
		_tasks = tasks;
	}

	[BindProperty(SupportsGet = true)]
	public string WorkspaceKey { get; set; } = string.Empty;

	[BindProperty(SupportsGet = true)]
	public string ProjectKey { get; set; } = string.Empty;

	public List<TaskBoardMeta> Boards { get; private set; } = [];
	public bool ProjectNotFound { get; private set; }
	public string? ErrorMessage { get; private set; }

	// The four methodology kinds are per-project singletons. The UI offers EITHER enabling
	// the whole quartet OR adding free boards — never individual methodology-kind boards.
	static readonly string[] MethodologyKinds = ["spec", "ideas", "intake", "work"];
	public bool MethodologyEnabled { get; private set; }

	// The provisioning presets offered next to the Enable button (one today: the quartet).
	// Read straight off the registry, so a new preset appears here without touching the page.
	public IReadOnlyList<MethodologyPresets.MethodologyProvisioningPreset> Presets { get; } =
		MethodologyPresets.ProvisioningPresets;

	public async Task<IActionResult> OnGetAsync(CancellationToken ct)
	{
		if (!_features.IsEnabled(Feature.Tasks))
			return NotFound();

		var project = await _db.Projects.FirstOrDefaultAsync((Project p) => p.Key == ProjectKey, ct);
		if (project is null) { ProjectNotFound = true; return Page(); }

		Boards = [.. await _tasks.ListBoardsAsync(ProjectKey, ct)];
		var openKinds = Boards.Where(b => b.ClosedAt == null).Select(b => b.Kind).ToHashSet(StringComparer.Ordinal);
		MethodologyEnabled = MethodologyKinds.All(openKinds.Contains);
		return Page();
	}

	// Add a FREE board (scratch / ad-hoc). Methodology-kind boards are not created here —
	// they come as a quartet via Enable, so the singleton is never hit by hand.
	public async Task<IActionResult> OnPostCreateAsync(string name, string? description, CancellationToken ct)
	{
		if (!_features.IsEnabled(Feature.Tasks)) return NotFound();

		try
		{
			await _tasks.CreateBoardAsync(ProjectKey, name?.Trim() ?? string.Empty, "simple", description, specBoard: null, ct);
		}
		catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
		{
			ErrorMessage = ex.Message;
			await OnGetAsync(ct);
			return Page();
		}

		return RedirectToPage();
	}

	// Opt-in: provision the chosen preset's singleton methodology boards and auto-wire
	// work->spec. Idempotent — adds only what's missing. An empty <select> value binds to null
	// (Razor gotcha), so fall back to the default preset before delegating.
	public async Task<IActionResult> OnPostEnableMethodologyAsync(string? preset, CancellationToken ct)
	{
		if (!_features.IsEnabled(Feature.Tasks)) return NotFound();

		try
		{
			await _tasks.EnableMethodologyAsync(ProjectKey,
				string.IsNullOrWhiteSpace(preset) ? MethodologyPresets.DefaultProvisioningPreset : preset, ct);
		}
		catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
		{
			ErrorMessage = ex.Message;
			await OnGetAsync(ct);
			return Page();
		}

		return RedirectToPage();
	}

	public async Task<IActionResult> OnPostDeleteAsync(string name, CancellationToken ct)
	{
		if (!_features.IsEnabled(Feature.Tasks)) return NotFound();

		await _tasks.DeleteBoardAsync(ProjectKey, name, ct);
		return RedirectToPage();
	}

	public async Task<IActionResult> OnPostCloseAsync(string name, bool closed, CancellationToken ct)
	{
		if (!_features.IsEnabled(Feature.Tasks)) return NotFound();

		await _tasks.SetClosedAsync(ProjectKey, name, closed, ct);
		return RedirectToPage();
	}
}
