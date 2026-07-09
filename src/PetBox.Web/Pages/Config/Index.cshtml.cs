using LinqToDB;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Caching.Memory;
using PetBox.Config;
using PetBox.Config.Data;
using PetBox.Core.Auth;
using PetBox.Core.Data;
using PetBox.Core.Models;

namespace PetBox.Web.Pages.Config;

[Authorize(Policy = "WorkspaceAdmin")]
public sealed class IndexModel : PageModel
{
	readonly IConfigDbFactory _configFactory;
	readonly ISecretEncryptor _encryptor;
	readonly Microsoft.Extensions.Caching.Memory.IMemoryCache _cache;
	readonly PetBoxDb _db;

	public IndexModel(
		IConfigDbFactory configFactory,
		ISecretEncryptor encryptor,
		Microsoft.Extensions.Caching.Memory.IMemoryCache cache,
		PetBoxDb db)
	{
		_configFactory = configFactory;
		_encryptor = encryptor;
		_cache = cache;
		_db = db;
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

	public IActionResult OnGet(string? q)
	{
		EffectiveWorkspaceKey = ResolveWorkspace();
		KeyQuery = string.IsNullOrWhiteSpace(q) ? null : q.Trim();

		SavedFilters = _db.SavedConfigFilters
			.Where(f => f.WorkspaceKey == EffectiveWorkspaceKey)
			.OrderBy(f => f.Name)
			.ToList();

		using var configDb = _configFactory.GetConfigDb(EffectiveWorkspaceKey);
		var all = configDb.Bindings.Where(b => !b.IsDeleted).OrderBy(b => b.Path).ToList();

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

	public IActionResult OnPostDelete(long id)
	{
		EffectiveWorkspaceKey = ResolveWorkspace();
		using var configDb = _configFactory.GetConfigDb(EffectiveWorkspaceKey);

		var existing = configDb.Bindings.FirstOrDefault(b => b.Id == id && !b.IsDeleted);
		if (existing is not null)
		{
			var now = DateTime.UtcNow;
			configDb.Insert(new ConfigBindingHistoryEntry
			{
				BindingId = existing.Id,
				Action = "Delete",
				Path = existing.Path,
				Tags = existing.Tags,
				Kind = existing.Kind,
				OldValue = existing.Kind == Core.Models.BindingKind.Plain ? existing.Value : "(secret)",
				NewValue = null,
				Actor = User.Identity?.Name ?? "system",
				At = now,
			});
			configDb.Bindings
				.Where(b => b.Id == id)
				.Set(b => b.IsDeleted, true)
				.Set(b => b.DeletedAt, (DateTime?)now)
				.Set(b => b.UpdatedAt, now)
				.Update();
		}

		// PRG + shared success notice (carried in TempData across the redirect), replacing the
		// old ?deleteSuccess=1 query flag. Build the redirect URL by hand (LocalRedirect) —
		// RedirectToPage("Index") yields an empty URL for these custom-routed config pages and
		// throws at execution (500). Mirrors RedirectBack; the page is also linked via Routes.*.
		this.NotifySuccess("Binding deleted.");
		return RedirectBack();
	}

	public IActionResult OnPostSaveFilter(string name)
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
			var existing = _db.SavedConfigFilters.FirstOrDefault(
				f => f.WorkspaceKey == EffectiveWorkspaceKey && f.Name == trimmed);
			if (existing is null)
				_db.Insert(new SavedConfigFilter { WorkspaceKey = EffectiveWorkspaceKey, Name = trimmed, FilterTags = filterTags, CreatedAt = DateTime.UtcNow });
			else
				_db.SavedConfigFilters.Where(f => f.Id == existing.Id).Set(f => f.FilterTags, filterTags).Update();
			this.NotifySuccess($"Filter '{trimmed}' saved.");
		}
		return RedirectBack();
	}

	public IActionResult OnPostDeleteFilter(long id)
	{
		EffectiveWorkspaceKey = ResolveWorkspace();
		_db.SavedConfigFilters.Where(f => f.Id == id && f.WorkspaceKey == EffectiveWorkspaceKey).Delete();
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

	public IActionResult OnPostReveal(long id)
	{
		EffectiveWorkspaceKey = ResolveWorkspace();
		using var configDb = _configFactory.GetConfigDb(EffectiveWorkspaceKey);
		var binding = configDb.Bindings.FirstOrDefault(b => b.Id == id);

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

		configDb.Insert(new ConfigBindingHistoryEntry
		{
			BindingId = id,
			Action = "Reveal",
			Path = binding.Path,
			Tags = binding.Tags,
			Kind = binding.Kind,
			OldValue = null,
			NewValue = null,
			Actor = userName,
			At = DateTime.UtcNow,
		});

		return new JsonResult(new { plaintext });
	}

	string ResolveWorkspace()
	{
		if (!string.IsNullOrEmpty(WorkspaceKey))
			return WorkspaceKey;
		var claimWs = User.FindFirst(PetBoxClaims.ActiveWorkspace)?.Value;
		return string.IsNullOrEmpty(claimWs) ? "$system" : claimWs;
	}

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
