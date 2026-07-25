using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Config;
using PetBox.Config.Data;
using PetBox.Core.Auth;

namespace PetBox.Web.Pages.Config;

[Authorize(Policy = "WorkspaceAdmin")]
public sealed class TagsModel : PageModel
{
	readonly IConfigDirectory _config;

	public TagsModel(IConfigDirectory config) => _config = config;

	// authz-bypass-project-create: route-only bind — see Admin/Projects.cshtml.cs for why.
	[FromRoute(Name = "workspaceKey")]
	public string? WorkspaceKey { get; set; }

	public string EffectiveWorkspaceKey { get; private set; } = "$system";
	public IReadOnlyList<TagVocabularyEntry> Declared { get; private set; } = [];
	public IReadOnlyDictionary<string, IReadOnlyList<string>> UsedKeyValues { get; private set; } =
		new Dictionary<string, IReadOnlyList<string>>();
	public string? ErrorMessage { get; set; }

	public async Task OnGetAsync(CancellationToken ct)
	{
		EffectiveWorkspaceKey = ResolveWorkspace();
		await LoadAsync(ct);
	}

	public async Task<IActionResult> OnPostDeclareAsync(string TagKey, string? Description, CancellationToken ct)
	{
		EffectiveWorkspaceKey = ResolveWorkspace();

		if (string.IsNullOrWhiteSpace(TagKey))
		{
			ErrorMessage = "Tag key is required.";
			await LoadAsync(ct);
			return Page();
		}

		await _config.DeclareTagAsync(EffectiveWorkspaceKey, TagKey, Description, ct);

		return RedirectToPage(new { workspaceKey = EffectiveWorkspaceKey });
	}

	public async Task<IActionResult> OnPostRetireAsync(long id, CancellationToken ct)
	{
		EffectiveWorkspaceKey = ResolveWorkspace();
		await _config.RetireTagAsync(EffectiveWorkspaceKey, id, ct);
		return RedirectToPage(new { workspaceKey = EffectiveWorkspaceKey });
	}

	async Task LoadAsync(CancellationToken ct)
	{
		Declared = await _config.ListTagsAsync(EffectiveWorkspaceKey, ct);

		var bindings = await _config.ListAllBindingsAsync(EffectiveWorkspaceKey, ct);
		UsedKeyValues = AggregateUsedValues(bindings.Select(b => b.Tags));
	}

	// Aggregates the distinct values seen per tag namespace across all binding tag strings.
	// Binding tags are canonical "namespace:value" tokens (matching Config/Index.ParseTags),
	// split on the first ':'. Bare-namespace tokens with no ':' carry no value and are skipped.
	public static IReadOnlyDictionary<string, IReadOnlyList<string>> AggregateUsedValues(
		IEnumerable<string?> bindingTags)
	{
		var used = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
		foreach (var tags in bindingTags)
		{
			if (string.IsNullOrWhiteSpace(tags)) continue;
			foreach (var part in tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
			{
				var sep = part.IndexOf(':');
				if (sep <= 0) continue;
				var key = part[..sep].Trim();
				var value = part[(sep + 1)..].Trim();
				if (!used.TryGetValue(key, out var set))
				{
					set = new SortedSet<string>(StringComparer.Ordinal);
					used[key] = set;
				}
				set.Add(value);
			}
		}
		return used.ToDictionary(
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
