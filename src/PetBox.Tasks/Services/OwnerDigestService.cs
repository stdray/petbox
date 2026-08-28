using PetBox.Core.Contract;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Data;
using PetBox.Tasks.Workflow;

namespace PetBox.Tasks.Services;

// THE ONE ASSEMBLY of the owner-away digest — read IOwnerDigestService's header first for what the
// sections are and why their order is fixed.
//
// IT OWNS NO STORE. Every fact here comes from the module's EXISTING doors:
//   * ITasksService.DeltaAsync      — the change window. This is `tasks_delta`'s own service call,
//                                     the cursor/catch-up surface that rides TemporalStore
//                                     .ChangesSinceAsync; the digest does not re-derive "what moved".
//   * ITasksService.SearchNodesAsync — enrichment (tags, permalinks) for the window's nodes, and the
//                                     `decisionPending` PREDICATE for section (1). That filter exists
//                                     precisely so this section is not a full board scan (see
//                                     TaskNodeFilter.DecisionPending's own comment).
//   * ITasksService.GetBoardWorkflowAsync — the terminal classification. "Closed" is resolved from
//                                     the board's own FSM, never from a status SPELLING: a board
//                                     whose methodology calls its terminal state something else
//                                     still reports closures correctly.
//   * ICommentService.DeltaAsync    — `comments_delta`, the chronology's other half.
//
// WHY THE DELTA DEFINES THE WINDOW BUT NOT THE ROWS: the delta hands back TaskNode rows, whose
// Created/Updated are the temporal store's own stamps — the only timestamps in this system that are
// certainly there. Enrichment (tags/urls) comes from the search door, and is JOINED onto the delta
// rows by key rather than replacing them, so a difference in what an enrichment projection happens
// to fill can never move a node into or out of the period.
//
// MEMORY IS NOT IN THIS DIGEST, and that is a decision rather than an omission. `memory_delta` is
// cursored PER STORE (IMemoryService.DeltaAsync(projectKey, store, sinceVersion)), so covering
// memory means holding one cursor per store and inventing a composite cursor shape; and none of the
// three sections the owner ordered — waiting / closed / new cohorts — has a memory row to put in it.
// Chronology could carry memory writes, but only at the price of that composite cursor. Left out,
// named here, not worked around silently.
public sealed class OwnerDigestService : IOwnerDigestService
{
	// THE CAVEAT, in one place, so the MCP verb and the page cannot drift into two different
	// promises. The server does not record the moment a node's status changed — there is no
	// status-transition log — so "what closed" can only be dated by the terminal node's own
	// `updatedAt`, which is the last time ANYTHING on that node was revised. A node closed a week
	// ago and re-tagged today dates to today. It is a PROXY, it is named as one on both doors, and
	// cycle time is NOT measurable from it.
	public const string Caveat =
		"Closure dates are a PROXY: the server does not store when a status changed, so a closed "
		+ "node is dated by its updatedAt — the last revision of anything on it, not the moment it "
		+ "closed. Cycle time is not measurable from this.";

	readonly ITasksService _tasks;
	readonly ICommentService _comments;
	readonly TimeProvider _time;

	public OwnerDigestService(ITasksService tasks, ICommentService comments, TimeProvider? time = null)
	{
		_tasks = tasks;
		_comments = comments;
		_time = time ?? TimeProvider.System;
	}

