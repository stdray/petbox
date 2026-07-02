using LinqToDB;
using LinqToDB.Async;
using PetBox.Core.Data;
using PetBox.Core.Data.Temporal;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Data;

namespace PetBox.Tasks.Services;

// The one implementation of ICommentService. Store + service folded into one class (all
// validation lives here): reads via ctx.GetTable<T>() and writes the comment via
// TemporalStore.UpsertAsync — the same per-project file (IScopedDbFactory<TasksDb>) as
// plan_nodes, partitioned by Board. Tags are managed like TagStore.SetAsync, but OPEN
// (no vocabulary). Comments never touch ITasksService, so they stay out of tasks_search.
public sealed class CommentService : ICommentService
{
	readonly IScopedDbFactory<TasksDb> _factory;
	public CommentService(IScopedDbFactory<TasksDb> factory) => _factory = factory;

	public async Task<CommentUpsertResult> AddAsync(
		string projectKey, string board, string nodeId, string? parentId, string author, string body,
		IReadOnlyList<string>? tags, CancellationToken ct = default)
	{
		if (string.IsNullOrWhiteSpace(nodeId)) throw new ArgumentException("nodeId is required");
		if (string.IsNullOrWhiteSpace(body)) throw new ArgumentException("body is required");

		var ctx = _factory.GetDb(projectKey);

		if (!string.IsNullOrEmpty(parentId))
		{
			var parent = await ctx.GetTable<CommentRow>()
				.FirstOrDefaultAsync(c => c.Key == parentId && c.ActiveTo == null, ct);
			// A reply must hang under an active comment of the SAME thread (board+node) —
			// rejects cross-thread parenting and orphan parents. (No re-parent in v1, so a
			// fresh GUID can never form a cycle.)
			if (parent is null || parent.Board != board || parent.NodeId != nodeId)
				throw new ArgumentException($"parentId '{parentId}' is not an active comment under this node");
		}

		var id = Guid.NewGuid().ToString("N");
		var row = new CommentRow
		{
			Key = id, Version = 0, Board = board, NodeId = nodeId,
			ParentId = string.IsNullOrEmpty(parentId) ? null : parentId,
			Author = author ?? string.Empty, Body = body,
		};
		var r = await TemporalStore.UpsertAsync(ctx, new[] { row }, partition: x => x.Board == board, ct: ct);
		if (r.Applied) await SetTagsAsync(ctx, id, board, tags, ct);
		return Map(r, id);
	}

	public async Task<CommentUpsertResult> EditAsync(
		string projectKey, string board, string id, string body,
		IReadOnlyList<string>? tags, long version, CancellationToken ct = default)
	{
		if (string.IsNullOrWhiteSpace(body)) throw new ArgumentException("body is required");

		var ctx = _factory.GetDb(projectKey);
		var current = await ctx.GetTable<CommentRow>()
			.FirstOrDefaultAsync(c => c.Key == id && c.Board == board && c.ActiveTo == null, ct);
		if (current is null) throw new ArgumentException($"comment '{id}' not found or already deleted");

		// Carry identity/parent/author; only the body changes. `version` is the caller's
		// baseline — TemporalStore turns a stale one into a conflict, not a clobber.
		var row = current with { Version = version, Body = body };
		var r = await TemporalStore.UpsertAsync(ctx, new[] { row }, partition: x => x.Board == board, ct: ct);
		if (r.Applied && tags is not null) await SetTagsAsync(ctx, id, board, tags, ct);
		return Map(r, id);
	}

	public async Task<bool> DeleteAsync(string projectKey, string board, string id, CancellationToken ct = default)
	{
		var ctx = _factory.GetDb(projectKey);
		var current = await ctx.GetTable<CommentRow>()
			.FirstOrDefaultAsync(c => c.Key == id && c.Board == board && c.ActiveTo == null, ct);
		if (current is null) return false; // already gone / not found — idempotent

		var hasChildren = await ctx.GetTable<CommentRow>()
			.AnyAsync(c => c.ParentId == id && c.ActiveTo == null, ct);
		if (hasChildren)
			throw new InvalidOperationException($"comment '{id}' has replies — delete them first");

		// Soft-close the comment (no replacement revision) + its active tags.
		var r = await TemporalStore.UpsertAsync(
			ctx, Array.Empty<CommentRow>(), new[] { (id, 0L) }, partition: x => x.Board == board, ct: ct);
		await ctx.GetTable<CommentTag>()
			.Where(t => t.CommentId == id && t.ValidTo == null)
			.Set(t => t.ValidTo, _ => DateTime.UtcNow)
			.UpdateAsync(ct);
		return r.Applied;
	}

