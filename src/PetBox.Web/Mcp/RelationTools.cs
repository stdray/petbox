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
	[McpServerTool(Name = "relations_create", Title = "Create a relation", UseStructuredContent = true, OutputSchemaType = typeof(RelationCreatedResult))]
	[Description("CREATE (idempotent) a typed directed edge between two nodes — an identical existing edge is returned, not duplicated. kind: process kinds task_spec|issue_task|idea_spec|blocks|part_of|supersedes (carry FSM effects/guards), NEUTRAL kinds relates_to|depends_on|mirrors (free semantic edges between any nodes — no FSM effects, no process meaning), plus any kinds the project's methodology definition declares (tasks_methodology_def_upsert linkKinds — also effect-free). An unknown kind is rejected listing every kind valid for the project. fromNodeId/toNodeId each take a slug or NodeId: a 32-hex value is the stable PlanNode.NodeId (from tasks_upsert/tasks_search); a slug resolves across ALL the project's boards and must be unambiguous — the same slug on 2+ boards is an error naming the boards (pass the NodeId then). Requires tasks:write.")]
	public static async Task<RelationCreatedResult> CreateAsync(
		IHttpContextAccessor http, FeatureFlags features, IRelationStore relations, ITasksService tasks,
		string projectKey, string kind,
		[Description("Source node: slug or NodeId (a slug resolves project-wide and must be unambiguous).")] string fromNodeId,
		[Description("Target node: slug or NodeId (a slug resolves project-wide and must be unambiguous).")] string toNodeId,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksWrite);
		// The kind vocabulary is project-aware (builtin + definition-declared linkKinds), so
		// the service validates it; the store itself only checks structure.
		kind = await tasks.ValidateRelationKindAsync(projectKey, kind, ct);
		var from = await tasks.ResolveNodeRefAsync(projectKey, fromNodeId, ct: ct);
		var to = await tasks.ResolveNodeRefAsync(projectKey, toNodeId, ct: ct);
		var rel = await relations.CreateAsync(projectKey, kind, from, to, ct);
		return new RelationCreatedResult(rel.Id, rel.Kind, rel.FromNodeId, rel.ToNodeId);
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
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksRead);
		var id = await tasks.ResolveNodeRefOrNullAsync(projectKey, nodeId, ct: ct);
		if (id is null) return new RelationsListResult([]); // a node that isn't there → no edges (soft read), never an error
		var list = await relations.ListAsync(projectKey, id, direction ?? "both", includeHistory, ct);
		return new RelationsListResult(list.Select(r => new RelationRow(r.Id, r.Kind, r.FromNodeId, r.ToNodeId, r.CreatedAt, r.ClosedAt)).ToList());
	}

	[McpServerTool(Name = "relations_delete", Title = "Delete a relation", Destructive = true, UseStructuredContent = true, OutputSchemaType = typeof(RelationDeletedResult))]
	[Description("Delete a relation by its edge id (from relations_create/relations_list — not a node ref). Requires tasks:write.")]
	public static async Task<RelationDeletedResult> DeleteAsync(
		IHttpContextAccessor http, FeatureFlags features, IRelationStore relations,
		string projectKey, string id, CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksWrite);
		return new RelationDeletedResult(await relations.DeleteAsync(projectKey, id, ct));
	}
}
