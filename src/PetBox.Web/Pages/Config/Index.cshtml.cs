using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Caching.Memory;
using PetBox.Config;
using PetBox.Core.Auth;
using PetBox.Core.Models;

namespace PetBox.Web.Pages.Config;

[Authorize(Policy = "WorkspaceAdmin")]
// THE TRAP, and why the declaration names `workspaceKey` on a page that also binds a `projectKey`.
//
// This page is mapped by TWO route templates (Program.cs AddPageRoute):
//
//     /ui/{workspaceKey}/config                  — workspace-scoped
//     /ui/{workspaceKey}/{projectKey}/config     — project-scoped, same page
//
// A PageModel declares ONCE for every route of its page. `[TenantFrom(Route, "projectKey")]` would
// therefore resolve to NOTHING on the first template — and an unresolved TenantRef is a refusal, for
// every principal including a wildcard key. The workspace-level config pages would have started
// answering 403 with a green ratchet above them, which is silent in production and loud nowhere.
//
// So the declaration names the value BOTH templates carry. Nothing is lost by it: a caller holding only
// a project claim still reaches this page, because ITenantAuthorizer applies "a project claim authorizes
// the workspace that project belongs to" centrally (TenantAuthorizer.KeyOnWorkspaceAsync) — the same rule
// ConfigApi.AuthorizeWorkspaceAsync used to spell out by hand before the REST wave deleted it. And
// nothing is loosened: config bindings ARE workspace-scoped rows (every binding must carry a
// `ws:{workspaceKey}` tag — Editor enforces it), so the workspace IS the tenant of this data; the
// {projectKey} on the second template is a display FILTER over tags, not a second boundary.
//
// AuthzDeclarationRatchetTests.APageDeclaringARouteTenant_NamesARouteValueAllOfItsRoutesCarry is the
// tripwire for this whole class of mistake and stops being vacuous with this commit.
[TenantFrom(TenantSource.Route, "workspaceKey", tenant: TenantKind.Workspace)]
public sealed class IndexModel : PageModel
{
	readonly IConfigDirectory _config;
	readonly ISecretEncryptor _encryptor;
	readonly Microsoft.Extensions.Caching.Memory.IMemoryCache _cache;

	public IndexModel(
		IConfigDirectory config,
		ISecretEncryptor encryptor,
		Microsoft.Extensions.Caching.Memory.IMemoryCache cache)
	{
		_config = config;
		_encryptor = encryptor;
		_cache = cache;
	}

	public IReadOnlyList<SavedConfigFilter> SavedFilters { get; private set; } = [];

	// authz-bypass-project-create: route-only bind — see Admin/Projects.cshtml.cs for why. Both
	// route templates (Program.cs AddPageRoute) carry {workspaceKey}; the project-scoped one also
	// carries {projectKey}, absent (null) on the workspace-only template.
	[FromRoute(Name = "workspaceKey")]
	public string? WorkspaceKey { get; set; }

	// Set when the page is mounted under /ui/{ws}/{projectKey}/config — auto-filters bindings
	// whose Tags include "project:{projectKey}".
	[FromRoute(Name = "projectKey")]
	public string? ProjectKey { get; set; }

	public string EffectiveWorkspaceKey { get; private set; } = "$system";

	public bool IsProjectScoped => !string.IsNullOrEmpty(ProjectKey);

	public string? KeyQuery { get; private set; }
	public IReadOnlyDictionary<string, string> TagFilter { get; private set; } =
		new Dictionary<string, string>(StringComparer.Ordinal);

	public IReadOnlyList<string> FacetKeys { get; private set; } = [];
	public IReadOnlyDictionary<string, IReadOnlyList<string>> FacetValues { get; private set; } =
		new Dictionary<string, IReadOnlyList<string>>();

	public IReadOnlyList<Core.Models.ConfigBinding> Rows { get; private set; } = [];
	public string? ErrorMessage { get; set; }
	public bool SecretsAvailable => _encryptor.IsAvailable;

