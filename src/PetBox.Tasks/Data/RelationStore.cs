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

	// The registry (plan_node_ids) is the FK parent, and triggers keep it == the set of node
	// identities present in plan_nodes — so this is exactly "does this node exist in this
	// project's file", asked of the same source of truth the FK will consult.
	static async Task AssertNodeExistsAsync(TasksDb ctx, string projectKey, string nodeId, string param, CancellationToken ct)
	{
		if (await ctx.GetTable<PlanNodeId>().AnyAsync(n => n.NodeId == nodeId, ct)) return;
		throw new ArgumentException(
			$"{param} '{nodeId}' does not exist in project '{projectKey}' — a relation endpoint must be an existing node " +
			"(pass a node slug or the NodeId of a node from tasks_upsert/tasks_search)");
	}
}
