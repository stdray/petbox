using System.ComponentModel;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Server;
using PetBox.Core.Auth;
using PetBox.Core.Contract;
using PetBox.Core.Features;
using PetBox.Tasks.Contract;
using PetBox.Web.Mcp.Contract;

namespace PetBox.Web.Mcp;

// MCP surface for node comments: a generic, editable, tree-structured discussion thread
// under any plan node (idea/task/spec/…). Comments are NOT plan nodes — they never appear
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
[McpServerToolType]
public static class CommentTools
{
	[McpServerTool(Name = "comments_upsert", Title = "Upsert node comments", UseStructuredContent = true, OutputSchemaType = typeof(CommentsUpsertResult))]
	[Description("""
		Batch declarative upsert of node comments (uniform-entity-verbs). Each item: {id?, nodeId?,
		parentId?, author?, body, tags?, version?}. `id` ABSENT ⇒ CREATE (needs nodeId + author;
		parentId = a COMMENT id, NOT a node ref, makes it a reply); `id` PRESENT ⇒ PATCH body/tags
		under a `version` WATERMARK (a stale baseline ⇒ conflict, never clobber; version:0 = new,
		exactly like tasks_upsert). `body` is GFM markdown — `##` headings and REAL newlines, NOT
		literal `\n`, NOT `==headings==`. `applied` is the SINGLE source of truth — false = nothing
		written, see conflicts[]. Requires tasks:write.
		[[full]]
		Batch declarative upsert of node comments (a discussion thread separate from the plan) —
		the uniform write verb that replaced comments_create + comments_edit. `items` is a JSON
		array; each item is one of:
		  • CREATE — `id` absent/null. Requires `nodeId` (the owner node: its slug key on `board`,
		    or its 32-hex NodeId) and `author`. `parentId` (a COMMENT id, NOT a node ref) makes it a
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
		created/edited comments (id, nodeId, parentId, author, tags, version); `body` follows the
		uniform bodyLen knob (omitted here = NO body, a compact ack). `currentVersion` is the board's
		comment cursor — pass it to comments_delta as `sinceVersion` for the full delta. To delete a
		comment use comments_delete (delete is not folded into upsert). Requires tasks:write.
		""")]
	public static async Task<CommentsUpsertResult> UpsertAsync(
		IHttpContextAccessor http, FeatureFlags features, ICommentService comments, ITasksService tasks,
		string projectKey, string board,
		[Description("Array of comment items: { id? (omit to CREATE), nodeId? (owner slug|NodeId, required to create), parentId? (a COMMENT id = reply), author? (required to create), body, tags? (array of strings), version? (watermark for a PATCH; 0 = new) }.")] CommentItemInput[] items,
		[Description("Body length knob (uniform contract): omitted = NO body (the compact ack default); 0 = no body; N>0 = the first N chars (\"…\" when cut); -1 = the full body.")] int? bodyLen = null,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksWrite);

		// Resolve each CREATE item's node ref (slug on `board` → 32-hex NodeId) at the adapter, so
		// the service stays free of ITasksService (comments never leak into tasks_search).
		var parsed = new List<CommentItem>(items.Length);
		foreach (var i in items)
		{
			var body = i.Body ?? throw new ArgumentException("each comment item needs a body");
			string? node = null;
			if (string.IsNullOrEmpty(i.Id))
			{
				if (string.IsNullOrWhiteSpace(i.NodeId)) throw new ArgumentException("a new comment (no id) needs nodeId");
				node = await tasks.ResolveNodeRefAsync(projectKey, i.NodeId!, board, ct);
			}
			parsed.Add(new CommentItem(i.Id, node, i.ParentId, i.Author, body, i.Tags, i.Version));
		}

