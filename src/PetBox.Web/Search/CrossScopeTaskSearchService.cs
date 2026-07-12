using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
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
//
// ONE DI SCOPE PER BRANCH — the structural invariant that makes this fan-out legal at all.
// The request's own scope owns ONE PetBoxDb (AddScoped, Program.cs:101) and ONE of every other
// scoped service hanging off it (ITasksService/ITaskBoardStore, ILlmClient -> CapabilityRouter ->
// ILlmRegistryLevelResolver -> ISettingsResolver). A LinqToDB DataConnection is NOT thread-safe,
// so running N branches in parallel inside the request scope means N threads on one connection:
// prod 500'd with "Must add values for the following parameters: @projectKey, @board" (and
// "Collection was modified", ObjectDisposedException — one race, several faces). Patching the
// stores one by one to open their own connection only closes the CASE: the first fix gave the
// board-meta reads (TaskBoardStore) a private connection while the EMBED leg
// (SearchNodesAsync -> VectorSearchIndex -> CapabilityRouter -> LlmRegistryLevelResolver, which
// reads Projects/Settings/LlmRoutes/LlmEndpoints on EVERY query, before it can even decide there
// is no route) kept racing on the shared one. So the fix here is at the boundary that creates the
// parallelism: every branch resolves its OWN ITasksService out of its OWN IServiceScope, hence its
// own PetBoxDb and its own object graph. "Inside one DI scope there is no parallelism" becomes a
// structural fact instead of a convention no scoped service (present or future) can rely on.
public sealed class CrossScopeTaskSearchService(
	INavigationContext nav,
	IHttpContextAccessor http,
	IServiceScopeFactory scopes,
	ILogger<CrossScopeTaskSearchService>? log = null)
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
			// PARTIAL DEGRADATION, not a 500: one sick project must not take the whole page down.
			// The catch used to be `ArgumentException` around the full-text leg only, so anything
			// else (a corrupt tasks file, a missing board, the shared-PetBoxDb race this commit
			// fixes) escaped Task.WhenAll and 500'd /ui/search. A branch that fails contributes no
			// rows; every healthy project still answers. Cancellation is NOT swallowed — a
			// cancelled request must stay cancelled.
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				log?.LogWarning(ex, "Cross-scope search: project {ProjectKey} failed, skipping it", job.Project.Key);
				return (Exact: (IReadOnlyList<CrossScopeSearchHit>)[], FullText: (IReadOnlyList<CrossScopeSearchHit>)[]);
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
		// The branch's own DI scope: its own PetBoxDb and its own scoped graph (see the class note).
		// Everything it returns is a materialized record, so the scope — and every connection in it —
		// can die with the branch.
		await using var scope = scopes.CreateAsyncScope();
		var tasks = scope.ServiceProvider.GetRequiredService<ITasksService>();

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

		// Full-text leg. A failure here throws: the fan-out's per-project catch turns it into an
		// empty contribution (partial degradation) — no leg-local swallow, one place to reason about.
		var textRes = await tasks.SearchNodesAsync(project.Key, new SearchRequest<TaskNodeFilter, TaskSortBy>
		{
			Query = query,
			Limit = MaxFullTextPerProject,
			BodyLen = 0,
		}, urlPrefix, ct).ConfigureAwait(false);
		var fullText = textRes.Hits.Select(h => ToHit(ws, project.Key, h, exactMatch: false)).ToList();

		return (exact, (IReadOnlyList<CrossScopeSearchHit>)fullText);
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
		ExactMatch: exactMatch,
		// board-view-table reuse: h.Node is already the fully-enriched PlanNodeView (same read
		// TaskBoard's own table view projects), so these ride along for free — no extra query.
		Priority: h.Node.Priority,
		Tags: h.Node.Tags,
		UpdatedAt: h.Node.UpdatedAt,
		Delivery: h.Node.Delivery);
}

// One cross-scope search result row: the enriched node's identity/status plus WHERE it lives
// (Workspace/ProjectKey/Board — the spec requires every result to locate the task) and its
// absolute permalink. ExactMatch marks an identifier-leg hit (shown ahead of full-text hits).
// Priority/Tags/UpdatedAt/Delivery back the reused _TaskTable columns (board-view-mode-
// framework's table task) — this fan-out spans many projects/methodologies at once, so unlike
// TaskBoard's own table view, Status here is NOT resolved through a MethodologyRuntime (no
// single runtime applies); it renders as a plain outline badge, same as the page always has.
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
	bool ExactMatch,
	long Priority = 0,
	IReadOnlyList<string>? Tags = null,
	DateTime? UpdatedAt = null,
	string? Delivery = null);
