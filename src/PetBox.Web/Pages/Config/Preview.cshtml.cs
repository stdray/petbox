using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Config;
using PetBox.Config.Data;
using PetBox.Core.Auth;

namespace PetBox.Web.Pages.Config;

[Authorize(Policy = "WorkspaceAdmin")]
public sealed class PreviewModel : PageModel
{
	readonly IConfigDirectory _config;

	public PreviewModel(IConfigDirectory config) => _config = config;

	// authz-bypass-project-create: route-only bind — see Admin/Projects.cshtml.cs for why.
	[FromRoute(Name = "workspaceKey")]
	public string? WorkspaceKey { get; set; }

	[BindProperty]
	public string TagsInput { get; set; } = string.Empty;

	[BindProperty]
	public string PathsInput { get; set; } = string.Empty;

	public string EffectiveWorkspaceKey { get; private set; } = "$system";
	public IReadOnlyList<PreviewRow> Results { get; private set; } = [];

	public sealed record PreviewRow(string Path, string? Value, int Specificity, long? BindingId, string? AmbiguityNote);

	public void OnGet()
	{
		EffectiveWorkspaceKey = ResolveWorkspace();
		TagsInput = $"ws:{EffectiveWorkspaceKey}";
	}

	public async Task OnPostAsync(CancellationToken ct)
	{
		EffectiveWorkspaceKey = ResolveWorkspace();

		var tags = (TagsInput ?? string.Empty)
			.Split([',', '\n', '\r', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.ToList();

		var wsTag = $"ws:{EffectiveWorkspaceKey}";
		if (!tags.Any(t => string.Equals(t, wsTag, StringComparison.OrdinalIgnoreCase)))
			tags.Add(wsTag);

		var paths = (PathsInput ?? string.Empty)
			.Split([',', '\n', '\r', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.ToList();

		// Deleted rows included on purpose — ResolvePipeline does its own IsDeleted filtering.
		var bindings = await _config.ListAllBindingsAsync(EffectiveWorkspaceKey, ct);

		var results = new List<PreviewRow>();
		foreach (var path in paths)
		{
			try
			{
				var match = ResolvePipeline.ResolveDetailed(path, tags, bindings);
				results.Add(match is null
					? new PreviewRow(path, null, 0, null, null)
					: new PreviewRow(path, match.Binding.Value, match.Specificity, match.Binding.Id, null));
			}
			catch (AmbiguousConfigException ex)
			{
				var note = "ambiguous: ids " + string.Join(", ", ex.CandidateBindingIds);
				results.Add(new PreviewRow(path, null, 0, null, note));
			}
		}
		Results = results;
	}

	string ResolveWorkspace()
	{
		if (!string.IsNullOrEmpty(WorkspaceKey))
			return WorkspaceKey;
		var claimWs = User.FindFirst(PetBoxClaims.ActiveWorkspace)?.Value;
		return string.IsNullOrEmpty(claimWs) ? "$system" : claimWs;
	}
}