	public async Task<OwnerDigestView> DigestAsync(
		string projectKey, OwnerDigestRequest request, string? urlPrefix = null, CancellationToken ct = default)
	{
		var board = request.Board;
		var limit = request.SectionLimit > 0 ? request.SectionLimit : OwnerDigestRequest.DefaultSectionLimit;
		var sinceVersion = request.SinceVersion ?? 0;

		// The window. A version cursor names a REVISION, not an instant, so WindowStart stays null
		// there — the alternative is printing a date the cursor does not actually carry.
		DateTime? windowStart = request.SinceVersion is null
			? _time.GetUtcNow().UtcDateTime.AddDays(-(request.Days > 0 ? request.Days : OwnerDigestRequest.DefaultDays))
			: null;

		var delta = await _tasks.DeltaAsync(projectKey, board, sinceVersion, ct);
		var currentVersion = delta.Result.CurrentVersion;

		// `Added` vs `Updated` is the temporal layer's per-batch birth split (Created == Updated).
		// It is the ONLY birth signal available against a bare version cursor; with a time window we
		// have the better one (the row's own Created), and BirthIsInWindow prefers it. Both are used
		// so a node born after the cursor AND edited since — which lands in `Updated` — is still new.
		var bornInBatch = delta.Result.Added.Select(n => n.Key).ToHashSet(StringComparer.Ordinal);
		var changed = delta.Result.Added.Concat(delta.Result.Updated)
			.Where(n => windowStart is null || n.Updated >= windowStart.Value)
			.ToList();

		var statusKinds = await StatusKindMapAsync(projectKey, board, ct);

		// One enrichment read for the whole window, addressed by key (terminal nodes included — an
		// explicit ask, which is exactly what "what closed" needs).
		var enrichment = await EnrichAsync(projectKey, board, changed.Select(n => n.Key).ToList(), urlPrefix, ct);

		// ── (2) what closed ──────────────────────────────────────────────────────────────────────
		var closedAll = changed
			.Where(n => Kind(statusKinds, n) is StatusKind.TerminalOk or StatusKind.TerminalCancel)
			.OrderByDescending(n => n.Updated)
			.ThenBy(n => n.Key, StringComparer.Ordinal)
			.ToList();
		var closedKeys = closedAll.Select(n => n.Key).ToHashSet(StringComparer.Ordinal);

		// ── (3) new cohorts by theme ─────────────────────────────────────────────────────────────
		// A node created AND closed inside the same window belongs to "what closed" — repeating it
		// as a new arrival is noise in a digest whose whole point is that it can be read.
		var newAll = changed
			.Where(n => !closedKeys.Contains(n.Key) && BirthIsInWindow(n, windowStart, bornInBatch))
			.OrderByDescending(n => n.Created)
			.ThenBy(n => n.Key, StringComparer.Ordinal)
			.ToList();

		var cohorts = newAll
			.SelectMany(n => Areas(enrichment, n.Key).Select(area => (Area: area, Node: n)))
			.GroupBy(x => x.Area, StringComparer.Ordinal)
			.Select(g => new OwnerDigestCohort(
				g.Key,
				g.Count(),
				g.Take(limit).Select(x => Item(x.Node, statusKinds, enrichment)).ToList()))
			// Biggest theme first; the no-area bucket last however big it is — it is a fallback, not
			// a theme, and letting it lead would bury the themes the grouping exists to surface.
			.OrderBy(c => c.Area == OwnerDigestCohort.NoArea ? 1 : 0)
			.ThenByDescending(c => c.Total)
			.ThenBy(c => c.Area, StringComparer.Ordinal)
			.ToList();

		// ── (1) waiting on your decision — STATE, not the window ─────────────────────────────────
		var awaiting = await AwaitingAsync(projectKey, board, statusKinds, urlPrefix, ct);

		// ── (4) chronology, on request ───────────────────────────────────────────────────────────
		IReadOnlyList<OwnerDigestEvent>? timeline = null;
		int? timelineTotal = null;
		long currentCommentVersion = 0;
		var sinceCommentVersion = request.SinceCommentVersion ?? 0;
		if (request.IncludeTimeline)
		{
			var commentDelta = await _comments.DeltaAsync(projectKey, board, sinceCommentVersion, ct);
			currentCommentVersion = commentDelta.CurrentVersion;
			var titles = changed.ToDictionary(n => n.NodeId, n => n.Name, StringComparer.Ordinal);
			var keys = changed.ToDictionary(n => n.NodeId, n => n.Key, StringComparer.Ordinal);

			var events = changed
				.Select(n => new OwnerDigestEvent("node", n.Updated, n.Key, n.NodeId, n.Name, null, n.Status))
				.Concat(commentDelta.Added.Concat(commentDelta.Updated)
					.Where(c => windowStart is null || c.Updated >= windowStart.Value)
					.Select(c => new OwnerDigestEvent(
						"comment", c.Updated,
						keys.TryGetValue(c.NodeId, out var k) ? k : c.NodeId,
						c.NodeId,
						titles.TryGetValue(c.NodeId, out var t) ? t : "",
						c.Author,
						Excerpt(c.Body))))
				.OrderByDescending(e => e.At)
				.ThenBy(e => e.NodeKey, StringComparer.Ordinal)
				.ToList();

			timelineTotal = events.Count;
			timeline = events.Take(limit).ToList();
		}

		return new OwnerDigestView(
			Board: board,
			Kind: delta.Kind,
			SinceVersion: sinceVersion,
			CurrentVersion: currentVersion,
			SinceCommentVersion: sinceCommentVersion,
			CurrentCommentVersion: currentCommentVersion,
			WindowStart: windowStart,
			AwaitingDecision: awaiting.Take(limit).ToList(),
			AwaitingDecisionTotal: awaiting.Count,
			Closed: closedAll.Take(limit).Select(n => Item(n, statusKinds, enrichment)).ToList(),
			ClosedTotal: closedAll.Count,
			NewCohorts: cohorts,
			NewTotal: newAll.Count,
			Timeline: timeline,
			TimelineTotal: timelineTotal,
			RemovedKeys: delta.Result.Removed,
			ClosureDatingCaveat: Caveat);
	}

