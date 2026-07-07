using LinqToDB;
using LinqToDB.Async;
using PetBox.Core.Data;
using PetBox.Core.Data.Temporal;
using PetBox.Core.Search;
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

	// ── uniform-entity verbs (comments_upsert / _search / _delta / _get) ───────────────

	public async Task<CommentBatchResult> UpsertAsync(
		string projectKey, string board, IReadOnlyList<CommentItem> items, CancellationToken ct = default)
	{
		var ctx = _factory.GetDb(projectKey);

		// Load the active rows the EDIT items address (identity/parent/author/nodeId are carried
		// forward; only body changes, exactly like EditAsync). A missing id is a clear error.
		var editIds = items.Where(i => !string.IsNullOrEmpty(i.Id)).Select(i => i.Id!).Distinct().ToList();
		var currentById = editIds.Count == 0
			? new Dictionary<string, CommentRow>(StringComparer.Ordinal)
			: (await ctx.GetTable<CommentRow>()
					.Where(c => editIds.Contains(c.Key) && c.Board == board && c.ActiveTo == null).ToListAsync(ct))
				.ToDictionary(c => c.Key, StringComparer.Ordinal);

		var desired = new List<CommentRow>(items.Count);
		var itemByKey = new Dictionary<string, CommentItem>(StringComparer.Ordinal);
		foreach (var it in items)
		{
			if (string.IsNullOrWhiteSpace(it.Body)) throw new ArgumentException("comment body is required");
			if (string.IsNullOrEmpty(it.Id))
			{
				// CREATE
				if (string.IsNullOrWhiteSpace(it.NodeId)) throw new ArgumentException("nodeId is required to create a comment");
				if (string.IsNullOrWhiteSpace(it.Author)) throw new ArgumentException("author is required to create a comment");
				if (!string.IsNullOrEmpty(it.ParentId))
				{
					// A reply must hang under an active comment of the SAME thread (board+node). An
					// intra-batch parent (a reply to another item created in the same call) is not
					// supported — the parent must already exist.
					var parent = await ctx.GetTable<CommentRow>()
						.FirstOrDefaultAsync(c => c.Key == it.ParentId && c.ActiveTo == null, ct);
					if (parent is null || parent.Board != board || parent.NodeId != it.NodeId)
						throw new ArgumentException($"parentId '{it.ParentId}' is not an active comment under this node");
				}
				var id = Guid.NewGuid().ToString("N");
				desired.Add(new CommentRow
				{
					Key = id, Version = it.Version, Board = board, NodeId = it.NodeId!,
					ParentId = string.IsNullOrEmpty(it.ParentId) ? null : it.ParentId,
					Author = it.Author ?? string.Empty, Body = it.Body,
				});
				itemByKey[id] = it;
			}
			else
			{
				// PATCH
				if (!currentById.TryGetValue(it.Id!, out var cur))
					throw new ArgumentException($"comment '{it.Id}' not found or already deleted");
				desired.Add(cur with { Version = it.Version, Body = it.Body });
				itemByKey[it.Id!] = it;
			}
		}

		// One atomic temporal batch (partitioned by board, so `currentVersion` is the board's
		// comment cursor). FTS is re-indexed inside the tx — the same Class-A discipline as Add/Edit.
		var fts = new SqliteFtsIndex(() => ctx);
		var r = await TemporalStore.UpsertAsync(ctx, desired, partition: x => x.Board == board,
			onWithinTx: async (tx, upserted, _, c) =>
			{
				foreach (var u in upserted)
					await fts.IndexAsync(tx, TasksSearchDocs.CommentToDoc(u, projectKey), c);
			}, ct: ct);

		// r.Added/r.Updated are the delta since sinceVersion (0 here → the whole board's active
		// comments). The ECHO must cover ONLY this call (like tasks_upsert/memory_upsert): keep just
		// the rows whose key is in THIS batch, and — when the batch was REJECTED — nothing at all
		// (applied:false ⇒ nothing written, added/updated empty).
		var mineAdded = r.Applied ? r.Added.Where(x => itemByKey.ContainsKey(x.Key)).ToList() : [];
		var mineUpdated = r.Applied ? r.Updated.Where(x => itemByKey.ContainsKey(x.Key)).ToList() : [];
		if (r.Applied)
		{
			// Tags: a create always writes its set (null → none); an edit only when tags != null
			// (PATCH — omitted leaves the set as-is), matching AddAsync/EditAsync.
			foreach (var row in mineAdded)
				await SetTagsAsync(ctx, row.Key, board, itemByKey[row.Key].Tags, ct);
			foreach (var row in mineUpdated)
				if (itemByKey[row.Key].Tags is { } tags)
					await SetTagsAsync(ctx, row.Key, board, tags, ct);
		}

		var tagLookup = await TagsForAsync(ctx, board, ct);
		return new CommentBatchResult(
			r.Applied, r.CurrentVersion,
			mineAdded.Select(x => ToView(x, tagLookup)).ToList(),
			mineUpdated.Select(x => ToView(x, tagLookup)).ToList(),
			r.Conflicts.Select(c => new CommentConflict(c.Key, c.Kind.ToString(), c.BaselineVersion, c.ActiveVersion, c.Reason)).ToList());
	}

	public async Task<CommentSearchResult> SearchAsync(
		string projectKey, string? board, string? nodeId, string? query, int limit, CancellationToken ct = default)
	{
		var ctx = _factory.GetDb(projectKey);
		var q = string.IsNullOrWhiteSpace(query) ? null : query.Trim();
		var tags = await TagsForAsync(ctx, board, ct);

		if (q is null)
		{
			// LIST: deterministic chronological listing (the former comments_list, now optionally
			// project-wide or board-scoped, and optionally narrowed to one owner node).
			var listQ = ctx.GetTable<CommentRow>().Where(c => c.ActiveTo == null);
			if (board is not null) listQ = listQ.Where(c => c.Board == board);
			if (nodeId is not null) listQ = listQ.Where(c => c.NodeId == nodeId);
			var rows = await listQ.ToListAsync(ct);
			IEnumerable<CommentView> views = rows.OrderBy(r => r.Created).Select(r => ToView(r, tags));
			if (limit > 0) views = views.Take(limit);
			return new CommentSearchResult(views.ToList());
		}

		// QUERY: the lexical floor only (semantic is a later Class-B item for comments). Reads open
		// a FRESH connection (SqliteFtsIndex disposes it) — never the cached request context.
		var indexes = new List<ISearchIndex> { new SqliteFtsIndex(() => _factory.NewConnection(projectKey)) };
		var k = limit > 0 ? Math.Max(limit * 3, 50) : 200;
		var resp = await new SearchService(indexes).SearchAsync(projectKey, q, new SearchFilter(board), k, ct);

		// The FTS covers node docs AND comment docs in the same (scope, board) partition — keep
		// only comment hits ("c:"+key), in fused-rank order, dedup by key.
		var hitKeys = new List<string>();
		var seen = new HashSet<string>(StringComparer.Ordinal);
		foreach (var h in resp.Hits)
		{
			if (!h.Id.StartsWith(TasksSearchDocs.CommentIdPrefix, StringComparison.Ordinal)) continue;
			var key = h.Id[TasksSearchDocs.CommentIdPrefix.Length..];
			if (seen.Add(key)) hitKeys.Add(key);
		}
		if (hitKeys.Count == 0) return new CommentSearchResult([], resp.Retrievers);

		var rowsById = (await ctx.GetTable<CommentRow>()
				.Where(c => hitKeys.Contains(c.Key) && c.ActiveTo == null).ToListAsync(ct))
			.ToDictionary(c => c.Key, StringComparer.Ordinal);
		IEnumerable<CommentView> ordered = hitKeys
			.Where(rowsById.ContainsKey)
			.Select(key => rowsById[key])
			.Where(r => nodeId is null || r.NodeId == nodeId)
			.Select(r => ToView(r, tags));
		if (limit > 0) ordered = ordered.Take(limit);
		return new CommentSearchResult(ordered.ToList(), resp.Retrievers);
	}

	public async Task<CommentDelta> DeltaAsync(
		string projectKey, string board, long sinceVersion, CancellationToken ct = default)
	{
		var ctx = _factory.GetDb(projectKey);
		var (added, updated, removed, current) =
			await TemporalStore.ChangesSinceAsync<CommentRow>(ctx, sinceVersion, partition: x => x.Board == board, ct: ct);
		var tags = await TagsForAsync(ctx, board, ct);
		return new CommentDelta(
			current,
			added.Select(x => ToView(x, tags)).ToList(),
			updated.Select(x => ToView(x, tags)).ToList(),
			removed.ToList());
	}

	public async Task<CommentView?> GetAsync(string projectKey, string id, CancellationToken ct = default)
	{
		var ctx = _factory.GetDb(projectKey);
		var row = await ctx.GetTable<CommentRow>()
			.FirstOrDefaultAsync(c => c.Key == id && c.ActiveTo == null, ct);
		if (row is null) return null;
		var tags = await TagsForAsync(ctx, row.Board, ct);
		return ToView(row, tags);
	}

	// ── low-ceremony single-write door (board UI) ──────────────────────────────────────

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
		// Class-A lexical floor: index the comment INSIDE the entity tx (onWithinTx), so a
		// committed comment is never lexically-stale and the FTS row rolls back with it —
		// same discipline as MemoryService/RefreshFtsTagsAsync. Indexed UNCONDITIONALLY (no
		// owner-indexability check): a comment under a terminal/closed node is filtered at
		// read time (owner absent from the open board view), so the extra row is harmless and
		// saves a lookup. Tags aren't set yet (SetTagsAsync runs after) → doc carries none.
		var fts = new SqliteFtsIndex(() => ctx);
		var r = await TemporalStore.UpsertAsync(ctx, new[] { row }, partition: x => x.Board == board,
			onWithinTx: async (tx, upserted, _, c) =>
			{
				foreach (var u in upserted)
					await fts.IndexAsync(tx, TasksSearchDocs.CommentToDoc(u, projectKey), c);
			}, ct: ct);
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
		// Re-index the edited body inside the entity tx (the old text's row is overwritten by
		// IndexAsync's delete+insert on (Scope,Type,Id), so a stale-body search stops matching).
		var fts = new SqliteFtsIndex(() => ctx);
		var r = await TemporalStore.UpsertAsync(ctx, new[] { row }, partition: x => x.Board == board,
			onWithinTx: async (tx, upserted, _, c) =>
			{
				foreach (var u in upserted)
					await fts.IndexAsync(tx, TasksSearchDocs.CommentToDoc(u, projectKey), c);
			}, ct: ct);
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

		// Soft-close the comment (no replacement revision) + its active tags. Drop the FTS row
		// inside the entity tx, keyed by the "c:"+id address.
		var fts = new SqliteFtsIndex(() => ctx);
		var r = await TemporalStore.UpsertAsync(
			ctx, Array.Empty<CommentRow>(), new[] { (id, 0L) }, partition: x => x.Board == board,
			onWithinTx: async (tx, _, deletedKeys, c) =>
			{
				foreach (var key in deletedKeys)
					await fts.DeleteAsync(tx, projectKey, board, TasksSearchDocs.CommentIdPrefix + key, c);
			}, ct: ct);
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

	// Active tags of every comment on a board (or the whole project when `board` is null, for a
	// project-wide comments_search listing), as commentId -> tags — mirrors TagStore.BoardTagsAsync.
	static async Task<ILookup<string, string>> TagsForAsync(TasksDb ctx, string? board, CancellationToken ct)
	{
		var q = ctx.GetTable<CommentTag>().Where(t => t.ValidTo == null);
		if (board is not null) q = q.Where(t => t.Board == board);
		var rows = await q.Select(t => new { t.CommentId, t.Tag }).ToListAsync(ct);
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
