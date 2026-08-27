using LinqToDB.Mapping;

namespace PetBox.Core.Models;

// Metadata row for a single named task board. PK is (ProjectKey, Name). The
// actual task nodes live in `data/tasks/{ProjectKey}/{Name}.db` (temporal table);
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

	// The board's DECLARED role in DELIVERY (spec: task-usage-layer-with-declared-role):
	// `corpus` (the default — the board's nodes are the answer, so a node that never gets
	// opened is waste) or `index` (the board is an ENTRY POINT — its nodes are supposed to be
	// surfaced far more often than they are opened, so a dead tail there is coverage, not
	// waste). Usage's cost/fit axes are read against this expectation; without it an index is
	// mis-read as the worst surface in the system on exactly the numbers that prove it works.
	//
	// DECLARED, never inferred from `Name` or `Kind`. A board's name is the user's, and a
	// hardcoded name list silently mis-measures every board it does not recognize — see
	// BoardDeclaredRole for the precedent (memory's `session-digests` store).
	//
	// Values are validated through BoardDeclaredRole.Normalize; unknown/blank reads back as
	// `corpus` (M051 backfills every pre-existing row to the same).
	[Column, NotNull]
	public string DeclaredRole { get; init; } = BoardDeclaredRole.Corpus;

	[Column, NotNull]
	public DateTime CreatedAt { get; init; }

	[Column, NotNull]
	public DateTime UpdatedAt { get; init; }

	// Closed/archived: null = open. A closed board rejects writes (agents stop writing
	// to it by inertia) but stays readable; history is kept.
	[Column, Nullable]
	public DateTime? ClosedAt { get; init; }

	// For a work board: the name of the wired board its tasks link into (task_spec).
	// Makes the work->spec relationship explicit so an agent doesn't guess among several
	// spec boards; specRef targets are validated against this board. Null = unset.
	//
	// PHYSICAL COLUMN stays "SpecBoard" (created by M027): the C# property was renamed
	// WiredBoard for the wired/set_wire contract rename, but the live column carries real
	// wiring on client-issues/work boards — an ALTER RENAME COLUMN risks that data on the
	// SQLite engine, so the property is MAPPED onto the existing column instead. The column
	// name never leaks to any surface (MCP/JSON/UI all resolve through this property + the
	// wire DTOs), so it is invisible to the contract.
	[Column("SpecBoard"), Nullable]
	public string? WiredBoard { get; init; }

	// The world this board is a member of (methodology-board-membership /
	// methodology-utility-kinds): a real methodology instance's key, OR the reserved
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
	// with `TaskBoardMeta.IsUtilityMembership`; a real instance key is anything else non-null.
	[Column, Nullable]
	public string? MethodologyInstance { get; init; }

	// Reserved `MethodologyInstance` value marking a board as a member of the project's
	// utility world (spec methodology-utility-kinds) — project-homed kinds (builtin `wiki`/
	// `simple`/`classic` + any project-declared custom kind) that exist independently of the
	// active methodology and survive its switch, because they are structurally outside any
	// instance rather than merely un-swept. Never a legal methodology instance KEY:
	// MethodologyInstanceService.NormalizeKey's slug regex (`^[a-z][a-z0-9_-]{0,99}$`)
	// rejects a leading `$`, so no real instance can ever collide with it — same posture as
	// the reserved `$system` project key.
	public const string UtilityWorld = "$utility";

	// True ONLY for the deliberate utility-world sentinel — NOT for null (the separate,
	// unrelated legacy-unassigned bootstrap state; see the field comment above). A board's
	// kind/runtime resolution branches THREE ways (TasksService.RuntimeForBoardAsync): this
	// sentinel → the project's utility layer; a real instance key → that instance's rules;
	// null → the old active-instance/presets heuristic, untouched.
	public static bool IsUtilityMembership(string? methodologyInstance) =>
		string.Equals(methodologyInstance, UtilityWorld, StringComparison.OrdinalIgnoreCase);
}

// The two legal values of TaskBoardMeta.DeclaredRole — the ONE place these wire strings are
// spelled out, so the migration default, the MCP argument validator, the usage reader and the
// UI cannot drift into three different vocabularies (memory learned this the hard way with
// UsageSourceKind).
//
// WHY A DECLARATION AND NOT A NAME LIST: this was decided against hardcoding roles by board
// name. Boards are created by the user; in another project the same process roles carry
// different names, and a hardcoded list mis-measures everything unfamiliar WITHOUT SAYING SO.
// The precedent is memory's `session-digests` store: an entry point into session search, where
// `opened: 0%` is the normal and correct reading, which by corpus expectations read as the
// worst-performing store in the system. It is also deliberately NOT a property of the node
// TYPE: the role is a property of the delivery SURFACE (what this board is FOR when it is
// searched), not of the unit of work sitting on it.
public static class BoardDeclaredRole
{
	// The board's nodes ARE the answer — surfaced and then opened. A never-opened node is waste.
	public const string Corpus = "corpus";
	// The board is an ENTRY POINT — surfaced to route the reader onward. Surfaced >> opened is
	// the DESIGNED outcome here, not a failure, and a dead tail is coverage rather than waste.
	public const string Index = "index";

	// null/blank/unknown -> Corpus. The read side never throws and never returns null: a board
	// whose column predates M051 (or carries a value some future writer did not know about)
	// must still be measurable, and `corpus` is the conservative expectation. The WRITE side is
	// strict instead (see TryNormalize) — a typo at declaration time is refused out loud rather
	// than silently filed as `corpus`, which would reproduce the exact mis-measurement this
	// field exists to prevent.
	public static string Normalize(string? role) =>
		TryNormalize(role, out var normalized) ? normalized : Corpus;

	// Strict parse for a CALLER-SUPPLIED value: true + the canonical lowercase form, or false
	// (blank included — an omitted argument is the caller's business to default, not ours).
	public static bool TryNormalize(string? role, out string normalized)
	{
		var trimmed = role?.Trim();
		if (string.Equals(trimmed, Corpus, StringComparison.OrdinalIgnoreCase)) { normalized = Corpus; return true; }
		if (string.Equals(trimmed, Index, StringComparison.OrdinalIgnoreCase)) { normalized = Index; return true; }
		normalized = Corpus;
		return false;
	}
}
