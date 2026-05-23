using Microsoft.AspNetCore.Mvc.RazorPages;
using YobaBox.Core.Data;

namespace YobaBox.Web.Pages.Config;

public sealed class RowModel : PageModel
{
	readonly YobaBoxDb _db;

	public RowModel(YobaBoxDb db) => _db = db;

	public Core.Models.ConfigBinding Binding { get; private set; } = new();

	public void OnGet(long id) => Binding = _db.ConfigBindings.First(b => b.Id == id);
}
