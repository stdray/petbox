using LinqToDB;
using LinqToDB.Async;
using PetBox.Core.Data;
using PetBox.Core.Models;

namespace PetBox.Tasks.Data;

// Project-level store of typed edges between node ids (in petbox.db). Edges bind to
// stable NodeId, so they survive node renames. Interval-temporal: an edge is active
// while ClosedAt is null; delete/effects soft-close it (history kept). Mirrors
// TaskBoardStore (uses PetBoxDb).
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

	readonly PetBoxDb _db;
	public RelationStore(PetBoxDb db) => _db = db;

	public async Task<Relation> CreateAsync(string projectKey, string kind, string fromNodeId, string toNodeId, CancellationToken ct = default)
	{
		kind = (kind ?? "").ToLowerInvariant();
		if (kind.Length == 0)
			throw new ArgumentException("relation kind is required");
		if (string.IsNullOrWhiteSpace(fromNodeId) || string.IsNullOrWhiteSpace(toNodeId))
			throw new ArgumentException("fromNodeId and toNodeId are required");

		// Idempotent: an identical ACTIVE edge is returned, not duplicated.
		var existing = await _db.Relations.FirstOrDefaultAsync(
			r => r.ProjectKey == projectKey && r.Kind == kind && r.FromNodeId == fromNodeId && r.ToNodeId == toNodeId && r.ClosedAt == null, ct);
		if (existing is not null)
			return existing;

		var rel = new Relation
		{
			Id = Guid.NewGuid().ToString("N"),
			ProjectKey = projectKey,
			Kind = kind,
			FromNodeId = fromNodeId,
			ToNodeId = toNodeId,
			CreatedAt = DateTime.UtcNow,
			ClosedAt = null,
		};
		await _db.InsertAsync(rel, token: ct);
		return rel;
	}

	public async Task<IReadOnlyList<Relation>> ListAsync(string projectKey, string nodeId, string direction = "both", bool includeHistory = false, CancellationToken ct = default)
	{
		var q = _db.Relations.Where(r => r.ProjectKey == projectKey);
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

	public async Task<bool> DeleteAsync(string projectKey, string id, CancellationToken ct = default) =>
		await _db.Relations
			.Where(r => r.ProjectKey == projectKey && r.Id == id && r.ClosedAt == null)
			.Set(r => r.ClosedAt, _ => DateTime.UtcNow)
			.UpdateAsync(ct) > 0;

	public async Task<int> CloseAsync(string projectKey, string kind, string fromNodeId, string toNodeId, CancellationToken ct = default)
	{
		kind = (kind ?? "").ToLowerInvariant();
		return await _db.Relations
			.Where(r => r.ProjectKey == projectKey && r.Kind == kind && r.FromNodeId == fromNodeId && r.ToNodeId == toNodeId && r.ClosedAt == null)
			.Set(r => r.ClosedAt, _ => DateTime.UtcNow)
			.UpdateAsync(ct);
	}

	public async Task<IReadOnlyList<Relation>> ListByKindAsync(string projectKey, string kind, CancellationToken ct = default)
	{
		kind = (kind ?? "").ToLowerInvariant();
		return await _db.Relations
			.Where(r => r.ProjectKey == projectKey && r.Kind == kind && r.ClosedAt == null)
			.ToListAsync(ct);
	}
}
