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

	// authz-bypass-project-create: route-only bind — see Admin/Projects.cshtml.cs for why.
	[FromRoute(Name = "workspaceKey")]
	public string WorkspaceKey { get; set; } = string.Empty;

	[FromRoute(Name = "projectKey")]
	public string ProjectKey { get; set; } = string.Empty;

	public List<TaskBoardMeta> Boards { get; private set; } = [];
	public bool ProjectNotFound { get; private set; }
	public string? ErrorMessage { get; private set; }

	// Open methodology instance rules (merged) — kind badges resolve through this, so an
	// instance-declared custom kind shows its own slug instead of the Simple fallback.
	public MethodologyRuntime Runtime { get; private set; } = MethodologyRuntime.PresetsOnly;

	// First open instance by name (used when creating a free board once instances exist).
	public string? FirstOpenInstance { get; private set; }

	// The four methodology kinds are per-instance singletons. The UI offers EITHER enabling
	// a methodology preset (creates an instance) OR adding free boards.
	static readonly string[] MethodologyKinds = ["spec", "ideas", "intake", "work"];
	public bool MethodologyEnabled { get; private set; }

	// The provisioning presets offered next to the Enable button.
	// Read straight off the registry, so a new preset appears here without touching the page.
	public IReadOnlyList<MethodologyPresets.MethodologyProvisioningPreset> Presets { get; } =
		MethodologyPresets.ProvisioningPresets;

	public async Task<IActionResult> OnGetAsync(CancellationToken ct)
	{
		if (!_features.IsEnabled(Feature.Tasks))
			return NotFound();

		var project = await _db.Projects.FirstOrDefaultAsync((Project p) => p.Key == ProjectKey, ct);
		if (project is null) { ProjectNotFound = true; return Page(); }

		Runtime = await _tasks.GetRuntimeAsync(ProjectKey, ct);
		Boards = [.. await _tasks.ListBoardsAsync(ProjectKey, ct)];
		var openKinds = Boards.Where(b => b.ClosedAt == null).Select(b => b.Kind).ToHashSet(StringComparer.Ordinal);
		MethodologyEnabled = MethodologyKinds.All(openKinds.Contains);
		var open = (await _tasks.ListMethodologyInstancesAsync(ProjectKey, ct))
			.Where(i => !i.Closed)
			.OrderBy(i => i.Name, StringComparer.Ordinal)
			.ToList();
		FirstOpenInstance = open.Count > 0 ? open[0].Name : null;
		return Page();
	}

	// Add a FREE board (scratch / ad-hoc). Methodology-kind boards are not created here —
	// they come as a unit via Enable / tasks_methodology_create.
	// Once the project has any open methodology instance, the board must join one (service
	// rule); put it on the first open instance by name.
	public async Task<IActionResult> OnPostCreateAsync(string name, string? description, CancellationToken ct)
	{
		if (!_features.IsEnabled(Feature.Tasks)) return NotFound();

		try
		{
			var open = (await _tasks.ListMethodologyInstancesAsync(ProjectKey, ct))
				.Where(i => !i.Closed)
				.OrderBy(i => i.Name, StringComparer.Ordinal)
				.ToList();
			// Any instance (including closed) forces membership on board_create; when only
			// closed ones exist, reject with a clear CTA rather than inventing membership.
			var any = (await _tasks.ListMethodologyInstancesAsync(ProjectKey, ct)).Count > 0;
			string? instance = null;
			if (open.Count > 0)
				instance = open[0].Name;
			else if (any)
				throw new ArgumentException(
					"no open methodology instance — create or reopen one (Enable methodology / tasks_methodology_create) before adding a board");

			await _tasks.CreateBoardAsync(ProjectKey, name?.Trim() ?? string.Empty, "simple", description,
				specBoard: null, methodologyInstance: instance, ct);
		}
		catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
		{
			ErrorMessage = ex.Message;
			await OnGetAsync(ct);
			return Page();
		}

		this.NotifySuccess($"Board '{name?.Trim()}' created.");
		return RedirectToPage();
	}

	// Opt-in: create a named methodology instance from a builtin preset (one act: rules +
	// boards). Idempotent when the instance already exists (EnableMethodologyAsync re-provisions
	// missing kinds). An empty <select> value binds to null (Razor gotcha), so fall back to the
	// default preset before delegating.
	public async Task<IActionResult> OnPostEnableMethodologyAsync(string? preset, CancellationToken ct)
	{
		if (!_features.IsEnabled(Feature.Tasks)) return NotFound();

		try
		{
			var slug = string.IsNullOrWhiteSpace(preset) ? MethodologyPresets.DefaultProvisioningPreset : preset.Trim();
			// Prefer the explicit instance create door; fall back to EnableMethodologyAsync
			// for its idempotent re-provision of missing kinds when the instance already exists.
			var existing = await _tasks.GetMethodologyInstanceAsync(ProjectKey, slug, ct);
			if (existing is null)
			{
				try
				{
					await _tasks.CreateMethodologyInstanceAsync(ProjectKey, slug, "builtin", slug, ct);
				}
				catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
					&& ex.Message.Contains("already", StringComparison.OrdinalIgnoreCase))
				{
					// Race / already-exists: treat as enable's idempotent path.
					await _tasks.EnableMethodologyAsync(ProjectKey, slug, ct);
				}
			}
			else
			{
				await _tasks.EnableMethodologyAsync(ProjectKey, slug, ct);
			}
		}
		catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
		{
			ErrorMessage = ex.Message;
			await OnGetAsync(ct);
			return Page();
		}

		this.NotifySuccess("Methodology enabled.");
		return RedirectToPage();
	}

	public async Task<IActionResult> OnPostDeleteAsync(string name, CancellationToken ct)
	{
		if (!_features.IsEnabled(Feature.Tasks)) return NotFound();

		await _tasks.DeleteBoardAsync(ProjectKey, name, ct);
		this.NotifySuccess($"Board '{name}' deleted.");
		return RedirectToPage();
	}

	public async Task<IActionResult> OnPostCloseAsync(string name, bool closed, CancellationToken ct)
	{
		if (!_features.IsEnabled(Feature.Tasks)) return NotFound();

		await _tasks.SetClosedAsync(ProjectKey, name, closed, ct);
		this.NotifySuccess(closed ? $"Board '{name}' closed." : $"Board '{name}' reopened.");
		return RedirectToPage();
	}
}
