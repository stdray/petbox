using LinqToDB;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Config.Data;
using PetBox.Core.Auth;

namespace PetBox.Web.Pages.Config;

[Authorize]
public sealed class TagsModel : PageModel
{
	readonly IConfigDbFactory _configFactory;

	public TagsModel(IConfigDbFactory configFactory) => _configFactory = configFactory;

	[BindProperty(SupportsGet = true)]
	public string? WorkspaceKey { get; set; }

	public string EffectiveWorkspaceKey { get; private set; } = "$system";
	public IReadOnlyList<TagVocabularyEntry> Declared { get; private set; } = [];
	public IReadOnlyDictionary<string, IReadOnlyList<string>> UsedKeyValues { get; private set; } =
		new Dictionary<string, IReadOnlyList<string>>();
	public string? ErrorMessage { get; set; }

	public void OnGet()
	{
		EffectiveWorkspaceKey = ResolveWorkspace();
		Load();
	}

	public async Task<IActionResult> OnPostDeclareAsync(string TagKey, string? Description)
	{
		EffectiveWorkspaceKey = ResolveWorkspace();

		if (string.IsNullOrWhiteSpace(TagKey))
		{
			ErrorMessage = "Tag key is required.";
			Load();
			return Page();
		}

		var configDb = _configFactory.GetConfigDb(EffectiveWorkspaceKey);
		var exists = configDb.Tags.Any(t => t.TagKey == TagKey);
		if (!exists)
		{
			await configDb.InsertAsync(new TagVocabularyEntry
			{
				TagKey = TagKey.Trim(),
				Description = Description?.Trim(),
				CreatedAt = DateTime.UtcNow,
			});
		}

		return RedirectToPage(new { workspaceKey = EffectiveWorkspaceKey });
	}

	public async Task<IActionResult> OnPostRetireAsync(long id)
	{
		EffectiveWorkspaceKey = ResolveWorkspace();
		var configDb = _configFactory.GetConfigDb(EffectiveWorkspaceKey);
		await configDb.Tags.Where(t => t.Id == id).DeleteAsync();
		return RedirectToPage(new { workspaceKey = EffectiveWorkspaceKey });
	}

	void Load()
	{
		var configDb = _configFactory.GetConfigDb(EffectiveWorkspaceKey);
		Declared = configDb.Tags.OrderBy(t => t.TagKey).ToList();

		var bindings = configDb.Bindings.ToList();
		var used = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
		foreach (var b in bindings)
		{
			foreach (var pair in b.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
			{
				var eq = pair.IndexOf('=');
				if (eq <= 0) continue;
				var key = pair[..eq].Trim();
				var value = pair[(eq + 1)..].Trim();
				if (!used.TryGetValue(key, out var set))
				{
					set = new SortedSet<string>(StringComparer.Ordinal);
					used[key] = set;
				}
				set.Add(value);
			}
		}
		UsedKeyValues = used.ToDictionary(
			kv => kv.Key,
			kv => (IReadOnlyList<string>)[.. kv.Value]);
	}

	string ResolveWorkspace()
	{
		if (!string.IsNullOrEmpty(WorkspaceKey))
			return WorkspaceKey;
		var claimWs = User.FindFirst(PetBoxClaims.ActiveWorkspace)?.Value;
		return string.IsNullOrEmpty(claimWs) ? "$system" : claimWs;
	}
}
