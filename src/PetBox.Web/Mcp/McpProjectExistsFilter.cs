using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using PetBox.Core.Auth;
using PetBox.Core.Data;

namespace PetBox.Web.Mcp;

// W3 (spec project-ref-must-exist): a call that REFERS to a project which is not in the registry is
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
// sees the RESOLVED projectKey. It is INSIDE McpErrorEnvelopeFilter, so the throw becomes the
// structured { error: … } body every other reject uses.
//
// THREE ways a call refers to a project, and all three are checked — the first version only checked
// the first, which left the very write path W1 enabled unguarded:
//   explicit — the caller passed projectKey;
//   injected — W2's leg 1 filled in the key's default (the tool REQUIRES projectKey);
//   resolved — the caller passed NOTHING to a tool whose projectKey is OPTIONAL (memory_remember,
//              memory_search, session_search, search_reindex). W2 deliberately does not inject there;
//              the tool resolves the SAME default INSIDE, via ModuleMcp.ResolveProject. So when the
//              tool takes a projectKey and none was supplied, the caller's default IS the project
//              this call names — validate it here, or memory_remember(text:"…") on a key whose
//              default was deleted auto-vivifies memory/<gone>.db + its MemoryStores row.
// One catalog query per call at most (either the named project or the resolved default, never both),
// and the default is read through ModuleMcp.DefaultProjectOf — the same single source W2's two legs
// and ResolveProject use, so what is validated is what the tool will use.
//
// The two VALUES that are not project references, and their one rule each:
//   "*"   — the claim sentinel. A project reference NEVER, except on the apikey_* tools that address
//           keys BY claim (WildcardTools below). Everywhere else it is refused: `Authorizes("*","*")`
//           is true and ScopedDbFiles.PathFor does not sanitize, so tasks_board_create(projectKey:"*")
//           from a "*" key used to create a literal `tasks/*.db`.
//   blank — ABSENT (the resolver's job). Normalized away by McpProjectDefaultFilter before this filter
//           runs, and refused by ProjectScope.Authorizes if it ever gets past that.
//
// $workspace / $ws-<key> are project references to a MEMORY CONTAINER, and are validated against what
// they actually name — an existing WORKSPACE. Their Projects row is created lazily on first resolve
// (WorkspaceMemory.EnsureContainerAsync), so checking them against the project registry would refuse
// the first-ever direct write to a fresh workspace's shared memory — and the advice ("create it with
// project_create") cannot even be followed, project_create's key regex forbids `$`. A typo'd
// `$ws-nosuch` still names no workspace and is still refused.
//
// Deliberately NOT in ProjectScope/ScopedDbFactory: lazy file creation is load-bearing below the MCP
// surface (a project created seconds ago has no store file until its first write; the $system seed,
// the $ws-* memory containers and the test fixtures all rely on the store appearing on demand). The
// registry check belongs at the ENTRY point, where "which project did the caller name" is a question
// that still exists.
//
// FAIL-CLOSED (the opposite of the sibling default-filter, which fails open): an unknown project is
// an error, and if the catalog cannot be read at all the call is not silently let through — the only
// pass-throughs are the cases above, which are not project references at all.
static class McpProjectExistsFilter
{
	// The ONLY tools where "*" in the projectKey slot is a legitimate value: they address API KEYS by
	// their project CLAIM, and "*" is the cross-project claim — apikey_list('*') lists those keys,
	// apikey_create carries it in the same slot alongside allProjects:true. Neither addresses storage.
	// Verified against ApiKeyTools (apikey_delete takes `key`, not `projectKey`). Every other tool
	// routes STORAGE by projectKey, where "*" is a file name, not a wildcard.
	static readonly HashSet<string> WildcardTools =
		new(StringComparer.Ordinal) { "apikey_create", "apikey_list" };

	public static void Register(IMcpRequestFilterBuilder filters) =>
		filters.AddCallToolFilter(next => async (request, ct) =>
		{
			await AssertExistsAsync(request, ct);
			return await next(request, ct);
		});

