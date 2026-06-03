using System.ComponentModel;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Server;
using PetBox.Core.Auth;
using PetBox.Core.Features;
using PetBox.Tasks.Data;

namespace PetBox.Web.Mcp;

// MCP surface for typed relations between node ids (task↔spec, issue→task, etc.).
// Edges bind to the stable PlanNode.NodeId, so they survive renames. Scopes:
// tasks:read / tasks:write. Feature: Tasks.
[McpServerToolType]
public static class RelationTools
{
	[McpServerTool(Name = "relations.create", Title = "Create a relation")]
	[Description("Create a typed directed edge between two node ids. kind ∈ task_spec|issue_task|idea_spec|blocks|part_of|supersedes. Idempotent (identical edge is returned, not duplicated). Node ids are stable PlanNode.NodeId values (from tasks.upsert/tasks.get). Requires tasks:write.")]
	public static Task<object> CreateAsync(
		IHttpContextAccessor http, FeatureFlags features, IRelationStore relations,
		string projectKey, string kind, string fromNodeId, string toNodeId,
		CancellationToken ct = default) => ModuleMcp.GuardAsync(async () =>
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksWrite);
		var rel = await relations.CreateAsync(projectKey, kind, fromNodeId, toNodeId, ct);
		return (object)new { rel.Id, rel.Kind, rel.FromNodeId, rel.ToNodeId };
	});

	[McpServerTool(Name = "relations.list", Title = "List relations", ReadOnly = true)]
	[Description("List relations touching a node id. direction ∈ from|to|both (default both). Use direction=to to find edges pointing AT a node (reverse traversal, e.g. which tasks implement a spec node). includeHistory=true also returns soft-closed edges (with closedAt). Requires tasks:read.")]
	public static Task<object> ListAsync(
		IHttpContextAccessor http, FeatureFlags features, IRelationStore relations,
		string projectKey, string nodeId, string? direction = null, bool includeHistory = false,
		CancellationToken ct = default) => ModuleMcp.GuardAsync(async () =>
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksRead);
		var list = await relations.ListAsync(projectKey, nodeId, direction ?? "both", includeHistory, ct);
		return (object)new { relations = list.Select(r => new { r.Id, r.Kind, r.FromNodeId, r.ToNodeId, r.CreatedAt, r.ClosedAt }).ToList() };
	});

	[McpServerTool(Name = "relations.delete", Title = "Delete a relation", Destructive = true)]
	[Description("Delete a relation by id. Requires tasks:write.")]
	public static Task<object> DeleteAsync(
		IHttpContextAccessor http, FeatureFlags features, IRelationStore relations,
		string projectKey, string id, CancellationToken ct = default) => ModuleMcp.GuardAsync(async () =>
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksWrite);
		return (object)new { deleted = await relations.DeleteAsync(projectKey, id, ct) };
	});
}
