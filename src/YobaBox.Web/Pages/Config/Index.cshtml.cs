using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using YobaBox.Core.Data;

namespace YobaBox.Web.Pages.Config;

public sealed class IndexModel : PageModel
{
	readonly YobaBoxDb _db;

	public IndexModel(YobaBoxDb db) => _db = db;

	public IReadOnlyList<Core.Models.ConfigBinding> Bindings { get; private set; } = [];

	public void OnGet() => Bindings = _db.ConfigBindings.OrderBy(b => b.Path).ToList();
}