	static async ValueTask AssertExistsAsync(RequestContext<CallToolRequestParams> request, CancellationToken ct)
	{
		if (request.Params is not { } p) return;

		if (Named(p.Arguments) is not { } project)
		{
			// No projectKey on the wire. On a tool that TAKES one this is still a project reference —
			// the tool will resolve the caller's default itself (see the header). On a tool that takes
			// none (whoami, project_list, …) it is not, and must not drag the default into the call.
			if (!McpProjectDefaultFilter.TakesProjectKey(request.Services, p.Name)) return;
			if (ModuleMcp.DefaultProjectOf(request.User) is not { } fallback) return;   // nothing resolves — the tool's own error
			await AssertKnownAsync(request, fallback, ct);
			return;
		}

		if (project == ProjectScope.AllProjects)
		{
			if (WildcardTools.Contains(p.Name ?? "")) return;   // the claim sentinel, on the tools that mean it
			throw new InvalidOperationException(WildcardMessage(p.Name));
		}

		// AUTHORIZATION FIRST: a key that may not touch this project gets the tool's own
		// UnauthorizedAccessException, not "does not exist" — the reject an unauthorized caller
		// deserves, and it keeps the registry from becoming an existence oracle for foreign keys.
		// What remains for the existence check is exactly the surface that could create storage:
		// every project a "*" key names, and a scoped key's own claim (which a deletion can strip).
		var claim = request.User?.Claims.FirstOrDefault(c => c.Type == "project")?.Value;
		if (!ProjectScope.Authorizes(claim, project)) return;

		await AssertKnownAsync(request, project, ct);
	}

	// The registry the reference is checked against depends on what it names: a memory container names
	// a WORKSPACE, everything else names a PROJECT.
	static async ValueTask AssertKnownAsync(
		RequestContext<CallToolRequestParams> request, string project, CancellationToken ct)
	{
		var catalog = request.Services?.GetService<IProjectCatalog>();
		if (catalog is null) return;   // no catalog in this host at all — nothing to check against

		if (WorkspaceMemory.WorkspaceKeyOfContainer(project) is { } workspace)
		{
			var workspaces = await catalog.ListWorkspaceKeysAsync(ct);
			if (workspaces.Contains(workspace, StringComparer.Ordinal)) return;
			throw new InvalidOperationException(ContainerMessage(project, workspace, workspaces));
		}

		var known = await catalog.ListProjectKeysAsync(ct);
		if (known.Contains(project, StringComparer.Ordinal)) return;

		throw new InvalidOperationException(Message(project, known));
	}

	// The project this call NAMES on the wire, or null when it names none (no argument, a null one, or
	// a non-string one — the tool's own binder gives the better error for the last). A blank string is
	// ABSENT and never reaches here: McpProjectDefaultFilter strips it upstream.
	static string? Named(IDictionary<string, JsonElement>? args)
	{
		if (args is null || !args.TryGetValue(McpProjectDefaultFilter.ProjectKeyArg, out var el)) return null;
		if (el.ValueKind != JsonValueKind.String) return null;
		var project = el.GetString();
		return string.IsNullOrWhiteSpace(project) ? null : project;
	}

	internal static string WildcardMessage(string? tool) =>
		$"'{ProjectScope.AllProjects}' is not a project. It is the cross-project API-key CLAIM, valid in a "
		+ $"projectKey argument only on the apikey_* tools that address keys by claim — not on '{tool}', which "
		+ "routes per-project storage. Name one real project (project_list), or omit projectKey to use the "
		+ "key's default project.";

	// A $workspace / $ws-<key> reference that names no workspace. Deliberately does NOT say
	// "create it with project_create" — that tool's key regex forbids `$`; the container appears by
	// itself once the WORKSPACE exists.
	internal static string ContainerMessage(string container, string workspace, IReadOnlyList<string> known)
	{
		var near = Suggest(workspace, known);
		var hint = near.Count == 0
			? ""
			: $" Did you mean {string.Join(" / ", near.Select(k => $"'{WorkspaceMemory.ContainerKeyFor(k)}'"))}?";
		return $"'{container}' names no workspace — there is no workspace '{workspace}'.{hint} "
			+ "A workspace memory container is '$workspace' ($system) or '$ws-<workspaceKey>' of an existing "
			+ "workspace; omit projectKey and pass scope:'workspace' to address your own.";
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

	// Near misses over the registry: prefix relatives first, then small edit distances. Deliberately
	// cheap (the registry is tens of rows) and deliberately narrow — a suggestion list that fires on
	// everything is noise.
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

	// A prefix relation counts as a near miss only from MinPrefix chars up, and scores the LENGTH GAP
	// rather than 0: a one-char reference is a fragment, not a typo, and used to score every project
	// starting with that letter as a perfect match ("k" → "kpvotes"). Scoring the gap also orders the
	// prefix relatives sensibly ("kpvotes-bot" is closer to "kpvotes" than "kpvotes-bot-staging" is).
	const int MinPrefix = 3;

	// Lower is closer; int.MaxValue = "not a near miss".
	static int Score(string typo, string candidate, int budget)
	{
		if (candidate.StartsWith(typo, StringComparison.OrdinalIgnoreCase)
			|| typo.StartsWith(candidate, StringComparison.OrdinalIgnoreCase))
			return Math.Min(typo.Length, candidate.Length) >= MinPrefix
				? Math.Abs(typo.Length - candidate.Length)
				: int.MaxValue;
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