		var r = await comments.UpsertAsync(projectKey, board, parsed, ct);
		return new CommentsUpsertResult(
			r.Applied, r.CurrentVersion,
			r.Added.Select(c => Shape(c, bodyLen, ModuleMcp.NoBody)).ToList(),
			r.Updated.Select(c => Shape(c, bodyLen, ModuleMcp.NoBody)).ToList(),
			[],
			r.Conflicts);
	}

	[McpServerTool(Name = "comments_search", Title = "Read node comments (list + search)", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(CommentsSearchResult))]
	[Description("THE comment read verb — one tool for LISTING (no `q`) and SEARCH (`q`). Without `q`: a deterministic chronological list of active comments, optionally scoped to one `board` and/or one `nodeId` (slug|NodeId). With `q`: a lexical FTS relevance selection over comment bodies in the same scope (semantic isn't wired for comments yet, so a query runs on the lexical floor — `retrievers` reports semantic:false). Bodies follow the uniform bodyLen knob (omitted = FULL in a listing / a ~240-char snippet with `q`, or fetch one full comment with comments_get). Hard ~30k-char output budget: overflow rows are prefix-cut + flagged (truncated/omitted/hint). Requires tasks:read.\n\nCost — your context pays it. Same query, same rows: bodyLen:0 = 1x, a snippet ~1.5-2x, bodyLen:-1 (and the listing's FULL default) ~3x+ and unbounded per row — a single long comment can add thousands of chars on its own.\nCheap path: search with bodyLen:0, read the row identities, then comments_get the 1-3 comments you actually need. Use -1 only when you already know the ids and there are few.\nPulling full bodies across a wide limit \"just in case\" is the most expensive habit available here: it routinely spends a third of the response budget on text you will not read.")]
	public static async Task<CommentsSearchResult> SearchAsync(
		IHttpContextAccessor http, FeatureFlags features, ICommentService comments, ITasksService tasks,
		string projectKey,
		[LogArg(LogArgMode.Presence)][Description("Search query. Omit for a deterministic chronological listing (list = search without q).")] string? q = null,
		[Description("Scope to one board. Omit = the whole project.")] string? board = null,
		[Description("Scope to one owner node: its slug key on `board`, or its 32-hex NodeId. A node that matches nothing → an empty result (not an error).")] string? nodeId = null,
		[LogArg][Description("Body length knob (uniform contract): omitted = FULL in a listing / a ~240-char snippet with q; 0 = no body; N>0 = the first N chars (\"…\" when cut); -1 = the full body.")] int? bodyLen = null,
		[LogArg][Description("Max rows returned. Default: unbounded listing / 20 with q (0 = no cap).")] int? limit = null,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksRead);

		var hasQuery = !string.IsNullOrWhiteSpace(q);
		string? node = null;
		if (!string.IsNullOrWhiteSpace(nodeId))
		{
			node = await tasks.ResolveNodeRefOrNullAsync(projectKey, nodeId, board, ct);
			if (node is null) return new CommentsSearchResult([]); // no such node → an empty result (soft read)
		}

		var res = await comments.SearchAsync(projectKey, board, node, q, limit ?? (hasQuery ? DefaultSearchLimit : 0), ct);
		// Uniform bodyLen (default FULL in a listing — the discussion; a ~240-char snippet with q),
		// shaped BEFORE the budget so it measures the real wire payload.
		var dflt = hasQuery ? ModuleMcp.DefaultSnippet : ModuleMcp.FullBody;
		var rows = res.Items.Select(c => Shape(c, bodyLen, dflt)).ToList();
		var (kept, omitted) = new ResponseBudget().Take(rows);
		var retrievers = res.Retrievers is { } r ? new RetrieverInfo(r.Lexical, r.Semantic, r.Degraded, r.DegradedReason) : null;
		return omitted == 0
			? new CommentsSearchResult(kept, retrievers)
			: new CommentsSearchResult(kept, retrievers, Truncated: true, Omitted: omitted, Hint: SearchBudgetHint);
	}

	[McpServerTool(Name = "comments_delta", Title = "Comments delta since cursor", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(CommentsUpsertResult))]
	[Description("Return comments added/updated/removed on a board since `sinceVersion` (no writes) — THE cursor/catch-up surface (a comments_upsert ack echoes only its own call; pass its `currentVersion` here for the full board comment delta). Bodies follow the uniform bodyLen knob (compact by default). Requires tasks:read.")]
	public static async Task<CommentsUpsertResult> DeltaAsync(
		IHttpContextAccessor http, FeatureFlags features, ICommentService comments,
		string projectKey, string board, long sinceVersion,
		[Description("Body length knob (uniform contract): omitted = NO body (compact default); 0 = no body; N>0 = the first N chars (\"…\" when cut); -1 = the full body.")] int? bodyLen = null,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertProject(http, projectKey);
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
		ModuleMcp.AssertProject(http, projectKey);
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
		ModuleMcp.AssertProject(http, projectKey);
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
		"read: `nodeId` (one node's thread), `board` (one board), `q` (a relevance selection), " +
		"`bodyLen` (snippet bodies), a smaller `limit`, or comments_get for one full comment.";
}
