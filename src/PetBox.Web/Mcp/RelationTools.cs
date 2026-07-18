using System.ComponentModel;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Server;
using PetBox.Core.Auth;
using PetBox.Core.Features;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Data;
using PetBox.Web.Mcp.Contract;

namespace PetBox.Web.Mcp;

// MCP surface for typed relations between nodes (task↔spec, issue→task, etc.).
// Edges bind to the stable PlanNode.NodeId, so they survive renames. Node refs are
// uniform slug-or-NodeId (uniform-node-refs): a 32-hex value is a NodeId; a slug is
// resolved via ITasksService across the whole project (these tools carry no board
// context), erroring on ambiguity. Scopes: tasks:read / tasks:write. Feature: Tasks.
// Tools throw on a failed Assert*; McpErrorEnvelopeFilter renders the exception as
// the structured {error} body.
[McpServerToolType]
public static class RelationTools
{
	[McpServerTool(Name = "relations_create", Title = "Create a relation", UseStructuredContent = true, OutputSchemaType = typeof(RelationsCreatedResult))]
	[Description("CREATE (idempotent) typed directed edge(s) between nodes — an identical existing edge is returned, not duplicated. BATCH form: items:[{kind, from, to}, …] (from/to = slug|NodeId; fromNodeId/toNodeId accepted as aliases). SINGLE form: omit items and pass kind + fromNodeId + toNodeId. All items are validated before any write — a bad item fails the whole batch, naming its index. kind: process kinds task_spec|issue_task|idea_spec|blocks|part_of|supersedes (carry FSM effects/guards), NEUTRAL kinds relates_to|depends_on|mirrors (free semantic edges between any nodes — no FSM effects, no process meaning), plus any kinds the FROM node's methodology instance declares (linkKinds — also effect-free). An unknown kind is rejected listing every kind valid for that instance (or the project singleton when the board has no instance membership). from/to each take a slug or NodeId: a 32-hex value is the stable PlanNode.NodeId (from tasks_upsert/tasks_search); a slug resolves across ALL the project's boards and must be unambiguous — the same slug on 2+ boards is an error naming the boards (pass the NodeId then). Returns {relations:[{id,kind,fromNodeId,toNodeId},…]}. Requires tasks:write.")]
	public static async Task<RelationsCreatedResult> CreateAsync(
		IHttpContextAccessor http, FeatureFlags features, IRelationStore relations, ITasksService tasks,
		string projectKey,
		[Description("Single-form kind (when items is omitted).")] string? kind = null,
		[Description("Single-form source node: slug or NodeId (when items is omitted).")] string? fromNodeId = null,
		[Description("Single-form target node: slug or NodeId (when items is omitted).")] string? toNodeId = null,
		[Description("Batch items: [{kind, from, to}] (fromNodeId/toNodeId accepted as aliases). Prefer this for multi-edge creates.")] RelationCreateItemInput[]? items = null,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		await ModuleMcp.AssertProject(http, projectKey, ct);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksWrite);

		var (batch, singleForm) = NormalizeCreateItems(items, kind, fromNodeId, toNodeId);
		// Resolve+validate ALL items first so a bad ref never partially writes earlier edges.
		var resolved = new List<(string Kind, string From, string To)>(batch.Length);
		for (var i = 0; i < batch.Length; i++)
		{
			var item = batch[i];
			try
			{
				if (item is null)
					throw new ArgumentException("item is null");
				if (string.IsNullOrWhiteSpace(item.Kind))
					throw new ArgumentException("kind is required");
				var fromRef = ItemFrom(item);
				var toRef = ItemTo(item);
				if (string.IsNullOrWhiteSpace(fromRef))
					throw new ArgumentException("from (or fromNodeId) is required");
				if (string.IsNullOrWhiteSpace(toRef))
					throw new ArgumentException("to (or toNodeId) is required");
				// Resolve endpoints first so the kind vocabulary can be scoped to the FROM node's
				// board → methodology instance (methodology-instance-scoped-axes). The store itself
				// only checks structure.
				var from = await tasks.ResolveNodeRefAsync(projectKey, fromRef, ct: ct);
				var to = await tasks.ResolveNodeRefAsync(projectKey, toRef, ct: ct);
				var k = await tasks.ValidateRelationKindAsync(projectKey, item.Kind, from, to, ct);
				resolved.Add((k, from, to));
			}
			catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
			{
				// Single-form (BC) keeps its original, unprefixed message; batch items are tagged by index.
				if (singleForm) throw;
				throw new ArgumentException($"items[{i}]: {ex.Message}", ex);
			}
		}

