using LinqToDB;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Data;
using PetBox.Core.Features;
using PetBox.Memory.Contract;
using PetBox.Memory.Data;
using PetBox.Web.Memory;

namespace PetBox.Web.Pages.ProjectHome;

// Read-only detail for one memory store (/ui/{ws}/{project}/memory/{store}). Shows
// the currently-active entries (ActiveTo == null) ordered by Key. Existence is
// checked against metadata first so we don't auto-vivify a phantom file.
// WorkspaceViewer + project↔route workspace bind — same tenant gate as Memory page.
[Authorize(Policy = "WorkspaceViewer")]
public sealed class MemoryStoreModel : PageModel
{
	readonly ICoreDbFactory _f;
	readonly FeatureFlags _features;
	readonly IMemoryService _memory;

	public MemoryStoreModel(ICoreDbFactory f, FeatureFlags features, IMemoryService memory)
	{
		_f = f;
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

	// The deep-link half of the stable entry URL (…/memory/{store}?key={key}#{key}, MemoryLinks):
	// the SERVER resolves which page holds the key and renders THAT page, so the fragment has a card
	// to land on. A bare fragment cannot: it is never sent to the server, so the entry was silently
	// absent from the DOM for every store bigger than one page.
	[BindProperty(SupportsGet = true, Name = MemoryLinks.KeyParam)]
	public string? Key { get; set; }

	// The key the request asked for AND that this page actually renders — the card is marked
	// `data-highlight="true"` so the highlight does not hang on `:target` alone.
	public string? HighlightKey { get; private set; }

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
		using var db = _f.Open();
		if (!_features.IsEnabled(Feature.Memory)) return NotFound();
		var project = await db.Projects.FirstOrDefaultAsync(p => p.Key == ProjectKey, ct);
		if (project is null || !string.Equals(project.WorkspaceKey, WorkspaceKey, StringComparison.Ordinal))
			return NotFound();
		if (!await _memory.StoreExistsAsync(ProjectKey, Store, ct)) return NotFound();

		if (PageNum < 0) PageNum = 0;

		// A `?key=` deep-link OWNS the page number: the entry's page is computed from its rank in the
		// listing order, so the link keeps working as the store grows (and an explicit ?pageNum is
		// overridden — the key is the more specific ask). Resolution runs against the UNFILTERED
		// listing, so a `?q=` narrowing is dropped for the deep-link; a key that no longer resolves
		// (deleted entry, typo) leaves the page as-is and simply highlights nothing.
		if (!string.IsNullOrWhiteSpace(Key))
		{
			Query = null;
			var found = await _memory.FindActiveEntryPageAsync(ProjectKey, Store, Key, PageSize, ct);
			if (found is { } p)
			{
				PageNum = p;
				HighlightKey = Key;
			}
		}

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
