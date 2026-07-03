using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Web.Search;

namespace PetBox.Web.Pages;

// Cross-scope task search results (/ui/search?q=...) — the destination of the global top-nav
// search box (_Layout). Fans the read out across every workspace/project the user can reach
// (CrossScopeTaskSearchService); this page just renders what comes back, grouped by
// workspace -> project so a result always shows WHERE the task lives.
[Authorize]
public sealed class SearchModel(CrossScopeTaskSearchService search) : PageModel
{
	// GET-bound query param; an omitted/empty `q` binds to null (empty-form-field gotcha),
	// so the empty-state check below is a null-or-empty guard, never a bare .Trim().
	[BindProperty(SupportsGet = true, Name = "q")]
	public string? Q { get; set; }

	public IReadOnlyList<CrossScopeSearchHit> Hits { get; private set; } = [];

	public async Task OnGetAsync(CancellationToken ct)
	{
		if (string.IsNullOrWhiteSpace(Q)) return;
		Hits = await search.SearchAsync(Q, ct);
	}
}
