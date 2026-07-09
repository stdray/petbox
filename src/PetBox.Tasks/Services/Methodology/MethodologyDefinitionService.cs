using System.Text.Json;
using System.Text.Json.Serialization;
using LinqToDB;
using LinqToDB.Async;
using LinqToDB.Data;
using PetBox.Core.Data.Temporal;
using PetBox.Core.Models;
using PetBox.Core.Search;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Data;
using PetBox.Tasks.Validation;
using PetBox.Tasks.Workflow;

namespace PetBox.Tasks.Services.Methodology;

// Definition storage + live-schema migration for project methodologies. Owns the
// singleton methodology_defs temporal document, the migration planner that repairs
// live nodes when a definition change would strand them, and the rewrite path that
// applies those repairs as temporal revisions. TasksService public methods stay as
// thin wrappers so ITasksService / MCP / DI stay unchanged (private collaborator —
// not DI-registered; same posture as NodeRefResolver / TaskUpsertAssociations).
public sealed class MethodologyDefinitionService
{
	readonly ITaskBoardStore _boards;

	// Whole-document integrity rules (slugs, per-block references, uniqueness). Static — no state.
	static readonly MethodologyDefinitionValidator DefinitionValidator = new();

	// Storage form of the definition document: camelCase + enums as strings, so the stored
	// JSON reads like the wire (and survives enum reordering).
	static readonly JsonSerializerOptions DefinitionJson = new(JsonSerializerDefaults.Web)
	{
		Converters = { new JsonStringEnumConverter() },
	};

	public MethodologyDefinitionService(ITaskBoardStore boards) => _boards = boards;

	public async Task<MethodologyDefView?> GetAsync(string projectKey, CancellationToken ct = default)
	{
		using var ctx = _boards.NewEnsuredConnection(projectKey);
		var row = await ctx.GetTable<MethodologyDefRow>()
			.FirstOrDefaultAsync(m => m.Key == MethodologyDefRow.SingletonKey && m.ActiveTo == null, ct);
		if (row is null) return null;
		var def = JsonSerializer.Deserialize<MethodologyDefinition>(row.Json, DefinitionJson)
			?? throw new InvalidOperationException($"project '{projectKey}': stored methodology definition failed to deserialize");
		return new MethodologyDefView(def, row.Version, row.Created, row.Updated);
	}

