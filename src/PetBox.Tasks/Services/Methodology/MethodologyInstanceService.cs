using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using LinqToDB;
using LinqToDB.Async;
using PetBox.Core.Data.Temporal;
using PetBox.Core.Models;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Data;
using PetBox.Tasks.Validation;
using PetBox.Tasks.Workflow;

namespace PetBox.Tasks.Services.Methodology;

// Named methodology INSTANCES — the live process automaton (rules + boards + open/closed).
// Owns methodology_instances temporal rows and the create/list/get/close/adopt/rules-edit
// surface. Board membership is stored on TaskBoards.MethodologyInstance (Core catalog).
//
// HARD INVARIANT: create is the ONLY path that provisions boards from a source template;
// template writes never create boards (MethodologyTemplateService). Source is always
// explicit (builtin|template|instance) — no silent quartet default. Rules edit reuses
// MethodologyLiveMigration (same declarative status/type repair as def_upsert) scoped to
// THIS instance's member boards only — never mutates templates or other instances.
//
// Private collaborator of TasksService (not DI-registered) — same posture as
// MethodologyDefinitionService / MethodologyTemplateService.
public sealed partial class MethodologyInstanceService
{
	public const string SourceBuiltin = "builtin";
	public const string SourceTemplate = "template";
	public const string SourceInstance = "instance";

	// Process-role kinds: ≤1 open board per kind INSIDE an instance (not project-wide).
	static readonly BoardKind[] ProcessRoleKinds = [BoardKind.Spec, BoardKind.Ideas, BoardKind.Intake, BoardKind.Work];

	readonly ITaskBoardStore _boards;
	readonly MethodologyTemplateService _templates;
	readonly MethodologyLiveMigration _live;
	// Optional count callback — TasksService supplies status histograms without a circular
	// dependency on full GetAsync; null → empty counts on list/get.
	readonly Func<string, string, CancellationToken, Task<IReadOnlyDictionary<string, int>>>? _countNodes;

	static readonly MethodologyDefinitionValidator DefinitionValidator = new();

	static readonly JsonSerializerOptions DefinitionJson = new(JsonSerializerDefaults.Web)
	{
		Converters = { new JsonStringEnumConverter() },
	};

	[GeneratedRegex(@"^[a-z][a-z0-9_-]{0,99}$")]
	private static partial Regex SlugRegex();

	public MethodologyInstanceService(
		ITaskBoardStore boards,
		MethodologyTemplateService templates,
		Func<string, string, CancellationToken, Task<IReadOnlyDictionary<string, int>>>? countNodes = null)
	{
		_boards = boards;
		_templates = templates;
		_live = new MethodologyLiveMigration(boards);
		_countNodes = countNodes;
	}

	// ---- reads ----

	public async Task<IReadOnlyList<MethodologyInstanceView>> ListAsync(string projectKey, CancellationToken ct = default)
	{
		using var ctx = _boards.NewEnsuredConnection(projectKey);
		var rows = await ctx.GetTable<MethodologyInstanceRow>()
			.Where(r => r.ActiveTo == null)
			.OrderBy(r => r.Key)
			.ToListAsync(ct);
		var boards = await _boards.ListAsync(projectKey, ct);
		var result = new List<MethodologyInstanceView>(rows.Count);
		foreach (var row in rows)
			result.Add(await ProjectViewAsync(projectKey, row, boards, ct));
		return result;
	}

	public async Task<MethodologyInstanceView?> GetAsync(string projectKey, string name, CancellationToken ct = default)
	{
		var key = NormalizeName(name);
		using var ctx = _boards.NewEnsuredConnection(projectKey);
		var row = await ctx.GetTable<MethodologyInstanceRow>()
			.FirstOrDefaultAsync(r => r.Key == key && r.ActiveTo == null, ct);
		if (row is null) return null;
		var boards = await _boards.ListAsync(projectKey, ct);
		return await ProjectViewAsync(projectKey, row, boards, ct);
	}

