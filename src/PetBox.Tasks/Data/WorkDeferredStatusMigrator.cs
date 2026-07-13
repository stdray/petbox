using System.Text.Json;
using System.Text.Json.Serialization;
using LinqToDB;
using Microsoft.Extensions.Logging;
using PetBox.Core.Data;
using PetBox.Core.Data.Temporal;
using PetBox.Core.Models;
using PetBox.Tasks.Services;
using PetBox.Tasks.Services.Methodology;
using PetBox.Tasks.Workflow;

namespace PetBox.Tasks.Data;

// One-time, idempotent (work-preset-drop-deferred): the `work` kind's built-in preset
// (MethodologyPresets.WorkKind) no longer declares the `Deferred` status — the maintainer
// decided the kanban board shouldn't carry a column for it. Editing the preset alone is NOT
// enough: MethodologyInstanceService.CreateAsync / MethodologyTemplateService materialize a
// preset kind VERBATIM into the STORED MethodologyDefinition at creation time
// (RenderBuiltinTemplate → MethodologyInstanceRow.Json / MethodologyDefRow.Json), so any
// instance/definition created before this change still carries `Deferred` (and its
// transitions) baked into its own document — the preset code change never reaches it
// (same class of miss as board-view-defaults-not-applied-existing-instances, but for a
// PROCESS field: MethodologyRuntime reads a declared kind's statuses/transitions WHOLE-
// OBJECT from the stored document, by design — a per-field merge would be wrong here, the
// definition IS the source of truth for process shape).
//
// PetBox is a build-your-own-methodology product (spec methodology-from-primitives): a
// project's `work`-kind document may be a project's OWN process, not our copy — nothing in
// the stored row says which (MethodologyInstanceRow/MethodologyDefRow carry only Json, no
// source/sourceKey provenance). So this migrator does NOT strip `Deferred` from every kind
// literally named `work`; it only touches a workflow block whose STATUSES AND TRANSITIONS
// are byte-for-byte the OLD builtin preset's shape (LegacyWorkBlockSnapshot below) — i.e. an
// untouched verbatim materialization of OUR preset, never customized. Anything else that
// still carries `Deferred` (a project's own methodology, or a customized copy of ours) is
// left alone and logged as skipped — the alternative is silently overwriting a deliberate
// user decision because it happens to share our old default.
//
// Strategy: scan every project's stored methodology documents (the project-singleton
// methodology_defs row + every methodology_instances row) for a `work`-slug kind whose
// workflow block matches LegacyWorkBlockSnapshot EXACTLY. For a matching (= "ours,
// untouched") document, BEFORE rewriting the definition: find every active node on this
// document's `work` board(s) that is still in status `Deferred` (verified empty on `$system`
// today, but the code stays in the startup path forever, so a future project's stray node
// must not end up pointing at a status that no longer exists — no FSM edge, no kanban
// column, nothing to fix it forward with) and move it to `Cancelled` — the maintainer's call
// (spec work-preset-drop-deferred). The move is a SYSTEM write via MethodologyLiveMigration
// (same posture as a definition-driven remap elsewhere in this engine: "the mapping IS the
// sanctioned transition" — no FSM edge is required, because none exists from `Deferred` to
// `Cancelled` in either the old or the new document) plus an `artifact:reason` comment, THEN
// the definition is rewritten with `Deferred` gone — so there is never a moment where the
// definition already lacks `Deferred` while a node still carries it.
//
// Scoped to project databases only (per-project tasks files); board membership (Core DB) is
// read but not modified. Runs at startup like MethodologyInstanceBackfill / FlatNodePartOfMigrator —
// content lives in per-project files a FluentMigrator schema migration cannot reach.
public sealed class WorkDeferredStatusMigrator
{
	const string WorkKind = "work";
	const string DeferredStatus = "Deferred";
	const string CancelledStatus = "Cancelled";
	const string ReasonText = "work-preset-drop-deferred: the Deferred status was retired from the work methodology; this node was moved to Cancelled.";

