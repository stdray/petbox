using LinqToDB;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Features;
using PetBox.Memory.Data;

namespace PetBox.Web.Pages.ProjectHome;

// Read-only detail for one memory store (/ui/{ws}/{project}/memory/{store}). Shows
// the currently-active entries (ActiveTo == null) ordered by Key. Existence is
// checked against metadata first so we don't auto-vivify a phantom file.
[Authorize]
public sealed class MemoryStoreModel : PageModel
{
	readonly FeatureFlags _features;
	readonly IMemoryStore _store;

	public MemoryStoreModel(FeatureFlags features, IMemoryStore store)
	{
		_features = features;
		_store = store;
	}

	[BindProperty(SupportsGet = true, Name = "workspaceKey")]
	public string WorkspaceKey { get; set; } = string.Empty;

	[BindProperty(SupportsGet = true, Name = "projectKey")]
	public string ProjectKey { get; set; } = string.Empty;

	[BindProperty(SupportsGet = true, Name = "store")]
	public string Store { get; set; } = string.Empty;

	public IReadOnlyList<MemoryEntry> Entries { get; private set; } = [];

	public async Task<IActionResult> OnGetAsync(CancellationToken ct)
	{
		if (!_features.IsEnabled(Feature.Memory)) return NotFound();
		if (!await _store.ExistsAsync(ProjectKey, Store, ct)) return NotFound();

		var ctx = _store.GetContext(ProjectKey, Store);
		Entries = await ctx.Entries
			.Where(e => e.ActiveTo == null)
			.OrderBy(e => e.Key)
			.ToListAsync(ct);
		return Page();
	}
}
