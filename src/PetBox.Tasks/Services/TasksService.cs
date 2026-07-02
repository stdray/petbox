using System.Diagnostics;
using System.Text.RegularExpressions;
using LinqToDB;
using LinqToDB.Data;
using LinqToDB.DataProvider.SQLite;
using PetBox.Core.Data.Temporal;
using PetBox.Core.Models;
using PetBox.Core.Observability;
using PetBox.Core.Search;
using PetBox.LlmRouter.Contract;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Data;
using PetBox.Tasks.Validation;
using PetBox.Tasks.Workflow;

namespace PetBox.Tasks.Services;

// The one implementation of ITasksService: the single door to the task store. All the
// domain logic that used to live in the MCP tool layer (spec validation, workflow
// application, blocker/spec-link invariants, delivery roll-up, FSM effects) lives here,
// so the MCP tools, Razor pages and REST share exactly one code path into the data.
public sealed partial class TasksService : ITasksService
{
	readonly ITaskBoardStore _boards;
	readonly IRelationStore _relations;
	readonly ITagStore _tags;
	readonly ICommentService _comments;
	// Optional embedding capability (DI auto-fills when an LLM router is registered).
	// Null → semantic search disabled and embed-on-write skipped (lexical-only); never throws.
	readonly ILlmClient? _llm;

	// Dependency-free declarative invariants (immutable NodeId/type). Static — no state.
	static readonly PlanNodeChangeValidator ChangeValidator = new();

	// MRL truncation dim for the vector index (must match TasksVectorizationJob) and the fusion
	// candidate depth; board/type filtering is applied by the index, so SearchK bounds the result.
	const int VectorDim = 1024;
	const int SearchK = 50;

	public TasksService(ITaskBoardStore boards, IRelationStore relations, ITagStore tags, ICommentService comments, ILlmClient? llm = null)
	{
		_boards = boards;
		_relations = relations;
		_tags = tags;
		_comments = comments;
		_llm = llm;
	}

	// ---- board lifecycle ----

	// The methodology kinds are per-project singletons (the quartet); `simple` is unlimited.
	static readonly BoardKind[] Methodological = [BoardKind.Spec, BoardKind.Ideas, BoardKind.Intake, BoardKind.Work];

	public async Task<TaskBoardMeta> CreateBoardAsync(string projectKey, string board, string? kind, string? description, string? specBoard, CancellationToken ct = default)
	{
		var k = WorkflowCatalog.ParseKind(kind ?? "simple");
		await AssertSingletonAsync(projectKey, k, ct);
		await ValidateSpecBoardAsync(projectKey, kind ?? "simple", specBoard, ct);
		var meta = await _boards.CreateAsync(projectKey, board, description, kind ?? "simple", specBoard, ct);
		await AutoWireSpecAsync(projectKey, ct); // a fresh spec or work board may complete the link
		return meta;
	}

	// Reject a 2nd active board of a methodology kind (one-per-project). `free` is exempt.
	async Task AssertSingletonAsync(string projectKey, BoardKind kind, CancellationToken ct)
	{
		if (!Methodological.Contains(kind)) return;
		var existing = (await _boards.ListAsync(projectKey, ct))
			.FirstOrDefault(b => b.ClosedAt == null && WorkflowCatalog.ParseKind(b.Kind) == kind);
		if (existing is not null)
			throw new ArgumentException($"project '{projectKey}' already has an active {kind.ToString().ToLowerInvariant()} board ('{existing.Name}') — the methodology quartet is one-per-project; close it (tasks.board_close) or use a simple board");
	}

	// When exactly one active spec and one active work board exist and the work board has no
	// spec link, wire it automatically — so the agent need not call board_set_spec by hand.
	async Task AutoWireSpecAsync(string projectKey, CancellationToken ct)
	{
		var boards = await _boards.ListAsync(projectKey, ct);
		var spec = boards.SingleOrDefault(b => b.ClosedAt == null && WorkflowCatalog.ParseKind(b.Kind) == BoardKind.Spec);
		var work = boards.SingleOrDefault(b => b.ClosedAt == null && WorkflowCatalog.ParseKind(b.Kind) == BoardKind.Work);
		if (spec is not null && work is not null && string.IsNullOrWhiteSpace(work.SpecBoard))
			await _boards.UpdateAsync(projectKey, work.Name, m => m with { SpecBoard = spec.Name }, ct);
	}

	public async Task<(bool Set, string? SpecBoard)> SetSpecBoardAsync(string projectKey, string board, string? specBoard, CancellationToken ct = default)
	{
		await EnsureBoard(projectKey, board, ct);
		var meta = (await _boards.FindAsync(projectKey, board, ct))!;
		await ValidateSpecBoardAsync(projectKey, meta.Kind, specBoard, ct);
		var norm = string.IsNullOrWhiteSpace(specBoard) ? null : specBoard;
		var set = await _boards.UpdateAsync(projectKey, board, m => m with { SpecBoard = norm }, ct);
		return (set, norm);
	}

	public Task<IReadOnlyList<TaskBoardMeta>> ListBoardsAsync(string projectKey, CancellationToken ct = default) =>
		_boards.ListAsync(projectKey, ct);

	public Task<bool> DeleteBoardAsync(string projectKey, string board, CancellationToken ct = default) =>
		_boards.DeleteAsync(projectKey, board, ct);

	public Task<bool> SetClosedAsync(string projectKey, string board, bool closed, CancellationToken ct = default) =>
		_boards.UpdateAsync(projectKey, board, m => m with { ClosedAt = closed ? DateTime.UtcNow : null }, ct);

	public Task<bool> BoardExistsAsync(string projectKey, string board, CancellationToken ct = default) =>
		_boards.ExistsAsync(projectKey, board, ct);

	public async Task<BoardKind> ResolveKindAsync(string projectKey, string board, CancellationToken ct = default)
	{
		await EnsureBoard(projectKey, board, ct);
		return WorkflowCatalog.ParseKind((await _boards.FindAsync(projectKey, board, ct))!.Kind);
	}

	// Pipeline order of the quartet kinds.
	static readonly BoardKind[] Quartet = [BoardKind.Intake, BoardKind.Ideas, BoardKind.Spec, BoardKind.Work];

	public async Task<MethodologyView> EnableMethodologyAsync(string projectKey, CancellationToken ct = default)
	{
		var boards = await _boards.ListAsync(projectKey, ct);
		foreach (var kind in Quartet)
		{
			if (boards.Any(b => b.ClosedAt == null && WorkflowCatalog.ParseKind(b.Kind) == kind)) continue;
			var name = kind.ToString().ToLowerInvariant();
			if (await _boards.ExistsAsync(projectKey, name, ct)) continue; // name taken by another board; leave it
			await CreateBoardAsync(projectKey, name, name, $"methodology {name}", null, ct);
		}
		await AutoWireSpecAsync(projectKey, ct);
		return await GetMethodologyAsync(projectKey, ct: ct);
	}

	public Task<string?> ResolveWorkspaceAsync(string projectKey, CancellationToken ct = default) =>
		_boards.FindProjectWorkspaceAsync(projectKey, ct);

	static readonly IReadOnlyDictionary<string, int> EmptyCounts = new Dictionary<string, int>();

	// The quartet as one COMPACT INDEX. By default each node is a header row (no body);
	// `bodyLen > 0` slices the first N chars of each body into the row, `includeBoards` (kind
	// names) restricts which quartet boards are returned. `Enabled` reflects true provisioning
	// (all four exist) regardless of the filter.
	public async Task<MethodologyView> GetMethodologyAsync(string projectKey, int bodyLen = 0, string[]? includeBoards = null, string? urlPrefix = null, CancellationToken ct = default)
	{
		var want = ResolveBoardFilter(includeBoards); // null = all quartet boards
		var boards = await _boards.ListAsync(projectKey, ct);
		var result = new List<MethodologyBoard>(Quartet.Length);
		var all = true;
		foreach (var kind in Quartet)
		{
			var b = boards.FirstOrDefault(x => x.ClosedAt == null && WorkflowCatalog.ParseKind(x.Kind) == kind);
			if (b is null) all = false; // existence is a global fact, independent of the filter
			if (want is not null && !want.Contains(kind)) continue;
			var kindName = kind.ToString().ToLowerInvariant();
			if (b is null) { result.Add(new MethodologyBoard(kindName, null, EmptyCounts, [])); continue; }
			var view = await GetAsync(projectKey, b.Name, includeClosed: false, urlPrefix: urlPrefix, ct: ct);
			var headers = view.Nodes.Select(n => ToHeader(n, bodyLen)).ToList();
			var counts = view.Nodes
				.GroupBy(n => n.Status, StringComparer.Ordinal)
				.ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
			result.Add(new MethodologyBoard(kindName, b.Name, counts, headers));
		}
		return new MethodologyView(all, result);
	}

