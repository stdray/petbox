using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using YobaBox.Config;
using YobaBox.Config.Data;
using YobaBox.Core.Auth;

namespace YobaBox.Web.Pages.Config;

[Authorize]
public sealed class PreviewModel : PageModel
{
	readonly IConfigDbFactory _configFactory;

	public PreviewModel(IConfigDbFactory configFactory) => _configFactory = configFactory;

	[BindProperty(SupportsGet = true)]
	public string? WorkspaceKey { get; set; }

	[BindProperty]
	public string TagsInput { get; set; } = string.Empty;

	[BindProperty]
	public string PathsInput { get; set; } = string.Empty;

	public string EffectiveWorkspaceKey { get; private set; } = "$system";
	public IReadOnlyList<(string Path, string? Value, int MatchedTags, long? BindingId)> Results { get; private set; } = [];

	public void OnGet()
	{
		EffectiveWorkspaceKey = ResolveWorkspace();
		TagsInput = $"ws:{EffectiveWorkspaceKey}";
	}

	public void OnPost()
	{
		EffectiveWorkspaceKey = ResolveWorkspace();

		var tags = TagsInput
			.Split([',', '\n', '\r', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.ToList();
		var paths = PathsInput
			.Split([',', '\n', '\r', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.ToList();

		var configDb = _configFactory.GetConfigDb(EffectiveWorkspaceKey);
		var bindings = configDb.Bindings.ToList();

		var results = new List<(string, string?, int, long?)>();
		foreach (var path in paths)
		{
			var value = ResolvePipeline.Resolve(path, tags, bindings);
			var matched = bindings
				.Where(b => string.Equals(b.Path, path, StringComparison.OrdinalIgnoreCase))
				.OrderByDescending(b => MatchCount(b.Tags, tags))
				.ThenBy(b => b.Id)
				.FirstOrDefault();
			results.Add((path, value, matched is null ? 0 : MatchCount(matched.Tags, tags), matched?.Id));
		}
		Results = results;
	}

	string ResolveWorkspace()
	{
		if (!string.IsNullOrEmpty(WorkspaceKey))
			return WorkspaceKey;
		var claimWs = User.FindFirst(YobaBoxClaims.ActiveWorkspace)?.Value;
		return string.IsNullOrEmpty(claimWs) ? "$system" : claimWs;
	}

	static int MatchCount(string bindingTags, IReadOnlyList<string> requestTags)
	{
		if (string.IsNullOrWhiteSpace(bindingTags)) return 0;
		var set = new HashSet<string>(requestTags, StringComparer.OrdinalIgnoreCase);
		var bindingSet = bindingTags
			.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		return bindingSet.Count(t => set.Contains(t));
	}
}
