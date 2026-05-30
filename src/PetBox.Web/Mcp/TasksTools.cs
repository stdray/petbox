using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Microsoft.AspNetCore.Http;
using PetBox.Core.Auth;
using PetBox.Core.Data.Temporal;
using PetBox.Core.Features;
using PetBox.Tasks.Data;

namespace PetBox.Web.Mcp;

// MCP surface for the Tasks module: named board lifecycle + temporal node content.
// Boards are created explicitly (no auto-vivify). Node ops go through the generic
// temporal engine (optimistic concurrency by baseline, rename via prevKey,
// delta-since-cursor). Scopes: tasks:read / tasks:write. Feature: Tasks.
[McpServerToolType]
public static class TasksTools
{
	[McpServerTool(Name = "tasks.board_create", Title = "Create a task board")]
	[Description("Create a named task board in a project. Requires tasks:write.")]
	public static async Task<object> BoardCreateAsync(
		IHttpContextAccessor http, FeatureFlags features, ITaskBoardStore boards,
		string projectKey, string board, string? description = null, CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksWrite);
		var meta = await boards.CreateAsync(projectKey, board, description, ct);
		return new { meta.ProjectKey, meta.Name, meta.Description, meta.CreatedAt };
	}

	[McpServerTool(Name = "tasks.board_list", Title = "List task boards", ReadOnly = true)]
	[Description("List task boards in a project. Requires tasks:read.")]
	public static async Task<object> BoardListAsync(
		IHttpContextAccessor http, FeatureFlags features, ITaskBoardStore boards,
		string projectKey, CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksRead);
		var list = await boards.ListAsync(projectKey, ct);
		return new { boards = list.Select(b => new { b.Name, b.Description, b.CreatedAt }).ToList() };
	}

	[McpServerTool(Name = "tasks.board_delete", Title = "Delete a task board", Destructive = true)]
	[Description("Delete a task board and its nodes. Requires tasks:write.")]
	public static async Task<object> BoardDeleteAsync(
		IHttpContextAccessor http, FeatureFlags features, ITaskBoardStore boards,
		string projectKey, string board, CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksWrite);
		return new { deleted = await boards.DeleteAsync(projectKey, board, ct) };
	}

	[McpServerTool(Name = "tasks.get", Title = "Get a board's nodes", ReadOnly = true)]
	[Description("Return the active plan nodes of a board ordered by priority then key, with rename lineage. Requires tasks:read.")]
	public static async Task<object> GetAsync(
		IHttpContextAccessor http, FeatureFlags features, ITaskBoardStore boards,
		string projectKey, string board, CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksRead);
		await EnsureBoard(boards, projectKey, board, ct);

		var ctx = boards.GetContext(projectKey, board);
		var all = ctx.PlanNodes.ToList();
		var lineage = BuildLineage(all);
		var active = all.Where(n => n.ActiveTo == null).OrderBy(n => n.Priority).ThenBy(n => n.Key).ToList();
		var current = all.Count == 0 ? 0 : all.Max(n => n.Version);
		return new
		{
			currentVersion = current,
			nodes = active.Select(n => new
			{
				key = n.Key,
				status = n.Status.ToString(),
				body = n.Body,
				commitRef = n.CommitRef,
				priority = n.Priority,
				version = n.Version,
				renamedFrom = lineage.TryGetValue(n.Key, out var p) ? p : [],
			}).ToList(),
		};
	}

	[McpServerTool(Name = "tasks.upsert", Title = "Upsert plan nodes")]
	[Description("""
		Declarative temporal upsert of plan nodes into a board. Requires tasks:write.
		`nodes` is a JSON array of { key, status, body, commitRef?, priority?, version?, prevKey? }.
		`version` is the baseline you last saw (0 = new node). `status` is one of
		Pending|InProgress|Done|Blocked|Deferred|Cancelled. Set `prevKey` to rename a node
		(retires the old key, creates the new linked one). `sinceVersion` selects the delta
		returned. Result: { applied, currentVersion, inserted, closed, conflicts[], added[], updated[], removed[] }.
		""")]
	public static async Task<object> UpsertAsync(
		IHttpContextAccessor http, FeatureFlags features, ITaskBoardStore boards,
		string projectKey, string board,
		[Description("JSON array of node objects")] JsonElement nodes,
		long sinceVersion = 0, CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksWrite);
		await EnsureBoard(boards, projectKey, board, ct);

		var desired = ParseNodes(nodes);
		var ctx = boards.GetContext(projectKey, board);
		var r = await TemporalStore.UpsertAsync(ctx, desired, sinceVersion, ct: ct);
		return Serialize(r);
	}

	[McpServerTool(Name = "tasks.delta", Title = "Plan delta since cursor", ReadOnly = true)]
	[Description("Return nodes added/updated/removed since `sinceVersion` (no writes). Requires tasks:read.")]
	public static async Task<object> DeltaAsync(
		IHttpContextAccessor http, FeatureFlags features, ITaskBoardStore boards,
		string projectKey, string board, long sinceVersion, CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Tasks);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.TasksRead);
		await EnsureBoard(boards, projectKey, board, ct);

		var ctx = boards.GetContext(projectKey, board);
		var r = await TemporalStore.UpsertAsync(ctx, Array.Empty<PlanNode>(), sinceVersion, ct: ct);
		return Serialize(r);
	}

	static async Task EnsureBoard(ITaskBoardStore boards, string projectKey, string board, CancellationToken ct)
	{
		if (!await boards.ExistsAsync(projectKey, board, ct))
			throw new InvalidOperationException($"task board '{board}' not found in project '{projectKey}'");
	}

	static object Serialize(TemporalUpsertResult<PlanNode> r) => new
	{
		applied = r.Applied,
		currentVersion = r.CurrentVersion,
		inserted = r.Inserted,
		closed = r.Closed,
		conflicts = r.Conflicts.Select(c => new
		{
			key = c.Key,
			kind = c.Kind.ToString(),
			baselineVersion = c.BaselineVersion,
			activeVersion = c.ActiveVersion,
		}).ToList(),
		added = r.Added.Select(NodeDto).ToList(),
		updated = r.Updated.Select(NodeDto).ToList(),
		removed = r.Removed.ToList(),
	};

	static object NodeDto(PlanNode n) => new
	{
		key = n.Key,
		status = n.Status.ToString(),
		body = n.Body,
		commitRef = n.CommitRef,
		priority = n.Priority,
		version = n.Version,
	};

	static PlanNode[] ParseNodes(JsonElement nodes)
	{
		if (nodes.ValueKind != JsonValueKind.Array)
			throw new ArgumentException("nodes must be a JSON array");
		var list = new List<PlanNode>();
		foreach (var e in nodes.EnumerateArray())
		{
			list.Add(new PlanNode
			{
				Key = ModuleMcp.ReqStr(e, "key"),
				Version = ModuleMcp.OptLong(e, "version", 0),
				Status = ParseStatus(ModuleMcp.OptStr(e, "status") ?? "Pending"),
				Body = ModuleMcp.OptStr(e, "body") ?? string.Empty,
				CommitRef = ModuleMcp.OptStr(e, "commitRef"),
				Priority = ModuleMcp.OptLong(e, "priority", 0),
				PrevKey = ModuleMcp.OptStr(e, "prevKey"),
			});
		}
		return list.ToArray();
	}

	static PlanStatus ParseStatus(string s) =>
		Enum.TryParse<PlanStatus>(s, ignoreCase: true, out var v)
			? v
			: throw new ArgumentException($"invalid status '{s}' (Pending|InProgress|Done|Blocked|Deferred|Cancelled)");

	// Active node key -> chain of prior keys it was renamed from (walk PrevKey edges
	// across the full revision history).
	static Dictionary<string, List<string>> BuildLineage(List<PlanNode> all)
	{
		var edge = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var g in all.GroupBy(n => n.Key, StringComparer.Ordinal))
		{
			var birth = g.OrderBy(n => n.Version).First();
			if (!string.IsNullOrEmpty(birth.PrevKey))
				edge[g.Key] = birth.PrevKey!;
		}

		var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
		foreach (var key in edge.Keys)
		{
			var chain = new List<string>();
			var cur = key;
			var guard = 0;
			while (edge.TryGetValue(cur, out var prev) && guard++ < 1000)
			{
				chain.Add(prev);
				cur = prev;
			}
			result[key] = chain;
		}
		return result;
	}
}
