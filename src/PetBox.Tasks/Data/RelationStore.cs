using LinqToDB;
using LinqToDB.Async;
using PetBox.Core.Data;

namespace PetBox.Tasks.Data;

// Project-level store of typed edges between node ids. Lives in the PER-PROJECT tasks file
// (tasks/{project}.db) via IScopedDbFactory<TasksDb>, in the same file as plan_nodes — that
// co-location is what lets the endpoints carry a real FK (relations-in-project-db). The
// `projectKey` argument is the FILE selector, not a column: edges are strictly intra-project
// and there is no ProjectKey column any more.
//
// Edges bind to the stable NodeId, so they survive node renames. Interval-temporal: an edge
// is active while ClosedAt is null; delete/effects soft-close it (history kept). Mirrors
// TagStore (the other per-project, node-keyed, SCD-2 association store).
public interface IRelationStore
{
	Task<Relation> CreateAsync(string projectKey, string kind, string fromNodeId, string toNodeId, CancellationToken ct = default);
	// Batch form of CreateAsync for the relations_create atomic:true contract: ONE connection,
	// ONE transaction for the whole batch — idempotency lookup, node-existence check and insert
	// per item, all-or-nothing (any failure, including a write-phase FK/existence miss, rolls
	// back everything already inserted earlier in the same call). Per-item CreateAsync stays as
	// it is for the single form and atomic:false (partial-apply is intentionally non-transactional).
	Task<IReadOnlyList<Relation>> CreateBatchAsync(string projectKey, IReadOnlyList<(string Kind, string From, string To)> items, CancellationToken ct = default);
	// direction: "from" (edges out of nodeId), "to" (edges into nodeId), "both" (default).
	// includeHistory=false returns only active edges (ClosedAt is null).
	Task<IReadOnlyList<Relation>> ListAsync(string projectKey, string nodeId, string direction = "both", bool includeHistory = false, CancellationToken ct = default);
	// Soft-close the active edge with this id; returns true if one was active.
	Task<bool> DeleteAsync(string projectKey, string id, CancellationToken ct = default);
	// Soft-close active edges matching (kind, from, to); returns how many were closed.
	Task<int> CloseAsync(string projectKey, string kind, string fromNodeId, string toNodeId, CancellationToken ct = default);
	// All ACTIVE edges of a kind in the project (one query) — for building parent/child
	// maps (part_of) without an N+1 walk.
	Task<IReadOnlyList<Relation>> ListByKindAsync(string projectKey, string kind, CancellationToken ct = default);
	// Every ACTIVE edge touching ANY of `nodeIds`, either end (board-page-cost): ONE connection,
	// a small number of queries for a whole board render, instead of ListAsync's per-node
	// (~2 x node count) connections. The caller reproduces ListAsync(nodeId, "from"/"to") for
	// each id by grouping the result on FromNodeId/ToNodeId — a relation touching id X is
	// guaranteed present because X itself was in `nodeIds` (matches on FromNodeId OR ToNodeId).
	Task<IReadOnlyList<Relation>> ListForNodesAsync(string projectKey, IReadOnlyCollection<string> nodeIds, CancellationToken ct = default);
}

public sealed class RelationStore : IRelationStore
{
	// The kind VOCABULARY is validated in the service layer (ITasksService.
	// ValidateRelationKindAsync), instance-scoped since methodology-instance-scoped-axes:
	// builtin process kinds (task_spec: task→spec. issue_task: intake issue→task.
	// idea_spec: idea→spec. blocks: blocker→blocked. part_of: child→parent. supersedes:
	// new→obsoleted) + builtin neutral kinds (relates_to/depends_on/mirrors) + the
	// FROM node's instance-declared kinds — see MethodologyRuntime.KnownRelationKinds().
	// The store only normalizes and checks structure; internal service callers pass
	// literal builtin kinds.

	readonly IScopedDbFactory<TasksDb> _factory;
	public RelationStore(IScopedDbFactory<TasksDb> factory) => _factory = factory;