	// Project the full node view down to an index header, slicing the body to `bodyLen`.
	static PlanNodeHeader ToHeader(PlanNodeView n, int bodyLen) => new(
		n.Key, n.NodeId, n.ParentNodeId, n.ParentSlug, n.Depth,
		n.Status, n.Type, n.Title, n.Priority,
		SliceBody(n.Body, bodyLen), n.Delivery,
		n.Spec, n.BlockedBy, n.LinkedTasks, n.Supersedes, n.Tags, n.Url);

	// bodyLen <= 0 -> no body (pure index). Otherwise the first N chars, with "…" appended
	// when the body was cut. Char-based and predictable (no word-boundary cleverness).
	static string? SliceBody(string? body, int bodyLen)
	{
		if (bodyLen <= 0 || string.IsNullOrEmpty(body)) return null;
		return body.Length <= bodyLen ? body : string.Concat(body.AsSpan(0, bodyLen), "…");
	}

	// Map include_boards (quartet kind names) to a BoardKind set; null/empty = all. An
	// unknown name is rejected (it would otherwise silently return nothing).
	static HashSet<BoardKind>? ResolveBoardFilter(string[]? includeBoards)
	{
		if (includeBoards is null || includeBoards.Length == 0) return null;
		var set = new HashSet<BoardKind>();
		foreach (var raw in includeBoards)
		{
			var name = (raw ?? "").Trim();
			var match = Quartet.Cast<BoardKind?>().FirstOrDefault(k => k!.Value.ToString().Equals(name, StringComparison.OrdinalIgnoreCase));
			if (match is null)
				throw new ArgumentException($"includeBoards: '{raw}' is not a quartet board (valid: {string.Join("|", Quartet.Select(k => k.ToString().ToLowerInvariant()))})");
			set.Add(match.Value);
		}
		return set;
	}

	// A specBoard link only makes sense on a work board and must point at an existing spec board.
	async Task ValidateSpecBoardAsync(string projectKey, string kind, string? specBoard, CancellationToken ct)
	{
		if (string.IsNullOrWhiteSpace(specBoard)) return;
		if (WorkflowCatalog.ParseKind(kind) != BoardKind.Work)
			throw new ArgumentException($"specBoard applies only to a work board (this board's kind is '{kind}')");
		var target = await _boards.FindAsync(projectKey, specBoard, ct)
			?? throw new ArgumentException($"spec board '{specBoard}' not found in project '{projectKey}'");
		if (WorkflowCatalog.ParseKind(target.Kind) != BoardKind.Spec)
			throw new ArgumentException($"'{specBoard}' is not a spec board (kind is '{target.Kind}')");
	}

	async Task EnsureBoard(string projectKey, string board, CancellationToken ct)
	{
		if (!await _boards.ExistsAsync(projectKey, board, ct))
			throw new InvalidOperationException($"task board '{board}' not found in project '{projectKey}'");
	}

	// ---- read: tree view ----

	public async Task<PlanBoardView> GetAsync(string projectKey, string board, bool includeClosed = false, string? under = null, string? urlPrefix = null, CancellationToken ct = default)
	{
		await EnsureBoard(projectKey, board, ct);

		var ctx = _boards.GetContext(projectKey);
		var all = ctx.PlanNodes.Where(n => n.Board == board).ToList();
		var lineage = BuildLineage(all);
		var active = all.Where(n => n.ActiveTo == null).OrderBy(n => n.Priority).ThenBy(n => n.Key).ToList();
		var current = all.Count == 0 ? 0 : all.Max(n => n.Version);

		var meta = (await _boards.FindAsync(projectKey, board, ct))!;
		var kind = WorkflowCatalog.ParseKind(meta.Kind);
		var parentOf = await ParentMapAsync(projectKey, ct);
		var delivery = kind == BoardKind.Spec ? await ComputeSpecDeliveryAsync(projectKey, active, parentOf, ct) : null;

		var index = await BuildNodeIndexAsync(projectKey, ct);
		var tagsByNode = await _tags.BoardTagsAsync(projectKey, board, ct);
		var underId = ResolveUnderNodeId(under, active);
		var visible = FilterVisible(active, includeClosed, underId, parentOf);

		var nodes = new List<PlanNodeView>();
		foreach (var n in visible)
		{
			var fromEdges = n.NodeId.Length > 0 ? await _relations.ListAsync(projectKey, n.NodeId, "from", ct: ct) : [];
			var toEdges = n.NodeId.Length > 0 ? await _relations.ListAsync(projectKey, n.NodeId, "to", ct: ct) : [];
			var spec = fromEdges.Where(e => e.Kind == "task_spec").Select(e => LinkRef(e.ToNodeId, index)).ToList();
			var blockedBy = toEdges.Where(e => e.Kind == "blocks").Select(e => LinkRef(e.FromNodeId, index)).ToList();
			var linkedTasks = kind == BoardKind.Spec ? toEdges.Where(e => e.Kind == "task_spec").Select(e => LinkRef(e.FromNodeId, index)).ToList() : null;
			var supersedes = fromEdges.Where(e => e.Kind == "supersedes").Select(e => LinkRef(e.ToNodeId, index)).ToList();
			var parentId = parentOf.GetValueOrDefault(n.NodeId);
			nodes.Add(new PlanNodeView(
				Key: n.Key,
				NodeId: n.NodeId,
				ParentNodeId: parentId,
				ParentSlug: parentId is not null && index.TryGetValue(parentId, out var pr) ? pr.Slug : null,
				Depth: DepthOf(n.NodeId, parentOf),
				Status: n.Status,
				Type: n.Type,
				Title: n.Name,
				Body: n.Body,
				CommitRef: n.CommitRef,
				Priority: n.Priority,
				Version: n.Version,
				Delivery: delivery is not null && delivery.TryGetValue(n.NodeId, out var dv) ? dv : null,
				Spec: spec.Count > 0 ? spec : null,
				BlockedBy: blockedBy.Count > 0 ? blockedBy : null,
				LinkedTasks: linkedTasks is { Count: > 0 } ? linkedTasks : null,
				Supersedes: supersedes.Count > 0 ? supersedes : null,
				RenamedFrom: lineage.TryGetValue(n.Key, out var p) ? p : [],
				Tags: tagsByNode[n.NodeId].OrderBy(t => t, StringComparer.Ordinal).ToList(),
				// Canonical slug-URL: prefix ends with "/tasks/", append "{board}/{slug}"
				// (node-slug-addressable). board/key are validated slugs → URL-safe.
				Url: urlPrefix is null ? null : urlPrefix + board + "/" + n.Key));
		}
		return new PlanBoardView(current, kind.ToString().ToLowerInvariant(), meta.SpecBoard, nodes);
	}

	public async Task<NodeDetailView?> GetNodeAsync(string projectKey, string nodeId, CancellationToken ct = default)
	{
		if (string.IsNullOrWhiteSpace(nodeId)) return null;
		var board = await _boards.FindBoardByNodeIdAsync(projectKey, nodeId, ct);
		if (board is null) return null;

		// Reuse GetAsync (includeClosed: the node or its ancestors may be terminal) — it builds
		// the fully-enriched view (links, delivery, parent/depth) we'd otherwise duplicate.
		var view = await GetAsync(projectKey, board, includeClosed: true, ct: ct);
		var node = view.Nodes.FirstOrDefault(n => n.NodeId == nodeId);
		if (node is null) return null;

		// Walk part_of up via ParentNodeId (same-board, single-parent, cycle-guarded) to build
		// the breadcrumb chain, then reverse to root→parent order.
		var byId = view.Nodes.ToDictionary(n => n.NodeId, StringComparer.Ordinal);
		var ancestors = new List<NodeCrumb>();
		var cur = node.ParentNodeId; var guard = 0;
		while (cur is not null && byId.TryGetValue(cur, out var p) && guard++ < 1000)
		{
			ancestors.Add(new NodeCrumb(p.NodeId, p.Key, p.Title));
			cur = p.ParentNodeId;
		}
		ancestors.Reverse();
		return new NodeDetailView(board, view.Kind, node, ancestors);
	}

	public async Task<NodeDetailView?> GetNodeBySlugAsync(string projectKey, string board, string slug, CancellationToken ct = default)
	{
		if (string.IsNullOrWhiteSpace(board) || string.IsNullOrWhiteSpace(slug)) return null;
		var nodeId = await _boards.FindNodeIdBySlugAsync(projectKey, board, slug, ct);
		if (nodeId is null) return null;
		return await GetNodeAsync(projectKey, nodeId, ct);
	}

	// nodeId -> its active part_of parent nodeId (single parent). One query, project-wide.
	async Task<Dictionary<string, string>> ParentMapAsync(string projectKey, CancellationToken ct) =>
		(await _relations.ListByKindAsync(projectKey, "part_of", ct))
			.GroupBy(e => e.FromNodeId, StringComparer.Ordinal)
			.ToDictionary(g => g.Key, g => g.First().ToNodeId, StringComparer.Ordinal);