	public async Task<IReadOnlyList<CommentView>> ListForNodeAsync(
		string projectKey, string board, string nodeId, CancellationToken ct = default)
	{
		var ctx = _factory.GetDb(projectKey);
		var rows = await ctx.GetTable<CommentRow>()
			.Where(c => c.Board == board && c.NodeId == nodeId && c.ActiveTo == null).ToListAsync(ct);
		var tags = await TagsForAsync(ctx, board, ct);
		return rows.OrderBy(r => r.Created).Select(r => ToView(r, tags)).ToList();
	}

	public async Task<ILookup<string, CommentView>> ListForBoardAsync(
		string projectKey, string board, CancellationToken ct = default)
	{
		var ctx = _factory.GetDb(projectKey);
		var rows = await ctx.GetTable<CommentRow>()
			.Where(c => c.Board == board && c.ActiveTo == null).ToListAsync(ct);
		var tags = await TagsForAsync(ctx, board, ct);
		return rows.OrderBy(r => r.Created).Select(r => ToView(r, tags)).ToLookup(v => v.NodeId, StringComparer.Ordinal);
	}

	// ── helpers ──────────────────────────────────────────────────────────────

	// Active tags of every comment on a board, as commentId -> tags (one query, grouped in
	// memory) — mirrors TagStore.BoardTagsAsync.
	static async Task<ILookup<string, string>> TagsForAsync(TasksDb ctx, string board, CancellationToken ct)
	{
		var rows = await ctx.GetTable<CommentTag>()
			.Where(t => t.Board == board && t.ValidTo == null)
			.Select(t => new { t.CommentId, t.Tag }).ToListAsync(ct);
		return rows.ToLookup(t => t.CommentId, t => t.Tag, StringComparer.Ordinal);
	}

	static CommentView ToView(CommentRow r, ILookup<string, string> tags) =>
		new(r.Key, r.NodeId, r.ParentId, r.Author, r.Body,
			tags[r.Key].OrderBy(t => t, StringComparer.Ordinal).ToList(), r.Version, r.Created, r.Updated);

	static CommentUpsertResult Map(TemporalUpsertResult<CommentRow> r, string id) =>
		new(r.Applied, r.CurrentVersion, r.Applied ? id : null,
			// .Kind.ToString() is fine here — in memory, not a SQL projection.
			r.Conflicts.Select(c => new CommentConflict(c.Key, c.Kind.ToString(), c.BaselineVersion, c.ActiveVersion, c.Reason)).ToList());

	// Replace a comment's active tag set: soft-close removed, insert added. OPEN — any
	// non-empty "tag" (lowercased/trimmed/deduped), no namespace allowlist (unlike TagStore).
	static async Task SetTagsAsync(TasksDb ctx, string commentId, string board, IReadOnlyList<string>? tags, CancellationToken ct)
	{
		var desired = NormalizeTags(tags);
		var active = await ctx.GetTable<CommentTag>()
			.Where(t => t.CommentId == commentId && t.ValidTo == null).ToListAsync(ct);
		var activeTags = active.Select(t => t.Tag).ToHashSet(StringComparer.Ordinal);
		var now = DateTime.UtcNow;

		foreach (var a in active.Where(a => !desired.Contains(a.Tag)))
			await ctx.GetTable<CommentTag>()
				.Where(t => t.CommentId == commentId && t.Tag == a.Tag && t.ValidTo == null)
				.Set(t => t.ValidTo, _ => now)
				.UpdateAsync(ct);

		foreach (var tag in desired.Where(d => !activeTags.Contains(d)))
			await ctx.InsertAsync(new CommentTag { CommentId = commentId, Board = board, Tag = tag, ValidFrom = now }, token: ct);
	}

	public static IReadOnlySet<string> NormalizeTags(IReadOnlyList<string>? tags)
	{
		var set = new HashSet<string>(StringComparer.Ordinal);
		if (tags is null) return set;
		foreach (var raw in tags)
		{
			if (string.IsNullOrWhiteSpace(raw)) continue;
			set.Add(raw.Trim().ToLowerInvariant());
		}
		return set;
	}
}