	// Active instance rules document, or null when missing/closed (caller decides).
	public async Task<MethodologyDefinition?> GetDefinitionAsync(string projectKey, string name, bool allowClosed = true, CancellationToken ct = default)
	{
		var key = NormalizeName(name);
		using var ctx = _boards.NewEnsuredConnection(projectKey);
		var row = await ctx.GetTable<MethodologyInstanceRow>()
			.FirstOrDefaultAsync(r => r.Key == key && r.ActiveTo == null, ct);
		if (row is null) return null;
		if (!allowClosed && row.ClosedAt is not null) return null;
		return Deserialize(projectKey, row);
	}

	public async Task<bool> ExistsAsync(string projectKey, string name, CancellationToken ct = default)
	{
		var key = NormalizeName(name);
		using var ctx = _boards.NewEnsuredConnection(projectKey);
		return await ctx.GetTable<MethodologyInstanceRow>()
			.AnyAsync(r => r.Key == key && r.ActiveTo == null, ct);
	}

	public async Task<bool> AnyAsync(string projectKey, CancellationToken ct = default)
	{
		using var ctx = _boards.NewEnsuredConnection(projectKey);
		return await ctx.GetTable<MethodologyInstanceRow>()
			.AnyAsync(r => r.ActiveTo == null, ct);
	}

	// ---- create (one act: rules + boards) ----

	// source ∈ builtin|template|instance; sourceKey names the builtin slug / template key /
	// source instance. Explicit source only — no silent quartet default.
	public async Task<MethodologyInstanceAck> CreateAsync(
		string projectKey, string name, string source, string sourceKey, CancellationToken ct = default)
	{
		var key = NormalizeName(name);
		var src = (source ?? "").Trim().ToLowerInvariant();
		var srcKey = (sourceKey ?? "").Trim();
		if (srcKey.Length == 0)
			throw new ArgumentException("sourceKey is required (builtin slug, template key, or source instance name)", nameof(sourceKey));

		if (await ExistsAsync(projectKey, key, ct))
			throw new InvalidOperationException($"methodology instance '{key}' already exists in project '{projectKey}'");

		var def = await ResolveCreateSourceAsync(projectKey, src, srcKey, ct);
		var result = DefinitionValidator.Validate(def);
		if (!result.IsValid)
			throw new ArgumentException(result.Errors[0].ErrorMessage);

		// Store instance first (open), then provision boards for each kind.
		using (var ctx = _boards.NewEnsuredConnection(projectKey))
		{
			var row = new MethodologyInstanceRow
			{
				Key = key,
				Version = 0,
				Json = JsonSerializer.Serialize(def, DefinitionJson),
				ClosedAt = null,
			};
			var r = await TemporalStore.UpsertAsync(ctx, new[] { row }, ct: ct);
			if (!r.Applied)
				throw new InvalidOperationException($"methodology instance '{key}' could not be created (conflict)");
		}

		var boards = await ProvisionBoardsAsync(projectKey, key, def, ct);
		var version = await CurrentVersionAsync(projectKey, key, ct);
		return new MethodologyInstanceAck(key, Changed: true, Closed: false, boards, version);
	}

	// ---- rules edit (live instance definition + declarative migration) ----

	// Full rules document of one instance (baseline for rules_upsert). Null when missing.
	public async Task<MethodologyInstanceRulesView?> GetRulesAsync(string projectKey, string name, CancellationToken ct = default)
	{
		var key = NormalizeName(name);
		using var ctx = _boards.NewEnsuredConnection(projectKey);
		var row = await ctx.GetTable<MethodologyInstanceRow>()
			.FirstOrDefaultAsync(r => r.Key == key && r.ActiveTo == null, ct);
		if (row is null) return null;
		return new MethodologyInstanceRulesView(
			key, Deserialize(projectKey, row), row.Version, row.Created, row.Updated, row.ClosedAt is not null);
	}