	// Distance from a root along part_of (0 = root). Guarded against cycles.
	static int DepthOf(string nodeId, Dictionary<string, string> parentOf)
	{
		var d = 0; var cur = nodeId; var guard = 0;
		while (parentOf.TryGetValue(cur, out var par) && guard++ < 1000) { d++; cur = par; }
		return d;
	}

	// Resolve `under` (a flat slug on this board) to its nodeId, or null if absent/unset.
	static string? ResolveUnderNodeId(string? under, List<PlanNode> active)
	{
		if (string.IsNullOrWhiteSpace(under)) return null;
		var slug = TaskSlug.Validate(under);
		return active.FirstOrDefault(n => n.Key == slug)?.NodeId;
	}

	// Tag-projection: bucket the board's active nodes by their tag value in each `groupBy`
	// namespace, nested in dimension order (e.g. [area, concern] → area buckets, each split
	// by concern). A node with several values in a dimension lands in several buckets;
	// untagged nodes go to "(none)" (ordered last). Every group — at every level — carries a
	// delivery roll-up over its nodes (spec boards). This is a VIEW: part_of is never touched
	// (tag-grouping-is-projection).
	public async Task<GroupedBoardView> GetGroupedAsync(string projectKey, string board, IReadOnlyList<string> groupBy, CancellationToken ct = default)
	{
		await EnsureBoard(projectKey, board, ct);
		var dims = (groupBy ?? []).Select(d => (d ?? "").Trim().ToLowerInvariant()).Where(d => d.Length > 0).ToList();
		if (dims.Count == 0)
			throw new ArgumentException($"groupBy needs at least one tag namespace ({string.Join("|", TagStore.Namespaces)})");
		foreach (var ns in dims)
			if (!TagStore.Namespaces.Contains(ns))
				throw new ArgumentException($"groupBy must be tag namespaces ({string.Join("|", TagStore.Namespaces)}); got '{ns}'");

		var ctx = _boards.GetContext(projectKey);
		var active = ctx.PlanNodes.Where(n => n.Board == board && n.ActiveTo == null).ToList();
		var meta = (await _boards.FindAsync(projectKey, board, ct))!;
		var kind = WorkflowCatalog.ParseKind(meta.Kind);
		var tagsByNode = await _tags.BoardTagsAsync(projectKey, board, ct);
		var delivery = kind == BoardKind.Spec
			? await ComputeSpecDeliveryAsync(projectKey, active, await ParentMapAsync(projectKey, ct), ct)
			: null;

		var groups = ProjectByTags(active, dims, 0, tagsByNode, delivery);
		return new GroupedBoardView(dims, kind.ToString().ToLowerInvariant(), groups);
	}

	// Recursively bucket `nodes` by dimension `dims[depth]`, then by the remaining dimensions.
	// The final dimension yields leaf groups (NodeKeys filled, SubGroups empty); earlier ones
	// yield nesting groups (SubGroups filled, NodeKeys empty). Each group's delivery rolls up
	// over all its nodes regardless of depth.
	static List<TagGroup> ProjectByTags(
		List<PlanNode> nodes, IReadOnlyList<string> dims, int depth,
		ILookup<string, string> tagsByNode, IReadOnlyDictionary<string, string>? delivery)
	{
		var prefix = dims[depth] + ":";
		var buckets = new Dictionary<string, List<PlanNode>>(StringComparer.Ordinal);
		foreach (var n in nodes)
		{
			var vals = tagsByNode[n.NodeId].Where(t => t.StartsWith(prefix, StringComparison.Ordinal)).ToList();
			foreach (var key in vals.Count > 0 ? vals : ["(none)"])
				(buckets.TryGetValue(key, out var l) ? l : buckets[key] = []).Add(n);
		}

		var last = depth == dims.Count - 1;
		return buckets
			.OrderBy(b => b.Key == "(none)" ? 1 : 0) // "(none)" last
			.ThenBy(b => b.Key, StringComparer.Ordinal)
			.Select(b => new TagGroup(
				b.Key,
				delivery is null ? null : CombineDelivery(b.Value.Select(n => delivery.GetValueOrDefault(n.NodeId))),
				last ? b.Value.OrderBy(n => n.Priority).ThenBy(n => n.Key, StringComparer.Ordinal).Select(n => n.Key).ToList() : [],
				last ? [] : ProjectByTags(b.Value, dims, depth + 1, tagsByNode, delivery)))
			.ToList();
	}

	// Roll up a group's per-node delivery into one status. not_started if all are (or none);
	// done only if all done; done_with_defects if all terminal with a defect; else in_progress.
	static string? CombineDelivery(IEnumerable<string?> deliveries)
	{
		var ds = deliveries.Where(d => d is not null).Select(d => d!).ToList();
		if (ds.Count == 0 || ds.All(d => d == "not_started")) return "not_started";
		if (ds.All(d => d == "done")) return "done";
		if (ds.All(d => d is "done" or "done_with_defects")) return "done_with_defects";
		return "in_progress";
	}

	public Task<IReadOnlyList<PlanNode>> ListActiveNodesAsync(string projectKey, string board, CancellationToken ct = default)
	{
		var ctx = _boards.GetContext(projectKey);
		IReadOnlyList<PlanNode> active = ctx.PlanNodes.Where(n => n.Board == board && n.ActiveTo == null).ToList();
		return Task.FromResult(active);
	}

	// Hide terminal (closed) nodes unless includeClosed; keep terminal part_of ancestors of
	// any visible node so the tree stays connected. `underId` restricts to a part_of subtree.
	static List<PlanNode> FilterVisible(List<PlanNode> active, bool includeClosed, string? underId, Dictionary<string, string> parentOf)
	{
		IEnumerable<PlanNode> scoped = active;
		if (underId is not null)
			scoped = active.Where(n => InSubtree(n.NodeId, underId, parentOf));
		var pool = scoped.ToList();
		if (includeClosed) return pool;

		var keep = new HashSet<string>(StringComparer.Ordinal); // nodeIds
		foreach (var n in pool.Where(n => !WorkflowCatalog.IsTerminalSlug(n.Status)))
		{
			keep.Add(n.NodeId);
			var cur = n.NodeId; var guard = 0;
			while (parentOf.TryGetValue(cur, out var par) && guard++ < 1000) { keep.Add(par); cur = par; }
		}
		return pool.Where(n => keep.Contains(n.NodeId)).ToList();
	}

	// True if `nodeId` is `rootId` or a part_of descendant of it (walk parents up to root).
	static bool InSubtree(string nodeId, string rootId, Dictionary<string, string> parentOf)
	{
		var cur = nodeId; var guard = 0;
		while (true)
		{
			if (cur == rootId) return true;
			if (!parentOf.TryGetValue(cur, out var par) || guard++ >= 1000) return false;
			cur = par;
		}
	}

	// A resolvable reference to a node anywhere in the project (links cross boards).
	sealed record NodeRef(string Board, string BoardKind, string Slug, string Title, string Status, string Type);

	// nodeId -> NodeRef across every board in the project (links bind to nodeId, which is
	// globally unique, so a link target may live on another board).
	async Task<Dictionary<string, NodeRef>> BuildNodeIndexAsync(string projectKey, CancellationToken ct)
	{
		var index = new Dictionary<string, NodeRef>(StringComparer.Ordinal);
		var ctx = _boards.GetContext(projectKey);
		foreach (var b in await _boards.ListAsync(projectKey, ct))
			foreach (var n in ctx.PlanNodes.Where(x => x.Board == b.Name && x.ActiveTo == null).ToList())
				if (n.NodeId.Length > 0)
					index[n.NodeId] = new NodeRef(b.Name, b.Kind, n.Key, n.Name, n.Status, n.Type);
		return index;
	}

	static LinkDto LinkRef(string nodeId, Dictionary<string, NodeRef> index) =>
		index.TryGetValue(nodeId, out var r)
			? new LinkDto(nodeId, r.Board, r.Slug, r.Title, r.Status)
			: new LinkDto(nodeId, null, null, null, "missing");