	public static IReadOnlyList<(string Key, string Value)> ParseTagsDisplay(string tags)
	{
		var result = new List<(string, string)>();
		if (string.IsNullOrWhiteSpace(tags))
			return result;

		foreach (var part in tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			var eq = part.IndexOf('=');
			if (eq > 0)
				result.Add((part[..eq].Trim(), part[(eq + 1)..].Trim()));
		}
		return result;
	}

	public async Task<IActionResult> OnGetAsync(string? q, CancellationToken ct)
	{
		EffectiveWorkspaceKey = ResolveWorkspace();
		KeyQuery = string.IsNullOrWhiteSpace(q) ? null : q.Trim();

		SavedFilters = await _config.ListSavedFiltersAsync(EffectiveWorkspaceKey, ct);

		var all = await _config.ListActiveBindingsAsync(EffectiveWorkspaceKey, ct);

		var facetKeys = new SortedSet<string>(StringComparer.Ordinal);
		var facetValues = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
		foreach (var b in all)
		{
			foreach (var (k, v) in ParseTags(b.Tags))
			{
				facetKeys.Add(k);
				if (!facetValues.TryGetValue(k, out var set))
				{
					set = new SortedSet<string>(StringComparer.Ordinal);
					facetValues[k] = set;
				}
				set.Add(v);
			}
		}
		FacetKeys = [.. facetKeys];
		FacetValues = facetKeys.ToDictionary(k => k, k => (IReadOnlyList<string>)[.. facetValues[k]]);

		var filter = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var kv in Request.Query)
		{
			if (!kv.Key.StartsWith("t.", StringComparison.Ordinal)) continue;
			var tagKey = kv.Key[2..];
			var tagValue = kv.Value.LastOrDefault();
			if (string.IsNullOrEmpty(tagValue)) continue;
			filter[tagKey] = tagValue;
		}
		TagFilter = filter;

