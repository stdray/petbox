using LinqToDB;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using YobaBox.Core.Data;

namespace YobaBox.Web.Pages.Config;

[Authorize]
public sealed class EditorModel : PageModel
{
	readonly YobaBoxDb _db;

	public EditorModel(YobaBoxDb db) => _db = db;

	public Core.Models.ConfigBinding Binding { get; private set; } = new();

	public void OnGet(long id) => Binding = _db.ConfigBindings.First(b => b.Id == id);

	public async Task<IActionResult> OnPostSaveAsync(long id, string Value, string Tags)
	{
		var binding = _db.ConfigBindings.First(b => b.Id == id);
		var updated = binding with { Value = Value, Tags = Tags, UpdatedAt = DateTime.UtcNow };
		await _db.UpdateAsync(updated);

		Binding = _db.ConfigBindings.First(b => b.Id == id);
		return Partial("_Row", Binding);
	}
}