	// Replace the instance's rules document with optimistic concurrency + live-node
	// migration (spec methodology-instance-rules-edit). Scoped to THIS instance's open
	// member boards only — never mutates templates, other instances, or the project
	// singleton def. Closed instances reject the write. Unmapped stranded values reject
	// the whole call (nothing written). `migration` shape identical to def_upsert.
	public async Task<MethodologyInstanceRulesAck> DefineRulesAsync(
		string projectKey, string name, MethodologyDefinition def, long version,
		IReadOnlyList<MethodologyMigration>? migration = null, CancellationToken ct = default)
	{
		var key = NormalizeName(name);
		var result = DefinitionValidator.Validate(def);
		if (!result.IsValid)
			throw new ArgumentException(result.Errors[0].ErrorMessage);

		using var ctx = _boards.NewEnsuredConnection(projectKey);
		var current = await ctx.GetTable<MethodologyInstanceRow>()
			.FirstOrDefaultAsync(r => r.Key == key && r.ActiveTo == null, ct)
			?? throw new InvalidOperationException($"methodology instance '{key}' not found in project '{projectKey}'");
		if (current.ClosedAt is not null)
			throw new InvalidOperationException(
				$"methodology instance '{key}' is closed — rules cannot be edited on a closed instance; create a new instance from this one's rules if you need a revised process");

		var json = JsonSerializer.Serialize(def, DefinitionJson);
		var sameDefinition = string.Equals(current.Json, json, StringComparison.Ordinal);
		var newRuntime = new MethodologyRuntime(def);
		var rewrites = new List<(string Board, List<PlanNode> Nodes)>();
		if (!sameDefinition)
		{
			// Scope: open boards that belong to THIS instance only. Other instances /
			// legacy unassigned boards keep their own resolution.
			var boards = (await _boards.ListAsync(projectKey, ct))
				.Where(b => b.ClosedAt is null
					&& string.Equals(b.MethodologyInstance, key, StringComparison.OrdinalIgnoreCase))
				.ToList();
			var oldDef = Deserialize(projectKey, current);
			MethodologyLiveMigration.Validate(migration ?? [], newRuntime, boards);
			rewrites = MethodologyLiveMigration.Plan(ctx, oldDef, def, newRuntime, migration ?? [], boards,
				subject: $"methodology instance '{key}' rules change");
		}

		// Preserve ClosedAt (must stay null here — closed rejected above) so SamePayload
		// only flips on the rules document.
		var next = new MethodologyInstanceRow
		{
			Key = key,
			Version = version,
			Json = json,
			ClosedAt = current.ClosedAt,
		};
		var r = await TemporalStore.UpsertAsync(ctx, new[] { next }, ct: ct);
		if (!r.Applied)
		{
			var c = r.Conflicts[0];
			throw new InvalidOperationException(c.Kind switch
			{
				TemporalConflictKind.FutureBaseline =>
					$"methodology instance '{key}' rules conflict: your baseline version {version} is ahead of this instance's cursor {c.ActiveVersion} — re-read with tasks_methodology_instance_rules_get and resubmit against the current version",
				TemporalConflictKind.Vanished =>
					$"methodology instance '{key}' rules conflict: your baseline version {version} no longer exists (the instance was removed); re-read with tasks_methodology_instance_rules_get",
				_ =>
					$"methodology instance '{key}' rules conflict: your baseline version {version} is stale — the current version is {c.ActiveVersion}; pass the version from your last tasks_methodology_instance_rules_get",
			});
		}

		var migrated = 0;
		foreach (var (board, nodes) in rewrites)
			migrated += await _live.RewriteAsync(ctx, projectKey, board, nodes, newRuntime, ct);
		return new MethodologyInstanceRulesAck(key, r.CurrentVersion, Changed: r.Inserted > 0, Migrated: migrated);
	}

	// ---- close (whole instance + member boards) ----

