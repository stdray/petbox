using System.Text.Json;
using System.Text.Json.Serialization;
using LinqToDB;
using LinqToDB.Async;
using PetBox.Core.Data.Temporal;
using PetBox.Core.Models;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Data;
using PetBox.Tasks.Validation;
using PetBox.Tasks.Workflow;

namespace PetBox.Tasks.Services.Methodology;

// Definition storage + live-schema migration for project methodologies. Owns the
// singleton methodology_defs temporal document and delegates live-node repair to
// MethodologyLiveMigration (shared with instance rules edit). TasksService public
// methods stay as thin wrappers so ITasksService / MCP / DI stay unchanged (private
// collaborator — not DI-registered; same posture as NodeRefResolver / TaskUpsertAssociations).
public sealed class MethodologyDefinitionService
{
	readonly ITaskBoardStore _boards;
	readonly MethodologyLiveMigration _live;

	// Whole-document integrity rules (slugs, per-block references, uniqueness). Static — no state.
	static readonly MethodologyDefinitionValidator DefinitionValidator = new();

	// Storage form of the definition document: camelCase + enums as strings, so the stored
	// JSON reads like the wire (and survives enum reordering).
	static readonly JsonSerializerOptions DefinitionJson = new(JsonSerializerDefaults.Web)
	{
		Converters = { new JsonStringEnumConverter() },
	};

	public MethodologyDefinitionService(ITaskBoardStore boards)
	{
		_boards = boards;
		_live = new MethodologyLiveMigration(boards);
	}

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
			MethodologyLiveMigration.Validate(migration ?? [], newRuntime, boards);
			rewrites = MethodologyLiveMigration.Plan(ctx, current?.Definition, def, newRuntime, migration ?? [], boards,
				subject: "methodology definition change");
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
			migrated += await _live.RewriteAsync(ctx, projectKey, board, nodes, newRuntime, ct);
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
		MethodologyLiveMigration.Plan(ctx, current.Definition, newDef: null, MethodologyRuntime.PresetsOnly, [], boards,
			subject: "methodology definition delete (revert to builtin presets)", migrationHint: false);

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
}
