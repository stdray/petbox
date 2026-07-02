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

	// Usage counters per key (spec: memory-usage-observability). Viewing this page is
	// curation, not usage — it reads the counters and never increments them.
	public IReadOnlyDictionary<string, MemoryUsageView> Usage { get; private set; } =
		new Dictionary<string, MemoryUsageView>();

	// Store-wide usage aggregate (spec: memory-usage-aggregate) — rendered as a summary
	// band above the entries. Reading this page is curation, never an impression.
	public MemoryUsageAggregate? Aggregate { get; private set; }

	public async Task<IActionResult> OnGetAsync(CancellationToken ct)
	{
		if (!_features.IsEnabled(Feature.Memory)) return NotFound();
		if (!await _memory.StoreExistsAsync(ProjectKey, Store, ct)) return NotFound();

		Entries = await _memory.ListActiveEntriesAsync(ProjectKey, Store, ct);
		Usage = await _memory.GetUsageAsync(ProjectKey, Store, ct: ct);
		Aggregate = await _memory.GetUsageAggregateAsync(ProjectKey, Store, ct: ct);
		return Page();
	}
}