	public async Task<MethodologyDefAck> DefineAsync(
		string projectKey, MethodologyDefinition def, long version,
		IReadOnlyList<MethodologyMigration>? migration = null, CancellationToken ct = default)
	{
		var result = DefinitionValidator.Validate(def);
		if (!result.IsValid)
			throw new ArgumentException(result.Errors[0].ErrorMessage);

		using var ctx = _boards.NewEnsuredConnection(projectKey);
		var row = new MethodologyDefRow
		{
			Key = MethodologyDefRow.SingletonKey,
			Version = version,
			Json = JsonSerializer.Serialize(def, DefinitionJson),
		};

		// Live-data compatibility (spec primitives-schema-migration). An identical resubmit
		// can't change any node's resolution, so the no-op path skips all of it. A CHANGE is
		// checked against every active node whose board's kind the old or the new definition
		// declares (any other kind resolves from the immutable presets before AND after);
		// the declared `migration` repairs invalid values, anything left over rejects the
		// whole call before a single write.
		var newRuntime = new MethodologyRuntime(def);
		var current = await GetAsync(projectKey, ct);
		// An identical definition can't change any node's resolution, so we skip the migration
		// planning below. It does NOT skip the store: TemporalStore is the baseline arbiter — an
		// identical resubmit no-ops on any non-FUTURE baseline (stale included: the store already
		// holds what the author wants, so there is nothing to protect — the guard is about payload,
		// not version arithmetic), while a future baseline still conflicts (wrong-scope quote).
		var sameDefinition = current is not null && JsonSerializer.Serialize(current.Definition, DefinitionJson) == row.Json;
		var rewrites = new List<(string Board, List<PlanNode> Nodes)>();
		if (!sameDefinition)
		{
			var boards = (await _boards.ListAsync(projectKey, ct)).Where(b => b.ClosedAt == null).ToList();
			ValidateMigration(migration ?? [], newRuntime, boards);
			rewrites = PlanDefinitionMigration(ctx, current?.Definition, def, newRuntime, migration ?? [], boards);
		}

		var r = await TemporalStore.UpsertAsync(ctx, new[] { row }, ct: ct);
		if (!r.Applied)
		{
			// Singleton document: exactly one conflict possible. Name the current version so
			// the caller re-reads (tasks_methodology_def_get) and rebases — same optimistic-
			// concurrency spirit as the node upsert, but a throw (there is no batch to ack).
			// Thrown BEFORE any node rewrite, so a conflicting call writes nothing at all.
			var c = r.Conflicts[0];
			throw new InvalidOperationException(c.Kind switch
			{
				TemporalConflictKind.FutureBaseline => $"methodology definition conflict: your baseline version {version} is ahead of this project's cursor {c.ActiveVersion} — that version was never reached here (a baseline from another project/scope?); re-read with tasks_methodology_def_get and resubmit against the current version",
				TemporalConflictKind.Vanished => $"methodology definition conflict: your baseline version {version} no longer exists (the definition was removed); re-read with tasks_methodology_def_get and resubmit with version 0",
				_ => $"methodology definition conflict: your baseline version {version} is stale — the current version is {c.ActiveVersion}; pass the currentVersion from your last tasks_methodology_def_get (0 = no definition yet)",
			});
		}

		// The definition is committed; now rewrite the mapped nodes, one temporal batch per
		// board partition. A system write: the mapping IS the sanctioned transition, so no
		// FSM guards run here (same posture as the M029 in-place normalization, but through
		// the temporal store so history stays honest).
		var migrated = 0;
		foreach (var (board, nodes) in rewrites)
			migrated += await RewriteMigratedNodesAsync(ctx, projectKey, board, nodes, newRuntime, ct);
		return new MethodologyDefAck(r.CurrentVersion, Changed: r.Inserted > 0, Migrated: migrated);
	}

	public async Task<MethodologyDefAck> DeleteAsync(string projectKey, long version, CancellationToken ct = default)
	{
		var current = await GetAsync(projectKey, ct);
		if (current is null)
			return new MethodologyDefAck(Version: 0, Changed: false); // idempotent: nothing to delete

		// Live-node compatibility against the PRESETS-ONLY resolution the delete reverts to:
		// every active node on a board whose kind the current definition declares must fit
		// the preset it falls back to (a declared quartet kind → its preset; a custom kind →
		// `simple`). No `migration` on delete — an incompatible node REJECTS the call with a
		// clear message and nothing is written (repair the definition/nodes first, or change
		// the definition with a migration instead of deleting it).
		using var ctx = _boards.NewEnsuredConnection(projectKey);
		var boards = (await _boards.ListAsync(projectKey, ct)).Where(b => b.ClosedAt == null).ToList();
		PlanDefinitionMigration(ctx, current.Definition, newDef: null, MethodologyRuntime.PresetsOnly, [], boards,
			action: "delete (revert to builtin presets)", migrationHint: false);

		var r = await TemporalStore.UpsertAsync(ctx, Array.Empty<MethodologyDefRow>(),
			[(MethodologyDefRow.SingletonKey, version)], ct: ct);
		if (!r.Applied)
		{
			var c = r.Conflicts[0];
			throw new InvalidOperationException(c.Kind switch
			{
				TemporalConflictKind.FutureBaseline => $"methodology definition conflict: your baseline version {version} is ahead of this project's cursor {c.ActiveVersion} — re-read with tasks_methodology_def_get and retry the delete against the current version",
				_ => $"methodology definition conflict: your baseline version {version} is stale — the current version is {c.ActiveVersion}; re-read with tasks_methodology_def_get and retry the delete against the current version",
			});
		}
		return new MethodologyDefAck(r.CurrentVersion, Changed: r.Closed > 0);
	}

