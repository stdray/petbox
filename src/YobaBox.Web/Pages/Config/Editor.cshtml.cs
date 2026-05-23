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

	public void OnGet(long? id)
	{
		Id = id;
		if (id is { } bindingId)
		{
			var binding = _db.ConfigBindings.First(b => b.Id == bindingId);
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

	public async Task<IActionResult> OnPostSaveAsync(long? id, string Path, string Value, string Tags)
	{
		if (string.IsNullOrWhiteSpace(Path))
			return BadRequest("Path is required.");

		if (id is > 0)
		{
			var binding = _db.ConfigBindings.First(b => b.Id == id.Value);
			var updated = binding with { Path = Path, Value = Value, Tags = Tags, UpdatedAt = DateTime.UtcNow };
			await _db.UpdateAsync(updated);

			var saved = _db.ConfigBindings.First(b => b.Id == id.Value);
			return Partial("_Row", saved);
		}
		else
		{
			var created = new Core.Models.ConfigBinding
			{
				Path = Path,
				Value = Value,
				Tags = Tags,
				CreatedAt = DateTime.UtcNow,
				UpdatedAt = DateTime.UtcNow,
			};
			await _db.InsertWithIdentityAsync(created);

			return Partial("_Row", created);
		}
	}
}
