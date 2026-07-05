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

	// The paging arg is 'pageNum', not 'page' — 'page' is a reserved route-key in Razor
	// Pages, so a ?page=N value never binds (see the Data-module table view lesson).
	[BindProperty(SupportsGet = true, Name = "pageNum")]
	public int PageNum { get; set; }

	[BindProperty(SupportsGet = true, Name = "q")]
	public string? Query { get; set; }

	const int PageSize = 40;

	public IReadOnlyList<MemoryEntry> Entries { get; private set; } = [];
	public int Total { get; private set; }
	public bool HasNext { get; private set; }

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

		if (PageNum < 0) PageNum = 0;
		var page = await _memory.ListActiveEntriesPageAsync(ProjectKey, Store, Query, PageNum, PageSize, ct);
		Entries = page.Entries;
		HasNext = page.HasNext;
		Total = page.Total;
		// Only load the usage counters for the keys actually rendered on this page.
		var keys = Entries.Select(e => e.Key).ToList();
		Usage = keys.Count == 0
			? new Dictionary<string, MemoryUsageView>()
			: await _memory.GetUsageAsync(ProjectKey, Store, keys, ct);
		Aggregate = await _memory.GetUsageAggregateAsync(ProjectKey, Store, ct: ct);
		return Page();
	}
}
