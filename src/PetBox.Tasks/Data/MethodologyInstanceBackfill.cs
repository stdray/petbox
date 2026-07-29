using System.Text.Json;
using System.Text.Json.Serialization;
using LinqToDB;
using Microsoft.Extensions.Logging;
using PetBox.Core.Data;
using PetBox.Core.Data.Temporal;
using PetBox.Core.Models;
using PetBox.Tasks.Workflow;

namespace PetBox.Tasks.Data;

// One-time, idempotent back-fill (methodology-instance-backfill): every existing TaskBoard
// gets exactly-one MethodologyInstance membership after methodology-instance-core.
//
// Spans Core catalog (TaskBoards.MethodologyInstance) + per-project tasks files
// (methodology_instances rows). FluentMigrator cannot own this alone — membership and
// instance documents live in different DBs — so this runs at startup like
// FlatNodePartOfMigrator / LegacyTaskFileMigrator.
//
// Strategy (per project):
//   1. Skip boards that already have membership (idempotent re-run = no-op).
//   2. Rules source: active methodology_defs singleton when present; else a builtin
//      template chosen from unassigned board kinds (any process-role → quartet; else any
//      classic → classic; else simple).
//   3. Primary instance key: "main" when a project def exists; else the builtin slug
//      (quartet|classic|simple). Prefer adopting into an existing OPEN instance of that
//      key (or any open instance with free process-role slots) before creating.
//   4. Process-role boards (intake|ideas|spec|work): pack open boards with ≤1 open board
//      per kind per instance. First coherent group → primary instance (so a $system-like
//      quartet shares ONE instance). Extra open duplicates of a kind open a new instance
//      (`{key}-2`, …) with the SAME rules. Closed process-role boards join the primary.
//   5. Loose boards (classic|simple|custom): share ONE instance with the primary group when
//      it exists; when the project has only loose boards, one shared instance for all of
//      them (classic|simple unlimited within an instance — not one instance per board).
//   6. Instance create writes methodology_instances only — NEVER provisions new boards
//      (create-path of MethodologyInstanceService is out of scope for backfill).
//
// Safe: per-project try/catch; never deletes boards or nodes; re-run leaves assigned boards alone.
public sealed class MethodologyInstanceBackfill
{
	// Process-role kinds — read from MethodologyRuntime data (Singleton = process-role
	// cardinality, spec methodology-kind-singleton) instead of a fourth hardcoded literal of
	// the class MethodologyDefinition.cs documents as removed (`Methodological` in
	// TasksService, `ProcessRoleKinds` in MethodologyInstanceService). Presets-only because
	// backfill runs BEFORE any board has membership — there is no instance/project runtime to
	// scope this to yet.
	static readonly BoardKind[] ProcessRoles = MethodologyRuntime.PresetsOnly.EffectiveKinds()
		.Where(k => k.Singleton == true)
		.Select(k => MethodologyPresets.ParseKind(k.Kind))
		.ToArray();

	static readonly JsonSerializerOptions DefinitionJson = new(JsonSerializerDefaults.Web)
	{
		Converters = { new JsonStringEnumConverter() },
	};

	readonly ICoreDbFactory _dbf;
	readonly IScopedDbFactory<TasksDb> _factory;
	readonly ILogger? _log;

	public MethodologyInstanceBackfill(ICoreDbFactory dbf, IScopedDbFactory<TasksDb> factory, ILogger? log = null)
	{
		_dbf = dbf;
		_factory = factory;
		_log = log;
	}

	// Returns the number of projects that had at least one board membership written.
	public int Migrate()
	{
		using var db = _dbf.Open();
		var projects = db.TaskBoards
			.Select(b => b.ProjectKey)
			.Distinct()
			.OrderBy(k => k)
			.ToList();
		var touched = 0;
		foreach (var project in projects)
		{
			try
			{
				if (MigrateProject(db, project)) touched++;
			}
			catch (Exception ex)
			{
				_log?.LogError(ex,
					"Tasks methodology-instance back-fill failed for project {Project}; left as-is",
					project);
			}
		}
		return touched;
	}