	public async Task<Relation> CreateAsync(string projectKey, string kind, string fromNodeId, string toNodeId, CancellationToken ct = default)
	{
		kind = (kind ?? "").ToLowerInvariant();
		if (kind.Length == 0)
			throw new ArgumentException("relation kind is required");
		if (string.IsNullOrWhiteSpace(fromNodeId) || string.IsNullOrWhiteSpace(toNodeId))
			throw new ArgumentException("fromNodeId and toNodeId are required");

		using var ctx = _factory.NewEnsuredConnection(projectKey);

		// Idempotent: an identical ACTIVE edge is returned, not duplicated.
		var existing = await ctx.GetTable<Relation>().FirstOrDefaultAsync(
			r => r.Kind == kind && r.FromNodeId == fromNodeId && r.ToNodeId == toNodeId && r.ClosedAt == null, ct);
		if (existing is not null)
			return existing;

		// Endpoint existence is ENFORCED BY THE DB (FK to plan_node_ids) — this check exists to
		// turn that into a readable error instead of a raw SQLite constraint failure. It closes
		// the dangling-edge hole at NodeRefResolver, which passes any 32-hex value through as a
		// NodeId without checking that a node by that id exists.
		await AssertNodeExistsAsync(ctx, projectKey, fromNodeId, "fromNodeId", ct);
		await AssertNodeExistsAsync(ctx, projectKey, toNodeId, "toNodeId", ct);

		var rel = new Relation
		{
			Id = Guid.NewGuid().ToString("N"),
			Kind = kind,
			FromNodeId = fromNodeId,
			ToNodeId = toNodeId,
			CreatedAt = DateTime.UtcNow,
			ClosedAt = null,
		};
		await ctx.InsertAsync(rel, token: ct);
		return rel;
	}

	// Same steps as CreateAsync (idempotency lookup, existence check, insert) but for the WHOLE
	// batch inside one BeginTransactionAsync/CommitAsync, rollback on any exception — mirrors
	// TemporalStore.ApplyAsync's transaction shape (TemporalStore.cs). This is what makes
	// relations_create's atomic:true promise true: a write-phase failure (e.g. a syntactically
	// valid 32-hex NodeId that NodeRefResolver let through but that names no real node) undoes
	// every insert already made earlier in the same call, not just the failing item.
	public async Task<IReadOnlyList<Relation>> CreateBatchAsync(
		string projectKey, IReadOnlyList<(string Kind, string From, string To)> items, CancellationToken ct = default)
	{
		if (items.Count == 0) return [];

		using var ctx = _factory.NewEnsuredConnection(projectKey);
		using var tx = await ctx.BeginTransactionAsync(ct);
		try
		{
			var results = new List<Relation>(items.Count);
			foreach (var (kindRaw, fromNodeId, toNodeId) in items)
			{
				var kind = (kindRaw ?? "").ToLowerInvariant();

				var existing = await ctx.GetTable<Relation>().FirstOrDefaultAsync(
					r => r.Kind == kind && r.FromNodeId == fromNodeId && r.ToNodeId == toNodeId && r.ClosedAt == null, ct);
				if (existing is not null)
				{
					results.Add(existing);
					continue;
				}

				await AssertNodeExistsAsync(ctx, projectKey, fromNodeId, "fromNodeId", ct);
				await AssertNodeExistsAsync(ctx, projectKey, toNodeId, "toNodeId", ct);

				var rel = new Relation
				{
					Id = Guid.NewGuid().ToString("N"),
					Kind = kind,
					FromNodeId = fromNodeId,
					ToNodeId = toNodeId,
					CreatedAt = DateTime.UtcNow,
					ClosedAt = null,
				};
				await ctx.InsertAsync(rel, token: ct);
				results.Add(rel);
			}

			await tx.CommitAsync(ct);
			return results;
		}
		catch
		{
			await tx.RollbackAsync(ct);
			throw;
		}
	}

	public async Task<IReadOnlyList<Relation>> ListAsync(string projectKey, string nodeId, string direction = "both", bool includeHistory = false, CancellationToken ct = default)
	{
		using var ctx = _factory.NewEnsuredConnection(projectKey);
		IQueryable<Relation> q = ctx.GetTable<Relation>();
		if (!includeHistory)
			q = q.Where(r => r.ClosedAt == null);
		q = direction.ToLowerInvariant() switch
		{
			"from" => q.Where(r => r.FromNodeId == nodeId),
			"to" => q.Where(r => r.ToNodeId == nodeId),
			_ => q.Where(r => r.FromNodeId == nodeId || r.ToNodeId == nodeId),
		};
		return await q.ToListAsync(ct);
	}