	// Snapshot of MethodologyPresets.WorkKind's SOLE workflow block as it read BEFORE
	// work-preset-drop-deferred (7 statuses / 11 transitions, Deferred included). This is a
	// FROZEN COPY for comparison purposes ONLY — it must never track the live preset (which
	// no longer has Deferred) and is deliberately duplicated rather than referencing
	// MethodologyPresets, so a future preset edit can't accidentally change what this
	// migrator recognizes as "our old shape".
	static readonly MethodologyWorkflowDef LegacyWorkBlockSnapshot = new(["feature", "bug", "chore"],
		[
			new("Pending", "Pending", StatusKind.Open),
			new("InProgress", "In progress", StatusKind.Open),
			new("Review", "Review", StatusKind.Open),
			new("Done", "Done", StatusKind.TerminalOk),
			new("Blocked", "Blocked", StatusKind.Open),
			new("Deferred", "Deferred", StatusKind.Open),
			new("Cancelled", "Cancelled", StatusKind.TerminalCancel),
		],
		[
			new("Pending", "InProgress"),
			new("InProgress", "Review"),
			new("Review", "InProgress"),
			new("Review", "Done", RequiresApproval: true),
			new("InProgress", "Blocked"),
			new("Blocked", "InProgress"),
			new("Pending", "Deferred"),
			new("Deferred", "Pending"),
			new("Pending", "Cancelled"),
			new("InProgress", "Cancelled"),
			new("Review", "Cancelled"),
		]);

	static readonly JsonSerializerOptions DefinitionJson = new(JsonSerializerDefaults.Web)
	{
		Converters = { new JsonStringEnumConverter() },
	};

	readonly ICoreDbFactory _dbf;
	readonly IScopedDbFactory<TasksDb> _factory;
	readonly TaskBoardStore _boards;
	readonly MethodologyLiveMigration _live;
	readonly CommentService _comments;
	readonly ILogger? _log;

	public WorkDeferredStatusMigrator(ICoreDbFactory dbf, IScopedDbFactory<TasksDb> factory, ILogger? log = null)
	{
		_dbf = dbf;
		_factory = factory;
		_boards = new TaskBoardStore(dbf, factory);
		_live = new MethodologyLiveMigration(_boards);
		_comments = new CommentService(factory);
		_log = log;
	}

