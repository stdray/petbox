using System.ComponentModel;
using ModelContextProtocol.Server;
using PetBox.Core.Auth;
using PetBox.Core.Contract;
using PetBox.Core.Features;
using PetBox.Tasks.Contract;
using PetBox.Web.Mcp.Contract;

namespace PetBox.Web.Mcp;

// MCP surface for node comments: a generic, editable, tree-structured discussion thread
// under any task node (idea/task/spec/…). Comments are NOT task nodes — they never appear
// in tasks_search / the workflow / delivery. Tree via parentId; tags are OPEN (e.g.
// `artifact:<slug>` marks a key deliberation artifact). Scopes: tasks:read / tasks:write.
// Feature: Tasks. Reaches the module only through the boundary doors: ICommentService for
// the thread itself, ITasksService to resolve the uniform slug-or-NodeId node ref (a slug
// resolves on the given board — comments are board-scoped, unlike relations).
//
// The comments family is on the uniform-entity-verbs matrix, mirroring tasks/memory:
//   comments_upsert (batch write) · comments_search (list = search without q) ·
//   comments_delta (cursor/catch-up) · comments_get (addressed single read) · comments_delete.
//
// Tools throw on a failed Assert* (or a business-rule reject, e.g. deleting a comment with
// replies); McpErrorEnvelopeFilter renders the exception as the structured {error} body.
// TENANT DECLARATION (spec authz-scope-declaration): the `projectKey` ARGUMENT, for all five verbs.
// Manual coverage was already complete (five AssertProject calls, one per verb, all against the same
// ProjectScope the decision point uses), so enforcement changes no allow/deny outcome — only that the
// refusal now precedes the feature gate, the node-ref resolution and the tool body. Those five calls
// are removed here; the tasks:read / tasks:write scope guards stay (a different axis).
[McpServerToolType]
[TenantFrom(TenantSource.Argument, "projectKey")]
public static class CommentTools
{
	[McpServerTool(Name = "comments_upsert", Title = "Upsert node comments", UseStructuredContent = true, OutputSchemaType = typeof(CommentsUpsertResult))]
	[Description("""
		Batch declarative upsert of node comments (uniform-entity-verbs). Each item: {id?, node?,
		parentId?, author?, body, tags?, version?}. `id` ABSENT ⇒ CREATE (needs node + author;
		parentId = a COMMENT id, NOT a node ref, makes it a reply); `id` PRESENT ⇒ PATCH body and, when `tags` is given, the WHOLE tag set — `tags:[]`
		CLEARS it, omit `tags` to leave it as-is — under a `version` WATERMARK (a stale baseline ⇒ conflict, never clobber; version:0 = new,
		exactly like tasks_upsert). `body` is GFM markdown — `##` headings and REAL newlines, NOT
		literal `\n`, NOT `==headings==`. `applied` is the SINGLE source of truth — false = nothing
		written, see conflicts[]. Requires tasks:write.
		`fragment` is a POINT edit of `body`: a list of {old, new} applied IN ORDER to the CURRENT
		text, so the call costs the size of the CHANGE, not the size of the whole body. Mutually
		exclusive with `body`. Each `old` must occur EXACTLY once — zero matches or two or more
		REFUSE the write through conflicts[] (never a first-match guess, never a partial apply),
		and a list is all-or-nothing. `new` is required; send "" to delete the matched text.
		PATCH only — a create (no id) has no current text for `old` to match.
		""" + "\n\t\t" + ModuleMcp.SizeGuidanceText + """

		[[full]]
		Batch declarative upsert of node comments (a discussion thread separate from the plan) —
		the uniform write verb that replaced comments_create + comments_edit. `items` is a JSON
		array — it must be non-empty; an empty array is REJECTED ("'items': empty batch — nothing
		to write"), never a silent no-op. Each item is one of:
		  • CREATE — `id` absent/null. Requires `node` (the owner node, given as a node reference —
		    its slug key on `board` or its 32-hex NodeId, both accepted) and `author`. `parentId`
		    is a COMMENT id, NOT a node reference: it makes the item a
		    REPLY — it must be an active comment under the SAME node, else the batch is rejected.
		  • PATCH — `id` present (an existing comment id). Updates `body` and, when `tags` is given,
		    replaces the tag set (omitted `tags` leaves it as-is). You cannot re-parent in v1.
		`version` is the WATERMARK baseline for a PATCH: pass the board's comment `currentVersion`
		from your last read OR the comment's own version — both valid; 0 = a new comment. A stale
		baseline (the comment moved on) returns a conflict instead of clobbering.
		`tags` are OPEN strings (the convention `artifact:<slug>` flags a key deliberation artifact,
		e.g. `artifact:spec_plan`). `body` renders as GFM markdown — use `##` headings and real
		newlines (NOT `\n` literals, NOT `==headings==`).
		ATOMIC batch: any conflict aborts the WHOLE call (nothing is written) — mirrors tasks_upsert.
		Returns { applied, currentVersion, added[], updated[], removed[], conflicts[] }. `applied`
		is the SINGLE source of truth: FALSE = nothing written (conflicts[] carry each rejected id's
		baseline vs active version; added/updated EMPTY). When TRUE, added/updated carry this call's
		created/edited comments (id, node, parentId, author, tags, version); `body` follows the
		uniform bodyLen knob (omitted here = NO body, a compact ack). `currentVersion` is the board's
		comment cursor — pass it to comments_delta as `sinceVersion` for the full delta. To delete a
		comment use comments_delete (delete is not folded into upsert).
		`warning` (optional) is set when an APPLIED call's request payload was large enough to
		risk the client-side truncation described above — informational, never a refusal (the
		write already landed); omitted the rest of the time. Requires tasks:write.
		""")]
	public static async Task<CommentsUpsertResult> UpsertAsync(
		IHttpContextAccessor http, FeatureFlags features, ICommentService comments, ITasksService tasks,
		string projectKey, string board,
		[Description("Array of comment items: { id? (omit to CREATE), node? (the owner node — a node reference: its slug key or its 32-hex NodeId, both accepted; required to create), parentId? (a COMMENT id = reply, NOT a node reference), author? (required to create), body, bodyRef? (a blob reference from POST /api/blobs/{projectKey} — its text BECOMES this comment's body; mutually exclusive with body and fragment, sending two is a refusal in conflicts[]), tags? (array of strings), version? (watermark for a PATCH; 0 = new) }. A response row's `nodeId` is a valid `node` on a later call — reading and writing address the same owner node, just under the response-only `NodeId` suffix convention.")] CommentItemInput[] items,
		[Description("Body length knob (uniform contract): omitted = NO body (the compact ack default); 0 = no body; N>0 = the first N chars (\"…\" when cut); -1 = the full body.")] int? bodyLen = null,
		[Description("Batch policy. TRUE (default) = ATOMIC: any conflict/refusal aborts the WHOLE call, nothing is written. FALSE = PARTIAL apply (explicit opt-in): valid items LAND, each refused item comes back in conflicts[] with its own reason — a STALE baseline is then a refusal of THAT ITEM, not of the call. A parentId must address an already-active comment (no intra-batch forward reference), so nothing cascades: every item is independent. A rejected CREATE has no id yet — its conflict is keyed by the item's position (\"#0\", \"#1\", …).")] bool atomic = true,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksWrite);
		// An empty batch is almost always a client bug (a filter emptied the list, the call still
		// went out) — reject it instead of silently no-opping. `items` maps 1:1 into parsed comment
		// items below (nothing is filtered out), so the raw array length IS the effective batch size.
		if (items.Length == 0)
			throw new ArgumentException("'items': empty batch — nothing to write");