		var created = new List<RelationCreatedResult>(resolved.Count);
		foreach (var (k, from, to) in resolved)
		{
			var rel = await relations.CreateAsync(projectKey, k, from, to, ct);
			created.Add(new RelationCreatedResult(rel.Id, rel.Kind, rel.FromNodeId, rel.ToNodeId));
		}
		return new RelationsCreatedResult(created);
	}

	[McpServerTool(Name = "relations_list", Title = "List relations", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(RelationsListResult))]
	[Description("List relations touching a node. nodeId takes a slug or NodeId (32-hex = the stable PlanNode.NodeId; a slug resolves across ALL the project's boards). A nodeId that matches no node → an empty list (not an error); an ambiguous cross-board slug still needs a NodeId. direction ∈ from|to|both (default both). Use direction=to to find edges pointing AT a node (reverse traversal, e.g. which tasks implement a spec node). includeHistory=true also returns soft-closed edges (with closedAt). Requires tasks:read.")]
	public static async Task<RelationsListResult> ListAsync(
		IHttpContextAccessor http, FeatureFlags features, IRelationStore relations, ITasksService tasks,
		string projectKey,
		[Description("The node: slug or NodeId (a slug resolves project-wide and must be unambiguous).")] string nodeId,
		string? direction = null, bool includeHistory = false,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		await ModuleMcp.AssertProject(http, projectKey, ct);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksRead);
		var id = await tasks.ResolveNodeRefOrNullAsync(projectKey, nodeId, ct: ct);
		if (id is null) return new RelationsListResult([]); // a node that isn't there → no edges (soft read), never an error
		var list = await relations.ListAsync(projectKey, id, direction ?? "both", includeHistory, ct);
		return new RelationsListResult(list.Select(r => new RelationRow(r.Id, r.Kind, r.FromNodeId, r.ToNodeId, r.CreatedAt, r.ClosedAt)).ToList());
	}

	[McpServerTool(Name = "relations_delete", Title = "Delete a relation", Destructive = true, UseStructuredContent = true, OutputSchemaType = typeof(RelationsDeletedResult))]
	[Description("Soft-delete relation edge(s) by edge id (from relations_create/relations_list — not a node ref). BATCH form: ids:[…]. SINGLE form: omit ids and pass id. Returns {relations:[{id,deleted},…]} — deleted=false when the id was already closed or unknown. Requires tasks:write.")]
	public static async Task<RelationsDeletedResult> DeleteAsync(
		IHttpContextAccessor http, FeatureFlags features, IRelationStore relations,
		string projectKey,
		[Description("Single-form edge id (when ids is omitted).")] string? id = null,
		[Description("Batch of edge ids to soft-close.")] string[]? ids = null,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		await ModuleMcp.AssertProject(http, projectKey, ct);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksWrite);

		var batch = NormalizeDeleteIds(ids, id);
		var results = new List<RelationDeletedResult>(batch.Length);
		foreach (var edgeId in batch)
		{
			var deleted = await relations.DeleteAsync(projectKey, edgeId, ct);
			results.Add(new RelationDeletedResult(edgeId, deleted));
		}
		return new RelationsDeletedResult(results);
	}

	// Returns the item batch plus whether it came from the single-form (BC) path — single-form
	// errors are rethrown verbatim (no items[i] prefix) so the pre-batch wire error text is preserved.
	static (RelationCreateItemInput[] Batch, bool SingleForm) NormalizeCreateItems(
		RelationCreateItemInput[]? items, string? kind, string? fromNodeId, string? toNodeId)
	{
		var hasSingle = !string.IsNullOrWhiteSpace(kind) || !string.IsNullOrWhiteSpace(fromNodeId) || !string.IsNullOrWhiteSpace(toNodeId);
		if (items is { Length: > 0 })
		{
			if (hasSingle)
				throw new ArgumentException("relations_create: pass either items:[…] or single-form kind/fromNodeId/toNodeId, not both");
			return (items, false);
		}
		if (hasSingle)
		{
			return
			(
				[
					new RelationCreateItemInput
					{
						Kind = kind,
						FromNodeId = fromNodeId,
						ToNodeId = toNodeId,
					},
				],
				true
			);
		}
		throw new ArgumentException("relations_create requires items:[{kind,from,to},…] or single-form kind + fromNodeId + toNodeId");
	}

	static string[] NormalizeDeleteIds(string[]? ids, string? id)
	{
		if (ids is { Length: > 0 }) return ids;
		if (!string.IsNullOrWhiteSpace(id)) return [id];
		throw new ArgumentException("relations_delete requires ids:[…] or single-form id");
	}

	static string? ItemFrom(RelationCreateItemInput item) =>
		!string.IsNullOrWhiteSpace(item.From) ? item.From : item.FromNodeId;

	static string? ItemTo(RelationCreateItemInput item) =>
		!string.IsNullOrWhiteSpace(item.To) ? item.To : item.ToNodeId;
}