	// Returns the number of project documents (definition + instance rows, summed) rewritten.
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
				touched += MigrateProject(project);
			}
			catch (Exception ex)
			{
				_log?.LogError(ex,
					"Tasks work-preset-drop-deferred migration failed for project {Project}; left as-is",
					project);
			}
		}
		return touched;
	}

	// Exposed for tests: run a single project, return the number of documents rewritten.
	internal int MigrateProject(string projectKey)
	{
		using var ctx = _factory.NewEnsuredConnection(projectKey);
		var rewritten = 0;
		var allBoards = _boards.ListAsync(projectKey).GetAwaiter().GetResult();

		var defRow = ctx.GetTable<MethodologyDefRow>()
			.FirstOrDefault(r => r.Key == MethodologyDefRow.SingletonKey && r.ActiveTo == null);
		if (defRow is not null)
		{
			var subject = $"project {projectKey}'s methodology definition";
			// Dual-read posture (transitional): the project singleton applies to every board of
			// this project's `work` kind — membership in a named instance is a NEWER concept the
			// singleton predates, so scope liberally here (any Deferred straggler matters more
			// than being precise about which boards "really" belong to the singleton).
			var scope = allBoards.Where(b => string.Equals(b.Kind, WorkKind, StringComparison.OrdinalIgnoreCase)).ToList();
			if (TryStrip(defRow.Json, subject, scope, projectKey, ctx, out var newDefJson, out var moved))
			{
				var next = defRow with { Version = defRow.Version, Json = newDefJson };
				var r = TemporalStore.UpsertAsync(ctx, new[] { next }).GetAwaiter().GetResult();
				if (r.Applied)
				{
					rewritten++;
					_log?.LogInformation(
						"Tasks: dropped 'Deferred' from the work kind's project methodology definition in {Project} ({Moved} node(s) moved Deferred -> Cancelled first)",
						projectKey, moved);
				}
				else
				{
					_log?.LogWarning(
						"Tasks work-preset-drop-deferred: project {Project}'s methodology definition changed concurrently — left as-is, will retry next startup",
						projectKey);
				}
			}
		}

		var instanceRows = ctx.GetTable<MethodologyInstanceRow>()
			.Where(r => r.ActiveTo == null)
			.ToList();
		foreach (var row in instanceRows)
		{
			var subject = $"methodology instance '{row.Key}' in project {projectKey}";
			var scope = allBoards.Where(b =>
				string.Equals(b.Kind, WorkKind, StringComparison.OrdinalIgnoreCase)
				&& string.Equals(b.MethodologyInstance, row.Key, StringComparison.OrdinalIgnoreCase)).ToList();
			if (!TryStrip(row.Json, subject, scope, projectKey, ctx, out var newJson, out var moved)) continue;
			var next = row with { Version = row.Version, Json = newJson };
			var r = TemporalStore.UpsertAsync(ctx, new[] { next }).GetAwaiter().GetResult();
			if (r.Applied)
			{
				rewritten++;
				_log?.LogInformation(
					"Tasks: dropped 'Deferred' from the work kind's methodology instance '{Instance}' in {Project} ({Moved} node(s) moved Deferred -> Cancelled first)",
					row.Key, projectKey, moved);
			}
			else
			{
				_log?.LogWarning(
					"Tasks work-preset-drop-deferred: methodology instance '{Instance}' in {Project} changed concurrently — left as-is, will retry next startup",
					row.Key, projectKey);
			}
		}

		return rewritten;
	}

	// Deserializes `json`, and IF its `work`-slug kind has a workflow block that matches
	// LegacyWorkBlockSnapshot EXACTLY (an untouched copy of our old preset): first moves every
	// active `Deferred` node on `scope`'s boards to `Cancelled` (system write + reason
	// comment), THEN strips `Deferred` (status + referencing transitions) from that block and
	// re-serializes. Returns false (out set to the input, moved=0) when nothing qualifies — no
	// `work` kind, no block with `Deferred` at all (already migrated, or never had it), or a
	// block that DOES carry `Deferred` but isn't an untouched copy of our old preset (a
	// project's own methodology, or a customized copy of ours) — that last case is logged so
	// the skip is visible, not silent, and NEITHER the document NOR its nodes are touched.
	bool TryStrip(
		string json, string subject, IReadOnlyList<TaskBoardMeta> scope, string projectKey, TasksDb ctx,
		out string result, out int moved)
	{
		result = json;
		moved = 0;
		MethodologyDefinition? def;
		try
		{
			def = JsonSerializer.Deserialize<MethodologyDefinition>(json, DefinitionJson);
		}
		catch (JsonException)
		{
			return false; // not a shape we understand — leave it for a human, not a crash loop
		}
		if (def is null) return false;

		var changed = false;
		var kinds = def.Kinds.Select(kind =>
		{
			if (!string.Equals(kind.Kind, WorkKind, StringComparison.OrdinalIgnoreCase))
				return kind;
			var workflows = kind.Workflows.Select(block =>
			{
				var hasDeferred = block.Statuses.Any(s => string.Equals(s.Slug, DeferredStatus, StringComparison.OrdinalIgnoreCase));
				if (!hasDeferred)
					return block;
				if (!BlockEquals(block, LegacyWorkBlockSnapshot))
				{
					_log?.LogInformation(
						"Tasks work-preset-drop-deferred: {Subject} has a 'work' kind carrying 'Deferred' that does NOT match the old builtin preset exactly (customized statuses/transitions, or a project-owned methodology) — left as-is",
						subject);
					return block;
				}
				changed = true;
				var statuses = block.Statuses
					.Where(s => !string.Equals(s.Slug, DeferredStatus, StringComparison.OrdinalIgnoreCase))
					.ToList();
				var transitions = block.Transitions
					.Where(t => !string.Equals(t.From, DeferredStatus, StringComparison.OrdinalIgnoreCase)
						&& !string.Equals(t.To, DeferredStatus, StringComparison.OrdinalIgnoreCase))
					.ToList();
				return block with { Statuses = statuses, Transitions = transitions };
			}).ToList();
			return changed ? kind with { Workflows = workflows } : kind;
		}).ToList();

		if (!changed) return false;

		var newDef = def with { Kinds = kinds };
		moved = MoveDeferredNodesAsync(projectKey, ctx, scope, newDef, subject).GetAwaiter().GetResult();
		result = JsonSerializer.Serialize(newDef, DefinitionJson);
		return true;
	}

	// Moves every active `Deferred` node on `scope`'s boards to `Cancelled`, board by board, as
	// a SYSTEM write (MethodologyLiveMigration.RewriteAsync — no FSM edge is required, mirrors
	// the posture of a definition-driven remap elsewhere in this engine) plus an
	// `artifact:reason` comment per node. Runs BEFORE the caller writes the stripped
	// definition, so no window exists where the definition already lacks `Deferred` while a
	// node still names it. Returns the total node count moved (0 in the common/expected case).
	async Task<int> MoveDeferredNodesAsync(
		string projectKey, TasksDb ctx, IReadOnlyList<TaskBoardMeta> scope, MethodologyDefinition newDef, string subject)
	{
		var runtime = new MethodologyRuntime(newDef);
		var total = 0;
		foreach (var board in scope)
		{
			var stale = ctx.GetTable<PlanNode>()
				.Where(n => n.Board == board.Name && n.ActiveTo == null && n.Status == DeferredStatus)
				.ToList();
			if (stale.Count == 0) continue;

			var patched = stale.Select(n => n with { Status = CancelledStatus }).ToList();
			await _live.RewriteAsync(ctx, projectKey, board.Name, patched, runtime, CancellationToken.None);
			foreach (var n in stale)
				await _comments.AddAsync(projectKey, board.Name, n.NodeId, parentId: null, author: "system",
					body: ReasonText, tags: ["artifact:reason"], ct: CancellationToken.None);

			total += stale.Count;
			_log?.LogInformation(
				"Tasks work-preset-drop-deferred: {Subject}, board '{Board}': moved {Count} node(s) from Deferred to Cancelled ahead of dropping the status",
				subject, board.Name, stale.Count);
		}
		return total;
	}

	// Structural equality of two workflow blocks over exactly the fields that define
	// "process shape": the type vocabulary sharing the FSM, the status set (slug/name/kind),
	// and the transition set (from/to + every gate attribute — approval, reason,
	// precondition artifact, the approval-enforce mode, the checklist). Order-independent
	// (a semantically identical document re-serialized in a different element order is still
	// "ours"); every attribute must match exactly — a single flipped flag or added checklist
	// item marks the block as customized, which is exactly the point: we only touch a block
	// nobody has put a hand on since it was materialized from the old preset.
	static bool BlockEquals(MethodologyWorkflowDef a, MethodologyWorkflowDef b) =>
		a.Types.Count == b.Types.Count
		&& a.Types.OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
			.SequenceEqual(b.Types.OrderBy(t => t, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase)
		&& a.Statuses.Count == b.Statuses.Count
		&& a.Statuses.OrderBy(s => s.Slug, StringComparer.OrdinalIgnoreCase)
			.SequenceEqual(b.Statuses.OrderBy(s => s.Slug, StringComparer.OrdinalIgnoreCase), StatusComparer)
		&& a.Transitions.Count == b.Transitions.Count
		&& a.Transitions.OrderBy(t => (t.From, t.To), TransitionKeyComparer)
			.SequenceEqual(b.Transitions.OrderBy(t => (t.From, t.To), TransitionKeyComparer), TransitionComparer);

	static readonly IEqualityComparer<WorkflowStatus> StatusComparer = new StatusEq();
	static readonly IEqualityComparer<MethodologyTransitionDef> TransitionComparer = new TransitionEq();
	static readonly IComparer<(string From, string To)> TransitionKeyComparer = Comparer<(string From, string To)>.Create(
		(x, y) =>
		{
			var c = string.CompareOrdinal(x.From, y.From);
			return c != 0 ? c : string.CompareOrdinal(x.To, y.To);
		});

	sealed class StatusEq : IEqualityComparer<WorkflowStatus>
	{
		public bool Equals(WorkflowStatus? x, WorkflowStatus? y) =>
			x is not null && y is not null
			&& string.Equals(x.Slug, y.Slug, StringComparison.OrdinalIgnoreCase)
			&& string.Equals(x.Name, y.Name, StringComparison.Ordinal)
			&& x.Kind == y.Kind;

		public int GetHashCode(WorkflowStatus s) => HashCode.Combine(s.Slug.ToLowerInvariant(), s.Name, s.Kind);
	}

	sealed class TransitionEq : IEqualityComparer<MethodologyTransitionDef>
	{
		public bool Equals(MethodologyTransitionDef? x, MethodologyTransitionDef? y) =>
			x is not null && y is not null
			&& string.Equals(x.From, y.From, StringComparison.OrdinalIgnoreCase)
			&& string.Equals(x.To, y.To, StringComparison.OrdinalIgnoreCase)
			&& x.RequiresApproval == y.RequiresApproval
			&& x.RequiresReason == y.RequiresReason
			&& x.PreconditionArtifact == y.PreconditionArtifact
			&& x.EnforceApproval == y.EnforceApproval
			&& x.Checklist.SequenceEqual(y.Checklist, StringComparer.Ordinal);

		public int GetHashCode(MethodologyTransitionDef t) =>
			HashCode.Combine(t.From.ToLowerInvariant(), t.To.ToLowerInvariant(), t.RequiresApproval, t.RequiresReason, t.PreconditionArtifact);
	}
}
