using PetBox.Tasks.Contract;
using PetBox.Tasks.Data;

namespace PetBox.Tasks.Services.NodeRef;

// Shared node-identity resolution core: slug-or-32-hex NodeId addressing with explicit
// policies (Strict / SoftNull / MultiHit / BatchRename). TasksService public methods stay as
// thin wrappers so ITasksService is unchanged.
public sealed class NodeRefResolver
{
	readonly ITaskBoardStore _boards;

	public NodeRefResolver(ITaskBoardStore boards) => _boards = boards;

	// A NodeId is a 32-hex Guid ("N"); a slug starts [a-z] and can't be 32 hex chars in
	// practice — the two are trivially distinguishable.
	public static bool LooksLikeNodeId(string v) => v.Length == 32 && v.All(Uri.IsHexDigit);

	// NodeRefPolicy.Strict — write-addressing / tasks_node_get: miss and ambiguity throw.
	public Task<string> ResolveStrictAsync(string projectKey, string nodeRef, string? board = null, CancellationToken ct = default)
	{
		_ = ct;
		var v = (nodeRef ?? "").Trim();
		if (v.Length == 0)
			throw new ArgumentException("node reference is required — a node slug or a 32-hex NodeId");
		if (LooksLikeNodeId(v)) return Task.FromResult(v);
		var matches = FindActiveBySlug(projectKey, v.ToLowerInvariant(), board);
		return matches.Count switch
		{
			1 => Task.FromResult(matches[0].NodeId),
			0 => throw new ArgumentException(board is null
				? $"node '{nodeRef}' does not match any active node in project '{projectKey}' — pass a node slug or a 32-hex NodeId"
				: $"node '{nodeRef}' does not match any active node on board '{board}' in project '{projectKey}' — pass a slug on this board or a 32-hex NodeId"),
			_ => throw new ArgumentException(
				$"ambiguous slug '{nodeRef}' — found on boards: [{string.Join(", ", matches.Select(m => m.Board).OrderBy(b => b, StringComparer.Ordinal))}]; pass the node's NodeId instead"),
		};
	}

	// NodeRefPolicy.SoftNull — soft single-node reads: miss → null; ambiguity still throws.
	public Task<string?> ResolveSoftNullAsync(string projectKey, string nodeRef, string? board = null, CancellationToken ct = default)
	{
		_ = ct;
		var v = (nodeRef ?? "").Trim();
		if (v.Length == 0) return Task.FromResult<string?>(null);
		if (LooksLikeNodeId(v)) return Task.FromResult<string?>(v);
		var matches = FindActiveBySlug(projectKey, v.ToLowerInvariant(), board);
		return matches.Count switch
		{
			0 => Task.FromResult<string?>(null),
			1 => Task.FromResult<string?>(matches[0].NodeId),
			_ => throw new ArgumentException(
				$"ambiguous slug '{nodeRef}' — found on boards: [{string.Join(", ", matches.Select(m => m.Board).OrderBy(b => b, StringComparer.Ordinal))}]; pass the node's NodeId instead"),
		};
	}

	// NodeRefPolicy.MultiHit — soft multi-hit id list for search surfaces. Never throws: empty
	// input → empty; NodeId-shaped → single passthrough (existence is the caller's check);
	// slug → every active match's NodeId (caller enriches + orders).
	public IReadOnlyList<string> ResolveMultiHitIds(string projectKey, string identifier, string? board = null)
	{
		var v = (identifier ?? "").Trim();
		if (v.Length == 0) return [];
		if (LooksLikeNodeId(v)) return [v];
		return FindActiveBySlug(projectKey, v.ToLowerInvariant(), board)
			.Select(n => n.NodeId)
			.ToList();
	}