	// ── section (1) ──────────────────────────────────────────────────────────────────────────────

	// The decision queue, through the `decisionPending` predicate rather than a board sweep. The
	// listing statusKind default (open) is left alone deliberately: a node that has already reached
	// a terminal state is not waiting on anybody, whatever its flag still says.
	async Task<List<OwnerDigestItem>> AwaitingAsync(
		string projectKey, string board, IReadOnlyDictionary<string, StatusKind> statusKinds,
		string? urlPrefix, CancellationToken ct)
	{
		var result = await _tasks.SearchNodesAsync(projectKey, new SearchRequest<TaskNodeFilter, TaskSortBy>
		{
			Filter = new TaskNodeFilter(Board: board, DecisionPending: true),
			Sort = (TaskSortBy.Updated, true),
			Limit = 0,
			BodyLen = 0,
		}, urlPrefix, ct);

		return result.Hits.Select(h => new OwnerDigestItem(
			h.Node.Key, h.Node.NodeId, h.Node.Title, h.Node.Status,
			Facet(Kind(statusKinds, h.Node.Type, h.Node.Status)), h.Node.Type,
			h.Node.Tags, h.Node.CreatedAt ?? default, h.Node.UpdatedAt ?? default,
			h.Node.DecisionPending, h.Node.Url)).ToList();
	}

	// ── enrichment ───────────────────────────────────────────────────────────────────────────────

	sealed record NodeEnrichment(IReadOnlyList<string> Tags, string? Url);

	async Task<IReadOnlyDictionary<string, NodeEnrichment>> EnrichAsync(
		string projectKey, string board, List<string> keys, string? urlPrefix, CancellationToken ct)
	{
		if (keys.Count == 0) return new Dictionary<string, NodeEnrichment>(StringComparer.Ordinal);

		var result = await _tasks.SearchNodesAsync(projectKey, new SearchRequest<TaskNodeFilter, TaskSortBy>
		{
			Filter = new TaskNodeFilter(Board: board, Keys: keys),
			Limit = 0,
			BodyLen = 0,
		}, urlPrefix, ct);

		var map = new Dictionary<string, NodeEnrichment>(StringComparer.Ordinal);
		foreach (var hit in result.Hits)
			map[hit.Node.Key] = new NodeEnrichment(hit.Node.Tags, hit.Node.Url);
		return map;
	}