	// Exposed for tests: run a single project and report whether any membership was written.
	internal bool MigrateProject(PetBoxDb db, string projectKey)
	{
		var boards = db.TaskBoards
			.Where(b => b.ProjectKey == projectKey)
			.OrderBy(b => b.Name)
			.ToList();
		if (boards.Count == 0) return false;

		var unassigned = boards
			.Where(b => string.IsNullOrWhiteSpace(b.MethodologyInstance))
			.ToList();
		if (unassigned.Count == 0) return false;

		using var ctx = _factory.NewEnsuredConnection(projectKey);

		var existingInstances = ctx.GetTable<MethodologyInstanceRow>()
			.Where(r => r.ActiveTo == null)
			.ToList();
		// Pre-existing keys (already on disk) — distinct from keys we allocate this run.
		var preexistingKeys = existingInstances
			.Select(r => r.Key)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		var openInstanceKeys = existingInstances
			.Where(r => r.ClosedAt is null)
			.Select(r => r.Key)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		// All keys claimed this run (pre-existing + newly allocated) — for unique keying.
		var claimedKeys = new HashSet<string>(preexistingKeys, StringComparer.OrdinalIgnoreCase);

		// Open process-role kinds already claimed by existing membership (incl. boards we
		// will not reassign). Closed member boards do not claim a process-role slot.
		var occupied = new Dictionary<string, HashSet<BoardKind>>(StringComparer.OrdinalIgnoreCase);
		foreach (var b in boards.Where(b => !string.IsNullOrWhiteSpace(b.MethodologyInstance)))
		{
			var inst = b.MethodologyInstance!;
			if (b.ClosedAt is not null) continue;
			var kind = MethodologyPresets.ParseKind(b.Kind);
			if (!ProcessRoles.Contains(kind)) continue;
			if (!occupied.TryGetValue(inst, out var set))
				occupied[inst] = set = [];
			set.Add(kind);
		}

		var defJson = ctx.GetTable<MethodologyDefRow>()
			.FirstOrDefault(r => r.Key == MethodologyDefRow.SingletonKey && r.ActiveTo == null)
			?.Json;
		var (preferredKey, rulesJson) = ResolvePrimaryRules(defJson, unassigned);

		// plan: board name → instance key
		var plan = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		// instances we must ensure exist → rules JSON
		var ensure = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		// ---- process-role packing ----
		var roleBoards = unassigned
			.Where(b => ProcessRoles.Contains(MethodologyPresets.ParseKind(b.Kind)))
			.OrderBy(b => b.ClosedAt is null ? 0 : 1) // open first
			.ThenBy(b => PipelineRank(MethodologyPresets.ParseKind(b.Kind)))
			.ThenBy(b => b.Name, StringComparer.Ordinal)
			.ToList();

		// Primary bucket key — may reuse an existing open instance.
		string? primaryKey = null;

		foreach (var board in roleBoards)
		{
			var kind = MethodologyPresets.ParseKind(board.Kind);
			string target;
			if (board.ClosedAt is not null)
			{
				// Closed: no slot claim — park on primary (create later if needed).
				primaryKey ??= PickOrAllocatePrimary(preferredKey, openInstanceKeys, claimedKeys, ensure, rulesJson, occupied);
				target = primaryKey;
			}
			else
			{
				// Open: find an open instance with a free slot for this kind, else allocate.
				target = FindOpenSlot(kind, preferredKey, openInstanceKeys, claimedKeys, occupied, ensure, rulesJson, ref primaryKey);
			}
			plan[board.Name] = target;
		}

		// ---- loose boards (classic|simple|custom): one shared group with primary ----
		var loose = unassigned
			.Where(b => !plan.ContainsKey(b.Name))
			.OrderBy(b => b.Name, StringComparer.Ordinal)
			.ToList();
		if (loose.Count > 0)
		{
			primaryKey ??= PickOrAllocatePrimary(preferredKey, openInstanceKeys, claimedKeys, ensure, rulesJson, occupied);
			foreach (var b in loose)
				plan[b.Name] = primaryKey;
		}

		// Ensure every planned instance row exists (create missing; never re-open closed).
		foreach (var (key, json) in ensure)
		{
			if (preexistingKeys.Contains(key)) continue;
			var row = new MethodologyInstanceRow
			{
				Key = key,
				Version = 0,
				Json = json,
				ClosedAt = null,
			};
			var r = TemporalStore.UpsertAsync(ctx, new[] { row }).GetAwaiter().GetResult();
			if (!r.Applied)
				throw new InvalidOperationException(
					$"methodology instance '{key}' could not be created during backfill for project '{projectKey}'");
			preexistingKeys.Add(key);
			openInstanceKeys.Add(key);
		}

		// Also cover the case where plan points at an already-existing instance (no ensure entry).
		foreach (var key in plan.Values.Distinct(StringComparer.OrdinalIgnoreCase))
		{
			if (preexistingKeys.Contains(key) || ensure.ContainsKey(key)) continue;
			// Should not happen — FindOpenSlot/PickOrAllocate always register ensure — but be safe.
			var row = new MethodologyInstanceRow
			{
				Key = key,
				Version = 0,
				Json = rulesJson,
				ClosedAt = null,
			};
			var r = TemporalStore.UpsertAsync(ctx, new[] { row }).GetAwaiter().GetResult();
			if (!r.Applied)
				throw new InvalidOperationException(
					$"methodology instance '{key}' could not be created during backfill for project '{projectKey}'");
			preexistingKeys.Add(key);
		}

		// Write memberships.
		var now = DateTime.UtcNow;
		var assigned = 0;
		foreach (var (boardName, instanceKey) in plan)
		{
			var n = db.TaskBoards
				.Where(b => b.ProjectKey == projectKey && b.Name == boardName)
				.Set(b => b.MethodologyInstance, instanceKey)
				.Set(b => b.UpdatedAt, now)
				.Update();
			assigned += n;
		}

		if (assigned > 0)
		{
			_log?.LogInformation(
				"Tasks: back-filled methodology instance membership for {Count} board(s) in project {Project} (instances: {Instances})",
				assigned, projectKey, string.Join(", ", plan.Values.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s)));
			return true;
		}
		return false;
	}

	// ---- packing helpers ----

	static string FindOpenSlot(
		BoardKind kind,
		string preferredKey,
		HashSet<string> openInstanceKeys,
		HashSet<string> claimedKeys,
		Dictionary<string, HashSet<BoardKind>> occupied,
		Dictionary<string, string> ensure,
		string rulesJson,
		ref string? primaryKey)
	{
		// Prefer primary (existing or planned) when it has a free slot.
		if (primaryKey is not null && SlotFree(primaryKey, kind, occupied))
		{
			Claim(primaryKey, kind, occupied);
			return primaryKey;
		}

		// Prefer existing open instance keyed preferredKey.
		if (openInstanceKeys.Contains(preferredKey) && SlotFree(preferredKey, kind, occupied))
		{
			primaryKey ??= preferredKey;
			Claim(preferredKey, kind, occupied);
			return preferredKey;
		}

		// Any existing open instance with a free slot (stable order).
		foreach (var key in openInstanceKeys.OrderBy(k => k, StringComparer.Ordinal))
		{
			if (!SlotFree(key, kind, occupied)) continue;
			primaryKey ??= key;
			Claim(key, kind, occupied);
			return key;
		}

		// Allocate a new instance (primary key, then -2, -3, …).
		var allocated = AllocateKey(preferredKey, claimedKeys);
		ensure[allocated] = rulesJson;
		claimedKeys.Add(allocated);
		openInstanceKeys.Add(allocated);
		primaryKey ??= allocated;
		Claim(allocated, kind, occupied);
		return allocated;
	}

	static string PickOrAllocatePrimary(
		string preferredKey,
		HashSet<string> openInstanceKeys,
		HashSet<string> claimedKeys,
		Dictionary<string, string> ensure,
		string rulesJson,
		Dictionary<string, HashSet<BoardKind>> occupied)
	{
		if (openInstanceKeys.Contains(preferredKey))
			return preferredKey;
		// Prefer any existing open instance before minting a new key.
		var existing = openInstanceKeys.OrderBy(k => k, StringComparer.Ordinal).FirstOrDefault();
		if (existing is not null) return existing;

		var allocated = AllocateKey(preferredKey, claimedKeys);
		ensure[allocated] = rulesJson;
		claimedKeys.Add(allocated);
		openInstanceKeys.Add(allocated);
		if (!occupied.ContainsKey(allocated)) occupied[allocated] = [];
		return allocated;
	}

	static bool SlotFree(string instanceKey, BoardKind kind, Dictionary<string, HashSet<BoardKind>> occupied) =>
		!occupied.TryGetValue(instanceKey, out var set) || !set.Contains(kind);

	static void Claim(string instanceKey, BoardKind kind, Dictionary<string, HashSet<BoardKind>> occupied)
	{
		if (!occupied.TryGetValue(instanceKey, out var set))
			occupied[instanceKey] = set = [];
		set.Add(kind);
	}

	static string AllocateKey(string preferred, HashSet<string> claimedKeys)
	{
		if (!claimedKeys.Contains(preferred)) return preferred;
		var n = 2;
		while (claimedKeys.Contains($"{preferred}-{n}")) n++;
		return $"{preferred}-{n}";
	}

	static (string Key, string RulesJson) ResolvePrimaryRules(string? defJson, IReadOnlyList<TaskBoardMeta> unassigned)
	{
		if (!string.IsNullOrWhiteSpace(defJson))
			return ("main", defJson);

		var kinds = unassigned.Select(b => MethodologyPresets.ParseKind(b.Kind)).ToHashSet();
		string slug;
		if (kinds.Any(ProcessRoles.Contains))
			slug = "quartet";
		// Classic has no distinguishing FIELD in MethodologyKindDef (same as Simple: no
		// Singleton, no BlocksGate) — its only identity is its own kind SLUG, so the builtin-
		// template choice reads the board's stored `Kind` slug directly instead of round-
		// tripping it through BoardKind.Classic. `unassigned`'s process-role kinds are already
		// excluded above; ParseKind(b.Kind) == Classic exactly when b.Kind case-insensitively
		// equals "classic" (the only string that enum-parses to it), so this is the identical
		// condition without the enum comparison.
		else if (unassigned.Any(b => string.Equals(b.Kind, "classic", StringComparison.OrdinalIgnoreCase)))
			slug = "classic";
		else
			slug = "simple";

		var def = MethodologyPresets.RenderBuiltinTemplate(slug);
		return (slug, JsonSerializer.Serialize(def, DefinitionJson));
	}

	// The fifth surviving duplicate of the quartet's pipeline order — replaced by an index
	// lookup into `ProcessRoles` itself: that array is already derived from
	// MethodologyRuntime.PresetsOnly.EffectiveKinds() in PIPELINE order (Intake, Ideas, Spec,
	// Work — EffectiveKinds walks MethodologyRuntime.PipelineOrder and Singleton==true keeps
	// exactly the quartet, in that order), so no second literal is needed to rank them. Only
	// ever called on a kind already filtered through `ProcessRoles.Contains` above, so -1
	// (not found) is unreachable.
	static int PipelineRank(BoardKind kind) => Array.IndexOf(ProcessRoles, kind);
}
