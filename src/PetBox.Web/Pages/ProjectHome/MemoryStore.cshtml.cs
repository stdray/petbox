using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Features;
using PetBox.Memory.Contract;
using PetBox.Memory.Data;

namespace PetBox.Web.Pages.ProjectHome;

// Read-only detail for one memory store (/ui/{ws}/{project}/memory/{store}). Shows
// the currently-active entries (ActiveTo == null) ordered by Key. Existence is
// checked against metadata first so we don't auto-vivify a phantom file.
[Authorize]
public sealed class MemoryStoreModel : PageModel
{
	readonly FeatureFlags _features;
	readonly IMemoryService _memory;

	public MemoryStoreModel(FeatureFlags features, IMemoryService memory)
	{
		_features = features;
		_memory = memory;
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
		if (!await _memory.StoreExistsAsync(ProjectKey, Store, ct)) return NotFound();

		Entries = await _memory.ListActiveEntriesAsync(ProjectKey, Store, ct);
		return Page();
	}
}
