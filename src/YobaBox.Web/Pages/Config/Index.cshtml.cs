using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Routing;
using YobaBox.Core.Data;

namespace YobaBox.Web.Pages.Config;

[Authorize]
public sealed class IndexModel : PageModel
{
	readonly YobaBoxDb _db;

	public IndexModel(YobaBoxDb db) => _db = db;

	public string? KeyQuery { get; private set; }
	public IReadOnlyDictionary<string, string> TagFilter { get; private set; } =
		new Dictionary<string, string>(StringComparer.Ordinal);

	public IReadOnlyList<string> FacetKeys { get; private set; } = [];
	public IReadOnlyDictionary<string, IReadOnlyList<string>> FacetValues { get; private set; } =
		new Dictionary<string, IReadOnlyList<string>>();

	public IReadOnlyList<Core.Models.ConfigBinding> Rows { get; private set; } = [];
	public string? ErrorMessage { get; set; }
	public string? SuccessMessage { get; set; }

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

	public void OnGet(string? q)
	{
		KeyQuery = string.IsNullOrWhiteSpace(q) ? null : q.Trim();

		var flash = Request.Query["deleteSuccess"].FirstOrDefault();
		if (!string.IsNullOrEmpty(flash))
			SuccessMessage = "Binding deleted.";

		var all = _db.ConfigBindings.OrderBy(b => b.Path).ToList();

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

		var matching = all.Where(b => MatchesTagFilter(b, filter) && MatchesKeyQuery(b, KeyQuery));
		Rows = [.. matching];
	}

	public IActionResult OnPostDelete(long id)
	{
		var routeValues = new RouteValueDictionary();
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

		_db.ConfigBindings.Where(b => b.Id == id).Delete();
		return RedirectToPage("Index", routeValues);
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
