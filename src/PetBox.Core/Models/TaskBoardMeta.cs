using LinqToDB.Mapping;

namespace PetBox.Core.Models;

// Metadata row for a single named task board. PK is (ProjectKey, Name). The
// actual plan nodes live in `data/tasks/{ProjectKey}/{Name}.db` (temporal table);
// this table tracks which boards exist. Mirrors LogMeta — explicit creation, no
// auto-vivify.
[Table("TaskBoards")]
public sealed record TaskBoardMeta
{
	[Column, PrimaryKey, NotNull]
	public string ProjectKey { get; init; } = string.Empty;

	[Column, PrimaryKey, NotNull]
	public string Name { get; init; } = string.Empty;

	[Column, Nullable]
	public string? Description { get; init; }

	// Board role: simple|classic|spec|ideas|intake|work (default simple). Drives the workflow
	// (types/statuses/transitions) + invariants/effects via MethodologyPresets. Legacy rows
	// may still carry "free" (M029 migrates them; ParseKind also maps "free" → Simple).
	[Column, NotNull]
	public string Kind { get; init; } = "simple";

	[Column, NotNull]
	public DateTime CreatedAt { get; init; }

	[Column, NotNull]
	public DateTime UpdatedAt { get; init; }

	// Closed/archived: null = open. A closed board rejects writes (agents stop writing
	// to it by inertia) but stays readable; history is kept.
	[Column, Nullable]
	public DateTime? ClosedAt { get; init; }

	// For a work board: the name of the spec board its tasks link into (task_spec).
	// Makes the work->spec relationship explicit so an agent doesn't guess among several
	// spec boards; specRef targets are validated against this board. Null = unset.
	[Column, Nullable]
	public string? SpecBoard { get; init; }

	// The world this board is a member of (methodology-board-membership /
	// methodology-utility-kinds): a real methodology instance's name, OR the reserved
	// `UtilityWorld` sentinel — EXACTLY one, never both, never neither once a project has
	// left the pre-backfill bootstrap window. Process-role singleton and instance close
	// apply within whichever membership is set.
	//
	// Null is NOT a third world — it is the transient legacy-unassigned state
	// MethodologyInstanceBackfill sweeps into a real membership at startup (methodology-
	// instance-core), left over from before the instance model existed, and it deliberately
	// keeps its OLD resolution (TasksService.RuntimeAsync's active-instance/presets heuristic,
	// never methodology_defs — see LegacyUnassignedBoard_IgnoresProjectSingletonAxes)
	// unchanged: it is a bootstrap artifact, not a place to hang new behavior. `UtilityWorld`
	// is the deliberate, permanent, EXPLICIT home for a board that is NOT part of any
	// methodology's process (spec methodology-utility-kinds: "Доска ДОЛЖНА быть членом ровно
	// одного мира — инстанс методологии ЛИБО проектный utility-набор") — reached only by a
	// caller naming the sentinel, never inherited by the null bootstrap state. Test for it
	// with `TaskBoardMeta.IsUtilityMembership`; a real instance name is anything else non-null.
	[Column, Nullable]
	public string? MethodologyInstance { get; init; }

	// Reserved `MethodologyInstance` value marking a board as a member of the project's
	// utility world (spec methodology-utility-kinds) — project-homed kinds (builtin `wiki`/
	// `simple`/`classic` + any project-declared custom kind) that exist independently of the
	// active methodology and survive its switch, because they are structurally outside any
	// instance rather than merely un-swept. Never a legal methodology instance NAME:
	// MethodologyInstanceService.NormalizeName's slug regex (`^[a-z][a-z0-9_-]{0,99}$`)
	// rejects a leading `$`, so no real instance can ever collide with it — same posture as
	// the reserved `$system` project key.
	public const string UtilityWorld = "$utility";

	// True ONLY for the deliberate utility-world sentinel — NOT for null (the separate,
	// unrelated legacy-unassigned bootstrap state; see the field comment above). A board's
	// kind/runtime resolution branches THREE ways (TasksService.RuntimeForBoardAsync): this
	// sentinel → the project's utility layer; a real instance name → that instance's rules;
	// null → the old active-instance/presets heuristic, untouched.
	public static bool IsUtilityMembership(string? methodologyInstance) =>
		string.Equals(methodologyInstance, UtilityWorld, StringComparison.OrdinalIgnoreCase);
}