	// NodeRefPolicy.BatchRename — batch `[[slug]]` mention resolution with PrevKey lineage.
	// Current key beats former key of the same spelling; ambiguous (2+ boards) or miss omitted.
	public Task<IReadOnlyDictionary<string, NodeRefResolution>> ResolveBatchRenameAsync(
		string projectKey, IReadOnlyCollection<string> slugs, CancellationToken ct = default)
	{
		_ = ct;
		var wanted = new HashSet<string>(StringComparer.Ordinal);
		foreach (var s in slugs)
		{
			var v = s?.Trim().ToLowerInvariant();
			if (!string.IsNullOrEmpty(v)) wanted.Add(v);
		}
		var empty = (IReadOnlyDictionary<string, NodeRefResolution>)new Dictionary<string, NodeRefResolution>(StringComparer.Ordinal);
		if (wanted.Count == 0) return Task.FromResult(empty);

		// All revisions across every board (history is needed to trace former slugs). A minimal
		// projection keeps the read cheap; ordering/grouping is done in memory.
		using var ctx = _boards.NewEnsuredConnection(projectKey);
		var rows = ctx.PlanNodes
			.Select(n => new { n.Board, n.Key, n.NodeId, n.Name, n.PrevKey, n.Version, n.ActiveTo })
			.ToList();

		// Per-(board,key) birth edge: the earliest revision's PrevKey (the slug it renamed FROM).
		var edge = new Dictionary<(string Board, string Key), string>();
		foreach (var g in rows.GroupBy(r => (r.Board, r.Key)))
		{
			var birth = g.OrderBy(r => r.Version).First();
			if (!string.IsNullOrEmpty(birth.PrevKey))
				edge[(g.Key.Board, g.Key.Key)] = birth.PrevKey!;
		}

		// A wanted slug matched as a CURRENT key beats the same spelling matched as a FORMER key
		// (the live node is the natural target). Each map value is the list of matching active
		// nodes; a >1 count means the slug is ambiguous across boards → dropped below.
		var currentHits = new Dictionary<string, List<NodeRefResolution>>(StringComparer.Ordinal);
		var formerHits = new Dictionary<string, List<NodeRefResolution>>(StringComparer.Ordinal);
		static void AddHit(Dictionary<string, List<NodeRefResolution>> map, string slug, NodeRefResolution hit)
		{
			if (!map.TryGetValue(slug, out var list)) map[slug] = list = new List<NodeRefResolution>();
			list.Add(hit);
		}

		foreach (var a in rows.Where(r => r.ActiveTo == null && r.NodeId.Length > 0))
		{
			var hit = new NodeRefResolution(a.Board, a.Key, a.NodeId, a.Name);
			if (wanted.Contains(a.Key))
				AddHit(currentHits, a.Key, hit);
			// Walk this node's rename chain back through its former keys.
			var cur = a.Key;
			var guard = 0;
			while (edge.TryGetValue((a.Board, cur), out var prev) && guard++ < 1000)
			{
				if (wanted.Contains(prev))
					AddHit(formerHits, prev, hit);
				cur = prev;
			}
		}

		var result = new Dictionary<string, NodeRefResolution>(StringComparer.Ordinal);
		foreach (var slug in wanted)
		{
			var hits = currentHits.TryGetValue(slug, out var c) ? c
				: formerHits.TryGetValue(slug, out var f) ? f : null;
			if (hits is { Count: 1 })
				result[slug] = hits[0];
		}
		return Task.FromResult((IReadOnlyDictionary<string, NodeRefResolution>)result);
	}

	// Active rows matching a slug key, optionally board-scoped; empty NodeId rows dropped.
	List<PlanNode> FindActiveBySlug(string projectKey, string slug, string? board)
	{
		using var ctx = _boards.NewEnsuredConnection(projectKey);
		var q = ctx.PlanNodes.Where(n => n.ActiveTo == null && n.Key == slug);
		if (board is not null) q = q.Where(n => n.Board == board);
		return q.ToList().Where(n => n.NodeId.Length > 0).ToList();
	}
}
