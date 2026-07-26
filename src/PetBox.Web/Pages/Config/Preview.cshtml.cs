using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Config;
using PetBox.Config.Data;
using PetBox.Core.Auth;

namespace PetBox.Web.Pages.Config;

[Authorize(Policy = "WorkspaceAdmin")]
// {workspaceKey}, not {projectKey} — read Config/Index.cshtml.cs before changing this. This page is
// mapped by TWO templates (Program.cs AddPageRoute), one workspace-scoped and one project-scoped, and
// a PageModel declares once for both: naming `projectKey` would resolve to nothing on the
// workspace-only template and 403 it. A project-claimed key still reaches this page, because
// ITenantAuthorizer knows a project claim authorizes its own workspace.
[TenantFrom(TenantSource.Route, "workspaceKey", tenant: TenantKind.Workspace)]
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

	// THE TENANT THE PEP JUDGED, and nothing else. Both route templates of this page carry
	// {workspaceKey}, so it is always bound — and [TenantFrom(Route, "workspaceKey", …)] on the class
	// refuses the request when it is not, which is what finally makes that guarantee enforced rather
	// than assumed.
	//
	// The old body fell back to the ActiveWorkspace CLAIM and then to a hard-coded "$system". That
	// fallback was unreachable through routing, but it was also the one way this page could read and
	// WRITE config for a workspace TenantEnforcementMiddleware never saw — the target the decision point
	// judged and the target the handler acts on have to be the same string, so the fallback is deleted
	// rather than left as a comfort. If WorkspaceKey were ever empty here, the request would already
	// have been refused above.
	string ResolveWorkspace() => WorkspaceKey!;
}
