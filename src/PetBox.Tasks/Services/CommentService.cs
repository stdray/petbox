using LinqToDB;
using PetBox.Core.Contract;
using PetBox.Core.Data;
using PetBox.Core.Data.Temporal;
using PetBox.Core.Search;
using PetBox.Core.Settings;
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
	// Rerank candidate budget inputs (rerank-budget-params-to-settings), read per query at
	// Scope.Project via RerankCandidateBudget.ResolveAsync — null (DI absent, direct/test
	// construction) falls back to the compiled-in RerankCandidateBudget() default.
	readonly ISettingsResolver? _settings;
	public CommentService(IScopedDbFactory<TasksDb> factory, ISettingsResolver? settings = null)
	{
		_factory = factory;
		_settings = settings;
	}

	// ── uniform-entity verbs (comments_upsert / _search / _delta / _get) ───────────────

	public async Task<CommentBatchResult> UpsertAsync(
		string projectKey, string board, IReadOnlyList<CommentItem> items, bool atomic = true, CancellationToken ct = default)
	{
		using var ctx = _factory.NewEnsuredConnection(projectKey);

		// Load the active rows the EDIT items address (identity/parent/author/nodeId are carried
		// forward; only body changes, exactly like EditAsync). A missing id is a clear error.
		var editIds = items.Where(i => !string.IsNullOrEmpty(i.Id)).Select(i => i.Id!).Distinct().ToList();
		var currentById = editIds.Count == 0
			? new Dictionary<string, CommentRow>(StringComparer.Ordinal)
			: (await ctx.GetTable<CommentRow>()
					.Where(c => editIds.Contains(c.Key) && c.Board == board && c.ActiveTo == null).ToListAsync(ct))
				.ToDictionary(c => c.Key, StringComparer.Ordinal);

		// comment-slug-and-refs: the slugs already CLAIMED under each node this batch touches, as
		// (nodeId, slug) -> the comment Key holding it. Read once for the whole batch (the same
		// posture as `currentById` above), then extended as items in THIS batch claim theirs — so an
		// intra-batch duplicate is refused exactly like a stored one, instead of both landing and
		// leaving the node with two comments answering to the same address.
		var touchedNodes = items.Where(i => !string.IsNullOrWhiteSpace(i.NodeId)).Select(i => i.NodeId!)
			.Concat(currentById.Values.Select(c => c.NodeId))
			.Distinct(StringComparer.Ordinal).ToList();
		var slugOwners = new Dictionary<(string NodeId, string Slug), string>();
		if (touchedNodes.Count > 0)
			foreach (var row in await ctx.GetTable<CommentRow>()
						 .Where(c => c.Board == board && c.ActiveTo == null && c.Slug != null && touchedNodes.Contains(c.NodeId))
						 .Select(c => new { c.Key, c.NodeId, c.Slug })
						 .ToListAsync(ct))
				slugOwners[(row.NodeId, row.Slug!)] = row.Key;

		var desired = new List<CommentRow>(items.Count);
		var itemByKey = new Dictionary<string, CommentItem>(StringComparer.Ordinal);
		// Keys that entered `desired` via the PATCH branch below — each one's presence in
		// `currentById` (an ACTIVE row read before this call) was the precondition to get there,
		// so a patched key is ALWAYS an edit of something that already existed, never a create.
		// Used to correct the Added/Updated echo split (see mineAdded/mineUpdated below).
		var patchedKeys = new HashSet<string>(StringComparer.Ordinal);
		// PARTIAL mode (atomic:false): a refused item becomes a per-item Rejected conflict instead of
		// killing the call. A comment's `parentId` must already be an ACTIVE comment (an intra-batch
		// forward reference is not expressible — verified below), and comments carry no other
		// cross-item reference, so the dependent-rejection cascade has nothing to walk: every item is
		// independent. A rejected CREATE has no id yet, so its conflict is keyed by the item's
		// POSITION (#0, #1 …) — the only handle the caller holds for it.
		var rejected = new List<TemporalConflict>();

		// The slug an item's desired revision must carry. THREE refusals, all ArgumentException so
		// they ride the same channel every other per-item guard here uses (an atomic batch throws,
		// a partial one records a per-item conflict):
		//   * an invalid shape (CommentSlug.Validate);
		//   * a slug already held by ANOTHER comment under the same node — stored or claimed
		//     earlier in this very batch;
		//   * ANY change of a slug that is already set, a clear ("") included. Write-once, by
		//     decision: see CommentItem.Slug for why a node's rename does not generalize here.
		// `requested` null = omitted, so the current value is inherited (the `tags` posture) and a
		// create simply gets none — which is why every comment written before this field existed
		// keeps round-tripping through an ordinary body PATCH untouched.
		string? ResolveSlug(string? requested, string? currentSlug, string nodeId, string selfKey)
		{
			if (requested is null) return currentSlug;

			var trimmed = requested.Trim();
			if (trimmed.Length == 0)
			{
				if (currentSlug is null) return null; // nothing to clear — an honest no-op, not a refusal
				throw new ArgumentException(
					$"comment '{selfKey}' already carries slug '{currentSlug}' — a comment slug is write-once and cannot be "
					+ "cleared: bodies elsewhere may quote it, and dropping it would silently turn those mentions into plain text");
			}

			var slug = CommentSlug.Validate(trimmed);
			if (currentSlug is not null)
				return currentSlug == slug
					? currentSlug
					: throw new ArgumentException(
						$"comment '{selfKey}' already carries slug '{currentSlug}' — a comment slug is write-once and cannot be "
						+ $"changed to '{slug}': bodies elsewhere may quote the old one, and re-pointing it would silently turn "
						+ "those mentions into plain text");

			if (slugOwners.TryGetValue((nodeId, slug), out var owner) && !string.Equals(owner, selfKey, StringComparison.Ordinal))
				throw new ArgumentException(
					$"slug '{slug}' is already used by comment '{owner}' under this node — a comment slug is unique within its "
					+ "owning node (two comments under DIFFERENT nodes may share one)");

			slugOwners[(nodeId, slug)] = selfKey;
			return slug;
		}

		for (var i = 0; i < items.Count; i++)
		{
			var it = items[i];

			// ── write-body-by-reference ────────────────────────────────────────────────────
			// Ahead of the fragment block, so a `bodyRef` + `fragment` collision is named in the
			// caller's own vocabulary instead of as BodyAndFragment quoting a `body` never sent. A
			// resolved bodyRef simply becomes `it`'s body and the rest of the loop is unchanged.
			//
			// LEGAL ON A CREATE, unlike a fragment: a bodyRef replaces the text rather than patching
			// it, so there is nothing for it to match against and nothing to refuse. That is the
			// case this mechanism exists for — a subagent's report posted as a comment, which is a
			// create, from a file, in one call.
			if (it.BodyRef is not null)
			{
				var at = it.Id ?? $"#{i}";
				if (it.Body is not null)
				{
					rejected.Add(new(at, TemporalConflictKind.Rejected, it.Version, null, BodyRefs.BodyAndBodyRef));
					continue;
				}
				if (it.Fragment is not null)
				{
					rejected.Add(new(at, TemporalConflictKind.Rejected, it.Version, null, BodyRefs.FragmentAndBodyRef));
					continue;
				}
				if (it.BodyRef.Error is { } bodyRefError)
				{
					rejected.Add(new(at, TemporalConflictKind.Rejected, it.Version, null, bodyRefError));
					continue;
				}
				it = it with { Body = it.BodyRef.Text, BodyRef = null };
			}

			// ── write-fragment-patch ───────────────────────────────────────────────────────
			// A `fragment` PATCH is resolved against `currentById` — the active row this call
			// already read to build the ordinary PATCH below — so the substitution and the
			// version watermark see the same revision. Refusals ride `rejected` in BOTH atomic
			// and partial mode (unlike the ArgumentException guards below, which keep their
			// historical atomic-throw behaviour): a fragment that stopped matching means the text
			// moved under the caller, and that must surface as applied:false + conflicts[], the
			// same channel a stale baseline uses.
			if (it.Fragment is not null)
			{
				// A CREATE has no id yet, so a rejected item can only be named by its position.
				var at = it.Id ?? $"#{i}";
				if (it.Body is not null)
				{
					rejected.Add(new(at, TemporalConflictKind.Rejected, it.Version, null, FragmentPatch.BodyAndFragment));
					continue;
				}
				if (string.IsNullOrEmpty(it.Id))
				{
					rejected.Add(new(at, TemporalConflictKind.Rejected, it.Version, null,
						"'fragment' patches an existing comment — a create (no id) has no text to match; send 'body'"));
					continue;
				}
				if (!currentById.TryGetValue(it.Id!, out var curForFragment))
				{
					rejected.Add(new(at, TemporalConflictKind.Rejected, it.Version, null,
						$"comment '{it.Id}' not found or already deleted"));
					continue;
				}
				var patched = FragmentPatch.Apply(curForFragment.Body, it.Fragment);
				if (!patched.Ok)
				{
					rejected.Add(new(at, TemporalConflictKind.Rejected, it.Version, curForFragment.Version, patched.Error));
					continue;
				}
				// A `slug` riding along with a fragment edit is judged by the SAME rule as anywhere
				// else — it just rides this branch's channel (conflicts[] in atomic and partial mode
				// alike), like every other fragment refusal, instead of the throw the ordinary branch
				// below uses. Silently ignoring the field would be the one unacceptable option.
				string? fragmentSlug;
				try
				{
					fragmentSlug = ResolveSlug(it.Slug, curForFragment.Slug, curForFragment.NodeId, it.Id!);
				}
				catch (ArgumentException ex)
				{
					rejected.Add(new(at, TemporalConflictKind.Rejected, it.Version, curForFragment.Version, ex.Message));
					continue;
				}
				desired.Add(curForFragment with { Version = it.Version, Body = patched.Body, Slug = fragmentSlug });
				itemByKey[it.Id!] = it;
				patchedKeys.Add(it.Id!);
				continue;
			}

			try
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
						Key = id,
						Version = it.Version,
						Board = board,
						NodeId = it.NodeId!,
						ParentId = string.IsNullOrEmpty(it.ParentId) ? null : it.ParentId,
						Author = it.Author ?? string.Empty,
						Body = it.Body!,
						// The id is minted first so a slug claimed here is claimed BY this comment —
						// which is what makes a SECOND item in the same batch asking for the same slug
						// under the same node a refusal, rather than a silent second holder.
						Slug = ResolveSlug(it.Slug, null, it.NodeId!, id),
					});
					itemByKey[id] = it;
				}
				else
				{
					// PATCH
					if (!currentById.TryGetValue(it.Id!, out var cur))
						throw new ArgumentException($"comment '{it.Id}' not found or already deleted");
					desired.Add(cur with { Version = it.Version, Body = it.Body!, Slug = ResolveSlug(it.Slug, cur.Slug, cur.NodeId, it.Id!) });
					itemByKey[it.Id!] = it;
					patchedKeys.Add(it.Id!);
				}
			}
			catch (ArgumentException ex) when (!atomic)
			{
				rejected.Add(new(it.Id ?? $"#{i}", TemporalConflictKind.Rejected, it.Version, null, ex.Message));
			}
		}

		// One atomic temporal batch (partitioned by board, so `currentVersion` is the board's
		// comment cursor). FTS is re-indexed inside the tx — the same Class-A discipline as Add/Edit.
		var fts = new SqliteFtsIndex(() => ctx);
		var r = await TemporalStore.UpsertAsync(ctx, desired, [],
			new TemporalBatchPolicy(atomic, rejected), 0,
			onWithinTx: async (tx, upserted, _, c) =>
			{
				foreach (var u in upserted)
					await fts.IndexAsync(tx, TasksSearchDocs.CommentToDoc(u, projectKey), c);
			},
			partition: x => x.Board == board, ct: ct);

		// r.Added/r.Updated are the delta since sinceVersion (0 here → the whole board's active
		// comments). The ECHO must cover ONLY this call (like tasks_upsert/memory_upsert): keep just
		// the rows whose key is in THIS batch, and — when the batch was REJECTED — nothing at all
		// (applied:false ⇒ nothing written, added/updated empty).
		//
		// Added vs Updated does NOT trust the raw Created==Updated delta split for a PATCHED key:
		// that split only means "brand new" against a REAL sinceVersion cursor, and this call always
		// passes 0, so a still-v1 comment whose PATCH was a genuine SamePayload no-op (a tags-only
		// edit resubmits an identical Body — comments_upsert has no other way to touch ONLY tags)
		// reads as "added" even though nothing was inserted (tasks-upsert-edit-reported-as-added —
		// the same defect class, here for comments_upsert). A key in `patchedKeys` came from
		// `currentById` (an ACTIVE row read before this call) — it is ALWAYS an edit, never a
		// create, so it is forced into Updated regardless of the raw split. This also fixes a real
		// behavior bug below: a misclassified-as-added PATCH used to hit the CREATE tag branch
		// ("null -> none"), silently clearing tags on a tags-omitted PATCH.
		var mine = r.Applied ? r.Added.Concat(r.Updated).Where(x => itemByKey.ContainsKey(x.Key)).ToList() : [];
		var mineAdded = mine.Where(x => !patchedKeys.Contains(x.Key)).ToList();
		var mineUpdated = mine.Where(x => patchedKeys.Contains(x.Key)).ToList();
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
		using var ctx = _factory.NewEnsuredConnection(projectKey);
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
		var indexes = new List<ISearchIndex> { new SqliteFtsIndex(() => _factory.NewEnsuredConnection(projectKey)) };
		var k = limit > 0 ? Math.Max(limit * 3, 50) : 200;
		// The candidate budget caps the fused pool on EVERY ranking path, reranked or not
		// (rerank-budget-params-to-settings) — resolved from settings at this project's scope.
		var budget = await RerankCandidateBudget.ResolveAsync(_settings, projectKey, ct);
		var resp = await new SearchService(indexes, budget: budget).SearchAsync(projectKey, q, new SearchFilter(board), k, ct: ct);

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
		using var ctx = _factory.NewEnsuredConnection(projectKey);
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
		using var ctx = _factory.NewEnsuredConnection(projectKey);
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

		using var ctx = _factory.NewEnsuredConnection(projectKey);

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
			Key = id,
			Version = 0,
			Board = board,
			NodeId = nodeId,
			ParentId = string.IsNullOrEmpty(parentId) ? null : parentId,
			Author = author ?? string.Empty,
			Body = body,
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

		using var ctx = _factory.NewEnsuredConnection(projectKey);
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
		using var ctx = _factory.NewEnsuredConnection(projectKey);
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
		using var ctx = _factory.NewEnsuredConnection(projectKey);
		var rows = await ctx.GetTable<CommentRow>()
			.Where(c => c.Board == board && c.NodeId == nodeId && c.ActiveTo == null).ToListAsync(ct);
		var tags = await TagsForAsync(ctx, board, ct);
		return rows.OrderBy(r => r.Created).Select(r => ToView(r, tags)).ToList();
	}

	public async Task<ILookup<string, CommentView>> ListForBoardAsync(
		string projectKey, string board, CancellationToken ct = default)
	{
		using var ctx = _factory.NewEnsuredConnection(projectKey);
		var rows = await ctx.GetTable<CommentRow>()
			.Where(c => c.Board == board && c.ActiveTo == null).ToListAsync(ct);
		var tags = await TagsForAsync(ctx, board, ct);
		return rows.OrderBy(r => r.Created).Select(r => ToView(r, tags)).ToLookup(v => v.NodeId, StringComparer.Ordinal);
	}

	public async Task<IReadOnlyDictionary<string, int>> CountForBoardAsync(
		string projectKey, string board, CancellationToken ct = default)
	{
		using var ctx = _factory.NewEnsuredConnection(projectKey);
		// GROUP BY NodeId, COUNT(*) — no Body/Author/tags in the SELECT list at all, unlike
		// ListForBoardAsync above (which the board page used to call for a full thread render).
		var counts = await ctx.GetTable<CommentRow>()
			.Where(c => c.Board == board && c.ActiveTo == null)
			.GroupBy(c => c.NodeId)
			.Select(g => new { NodeId = g.Key, Count = g.Count() })
			.ToListAsync(ct);
		return counts.ToDictionary(x => x.NodeId, x => x.Count, StringComparer.Ordinal);
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
			tags[r.Key].OrderBy(t => t, StringComparer.Ordinal).ToList(), r.Version, r.Created, r.Updated, r.Slug);

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

	private static HashSet<string> NormalizeTags(IReadOnlyList<string>? tags)
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