	// COMPUTED spec roll-up (keyed by NodeId): a spec node's delivery derives from the
	// tasks linked (task_spec) to it AND its part_of descendants (decomposition may cross
	// boards). Replaces the old path-prefix descent.
	async Task<Dictionary<string, string>> ComputeSpecDeliveryAsync(string projectKey, IReadOnlyList<PlanNode> specNodes, Dictionary<string, string> parentOf, CancellationToken ct)
	{
		var byNodeId = new Dictionary<string, (string Type, string Status)>(StringComparer.Ordinal);
		var ctx = _boards.GetContext(projectKey);
		foreach (var b in await _boards.ListAsync(projectKey, ct))
			foreach (var n in ctx.PlanNodes.Where(x => x.Board == b.Name && x.ActiveTo == null).ToList())
				if (n.NodeId.Length > 0) byNodeId[n.NodeId] = (n.Type, n.Status);

		// childrenOf (invert part_of) and tasksOf (inbound task_spec), each one query.
		var childrenOf = new Dictionary<string, List<string>>(StringComparer.Ordinal);
		foreach (var (child, parent) in parentOf)
			(childrenOf.TryGetValue(parent, out var l) ? l : childrenOf[parent] = []).Add(child);
		var tasksOf = (await _relations.ListByKindAsync(projectKey, "task_spec", ct))
			.GroupBy(e => e.ToNodeId, StringComparer.Ordinal)
			.ToDictionary(g => g.Key, g => g.Select(e => e.FromNodeId).ToList(), StringComparer.Ordinal);

		var result = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var s in specNodes)
		{
			// BFS the spec node's part_of subtree, union each node's inbound tasks.
			var taskIds = new HashSet<string>(StringComparer.Ordinal);
			var stack = new Stack<string>(); stack.Push(s.NodeId); var guard = 0;
			var seen = new HashSet<string>(StringComparer.Ordinal);
			while (stack.Count > 0 && guard++ < 100000)
			{
				var cur = stack.Pop();
				if (!seen.Add(cur)) continue;
				if (tasksOf.TryGetValue(cur, out var ts)) foreach (var t in ts) taskIds.Add(t);
				if (childrenOf.TryGetValue(cur, out var kids)) foreach (var k in kids) stack.Push(k);
			}
			result[s.NodeId] = Delivery(taskIds.Where(byNodeId.ContainsKey).Select(id => byNodeId[id]).ToList());
		}
		return result;
	}

	static string Delivery(List<(string Type, string Status)> tasks)
	{
		var features = tasks.Where(t => t.Type == "feature").ToList();
		if (features.Count == 0) return "not_started";
		if (!features.All(f => WorkflowCatalog.KindOfSlug(f.Status) == StatusKind.TerminalOk)) return "in_progress";
		var openBug = tasks.Any(t => t.Type == "bug" && WorkflowCatalog.KindOfSlug(t.Status) == StatusKind.Open);
		return openBug ? "done_with_defects" : "done";
	}

	// ---- write: upsert ----

	public async Task<UpsertOutcome> UpsertAsync(string projectKey, string board, IReadOnlyList<NodePatch> nodes, long sinceVersion = 0, CancellationToken ct = default)
	{
		using var op = PetBoxActivitySources.Tasks.StartActivity("tasks.upsert");
		op?.SetTag("petbox.project", projectKey);
		op?.SetTag("petbox.board", board);
		op?.SetTag("petbox.node_count", nodes.Count);

		await _boards.EnsureAsync(projectKey, board, ct); // auto-vivify on first write
		var meta = (await _boards.FindAsync(projectKey, board, ct))!;
		if (meta.ClosedAt != null)
			throw new InvalidOperationException($"board '{board}' is closed — reopen it (tasks.board_reopen) before writing");

		var kind = WorkflowCatalog.ParseKind(meta.Kind);
		var ctx = _boards.GetContext(projectKey);
		var prior = ctx.PlanNodes.Where(n => n.Board == board && n.ActiveTo == null).ToList()
			.ToDictionary(n => n.Key, n => n, StringComparer.Ordinal);

		// Split the batch: a deleted patch carries only Key + Version (a temporal-close, no new
		// revision), so everything downstream — workflow, guards, links, tags, partOf, supersedes —
		// is built from the upsert patches only. That also means a spec-node delete needs no
		// ideaRef: erasing junk is not a spec change (retiring a real requirement is `deprecated`).
		var deletePatches = nodes.Where(p => p.Deleted).DistinctBy(p => p.Key, StringComparer.Ordinal).ToList();
		var upsertPatches = nodes.Where(p => !p.Deleted).ToList();
		if (deletePatches.Count > 0)
		{
			if (deletePatches.Any(p => p.PrevKey is not null))
				throw new ArgumentException("a node cannot be renamed and deleted in the same patch");
			var both = deletePatches.Select(p => p.Key)
				.Intersect(upsertPatches.Select(p => p.Key), StringComparer.Ordinal).FirstOrDefault();
			if (both is not null)
				throw new ArgumentException($"node '{both}' is both deleted and upserted in one batch — pick one");
		}

		var desired = upsertPatches.Select(p => ApplyWorkflow(kind, Merge(p, prior), prior) with { Board = board }).ToArray();
		ValidateChanges(desired, prior);
		// Resolve slug specRefs to NodeIds ONCE, up front — both the validation below and the
		// task_spec edge write (LinkRefsAsync) read this map, so it must never carry a raw slug.
		var specRefs = ResolveSpecRefs(projectKey, meta, LinkFields(upsertPatches, p => p.SpecRef));
		var blockedBy = LinkFields(upsertPatches, p => p.BlockedBy);
		var ideaRefs = LinkFields(upsertPatches, p => p.IdeaRef);
		using (PetBoxActivitySources.Tasks.StartActivity("tasks.upsert.guards"))
		{
			RequireSpecLinks(kind, desired, prior, specRefs);
			await ValidateSpecRefsAsync(projectKey, meta, specRefs, ct);
			await RequireBlockersAsync(kind, projectKey, desired, blockedBy, ct);
			await RequireSpecPlanForReviewAsync(kind, projectKey, board, desired, prior, ct);
			await RequireAcceptedIdeaForSpecAsync(kind, projectKey, desired, ideaRefs, ct);
		}

		// Children guard: a node with active part_of children is not deletable — unless its
		// children die in the SAME batch (so a subtree can go in one call, bottom-up implied).
		// Refusals ride the conflict shape (applied:false, nothing written), not an exception,
		// so the caller gets the per-key reason plus the fresh delta to rebase on.
		var dels = new List<(string Key, long Version)>();
		if (deletePatches.Count > 0)
		{
			var guardConflicts = new List<TemporalConflict>();
			var dyingIds = deletePatches
				.Select(p => prior.GetValueOrDefault(p.Key)?.NodeId)
				.Where(id => !string.IsNullOrEmpty(id)).Cast<string>()
				.ToHashSet(StringComparer.Ordinal);
			foreach (var p in deletePatches)
			{
				if (!prior.TryGetValue(p.Key, out var row))
					continue; // idempotent: nothing active to delete
				if ((await ActivePartOfChildrenAsync(projectKey, row.NodeId, ct)).Any(c => !dyingIds.Contains(c)))
					guardConflicts.Add(new(p.Key, TemporalConflictKind.Rejected, p.Version, row.Version,
						"node has active part_of children — delete them first (or in the same batch)"));
				else
					dels.Add((p.Key, p.Version));
			}
			if (guardConflicts.Count > 0)
			{
				// Not applied; still serve the cursor contract (the delta since sinceVersion).
				var delta = await TemporalStore.UpsertAsync(ctx, Array.Empty<PlanNode>(), sinceVersion,
					partition: n => n.Board == board, ct: ct);
				return new UpsertOutcome(delta with { Applied = false, Conflicts = guardConflicts }, kind);
			}
		}
		// Class-A lexical floor written INSIDE the entity tx: open nodes (re)indexed, terminal/
		// removed nodes dropped (search covers only the open set), committing/rolling back with the
		// entity. Tags read in-tx are the pre-upsert set; SetTagsAsync (below) is reflected by the
		// post-commit RefreshFtsTagsAsync. Vectors are materialized by the worker, not here.
		var fts = new SqliteFtsIndex(() => ctx);
		TemporalUpsertResult<PlanNode> r;
		using (PetBoxActivitySources.Tasks.StartActivity("tasks.upsert.temporal"))
			r = await TemporalStore.UpsertAsync(ctx, desired, dels, sinceVersion,
				onWithinTx: async (tx, upserted, deletedKeys, c) =>
				{
					var tags = await NodeTagsAsync(tx, board, upserted.Where(TasksSearchDocs.IsIndexable).Select(n => n.NodeId), c);
					foreach (var n in upserted)
						if (TasksSearchDocs.IsIndexable(n))
							await fts.IndexAsync(tx, TasksSearchDocs.ToDoc(n, projectKey, tags.GetValueOrDefault(n.NodeId, [])), c);
						else
							await fts.DeleteAsync(tx, projectKey, board, n.Key, c); // left the open set
					foreach (var key in deletedKeys)
						await fts.DeleteAsync(tx, projectKey, board, key, c);
				},
				partition: n => n.Board == board, ct: ct);
		if (r.Applied)
			using (PetBoxActivitySources.Tasks.StartActivity("tasks.upsert.links"))
			{
				await _boards.TouchAsync(projectKey, board, ct);
				await LinkRefsAsync(projectKey, "task_spec", desired, specRefs, blockerIsFrom: false, ct);
				await LinkRefsAsync(projectKey, "blocks", desired, blockedBy, blockerIsFrom: true, ct);
				await LinkRefsAsync(projectKey, "idea_spec", desired, ideaRefs, blockerIsFrom: true, ct);
				await CloseBlocksOnLeaveAsync(projectKey, desired, prior, ct);
				await RunDoneEffectsAsync(projectKey, kind, desired, ct);
				await RunDeleteEffectsAsync(projectKey, board, deletePatches, prior, ct);
			}
		// Tags + part_of are node metadata, not a content revision — apply whenever the
		// upsert did not conflict (so a pure tag/parent change on an unchanged node still
		// takes effect; on a no-op the NodeId in `desired` is the existing one).
		if (r.Conflicts.Count == 0)
			using (PetBoxActivitySources.Tasks.StartActivity("tasks.upsert.meta"))
			{
				await SetTagsAsync(projectKey, board, kind, upsertPatches, desired, ct);
				await SetPartOfAsync(projectKey, board, upsertPatches, desired, ct);
				await SetSupersedesAsync(projectKey, board, upsertPatches, desired, ct);
			}
		// Refresh the FTS Tags column now that SetTagsAsync (above) has run: the in-tx index wrote
		// content + pre-upsert tags transactionally; re-index this batch's open nodes with the
		// now-current tags. Content/membership are already committed with the entity; vectors are
		// materialized off the write path by the async-vectorization worker.
		if (r.Applied)
			using (PetBoxActivitySources.Tasks.StartActivity("tasks.upsert.fts-tags"))
				await RefreshFtsTagsAsync(ctx, projectKey, board, desired, ct);
		return new UpsertOutcome(r, kind);
	}

	public async Task<UpsertOutcome> DeltaAsync(string projectKey, string board, long sinceVersion, CancellationToken ct = default)
	{
		await EnsureBoard(projectKey, board, ct);
		var meta = (await _boards.FindAsync(projectKey, board, ct))!;
		var ctx = _boards.GetContext(projectKey);
		var r = await TemporalStore.UpsertAsync(ctx, Array.Empty<PlanNode>(), sinceVersion, partition: n => n.Board == board, ct: ct);
		return new UpsertOutcome(r, WorkflowCatalog.ParseKind(meta.Kind));
	}

	// ---- read: hybrid search ----

	public async Task<TaskSearchResult> SearchAsync(string projectKey, string query, string? board = null, bool? lexical = null, bool? semantic = null, string? urlPrefix = null, CancellationToken ct = default)
	{
		var boardFilter = string.IsNullOrWhiteSpace(board) ? null : board;
		if (boardFilter is not null) await EnsureBoard(projectKey, boardFilter, ct);
		var ctx = _boards.GetContext(projectKey);
		await EnsureLexicalBackfillAsync(ctx, projectKey, ct);

		// Hybrid search behind the contract: Class-A lexical floor ⊕ Class-B vectors, RRF-fused with
		// provenance by the facade. Entity address is (Scope=project, Type=board, Id=slug); the board
		// filter is a SearchFilter(Type=board). Toggles preserved: no embedder / semantic:false omits
		// the vector index (semantic=false, not degraded); a query-time embed failure is caught by
		// the facade and flagged degraded.
		Func<DataConnection> connect = () => _boards.NewConnection(projectKey);
		var indexes = new List<ISearchIndex>();
		if (lexical != false)
			indexes.Add(new SqliteFtsIndex(connect));
		if (semantic != false && _llm is not null)
			indexes.Add(new VectorSearchIndex(connect, new LlmClientEmbedder(_llm, projectKey), VectorDim));

		var resp = await new SearchService(indexes).SearchAsync(projectKey, query, new SearchFilter(boardFilter), SearchK, ct);
		if (resp.Hits.Count == 0) return new TaskSearchResult([], resp.Retrievers);

		// Hits carry Type=board, Id=slug — group by board (no need to look up each id's board),
		// build each owning board's enriched view once, pick the matched nodes by slug, order by
		// fused relevance.
		var order = new Dictionary<(string Board, string Slug), int>();
		for (var i = 0; i < resp.Hits.Count; i++)
			order[(resp.Hits[i].Type, resp.Hits[i].Id)] = i;

		var hits = new List<TaskSearchHit>();
		foreach (var g in resp.Hits.GroupBy(h => h.Type, StringComparer.Ordinal))
		{
			var view = await GetAsync(projectKey, g.Key, includeClosed: false, urlPrefix: urlPrefix, ct: ct);
			var slugs = g.Select(h => h.Id).ToHashSet(StringComparer.Ordinal);
			foreach (var n in view.Nodes.Where(n => slugs.Contains(n.Key)))
				hits.Add(new TaskSearchHit(g.Key, n));
		}
		var ordered = hits.OrderBy(h => order.GetValueOrDefault((h.Board, h.Node.Key), int.MaxValue)).ToList();
		return new TaskSearchResult(ordered, resp.Retrievers);
	}

	// Read-merge a patch against the prior active row: a field omitted from the patch
	// (null) inherits the prior value; a non-null value sets it ("" clears it).
	static PlanNode Merge(NodePatch p, IReadOnlyDictionary<string, PlanNode> prior)
	{
		var cur = prior.GetValueOrDefault(p.Key) ?? (p.PrevKey is not null ? prior.GetValueOrDefault(p.PrevKey) : null);
		return new PlanNode
		{
			Key = p.Key,
			Version = p.Version,
			Status = p.Status ?? cur?.Status ?? string.Empty,
			Type = (p.Type ?? cur?.Type ?? string.Empty).ToLowerInvariant(),
			Name = p.Title ?? cur?.Name ?? string.Empty,
			Body = p.Body ?? cur?.Body ?? string.Empty,
			CommitRef = p.CommitRefSet ? p.CommitRef : cur?.CommitRef,
			Priority = p.Priority ?? cur?.Priority ?? 0,
			PrevKey = p.PrevKey,
		};
	}

	static Dictionary<string, string> LinkFields(IReadOnlyList<NodePatch> nodes, Func<NodePatch, string?> pick)
	{
		var map = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var p in nodes)
		{
			var v = pick(p);
			if (!string.IsNullOrWhiteSpace(v)) map[p.Key] = v!;
		}
		return map;
	}

	// Idea Review gate (idea-review-needs-plan): an idea may not enter `review` without an
	// `artifact:spec_plan` comment (the spec-update plan) on it. WorkflowEngine stays pure
	// (kind/type/from/to); this guard lives here because it reads comments (ICommentService).
	// Only the plan precondition is enforced — approval of review→accepted stays a
	// convention (enforceApproval is off). NodeId comes from the prior row (desired rows get
	// their NodeId assigned inside the temporal upsert, after this check).
	async Task RequireSpecPlanForReviewAsync(
		BoardKind kind, string projectKey, string board, PlanNode[] desired,
		IReadOnlyDictionary<string, PlanNode> prior, CancellationToken ct)
	{
		if (kind != BoardKind.Ideas) return;
		foreach (var d in desired)
		{
			if (!string.Equals(d.Status, "review", StringComparison.OrdinalIgnoreCase)) continue;
			var p = prior.GetValueOrDefault(d.Key) ?? (d.PrevKey is not null ? prior.GetValueOrDefault(d.PrevKey) : null);
			if (p is not null && string.Equals(p.Status, "review", StringComparison.OrdinalIgnoreCase)) continue; // already in review
			if (p is null || p.NodeId.Length == 0)
				throw new InvalidOperationException($"idea '{d.Key}' can't be created directly in review — create it, add an artifact:spec_plan comment, then move it to review");
			var comments = await _comments.ListForNodeAsync(projectKey, board, p.NodeId, ct);
			var hasPlan = comments.Any(c => c.Tags.Any(t => string.Equals(t, "artifact:spec_plan", StringComparison.OrdinalIgnoreCase)));
			if (!hasPlan)
				throw new InvalidOperationException($"idea '{d.Key}' can't enter review without an artifact:spec_plan comment (the spec-update plan)");
		}
	}

	// Default status, assign/carry the stable NodeId (new = fresh, edit = keep, rename =
	// inherit from source), and validate status/transition — the single workflow point.
	static PlanNode ApplyWorkflow(BoardKind kind, PlanNode node, Dictionary<string, PlanNode> prior)
	{
		var type = node.Type.Length == 0 ? null : node.Type;
		// Simple boards: type is a label from a small fixed set; empty defaults to `task`, and an
		// out-of-set type is rejected. (Work validates type via For(); Simple's For() ignores type,
		// so the vocabulary is enforced here.)
		if (kind == BoardKind.Simple)
		{
			type ??= "task";
			if (!WorkflowCatalog.SimpleTypes.Contains(type))
				throw new ArgumentException($"invalid type '{type}' for a simple board; valid: {WorkflowCatalog.ValidTypes(kind)}");
			node = node with { Type = type };
		}
		var wf = WorkflowCatalog.For(kind, type);
		var n = node.Status.Length > 0 ? node : node with { Status = wf?.Initial ?? "Pending" };

		var current = prior.GetValueOrDefault(n.Key);
		var source = n.PrevKey is not null ? prior.GetValueOrDefault(n.PrevKey) : null;
		var nodeId = current?.NodeId is { Length: > 0 } cid ? cid
			: source?.NodeId is { Length: > 0 } sid ? sid
			: Guid.NewGuid().ToString("N");
		n = n with { NodeId = nodeId };

		var from = current?.Status ?? source?.Status;
		var res = WorkflowEngine.Validate(kind, type, from, n.Status, hasReason: !string.IsNullOrWhiteSpace(n.Body));
		if (!res.Ok) throw new ArgumentException(res.Error);
		return n;
	}

	// Declarative immutable-field invariants (NodeId/type once set). The prior row is the
	// active node at this key, or — for a rename — at its PrevKey; null means a new node.
	static void ValidateChanges(PlanNode[] desired, Dictionary<string, PlanNode> prior)
	{
		foreach (var n in desired)
		{
			var old = prior.GetValueOrDefault(n.Key)
				?? (n.PrevKey is not null ? prior.GetValueOrDefault(n.PrevKey) : null);
			var result = ChangeValidator.Validate(new EntityChange<PlanNode>(old, n));
			if (!result.IsValid)
				throw new ArgumentException(result.Errors[0].ErrorMessage);
		}
	}

	// specRef accepts the spec node's slug OR its NodeId, mirroring part_of (ResolveParentId).
	// A slug resolves against the board's linked spec board (SpecBoard); the returned map holds
	// NodeIds only. NodeId-shaped values pass through untouched (existing behavior).
	Dictionary<string, string> ResolveSpecRefs(string projectKey, TaskBoardMeta board, Dictionary<string, string> specRefs)
	{
		if (specRefs.Count == 0) return specRefs;
		var ctx = _boards.GetContext(projectKey);
		foreach (var (key, raw) in specRefs.ToList())
		{
			var v = raw.Trim();
			if (LooksLikeNodeId(v)) { specRefs[key] = v; continue; }
			if (board.SpecBoard is not { Length: > 0 } sb)
				throw new ArgumentException($"specRef '{raw}' (node '{key}') is a slug, but this board has no linked spec board — provide the spec node's NodeId");
			var slug = v.ToLowerInvariant();
			var target = ctx.PlanNodes.Where(n => n.Board == sb && n.ActiveTo == null && n.Key == slug).ToList().FirstOrDefault();
			if (target is null || target.NodeId.Length == 0)
				throw new ArgumentException($"specRef '{raw}' (node '{key}') does not match any node on spec board '{sb}'");
			specRefs[key] = target.NodeId;
		}
		return specRefs;
	}

	// A NodeId is a 32-hex Guid ("N"); a slug starts [a-z] and can't be 32 hex chars in
	// practice — the two are trivially distinguishable.
	static bool LooksLikeNodeId(string v) => v.Length == 32 && v.All(Uri.IsHexDigit);

	// Validate each specRef target: it must resolve to a node on a spec board, and (if
	// this work board has a SpecBoard set) on that specific board.
	async Task ValidateSpecRefsAsync(string projectKey, TaskBoardMeta workBoard, Dictionary<string, string> specRefs, CancellationToken ct)
	{
		if (specRefs.Count == 0) return;
		var index = await BuildNodeIndexAsync(projectKey, ct);
		foreach (var (key, refId) in specRefs)
		{
			if (!index.TryGetValue(refId, out var t))
				throw new ArgumentException($"specRef '{refId}' (node '{key}') does not resolve to any node");
			if (WorkflowCatalog.ParseKind(t.BoardKind) != BoardKind.Spec)
				throw new ArgumentException($"specRef '{refId}' (node '{key}') points to board '{t.Board}', which is not a spec board");
			if (workBoard.SpecBoard is { Length: > 0 } sb && t.Board != sb)
				throw new ArgumentException($"specRef '{refId}' (node '{key}') is on board '{t.Board}', but this work board links spec board '{sb}'");
		}
	}

	// spec-write-needs-accepted-idea (governance): every create/change of a spec node must
	// reference an `accepted` idea via ideaRef — which becomes the idea_spec edge (linked
	// after apply). No accepted idea, no spec write. WorkflowEngine stays pure; the idea
	// node is read here.
	async Task RequireAcceptedIdeaForSpecAsync(BoardKind kind, string projectKey, PlanNode[] desired, Dictionary<string, string> ideaRefs, CancellationToken ct)
	{
		if (kind != BoardKind.Spec) return;
		var index = await BuildNodeIndexAsync(projectKey, ct);
		foreach (var n in desired)
		{
			if (!ideaRefs.TryGetValue(n.Key, out var ideaId))
				throw new ArgumentException($"a spec change must be made under an accepted idea — provide ideaRef (node '{n.Key}')");
			if (!index.TryGetValue(ideaId, out var idea))
				throw new ArgumentException($"ideaRef '{ideaId}' (node '{n.Key}') does not resolve to any node");
			if (WorkflowCatalog.ParseKind(idea.BoardKind) != BoardKind.Ideas)
				throw new ArgumentException($"ideaRef '{ideaId}' (node '{n.Key}') is not an idea (board '{idea.Board}')");
			if (!string.Equals(idea.Status, "accepted", StringComparison.OrdinalIgnoreCase))
				throw new ArgumentException($"ideaRef '{ideaId}' (node '{n.Key}') idea is '{idea.Status}', not accepted — a spec change needs an accepted idea");
		}
	}

	// FSM effect: when a work node reaches a TerminalOk status, auto-close any intake
	// issue that spawned it and unblock tasks it was blocking. System action (no gate).
	async Task RunDoneEffectsAsync(string projectKey, BoardKind kind, PlanNode[] desired, CancellationToken ct)
	{
		if (kind != BoardKind.Work) return;
		foreach (var n in desired.Where(n => WorkflowCatalog.KindOfSlug(n.Status) == StatusKind.TerminalOk))
		{
			foreach (var e in (await _relations.ListAsync(projectKey, n.NodeId, "to", ct: ct)).Where(e => e.Kind == "issue_task"))
				await SetActiveNodeStatusAsync(projectKey, e.FromNodeId,
					(wf, node) => WorkflowCatalog.IsTerminalSlug(node.Status) ? null : wf?.Statuses.FirstOrDefault(s => s.Kind == StatusKind.TerminalOk)?.Slug, ct);

			foreach (var e in (await _relations.ListAsync(projectKey, n.NodeId, "from", ct: ct)).Where(e => e.Kind == "blocks"))
			{
				await _relations.CloseAsync(projectKey, "blocks", e.FromNodeId, e.ToNodeId, ct);
				var stillBlocked = (await _relations.ListAsync(projectKey, e.ToNodeId, "to", ct: ct)).Any(x => x.Kind == "blocks");
				if (!stillBlocked)
					await SetActiveNodeStatusAsync(projectKey, e.ToNodeId,
						(_, node) => string.Equals(node.Status, "Blocked", StringComparison.OrdinalIgnoreCase) ? "InProgress" : null, ct);
			}
		}
	}

	// NodeIds of this node's part_of children whose own row is still active. Terminal-status
	// children count too — they are active rows and would dangle just the same.
	async Task<IReadOnlyList<string>> ActivePartOfChildrenAsync(string projectKey, string nodeId, CancellationToken ct)
	{
		if (nodeId.Length == 0) return [];
		var childIds = (await _relations.ListAsync(projectKey, nodeId, "to", ct: ct))
			.Where(e => e.Kind == "part_of").Select(e => e.FromNodeId).ToList();
		if (childIds.Count == 0) return [];
		var ctx = _boards.GetContext(projectKey);
		return childIds.Where(id => ctx.PlanNodes.Any(n => n.ActiveTo == null && n.NodeId == id)).ToList();
	}

	// Delete effect: a temporal-closed node must not leave dangling structure behind — close
	// every edge touching it (both directions, any kind) and its tags. Unblocking mirrors the
	// Done effect: when the deleted node was a blocker, a target left with no blockers moves
	// Blocked → InProgress. System action (no gate).
	async Task RunDeleteEffectsAsync(string projectKey, string board, IReadOnlyList<NodePatch> deletePatches, Dictionary<string, PlanNode> prior, CancellationToken ct)
	{
		foreach (var p in deletePatches)
		{
			if (!prior.TryGetValue(p.Key, out var row) || row.NodeId.Length == 0) continue;
			foreach (var e in await _relations.ListAsync(projectKey, row.NodeId, "both", ct: ct))
			{
				await _relations.DeleteAsync(projectKey, e.Id, ct);
				if (e.Kind == "blocks" && e.FromNodeId == row.NodeId)
				{
					var stillBlocked = (await _relations.ListAsync(projectKey, e.ToNodeId, "to", ct: ct)).Any(x => x.Kind == "blocks");
					if (!stillBlocked)
						await SetActiveNodeStatusAsync(projectKey, e.ToNodeId,
							(_, node) => string.Equals(node.Status, "Blocked", StringComparison.OrdinalIgnoreCase) ? "InProgress" : null, ct);
				}
			}
			// An empty list REPLACES the node's full tag set — i.e. soft-closes every active tag.
			await _tags.SetAsync(projectKey, board, row.NodeId, [], ct: ct);
		}
	}

	// Find the active node with this NodeId across the project's boards and move it to a
	// target status chosen by `pick` (null = leave as-is). System action (no gate).
	async Task SetActiveNodeStatusAsync(string projectKey, string nodeId, Func<PetBox.Tasks.Workflow.Workflow?, PlanNode, string?> pick, CancellationToken ct)
	{
		// NodeId is unique across the project, so find the active row directly in the one
		// project file; its Board tells us which partition to write back into.
		var ctx = _boards.GetContext(projectKey);
		var node = ctx.PlanNodes.Where(x => x.ActiveTo == null && x.NodeId == nodeId).ToList().FirstOrDefault();
		if (node is null) return;
		var meta = await _boards.FindAsync(projectKey, node.Board, ct);
		var wf = WorkflowCatalog.For(WorkflowCatalog.ParseKind(meta?.Kind), node.Type.Length == 0 ? null : node.Type);
		var target = pick(wf, node);
		if (target is null || string.Equals(target, node.Status, StringComparison.OrdinalIgnoreCase)) return;
		await TemporalStore.UpsertAsync(ctx, new[] { node with { Status = target } }, partition: n => n.Board == node.Board, ct: ct);
		await _boards.TouchAsync(projectKey, node.Board, ct);
	}

	// Create relation edges from a per-node field after the upsert applies. task_spec
	// (specRef): task -> spec. blocks (blockedBy): blocker -> task. Idempotent.
	async Task LinkRefsAsync(string projectKey, string kind, PlanNode[] desired, Dictionary<string, string> refs, bool blockerIsFrom, CancellationToken ct)
	{
		if (refs.Count == 0) return;
		var byKey = desired.ToDictionary(n => n.Key, n => n.NodeId, StringComparer.Ordinal);
		foreach (var (key, other) in refs)
			if (byKey.TryGetValue(key, out var nid) && nid.Length > 0)
			{
				var (from, to) = blockerIsFrom ? (other, nid) : (nid, other);
				await _relations.CreateAsync(projectKey, kind, from, to, ct);
			}
	}

	// Apply enforced tags after the upsert. A patch whose Tags is null OMITS tags (leave
	// as-is); a non-null list (incl. empty) is the new full set for that node. Tags bind
	// to the node's stable NodeId.
	async Task SetTagsAsync(string projectKey, string board, BoardKind kind, IReadOnlyList<NodePatch> patches, PlanNode[] desired, CancellationToken ct)
	{
		// Simple boards take free-form tags (any namespace + bare words); methodology boards
		// keep the enforced area:/concern: namespaces.
		var enforceNs = kind != BoardKind.Simple;
		var nodeIdOf = desired.ToDictionary(n => n.Key, n => n.NodeId, StringComparer.Ordinal);
		foreach (var p in patches)
		{
			if (p.Tags is null) continue;
			if (nodeIdOf.TryGetValue(p.Key, out var nid) && nid.Length > 0)
				await _tags.SetAsync(projectKey, board, nid, p.Tags, enforceNs, ct);
		}
	}

	// Apply part_of (vertical decomposition) after the upsert. A patch whose PartOf is null
	// OMITS it (leave as-is); "" DETACHES (make a root); otherwise sets the parent (a slug
	// on this board or a NodeId). Enforces a single active parent and rejects cycles.
	async Task SetPartOfAsync(string projectKey, string board, IReadOnlyList<NodePatch> patches, PlanNode[] desired, CancellationToken ct)
	{
		if (!patches.Any(p => p.PartOf is not null)) return;
		var byKey = desired.ToDictionary(n => n.Key, n => n.NodeId, StringComparer.Ordinal);
		var ctx = _boards.GetContext(projectKey);
		// Slug -> nodeId for parent resolution on this board: active rows overlaid with this batch.
		var slugToId = ctx.PlanNodes.Where(n => n.Board == board && n.ActiveTo == null).ToList()
			.Where(n => n.NodeId.Length > 0).ToDictionary(n => n.Key, n => n.NodeId, StringComparer.Ordinal);
		foreach (var (k, nid) in byKey) slugToId[k] = nid;
		var parentOf = await ParentMapAsync(projectKey, ct);

		foreach (var p in patches)
		{
			if (p.PartOf is null) continue;
			if (!byKey.TryGetValue(p.Key, out var childId) || childId.Length == 0) continue;

			// Single parent: close any existing part_of from this child first.
			if (parentOf.TryGetValue(childId, out var oldParent))
			{
				await _relations.CloseAsync(projectKey, "part_of", childId, oldParent, ct);
				parentOf.Remove(childId);
			}
			if (p.PartOf.Length == 0) continue; // detach only

			var parentId = ResolveParentId(p.PartOf, slugToId, ctx);
			if (InSubtree(parentId, childId, parentOf)) // parent is the child or its descendant → cycle
				throw new ArgumentException($"part_of would create a cycle (node '{p.Key}')");
			await _relations.CreateAsync(projectKey, "part_of", childId, parentId, ct);
			parentOf[childId] = parentId;
		}
	}

	// Apply supersedes after the upsert: the new node replaces another, which is moved to
	// its kind's terminal-cancel (obsoleted). A system effect (no approve gate), like the
	// Done effects. Self-supersede and a missing target are ignored.
	async Task SetSupersedesAsync(string projectKey, string board, IReadOnlyList<NodePatch> patches, PlanNode[] desired, CancellationToken ct)
	{
		if (!patches.Any(p => !string.IsNullOrWhiteSpace(p.Supersedes))) return;
		var byKey = desired.ToDictionary(n => n.Key, n => n.NodeId, StringComparer.Ordinal);
		var ctx = _boards.GetContext(projectKey);
		var slugToId = ctx.PlanNodes.Where(n => n.Board == board && n.ActiveTo == null).ToList()
			.Where(n => n.NodeId.Length > 0).ToDictionary(n => n.Key, n => n.NodeId, StringComparer.Ordinal);
		foreach (var (k, nid) in byKey) slugToId[k] = nid;

		foreach (var p in patches)
		{
			if (string.IsNullOrWhiteSpace(p.Supersedes)) continue;
			if (!byKey.TryGetValue(p.Key, out var newId) || newId.Length == 0) continue;
			var targetId = ResolveParentId(p.Supersedes, slugToId, ctx);
			if (targetId == newId) continue; // a node can't supersede itself
			await _relations.CreateAsync(projectKey, "supersedes", newId, targetId, ct);
			// Obsolete the superseded node: move it to its workflow's terminal-cancel status.
			await SetActiveNodeStatusAsync(projectKey, targetId,
				(wf, node) => WorkflowCatalog.IsTerminalSlug(node.Status) ? null : wf?.Statuses.FirstOrDefault(s => s.Kind == StatusKind.TerminalCancel)?.Slug, ct);
		}
	}

	// Resolve a PartOf value to a parent NodeId: a slug on this board, else an existing
	// NodeId (cross-board allowed — the project file holds every board's nodes).
	static string ResolveParentId(string partOf, Dictionary<string, string> slugToId, TasksDb ctx)
	{
		var v = partOf.Trim();
		if (slugToId.TryGetValue(v.ToLowerInvariant(), out var bySlug)) return bySlug;
		if (ctx.PlanNodes.Any(n => n.ActiveTo == null && n.NodeId == v)) return v;
		throw new ArgumentException($"part_of parent '{partOf}' is neither a node key on this board nor a known NodeId");
	}

	// Invariant: a work task in `Blocked` must name a blocker (blockedBy in this call, or
	// an already-active `blocks` edge into it). "Blocked requires a link."
	async Task RequireBlockersAsync(BoardKind kind, string projectKey, PlanNode[] desired, Dictionary<string, string> blockedBy, CancellationToken ct)
	{
		if (kind != BoardKind.Work) return;
		foreach (var n in desired)
		{
			if (!string.Equals(n.Status, "Blocked", StringComparison.OrdinalIgnoreCase)) continue;
			if (blockedBy.ContainsKey(n.Key)) continue;
			var hasActiveBlocker = (await _relations.ListAsync(projectKey, n.NodeId, "to", ct: ct)).Any(e => e.Kind == "blocks");
			if (!hasActiveBlocker)
				throw new ArgumentException($"a Blocked task must name a blocker — provide blockedBy (node '{n.Key}')");
		}
	}

	// Leaving Blocked manually closes the active `blocks` edges into the node (history kept).
	async Task CloseBlocksOnLeaveAsync(string projectKey, PlanNode[] desired, Dictionary<string, PlanNode> prior, CancellationToken ct)
	{
		foreach (var n in desired)
		{
			var wasBlocked = prior.TryGetValue(n.Key, out var cur) && string.Equals(cur.Status, "Blocked", StringComparison.OrdinalIgnoreCase);
			if (!wasBlocked || string.Equals(n.Status, "Blocked", StringComparison.OrdinalIgnoreCase)) continue;
			foreach (var e in (await _relations.ListAsync(projectKey, n.NodeId, "to", ct: ct)).Where(e => e.Kind == "blocks"))
				await _relations.CloseAsync(projectKey, "blocks", e.FromNodeId, e.ToNodeId, ct);
		}
	}

	// Invariant: a NEW work feature/bug must link a spec node (specRef). Edits don't re-require
	// it, and `chore` (engineering hygiene below the spec) is exempt by design.
	static void RequireSpecLinks(BoardKind kind, PlanNode[] desired, Dictionary<string, PlanNode> prior, Dictionary<string, string> specRefs)
	{
		if (kind != BoardKind.Work) return;
		foreach (var n in desired)
		{
			if (n.Type is not ("feature" or "bug")) continue;
			var isNew = !prior.ContainsKey(n.Key) && (n.PrevKey is null || !prior.ContainsKey(n.PrevKey));
			if (isNew && !specRefs.ContainsKey(n.Key))
				throw new ArgumentException($"a work {n.Type} must link a spec node — provide specRef (node '{n.Key}')");
		}
	}

	// Active node key -> chain of prior keys it was renamed from.
	static Dictionary<string, List<string>> BuildLineage(List<PlanNode> all)
	{
		var edge = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var g in all.GroupBy(n => n.Key, StringComparer.Ordinal))
		{
			var birth = g.OrderBy(n => n.Version).First();
			if (!string.IsNullOrEmpty(birth.PrevKey))
				edge[g.Key] = birth.PrevKey!;
		}

		var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
		foreach (var key in edge.Keys)
		{
			var chain = new List<string>();
			var cur = key;
			var guard = 0;
			while (edge.TryGetValue(cur, out var prev) && guard++ < 1000)
			{
				chain.Add(prev);
				cur = prev;
			}
			result[key] = chain;
		}
		return result;
	}

	// ---- UI quick-add ----

	public async Task QuickAddAsync(string projectKey, string board, string name, string? body, long priority, CancellationToken ct = default)
	{
		if (string.IsNullOrWhiteSpace(name)) return;

		// Status/type must fit the board's kind — a cold "Pending" is invalid on kinded
		// boards (ideas/spec/intake). Use the kind's initial status + its node type.
		var meta = await _boards.FindAsync(projectKey, board, ct);
		var kind = WorkflowCatalog.ParseKind(meta?.Kind);
		var type = kind switch
		{
			BoardKind.Ideas => "idea",
			BoardKind.Spec => "spec",
			BoardKind.Intake => "issue",
			BoardKind.Simple => "task", // simple empty-type default (mirrors ApplyWorkflow)
			_ => "",
		};
		var status = WorkflowCatalog.For(kind, type.Length == 0 ? null : type)?.Initial ?? "Pending";

		var key = GenKey(name);
		var ctx = _boards.GetContext(projectKey);
		// Assign a stable NodeId so the node can be linked (relations/specRef) later.
		await TemporalStore.UpsertAsync(ctx, new[]
		{
			new PlanNode { Board = board, Key = key, NodeId = Guid.NewGuid().ToString("N"), Version = 0, Status = status, Type = type, Name = name.Trim(), Body = body?.Trim() ?? string.Empty, Priority = priority },
		}, partition: n => n.Board == board, ct: ct);
		await _boards.TouchAsync(projectKey, board, ct);
	}

	static string GenKey(string name)
	{
		var ascii = Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
		if (ascii.Length == 0 || !char.IsLetter(ascii[0])) ascii = "task-" + ascii;
		ascii = ascii.Trim('-');
		if (ascii.Length > 32) ascii = ascii[..32].Trim('-');
		return $"{ascii}-{Guid.NewGuid():N}"[..(Math.Min(ascii.Length, 32) + 7)];
	}

	// ---- system: report.issue ----

	public async Task<string> ReportIssueAsync(string project, string board, string title, string body, CancellationToken ct = default)
	{
		if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("title is required");
		var key = IssueSlug(title);
		await _boards.EnsureAsync(project, board, ct); // auto-create the triage board on first report
		var ctx = _boards.GetContext(project);
		var r = await TemporalStore.UpsertAsync(ctx, new[]
		{
			// Assign a stable NodeId here: this path writes straight to TemporalStore and skips
			// ApplyWorkflow (the usual NodeId-assignment point), so without this the row lands with
			// an empty NodeId and the /tasks/{board}/{slug} permalink 404s (slug→NodeId→GetNode).
			// The issues board is a `simple` board → its preset vocab (Todo|InProgress|…), not the
			// intake `reported`. Type `issue` is in the simple type set.
			new PlanNode { Board = board, Key = key, NodeId = Guid.NewGuid().ToString("N"), Version = 0, Status = "Todo", Type = "issue", Name = title.Trim(), Body = body, Priority = 50 },
		}, partition: n => n.Board == board, ct: ct);
		if (r.Applied) await _boards.TouchAsync(project, board, ct);
		return key;
	}

	// ---- search seam: Class-A FTS tag refresh + lexical backfill ----

	// Re-index this batch's OPEN nodes with their now-current tags. The in-tx write (onWithinTx in
	// UpsertAsync) already committed content + membership transactionally with pre-upsert tags; this
	// post-commit pass reflects SetTagsAsync into the FTS Tags column. Targeted (not a wholesale
	// board rebuild) so it only re-writes the changed nodes and never empties the board's index.
	static async Task RefreshFtsTagsAsync(TasksDb ctx, string projectKey, string board, IReadOnlyList<PlanNode> desired, CancellationToken ct)
	{
		var open = desired.Where(TasksSearchDocs.IsIndexable).ToList();
		if (open.Count == 0) return;
		var tags = await NodeTagsAsync(ctx, board, open.Select(n => n.NodeId), ct);
		var fts = new SqliteFtsIndex(() => ctx);
		using var tx = await ctx.BeginTransactionAsync(ct);
		try
		{
			foreach (var n in open)
				await fts.IndexAsync(ctx, TasksSearchDocs.ToDoc(n, projectKey, tags.GetValueOrDefault(n.NodeId, [])), ct);
			await tx.CommitAsync(ct);
		}
		catch
		{
			await tx.RollbackAsync(ct);
			throw;
		}
	}

	// One-time lexical backfill: nodes written before the search retrofit have no search_fts rows.
	// Cheap, count-guarded, runs at most once per project file — rebuilds the OPEN set across every
	// board from the same projection the write seam uses.
	static async Task EnsureLexicalBackfillAsync(TasksDb ctx, string scope, CancellationToken ct)
	{
		if (ctx.Execute<long>("SELECT count(*) FROM search_fts") > 0) return;
		var open = ctx.PlanNodes.Where(n => n.ActiveTo == null).ToList()
			.Where(TasksSearchDocs.IsIndexable).ToList();
		if (open.Count == 0) return;

		var tagsByNode = (await ctx.GetTable<NodeTag>().Where(t => t.ValidTo == null)
				.Select(t => new { t.NodeId, t.Tag }).ToListAsync(ct))
			.GroupBy(r => r.NodeId).ToDictionary(g => g.Key, g => g.Select(x => x.Tag).ToList());

		var fts = new SqliteFtsIndex(() => ctx);
		using var tx = await ctx.BeginTransactionAsync(ct);
		try
		{
			foreach (var n in open)
				await fts.IndexAsync(ctx, TasksSearchDocs.ToDoc(n, scope, tagsByNode.GetValueOrDefault(n.NodeId, [])), ct);
			await tx.CommitAsync(ct);
		}
		catch
		{
			await tx.RollbackAsync(ct);
			throw;
		}
	}

	// Active (ValidTo == null) tags for the given nodes on a board, read on the supplied connection.
	static async Task<Dictionary<string, List<string>>> NodeTagsAsync(DataConnection db, string board, IEnumerable<string> nodeIds, CancellationToken ct)
	{
		var ids = nodeIds.Distinct().ToList();
		if (ids.Count == 0) return [];
		var rows = await db.GetTable<NodeTag>()
			.Where(t => t.Board == board && t.ValidTo == null && ids.Contains(t.NodeId))
			.Select(t => new { t.NodeId, t.Tag }).ToListAsync(ct);
		return rows.GroupBy(r => r.NodeId).ToDictionary(g => g.Key, g => g.Select(x => x.Tag).ToList());
	}

	[GeneratedRegex("[^a-z0-9]+")]
	private static partial Regex NonSlug();

	static string IssueSlug(string title)
	{
		var s = NonSlug().Replace(title.ToLowerInvariant(), "-").Trim('-');
		if (s.Length == 0 || !char.IsLetter(s[0])) s = "issue-" + s;
		if (s.Length > 32) s = s[..32].Trim('-');
		return $"{s}-{Guid.NewGuid():N}"[..(s.Length + 1 + 6)];
	}
}