	public async Task<MethodologyInstanceAck> CloseAsync(string projectKey, string name, CancellationToken ct = default)
	{
		var key = NormalizeName(name);
		using var ctx = _boards.NewEnsuredConnection(projectKey);
		var current = await ctx.GetTable<MethodologyInstanceRow>()
			.FirstOrDefaultAsync(r => r.Key == key && r.ActiveTo == null, ct)
			?? throw new InvalidOperationException($"methodology instance '{key}' not found in project '{projectKey}'");

		if (current.ClosedAt is not null)
		{
			// Idempotent: already closed — still report member boards.
			var already = (await _boards.ListAsync(projectKey, ct))
				.Where(b => string.Equals(b.MethodologyInstance, key, StringComparison.OrdinalIgnoreCase))
				.Select(ToBoardRow)
				.ToList();
			return new MethodologyInstanceAck(key, Changed: false, Closed: true, already, current.Version);
		}

		var closedAt = DateTime.UtcNow;
		var next = new MethodologyInstanceRow
		{
			Key = key,
			Version = current.Version,
			Json = current.Json,
			ClosedAt = closedAt,
		};
		var r = await TemporalStore.UpsertAsync(ctx, new[] { next }, ct: ct);
		if (!r.Applied)
		{
			var c = r.Conflicts[0];
			throw new InvalidOperationException(
				$"methodology instance '{key}' conflict: current version is {c.ActiveVersion}; re-read and retry close");
		}

		// Close every member board — history stays readable; writes rejected by existing guard.
		var members = (await _boards.ListAsync(projectKey, ct))
			.Where(b => string.Equals(b.MethodologyInstance, key, StringComparison.OrdinalIgnoreCase))
			.ToList();
		foreach (var b in members.Where(b => b.ClosedAt is null))
			await _boards.UpdateAsync(projectKey, b.Name, m => m with { ClosedAt = closedAt }, ct);

		var after = (await _boards.ListAsync(projectKey, ct))
			.Where(b => string.Equals(b.MethodologyInstance, key, StringComparison.OrdinalIgnoreCase))
			.Select(ToBoardRow)
			.ToList();
		return new MethodologyInstanceAck(key, Changed: true, Closed: true, after, r.CurrentVersion);
	}

	// ---- membership: adopt / move board into an instance ----

	public async Task<TaskBoardMeta> AdoptBoardAsync(
		string projectKey, string board, string instanceName, CancellationToken ct = default)
	{
		var key = NormalizeName(instanceName);
		var meta = await _boards.FindAsync(projectKey, board, ct)
			?? throw new InvalidOperationException($"task board '{board}' not found in project '{projectKey}'");

		using (var ctx = _boards.NewEnsuredConnection(projectKey))
		{
			var inst = await ctx.GetTable<MethodologyInstanceRow>()
				.FirstOrDefaultAsync(r => r.Key == key && r.ActiveTo == null, ct)
				?? throw new InvalidOperationException($"methodology instance '{key}' not found in project '{projectKey}'");
			if (inst.ClosedAt is not null)
				throw new InvalidOperationException($"methodology instance '{key}' is closed — reopen is not supported; create a new instance or adopt into an open one");
		}

		if (string.Equals(meta.MethodologyInstance, key, StringComparison.OrdinalIgnoreCase))
			return meta; // already a member

		// Process-role singleton INSIDE the target instance.
		await AssertProcessRoleSingletonAsync(projectKey, meta.Kind, key, excludeBoard: meta.Name, ct);

		var ok = await _boards.UpdateAsync(projectKey, board, m => m with { MethodologyInstance = key }, ct);
		if (!ok)
			throw new InvalidOperationException($"task board '{board}' not found in project '{projectKey}'");
		return (await _boards.FindAsync(projectKey, board, ct))!;
	}

	// Process-role singleton: ≤1 open board of a process-role kind per instance (or per
	// legacy null-membership bucket). classic|simple are unlimited.
	public async Task AssertProcessRoleSingletonAsync(
		string projectKey, string kindSlug, string? instanceName, string? excludeBoard = null, CancellationToken ct = default)
	{
		if (!Enum.TryParse<BoardKind>(kindSlug, ignoreCase: true, out var kind) || !ProcessRoleKinds.Contains(kind))
			return;

		var existing = (await _boards.ListAsync(projectKey, ct))
			.FirstOrDefault(b =>
				b.ClosedAt is null
				&& MethodologyPresets.ParseKind(b.Kind) == kind
				&& MembershipEquals(b.MethodologyInstance, instanceName)
				&& (excludeBoard is null || !string.Equals(b.Name, excludeBoard, StringComparison.OrdinalIgnoreCase)));
		if (existing is not null)
		{
			var scope = instanceName is null
				? "project (legacy unassigned boards)"
				: $"methodology instance '{instanceName}'";
			throw new ArgumentException(
				$"{scope} already has an active {kind.ToString().ToLowerInvariant()} board ('{existing.Name}') — process-role kinds are one-per-instance; close it (tasks_board_close), adopt it elsewhere, or use a simple board");
		}
	}

