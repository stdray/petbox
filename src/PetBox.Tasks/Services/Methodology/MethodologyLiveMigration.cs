using LinqToDB;
using LinqToDB.Async;
using LinqToDB.Data;
using PetBox.Core.Data.Temporal;
using PetBox.Core.Models;
using PetBox.Core.Search;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Data;
using PetBox.Tasks.Workflow;

namespace PetBox.Tasks.Services.Methodology;

// Shared live-node repair for methodology definition / instance-rules changes (spec
// primitives-schema-migration + methodology-instance-rules-edit). A mapping applies ONLY
// where a node's current value is INVALID under the new resolution — a valid value is never
// rewritten. Unmapped incompatibilities reject the whole call before anything is written.
// Used by MethodologyDefinitionService (project singleton) and MethodologyInstanceService
// (named instance rules); both pass the board set they own.
public sealed class MethodologyLiveMigration
{
	readonly ITaskBoardStore _boards;

	public MethodologyLiveMigration(ITaskBoardStore boards) => _boards = boards;

	// Integrity of the migration document itself, against the NEW resolution: every entry
	// names a kind that resolves somewhere (declared by the new definition, a builtin
	// preset, or the kind slug of an existing board — a DROPPED kind's boards keep their
	// slug and fall back to the presets), every `to` exists under that resolution, and no
	// `from` is mapped twice. Rejected with a clear message before anything is written.
	public static void Validate(
		IReadOnlyList<MethodologyMigration> migration, MethodologyRuntime runtime, IReadOnlyList<TaskBoardMeta> boards)
	{
		var seenKinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var m in migration)
		{
			if (string.IsNullOrWhiteSpace(m.Kind))
				throw new ArgumentException("migration: every entry needs a kind (the board-kind slug it repairs)");
			if (!seenKinds.Add(m.Kind))
				throw new ArgumentException($"migration: kind '{m.Kind}' appears more than once");
			var known = runtime.IsDefinedKind(m.Kind)
				|| Enum.TryParse<BoardKind>(m.Kind, ignoreCase: true, out _)
				|| boards.Any(b => string.Equals(b.Kind, m.Kind, StringComparison.OrdinalIgnoreCase));
			if (!known)
				throw new ArgumentException($"migration: kind '{m.Kind}' is not declared by the new definition, is not a builtin kind, and no board carries it");

			var workflows = runtime.Types(m.Kind);
			var seenTypeFrom = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (var t in m.Types)
			{
				RequireFromTo(m.Kind, "type", t);
				if (!seenTypeFrom.Add(t.From))
					throw new ArgumentException($"migration: kind '{m.Kind}': type '{t.From}' is mapped more than once");
				if (!workflows.Any(w => string.Equals(w.Type, t.To, StringComparison.OrdinalIgnoreCase)))
					throw new ArgumentException($"migration: kind '{m.Kind}': type mapping '{t.From}' -> '{t.To}': '{t.To}' is not a type of the new resolution (types: {runtime.ValidTypes(m.Kind)})");
			}
			var seenStatusFrom = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (var s in m.Statuses)
			{
				RequireFromTo(m.Kind, "status", s);
				if (!seenStatusFrom.Add(s.From))
					throw new ArgumentException($"migration: kind '{m.Kind}': status '{s.From}' is mapped more than once");
				// Document-level check: `to` exists on SOME workflow of the kind; whether it
				// fits a specific node's (possibly mapped) type is decided per node below.
				if (!workflows.Any(w => w.Has(s.To)))
					throw new ArgumentException($"migration: kind '{m.Kind}': status mapping '{s.From}' -> '{s.To}': '{s.To}' is not a status of the new resolution (statuses: {string.Join("|", workflows.SelectMany(w => w.Statuses.Select(x => x.Slug)).Distinct(StringComparer.OrdinalIgnoreCase))})");
			}
		}
	}

	static void RequireFromTo(string kind, string what, MethodologyValueMap map)
	{
		if (string.IsNullOrWhiteSpace(map.From) || string.IsNullOrWhiteSpace(map.To))
			throw new ArgumentException($"migration: kind '{kind}': every {what} mapping needs a non-empty from and to");
	}

	// Compatibility check + repair plan over the supplied boards (caller scopes to project
	// or one instance's members). Walks every active node of every AFFECTED board (kind
	// declared by the old or the new definition) against the NEW resolution, applying
	// declared mappings ONLY where the current value is invalid. Returns per-board rewrite
	// batches; any node still incompatible after the mappings throws — caller writes NOTHING.
	// `subject` is the human-readable what (e.g. "methodology definition change",
	// "methodology instance 'main' rules change") for the rejection message.
	public static List<(string Board, List<PlanNode> Nodes)> Plan(
		TasksDb ctx, MethodologyDefinition? oldDef, MethodologyDefinition? newDef,
		MethodologyRuntime newRuntime, IReadOnlyList<MethodologyMigration> migration,
		IReadOnlyList<TaskBoardMeta> boards,
		string subject = "methodology definition change", bool migrationHint = true)
	{
		static bool Declares(MethodologyDefinition? d, string? kind) =>
			kind is not null && d is not null && d.Kinds.Any(k => string.Equals(k.Kind, kind, StringComparison.OrdinalIgnoreCase));

		var rewrites = new List<(string Board, List<PlanNode> Nodes)>();
		var problems = new List<string>();
		foreach (var b in boards)
		{
			if (!Declares(oldDef, b.Kind) && !Declares(newDef, b.Kind)) continue;
			var map = migration.FirstOrDefault(m => string.Equals(m.Kind, b.Kind, StringComparison.OrdinalIgnoreCase));
			var active = ctx.PlanNodes.Where(n => n.Board == b.Name && n.ActiveTo == null).ToList();
			var boardRewrites = new List<PlanNode>();
			foreach (var n in active)
			{
				// 1. the type must resolve a workflow under the new resolution; a type
				//    mapping applies only when it doesn't.
				var type = n.Type;
				var wf = newRuntime.For(b.Kind, type);
				if (wf is null && map?.Types.FirstOrDefault(t => string.Equals(t.From, n.Type, StringComparison.OrdinalIgnoreCase)) is { } tm)
				{
					type = tm.To;
					wf = newRuntime.For(b.Kind, type);
				}
				if (wf is null)
				{
					problems.Add($"board '{b.Name}' node '{n.Key}': type '{n.Type}' does not resolve under the new '{b.Kind}' (types: {newRuntime.ValidTypes(b.Kind)})");
					continue;
				}
				// 2. the status must belong to the (possibly mapped) type's new workflow; a
				//    status mapping applies only when it doesn't, and its `to` must fit THAT
				//    workflow — otherwise the node stays incompatible.
				var status = n.Status;
				if (!wf.Has(status))
				{
					var sm = map?.Statuses.FirstOrDefault(s => string.Equals(s.From, n.Status, StringComparison.OrdinalIgnoreCase));
					if (sm is not null && wf.Has(sm.To))
						status = sm.To;
					else
					{
						problems.Add($"board '{b.Name}' node '{n.Key}': status '{n.Status}' is unknown to type '{type}' under the new '{b.Kind}' (statuses: {string.Join("|", wf.Statuses.Select(s => s.Slug))})");
						continue;
					}
				}
				if (!string.Equals(type, n.Type, StringComparison.Ordinal) || !string.Equals(status, n.Status, StringComparison.Ordinal))
					boardRewrites.Add(n with { Type = type, Status = status });
			}
			if (boardRewrites.Count > 0) rewrites.Add((b.Name, boardRewrites));
		}

		if (problems.Count > 0)
		{
			const int cap = 10;
			var more = problems.Count > cap ? $" …and {problems.Count - cap} more" : "";
			var fix = migrationHint
				? " Extend `migration` (per kind: types:[{from,to}] / statuses:[{from,to}]) to map every remaining value, or fix the nodes first."
				: " Move or close the offending nodes first, or change the rules (with a migration) instead of deleting them.";
			throw new ArgumentException(
				$"{subject} is incompatible with live nodes — rejected, nothing was written: "
				+ string.Join("; ", problems.Take(cap)) + more + fix);
		}
		return rewrites;
	}

	// Apply one board's repair batch as new temporal revisions (baseline = the version just
	// read, so a concurrent writer is still caught). Mirrors UpsertAsync's Class-A search
	// hygiene: inside the same transaction every node with an identity is re-indexed,
	// terminal or not (search-hides-terminal-nodes); vectors are the async worker's job,
	// as everywhere.
	public async Task<int> RewriteAsync(
		TasksDb ctx, string projectKey, string board, List<PlanNode> nodes, MethodologyRuntime runtime, CancellationToken ct)
	{
		var fts = new SqliteFtsIndex(() => ctx);
		// Mirror the reference layer alongside the FTS floor (search-index-authority): a methodology
		// migration can reclassify a status's StatusKind, so the facet row must be re-projected in the
		// same transaction or search_meta would drift from the (new) runtime. kindSlug: null → the new
		// runtime classifies project-wide, consistent with the backfill's own classification.
		var r = await TemporalStore.UpsertAsync(ctx, nodes,
			onWithinTx: async (tx, upserted, _, c) =>
			{
				var tags = await NodeTagsAsync(tx, board, upserted.Where(n => TasksSearchDocs.IsIndexable(n, runtime)).Select(n => n.NodeId), c);
				foreach (var n in upserted)
					if (TasksSearchDocs.IsIndexable(n, runtime))
					{
						await fts.IndexAsync(tx, TasksSearchDocs.ToDoc(n, projectKey, tags.GetValueOrDefault(n.NodeId, [])), c);
						await SqliteMetaIndex.IndexAsync(tx, TasksSearchDocs.ToMetaDoc(n, projectKey, runtime, kindSlug: null), c);
					}
					else
					{
						await fts.DeleteAsync(tx, projectKey, board, n.Key, c); // lost its identity (rare)
						await SqliteMetaIndex.DeleteAsync(tx, projectKey, board, n.Key, c);
					}
			},
			partition: n => n.Board == board, ct: ct);
		if (!r.Applied)
		{
			// A writer slipped between the compatibility read and this rewrite. The rules
			// document (and earlier boards' repairs) are already committed — say so
			// honestly instead of pretending atomicity the storage doesn't give us.
			var c = r.Conflicts[0];
			throw new InvalidOperationException(
				$"methodology migration: the rules were applied, but board '{board}' changed concurrently ({c.Kind} on '{c.Key}') and its nodes were NOT rewritten — re-read the board and repair the remaining nodes via tasks_upsert");
		}
		await _boards.TouchAsync(projectKey, board, ct);
		return r.Inserted;
	}

	// The boards whose StatusKind classification a vocab change can move: those whose kind the OLD
	// or the NEW resolution declares (a kind neither declares resolves from the immutable presets
	// before AND after, so its facets can't drift). The same scoping Plan walks for rewrites.
	public static IReadOnlyList<TaskBoardMeta> AffectedBoards(
		MethodologyDefinition? oldDef, MethodologyDefinition? newDef, IReadOnlyList<TaskBoardMeta> boards)
	{
		static bool Declares(MethodologyDefinition? d, string? kind) =>
			kind is not null && d is not null && d.Kinds.Any(k => string.Equals(k.Kind, kind, StringComparison.OrdinalIgnoreCase));
		return boards.Where(b => Declares(oldDef, b.Kind) || Declares(newDef, b.Kind)).ToList();
	}

	// Reproject a WHOLE board's search_meta facet layer under a (new) runtime
	// (spec tasks-reindex-on-methodology-vocab-change). A methodology vocab change can reclassify a
	// status's StatusKind WITHOUT changing the status STRING (e.g. a rule marks an existing status
	// terminal): such a node needs no type/status rewrite, so RewriteAsync — which only re-stamps the
	// nodes it rewrites — leaves its facet row STALE and the опорный слой quietly lies. This re-stamps
	// every active, indexable node of the board so search_meta always reflects the current vocabulary.
	// Per-board classification (kindSlug, the board's own kind), idempotent; boards are small, and a
	// reindex is a first-class operation here, not a fallback. Runs at the rules/adopt mutation points.
	public static async Task<int> ReindexBoardMetaAsync(
		TasksDb ctx, string projectKey, string board, string kindSlug, MethodologyRuntime runtime, CancellationToken ct)
	{
		var indexed = ctx.PlanNodes.Where(n => n.Board == board && n.ActiveTo == null).ToList()
			.Where(n => TasksSearchDocs.IsIndexable(n, runtime)).ToList();
		if (indexed.Count == 0) return 0;
		using var tx = await ctx.BeginTransactionAsync(ct);
		try
		{
			foreach (var n in indexed)
				await SqliteMetaIndex.IndexAsync(ctx, TasksSearchDocs.ToMetaDoc(n, projectKey, runtime, kindSlug), ct);
			await tx.CommitAsync(ct);
		}
		catch
		{
			await tx.RollbackAsync(ct);
			throw;
		}
		return indexed.Count;
	}

	// Active (ValidTo == null) tags for the given nodes on a board, read on the supplied connection.
	static async Task<Dictionary<string, List<string>>> NodeTagsAsync(DataConnection db, string board, IEnumerable<string> nodeIds, CancellationToken ct)
	{
		var ids = nodeIds.Distinct().ToList();
		if (ids.Count == 0) return [];
		var rows = await db.GetTable<NodeTag>()
			.Where(t => t.Board == board && t.ValidTo == null && ids.Contains(t.NodeId))
			.Select(t => new { t.NodeId, t.Tag }).ToListAsync(ct);
		return rows.GroupBy(r => r.NodeId).ToDictionary(g => g.Key, g => g.Select(x => x.Tag).ToList());
	}
}