		// Resolve each CREATE item's node ref (slug on `board` → 32-hex NodeId) at the adapter, so
		// the service stays free of ITasksService (comments never leak into tasks_search).
		// work/write-body-by-reference: every DISTINCT `bodyRef` in the batch is looked up ONCE, here
		// in the adapter, because the decision needs the caller's claims (see McpBodyRefs). The
		// service is handed verdicts, and words the refusals into conflicts[] beside the fragment
		// ones. Nothing is CONSUMED yet — that happens below, and only for items that landed.
		var bodyRefs = await McpBodyRefs.ResolveAsync(http, items.Select(i => i.BodyRef), ct);

		var parsed = new List<CommentItem>(items.Length);
		foreach (var i in items)
		{
			// The body/fragment choice is judged in the SERVICE, not here: it needs the current
			// row (a fragment is only meaningful against existing text) and its refusal must ride
			// conflicts[], which the adapter cannot produce. So the adapter no longer demands a
			// body — it forwards both fields and lets the merge decide.
			string? node = null;
			if (string.IsNullOrEmpty(i.Id))
			{
				if (string.IsNullOrWhiteSpace(i.Node)) throw new ArgumentException("a new comment (no id) needs node");
				node = await tasks.ResolveNodeRefAsync(projectKey, i.Node!, board, ct);
			}
			parsed.Add(new CommentItem(i.Id, node, i.ParentId, i.Author, i.Body, i.Tags, i.Version,
				FragmentEditDto.ToCore(i.Fragment), bodyRefs.For(i.BodyRef)));
		}

		var r = await comments.UpsertAsync(projectKey, board, parsed, atomic, ct);

