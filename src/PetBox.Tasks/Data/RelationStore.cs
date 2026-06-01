using LinqToDB;
using LinqToDB.Async;
using PetBox.Core.Data;
using PetBox.Core.Models;

namespace PetBox.Tasks.Data;

// Project-level store of typed edges between node ids (in petbox.db). Edges bind to
// stable NodeId, so they survive node renames. Mirrors TaskBoardStore (uses PetBoxDb).
public interface IRelationStore
{
	Task<Relation> CreateAsync(string projectKey, string kind, string fromNodeId, string toNodeId, CancellationToken ct = default);
	// direction: "from" (edges out of nodeId), "to" (edges into nodeId), "both" (default).
	Task<IReadOnlyList<Relation>> ListAsync(string projectKey, string nodeId, string direction = "both", CancellationToken ct = default);
	Task<bool> DeleteAsync(string projectKey, string id, CancellationToken ct = default);
}

public sealed class RelationStore : IRelationStore
{
	static readonly string[] Kinds = ["task_spec", "issue_task", "idea_spec", "blocks", "nfr", "dup"];

	readonly PetBoxDb _db;
	public RelationStore(PetBoxDb db) => _db = db;

	public async Task<Relation> CreateAsync(string projectKey, string kind, string fromNodeId, string toNodeId, CancellationToken ct = default)
	{
		kind = (kind ?? "").ToLowerInvariant();
		if (!Kinds.Contains(kind))
			throw new ArgumentException($"invalid relation kind '{kind}'; valid: {string.Join("|", Kinds)}");
		if (string.IsNullOrWhiteSpace(fromNodeId) || string.IsNullOrWhiteSpace(toNodeId))
			throw new ArgumentException("fromNodeId and toNodeId are required");

		// Idempotent: an identical edge is returned, not duplicated.
		var existing = await _db.Relations.FirstOrDefaultAsync(
			r => r.ProjectKey == projectKey && r.Kind == kind && r.FromNodeId == fromNodeId && r.ToNodeId == toNodeId, ct);
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
		};
		await _db.InsertAsync(rel, token: ct);
		return rel;
	}

	public async Task<IReadOnlyList<Relation>> ListAsync(string projectKey, string nodeId, string direction = "both", CancellationToken ct = default)
	{
		var q = _db.Relations.Where(r => r.ProjectKey == projectKey);
		q = direction.ToLowerInvariant() switch
		{
			"from" => q.Where(r => r.FromNodeId == nodeId),
			"to" => q.Where(r => r.ToNodeId == nodeId),
			_ => q.Where(r => r.FromNodeId == nodeId || r.ToNodeId == nodeId),
		};
		return await q.ToListAsync(ct);
	}

	public async Task<bool> DeleteAsync(string projectKey, string id, CancellationToken ct = default) =>
		await _db.Relations.Where(r => r.ProjectKey == projectKey && r.Id == id).DeleteAsync(ct) > 0;
}
