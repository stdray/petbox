using System.ComponentModel;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Server;
using PetBox.Core.Auth;
using PetBox.Core.Features;
using PetBox.Tasks.Contract;

namespace PetBox.Web.Mcp;

// MCP surface for node comments: a generic, editable, tree-structured discussion thread
// under any plan node (idea/task/spec/…). Comments are NOT plan nodes — they never appear
// in tasks.get / the workflow / delivery. Tree via parentId; tags are OPEN (e.g.
// `artifact:<slug>` marks a key deliberation artifact). Scopes: tasks:read / tasks:write.
// Feature: Tasks. Reaches the module only through ICommentService (the boundary door).
[McpServerToolType]
public static class CommentTools
{
	[McpServerTool(Name = "comments.add", Title = "Add a node comment")]
	[Description("Add a comment under a plan node (a discussion thread separate from the plan). nodeId is a stable PlanNode.NodeId. parentId (a comment id) makes it a REPLY — it must be an active comment under the same node, else rejected. tags are OPEN strings (convention `artifact:<slug>` flags a key artifact like a spec-update plan). author is caller-supplied. Returns {applied, currentVersion, id, conflicts}. Requires tasks:write.")]
	public static Task<object> AddAsync(
		IHttpContextAccessor http, FeatureFlags features, ICommentService comments,
		string projectKey, string board, string nodeId, string author, string body,
		string? parentId = null, string[]? tags = null,
		CancellationToken ct = default) => ModuleMcp.GuardAsync(async () =>
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksWrite);
		var r = await comments.AddAsync(projectKey, board, nodeId, parentId, author, body, tags, ct);
		return (object)new { applied = r.Applied, currentVersion = r.CurrentVersion, id = r.Id, conflicts = Conflicts(r) };
	});

	[McpServerTool(Name = "comments.list", Title = "List a node's comments", ReadOnly = true)]
	[Description("List the comment thread under a node: a FLAT list of active comments, each with parentId (build the tree from it), author, body, tags, version, timestamps. Chronological. Requires tasks:read.")]
	public static Task<object> ListAsync(
		IHttpContextAccessor http, FeatureFlags features, ICommentService comments,
		string projectKey, string board, string nodeId,
		CancellationToken ct = default) => ModuleMcp.GuardAsync(async () =>
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksRead);
		var list = await comments.ListForNodeAsync(projectKey, board, nodeId, ct);
		return (object)new { comments = list.Select(View).ToList() };
	});

	[McpServerTool(Name = "comments.edit", Title = "Edit a node comment")]
	[Description("Edit a comment's body (and, if `tags` is provided, replace its tag set). `version` is the revision you last saw — a stale baseline returns a conflict instead of clobbering. Body/tags only; you cannot re-parent a comment in v1. Returns {applied, currentVersion, id, conflicts}. Requires tasks:write.")]
	public static Task<object> EditAsync(
		IHttpContextAccessor http, FeatureFlags features, ICommentService comments,
		string projectKey, string board, string id, string body, long version,
		string[]? tags = null,
		CancellationToken ct = default) => ModuleMcp.GuardAsync(async () =>
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksWrite);
		var r = await comments.EditAsync(projectKey, board, id, body, tags, version, ct);
		return (object)new { applied = r.Applied, currentVersion = r.CurrentVersion, id = r.Id, conflicts = Conflicts(r) };
	});

	[McpServerTool(Name = "comments.delete", Title = "Delete a node comment", Destructive = true)]
	[Description("Soft-delete a comment. REJECTED if it still has active replies — delete the children first. Returns {deleted}. Requires tasks:write.")]
	public static Task<object> DeleteAsync(
		IHttpContextAccessor http, FeatureFlags features, ICommentService comments,
		string projectKey, string board, string id,
		CancellationToken ct = default) => ModuleMcp.GuardAsync(async () =>
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksWrite);
		return (object)new { deleted = await comments.DeleteAsync(projectKey, board, id, ct) };
	});

	static object View(CommentView c) =>
		new { id = c.Id, nodeId = c.NodeId, parentId = c.ParentId, author = c.Author, body = c.Body, tags = c.Tags, version = c.Version, created = c.Created, updated = c.Updated };

	static List<object> Conflicts(CommentUpsertResult r) =>
		r.Conflicts.Select(c => (object)new { id = c.Id, kind = c.Kind, baselineVersion = c.BaselineVersion, activeVersion = c.ActiveVersion }).ToList();
}
