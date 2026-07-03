using Microsoft.AspNetCore.Http;
using PetBox.Core.Contract;
using PetBox.Core.Models;
using PetBox.Tasks.Contract;
using PetBox.Web.Navigation;

namespace PetBox.Web.Search;

// Cross-scope task search (spec cross-scope-task-search): the user pastes a slug/NodeId an
// agent handed them, or free text, and this fans the read out across EVERY workspace/project
// they can reach — not just the active one. Access scoping is free: `projectsByWorkspace`
// (NavigationContext.ProjectsByWorkspace for the real page) is already filtered to the
// caller's memberships (sysadmin sees all), so the fan-out never touches a project the
// caller can't see.
//
// Per project TWO legs run and merge:
//   IDENTIFIER fast-path — tasks.ExactIdentifierHitsAsync(q) (resolved like tasks_node_get: a
//     32-hex value is a NodeId, anything else a slug). This is the leg that finds a bare slug
//     paste: the FTS index covers title/body/tags, not the key, so `q` alone would otherwise
//     miss. It surfaces terminal nodes and, per exact-identifier-search-surfacing, returns ALL
//     exact matches — a same-slug-on-two-boards paste yields both (labelled by board), not an
//     error. A miss is an empty list, never a fault.
//   FULL-TEXT — SearchNodesAsync with Query=q, a small per-project cap. Skipped when the
//     identifier leg already found an exact hit in that project (redundant).
// Exact hits are returned before full-text hits; within each leg, project order follows
// ProjectsByWorkspace (workspace then project key) and full-text hits keep their per-project
// relevance order. De-duped by NodeId (globally unique) and capped at MaxResults.
public sealed class CrossScopeTaskSearchService(INavigationContext nav, IHttpContextAccessor http, ITasksService tasks)
{
	// Bounded fan-out: enough parallelism to make a many-project search feel instant without
	// hammering every project's TasksDb connection pool at once.
	public const int MaxProjectConcurrency = 6;
	// Full-text hits kept per project — this is a locator, not a research tool; a handful of
	// candidates per project is plenty, and the response-wide cap does the rest.
	public const int MaxFullTextPerProject = 5;
	// Total rows returned across every workspace/project.
	public const int MaxResults = 50;

	// Convenience overload: pulls the access-scoped project enumeration and the current
	// request's scheme/host from the injected NavigationContext/HttpContextAccessor. This is
	// what the Razor page calls.
	public Task<IReadOnlyList<CrossScopeSearchHit>> SearchAsync(string? q, CancellationToken ct = default)
	{
		var req = http.HttpContext?.Request;
		return SearchAsync(nav.ProjectsByWorkspace, q, req?.Scheme, req?.Host.ToString(), ct);
	}

	// The testable core: fan out + merge against an EXPLICIT project enumeration and an
	// explicit scheme/host (so permalinks can be built without a live HttpContext). The caller
	// owns access scoping — this method trusts `projectsByWorkspace` completely.
	public async Task<IReadOnlyList<CrossScopeSearchHit>> SearchAsync(
		IReadOnlyDictionary<string, IReadOnlyList<Project>> projectsByWorkspace,
		string? q, string? scheme, string? host, CancellationToken ct = default)
	{
		var query = q?.Trim();
		if (string.IsNullOrEmpty(query)) return [];

		var jobs = projectsByWorkspace
			.SelectMany(kv => kv.Value.Select(p => (Ws: kv.Key, Project: p)))
			.ToList();
		if (jobs.Count == 0) return [];

		using var gate = new SemaphoreSlim(MaxProjectConcurrency);
		var perProject = await Task.WhenAll(jobs.Select(async job =>
		{
			await gate.WaitAsync(ct).ConfigureAwait(false);
			try
			{
				return await SearchOneProjectAsync(job.Ws, job.Project, query, scheme, host, ct).ConfigureAwait(false);
			}
			finally
			{
				gate.Release();
			}
		})).ConfigureAwait(false);

		// Exact-identifier hits first (in project order), then full-text hits (also in
		// project order, each project's own slice already relevance-sorted); de-dup by
		// NodeId (globally unique — the same node can't legitimately appear twice) and cap.
		var seen = new HashSet<string>(StringComparer.Ordinal);
		var merged = new List<CrossScopeSearchHit>();
		foreach (var hit in perProject.SelectMany(r => r.Exact).Concat(perProject.SelectMany(r => r.FullText)))
		{
			if (merged.Count >= MaxResults) break;
			if (seen.Add(hit.NodeId)) merged.Add(hit);
		}
		return merged;
	}

	async Task<(IReadOnlyList<CrossScopeSearchHit> Exact, IReadOnlyList<CrossScopeSearchHit> FullText)> SearchOneProjectAsync(
		string ws, Project project, string query, string? scheme, string? host, CancellationToken ct)
	{
		var urlPrefix = scheme is null || host is null
			? null
			: $"{scheme}://{host}{Routes.ProjectTasks(ws, project.Key)}/";

		// Exact-identifier leg (exact-identifier-search-surfacing): every node whose slug/NodeId
		// exactly equals `query`, INCLUDING terminal ones and ALL boards when a slug is shared —
		// ambiguity is not an error in search, so a same-slug-on-two-boards paste surfaces both
		// (each row labelled by board). A miss is an empty list, never a fault (no try/catch).
		var exactHits = await tasks.ExactIdentifierHitsAsync(project.Key, query, board: null, urlPrefix, ct).ConfigureAwait(false);

		var exact = exactHits.Select(h => ToHit(ws, project.Key, h, exactMatch: true)).ToList();
		if (exact.Count > 0)
			return (exact, []); // the identifier already resolved — a full-text pass would be redundant

		IReadOnlyList<CrossScopeSearchHit> fullText;
		try
		{
			var textRes = await tasks.SearchNodesAsync(project.Key, new SearchRequest<TaskNodeFilter, TaskSortBy>
			{
				Query = query,
				Limit = MaxFullTextPerProject,
				BodyLen = 0,
			}, urlPrefix, ct).ConfigureAwait(false);
			fullText = textRes.Hits.Select(h => ToHit(ws, project.Key, h, exactMatch: false)).ToList();
		}
		catch (ArgumentException)
		{
			// Defensive: one project's query-mode quirk shouldn't take down the whole fan-out.
			fullText = [];
		}

		return (exact, fullText);
	}

	static CrossScopeSearchHit ToHit(string ws, string projectKey, TaskSearchHit h, bool exactMatch) => new(
		Workspace: ws,
		ProjectKey: projectKey,
		Board: h.Board,
		Key: h.Node.Key,
		NodeId: h.Node.NodeId,
		Title: h.Node.Title,
		Status: h.Node.Status,
		Type: h.Node.Type,
		Url: h.Node.Url ?? "",
		ExactMatch: exactMatch);
}

// One cross-scope search result row: the enriched node's identity/status plus WHERE it lives
// (Workspace/ProjectKey/Board — the spec requires every result to locate the task) and its
// absolute permalink. ExactMatch marks an identifier-leg hit (shown ahead of full-text hits).
public sealed record CrossScopeSearchHit(
	string Workspace,
	string ProjectKey,
	string Board,
	string Key,
	string NodeId,
	string Title,
	string Status,
	string Type,
	string Url,
	bool ExactMatch);
