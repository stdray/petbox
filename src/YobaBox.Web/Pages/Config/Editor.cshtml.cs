using LinqToDB;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using YobaBox.Config.Data;

namespace YobaBox.Web.Pages.Config;

[Authorize]
public sealed class EditorModel : PageModel
{
	readonly IConfigDbFactory _configFactory;

	public EditorModel(IConfigDbFactory configFactory) => _configFactory = configFactory;

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
			var configDb = _configFactory.GetConfigDb("$system");
			var binding = configDb.Bindings.First(b => b.Id == bid);
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

		var configDb = _configFactory.GetConfigDb("$system");

		if (bindingId is > 0)
		{
			var binding = configDb.Bindings.First(b => b.Id == bindingId.Value);
			var updated = binding with { Path = Path, Value = Value, Tags = Tags, UpdatedAt = DateTime.UtcNow };
			await configDb.UpdateAsync(updated);
		}
		else
		{
#pragma warning disable CA2016
			await configDb.InsertWithIdentityAsync(new Core.Models.ConfigBinding
#pragma warning restore CA2016
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
