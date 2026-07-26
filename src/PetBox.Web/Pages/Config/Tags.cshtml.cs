using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Config;
using PetBox.Config.Data;
using PetBox.Core.Auth;

namespace PetBox.Web.Pages.Config;

[Authorize(Policy = "WorkspaceAdmin")]
// {workspaceKey} in the route IS the target tenant, and config bindings are workspace-scoped rows
// (every binding carries a mandatory `ws:{workspaceKey}` tag). Unlike its four siblings this page has
// only ONE route template — but it declares the same value, because a page that later gains a
// project-scoped alias must not have to rediscover why.
[TenantFrom(TenantSource.Route, "workspaceKey", tenant: TenantKind.Workspace)]
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