		Rows = [.. all.Where(b => MatchesTagFilter(b, filter) && MatchesKeyQuery(b, KeyQuery) && MatchesProjectScope(b))];
		return Page();
	}

	bool MatchesProjectScope(Core.Models.ConfigBinding b)
	{
		if (!IsProjectScoped) return true;
		var tag = $"project:{ProjectKey}";
		foreach (var t in b.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
			if (string.Equals(t, tag, StringComparison.OrdinalIgnoreCase))
				return true;
		return false;
	}

	public async Task<IActionResult> OnPostDeleteAsync(long id, CancellationToken ct)
	{
		EffectiveWorkspaceKey = ResolveWorkspace();
		await _config.DeleteBindingByIdAsync(EffectiveWorkspaceKey, id, User.Identity?.Name ?? "system", ct);

		// PRG + shared success notice (carried in TempData across the redirect), replacing the
		// old ?deleteSuccess=1 query flag. Build the redirect URL by hand (LocalRedirect) —
		// RedirectToPage("Index") yields an empty URL for these custom-routed config pages and
		// throws at execution (500). Mirrors RedirectBack; the page is also linked via Routes.*.
		this.NotifySuccess("Binding deleted.");
		return RedirectBack();
	}

	public async Task<IActionResult> OnPostSaveFilterAsync(string name, CancellationToken ct)
	{
		EffectiveWorkspaceKey = ResolveWorkspace();
		var pairs = new List<string>();
		foreach (var k in Request.Form.Keys)
		{
			if (!k.StartsWith("t.", StringComparison.Ordinal)) continue;
			var v = Request.Form[k].LastOrDefault();
			if (!string.IsNullOrEmpty(v)) pairs.Add($"{k[2..]}={v}");
		}
		if (!string.IsNullOrWhiteSpace(name) && pairs.Count > 0)
		{
			var filterTags = string.Join(",", pairs.OrderBy(p => p, StringComparer.Ordinal));
			var trimmed = name.Trim();
			await _config.SaveFilterAsync(EffectiveWorkspaceKey, trimmed, filterTags, ct);
			this.NotifySuccess($"Filter '{trimmed}' saved.");
		}
		return RedirectBack();
	}

	public async Task<IActionResult> OnPostDeleteFilterAsync(long id, CancellationToken ct)
	{
		EffectiveWorkspaceKey = ResolveWorkspace();
		await _config.DeleteFilterAsync(EffectiveWorkspaceKey, id, ct);
		this.NotifySuccess("Saved filter deleted.");
		return RedirectBack();
	}

	LocalRedirectResult RedirectBack(params string[] extraQuery)
	{
		var path = string.IsNullOrEmpty(ProjectKey)
			? $"/ui/{EffectiveWorkspaceKey}/config"
			: $"/ui/{EffectiveWorkspaceKey}/{ProjectKey}/config";
		var query = new List<string>();
		if (Request.Form.TryGetValue("q", out var qv) && !string.IsNullOrEmpty(qv))
			query.Add($"q={Uri.EscapeDataString(qv!)}");
		foreach (var k in Request.Form.Keys)
		{
			if (!k.StartsWith("t.", StringComparison.Ordinal)) continue;
			var v = Request.Form[k].LastOrDefault();
			if (!string.IsNullOrEmpty(v))
				query.Add($"{Uri.EscapeDataString(k)}={Uri.EscapeDataString(v)}");
		}
		query.AddRange(extraQuery);
		return LocalRedirect(query.Count > 0 ? $"{path}?{string.Join("&", query)}" : path);
	}

	public async Task<IActionResult> OnPostRevealAsync(long id, CancellationToken ct)
	{
		EffectiveWorkspaceKey = ResolveWorkspace();
		var binding = await _config.GetBindingAsync(EffectiveWorkspaceKey, id, ct);

		if (binding is null) return NotFound();
		if (binding.Kind != Core.Models.BindingKind.Secret) return BadRequest();
		if (!_encryptor.IsAvailable || binding.Ciphertext is null || binding.Iv is null || binding.AuthTag is null)
			return StatusCode(500);

		var userName = User.Identity?.Name ?? "system";
		var cacheKey = $"reveal-{EffectiveWorkspaceKey}-{id}-{userName}";

		var plaintext = _cache.Get<string>(cacheKey);
		if (plaintext is null)
		{
			try
			{
				plaintext = _encryptor.Decrypt(binding.Ciphertext, binding.Iv, binding.AuthTag);
				_cache.Set<string>(cacheKey, plaintext, TimeSpan.FromSeconds(10));
			}
			catch
			{
				return StatusCode(500);
			}
		}

		await _config.RecordRevealAsync(EffectiveWorkspaceKey, binding, userName, ct);

		return new JsonResult(new { plaintext });
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

	static Dictionary<string, string> ParseTags(string tags)
	{
		var result = new Dictionary<string, string>(StringComparer.Ordinal);
		if (string.IsNullOrWhiteSpace(tags))
			return result;

		// Binding tags are canonical "namespace:value" tokens (ws:/project:/env:/area:/…),
		// split on the first ':'. (The saved-filter wire format uses '=' and is parsed by
		// ParseTagsDisplay instead.)
		foreach (var part in tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			var sep = part.IndexOf(':');
			if (sep > 0)
				result[part[..sep].Trim()] = part[(sep + 1)..].Trim();
		}
		return result;
	}

	static bool MatchesTagFilter(Core.Models.ConfigBinding b, IReadOnlyDictionary<string, string> filter)
	{
		var tags = ParseTags(b.Tags);
		foreach (var (k, v) in filter)
			if (!tags.TryGetValue(k, out var bv) || !string.Equals(bv, v, StringComparison.Ordinal))
				return false;
		return true;
	}

	static bool MatchesKeyQuery(Core.Models.ConfigBinding b, string? q)
	{
		if (string.IsNullOrEmpty(q)) return true;
		if (q.EndsWith('*'))
			return b.Path.StartsWith(q[..^1], StringComparison.Ordinal);
		return b.Path.Contains(q, StringComparison.Ordinal);
	}
}