	// Integrity of the migration document itself, against the NEW resolution: every entry
	// names a kind that resolves somewhere (declared by the new definition, a builtin
	// preset, or the kind slug of an existing board — a DROPPED kind's boards keep their
	// slug and fall back to the presets), every `to` exists under that resolution, and no
	// `from` is mapped twice. Rejected with a clear message before anything is written.
	static void ValidateMigration(IReadOnlyList<MethodologyMigration> migration, MethodologyRuntime runtime, IReadOnlyList<TaskBoardMeta> boards)
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

	// The compatibility check + repair plan: walk every active node of every AFFECTED board
	// (kind declared by the old or the new definition — a kind neither declares resolves
	// from the immutable presets and can't break) against the NEW resolution, applying the
	// declared mappings ONLY where the current value is invalid (a valid value is never
	// rewritten). Returns the per-board rewrite batches; any node still incompatible after
	// the mappings throws, naming board/node/value — the caller then writes NOTHING.
	static List<(string Board, List<PlanNode> Nodes)> PlanDefinitionMigration(
		TasksDb ctx, MethodologyDefinition? oldDef, MethodologyDefinition? newDef,
		MethodologyRuntime newRuntime, IReadOnlyList<MethodologyMigration> migration,
		IReadOnlyList<TaskBoardMeta> boards,
		string action = "change", bool migrationHint = true)
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
				: " Move or close the offending nodes first, or change the definition (with a migration) instead of deleting it.";
			throw new ArgumentException(
				$"methodology definition {action} is incompatible with live nodes — rejected, nothing was written: "
				+ string.Join("; ", problems.Take(cap)) + more + fix);
		}
		return rewrites;
	}

	// Apply one board's repair batch as new temporal revisions (baseline = the version just
	// read, so a concurrent writer is still caught). Mirrors UpsertAsync's Class-A search
	// hygiene: inside the same transaction a node that stays in the open set is re-indexed
	// (its type/status may have crossed nothing FTS carries, but the row rewrite must not
	// stale the doc) and a node whose new status is terminal leaves the index; vectors are
	// the async worker's job, as everywhere.
	async Task<int> RewriteMigratedNodesAsync(
		TasksDb ctx, string projectKey, string board, List<PlanNode> nodes, MethodologyRuntime runtime, CancellationToken ct)
	{
		var fts = new SqliteFtsIndex(() => ctx);
		var r = await TemporalStore.UpsertAsync(ctx, nodes,
			onWithinTx: async (tx, upserted, _, c) =>
			{
				var tags = await NodeTagsAsync(tx, board, upserted.Where(n => TasksSearchDocs.IsIndexable(n, runtime)).Select(n => n.NodeId), c);
				foreach (var n in upserted)
					if (TasksSearchDocs.IsIndexable(n, runtime))
						await fts.IndexAsync(tx, TasksSearchDocs.ToDoc(n, projectKey, tags.GetValueOrDefault(n.NodeId, [])), c);
					else
						await fts.DeleteAsync(tx, projectKey, board, n.Key, c); // left the open set
			},
			partition: n => n.Board == board, ct: ct);
		if (!r.Applied)
		{
			// A writer slipped between the compatibility read and this rewrite. The
			// definition (and earlier boards' repairs) are already committed — say so
			// honestly instead of pretending atomicity the storage doesn't give us.
			var c = r.Conflicts[0];
			throw new InvalidOperationException(
				$"methodology migration: the definition was applied, but board '{board}' changed concurrently ({c.Kind} on '{c.Key}') and its nodes were NOT rewritten — re-read the board and repair the remaining nodes via tasks_upsert");
		}
		await _boards.TouchAsync(projectKey, board, ct);
		return r.Inserted;
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
