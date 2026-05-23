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

	public long? Id { get; private set; }
	public string Path { get; private set; } = string.Empty;
	public string Value { get; private set; } = string.Empty;
	public string Tags { get; private set; } = string.Empty;
	public bool IsNew { get; private set; }

	public void OnGet(long? bindingId)
	{
		Id = bindingId;
		if (bindingId is { } bid)
		{
			var binding = _db.ConfigBindings.First(b => b.Id == bid);
			Path = binding.Path;
			Value = binding.Value;
			Tags = binding.Tags;
			IsNew = false;
		}
		else
		{
			IsNew = true;
		}
	}

	public async Task<IActionResult> OnPostSaveAsync(long? bindingId, string Path, string Value, string Tags)
	{
		if (string.IsNullOrWhiteSpace(Path))
		{
			ModelState.AddModelError("Path", "Path is required.");
			Id = bindingId;
			IsNew = bindingId is null or <= 0;
			return Page();
		}

		if (bindingId is > 0)
		{
			await _db.ConfigBindings
				.Where(b => b.Id == bindingId.Value)
				.Set(b => b.Path, Path)
				.Set(b => b.Value, Value)
				.Set(b => b.Tags, Tags)
				.Set(b => b.UpdatedAt, DateTime.UtcNow)
				.UpdateAsync();
		}
		else
		{
			await _db.InsertWithIdentityAsync(new Core.Models.ConfigBinding
			{
				Path = Path,
				Value = Value,
				Tags = Tags,
				CreatedAt = DateTime.UtcNow,
				UpdatedAt = DateTime.UtcNow,
			});
		}

		return RedirectToPage("/Config/Index");
	}
}
