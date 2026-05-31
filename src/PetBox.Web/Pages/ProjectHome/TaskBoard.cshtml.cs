using System.Text.RegularExpressions;
using LinqToDB;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Data.Temporal;
using PetBox.Core.Features;
using PetBox.Tasks.Data;

namespace PetBox.Web.Pages.ProjectHome;

// Read-only detail for one task board (/ui/{ws}/{project}/tasks/{board}). Shows
// the currently-active plan nodes (ActiveTo == null) ordered by sparse Priority
// then Key. Existence is checked against metadata first so we don't auto-vivify
// a phantom file for an unknown board name.
[Authorize]
public sealed class TaskBoardModel : PageModel
{
	readonly FeatureFlags _features;
	readonly ITaskBoardStore _store;

	public TaskBoardModel(FeatureFlags features, ITaskBoardStore store)
	{
		_features = features;
		_store = store;
	}

	[BindProperty(SupportsGet = true, Name = "workspaceKey")]
	public string WorkspaceKey { get; set; } = string.Empty;

	[BindProperty(SupportsGet = true, Name = "projectKey")]
	public string ProjectKey { get; set; } = string.Empty;

	[BindProperty(SupportsGet = true, Name = "board")]
	public string Board { get; set; } = string.Empty;

	public IReadOnlyList<PlanNode> Nodes { get; private set; } = [];

	public async Task<IActionResult> OnGetAsync(CancellationToken ct)
	{
		if (!_features.IsEnabled(Feature.Tasks)) return NotFound();
		if (!await _store.ExistsAsync(ProjectKey, Board, ct)) return NotFound();

		var ctx = _store.GetContext(ProjectKey, Board);
		Nodes = await ctx.PlanNodes
			.Where(n => n.ActiveTo == null)
			.OrderBy(n => n.Priority)
			.ThenBy(n => n.Key)
			.ToListAsync(ct);
		return Page();
	}

	// Quick-add from the board UI: drops a new task into the `incoming` phase with an
	// auto-generated key (slug of the name + short unique suffix).
	public async Task<IActionResult> OnPostCreateAsync(string name, long priority, CancellationToken ct)
	{
		if (!_features.IsEnabled(Feature.Tasks)) return NotFound();
		if (!await _store.ExistsAsync(ProjectKey, Board, ct)) return NotFound();

		if (!string.IsNullOrWhiteSpace(name))
		{
			var key = new TaskNodeId("incoming", GenKey(name), null).ToKey();
			var ctx = _store.GetContext(ProjectKey, Board);
			await TemporalStore.UpsertAsync(ctx, new[]
			{
				new PlanNode { Key = key, Version = 0, Status = PlanStatus.Pending, Name = name.Trim(), Body = string.Empty, Priority = priority },
			}, ct: ct);
		}

		return RedirectToPage(new { workspaceKey = WorkspaceKey, projectKey = ProjectKey, board = Board });
	}

	static string GenKey(string name)
	{
		var ascii = Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
		if (ascii.Length == 0 || !char.IsLetter(ascii[0])) ascii = "task-" + ascii;
		ascii = ascii.Trim('-');
		if (ascii.Length > 32) ascii = ascii[..32].Trim('-'); // keep keys short/readable
		return $"{ascii}-{Guid.NewGuid():N}"[..(Math.Min(ascii.Length, 32) + 7)];
	}
}
