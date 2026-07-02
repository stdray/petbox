using System.ComponentModel;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Server;
using PetBox.Core.Auth;
using PetBox.Core.Features;
using PetBox.Tasks.Contract;
using PetBox.Web.Mcp.Contract;

namespace PetBox.Web.Mcp;

// MCP surface for node comments: a generic, editable, tree-structured discussion thread
// under any plan node (idea/task/spec/…). Comments are NOT plan nodes — they never appear
// in tasks.get / the workflow / delivery. Tree via parentId; tags are OPEN (e.g.
// `artifact:<slug>` marks a key deliberation artifact). Scopes: tasks:read / tasks:write.
// Feature: Tasks. Reaches the module only through the boundary doors: ICommentService for
// the thread itself, ITasksService to resolve the uniform slug-or-NodeId node ref (a slug
// resolves on the given board — comments are board-scoped, unlike relations).
//
// Tools throw on a failed Assert* (or a business-rule reject, e.g. deleting a comment with
// replies); McpErrorEnvelopeFilter renders the exception as the structured {error} body.
[McpServerToolType]
public static class CommentTools
{
	[McpServerTool(Name = "comments.add", Title = "Add a node comment", UseStructuredContent = true, OutputSchemaType = typeof(CommentUpsertResult))]
	[Description("Add a comment under a plan node (a discussion thread separate from the plan). nodeId takes a slug or NodeId (32-hex = the stable PlanNode.NodeId; a slug is the node's key on `board`). parentId (a comment id, NOT a node ref) makes it a REPLY — it must be an active comment under the same node, else rejected. tags are OPEN strings (convention `artifact:<slug>` flags a key artifact like a spec-update plan). author is caller-supplied. Returns {applied, currentVersion, id, conflicts}. Requires tasks:write.")]
	public static async Task<CommentUpsertResult> AddAsync(
		IHttpContextAccessor http, FeatureFlags features, ICommentService comments, ITasksService tasks,
		string projectKey, string board,
		[Description("The node to comment on: its slug key on `board`, or its 32-hex NodeId.")] string nodeId,
		string author, string body,
		string? parentId = null, string[]? tags = null,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksWrite);
		var id = await tasks.ResolveNodeRefAsync(projectKey, nodeId, board, ct);
		return await comments.AddAsync(projectKey, board, id, parentId, author, body, tags, ct);
	}

	[McpServerTool(Name = "comments.list", Title = "List a node's comments", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(CommentsListResult))]
	[Description("List the comment thread under a node: a FLAT list of active comments, each with parentId (build the tree from it), author, body, tags, version, timestamps. Chronological. nodeId takes a slug or NodeId (32-hex = the stable PlanNode.NodeId; a slug is the node's key on `board`). Requires tasks:read.")]
	public static async Task<CommentsListResult> ListAsync(
		IHttpContextAccessor http, FeatureFlags features, ICommentService comments, ITasksService tasks,
		string projectKey, string board,
		[Description("The node: its slug key on `board`, or its 32-hex NodeId.")] string nodeId,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksRead);
		var id = await tasks.ResolveNodeRefAsync(projectKey, nodeId, board, ct);
		var list = await comments.ListForNodeAsync(projectKey, board, id, ct);
		return new CommentsListResult(list.ToList());
	}

	[McpServerTool(Name = "comments.edit", Title = "Edit a node comment", UseStructuredContent = true, OutputSchemaType = typeof(CommentUpsertResult))]
	[Description("Edit a comment's body (and, if `tags` is provided, replace its tag set). `version` is the revision you last saw — a stale baseline returns a conflict instead of clobbering. Body/tags only; you cannot re-parent a comment in v1. Returns {applied, currentVersion, id, conflicts}. Requires tasks:write.")]
	public static async Task<CommentUpsertResult> EditAsync(
		IHttpContextAccessor http, FeatureFlags features, ICommentService comments,
		string projectKey, string board, string id, string body, long version,
		string[]? tags = null,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksWrite);
		return await comments.EditAsync(projectKey, board, id, body, tags, version, ct);
	}

	[McpServerTool(Name = "comments.delete", Title = "Delete a node comment", Destructive = true, UseStructuredContent = true, OutputSchemaType = typeof(CommentDeleteResult))]
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
}
