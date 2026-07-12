using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using PetBox.Core.Auth;
using PetBox.Core.Data;

namespace PetBox.Web.Mcp;

// W3 (spec project-ref-must-exist): a call that NAMES a project which is not in the registry is
// rejected — per-project storage must never come into existence merely because someone typed a key.
//
// The hole this closes: ProjectScope.Authorizes only compares the CLAIM. A cross-project ("*") key
// authorizes every projectKey, existing or not, and the per-project stores below (ScopedDbFactory,
// DataDbFactory) create their file LAZILY on first use. So `tasks_upsert(projectKey: "kpvots", …)`
// from a "*" key did not fail: it silently materialized a brand-new store and wrote the data there.
// The owner runs on a "*" key, so this was the live path — and the failure mode is the silent kind
// (the write "succeeds", in the wrong place). W2 widened the blast radius one notch further: an
// INJECTED default naming a since-deleted project would do the same without the caller naming it.
//
// Where it sits: a tools/call filter registered AFTER McpProjectDefaultFilter, i.e. INNERMOST, so it
// sees the RESOLVED projectKey — the caller's explicit one, or the one W2's leg 1 just injected. One
// check covers both paths. It is INSIDE McpErrorEnvelopeFilter, so the throw becomes the structured
// { error: … } body every other reject uses.
//
// Deliberately NOT in ProjectScope/ScopedDbFactory: lazy file creation is load-bearing below the MCP
// surface (a project created seconds ago has no store file until its first write; the $system seed,
// the $ws-* memory containers and the test fixtures all rely on the store appearing on demand). The
// registry check belongs at the ENTRY point, where "which project did the caller name" is a question
// that still exists.
//
// FAIL-CLOSED (the opposite of the sibling default-filter, which fails open): an unknown project is
// an error, and if the catalog cannot be read at all the call is not silently let through — the only
// pass-throughs are the cases below, which are not project references at all.
static class McpProjectExistsFilter
{
	// The tool allow-list is EMPTY, and that is a finding rather than an omission: no MCP tool takes a
	// `projectKey` that may name a not-yet-existing project. project_create — the one tool that must
	// name one — calls its parameter `key` (ProjectTools.CreateAsync), so it is structurally outside a
	// filter keyed on `projectKey` and needs no exemption. Should a future tool need one, add it here
	// with a reason, not silently.
	static readonly HashSet<string> ExemptTools = new(StringComparer.Ordinal);

	public static void Register(IMcpRequestFilterBuilder filters) =>
		filters.AddCallToolFilter(next => async (request, ct) =>
		{
			await AssertExistsAsync(request, ct);
			return await next(request, ct);
		});

	static async ValueTask AssertExistsAsync(RequestContext<CallToolRequestParams> request, CancellationToken ct)
	{
		if (request.Params is not { } p) return;
		if (p.Name is { } tool && ExemptTools.Contains(tool)) return;
		if (Named(p.Arguments) is not { } project) return;

		// AUTHORIZATION FIRST: a key that may not touch this project gets the tool's own
		// UnauthorizedAccessException, not "does not exist" — the reject an unauthorized caller
		// deserves, and it keeps the registry from becoming an existence oracle for foreign keys.
		// What remains for the existence check is exactly the surface that could create storage:
		// every project a "*" key names, and a scoped key's own claim (which a deletion can strip).
		var claim = request.User?.Claims.FirstOrDefault(c => c.Type == "project")?.Value;
		if (!ProjectScope.Authorizes(claim, project)) return;

		var catalog = request.Services?.GetService<IProjectCatalog>();
		if (catalog is null) return;   // no catalog in this host at all — nothing to check against

		var known = await catalog.ListProjectKeysAsync(ct);
		if (known.Contains(project, StringComparer.Ordinal)) return;

		throw new InvalidOperationException(Message(project, known));
	}

	// The project this call NAMES, or null when it names none. Not a project reference:
	//   * no projectKey argument (the tool doesn't take one, or W2 had no default to inject);
	//   * a null/blank/non-string one — the tool's own "projectKey is required" is the better error;
	//   * the wildcard "*" — a claim sentinel, not a project. apikey_list('*') lists the cross-project
	//     keys and apikey_create carries it in the same slot; neither addresses storage.
	static string? Named(IDictionary<string, JsonElement>? args)
	{
		if (args is null || !args.TryGetValue(McpProjectDefaultFilter.ProjectKeyArg, out var el)) return null;
		if (el.ValueKind != JsonValueKind.String) return null;
		var project = el.GetString();
		if (string.IsNullOrWhiteSpace(project)) return null;
		return project == ProjectScope.AllProjects ? null : project;
	}

	// The dominant real cause of an unknown project is a TYPO (or a model-hallucinated key), so the
	// rejection names the near misses — an agent that reads "did you mean 'kpvotes'?" self-corrects on
	// the next call instead of retrying the same misroute.
	internal static string Message(string project, IReadOnlyList<string> known)
	{
		var near = Suggest(project, known);
		var hint = near.Count == 0
			? " No project with a similar key exists — list them with project_list."
			: $" Did you mean {string.Join(" / ", near.Select(k => $"'{k}'"))}?";
		return $"Project '{project}' does not exist.{hint} "
			+ "A projectKey must name a project in the registry — storage is never created implicitly "
			+ "by naming it (create the project with project_create first).";
	}

	// Near misses over the registry: prefix/substring relatives first, then small edit distances.
	// Deliberately cheap (the registry is tens of rows) and deliberately narrow — a suggestion list
	// that fires on everything is noise.
	internal static IReadOnlyList<string> Suggest(string project, IReadOnlyList<string> known, int take = 3)
	{
		var budget = Math.Max(1, Math.Min(3, project.Length / 3));
		return known
			.Select(k => (Key: k, Score: Score(project, k, budget)))
			.Where(x => x.Score < int.MaxValue)
			.OrderBy(x => x.Score)
			.ThenBy(x => x.Key, StringComparer.Ordinal)
			.Take(take)
			.Select(x => x.Key)
			.ToList();
	}

	// Lower is closer; int.MaxValue = "not a near miss".
	static int Score(string typo, string candidate, int budget)
	{
		if (candidate.StartsWith(typo, StringComparison.OrdinalIgnoreCase)
			|| typo.StartsWith(candidate, StringComparison.OrdinalIgnoreCase))
			return 0;
		var distance = Distance(typo, candidate, budget);
		return distance <= budget ? distance : int.MaxValue;
	}

	// Levenshtein, abandoned once every cell of a row exceeds the budget (the registry rows that are
	// nowhere near the typo cost one short row each).
	static int Distance(string a, string b, int budget)
	{
		if (Math.Abs(a.Length - b.Length) > budget) return int.MaxValue;
		var previous = new int[b.Length + 1];
		var current = new int[b.Length + 1];
		for (var j = 0; j <= b.Length; j++) previous[j] = j;

		for (var i = 1; i <= a.Length; i++)
		{
			current[0] = i;
			var best = current[0];
			for (var j = 1; j <= b.Length; j++)
			{
				var cost = char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;
				current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
				best = Math.Min(best, current[j]);
			}
			if (best > budget) return int.MaxValue;
			(previous, current) = (current, previous);
		}
		return previous[b.Length];
	}
}