	// The `area` axis, read off the enforced tag namespace ("area:tasks" → "tasks"). A node with two
	// area tags legitimately appears in two cohorts — the same rule the board's own tag projection
	// uses (TagGroup: "a node with several tags in the namespace appears in several groups").
	static List<string> Areas(IReadOnlyDictionary<string, NodeEnrichment> enrichment, string key)
	{
		var tags = enrichment.TryGetValue(key, out var e) ? e.Tags : [];
		var areas = tags
			.Where(t => t.StartsWith(AreaPrefix, StringComparison.OrdinalIgnoreCase))
			.Select(t => t[AreaPrefix.Length..])
			.Where(v => v.Length > 0)
			.Distinct(StringComparer.Ordinal)
			.ToList();
		return areas.Count > 0 ? areas : [OwnerDigestCohort.NoArea];
	}

	const string AreaPrefix = "area:";

	static OwnerDigestItem Item(TaskNode n, IReadOnlyDictionary<string, StatusKind> statusKinds,
		IReadOnlyDictionary<string, NodeEnrichment> enrichment)
	{
		var e = enrichment.TryGetValue(n.Key, out var found) ? found : new NodeEnrichment([], null);
		return new OwnerDigestItem(
			n.Key, n.NodeId, n.Name, n.Status, Facet(Kind(statusKinds, n)), n.Type,
			e.Tags, n.Created, n.Updated, n.DecisionPending, e.Url);
	}

	// ── window / classification helpers ──────────────────────────────────────────────────────────

	static bool BirthIsInWindow(TaskNode n, DateTime? windowStart, HashSet<string> bornInBatch) =>
		windowStart is { } start ? n.Created >= start : bornInBatch.Contains(n.Key);

	static string Excerpt(string body) =>
		body.Length <= 160 ? body : body[..160] + "…";

	// (type, status) → terminal classification, from the board's OWN workflow. Keyed twice: exactly
	// by type, and by status alone as a fallback, because a board whose nodes carry no type (every
	// kind but `work`) still has to classify.
	async Task<IReadOnlyDictionary<string, StatusKind>> StatusKindMapAsync(
		string projectKey, string board, CancellationToken ct)
	{
		var workflow = await _tasks.GetBoardWorkflowAsync(projectKey, board, ct);
		var map = new Dictionary<string, StatusKind>(StringComparer.OrdinalIgnoreCase);
		foreach (var block in workflow.Workflows)
			foreach (var status in block.Workflow.Statuses)
			{
				foreach (var type in block.Types)
					map[type + " " + status.Slug] = status.Kind;
				// Status-only fallback. A collision (two types spelling the same status with
				// different kinds) resolves to the first block, which is also the order
				// tasks_workflow itself reports — a deterministic answer, not a random one.
				map.TryAdd(status.Slug, status.Kind);
			}
		return map;
	}

	static StatusKind Kind(IReadOnlyDictionary<string, StatusKind> map, TaskNode n) => Kind(map, n.Type, n.Status);

	static StatusKind Kind(IReadOnlyDictionary<string, StatusKind> map, string type, string status)
	{
		if (map.TryGetValue(type + " " + status, out var byType)) return byType;
		// Unknown to the board's vocabulary → Open. The same default TasksSearchDocs.StatusKindFacet
		// takes for an out-of-vocab legacy slug: an unclassifiable status is never reported as a
		// closure, because claiming a closure that did not happen is the worse error here.
		return map.TryGetValue(status, out var byStatus) ? byStatus : StatusKind.Open;
	}

	static string Facet(StatusKind kind) => kind.ToString().ToLowerInvariant();
}
