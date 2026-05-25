using LinqToDB;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Caching.Memory;
using YobaBox.Config;
using YobaBox.Config.Data;
using YobaBox.Core.Auth;

namespace YobaBox.Web.Pages.Config;

[Authorize]
public sealed class IndexModel : PageModel
{
	readonly IConfigDbFactory _configFactory;
	readonly ISecretEncryptor _encryptor;
	readonly Microsoft.Extensions.Caching.Memory.IMemoryCache _cache;

	public IndexModel(
		IConfigDbFactory configFactory,
		ISecretEncryptor encryptor,
		Microsoft.Extensions.Caching.Memory.IMemoryCache cache)
	{
		_configFactory = configFactory;
		_encryptor = encryptor;
		_cache = cache;
	}

	[BindProperty(SupportsGet = true)]
	public string? WorkspaceKey { get; set; }

	public string EffectiveWorkspaceKey { get; private set; } = "$system";

	public string? KeyQuery { get; private set; }
	public IReadOnlyDictionary<string, string> TagFilter { get; private set; } =
		new Dictionary<string, string>(StringComparer.Ordinal);

	public IReadOnlyList<string> FacetKeys { get; private set; } = [];
	public IReadOnlyDictionary<string, IReadOnlyList<string>> FacetValues { get; private set; } =
		new Dictionary<string, IReadOnlyList<string>>();

	public IReadOnlyList<Core.Models.ConfigBinding> Rows { get; private set; } = [];
	public string? ErrorMessage { get; set; }
	public string? SuccessMessage { get; set; }
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

		var flash = Request.Query["deleteSuccess"].FirstOrDefault();
		if (!string.IsNullOrEmpty(flash))
			SuccessMessage = "Binding deleted.";

		var configDb = _configFactory.GetConfigDb(EffectiveWorkspaceKey);
		var all = configDb.Bindings.OrderBy(b => b.Path).ToList();

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

		Rows = [.. all.Where(b => MatchesTagFilter(b, filter) && MatchesKeyQuery(b, KeyQuery))];
		return Page();
	}

	public IActionResult OnPostDelete(long id)
	{
		EffectiveWorkspaceKey = ResolveWorkspace();
		var configDb = _configFactory.GetConfigDb(EffectiveWorkspaceKey);

		var existing = configDb.Bindings.FirstOrDefault(b => b.Id == id);
		if (existing is not null)
		{
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
				At = DateTime.UtcNow,
			});
			configDb.Bindings.Where(b => b.Id == id).Delete();
		}

		var routeValues = new RouteValueDictionary { ["workspaceKey"] = EffectiveWorkspaceKey };
		if (Request.Form.TryGetValue("q", out var qv) && !string.IsNullOrEmpty(qv))
			routeValues["q"] = qv.ToString();
		foreach (var k in Request.Form.Keys)
		{
			if (!k.StartsWith("t.", StringComparison.Ordinal)) continue;
			var v = Request.Form[k].LastOrDefault();
			if (v is not null)
				routeValues[k] = v;
		}
		routeValues["deleteSuccess"] = "1";
		return RedirectToPage("Index", routeValues);
	}

	public IActionResult OnPostReveal(long id)
	{
		EffectiveWorkspaceKey = ResolveWorkspace();
		var configDb = _configFactory.GetConfigDb(EffectiveWorkspaceKey);
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
		var claimWs = User.FindFirst(YobaBoxClaims.ActiveWorkspace)?.Value;
		return string.IsNullOrEmpty(claimWs) ? "$system" : claimWs;
	}

	static Dictionary<string, string> ParseTags(string tags)
	{
		var result = new Dictionary<string, string>(StringComparer.Ordinal);
		if (string.IsNullOrWhiteSpace(tags))
			return result;

		foreach (var part in tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			var eq = part.IndexOf('=');
			if (eq > 0)
				result[part[..eq].Trim()] = part[(eq + 1)..].Trim();
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