	public async Task<bool> DeleteAsync(string projectKey, string id, CancellationToken ct = default)
	{
		using var ctx = _factory.NewEnsuredConnection(projectKey);
		return await ctx.GetTable<Relation>()
			.Where(r => r.Id == id && r.ClosedAt == null)
			.Set(r => r.ClosedAt, _ => DateTime.UtcNow)
			.UpdateAsync(ct) > 0;
	}

	public async Task<int> CloseAsync(string projectKey, string kind, string fromNodeId, string toNodeId, CancellationToken ct = default)
	{
		kind = (kind ?? "").ToLowerInvariant();
		using var ctx = _factory.NewEnsuredConnection(projectKey);
		return await ctx.GetTable<Relation>()
			.Where(r => r.Kind == kind && r.FromNodeId == fromNodeId && r.ToNodeId == toNodeId && r.ClosedAt == null)
			.Set(r => r.ClosedAt, _ => DateTime.UtcNow)
			.UpdateAsync(ct);
	}

	public async Task<IReadOnlyList<Relation>> ListByKindAsync(string projectKey, string kind, CancellationToken ct = default)
	{
		kind = (kind ?? "").ToLowerInvariant();
		using var ctx = _factory.NewEnsuredConnection(projectKey);
		return await ctx.GetTable<Relation>()
			.Where(r => r.Kind == kind && r.ClosedAt == null)
			.ToListAsync(ct);
	}

	// board-page-cost: batches what used to be 2 x nodeIds.Count separate ListAsync calls (each
	// opening its OWN connection) into ONE connection and a handful of `IN (...)` queries. SQLite
	// caps bound parameters at 999; Contains() on `chunk` compiles to one IN(...) per side of the
	// OR, so a chunk of 400 ids stays comfortably under that per query (linq2db logs the exact SQL
	// it emits — verified in RelationStoreBatchTests). Board sizes here are hundreds of nodes, so
	// this is 1-2 round trips in practice, not a scheme meant to scale to tens of thousands of ids.
	public async Task<IReadOnlyList<Relation>> ListForNodesAsync(
		string projectKey, IReadOnlyCollection<string> nodeIds, CancellationToken ct = default)
	{
		var ids = nodeIds.Where(id => !string.IsNullOrEmpty(id)).Distinct(StringComparer.Ordinal).ToList();
		if (ids.Count == 0) return [];

		using var ctx = _factory.NewEnsuredConnection(projectKey);
		var results = new List<Relation>();
		var seen = new HashSet<string>(StringComparer.Ordinal);
		const int ChunkSize = 400;
		for (var i = 0; i < ids.Count; i += ChunkSize)
		{
			var chunk = ids.Skip(i).Take(ChunkSize).ToList();
			// linq2db translates List<string>.Contains(column) to a SQL `column IN (@p0, @p1, …)`;
			// the OR combines the two IN-lists into one WHERE clause, one round trip per chunk.
			var rows = await ctx.GetTable<Relation>()
				.Where(r => r.ClosedAt == null && (chunk.Contains(r.FromNodeId) || chunk.Contains(r.ToNodeId)))
				.ToListAsync(ct);
			foreach (var r in rows)
				if (seen.Add(r.Id)) results.Add(r);
		}
		return results;
	}

	// The registry (plan_node_ids) is the FK parent, and triggers keep it == the set of node
	// identities present in plan_nodes — so this is exactly "does this node exist in this
	// project's file", asked of the same source of truth the FK will consult.
	static async Task AssertNodeExistsAsync(TasksDb ctx, string projectKey, string nodeId, string param, CancellationToken ct)
	{
		if (await ctx.GetTable<TaskNodeId>().AnyAsync(n => n.NodeId == nodeId, ct)) return;
		throw new ArgumentException(
			$"{param} '{nodeId}' does not exist in project '{projectKey}' — a relation endpoint must be an existing node " +
			"(pass a node slug or the NodeId of a node from tasks_upsert/tasks_search)");
	}
}
