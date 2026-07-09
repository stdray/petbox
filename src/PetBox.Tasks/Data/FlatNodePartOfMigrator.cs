using LinqToDB;
using Microsoft.Extensions.Logging;
using PetBox.Core.Data;

namespace PetBox.Tasks.Data;

// One-time, idempotent back-fill for spec-flat-tags: convert legacy path-keyed nodes
// ("phase/wave/task") to FLAT slugs and synthesize the part_of edges that now carry the
// hierarchy. Runs per per-project tasks file, after the schema is at M006 and after the
// legacy per-board fold (LegacyTaskFileMigrator).
//
// Per board, for every ACTIVE node whose Key contains '/':
//   - new slug = the last path segment; on collision with another active key, a stable
//     "-<6 hex of NodeId>" suffix is appended.
//   - the Key (across ALL revisions) and any PrevKey references are rewritten to the slug.
//   - a part_of edge (child -> the node at the parent path, resolved from the ORIGINAL
//     paths) is created, so the old nesting is preserved as edges. Idempotent: rerun is a
//     no-op once no '/' keys remain; edge creation is idempotent at the store.
public sealed class FlatNodePartOfMigrator
{
	readonly string _tasksDir;
	readonly IScopedDbFactory<TasksDb> _factory;
	readonly IRelationStore _relations;
	readonly ILogger? _log;

	public FlatNodePartOfMigrator(string tasksDir, IScopedDbFactory<TasksDb> factory, IRelationStore relations, ILogger? log = null)
	{
		_tasksDir = tasksDir;
		_factory = factory;
		_relations = relations;
		_log = log;
	}

	// Returns the number of project files that had at least one node converted.
	public int Migrate()
	{
		if (!Directory.Exists(_tasksDir)) return 0;
		var converted = 0;
		foreach (var projectFile in Directory.GetFiles(_tasksDir, "*.db"))
		{
			var project = Path.GetFileNameWithoutExtension(projectFile);
			try
			{
				if (MigrateProject(project).GetAwaiter().GetResult()) converted++;
			}
			catch (Exception ex)
			{
				_log?.LogError(ex, "Tasks flat/part_of back-fill failed for project {Project}; left as-is", project);
			}
		}
		return converted;
	}

	async Task<bool> MigrateProject(string project)
	{
		using var db = _factory.GetDb(project); // ensures schema (M001..M006)
		var active = db.PlanNodes.Where(n => n.ActiveTo == null).ToList();
		var multi = active.Where(n => n.Key.Contains('/', StringComparison.Ordinal)).ToList();
		if (multi.Count == 0) return false;

		// Original path -> NodeId per board, captured BEFORE any rewrite, for parent resolution.
		var idByPath = active.Where(n => n.NodeId.Length > 0)
			.GroupBy(n => n.Board, StringComparer.Ordinal)
			.ToDictionary(g => g.Key, g => g.ToDictionary(n => n.Key, n => n.NodeId, StringComparer.Ordinal), StringComparer.Ordinal);

		var didWork = false;
		foreach (var board in multi.Select(n => n.Board).Distinct(StringComparer.Ordinal))
		{
			// Active flat keys already on the board are the collision floor.
			var used = new HashSet<string>(
				active.Where(n => n.Board == board && !n.Key.Contains('/', StringComparison.Ordinal)).Select(n => n.Key),
				StringComparer.Ordinal);

			foreach (var node in multi.Where(n => n.Board == board).OrderBy(n => n.Key, StringComparer.Ordinal))
			{
				var oldKey = node.Key;
				var slug = oldKey[(oldKey.LastIndexOf('/') + 1)..];
				if (slug.Length == 0 || !used.Add(slug))
				{
					slug = $"{slug}-{(node.NodeId.Length >= 6 ? node.NodeId[..6] : node.NodeId)}";
					used.Add(slug);
				}

				// Rewrite Key (all revisions) + any PrevKey references on this board.
				await db.PlanNodes.Where(n => n.Board == board && n.Key == oldKey)
					.Set(n => n.Key, _ => slug).UpdateAsync();
				await db.PlanNodes.Where(n => n.Board == board && n.PrevKey == oldKey)
					.Set(n => n.PrevKey, _ => slug).UpdateAsync();

				// Synthesize the part_of edge from the original parent path.
				var slashIdx = oldKey.LastIndexOf('/');
				var parentPath = oldKey[..slashIdx];
				if (node.NodeId.Length > 0 && idByPath.TryGetValue(board, out var map)
					&& map.TryGetValue(parentPath, out var parentId) && parentId != node.NodeId)
					await _relations.CreateAsync(project, "part_of", node.NodeId, parentId);

				didWork = true;
			}
		}
		if (didWork) _log?.LogInformation("Tasks: flattened {Count} path-keyed node(s) into slugs + part_of for project {Project}", multi.Count, project);
		return didWork;
	}
}