		// ONE-SHOT, spent only on what actually landed. An item is "landed" when the call applied at
		// all AND that item is not among the conflicts — keyed by its id for a PATCH and by its
		// position for a CREATE, which is exactly how CommentService names a refused item. A blob
		// behind a REFUSED item deliberately survives, so the caller can fix the version watermark
		// and retry with the SAME ref instead of re-uploading — re-uploading on every CAS retry
		// would reintroduce, at the retry, the double payment this mechanism exists to abolish.
		if (!bodyRefs.IsEmpty)
		{
			var refused = r.Conflicts.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
			await bodyRefs.ConsumeAsync(items
				.Where((it, i) => r.Applied && !refused.Contains(it.Id ?? "") && !refused.Contains($"#{i}"))
				.Select(it => it.BodyRef), ct);
		}
		// card size-warning-not-wired-to-write-verbs, mirroring MemoryTools.UpsertAsync point 4:
		// only warn about size on a write that actually landed — a refused/conflicted call already
		// has its own signal (conflicts[]).
		var warning = r.Applied ? ModuleMcp.SizeWarningOrNull(http) : null;
		return new CommentsUpsertResult(
			r.Applied, r.CurrentVersion,
			r.Added.Select(c => Shape(c, bodyLen, ModuleMcp.NoBody)).ToList(),
			r.Updated.Select(c => Shape(c, bodyLen, ModuleMcp.NoBody)).ToList(),
			[],
			r.Conflicts,
			warning);
	}

	[McpServerTool(Name = "comments_search", Title = "Read node comments (list + search)", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(CommentsSearchResult))]
	[Description("THE comment read verb — one tool for LISTING (no `q`) and SEARCH (`q`). Without `q`: a deterministic chronological list of active comments, optionally scoped to one `board` and/or one `node` (a node reference — a slug key or a 32-hex NodeId, both accepted). With `q`: a lexical FTS relevance SELECTION over comment bodies in the same scope, NOT an enumeration (semantic isn't wired for comments yet, so a query runs on the lexical floor — `retrievers` reports semantic:false). Bodies follow the uniform bodyLen knob (omitted = a ~240-char snippet in BOTH modes, listing and `q` alike; fetch one full comment with comments_get). Hard ~30k-char output budget: overflow rows are prefix-cut + flagged (truncated/omitted/hint). Tracking changes since a known version cursor (added/updated/removed, including tombstones this search cannot show)? Use comments_delta instead — it's the way to enumerate a board's comments incrementally. Requires tasks:read.\n\nCost — your context pays it. Same query, same rows: bodyLen:0 = 1x, the default snippet ~1.5-2x, bodyLen:-1 ~3x+ and unbounded per row — a single long comment can add thousands of chars on its own.\nCheap path: search with bodyLen:0, read the row identities, then comments_get the 1-3 comments you actually need. Use -1 only when you already know the ids and there are few.\nPulling full bodies across a wide limit \"just in case\" is the most expensive habit available here: it routinely spends a third of the response budget on text you will not read.")]
	public static async Task<CommentsSearchResult> SearchAsync(
		IHttpContextAccessor http, FeatureFlags features, ICommentService comments, ITasksService tasks,
		string projectKey,
		[LogArg(LogArgMode.Presence)][Description("Search query. Omit for a deterministic chronological listing (list = search without q).")] string? q = null,
		[Description("Scope to one board. Omit = the whole project.")] string? board = null,
		[Description("Scope to one owner node: a node reference — its slug key or its 32-hex NodeId (both accepted). The slug resolves on `board` when `board` is given; when `board` is omitted it resolves PROJECT-WIDE and must be unambiguous (2+ boards sharing the slug is an error naming them — pass the NodeId then). A node that matches nothing → an empty result (not an error). A response row's `nodeId` is a valid `node` here — reading and writing address the same owner node.")] string? node = null,
		[LogArg][Description("Body length knob (uniform contract): omitted = a ~240-char snippet, in a listing or with q alike; 0 = no body; N>0 = the first N chars (\"…\" when cut); -1 = the full body.")] int? bodyLen = null,
		[LogArg][Description("Max rows returned. Default: unbounded listing / 20 with q (0 = no cap).")] int? limit = null,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksRead);

		var hasQuery = !string.IsNullOrWhiteSpace(q);
		string? resolvedNode = null;
		if (!string.IsNullOrWhiteSpace(node))
		{
			resolvedNode = await tasks.ResolveNodeRefOrNullAsync(projectKey, node, board, ct);
			if (resolvedNode is null) return new CommentsSearchResult([]); // no such node → an empty result (soft read)
		}

		var res = await comments.SearchAsync(projectKey, board, resolvedNode, q, limit ?? (hasQuery ? DefaultSearchLimit : 0), ct);
		// Uniform bodyLen: a ~240-char snippet by default in BOTH modes (listing and with q) —
		// same ModuleMcp.DefaultSnippet constant tasks_search/memory_search use, not a second
		// number. Shaped BEFORE the budget so it measures the real wire payload. A full comment
		// body is still one comments_get away.
		var rows = res.Items.Select(c => Shape(c, bodyLen, ModuleMcp.DefaultSnippet)).ToList();
		var (kept, omitted) = new ResponseBudget().Take(rows);
		var retrievers = res.Retrievers is { } r ? new RetrieverInfo(r.Lexical, r.Semantic, r.Degraded, r.DegradedReason) : null;
		return omitted == 0
			? new CommentsSearchResult(kept, retrievers)
			: new CommentsSearchResult(kept, retrievers, Truncated: true, Omitted: omitted, Hint: SearchBudgetHint);
	}

	[McpServerTool(Name = "comments_delta", Title = "Comments delta since cursor", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(CommentsUpsertResult))]
	[Description("Return comments added/updated/removed on a board since `sinceVersion` (no writes) — THE cursor/catch-up surface and the way to enumerate a board's comments incrementally (comments_search's `q` is a relevance slice, never an enumeration; a comments_upsert ack echoes only its own call — pass its `currentVersion` here for the full board comment delta). Bodies follow the uniform bodyLen knob (compact by default). Requires tasks:read.")]
	public static async Task<CommentsUpsertResult> DeltaAsync(
		IHttpContextAccessor http, FeatureFlags features, ICommentService comments,
		string projectKey, string board, long sinceVersion,
		[Description("Body length knob (uniform contract): omitted = NO body (compact default); 0 = no body; N>0 = the first N chars (\"…\" when cut); -1 = the full body.")] int? bodyLen = null,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksRead);
		var d = await comments.DeltaAsync(projectKey, board, sinceVersion, ct);
		return new CommentsUpsertResult(
			Applied: true, d.CurrentVersion,
			d.Added.Select(c => Shape(c, bodyLen, ModuleMcp.NoBody)).ToList(),
			d.Updated.Select(c => Shape(c, bodyLen, ModuleMcp.NoBody)).ToList(),
			d.Removed,
			[]);
	}

	[McpServerTool(Name = "comments_get", Title = "Get one comment in full", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(CommentView))]
	[Description("Return ONE comment in FULL by its id (the addressed single read; mirrors memory_get/tasks_node_get). A missing/deleted id is a not-found ERROR (never a bare null — a declared outputSchema demands structured content, so the error rides the isError channel). The body is COMPLETE by default; the uniform bodyLen knob still applies. Requires tasks:read.")]
	public static async Task<CommentView> GetAsync(
		IHttpContextAccessor http, FeatureFlags features, ICommentService comments,
		string projectKey, string id,
		[LogArg][Description("Body length knob (uniform contract): omitted = the FULL body (this is the pointed full read); 0 = no body; N>0 = the first N chars (\"…\" when cut); -1 = the full body.")] int? bodyLen = null,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksRead);
		var c = await comments.GetAsync(projectKey, id, ct)
			?? throw new InvalidOperationException($"comment '{id}' not found or already deleted in project '{projectKey}'");
		return Shape(c, bodyLen, ModuleMcp.FullBody);
	}

	[McpServerTool(Name = "comments_delete", Title = "Delete a node comment", Destructive = true, UseStructuredContent = true, OutputSchemaType = typeof(CommentDeleteResult))]
	[Description("Soft-delete a comment. REJECTED if it still has active replies — delete the children first. Returns {deleted}. Requires tasks:write.")]
	public static async Task<CommentDeleteResult> DeleteAsync(
		IHttpContextAccessor http, FeatureFlags features, ICommentService comments,
		string projectKey, string board, string id,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksWrite);
		return new CommentDeleteResult(await comments.DeleteAsync(projectKey, board, id, ct));
	}

	// With a query the answer is capped even when the caller asks for nothing specific.
	const int DefaultSearchLimit = 20;

	// Apply the uniform bodyLen contract to one comment's wire body (null → the serializer omits it).
	static CommentView Shape(CommentView c, int? bodyLen, int dflt) =>
		c with { Body = ModuleMcp.Body(c.Body, bodyLen, dflt) ?? string.Empty };

	// Surfaced on CommentsSearchResult.Hint when the rows were cut by the response budget.
	const string SearchBudgetHint =
		"Output budget exceeded: comment rows were truncated (see truncated/omitted). Narrow the " +
		"read: `node` (one node's thread), `board` (one board), `q` (a relevance selection), " +
		"`bodyLen` (snippet bodies), a smaller `limit`, comments_get for one full comment — or, " +
		"for the COMPLETE set, comments_delta (sinceVersion:0 enumerates from scratch).";
}