	// ---- internals ----

	async Task<MethodologyDefinition> ResolveCreateSourceAsync(
		string projectKey, string source, string sourceKey, CancellationToken ct)
	{
		switch (source)
		{
			case SourceBuiltin:
			{
				var slug = sourceKey.Trim().ToLowerInvariant();
				// Accept "preset:quartet" sugar too.
				if (slug.StartsWith("preset:", StringComparison.Ordinal))
					slug = slug["preset:".Length..].Trim();
				return MethodologyPresets.RenderBuiltinTemplate(slug);
			}
			case SourceTemplate:
			{
				var view = await _templates.GetAsync(projectKey, sourceKey, ct)
					?? throw new ArgumentException(
						$"methodology template '{sourceKey}' not found — use a stored key, a builtin (quartet|classic|simple), or source=builtin");
				return view.Definition;
			}
			case SourceInstance:
			{
				var def = await GetDefinitionAsync(projectKey, sourceKey, allowClosed: true, ct)
					?? throw new ArgumentException($"methodology instance '{sourceKey}' not found — cannot snapshot");
				return def;
			}
			default:
				throw new ArgumentException(
					$"unknown methodology create source '{source}' — use builtin|template|instance (explicit; no silent default)");
		}
	}

	async Task<IReadOnlyList<MethodologyInstanceBoard>> ProvisionBoardsAsync(
		string projectKey, string instanceName, MethodologyDefinition def, CancellationToken ct)
	{
		var existing = await _boards.ListAsync(projectKey, ct);
		var taken = existing.Select(b => b.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
		var created = new List<MethodologyInstanceBoard>(def.Kinds.Count);
		var singleKind = def.Kinds.Count == 1;

		foreach (var kindDef in def.Kinds)
		{
			var kindSlug = kindDef.Kind.Trim().ToLowerInvariant();
			// Process-role singleton inside this (new) instance — always free for a brand-new
			// instance, but re-check in case of concurrent create.
			await AssertProcessRoleSingletonAsync(projectKey, kindSlug, instanceName, ct: ct);

			var boardName = PickBoardName(instanceName, kindSlug, singleKind, taken);
			taken.Add(boardName);
			var meta = await _boards.CreateAsync(
				projectKey, boardName,
				description: $"methodology {instanceName}/{kindSlug}",
				kind: kindSlug,
				specBoard: null,
				methodologyInstance: instanceName,
				ct: ct);
			created.Add(ToBoardRow(meta));
		}

		// Auto-wire SpecBoard within this instance only (work→spec etc. from kind data).
		await AutoWireWithinInstanceAsync(projectKey, instanceName, def, ct);

		// Re-read for SpecBoard after auto-wire.
		var after = await _boards.ListAsync(projectKey, ct);
		return after
			.Where(b => string.Equals(b.MethodologyInstance, instanceName, StringComparison.OrdinalIgnoreCase))
			.OrderBy(b => b.Name, StringComparer.Ordinal)
			.Select(ToBoardRow)
			.ToList();
	}

	async Task AutoWireWithinInstanceAsync(
		string projectKey, string instanceName, MethodologyDefinition def, CancellationToken ct)
	{
		var runtime = new MethodologyRuntime(def);
		var active = (await _boards.ListAsync(projectKey, ct))
			.Where(b => b.ClosedAt is null
				&& string.Equals(b.MethodologyInstance, instanceName, StringComparison.OrdinalIgnoreCase))
			.ToList();
		foreach (var kind in runtime.EffectiveKinds())
		{
			if (kind.AutoWireSpecFrom is not { Length: > 0 } fromKind) continue;
			var self = active.Where(b => string.Equals(b.Kind, kind.Kind, StringComparison.OrdinalIgnoreCase)).ToList();
			var target = active.Where(b => string.Equals(b.Kind, fromKind, StringComparison.OrdinalIgnoreCase)).ToList();
			if (self.Count == 1 && target.Count == 1 && string.IsNullOrWhiteSpace(self[0].SpecBoard))
				await _boards.UpdateAsync(projectKey, self[0].Name, m => m with { SpecBoard = target[0].Name }, ct);
		}
	}

	static string PickBoardName(string instanceName, string kindSlug, bool singleKind, HashSet<string> taken)
	{
		// Prefer short, readable names; fall back to instance-prefixed when taken.
		IEnumerable<string> candidates = singleKind
			? [instanceName, kindSlug, $"{instanceName}-{kindSlug}"]
			: [kindSlug, $"{instanceName}-{kindSlug}"];
		foreach (var c in candidates)
		{
			if (c.Length > 0 && !taken.Contains(c) && c != "node")
				return c;
		}
		// Last resort: unique suffix (should be unreachable for normal projects).
		var n = 2;
		while (taken.Contains($"{instanceName}-{kindSlug}-{n}")) n++;
		return $"{instanceName}-{kindSlug}-{n}";
	}

	async Task<MethodologyInstanceView> ProjectViewAsync(
		string projectKey, MethodologyInstanceRow row, IReadOnlyList<TaskBoardMeta> allBoards, CancellationToken ct)
	{
		var def = Deserialize(projectKey, row);
		var members = allBoards
			.Where(b => string.Equals(b.MethodologyInstance, row.Key, StringComparison.OrdinalIgnoreCase))
			.OrderBy(b => b.Name, StringComparer.Ordinal)
			.Select(ToBoardRow)
			.ToList();

		var counts = new Dictionary<string, int>(StringComparer.Ordinal);
		if (_countNodes is not null)
		{
			foreach (var b in members.Where(m => !m.Closed))
			{
				var boardCounts = await _countNodes(projectKey, b.Name, ct);
				foreach (var (status, n) in boardCounts)
					counts[status] = counts.GetValueOrDefault(status) + n;
			}
		}

		return new MethodologyInstanceView(
			Name: row.Key,
			Closed: row.ClosedAt is not null,
			Version: row.Version,
			Created: row.Created,
			Updated: row.Updated,
			ClosedAt: row.ClosedAt,
			DefinitionName: def.Name,
			Kinds: def.Kinds.Select(k => k.Kind).ToList(),
			Boards: members,
			Counts: counts);
	}

	async Task<long> CurrentVersionAsync(string projectKey, string key, CancellationToken ct)
	{
		using var ctx = _boards.NewEnsuredConnection(projectKey);
		var row = await ctx.GetTable<MethodologyInstanceRow>()
			.FirstOrDefaultAsync(r => r.Key == key && r.ActiveTo == null, ct);
		return row?.Version ?? 0;
	}

	static MethodologyInstanceBoard ToBoardRow(TaskBoardMeta m) =>
		new(m.Name, m.Kind, m.ClosedAt is not null, m.SpecBoard);

	static MethodologyDefinition Deserialize(string projectKey, MethodologyInstanceRow row) =>
		JsonSerializer.Deserialize<MethodologyDefinition>(row.Json, DefinitionJson)
		?? throw new InvalidOperationException($"project '{projectKey}': stored methodology instance '{row.Key}' failed to deserialize");

	static string NormalizeName(string? name)
	{
		var k = name?.Trim().ToLowerInvariant();
		if (string.IsNullOrEmpty(k))
			throw new ArgumentException("a methodology instance name (slug) is required", nameof(name));
		if (!SlugRegex().IsMatch(k))
			throw new ArgumentException(
				$"'{name}' is not a valid methodology instance name; must match ^[a-z][a-z0-9_-]{{0,99}}$", nameof(name));
		return k;
	}

	static bool MembershipEquals(string? a, string? b) =>
		string.Equals(a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase);
}
